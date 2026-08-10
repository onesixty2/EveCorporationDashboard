using EveCorporationDashboard.Models;

namespace EveCorporationDashboard.Services;

public class CharDetail
{
    public string CharacterName { get; set; } = "";
    public DateTime? LogonDate { get; set; }
    public DateTime? LogoffDate { get; set; }
    public DateTime? StartDate { get; set; }
    public string? ShipType { get; set; }
    public string? Location { get; set; }
    public string Corp { get; set; } = "";
}

/// <summary>One row per person (forum account), aggregating all their mapped characters.</summary>
public class MemberRow
{
    public string Status { get; set; } = "";
    public string ForumName { get; set; } = "";
    public string CharacterNames { get; set; } = "";
    public int CharacterCount { get; set; }
    /// <summary>Location of the person's most recently active character.</summary>
    public string Location { get; set; } = "";
    public double? Paps30 { get; set; }
    public double? Paps60 { get; set; }
    public double? Paps90 { get; set; }
    public bool MinedLast30 { get; set; }
    /// <summary>⛏ / 🧱 / 🏆 for the top three miners across all ledger data.</summary>
    public string? MiningBadge { get; set; }
    public string Mined30Display => MiningBadge ?? (MinedLast30 ? "✔" : "");
    /// <summary>Security flag: seen on EVE/forums/jabber/mumble within 30d, but zero paps.</summary>
    public bool PapsRisk { get; set; }
    public string PapsRiskDisplay => PapsRisk ? "💀" : "";
    public DateTime? LastEve { get; set; }
    public DateTime? LastForum { get; set; }
    public DateTime? LastJabber { get; set; }
    public DateTime? LastMumble { get; set; }
    public DateTime? LastAnywhere { get; set; }
    public string LastEveDisplay { get; set; } = "";
    public string LastForumDisplay { get; set; } = "";
    public string LastJabberDisplay { get; set; } = "";
    public string LastMumbleDisplay { get; set; } = "";
    public string LastAnywhereDisplay { get; set; } = "";
    public List<CharDetail> Characters { get; set; } = new();
}

public static class Statuses
{
    public const string Awol = "AWOL";
    public const string AwayFromGame = "AFK - LOA";
    // Pap-drought ladder for members who ARE around, escalating at 30/60/90 days without paps.
    public const string LowParticipation = "Low Participation";
    public const string NoParticipation = "No Participation";
    public const string Inactive = "Inactive";
    public const string Afk = "AFK";
    public const string Active = "Active";
    /// <summary>Forum account with no characters in the corp structure - the vacationers group.</summary>
    public const string NoEsiData = "AFK - Vacationer";
    public const string Unmapped = "Unmapped character";
}

public static class RowBuilder
{
    public static List<MemberRow> Build(AppData data, AppSettings settings, DateTime utcNow)
    {
        var minedLast30 = data.MiningEntries
            .Where(e => (utcNow - e.Date).TotalDays <= 30)
            .Select(e => e.CharacterId)
            .ToHashSet();

        var mapByChar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in data.CharacterMappings)
            if (!string.IsNullOrWhiteSpace(m.CharacterName) && !string.IsNullOrWhiteSpace(m.ForumName))
                mapByChar[m.CharacterName.Trim()] = m.ForumName.Trim();

        var accountsByName = new Dictionary<string, ForumAccount>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in data.ForumAccounts)
            if (!string.IsNullOrWhiteSpace(a.ForumName))
                accountsByName[a.ForumName.Trim()] = a;

        // Group ESI characters by forum name; unmapped characters each get their own row.
        var charsByForum = new Dictionary<string, List<EsiMemberRecord>>(StringComparer.OrdinalIgnoreCase);
        var unmapped = new List<EsiMemberRecord>();
        foreach (var c in EnrichWithImport(data.EsiMembers.Concat(data.ExternalCharacters), data.CharacterMappings))
        {
            if (mapByChar.TryGetValue(c.CharacterName, out var forum))
            {
                if (!charsByForum.TryGetValue(forum, out var list)) charsByForum[forum] = list = new();
                list.Add(c);
            }
            else unmapped.Add(c);
        }

        // Top three miners across the entire ledger get rank badges instead of the checkmark.
        var minedTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in data.MiningEntries)
        {
            string account = mapByChar.GetValueOrDefault(e.CharacterName, e.CharacterName);
            minedTotals[account] = minedTotals.GetValueOrDefault(account) + e.Quantity;
        }
        string[] rankBadges = { "⛏", "🧱", "🏆" };
        var badges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int rank = 0;
        foreach (var top in minedTotals.Where(kv => kv.Value > 0)
                     .OrderByDescending(kv => kv.Value).Take(rankBadges.Length))
            badges[top.Key] = rankBadges[rank++];

        var rows = new List<MemberRow>();

        foreach (var (forumName, chars) in charsByForum)
        {
            accountsByName.TryGetValue(forumName, out var account);
            var row = BuildRow(forumName, chars, account, hasForumData: account != null, settings, utcNow, minedLast30);
            row.MiningBadge = badges.GetValueOrDefault(forumName);
            rows.Add(row);
        }

        // Forum accounts with no matched in-corp characters still get a row.
        foreach (var (name, account) in accountsByName)
        {
            if (charsByForum.ContainsKey(name)) continue;
            var row = BuildRow(name, new List<EsiMemberRecord>(), account, hasForumData: true, settings, utcNow, minedLast30);
            row.Status = Statuses.NoEsiData;
            row.MiningBadge = badges.GetValueOrDefault(name);
            rows.Add(row);
        }

        foreach (var c in unmapped)
        {
            var row = BuildRow("(unmapped)", new List<EsiMemberRecord> { c }, null, hasForumData: false, settings, utcNow, minedLast30);
            row.Status = Statuses.Unmapped;
            row.ForumName = "(unmapped)";
            row.MiningBadge = badges.GetValueOrDefault(c.CharacterName);
            rows.Add(row);
        }

        return rows
            .OrderBy(r => StatusRank(r.Status))
            .ThenBy(r => r.ForumName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Fills gaps in ESI records from the pilot map import (last online, joined, ship).
    /// ESI data always wins where present - the import only covers alt corp characters
    /// the corp tracking endpoint can't see.
    /// </summary>
    public static List<EsiMemberRecord> EnrichWithImport(
        IEnumerable<EsiMemberRecord> chars, IEnumerable<CharacterMapping> mappings)
    {
        var byName = new Dictionary<string, CharacterMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mappings)
            if (!string.IsNullOrWhiteSpace(m.CharacterName)) byName[m.CharacterName.Trim()] = m;

        return chars.Select(c =>
        {
            if (!byName.TryGetValue(c.CharacterName, out var m)) return c;
            if (c.LogonDate.HasValue && c.StartDate.HasValue && c.ShipType != null) return c;
            return new EsiMemberRecord
            {
                CharacterId = c.CharacterId,
                CharacterName = c.CharacterName,
                CorporationName = c.CorporationName,
                Location = c.Location,
                LogoffDate = c.LogoffDate,
                LogonDate = c.LogonDate ?? m.LastOnline,
                StartDate = c.StartDate ?? m.JoinedDate,
                ShipType = c.ShipType ?? (string.IsNullOrWhiteSpace(m.ShipType) ? null : m.ShipType),
            };
        }).ToList();
    }

    // Severity order for the default Overview sort - the AFK categories stay adjacent.
    private static int StatusRank(string s) => s switch
    {
        Statuses.Awol => 0,
        Statuses.AwayFromGame => 1,
        Statuses.NoEsiData => 2,
        Statuses.Afk => 3,
        Statuses.Inactive => 4,
        Statuses.NoParticipation => 5,
        Statuses.LowParticipation => 6,
        Statuses.Unmapped => 7,
        _ => 8,
    };

    private static MemberRow BuildRow(string forumName, List<EsiMemberRecord> chars, ForumAccount? account,
        bool hasForumData, AppSettings settings, DateTime utcNow, HashSet<long> minedLast30)
    {
        DateTime? lastEve = MaxDate(chars.SelectMany(c => new[] { c.LogonDate, c.LogoffDate }));
        DateTime? lastForum = account?.LastForum;
        DateTime? lastJabber = account?.LastJabber;
        DateTime? lastMumble = account?.LastMumble;
        DateTime? lastOut = MaxDate(new[] { lastForum, lastJabber, lastMumble });
        DateTime? lastAnywhere = MaxDate(new[] { lastEve, lastOut });

        var row = new MemberRow
        {
            ForumName = forumName,
            CharacterNames = string.Join(", ", chars.Select(c => c.CharacterName).OrderBy(n => n)),
            CharacterCount = chars.Count,
            Paps30 = account?.Paps30,
            Paps60 = account?.Paps60,
            Paps90 = account?.Paps90,
            MinedLast30 = chars.Any(c => minedLast30.Contains(c.CharacterId)),
            LastEve = lastEve,
            LastForum = lastForum,
            LastJabber = lastJabber,
            LastMumble = lastMumble,
            LastAnywhere = lastAnywhere,
            LastEveDisplay = FormatDate(lastEve, null, utcNow),
            LastForumDisplay = FormatDate(lastForum, account?.LastForumRaw, utcNow),
            LastJabberDisplay = FormatDate(lastJabber, account?.LastJabberRaw, utcNow),
            LastMumbleDisplay = FormatDate(lastMumble, account?.LastMumbleRaw, utcNow),
            LastAnywhereDisplay = FormatDate(lastAnywhere, null, utcNow),
            Characters = chars.Select(c => new CharDetail
            {
                CharacterName = c.CharacterName,
                LogonDate = c.LogonDate,
                LogoffDate = c.LogoffDate,
                StartDate = c.StartDate,
                ShipType = c.ShipType,
                Location = c.Location,
                Corp = c.CorporationName,
            }).OrderByDescending(c => c.LogoffDate ?? c.LogonDate ?? DateTime.MinValue).ToList(),
        };
        row.Location = row.Characters.FirstOrDefault()?.Location ?? "";

        // Security flag: present somewhere (EVE, forums, jabber, or mumble) in the last
        // 30 days, yet zero paps in that window.
        bool present30 =
            (lastEve.HasValue && (utcNow - lastEve.Value).TotalDays <= 30) ||
            (lastOut.HasValue && (utcNow - lastOut.Value).TotalDays <= 30);
        row.PapsRisk = present30 && (account?.Paps30 ?? 0) <= 0;

        row.Status = Evaluate(chars.Count > 0, lastEve, lastOut, hasForumData, account, settings, utcNow);
        return row;
    }

    private static string Evaluate(bool hasChars, DateTime? lastEve, DateTime? lastOut, bool hasForumData,
        ForumAccount? account, AppSettings settings, DateTime utcNow)
    {
        if (!hasChars) return Statuses.NoEsiData;

        int threshold = settings.InactiveDaysThreshold;
        // A character in ESI with no logon date at all counts as inactive forever.
        double daysEve = lastEve.HasValue ? (utcNow - lastEve.Value).TotalDays : double.MaxValue;
        double daysOut = lastOut.HasValue ? (utcNow - lastOut.Value).TotalDays : double.MaxValue;

        if (daysEve >= threshold)
        {
            // Not logging into EVE. Are they at least around out of game?
            if (hasForumData && daysOut < threshold) return Statuses.AwayFromGame;
            return Statuses.Awol;
        }

        // Present in game - escalate by how long the pap drought has lasted (30/60/90 days).
        double paps30 = account?.Paps30 ?? 0;
        double paps60 = account?.Paps60 ?? 0;
        double paps90 = account?.Paps90 ?? 0;
        if (paps90 <= 0) return Statuses.Afk;
        if (paps60 <= 0) return Statuses.Inactive;
        if (paps30 <= 0) return Statuses.NoParticipation;
        if (paps30 <= settings.MinPaps30) return Statuses.LowParticipation;
        return Statuses.Active;
    }

    private static DateTime? MaxDate(IEnumerable<DateTime?> dates)
    {
        DateTime? max = null;
        foreach (var d in dates)
            if (d.HasValue && (!max.HasValue || d.Value > max.Value)) max = d;
        return max;
    }

    private static string FormatDate(DateTime? date, string? raw, DateTime utcNow)
    {
        if (date.HasValue)
        {
            int days = (int)Math.Floor((utcNow - date.Value).TotalDays);
            return $"{date.Value:yyyy-MM-dd} ({days}d)";
        }
        return string.IsNullOrWhiteSpace(raw) ? "" : raw;
    }
}
