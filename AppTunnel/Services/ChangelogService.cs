using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using AppTunnel.Helpers;
using FlowDirection = System.Windows.FlowDirection;

namespace AppTunnel.Services;

public enum ReleaseNotesLanguageKind
{
    Neutral,
    English,
    Farsi
}

public sealed record ReleaseNotesBlock(
    FlowDirection FlowDirection,
    TextAlignment TextAlignment,
    string Text,
    bool IsSectionHeader,
    ReleaseNotesLanguageKind LanguageKind);

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

    /// <summary>
    /// Splits bilingual release notes so each section keeps its own RTL/LTR (not the UI language only).
    /// </summary>
    public static IReadOnlyList<ReleaseNotesBlock> ParseDisplayBlocks(string? notes, bool preferPersianFirst)
    {
        notes = NormalizeNotes(notes);
        if (string.IsNullOrWhiteSpace(notes))
            return Array.Empty<ReleaseNotesBlock>();

        var blocks = new List<(int Sequence, ReleaseNotesBlock Block)>();
        var lines = notes.Split('\n');
        var bodyLines = new List<string>();
        FlowDirection? sectionDirection = null;
        TextAlignment? sectionAlign = null;
        ReleaseNotesLanguageKind sectionKind = ReleaseNotesLanguageKind.Neutral;
        string? sectionTitle = null;
        var sequence = 0;

        void FlushSection()
        {
            if (sectionTitle == null && bodyLines.Count == 0)
                return;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(sectionTitle) && !IsLanguageOnlyHeading(sectionTitle))
                parts.Add(sectionTitle.Trim());

            var body = string.Join("\n", bodyLines).Trim();
            if (!string.IsNullOrWhiteSpace(body))
                parts.Add(body);

            var text = CleanMarkdownForDisplay(string.Join("\n\n", parts).Trim());
            if (string.IsNullOrWhiteSpace(text))
            {
                bodyLines.Clear();
                sectionTitle = null;
                return;
            }

            var flow = sectionDirection ?? TextHelper.DetectFlowDirection(text);
            var align = sectionAlign ?? (flow == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left);
            blocks.Add((sequence++, new ReleaseNotesBlock(flow, align, text, IsSectionHeader: false, sectionKind)));

            bodyLines.Clear();
            sectionTitle = null;
            sectionDirection = null;
            sectionAlign = null;
            sectionKind = ReleaseNotesLanguageKind.Neutral;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("<!--", StringComparison.Ordinal))
                continue;
            if (TryApplyHtmlDirectionSection(line, FlushSection, ref sectionDirection, ref sectionAlign, ref sectionKind))
                continue;
            if (IsHtmlLayoutWrapper(line))
            {
                if (line.Trim().Equals("</div>", StringComparison.OrdinalIgnoreCase))
                    FlushSection();
                continue;
            }
            if (line.Contains("release-provenance", StringComparison.OrdinalIgnoreCase))
                continue;

            var headingMatch = Regex.Match(line, @"^#{2,3}\s+(.+?)\s*$");
            if (headingMatch.Success)
            {
                FlushSection();
                var heading = headingMatch.Groups[1].Value.Trim();
                if (IsSkippedReleaseHeading(heading))
                    continue;

                (sectionDirection, sectionAlign, sectionKind) = ResolveSectionLayout(heading);
                sectionTitle = heading;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line))
                bodyLines.Add(line);
        }

        FlushSection();

        if (blocks.Count == 0)
        {
            var flow = TextHelper.DetectFlowDirection(notes);
            blocks.Add((0, new ReleaseNotesBlock(
                flow,
                flow == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left,
                notes,
                false,
                TextHelper.IsPersianText(notes) ? ReleaseNotesLanguageKind.Farsi : ReleaseNotesLanguageKind.English)));
        }

        return blocks
            .OrderBy(entry => SortKey(entry.Block.LanguageKind, preferPersianFirst))
            .ThenBy(entry => entry.Sequence)
            .Select(entry => entry.Block)
            .ToList();
    }

    private static bool IsSkippedReleaseHeading(string heading)
        => heading.Contains("provenance", StringComparison.OrdinalIgnoreCase);

    private static bool IsLanguageOnlyHeading(string heading)
        => heading.Equals("فارسی", StringComparison.Ordinal) ||
           heading.Equals("English", StringComparison.OrdinalIgnoreCase);

    private static bool TryApplyHtmlDirectionSection(
        string line,
        Action flushSection,
        ref FlowDirection? sectionDirection,
        ref TextAlignment? sectionAlign,
        ref ReleaseNotesLanguageKind sectionKind)
    {
        var match = Regex.Match(line, @"<div\b[^>]*\bdir\s*=\s*[""']?(?<dir>rtl|ltr)[""']?", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        flushSection();
        var dir = match.Groups["dir"].Value;
        if (dir.Equals("rtl", StringComparison.OrdinalIgnoreCase))
        {
            sectionDirection = FlowDirection.RightToLeft;
            sectionAlign = TextAlignment.Right;
            sectionKind = ReleaseNotesLanguageKind.Farsi;
        }
        else
        {
            sectionDirection = FlowDirection.LeftToRight;
            sectionAlign = TextAlignment.Left;
            sectionKind = ReleaseNotesLanguageKind.English;
        }

        return true;
    }

    private static bool IsHtmlLayoutWrapper(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("<div", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("</div>", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanMarkdownForDisplay(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = Regex.Replace(text, @"\[(?<label>[^\]]+)\]\((?<url>[^)]+)\)", "${label}");
        text = Regex.Replace(text, @"(\*\*|__)(?<value>.+?)\1", "${value}");
        text = Regex.Replace(text, @"`(?<value>[^`]+)`", "${value}");
        text = Regex.Replace(text, @"</?[^>]+>", "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    private static (FlowDirection Flow, TextAlignment Align, ReleaseNotesLanguageKind Kind) ResolveSectionLayout(string heading)
    {
        var normalized = heading.Trim();
        if (IsEnglishSectionHeading(normalized))
            return (FlowDirection.LeftToRight, TextAlignment.Left, ReleaseNotesLanguageKind.English);

        if (IsFarsiSectionHeading(normalized))
            return (FlowDirection.RightToLeft, TextAlignment.Right, ReleaseNotesLanguageKind.Farsi);

        var flow = TextHelper.DetectFlowDirection(normalized);
        var kind = flow == FlowDirection.RightToLeft
            ? ReleaseNotesLanguageKind.Farsi
            : ReleaseNotesLanguageKind.English;
        return (flow, flow == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left, kind);
    }

    private static bool IsEnglishSectionHeading(string heading)
    {
        if (heading.Equals("English", StringComparison.OrdinalIgnoreCase))
            return true;
        if (heading.Equals("Downloads", StringComparison.OrdinalIgnoreCase))
            return true;
        return heading.Contains("download", StringComparison.OrdinalIgnoreCase)
               && !TextHelper.IsPersianText(heading);
    }

    private static bool IsFarsiSectionHeading(string heading)
    {
        if (heading.Equals("فارسی", StringComparison.Ordinal))
            return true;
        if (heading.Contains("دانلود", StringComparison.Ordinal))
            return true;
        return TextHelper.IsPersianText(heading)
               && !heading.Contains("English", StringComparison.OrdinalIgnoreCase);
    }

    private static int SortKey(ReleaseNotesLanguageKind kind, bool preferPersianFirst)
    {
        if (preferPersianFirst)
        {
            return kind switch
            {
                ReleaseNotesLanguageKind.Farsi => 0,
                ReleaseNotesLanguageKind.Neutral => 1,
                ReleaseNotesLanguageKind.English => 2,
                _ => 3
            };
        }

        return kind switch
        {
            ReleaseNotesLanguageKind.English => 0,
            ReleaseNotesLanguageKind.Neutral => 1,
            ReleaseNotesLanguageKind.Farsi => 2,
            _ => 3
        };
    }
}
