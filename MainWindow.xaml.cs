using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using EveCorporationDashboard.Models;
using EveCorporationDashboard.Services;

namespace EveCorporationDashboard;

public partial class MainWindow : Window
{
    private const double MiningTaxRate = 0.15;

    private readonly AppSettings _settings;
    private readonly AppData _data;
    private readonly EsiAuthService _auth = new();
    private readonly EsiClient _esi = new();

    private List<MemberRow> _rows = new();
    private ICollectionView? _rowsView;

    private string _accessToken = "";
    private DateTime _accessTokenExpiresUtc = DateTime.MinValue;

    // Added/removed record counts from the most recent import of each type (this session).
    private (int Added, int Removed)? _papsDelta;
    private (int Added, int Removed)? _mappingsDelta;

    // Why fuel expiry is missing (role/scope), from the most recent refresh (this session).
    private string? _fuelStructuresNote;

    public MainWindow()
    {
        InitializeComponent();
        _settings = DataStore.LoadSettings();
        _data = DataStore.LoadData();
        ThemeManager.Apply(_settings.DarkMode);
        ThemeButton.Content = _settings.DarkMode ? "☀" : "🌙";
        AppUi.Scale = AppUi.Clamp(_settings.UiScale);
        _initializingScale = true;
        ScaleSlider.Value = AppUi.Scale;
        _initializingScale = false;
        ScaleText.Text = $"{AppUi.Scale:P0}";
        ApplyMainScale();
        ApplyCorpIcon();
        UpdateWindowTitle();
        if (_settings.CorporationId != 0 && string.IsNullOrEmpty(_settings.CorporationTicker))
            _ = FetchCorpTickerAsync();

        ReconcileCorpLabels();

        StatusFilterBox.Items.Add("All");
        foreach (var s in new[] { Statuses.Awol, Statuses.AwayFromGame, Statuses.NoEsiData,
                     Statuses.Afk, Statuses.Inactive, Statuses.NoParticipation,
                     Statuses.LowParticipation, Statuses.Active, Statuses.Unmapped })
            StatusFilterBox.Items.Add(s);
        StatusFilterBox.SelectedIndex = 0;

        RebuildRows();

        // Keep the "last refreshed … ago" label ticking while the app is open.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) => UpdateEsiRefreshLabel();
        timer.Start();

        // Refresh on launch when the data is over a week old (and login is in place).
        Loaded += (_, _) =>
        {
            bool stale = !_data.EsiLastRefreshUtc.HasValue ||
                         (DateTime.UtcNow - _data.EsiLastRefreshUtc.Value).TotalDays >= 7;
            if (stale) TryAutoRefreshEsi();
        };
    }

    private void UpdateEsiRefreshLabel()
    {
        if (!_data.EsiLastRefreshUtc.HasValue)
        {
            EsiLastRefreshText.Text = "Never refreshed";
            return;
        }
        var span = DateTime.UtcNow - _data.EsiLastRefreshUtc.Value;
        if (span.TotalHours >= 24)
        {
            EsiLastRefreshText.Text = $"Last refreshed {_data.EsiLastRefreshUtc.Value.ToLocalTime():MM/dd}";
            return;
        }
        if (span.TotalMinutes < 1)
        {
            EsiLastRefreshText.Text = "Last refreshed just now";
            return;
        }
        int hours = (int)span.TotalHours;
        int minutes = span.Minutes;
        string h = hours == 1 ? "1 hour" : $"{hours} hours";
        string m = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        EsiLastRefreshText.Text = hours > 0
            ? $"Last refreshed {h} {m} ago"
            : $"Last refreshed {m} ago";
    }

    // ---------- ESI login / refresh ----------

    /// <summary>Runs the full SSO login; invoked from the Settings window's Corp Director ESI section.</summary>
    private async Task<(bool Ok, string Message)> LoginFromSettingsAsync()
    {
        try
        {
            SetBusy(true, "Waiting for EVE SSO login in your browser…");
            var result = await _auth.LoginAsync(EsiConfig.ClientId, EsiConfig.RedirectUris, EsiConfig.Scopes,
                new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token);
            ApplyAuthResult(result);
            _settings.CorporationId = await _esi.GetCorporationIdAsync(result.CharacterId);
            DataStore.SaveSettings(_settings);
            await FetchCorpTickerAsync();
            ApplyCorpIcon();
            SetBusy(false, $"Logged in as {result.CharacterName} (corp ID {_settings.CorporationId}).");
            return (true, $"Logged in as {result.CharacterName} (corp ID {_settings.CorporationId}).");
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "Login timed out.");
            return (false, "Login timed out - no callback received within 5 minutes.");
        }
        catch (Exception ex)
        {
            SetBusy(false, "Login failed.");
            return (false, ex.Message.Split('\n')[0]);
        }
    }

    private async void RefreshEsi_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn()) return;
        await RefreshEsiAsync();
    }

    /// <summary>Fires an ESI refresh without user interaction, when login is already in place.</summary>
    private void TryAutoRefreshEsi()
    {
        if (_refreshing) return;
        if (string.IsNullOrEmpty(_settings.RefreshToken)) return;
        _ = RefreshEsiAsync();
    }

    private bool _refreshing;

    private async Task RefreshEsiAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            SetBusy(true, "Refreshing member data from ESI…");
            string token = await EnsureAccessTokenAsync();

            if (_settings.CorporationId == 0)
            {
                _settings.CorporationId = await _esi.GetCorporationIdAsync(_settings.CharacterId);
                DataStore.SaveSettings(_settings);
                await FetchCorpTickerAsync();
                ApplyCorpIcon();
            }

            var oldIds = _data.EsiMembers.Select(m => m.CharacterId).ToHashSet();
            var members = await _esi.GetMemberTrackingAsync(_settings.CorporationId, token);
            var newIds = members.Select(m => m.CharacterId).ToHashSet();
            int added = newIds.Count(id => !oldIds.Contains(id));
            int removed = oldIds.Count(id => !newIds.Contains(id));

            string mainCorpName = (await _esi.ResolveNamesAsync(new[] { _settings.CorporationId }))
                .GetValueOrDefault(_settings.CorporationId, "");
            foreach (var m in members) m.CorporationName = mainCorpName;

            _data.EsiMembers = members;
            _data.EsiLastRefreshUtc = DateTime.UtcNow;

            // Mapped characters outside the main corp (alt corps): no tracking data is available
            // for them, but public ESI gives us their IDs and current corporation.
            try
            {
                SetBusy(true, "Resolving alt corp characters…");
                var known = members.Select(m => m.CharacterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var extraNames = _data.CharacterMappings.Select(m => m.CharacterName)
                    .Where(n => !known.Contains(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var externals = new List<EsiMemberRecord>();
                if (extraNames.Count > 0)
                {
                    var ids = await _esi.ResolveCharacterIdsAsync(extraNames);
                    var affiliations = await _esi.GetAffiliationsAsync(ids.Values);
                    var corpNames = await _esi.ResolveNamesAsync(affiliations.Values.Distinct());
                    foreach (var (name, id) in ids)
                        externals.Add(new EsiMemberRecord
                        {
                            CharacterId = id,
                            CharacterName = name,
                            CorporationName = affiliations.TryGetValue(id, out long corpId)
                                ? corpNames.GetValueOrDefault(corpId, "")
                                : "",
                        });
                }
                _data.ExternalCharacters = externals;
            }
            catch { /* keep the previous external list if public resolution hiccups */ }

            // The mining ledger needs an extra scope/role - its failure shouldn't sink the whole refresh.
            string miningNote;
            try
            {
                SetBusy(true, "Refreshing corporation mining ledger…");
                var (observers, entries) = await _esi.GetMiningLedgerAsync(_settings.CorporationId, token);
                _data.MiningObservers = observers;
                _data.MiningEntries = entries;
                _data.MiningLastRefreshUtc = DateTime.UtcNow;
                miningNote = $"mining: {observers.Count} moons, {entries.Count:N0} ledger entries";
            }
            catch (Exception mex)
            {
                miningNote = "mining ledger unavailable - " + mex.Message.Split('\n')[0];
            }

            // Citadel fuel likewise degrades on its own rather than sinking the refresh.
            string fuelNote;
            try
            {
                SetBusy(true, "Refreshing citadel fuel from corp assets…");
                var (fuelLocations, structuresNote) = await _esi.GetFuelReportAsync(_settings.CorporationId, token);
                _data.FuelLocations = fuelLocations;
                _data.FuelLastRefreshUtc = DateTime.UtcNow;
                _fuelStructuresNote = structuresNote;
                fuelNote = $"fuel: {fuelLocations.Count} structures";
            }
            catch (Exception fex)
            {
                fuelNote = "fuel report unavailable - " + fex.Message.Split('\n')[0];
            }

            DataStore.SaveData(_data);
            RebuildRows();
            SetBusy(false, $"ESI refresh complete - {members.Count} characters (+{added} / −{removed}); {miningNote}; {fuelNote}");
        }
        catch (Exception ex)
        {
            SetBusy(false, "ESI refresh failed.");
            MessageBox.Show(this, ex.Message, "ESI refresh failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task<string> EnsureAccessTokenAsync()
    {
        if (_accessToken.Length > 0 && DateTime.UtcNow < _accessTokenExpiresUtc)
            return _accessToken;

        if (!string.IsNullOrEmpty(_settings.RefreshToken))
        {
            try
            {
                var refreshed = await _auth.RefreshAsync(EsiConfig.ClientId, _settings.RefreshToken);
                ApplyAuthResult(refreshed);
                DataStore.SaveSettings(_settings);
                return _accessToken;
            }
            catch
            {
                // Refresh token expired or revoked - fall through to a fresh login.
            }
        }

        var result = await _auth.LoginAsync(EsiConfig.ClientId, EsiConfig.RedirectUris, EsiConfig.Scopes,
            new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token);
        ApplyAuthResult(result);
        DataStore.SaveSettings(_settings);
        return _accessToken;
    }

    private void ApplyAuthResult(AuthResult result)
    {
        _accessToken = result.AccessToken;
        _accessTokenExpiresUtc = result.ExpiresAtUtc;
        if (!string.IsNullOrEmpty(result.RefreshToken)) _settings.RefreshToken = result.RefreshToken;
        if (result.CharacterId != 0) _settings.CharacterId = result.CharacterId;
        if (!string.IsNullOrEmpty(result.CharacterName)) _settings.CharacterName = result.CharacterName;
    }

    private bool EnsureLoggedIn()
    {
        if (!string.IsNullOrEmpty(_settings.RefreshToken)) return true;
        MessageBox.Show(this,
            "Not logged in yet.\n\nOpen Settings and click \"Log in with EVE\" using a character " +
            "with the Director role.",
            "Login needed", MessageBoxButton.OK, MessageBoxImage.Information);
        OpenSettings();
        return !string.IsNullOrEmpty(_settings.RefreshToken);
    }

    // ---------- Clipboard imports ----------


    /// <summary>The pilot map should be re-imported at least this often.</summary>
    private const int PilotMapStaleDays = 90;

    /// <summary>
    /// Member corps first, then alts in the order they were added. OrderByDescending is a
    /// stable sort, so positions never shift - even when a label is renamed (CEO alt
    /// corrected to the owner's forum name) after an import.
    /// </summary>
    private List<(string Label, string Url, string? Ticker)> OrderedPilotMapSources() =>
        _settings.PilotMapCorps
            .OrderByDescending(c => c.IsMemberCorp)
            .Select(c => (c.Label, c.Url, c.Ticker))
            .ToList();

    private static long? CorpIdFromUrl(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"corporation/view/(\d+)");
        return m.Success ? long.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// Inline corp sync: diffs the copied manager corp list page against the configured
    /// corps, auto-adding new ones (ticker and owner resolved via ESI, CEO alts mapped to
    /// forum names via the pilot map) and dropping corps no longer on the page.
    /// </summary>
    private async Task<(bool Ok, string Message)> SyncCorpsFromClipboardAsync()
    {
        string? html = null;
        try
        {
            if (Clipboard.ContainsText(TextDataFormat.Html)) html = Clipboard.GetText(TextDataFormat.Html);
        }
        catch { /* clipboard busy */ }

        var links = TableParser.ExtractCorpLinks(html);
        if (links.Count == 0)
            return (false, "No corp links found on the clipboard - open the corp list page, " +
                           "press Ctrl+A then Ctrl+C, and click 'Corps' again.");

        var pageIds = links.Select(l => l.CorpId).ToHashSet();
        var existingIds = _settings.PilotMapCorps
            .Select(c => CorpIdFromUrl(c.Url))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        int added = 0;
        foreach (var (id, pageName, isMember) in links
                     .Where(l => !existingIds.Contains(l.CorpId))
                     .OrderByDescending(l => l.IsMemberCorp)
                     .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            string label = pageName, corpName = pageName;
            string? ticker = null;
            try
            {
                var (name, tick, ceoId) = await _esi.GetCorporationInfoAsync(id);
                if (name.Length > 0) corpName = name;
                ticker = "[" + tick + "]";
                if (ceoId != 0)
                {
                    string? ceoName = (await _esi.ResolveNamesAsync(new[] { ceoId })).GetValueOrDefault(ceoId);
                    if (ceoName != null) label = LookupForumName(ceoName) ?? ceoName;
                }
            }
            catch { /* ESI hiccup: fall back to the page's link text */ }

            _settings.PilotMapCorps.Add(new CorpSource
            {
                Label = label,
                Url = $"https://manager.goonfleet.com/corporation/view/{id}#tab-chars",
                Ticker = ticker,
                CorpName = corpName,
                IsMemberCorp = isMember,
            });
            added++;
        }

        var removedLabels = _settings.PilotMapCorps
            .Where(c => CorpIdFromUrl(c.Url) is long corpId && !pageIds.Contains(corpId))
            .Select(c => c.Label)
            .ToList();
        foreach (var label in removedLabels)
        {
            _settings.PilotMapCorps.RemoveAll(c =>
                string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase));
            _data.PilotMapImportDates.Remove(label);
        }

        // The page tells us which corps are member corps; keep existing entries in sync.
        foreach (var (id, _, isMember) in links)
        {
            var corp = _settings.PilotMapCorps.FirstOrDefault(c => CorpIdFromUrl(c.Url) == id);
            if (corp != null) corp.IsMemberCorp = isMember;
        }

        DataStore.SaveSettings(_settings);
        DataStore.SaveData(_data);
        return (true, $"Corp list synced: {added} added, {removedLabels.Count} removed.");
    }

    private void ImportPaps_Click(object sender, RoutedEventArgs e)
    {
        var fields = new List<ImportField>
        {
            new("ForumName", "Forum name", true, "forum name", "username", "user name", "name", "user", "member"),
            new("Paps90", "Paps (90d)", true, "90"),
            new("Paps60", "Paps (60d)", true, "60"),
            new("Paps30", "Paps (30d)", true, "30"),
            new("LastForum", "Last on forums", true, "forum"),
            new("LastJabber", "Last on jabber", true, "jabber"),
            new("LastMumble", "Last on mumble", true, "mumble"),
        };
        if (string.IsNullOrEmpty(_settings.PapsGroupUrl))
        {
            MessageBox.Show(this,
                "No paps auth group is set yet. In Settings, click 'Auth Groups', copy that page " +
                "(Ctrl+A, Ctrl+C), then click 'Import group from clipboard'.",
                "Paps group needed", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenSettings();
            if (string.IsNullOrEmpty(_settings.PapsGroupUrl)) return;
        }

        var sources = new List<(string Label, string Url, string? Ticker)>
        {
            (_settings.PapsGroupName ?? "Paps group", _settings.PapsGroupUrl!, null),
        };
        var importedBefore = _data.PapsLastImportUtc;
        var dlg = new ImportWindow("Paps", fields, sources,
            applyCallback: ApplyPapsImport) { Owner = this };
        dlg.ShowDialog();

        // Once both paps and the pilot map exist, imported data flows straight into ESI numbers.
        if (_data.PapsLastImportUtc != importedBefore &&
            _data.ForumAccounts.Count > 0 && _data.CharacterMappings.Count > 0)
            TryAutoRefreshEsi();
    }

    /// <summary>Replaces the paps data with one freshly copied auth group page.</summary>
    private string ApplyPapsImport(ImportResult result, string? _)
    {
        var oldNames = _data.ForumAccounts.Select(a => a.ForumName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _data.ForumAccounts.Clear();
        var utcNow = DateTime.UtcNow;
        int imported = 0;
        foreach (var row in result.DataRows)
        {
            string? forumName = Cell(row, result, "ForumName");
            if (string.IsNullOrWhiteSpace(forumName)) continue;

            var account = _data.ForumAccounts.FirstOrDefault(a =>
                string.Equals(a.ForumName, forumName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (account == null)
            {
                account = new ForumAccount { ForumName = forumName.Trim() };
                _data.ForumAccounts.Add(account);
            }

            account.Paps30 = TableParser.ParseNumber(Cell(row, result, "Paps30")) ?? account.Paps30;
            account.Paps60 = TableParser.ParseNumber(Cell(row, result, "Paps60")) ?? account.Paps60;
            account.Paps90 = TableParser.ParseNumber(Cell(row, result, "Paps90")) ?? account.Paps90;

            string? rawForum = Cell(row, result, "LastForum");
            string? rawJabber = Cell(row, result, "LastJabber");
            string? rawMumble = Cell(row, result, "LastMumble");
            if (rawForum != null) { account.LastForumRaw = rawForum; account.LastForum = TableParser.ParseFuzzyDate(rawForum, utcNow); }
            if (rawJabber != null) { account.LastJabberRaw = rawJabber; account.LastJabber = TableParser.ParseFuzzyDate(rawJabber, utcNow); }
            if (rawMumble != null) { account.LastMumbleRaw = rawMumble; account.LastMumble = TableParser.ParseFuzzyDate(rawMumble, utcNow); }
            imported++;
        }

        var newNames = _data.ForumAccounts.Select(a => a.ForumName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _papsDelta = (newNames.Count(n => !oldNames.Contains(n)), oldNames.Count(n => !newNames.Contains(n)));

        _data.PapsLastImportUtc = utcNow;
        DataStore.SaveData(_data);
        RebuildRows();
        StatusText.Text = $"Imported {imported} paps/forum rows ({_data.ForumAccounts.Count} accounts total).";
        return $"Imported {imported} rows ({_data.ForumAccounts.Count} accounts total).";
    }

    private void ImportMappings_Click(object sender, RoutedEventArgs e)
    {
        var fields = new List<ImportField>
        {
            new("CharacterName", "Name", true, "name", "character", "char", "pilot"),
            new("ForumName", "Forum name", true, "forum", "main", "account", "user"),
            new("LastOnline", "Last online", false, "online", "last seen", "seen", "active", "login"),
            new("Joined", "Joined corp", false, "joined", "join", "member since", "start"),
            new("Ship", "Current ship", false, "ship"),
        };
        var dlg = new ImportWindow("Pilot Map", fields, OrderedPilotMapSources(),
            applyCallback: ApplyMappingsImport,
            lastImportLookup: label =>
                _data.PilotMapImportDates.TryGetValue(label, out var date) ? date : null,
            syncCorps: SyncCorpsFromClipboardAsync,
            sourcesProvider: OrderedPilotMapSources,
            labelConfirmed: label => _data.CharacterMappings.Any(m =>
                string.Equals(m.ForumName, label, StringComparison.OrdinalIgnoreCase)),
            instructions: "Click a corp above, press Ctrl+A then Ctrl+C on its page, then " +
                          "'Import from clipboard'. Repeat for each corp.",
            modeNote: "Each import merges into the existing mappings - nothing is deleted " +
                      "unless you delete all data in Settings.")
        { Owner = this };
        var importedBefore = _data.MappingsLastImportUtc;
        dlg.ShowDialog();

        // One refresh after the whole session, not one per corp imported.
        if (_data.MappingsLastImportUtc != importedBefore &&
            _data.ForumAccounts.Count > 0 && _data.CharacterMappings.Count > 0)
            TryAutoRefreshEsi();
    }

    /// <summary>Resolves a character name to its forum name via the pilot map, if mapped.</summary>
    private string? LookupForumName(string characterName) =>
        _data.CharacterMappings.FirstOrDefault(m =>
            string.Equals(m.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))?.ForumName;

    /// <summary>
    /// Corp source labels must be forum names. A label that turns out to be a mapped
    /// character (e.g. an alt corp's CEO alt) is corrected to that character's forum
    /// name, migrating its per-corp import date along with it.
    /// </summary>
    private bool ReconcileCorpLabels()
    {
        bool changed = false;
        foreach (var corp in _settings.PilotMapCorps)
        {
            string? forumName = LookupForumName(corp.Label);
            if (forumName == null ||
                string.Equals(forumName, corp.Label, StringComparison.OrdinalIgnoreCase)) continue;

            if (_data.PilotMapImportDates.Remove(corp.Label, out var importedAt))
                _data.PilotMapImportDates[forumName] = importedAt;
            corp.Label = forumName;
            changed = true;
        }
        if (changed)
        {
            DataStore.SaveSettings(_settings);
            DataStore.SaveData(_data);
        }
        return changed;
    }

    /// <summary>Merges one corp's page into the mappings (upsert by character name); never deletes.</summary>
    private string ApplyMappingsImport(ImportResult result, string? sourceLabel)
    {
        var utcNow = DateTime.UtcNow;
        int added = 0, updated = 0;
        foreach (var row in result.DataRows)
        {
            string? charName = Cell(row, result, "CharacterName")?.Trim();
            string? forumName = Cell(row, result, "ForumName")?.Trim();
            if (string.IsNullOrWhiteSpace(charName) || string.IsNullOrWhiteSpace(forumName)) continue;

            var existing = _data.CharacterMappings.FirstOrDefault(m =>
                string.Equals(m.CharacterName, charName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new CharacterMapping { CharacterName = charName };
                _data.CharacterMappings.Add(existing);
                added++;
            }
            else
            {
                updated++;
            }
            existing.ForumName = forumName;

            // Optional extras - only touched when the column was mapped in the import window.
            string? lastOnlineRaw = Cell(row, result, "LastOnline");
            if (lastOnlineRaw != null)
                existing.LastOnline = TableParser.ParseFuzzyDate(lastOnlineRaw, utcNow);
            string? joinedRaw = Cell(row, result, "Joined");
            if (joinedRaw != null)
                existing.JoinedDate = TableParser.ParseFuzzyDate(joinedRaw, utcNow);
            string? ship = Cell(row, result, "Ship")?.Trim();
            if (!string.IsNullOrWhiteSpace(ship))
                existing.ShipType = ship;
        }

        _mappingsDelta = (added, 0);
        _data.MappingsLastImportUtc = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(sourceLabel))
            _data.PilotMapImportDates[sourceLabel] = DateTime.UtcNow;
        DataStore.SaveData(_data);
        // The freshly imported mappings may reveal that a corp label was really a CEO alt.
        ReconcileCorpLabels();
        RebuildRows();

        string source = string.IsNullOrEmpty(sourceLabel) ? "clipboard" : sourceLabel;
        StatusText.Text = $"Imported {added + updated} characters from {source} ({_data.CharacterMappings.Count} total).";
        return $"Imported {added + updated} characters from {source} ({added} new, {updated} updated).";
    }

    private static string? Cell(string[] row, ImportResult result, string key)
    {
        if (!result.Mapping.TryGetValue(key, out int index) || index >= row.Length) return null;
        return row[index];
    }

    // ---------- Data management ----------

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// Window/taskbar icon: prefers the team icon captured from the auth group page,
    /// falling back to the corp logo on EVE's image server.
    /// </summary>
    private async void ApplyCorpIcon()
    {
        await CorpIcon.LoadAsync(_settings);
        CorpIcon.Apply(this);
    }

    private void UpdateWindowTitle()
    {
        Title = string.IsNullOrEmpty(_settings.CorporationTicker)
            ? "Eve Corporation Dashboard"
            : $"[{_settings.CorporationTicker}] - Corporation Dashboard";
    }

    private async Task FetchCorpTickerAsync()
    {
        try
        {
            _settings.CorporationTicker = (await _esi.GetCorporationInfoAsync(_settings.CorporationId)).Ticker;
            DataStore.SaveSettings(_settings);
            UpdateWindowTitle();
        }
        catch { /* offline: title falls back to the generic name until next launch */ }
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.DarkMode = !_settings.DarkMode;
        ThemeManager.Apply(_settings.DarkMode);
        DataStore.SaveSettings(_settings);
        ThemeButton.Content = _settings.DarkMode ? "☀" : "🌙";
    }

    private bool _initializingScale;

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires during XAML parsing before sibling controls exist; bail until ready.
        if (ScaleText == null || MainTabs == null) return;
        ScaleText.Text = $"{e.NewValue:P0}";
        if (_initializingScale) return;
        _settings.UiScale = Math.Round(e.NewValue, 2);
        AppUi.Scale = _settings.UiScale;
        DataStore.SaveSettings(_settings);
        ApplyMainScale();
    }

    private const double BaseWindowWidth = 1010;
    private const double BaseWindowHeight = 900;

    /// <summary>
    /// Scales only the tab content, leaving the toolbar (and the slider itself) fixed so
    /// the controls never shift under the cursor while dragging. The window grows with the
    /// scale (capped to the screen's working area) instead of relying on scrollbars.
    /// </summary>
    private void ApplyMainScale()
    {
        MainTabs.LayoutTransform = Math.Abs(AppUi.Scale - 1.0) < 0.01
            ? null
            : new ScaleTransform(AppUi.Scale, AppUi.Scale);

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(BaseWindowWidth * AppUi.Scale, workArea.Width);
        Height = Math.Min(BaseWindowHeight * AppUi.Scale, workArea.Height);
    }


    private void OpenSettings()
    {
        var dlg = new SettingsWindow(_settings, LoginFromSettingsAsync, deleteImportedData: () =>
        {
            // Clean slate: every piece of collected data goes, including the corp source
            // list. Only authentication (client ID, callback URL, tokens, logged-in
            // character) survives.
            _settings.PilotMapCorps.Clear();
            _settings.PapsGroupUrl = null;
            _settings.PapsGroupName = null;
            _settings.CorpIconUrl = null;
            DataStore.SaveSettings(_settings);
            CorpIcon.Reset();
            _data.ForumAccounts.Clear();
            _data.CharacterMappings.Clear();
            _data.ExternalCharacters.Clear();
            _data.EsiMembers.Clear();
            _data.MiningObservers.Clear();
            _data.MiningEntries.Clear();
            _data.FuelLocations.Clear();
            _data.FuelLastRefreshUtc = null;
            _fuelStructuresNote = null;
            _data.PilotMapImportDates.Clear();
            _data.EsiLastRefreshUtc = null;
            _data.MiningLastRefreshUtc = null;
            _data.PapsLastImportUtc = null;
            _data.MappingsLastImportUtc = null;
            _papsDelta = null;
            _mappingsDelta = null;
            DataStore.SaveData(_data);
            RebuildRows();
        })
        { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            DataStore.SaveSettings(_settings);
            RebuildRows();
        }
        // The auth group import inside Settings may have captured a new icon.
        ApplyCorpIcon();
        UpdateWindowTitle();
    }

    // ---------- Rows / filtering ----------

    private void RebuildRows()
    {
        _rows = RowBuilder.Build(_data, _settings, DateTime.UtcNow);
        _rowsView = CollectionViewSource.GetDefaultView(_rows);
        _rowsView.Filter = FilterRow;
        OverviewGrid.ItemsSource = _rowsView;

        RefreshMiningTab();
        RefreshFuelTab();

        UpdateStatusBar();
    }

    // ---------- Citadel fuel tab ----------

    public class FuelCitadelView
    {
        public string Name { get; set; } = "";
        public string ExpiryText { get; set; } = "";
        public Brush ExpiryBrush { get; set; } = Brushes.Gray;
        public string FuelLine { get; set; } = "";
    }

    private void RefreshFuelTab()
    {
        var views = new List<FuelCitadelView>();
        foreach (var loc in _data.FuelLocations)
        {
            var view = new FuelCitadelView { Name = loc.Name };

            if (loc.IsControlTower)
            {
                // Towers have no ESI fuel expiry; estimate from fuel bay blocks and burn rate.
                if (loc.State == "offline")
                {
                    view.ExpiryText = "UNPOWERED";
                    view.ExpiryBrush = Brushes.Firebrick;
                }
                else if (loc.FuelBlocks > 0)
                {
                    int blocksPerHour = loc.TypeName.Contains("Small", StringComparison.OrdinalIgnoreCase) ? 10
                        : loc.TypeName.Contains("Medium", StringComparison.OrdinalIgnoreCase) ? 20
                        : 40;
                    double days = loc.FuelBlocks / (double)blocksPerHour / 24.0;
                    view.ExpiryText = $"{days:0.0} days of fuel remaining (est)";
                    view.ExpiryBrush = days < 3 ? Brushes.Firebrick
                        : days < 7 ? Brushes.Chocolate
                        : days < 14 ? Brushes.DarkGoldenrod
                        : Brushes.Green;
                }
                else
                {
                    view.ExpiryText = "fuel bay empty";
                    view.ExpiryBrush = Brushes.Firebrick;
                }
            }
            else if (loc.FuelExpires.HasValue)
            {
                double days = (loc.FuelExpires.Value - DateTime.UtcNow).TotalDays;
                view.ExpiryText =
                    $"{Math.Max(days, 0):0.0} days of fuel remaining ({loc.FuelExpires.Value:yyyy-MM-dd HH:mm}Z)";
                view.ExpiryBrush = days < 3 ? Brushes.Firebrick
                    : days < 7 ? Brushes.Chocolate
                    : days < 14 ? Brushes.DarkGoldenrod
                    : Brushes.Green;
            }
            else
            {
                view.ExpiryText = "no fuel expiry data";
            }

            var lineParts = new List<string>();
            if (!string.IsNullOrEmpty(loc.TypeName)) lineParts.Add(loc.TypeName);
            if (!string.IsNullOrEmpty(loc.SystemName)) lineParts.Add("system: " + loc.SystemName);
            lineParts.Add(loc.FuelBlocks > 0 ? $"Fuel bay: {loc.FuelBlocks:N0} fuel blocks" : "Fuel bay: empty");
            if (loc.HangarBlocks > 0) lineParts.Add($"Corp hangar: {loc.HangarBlocks:N0} fuel blocks");
            if (loc.Strontium > 0) lineParts.Add($"{loc.Strontium:N0} strontium");
            // shield_vulnerable is the normal state for a fueled citadel; anything else is news.
            // For towers online/offline is already the headline; other states are still worth a note.
            if (!string.IsNullOrEmpty(loc.State) && loc.State != "shield_vulnerable" &&
                loc.State != "online" && loc.State != "offline")
                lineParts.Add("state: " + loc.State!.Replace('_', ' '));
            view.FuelLine = string.Join("  ·  ", lineParts);

            views.Add(view);
        }

        FuelList.ItemsSource = views;

        if (_data.FuelLocations.Count == 0)
        {
            FuelInfoText.Text = "No fuel data yet - run 'Refresh ESI data' " +
                "(needs the Director role and the esi-assets.read_corporation_assets.v1 scope).";
            return;
        }
        int towers = _data.FuelLocations.Count(l => l.IsControlTower);
        FuelInfoText.Text = $"{_data.FuelLocations.Count - towers} citadels" +
            (towers > 0 ? $" + {towers} control towers" : "") +
            (_data.FuelLastRefreshUtc.HasValue ? $" · fetched {_data.FuelLastRefreshUtc:yyyy-MM-dd HH:mm}Z" : "") +
            (_fuelStructuresNote != null ? $" · ⚠ {_fuelStructuresNote}" : "");
    }

    // ---------- Mining ledger tab ----------

    /// <summary>One moon-mining extraction window, derived by clustering ledger entry dates.</summary>
    public class FrackPeriod
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public int Entries { get; set; }
        public string Display => $"{Start:yyyy-MM-dd} → {End:yyyy-MM-dd}  ({Entries} entries)";
    }

    /// <summary>
    /// Clusters a moon's ledger dates into fracks: a frack lasts at most 72 hours, so its
    /// entries fall on consecutive-ish days; a gap of more than 2 days starts a new frack.
    /// </summary>
    private static List<FrackPeriod> ComputeFracks(List<MiningEntry> entries)
    {
        var fracks = new List<FrackPeriod>();
        FrackPeriod? current = null;
        DateTime previous = default;
        foreach (var date in entries.Select(e => e.Date.Date).Distinct().OrderBy(d => d))
        {
            if (current == null || (date - previous).TotalDays > 2 || (date - current.Start).TotalDays > 3)
            {
                current = new FrackPeriod { Start = date, End = date };
                fracks.Add(current);
            }
            else
            {
                current.End = date;
            }
            previous = date;
        }
        foreach (var f in fracks)
            f.Entries = entries.Count(e => e.Date.Date >= f.Start && e.Date.Date <= f.End);
        return fracks;
    }

    public class MiningRow
    {
        public string Account { get; set; } = "";
        public bool IsUnmapped { get; set; }
        public string Corp { get; set; } = "";
        public string Ore { get; set; } = "";
        public long Quantity { get; set; }
        public long Tax { get; set; }
    }

    /// <summary>One frack at one citadel - the unit the Mining Ledger tab displays.</summary>
    public class FrackView
    {
        public MiningObserver Obs { get; set; } = null!;
        public FrackPeriod Frack { get; set; } = null!;
        public string Display => $"{Frack.Start:yyyy-MM-dd} → {Frack.End:yyyy-MM-dd}   {Obs.Name}";
    }

    private bool _updatingFrackCombo;

    private void FrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFrackCombo) RefreshMiningView();
    }

    /// <summary>Rebuilds the combined frack+moon dropdown (newest frack first), then renders.</summary>
    private void RefreshMiningTab()
    {
        _updatingFrackCombo = true;
        var previous = FrackCombo.SelectedItem as FrackView;

        var views = new List<FrackView>();
        foreach (var obs in _data.MiningObservers)
            foreach (var frack in ComputeFracks(
                         _data.MiningEntries.Where(e => e.ObserverId == obs.ObserverId).ToList()))
                views.Add(new FrackView { Obs = obs, Frack = frack });
        views = views.OrderByDescending(v => v.Frack.End).ThenBy(v => v.Obs.Name).ToList();

        FrackCombo.ItemsSource = views;
        FrackCombo.SelectedItem = views.FirstOrDefault(v =>
                v.Obs.ObserverId == previous?.Obs.ObserverId && v.Frack.Start == previous.Frack.Start)
            ?? views.FirstOrDefault();
        _updatingFrackCombo = false;
        RefreshMiningView();
    }

    private void RefreshMiningView()
    {
        ChartPanel.Children.Clear();
        LegendPanel.Children.Clear();
        PieCanvas.Children.Clear();
        PieLegendPanel.Children.Clear();
        AccountPieCanvas.Children.Clear();
        AccountPieLegendPanel.Children.Clear();
        MiningGrid.ItemsSource = null;

        if (FrackCombo.SelectedItem is not FrackView view)
        {
            MiningInfoText.Text = _data.MiningObservers.Count == 0
                ? "No mining data yet - run 'Refresh ESI data' (needs the Accountant or Director role and the mining scope)."
                : "No fracks recorded yet.";
            return;
        }
        var obs = view.Obs;
        var frack = view.Frack;

        var mapByChar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _data.CharacterMappings) mapByChar[m.CharacterName] = m.ForumName;

        var moonEntries = _data.MiningEntries
            .Where(e => e.ObserverId == obs.ObserverId &&
                        e.Date.Date >= frack.Start && e.Date.Date <= frack.End)
            .ToList();
        var rows = moonEntries
            .GroupBy(e => (
                Mapped: mapByChar.ContainsKey(e.CharacterName),
                Account: mapByChar.GetValueOrDefault(e.CharacterName, e.CharacterName),
                Corp: e.CorporationName,
                e.OreType))
            .Select(g => new MiningRow
            {
                Account = g.Key.Mapped ? g.Key.Account : $"UNMAPPED - {g.Key.Account}",
                IsUnmapped = !g.Key.Mapped,
                Corp = g.Key.Corp,
                Ore = g.Key.OreType,
                Quantity = g.Sum(e => e.Quantity),
                Tax = (long)Math.Round(g.Sum(e => e.Quantity) * MiningTaxRate),
            })
            .OrderBy(r => r.Account, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Ore)
            .ToList();

        MiningGrid.ItemsSource = rows;
        MiningInfoText.Text =
            $"Frack {frack.Start:MM/dd}–{frack.End:MM/dd}: {moonEntries.Count:N0} entries" +
            (_data.MiningLastRefreshUtc.HasValue ? $" · fetched {_data.MiningLastRefreshUtc:yyyy-MM-dd HH:mm}Z" : "");
        BuildMiningChart(rows);
    }

    /// <summary>Copies the selected moon's table plus a per-account tax summary to the clipboard.</summary>
    private void ExportMining_Click(object sender, RoutedEventArgs e)
    {
        if (FrackCombo.SelectedItem is not FrackView view ||
            MiningGrid.ItemsSource is not List<MiningRow> rows || rows.Count == 0)
        {
            MessageBox.Show(this, "Nothing to export - select a frack with ledger data first.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var obs = view.Obs;
        var frack = view.Frack;

        // Wrapped in a code fence so it pastes into Discord as a monospace block.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```");
        sb.AppendLine(obs.Name);
        sb.AppendLine($"Frack {frack.Start:yyyy-MM-dd} → {frack.End:yyyy-MM-dd}");
        foreach (var account in rows.GroupBy(r => r.Account)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine($"Total owed for {account.Key}:");
            // Tax is in ore units, so it stays itemized per ore type rather than summed.
            foreach (var ore in account.GroupBy(r => r.Ore).OrderBy(o => o.Key))
                sb.AppendLine($"\t{ore.Key} - {ore.Sum(r => r.Tax):N0}");
        }
        sb.AppendLine("```");

        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText.Text = $"Mining ledger for {obs.Name} copied to clipboard " +
                              $"({rows.Count} rows + tax summary).";
        }
        catch
        {
            MessageBox.Show(this, "Couldn't write to the clipboard (another app may be holding it) - try again.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static readonly string[] OrePaletteHex =
    {
        "#4E79A7", "#F28E2B", "#E15759", "#76B7B2", "#59A14F", "#EDC948",
        "#B07AA1", "#FF9DA7", "#9C755F", "#86BCB6", "#D37295", "#BAB0AC",
    };

    // Deliberately disjoint from the ore palette so the two pies never share a color.
    private static readonly string[] AccountPaletteHex =
    {
        "#1F2A6B", "#8C1B4F", "#146B3A", "#7B52D1", "#0E7C86",
        "#7A5901", "#C71585", "#3AA6DD", "#5C4033", "#37474F",
    };

    private void BuildMiningChart(List<MiningRow> rows)
    {
        if (rows.Count == 0) return;

        var oreTypes = rows.Select(r => r.Ore).Distinct().OrderBy(o => o).ToList();
        var brushes = new Dictionary<string, Brush>();
        for (int i = 0; i < oreTypes.Count; i++)
            brushes[oreTypes[i]] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(OrePaletteHex[i % OrePaletteHex.Length]));

        foreach (var ore in oreTypes)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            item.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 12, Height = 12, Fill = brushes[ore], Margin = new Thickness(0, 0, 4, 0),
            });
            item.Children.Add(new TextBlock { Text = ore, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            LegendPanel.Children.Add(item);
        }

        // The table splits an account's ore per corp, so the same ore can appear on several
        // rows for one account (alts in different corps, or a corp change mid-frack).
        // Fold those together - the stacked bar wants one segment per ore.
        var accounts = rows.GroupBy(r => r.Account)
            .Select(g => (Account: g.Key, Total: g.Sum(r => r.Quantity),
                          ByOre: g.GroupBy(r => r.Ore)
                                  .ToDictionary(o => o.Key, o => o.Sum(r => r.Quantity))))
            .OrderByDescending(a => a.Total).ToList();
        double max = accounts[0].Total;
        if (max <= 0) return;

        const double barAreaHeight = 305;
        foreach (var acct in accounts)
        {
            var column = new StackPanel { Width = 68, Margin = new Thickness(2, 0, 2, 0) };
            var bar = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Width = 44,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            bar.Children.Add(new TextBlock
            {
                Text = FormatQty(acct.Total), FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 1),
            });
            // Children stack top-down, so add in reverse: the first ore type lands at the bottom.
            foreach (var ore in Enumerable.Reverse(oreTypes))
            {
                if (!acct.ByOre.TryGetValue(ore, out long qty) || qty <= 0) continue;
                bar.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Height = Math.Max(1, qty / max * barAreaHeight),
                    Fill = brushes[ore],
                    ToolTip = $"{acct.Account} - {ore}: {qty:N0}",
                });
            }
            column.Children.Add(new Border { Height = barAreaHeight + 16, Child = bar });
            column.Children.Add(new TextBlock
            {
                Text = acct.Account, FontSize = 10, Height = 42,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            });
            ChartPanel.Children.Add(column);
        }

        // Ore composition pie (shares the bar chart's ore colors).
        BuildPie(PieCanvas, PieLegendPanel, rows.GroupBy(r => r.Ore)
            .Select(g => (Label: g.Key, Qty: (double)g.Sum(r => r.Quantity), Fill: brushes[g.Key]))
            .Where(s => s.Qty > 0)
            .OrderByDescending(s => s.Qty)
            .ToList());

        BuildAccountPie(rows);
    }

    /// <summary>Pie of the frack's total quantity by account; small miners collapse into 'Others'.</summary>
    private void BuildAccountPie(List<MiningRow> rows)
    {
        var totals = rows.GroupBy(r => r.Account)
            .Select(g => (Label: g.Key, Qty: (double)g.Sum(r => r.Quantity)))
            .Where(t => t.Qty > 0)
            .OrderByDescending(t => t.Qty)
            .ToList();

        const int maxSlices = 9;
        var slices = new List<(string Label, double Qty, Brush Fill)>();
        for (int i = 0; i < totals.Count && i < maxSlices; i++)
            slices.Add((totals[i].Label, totals[i].Qty, new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(AccountPaletteHex[i % AccountPaletteHex.Length]))));
        if (totals.Count > maxSlices)
            slices.Add(($"Others ({totals.Count - maxSlices})",
                totals.Skip(maxSlices).Sum(t => t.Qty), Brushes.Gray));

        BuildPie(AccountPieCanvas, AccountPieLegendPanel, slices);
    }

    private void BuildPie(Canvas canvas, StackPanel legendPanel,
        List<(string Label, double Qty, Brush Fill)> slices)
    {
        canvas.Children.Clear();
        legendPanel.Children.Clear();
        double total = slices.Sum(s => s.Qty);
        if (total <= 0) return;

        const double cx = 115, cy = 115, radius = 110;
        if (slices.Count == 1)
        {
            var circle = new System.Windows.Shapes.Ellipse
            {
                Width = radius * 2, Height = radius * 2, Fill = slices[0].Fill,
                ToolTip = $"{slices[0].Label}: {slices[0].Qty:N0} (100%)",
            };
            Canvas.SetLeft(circle, cx - radius);
            Canvas.SetTop(circle, cy - radius);
            canvas.Children.Add(circle);
        }
        else
        {
            double angle = -90; // start at 12 o'clock, clockwise
            foreach (var s in slices)
            {
                double sweep = s.Qty / total * 360;
                var slice = PieSlice(cx, cy, radius, angle, angle + sweep, s.Fill);
                slice.ToolTip = $"{s.Label}: {s.Qty:N0} ({s.Qty / total:P1})";
                canvas.Children.Add(slice);
                angle += sweep;
            }
        }

        foreach (var s in slices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 11, Height = 11, Fill = s.Fill,
                Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{s.Label} - {s.Qty / total:P1} ({FormatQty(s.Qty)})",
                FontSize = 11,
            });
            legendPanel.Children.Add(row);
        }
    }

    private static System.Windows.Shapes.Path PieSlice(double cx, double cy, double r,
        double startDeg, double endDeg, Brush fill)
    {
        Point PointAt(double deg) =>
            new(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));

        var figure = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
        figure.Segments.Add(new LineSegment(PointAt(startDeg), true));
        figure.Segments.Add(new ArcSegment(PointAt(endDeg), new Size(r, r), 0,
            endDeg - startDeg > 180, SweepDirection.Clockwise, true));
        return new System.Windows.Shapes.Path
        {
            Data = new PathGeometry(new[] { figure }),
            Fill = fill,
            Stroke = Brushes.White,
            StrokeThickness = 1,
        };
    }

    private static string FormatQty(double v) =>
        v >= 1_000_000 ? $"{v / 1_000_000:0.#}M" : v >= 1_000 ? $"{v / 1_000:0.#}k" : v.ToString("N0");

    private bool FilterRow(object item)
    {
        if (item is not MemberRow row) return false;

        if (StatusFilterBox.SelectedIndex > 0 &&
            row.Status != (string)StatusFilterBox.SelectedItem) return false;

        string search = SearchBox.Text.Trim();
        if (search.Length > 0 &&
            !row.ForumName.Contains(search, StringComparison.OrdinalIgnoreCase) &&
            !row.CharacterNames.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => _rowsView?.Refresh();

    /// <summary>Click a row to expand its character details; click it again to collapse.</summary>
    private void OverviewGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // Clicks inside the expanded details area (the inner characters grid) shouldn't collapse it.
        if (FindParent<DataGridDetailsPresenter>(source) != null) return;

        var row = FindParent<DataGridRow>(source);
        if (row == null || FindParent<DataGrid>(row) != OverviewGrid) return;

        if (ReferenceEquals(OverviewGrid.SelectedItem, row.Item))
        {
            OverviewGrid.SelectedItem = null;
            e.Handled = true;
        }
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void UpdateStatusBar()
    {
        int awol = _rows.Count(r => r.Status == Statuses.Awol);
        int away = _rows.Count(r => r.Status == Statuses.AwayFromGame);
        int risk = _rows.Count(r => r.PapsRisk);

        string esi = _data.EsiLastRefreshUtc.HasValue
            ? $"ESI refreshed {_data.EsiLastRefreshUtc:yyyy-MM-dd HH:mm}Z"
            : "ESI: no data yet";
        string paps = _data.PapsLastImportUtc.HasValue
            ? $"Paps imported {_data.PapsLastImportUtc:yyyy-MM-dd HH:mm}Z"
            : "Paps: no data yet";

        StatusText.Text = $"{esi}  |  {paps}  |  AWOL: {awol}  AFK-LOA: {away}  💀 active without paps: {risk}";

        UpdateEsiRefreshLabel();

        PapsLastImportText.Text = _data.PapsLastImportUtc.HasValue
            ? $"Last imported {_data.PapsLastImportUtc.Value.ToLocalTime():MM/dd}"
            : "Never imported";
        MapLastImportText.Text = _data.MappingsLastImportUtc.HasValue
            ? $"Last imported {_data.MappingsLastImportUtc.Value.ToLocalTime():MM/dd}"
            : "Never imported";

        // Pilot map staleness: nag once the import is older than the cadence (or missing entirely).
        double? mapAgeDays = _data.MappingsLastImportUtc.HasValue
            ? (DateTime.UtcNow - _data.MappingsLastImportUtc.Value).TotalDays
            : null;
        bool stale = mapAgeDays == null || mapAgeDays > PilotMapStaleDays;
        PilotMapCaption.Text = stale ? "Character & Account ⚠" : "Character & Account";
        PilotMapCaption.Foreground = stale ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        PilotMapCaption.ToolTip = mapAgeDays == null
            ? "Pilot map has never been imported."
            : $"Pilot map last imported {(int)mapAgeDays} days ago" +
              (stale ? $" - re-import due (every {PilotMapStaleDays} days)." : ".");

        static string Delta((int Added, int Removed)? d) =>
            d.HasValue ? $" (+{d.Value.Added} / −{d.Value.Removed})" : "";
        TotalsText.Text = $"Characters: {_data.CharacterMappings.Count}{Delta(_mappingsDelta)}" +
                          $"   |   Forum accounts: {_data.ForumAccounts.Count}{Delta(_papsDelta)}";
    }

    private void SetBusy(bool busy, string message)
    {
        RefreshButton.IsEnabled = !busy;
        StatusText.Text = message;
    }
}
