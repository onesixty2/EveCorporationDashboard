using EveCorporationDashboard.Models;

namespace EveCorporationDashboard.Services;

/// <summary>One parsed row of a corp characters page, before it is folded into the map.</summary>
public sealed class PilotMapRow
{
    public string CharacterName { get; set; } = "";
    public string ForumName { get; set; } = "";
    /// <summary>Null means the column was not mapped, so any existing value is left alone.</summary>
    public string? LastOnlineRaw { get; set; }
    public string? JoinedRaw { get; set; }
    public string? Ship { get; set; }
}

/// <summary>
/// Folds a corp characters page into the pilot map.
///
/// A page imported against a known corp source is authoritative for that corp: mappings
/// already tagged with the same source label are retired unless the page still lists them,
/// so people who left stop showing up in reports. Characters belonging to other corps, and
/// untagged rows predating source tracking, are never touched by someone else's import.
/// </summary>
public static class PilotMapImport
{
    public sealed record Outcome(int Added, int Updated, IReadOnlyList<string> Departed);

    public static Outcome Apply(List<CharacterMapping> mappings, IReadOnlyList<PilotMapRow> rows,
        string? sourceLabel, DateTime utcNow)
    {
        bool scoped = !string.IsNullOrEmpty(sourceLabel);

        var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            if (!string.IsNullOrWhiteSpace(row.CharacterName) && !string.IsNullOrWhiteSpace(row.ForumName))
                incoming.Add(row.CharacterName.Trim());

        var departed = new List<string>();
        if (scoped)
        {
            foreach (var m in mappings)
                if (string.Equals(m.SourceLabel, sourceLabel, StringComparison.OrdinalIgnoreCase) &&
                    !incoming.Contains(m.CharacterName))
                    departed.Add(m.CharacterName);

            mappings.RemoveAll(m =>
                string.Equals(m.SourceLabel, sourceLabel, StringComparison.OrdinalIgnoreCase) &&
                !incoming.Contains(m.CharacterName));
        }

        int added = 0, updated = 0;
        foreach (var row in rows)
        {
            string charName = row.CharacterName.Trim();
            string forumName = row.ForumName.Trim();
            if (charName.Length == 0 || forumName.Length == 0) continue;

            var existing = mappings.FirstOrDefault(m =>
                string.Equals(m.CharacterName, charName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new CharacterMapping { CharacterName = charName };
                mappings.Add(existing);
                added++;
            }
            else
            {
                updated++;
            }
            existing.ForumName = forumName;
            // Claim the row for this source. This also adopts untagged rows left over from
            // before source tracking, and characters that have moved in from another corp.
            if (scoped) existing.SourceLabel = sourceLabel;

            if (row.LastOnlineRaw != null)
                existing.LastOnline = TableParser.ParseFuzzyDate(row.LastOnlineRaw, utcNow);
            if (row.JoinedRaw != null)
                existing.JoinedDate = TableParser.ParseFuzzyDate(row.JoinedRaw, utcNow);
            if (!string.IsNullOrWhiteSpace(row.Ship))
                existing.ShipType = row.Ship.Trim();
        }

        return new Outcome(added, updated, departed);
    }
}
