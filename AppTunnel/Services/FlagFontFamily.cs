using System.IO;
using System.Windows.Media;

namespace AppTunnel.Services;

/// <summary>
/// Resolves the Windows Segoe UI Emoji font file for reliable color flag rendering in WPF.
/// </summary>
public static class FlagFontFamily
{
    private static readonly Lazy<System.Windows.Media.FontFamily> Lazy = new(Create);

    public static System.Windows.Media.FontFamily Instance => Lazy.Value;

    private static System.Windows.Media.FontFamily Create()
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var fileName in new[] { "seguiemj.ttf", "SegoeIcons.ttf" })
        {
            var path = Path.Combine(fontsDir, fileName);
            if (!File.Exists(path))
                continue;

            try
            {
                return new System.Windows.Media.FontFamily(new Uri(path, UriKind.Absolute), "./#Segoe UI Emoji");
            }
            catch
            {
                // try next file / fallback
            }
        }

        return new System.Windows.Media.FontFamily("Segoe UI Emoji, Segoe UI Symbol");
    }
}
