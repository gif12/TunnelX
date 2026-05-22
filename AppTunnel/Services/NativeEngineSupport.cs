using System.IO;
using System.Reflection;

namespace AppTunnel.Services;

/// <summary>
/// Locates and prepares native binaries (WinDivert, wintun, sing-box, xray) for tunnel engines.
/// </summary>
internal static class NativeEngineSupport
{
    public static string SingBoxWorkDir => Path.Combine(App.AppDataDir, "singbox");
    public static string XrayWorkDir => Path.Combine(App.AppDataDir, "xray");

    public static string ResolveSingBoxExePath()
    {
        var sideBySide = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
        return File.Exists(sideBySide) ? sideBySide : Path.Combine(SingBoxWorkDir, "sing-box.exe");
    }

    public static string ResolveXrayExePath()
    {
        var sideBySide = Path.Combine(AppContext.BaseDirectory, "xray.exe");
        return File.Exists(sideBySide) ? sideBySide : Path.Combine(XrayWorkDir, "xray.exe");
    }

    /// <summary>
    /// Extracts WinDivert.dll, WinDivert64.sys and wintun.dll into AppDataDir (standalone builds).
    /// </summary>
    public static void EnsureAppNativeLibsExtracted()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in new[] { "WinDivert.dll", "WinDivert64.sys", "wintun.dll" })
            TryExtractEmbeddedResource(asm, name, Path.Combine(App.AppDataDir, name));
    }

    public static async Task<bool> EnsureEmbeddedExecutableAsync(
        string resourceName,
        string targetPath,
        CancellationToken ct)
    {
        if (!ShouldExtractToWorkDir(targetPath))
            return File.Exists(targetPath);

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            Logger.Warning($"[{resourceName}] Embedded resource not found.");
            return File.Exists(targetPath);
        }

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == stream.Length)
            return true;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            Logger.Info($"[{resourceName}] Extracting ({stream.Length / 1024 / 1024} MB) → {targetPath}");
            await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
            await stream.CopyToAsync(fs, 81920, ct);
            Logger.Info($"[{resourceName}] Extraction complete.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[{resourceName}] Extraction failed: {ex.Message}");
            return File.Exists(targetPath);
        }
    }

    public static void EnsureWintunBesideEngine(string engineDirectory)
    {
        if (string.IsNullOrWhiteSpace(engineDirectory))
            return;

        var source = ResolveWintunSourcePath();
        if (source == null)
        {
            Logger.Warning("[NATIVE] wintun.dll not found for sing-box/xray work directory");
            return;
        }

        Directory.CreateDirectory(engineDirectory);
        var dest = Path.Combine(engineDirectory, "wintun.dll");

        try
        {
            if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(source).Length)
                return;

            File.Copy(source, dest, overwrite: true);
            Logger.Info($"[NATIVE] wintun.dll → {dest}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[NATIVE] Could not copy wintun.dll to {engineDirectory}: {ex.Message}");
        }
    }

    public static void EnsureWintunBesideExecutable(string executablePath)
    {
        var dir = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrWhiteSpace(dir))
            EnsureWintunBesideEngine(dir);
    }

    public static string? ResolveWinDivertDllPath() => ResolveBundledNativePath("WinDivert.dll");

    public static string? ResolveWinDivert64SysPath() => ResolveBundledNativePath("WinDivert64.sys");

    private static string? ResolveBundledNativePath(string fileName)
    {
        var appData = Path.Combine(App.AppDataDir, fileName);
        if (File.Exists(appData))
            return appData;

        var sideBySide = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(sideBySide) ? sideBySide : null;
    }

    /// <summary>Returns true when both WinDivert.dll and WinDivert64.sys are present (after optional extract).</summary>
    public static bool EnsureWinDivertPairReady()
    {
        EnsureAppNativeLibsExtracted();
        var dll = ResolveWinDivertDllPath();
        var sys = ResolveWinDivert64SysPath();
        if (dll != null)
            LogFileProbe("WinDivert.dll", dll);
        if (sys != null)
            LogFileProbe("WinDivert64.sys", sys);
        return dll != null && sys != null;
    }

    public static string? ResolveWintunSourcePath()
    {
        var appData = Path.Combine(App.AppDataDir, "wintun.dll");
        if (File.Exists(appData))
            return appData;

        var sideBySide = Path.Combine(AppContext.BaseDirectory, "wintun.dll");
        return File.Exists(sideBySide) ? sideBySide : null;
    }

    public static void LogFileProbe(string tag, string path)
    {
        if (File.Exists(path))
        {
            var size = new FileInfo(path).Length;
            Logger.Info($"[ENGINE] {tag}: OK path={path} size={size}");
        }
        else
        {
            Logger.Warning($"[ENGINE] {tag}: MISSING path={path}");
        }
    }

    private static void TryExtractEmbeddedResource(Assembly asm, string resourceName, string destPath)
    {
        try
        {
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
                return;

            if (File.Exists(destPath) && new FileInfo(destPath).Length == stream.Length)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(fs);
            Logger.Info($"[NATIVE] Extracted {resourceName} → {destPath}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[NATIVE] Could not extract {resourceName}: {ex.Message}");
        }
    }

    private static bool ShouldExtractToWorkDir(string targetPath) =>
        targetPath.StartsWith(App.AppDataDir, StringComparison.OrdinalIgnoreCase);
}
