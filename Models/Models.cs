namespace EveCorporationDashboard.Models;

public class AppSettings
{
    /// <summary>Client ID of the EVE application registered at developers.eveonline.com.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Must exactly match the callback URL registered on the EVE application, trailing slash included.</summary>
    public string CallbackUrl { get; set; } = "http://localhost:53411/callback/";

    /// <summary>Days without an EVE login before a member counts as away/inactive.</summary>
    public int InactiveDaysThreshold { get; set; } = 30;

    /// <summary>Minimum paps in the trailing 30 days to count as participating.</summary>
    public double MinPaps30 { get; set; } = 1.0;

    public bool DarkMode { get; set; }

    /// <summary>UI zoom factor (0.8 to 1.6).</summary>
    public double UiScale { get; set; } = 1.0;

    public string? RefreshToken { get; set; }
    public long CharacterId { get; set; }
    public string? CharacterName { get; set; }
    public long CorporationId { get; set; }

    /// <summary>Pilot map source pages, built via the 'Corps' scan.</summary>
    public List<CorpSource> PilotMapCorps { get; set; } = new();

    /// <summary>The member corp's paps auth group, captured from the auth group list page.</summary>
    public string? PapsGroupUrl { get; set; }
    public string? PapsGroupName { get; set; }

    /// <summary>Ticker of the director's corp, shown in the window title.</summary>
    public string? CorporationTicker { get; set; }
    /// <summary>Team icon captured from the auth group page; falls back to the ESI corp logo.</summary>
    public string? CorpIconUrl { get; set; }
}

/// <summary>A pilot map source page: a member or alt corp, labeled by its owner.</summary>
public class CorpSource
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Ticker { get; set; }
    public string? CorpName { get; set; }
    /// <summary>True when the manager's corp list page marks this as a Member Corp.</summary>
    public bool IsMemberCorp { get; set; }
}

public class ForumAccount
{
    public string ForumName { get; set; } = "";
    public double? Paps30 { get; set; }
    public double? Paps60 { get; set; }
    public double? Paps90 { get; set; }
    public DateTime? LastForum { get; set; }
    public DateTime? LastJabber { get; set; }
    public DateTime? LastMumble { get; set; }
    // Raw cell text kept so unparseable values ("Never", odd formats) still display.
    public string? LastForumRaw { get; set; }
    public string? LastJabberRaw { get; set; }
    public string? LastMumbleRaw { get; set; }
}

public class CharacterMapping
{
    public string CharacterName { get; set; } = "";
    public string ForumName { get; set; } = "";
    // Optional extras captured from the corp characters page - used as fallback where ESI
    // tracking data isn't available (alt corp characters).
    public DateTime? LastOnline { get; set; }
    public DateTime? JoinedDate { get; set; }
    public string? ShipType { get; set; }
}

public class EsiMemberRecord
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public DateTime? LogonDate { get; set; }
    public DateTime? LogoffDate { get; set; }
    public DateTime? StartDate { get; set; }
    public string? ShipType { get; set; }
    public string? Location { get; set; }
    public string CorporationName { get; set; } = "";
}

/// <summary>A corporation mining observer - in practice a moon mining structure.</summary>
public class MiningObserver
{
    public long ObserverId { get; set; }
    public string Name { get; set; } = "";
    public DateTime? LastUpdated { get; set; }
}

/// <summary>One mining ledger record: character × ore type × day at one observer.</summary>
public class MiningEntry
{
    public long ObserverId { get; set; }
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public long TypeId { get; set; }
    public string OreType { get; set; } = "";
    public DateTime Date { get; set; }
    public long Quantity { get; set; }
    /// <summary>Corporation the character belonged to when the mining was recorded.</summary>
    public long CorporationId { get; set; }
    public string CorporationName { get; set; } = "";
}

/// <summary>One corp-owned citadel (or control tower) and its fuel bay contents.</summary>
public class FuelLocation
{
    public long LocationId { get; set; }
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    /// <summary>Control towers sort to the bottom of the Citadel Fuel tab.</summary>
    public bool IsControlTower { get; set; }
    /// <summary>Solar system, filled for control towers.</summary>
    public string SystemName { get; set; } = "";
    public DateTime? FuelExpires { get; set; }
    public string? State { get; set; }
    /// <summary>Fuel blocks in the fuel bay, all four isotope variants combined.</summary>
    public long FuelBlocks { get; set; }
    /// <summary>Fuel blocks sitting in the structure's corp hangars (reserve stock, not burning).</summary>
    public long HangarBlocks { get; set; }
    /// <summary>Strontium clathrates (control tower stront bay).</summary>
    public long Strontium { get; set; }
}

public class AppData
{
    public List<ForumAccount> ForumAccounts { get; set; } = new();
    public List<CharacterMapping> CharacterMappings { get; set; } = new();
    public List<EsiMemberRecord> EsiMembers { get; set; } = new();
    /// <summary>Mapped characters outside the main corp (alt corps) - no tracking data, resolved via public ESI.</summary>
    public List<EsiMemberRecord> ExternalCharacters { get; set; } = new();
    public List<MiningObserver> MiningObservers { get; set; } = new();
    public List<MiningEntry> MiningEntries { get; set; } = new();
    public List<FuelLocation> FuelLocations { get; set; } = new();
    public DateTime? EsiLastRefreshUtc { get; set; }
    public DateTime? MiningLastRefreshUtc { get; set; }
    public DateTime? FuelLastRefreshUtc { get; set; }
    public DateTime? PapsLastImportUtc { get; set; }
    public DateTime? MappingsLastImportUtc { get; set; }
    /// <summary>Per-corp pilot map import timestamps, keyed by source link label (the owner's forum name).</summary>
    public Dictionary<string, DateTime> PilotMapImportDates { get; set; } = new();
}
