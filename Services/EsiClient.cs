using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveCorporationDashboard.Models;

namespace EveCorporationDashboard.Services;

public class EsiMemberTrackingDto
{
    [JsonPropertyName("character_id")] public long CharacterId { get; set; }
    [JsonPropertyName("logon_date")] public DateTime? LogonDate { get; set; }
    [JsonPropertyName("logoff_date")] public DateTime? LogoffDate { get; set; }
    [JsonPropertyName("start_date")] public DateTime? StartDate { get; set; }
    [JsonPropertyName("ship_type_id")] public long? ShipTypeId { get; set; }
    [JsonPropertyName("location_id")] public long? LocationId { get; set; }
}

public class MiningObserverDto
{
    [JsonPropertyName("observer_id")] public long ObserverId { get; set; }
    [JsonPropertyName("observer_type")] public string? ObserverType { get; set; }
    [JsonPropertyName("last_updated")] public DateTime? LastUpdated { get; set; }
}

public class MiningLedgerEntryDto
{
    [JsonPropertyName("character_id")] public long CharacterId { get; set; }
    [JsonPropertyName("last_updated")] public DateTime LastUpdated { get; set; }
    [JsonPropertyName("quantity")] public long Quantity { get; set; }
    [JsonPropertyName("type_id")] public long TypeId { get; set; }
    [JsonPropertyName("recorded_corporation_id")] public long RecordedCorporationId { get; set; }
}

public class StarbaseDto
{
    [JsonPropertyName("starbase_id")] public long StarbaseId { get; set; }
    [JsonPropertyName("system_id")] public long SystemId { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
}

public class CorpAssetDto
{
    [JsonPropertyName("item_id")] public long ItemId { get; set; }
    [JsonPropertyName("type_id")] public long TypeId { get; set; }
    [JsonPropertyName("quantity")] public long Quantity { get; set; }
    [JsonPropertyName("location_id")] public long LocationId { get; set; }
    [JsonPropertyName("location_flag")] public string LocationFlag { get; set; } = "";
    [JsonPropertyName("location_type")] public string LocationType { get; set; } = "";
}

public class CorpStructureDto
{
    [JsonPropertyName("structure_id")] public long StructureId { get; set; }
    [JsonPropertyName("fuel_expires")] public DateTime? FuelExpires { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("type_id")] public long TypeId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public class EsiClient
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>The four isotope variants of Fuel Blocks; tracked as one combined quantity.</summary>
    private static readonly HashSet<long> FuelBlockTypeIds = new()
    {
        4051,  // Nitrogen Fuel Block
        4246,  // Hydrogen Fuel Block
        4247,  // Helium Fuel Block
        4312,  // Oxygen Fuel Block
    };

    /// <summary>Strontium Clathrates (control tower stront bays).</summary>
    private const long StrontiumTypeId = 16275;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { BaseAddress = new Uri("https://esi.evetech.net/latest/") };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("EveCorporationDashboard/1.0");
        return c;
    }

    public async Task<long> GetCorporationIdAsync(long characterId)
    {
        using var doc = JsonDocument.Parse(await GetStringAsync($"characters/{characterId}/"));
        return doc.RootElement.GetProperty("corporation_id").GetInt64();
    }

    public async Task<(string Name, string Ticker, long CeoId)> GetCorporationInfoAsync(long corporationId)
    {
        using var doc = JsonDocument.Parse(await GetStringAsync($"corporations/{corporationId}/"));
        return (doc.RootElement.GetProperty("name").GetString() ?? "",
            doc.RootElement.GetProperty("ticker").GetString() ?? "",
            doc.RootElement.TryGetProperty("ceo_id", out var ceo) ? ceo.GetInt64() : 0);
    }

    /// <summary>Requires the Director role in-game and the esi-corporations.track_members.v1 scope.</summary>
    public async Task<List<EsiMemberRecord>> GetMemberTrackingAsync(long corporationId, string accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"corporations/{corporationId}/membertracking/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await Http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                "ESI refused member tracking (403). The logged-in character needs the Director role " +
                "in this corporation, and the app must be authorized with the esi-corporations.track_members.v1 scope.");
        resp.EnsureSuccessStatusCode();

        var dtos = await resp.Content.ReadFromJsonAsync<List<EsiMemberTrackingDto>>() ?? new();

        // Stations and solar systems resolve via the public names endpoint; Upwell structure
        // IDs (>= 10^12) need an authed per-structure lookup.
        const long StructureIdFloor = 1_000_000_000_000;
        var charIds = dtos.Select(d => d.CharacterId).Distinct().ToList();
        var shipIds = dtos.Where(d => d.ShipTypeId.HasValue).Select(d => d.ShipTypeId!.Value).Distinct().ToList();
        var plainLocIds = dtos.Where(d => d.LocationId is < StructureIdFloor)
            .Select(d => d.LocationId!.Value).Distinct().ToList();
        var names = await ResolveNamesAsync(charIds.Concat(shipIds).Concat(plainLocIds));

        var structureNames = new Dictionary<long, string>();
        foreach (long sid in dtos.Where(d => d.LocationId is >= StructureIdFloor)
                     .Select(d => d.LocationId!.Value).Distinct())
            structureNames[sid] = await GetStructureNameAsync(sid, accessToken);

        return dtos.Select(d => new EsiMemberRecord
        {
            CharacterId = d.CharacterId,
            CharacterName = names.GetValueOrDefault(d.CharacterId, d.CharacterId.ToString()),
            LogonDate = d.LogonDate,
            LogoffDate = d.LogoffDate,
            StartDate = d.StartDate,
            ShipType = d.ShipTypeId.HasValue ? names.GetValueOrDefault(d.ShipTypeId.Value) : null,
            Location = d.LocationId.HasValue
                ? names.GetValueOrDefault(d.LocationId.Value,
                    structureNames.GetValueOrDefault(d.LocationId.Value, $"Location {d.LocationId.Value}"))
                : null,
        }).ToList();
    }

    /// <summary>Requires esi-universe.read_structures.v1 and docking access; falls back to the raw ID.</summary>
    private async Task<string> GetStructureNameAsync(long structureId, string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"universe/structures/{structureId}/");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                return doc.RootElement.GetProperty("name").GetString() ?? $"Structure {structureId}";
            }
        }
        catch { /* fall through */ }
        return $"Structure {structureId}";
    }

    /// <summary>
    /// Fetches the corporation mining ledger. Requires the Accountant or Director role in-game
    /// and the esi-industry.read_corporation_mining.v1 scope.
    /// </summary>
    public async Task<(List<MiningObserver> Observers, List<MiningEntry> Entries)> GetMiningLedgerAsync(
        long corporationId, string accessToken)
    {
        const string miningForbidden =
            "ESI refused the corporation mining ledger (403). The logged-in character needs the Accountant " +
            "or Director role, and the app must be authorized with the esi-industry.read_corporation_mining.v1 " +
            "scope (Settings → Forget login, then log in again after adding the scope).";
        var observerDtos = await GetPagedAsync<MiningObserverDto>(
            $"corporation/{corporationId}/mining/observers/", accessToken, miningForbidden);

        var raw = new List<(long ObserverId, MiningLedgerEntryDto Dto)>();
        foreach (var o in observerDtos)
        {
            var page = await GetPagedAsync<MiningLedgerEntryDto>(
                $"corporation/{corporationId}/mining/observers/{o.ObserverId}/", accessToken, miningForbidden);
            raw.AddRange(page.Select(d => (o.ObserverId, d)));
        }

        var names = await ResolveNamesAsync(
            raw.Select(r => r.Dto.CharacterId)
                .Concat(raw.Select(r => r.Dto.TypeId))
                .Concat(raw.Select(r => r.Dto.RecordedCorporationId)));

        var observers = new List<MiningObserver>();
        foreach (var o in observerDtos)
        {
            // Observers are structures; resolve their names via the authed structure endpoint.
            string name = o.ObserverType == "structure"
                ? await GetStructureNameAsync(o.ObserverId, accessToken)
                : $"Observer {o.ObserverId}";
            observers.Add(new MiningObserver { ObserverId = o.ObserverId, Name = name, LastUpdated = o.LastUpdated });
        }

        var entries = raw.Select(r => new MiningEntry
        {
            ObserverId = r.ObserverId,
            CharacterId = r.Dto.CharacterId,
            CharacterName = names.GetValueOrDefault(r.Dto.CharacterId, r.Dto.CharacterId.ToString()),
            TypeId = r.Dto.TypeId,
            OreType = names.GetValueOrDefault(r.Dto.TypeId, $"Type {r.Dto.TypeId}"),
            Date = r.Dto.LastUpdated,
            Quantity = r.Dto.Quantity,
            CorporationId = r.Dto.RecordedCorporationId,
            CorporationName = names.GetValueOrDefault(r.Dto.RecordedCorporationId,
                r.Dto.RecordedCorporationId.ToString()),
        }).ToList();

        return (observers, entries);
    }

    /// <summary>
    /// Builds the citadel fuel report. The corp structures endpoint is the authoritative list of
    /// owned citadels (athanors, tataras, sotiyos, fortizars, ...) whether fueled or not; corp
    /// assets fill in fuel bay contents and discover control towers, which the structures
    /// endpoint doesn't cover.
    /// </summary>
    public async Task<(List<FuelLocation> Locations, string? StructuresNote)> GetFuelReportAsync(
        long corporationId, string accessToken)
    {
        var structures = new List<CorpStructureDto>();
        string? structuresNote = null;
        try
        {
            structures = await GetPagedAsync<CorpStructureDto>(
                $"corporations/{corporationId}/structures/", accessToken,
                "ESI refused the corporation structures list (403). The logged-in character needs the " +
                "Station Manager or Director role and the esi-corporations.read_structures.v1 scope.");
        }
        catch (Exception ex)
        {
            structuresNote = ex.Message.Split('\n')[0];
        }

        var assets = await GetPagedAsync<CorpAssetDto>($"corporations/{corporationId}/assets/", accessToken,
            "ESI refused the corporation assets request (403). The logged-in character needs the Director " +
            "role, and the app must be authorized with the esi-assets.read_corporation_assets.v1 scope " +
            "(Settings → Forget login, then log in again after adding the scope).");
        var byId = assets.ToDictionary(a => a.ItemId);

        var locations = new Dictionary<long, FuelLocation>();
        var structureIds = new HashSet<long>();
        var rootTypeIds = new Dictionary<long, long>();
        var rootSystemIds = new Dictionary<long, long>();
        foreach (var s in structures)
        {
            structureIds.Add(s.StructureId);
            rootTypeIds[s.StructureId] = s.TypeId;
            locations[s.StructureId] = new FuelLocation
            {
                LocationId = s.StructureId,
                Name = s.Name ?? "",
                FuelExpires = s.FuelExpires,
                State = s.State,
            };
        }

        // Corp assets deployed in space that the structures endpoint doesn't list - control
        // towers, mostly. They only survive the final filter if they turn out to be towers
        // or actually hold fuel. Their location_id is the solar system they sit in.
        foreach (var a in assets.Where(a => a.LocationType == "solar_system"))
        {
            rootTypeIds.TryAdd(a.ItemId, a.TypeId);
            rootSystemIds.TryAdd(a.ItemId, a.LocationId);
            if (!locations.ContainsKey(a.ItemId))
                locations[a.ItemId] = new FuelLocation { LocationId = a.ItemId };
        }

        // Types resolve publicly and flag control towers (needed before the fuel pass, since
        // tower bays don't always carry the StructureFuel flag).
        var typeNames = await ResolveNamesAsync(rootTypeIds.Values.Distinct());
        foreach (var loc in locations.Values)
        {
            loc.TypeName = typeNames.GetValueOrDefault(rootTypeIds.GetValueOrDefault(loc.LocationId), "");
            loc.IsControlTower = loc.TypeName.Contains("Control Tower", StringComparison.OrdinalIgnoreCase);
        }

        // Fuel pass: fuel blocks (any isotope variant) and strontium. Citadel fuel bays carry
        // the StructureFuel flag; for control towers any directly-contained fuel counts.
        // Blocks in corp hangars (possibly nested in containers) count as reserve stock.
        foreach (var a in assets)
        {
            bool isBlock = FuelBlockTypeIds.Contains(a.TypeId);
            bool isStront = a.TypeId == StrontiumTypeId;
            if (!isBlock && !isStront) continue;

            var flags = new List<string> { a.LocationFlag };
            var node = a;
            int hops = 0;
            while (byId.TryGetValue(node.LocationId, out var parent) && hops < 20)
            {
                node = parent;
                flags.Add(node.LocationFlag);
                hops++;
            }
            long rootId = !ReferenceEquals(node, a) && node.LocationType == "solar_system"
                ? node.ItemId
                : node.LocationId;
            if (!locations.TryGetValue(rootId, out var loc)) continue;

            bool inFuelBay = a.LocationFlag == "StructureFuel" ||
                             (loc.IsControlTower && a.LocationId == loc.LocationId);
            if (inFuelBay)
            {
                if (isBlock) loc.FuelBlocks += a.Quantity;
                else loc.Strontium += a.Quantity;
            }
            else if (isBlock &&
                     (flags.Any(f => f.StartsWith("CorpSAG")) || flags.Contains("OfficeFolder")))
            {
                loc.HangarBlocks += a.Quantity;
            }
        }

        // Tower state (unpowered detection) and system come from the starbases endpoint.
        try
        {
            foreach (var sb in await GetPagedAsync<StarbaseDto>(
                         $"corporations/{corporationId}/starbases/", accessToken,
                         "Tower states unavailable: needs the Director role and the " +
                         "esi-corporations.read_starbases.v1 scope."))
            {
                if (!locations.TryGetValue(sb.StarbaseId, out var loc)) continue;
                loc.State = sb.State;
                rootSystemIds[sb.StarbaseId] = sb.SystemId;
            }
        }
        catch (Exception ex)
        {
            string note = ex.Message.Split('\n')[0];
            structuresNote = structuresNote == null ? note : structuresNote + " · " + note;
        }

        // System names for control towers.
        var towerSystemIds = locations.Values
            .Where(l => l.IsControlTower && rootSystemIds.ContainsKey(l.LocationId))
            .Select(l => rootSystemIds[l.LocationId])
            .Distinct();
        var systemNames = await ResolveNamesAsync(towerSystemIds);
        foreach (var loc in locations.Values.Where(l => l.IsControlTower))
            if (rootSystemIds.TryGetValue(loc.LocationId, out long sysId))
                loc.SystemName = systemNames.GetValueOrDefault(sysId, "");
        var unnamedOwned = locations.Values
            .Where(l => string.IsNullOrEmpty(l.Name) && byId.ContainsKey(l.LocationId))
            .Select(l => l.LocationId)
            .ToList();
        foreach (var chunk in unnamedOwned.Chunk(999))
        {
            try
            {
                using var resp = await Http.SendAsync(AuthedPost(
                    $"corporations/{corporationId}/assets/names/", chunk, accessToken));
                if (!resp.IsSuccessStatusCode) break;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    long id = el.GetProperty("item_id").GetInt64();
                    string? n = el.GetProperty("name").GetString();
                    if (!string.IsNullOrEmpty(n) && locations.TryGetValue(id, out var loc))
                        loc.Name = n!;
                }
            }
            catch { break; }
        }

        foreach (var loc in locations.Values)
        {
            if (string.IsNullOrEmpty(loc.Name))
                loc.Name = loc.TypeName.Length > 0
                    ? $"{loc.TypeName} {loc.LocationId}"
                    : $"Structure {loc.LocationId}";
        }

        // Citadels by fuel urgency (unknown expiry last), control towers at the bottom.
        var result = locations.Values
            .Where(l => structureIds.Contains(l.LocationId) || l.IsControlTower ||
                        l.FuelBlocks > 0 || l.Strontium > 0)
            .OrderBy(l => l.IsControlTower)
            .ThenBy(l => l.FuelExpires == null)
            .ThenBy(l => l.FuelExpires ?? DateTime.MaxValue)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (result, structuresNote);
    }

    private static HttpRequestMessage AuthedPost<T>(string path, T body, string accessToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    private async Task<List<T>> GetPagedAsync<T>(string path, string accessToken, string forbiddenMessage)
    {
        var all = new List<T>();
        int page = 1, pages = 1;
        do
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{path}?page={page}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new InvalidOperationException(forbiddenMessage);
            resp.EnsureSuccessStatusCode();
            if (resp.Headers.TryGetValues("X-Pages", out var v) && int.TryParse(v.FirstOrDefault(), out int p))
                pages = p;
            all.AddRange(await resp.Content.ReadFromJsonAsync<List<T>>() ?? new());
            page++;
        } while (page <= pages);
        return all;
    }

    /// <summary>Resolves exact character names to IDs via the public /universe/ids/ endpoint.</summary>
    public async Task<Dictionary<string, long>> ResolveCharacterIdsAsync(IEnumerable<string> names)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in names.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(500))
        {
            using var resp = await Http.PostAsJsonAsync("universe/ids/", chunk);
            if (!resp.IsSuccessStatusCode) continue; // best-effort
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("characters", out var chars)) continue;
            foreach (var el in chars.EnumerateArray())
                result[el.GetProperty("name").GetString() ?? ""] = el.GetProperty("id").GetInt64();
        }
        return result;
    }

    /// <summary>Maps character IDs to their current corporation IDs (public endpoint).</summary>
    public async Task<Dictionary<long, long>> GetAffiliationsAsync(IEnumerable<long> characterIds)
    {
        var result = new Dictionary<long, long>();
        foreach (var chunk in characterIds.Distinct().Chunk(1000))
        {
            using var resp = await Http.PostAsJsonAsync("characters/affiliation/", chunk);
            if (!resp.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var el in doc.RootElement.EnumerateArray())
                result[el.GetProperty("character_id").GetInt64()] = el.GetProperty("corporation_id").GetInt64();
        }
        return result;
    }

    public async Task<Dictionary<long, string>> ResolveNamesAsync(IEnumerable<long> ids)
    {
        var result = new Dictionary<long, string>();
        foreach (var chunk in ids.Distinct().Chunk(1000))
        {
            using var resp = await Http.PostAsJsonAsync("universe/names/", chunk);
            if (!resp.IsSuccessStatusCode) continue; // name resolution is best-effort
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var el in doc.RootElement.EnumerateArray())
                result[el.GetProperty("id").GetInt64()] = el.GetProperty("name").GetString() ?? "";
        }
        return result;
    }

    private async Task<string> GetStringAsync(string path)
    {
        using var resp = await Http.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }
}
