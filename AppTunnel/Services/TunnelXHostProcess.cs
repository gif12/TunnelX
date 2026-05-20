using System.Diagnostics;

namespace AppTunnel.Services;

/// <summary>
/// Detects other TunnelX host processes and stale single-instance state.
/// </summary>
internal static class TunnelXHostProcess
{
    public static int CountOtherSamePathHosts()
    {
        var current = Process.GetCurrentProcess();
        var currentPath = TryGetMainModulePath(current);
        var count = 0;

        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id)
                continue;

            try
            {
                if (process.HasExited)
                    continue;

                var otherPath = TryGetMainModulePath(process);
                if (currentPath != null &&
                    otherPath != null &&
                    !string.Equals(currentPath, otherPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                count++;
            }
            catch
            {
                count++;
            }
            finally
            {
                process.Dispose();
            }
        }

        return count;
    }

    public static bool IsAnotherTunnelXHostAlive()
    {
        var current = Process.GetCurrentProcess();
        var currentPath = TryGetMainModulePath(current);

        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id)
                continue;

            try
            {
                if (process.HasExited)
                    continue;

                var otherPath = TryGetMainModulePath(process);
                if (currentPath != null &&
                    otherPath != null &&
                    !string.Equals(currentPath, otherPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                return true;
            }
            catch
            {
                return true;
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    /// <summary>
    /// Kills other TunnelX hosts that are alive but not responding (likely hung after End Task).
    /// Returns true if any process was terminated.
    /// </summary>
    public static bool TryTerminateUnresponsiveHosts()
    {
        var current = Process.GetCurrentProcess();
        var currentPath = TryGetMainModulePath(current);
        var terminated = false;

        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id)
                continue;

            try
            {
                if (process.HasExited || process.Responding)
                    continue;

                var otherPath = TryGetMainModulePath(process);
                if (currentPath != null &&
                    otherPath != null &&
                    !string.Equals(currentPath, otherPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                Logger.Warning($"[STARTUP] Terminating unresponsive TunnelX host PID={process.Id}");
                process.Kill(entireProcessTree: true);
                terminated = true;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); } catch { }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[STARTUP] Could not terminate unresponsive TunnelX PID={process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return terminated;
    }

    public static void TerminateOtherHosts()
    {
        var current = Process.GetCurrentProcess();
        var currentPath = TryGetMainModulePath(current);

        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id)
                continue;

            try
            {
                if (process.HasExited)
                    continue;

                var otherPath = TryGetMainModulePath(process);
                if (currentPath != null &&
                    otherPath != null &&
                    !string.Equals(currentPath, otherPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                Logger.Warning($"[STARTUP] Terminating stale TunnelX host PID={process.Id}");
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); } catch { }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[STARTUP] Could not terminate stale TunnelX PID={process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string? TryGetMainModulePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }
}
