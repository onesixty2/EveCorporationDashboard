using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace EveCorporationDashboard.Services;

/// <summary>
/// Parses tabular data off the clipboard. Prefers the HTML clipboard format (a table copied
/// from a browser keeps its structure there), falling back to tab-separated plain text.
/// </summary>
public static class TableParser
{
    public static List<string[]>? ParseClipboard(string? html, string? text)
    {
        List<string[]>? rows = null;
        if (!string.IsNullOrWhiteSpace(html))
            rows = ParseHtmlTable(html);
        if ((rows == null || rows.Count == 0) && !string.IsNullOrWhiteSpace(text))
            rows = ParseTabSeparated(text);
        if (rows == null || rows.Count == 0) return null;

        // Pad ragged rows to a uniform width so index-based column bindings never miss.
        int width = rows.Max(r => r.Length);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Length < width)
            {
                var padded = new string[width];
                rows[i].CopyTo(padded, 0);
                for (int j = rows[i].Length; j < width; j++) padded[j] = "";
                rows[i] = padded;
            }
        }
        return rows;
    }

    private static List<string[]>? ParseHtmlTable(string html)
    {
        var tables = Regex.Matches(html, @"<table[\s\S]*?</table>", RegexOptions.IgnoreCase);
        List<string[]>? best = null;
        foreach (Match table in tables)
        {
            var rows = new List<string[]>();
            foreach (Match tr in Regex.Matches(table.Value, @"<tr[\s\S]*?</tr>", RegexOptions.IgnoreCase))
            {
                var cells = new List<string>();
                foreach (Match cell in Regex.Matches(tr.Value, @"<t[dh][^>]*>([\s\S]*?)</t[dh]>", RegexOptions.IgnoreCase))
                    cells.Add(CleanCell(cell.Groups[1].Value));
                if (cells.Any(c => c.Length > 0))
                    rows.Add(cells.ToArray());
            }
            if (best == null || rows.Count > best.Count) best = rows;
        }
        return best is { Count: > 0 } ? best : null;
    }

    private static List<string[]> ParseTabSeparated(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Split('\t').Select(c => c.Trim()).ToArray())
            .Where(r => r.Any(c => c.Length > 0))
            .ToList();
    }

    /// <summary>
    /// Pulls corporation links (/corporation/view/{id}) out of copied page HTML, noting
    /// which table rows the manager marks as "Member Corp".
    /// </summary>
    public static List<(long CorpId, string Name, bool IsMemberCorp)> ExtractCorpLinks(string? html)
    {
        var byId = new Dictionary<long, (string Name, bool IsMember)>();
        if (string.IsNullOrEmpty(html)) return new();

        // Work row by row so each corp inherits its own row's type column.
        var rowMatches = Regex.Matches(html, @"<tr[\s\S]*?</tr>", RegexOptions.IgnoreCase);
        var chunks = rowMatches.Count > 0
            ? rowMatches.Select(m => m.Value).ToList()
            : new List<string> { html };

        foreach (var chunk in chunks)
        {
            bool rowIsMember = Regex.IsMatch(chunk, @"member\s*corp", RegexOptions.IgnoreCase);
            foreach (Match m in Regex.Matches(chunk,
                         @"<a[^>]+href=""[^""]*corporation/view/(\d+)[^""]*""[^>]*>([\s\S]*?)</a>",
                         RegexOptions.IgnoreCase))
            {
                long id = long.Parse(m.Groups[1].Value);
                string name = CleanCell(m.Groups[2].Value);
                // The same corp can be linked more than once (icon + name); keep the richest text.
                if (!byId.TryGetValue(id, out var existing))
                    byId[id] = (name, rowIsMember);
                else
                    byId[id] = (name.Length > existing.Name.Length ? name : existing.Name,
                        existing.IsMember || rowIsMember);
            }
        }
        return byId.Select(kv => (kv.Key, kv.Value.Name, kv.Value.IsMember)).ToList();
    }

    /// <summary>
    /// Pulls auth group links (/auth-group/view/{id}) out of copied page HTML, with the
    /// group's team icon URL when its table row carries one.
    /// </summary>
    public static List<(long GroupId, string Name, string? IconUrl)> ExtractAuthGroupLinks(string? html)
    {
        var byId = new Dictionary<long, (string Name, string? Icon)>();
        if (string.IsNullOrEmpty(html)) return new();

        var rowMatches = Regex.Matches(html, @"<tr[\s\S]*?</tr>", RegexOptions.IgnoreCase);
        var chunks = rowMatches.Count > 0
            ? rowMatches.Select(m => m.Value).ToList()
            : new List<string> { html };

        foreach (var chunk in chunks)
        {
            string? icon = null;
            var img = Regex.Match(chunk, @"<img[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase);
            if (img.Success)
            {
                icon = img.Groups[1].Value;
                if (icon.StartsWith("//")) icon = "https:" + icon;
                else if (icon.StartsWith("/")) icon = "https://goonfleet.com" + icon;
            }
            foreach (Match m in Regex.Matches(chunk,
                         @"<a[^>]+href=""[^""]*auth-group/view/(\d+)[^""]*""[^>]*>([\s\S]*?)</a>",
                         RegexOptions.IgnoreCase))
            {
                long id = long.Parse(m.Groups[1].Value);
                string name = CleanCell(m.Groups[2].Value);
                if (!byId.TryGetValue(id, out var existing))
                    byId[id] = (name, icon);
                else
                    byId[id] = (name.Length > existing.Name.Length ? name : existing.Name,
                        existing.Icon ?? icon);
            }
        }
        return byId.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Icon)).ToList();
    }

    private static string CleanCell(string inner)
    {
        var s = Regex.Replace(inner, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static readonly string[] NeverValues = { "", "-", "--", "n/a", "na", "never", "none", "unknown" };

    /// <summary>
    /// Best-effort date parsing for cells like "2026-07-01 14:33", "3 days ago", "yesterday", "Never".
    /// Returns null when the cell holds no usable date; keep the raw text for display in that case.
    /// </summary>
    public static DateTime? ParseFuzzyDate(string? cell, DateTime utcNow)
    {
        if (cell == null) return null;
        var s = cell.Trim();
        if (NeverValues.Contains(s.ToLowerInvariant())) return null;

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
            return dt;

        var lower = s.ToLowerInvariant();
        if (lower is "today" or "just now" or "now") return utcNow;
        if (lower == "yesterday") return utcNow.AddDays(-1);

        var m = Regex.Match(lower, @"(?:(\d+(?:\.\d+)?)|an?)\s*(second|minute|min|hour|hr|day|week|month|year)s?\s*ago");
        if (m.Success)
        {
            double n = m.Groups[1].Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
            return m.Groups[2].Value switch
            {
                "second" => utcNow.AddSeconds(-n),
                "minute" or "min" => utcNow.AddMinutes(-n),
                "hour" or "hr" => utcNow.AddHours(-n),
                "day" => utcNow.AddDays(-n),
                "week" => utcNow.AddDays(-7 * n),
                "month" => utcNow.AddMonths(-(int)Math.Round(n)),
                "year" => utcNow.AddYears(-(int)Math.Round(n)),
                _ => null,
            };
        }
        return null;
    }

    public static double? ParseNumber(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return null;
        var s = cell.Trim().Replace(",", "");
        var m = Regex.Match(s, @"-?\d+(\.\d+)?");
        if (!m.Success) return null;
        return double.Parse(m.Value, CultureInfo.InvariantCulture);
    }
}
