using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AppTunnel.Services;

/// <summary>Resolves release notes for the update notification (GitHub body or local CHANGELOG.md).</summary>
public static class ChangelogService
{
    public static string ResolveDisplayNotes(string? githubReleaseBody, string? tagName)
    {
        var body = NormalizeNotes(githubReleaseBody);
        if (!string.IsNullOrWhiteSpace(body))
            return body;

        if (!string.IsNullOrWhiteSpace(tagName))
        {
            var fromFile = TryGetSectionFromChangelogFile(tagName);
            if (!string.IsNullOrWhiteSpace(fromFile))
                return fromFile!;
        }

        return string.Empty;
    }

    public static string? TryGetSectionFromChangelogFile(string tagOrVersion)
    {
        var versionKey = NormalizeVersionHeading(tagOrVersion);
        if (string.IsNullOrWhiteSpace(versionKey))
            return null;

        foreach (var path in FindChangelogPaths())
        {
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var section = ExtractSection(text, versionKey);
                if (!string.IsNullOrWhiteSpace(section))
                    return section.Trim();
            }
            catch
            {
                // try next path
            }
        }

        return null;
    }

    private static IEnumerable<string> FindChangelogPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(dir); i++)
        {
            var candidate = Path.Combine(dir, "CHANGELOG.md");
            if (File.Exists(candidate) && seen.Add(candidate))
                yield return candidate;

            var parent = Directory.GetParent(dir)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || parent == dir)
                break;
            dir = parent;
        }
    }

    private static string? ExtractSection(string changelog, string versionKey)
    {
        var pattern = $@"(?ms)^##\s+{Regex.Escape(versionKey)}\s+-\s+.*?\r?\n(?<body>.*?)(?=^##\s|\z)";
        var match = Regex.Match(changelog, pattern);
        return match.Success ? match.Groups["body"].Value.Trim() : null;
    }

    private static string NormalizeVersionHeading(string tagOrVersion)
    {
        var v = (tagOrVersion ?? "").Trim().TrimStart('v', 'V');
        if (GitHubReleaseChecker.TryParseVersion(v, out var version))
            return version.ToString(3);
        return v;
    }

    private static string NormalizeNotes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        return raw.Replace("\r\n", "\n").Trim();
    }
}
