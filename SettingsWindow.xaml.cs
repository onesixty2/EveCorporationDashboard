using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EveCorporationDashboard.Models;
using EveCorporationDashboard.Services;

namespace EveCorporationDashboard;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<Task<(bool Ok, string Message)>>? _login;
    private readonly Action? _deleteImportedData;

    public SettingsWindow(AppSettings settings,
        Func<Task<(bool Ok, string Message)>>? loginAsync = null,
        Action? deleteImportedData = null)
    {
        InitializeComponent();
        CorpIcon.Apply(this);
        AppUi.Apply(this);
        _settings = settings;
        _login = loginAsync;
        _deleteImportedData = deleteImportedData;
        LoginButton.IsEnabled = loginAsync != null;
        InactiveDaysBox.Text = settings.InactiveDaysThreshold.ToString();
        MinPapsBox.Text = settings.MinPaps30.ToString(CultureInfo.InvariantCulture);
        UpdateLoginInfo();
        UpdatePapsGroupText();
    }

    private const string AuthGroupListUrl = "https://manager.goonfleet.com/auth-group";

    private void UpdatePapsGroupText()
    {
        PapsGroupText.Inlines.Clear();
        if (string.IsNullOrEmpty(_settings.PapsGroupUrl))
        {
            PapsGroupText.Text = "No paps group set. Click 'Auth Groups', press Ctrl+A then Ctrl+C " +
                                 "on that page, then click 'Import group from clipboard'.";
            return;
        }
        PapsGroupText.Inlines.Add(new System.Windows.Documents.Run($"Group: {_settings.PapsGroupName}  "));
        var link = new System.Windows.Documents.Hyperlink(
            new System.Windows.Documents.Run(_settings.PapsGroupUrl))
        {
            NavigateUri = new Uri(_settings.PapsGroupUrl),
        };
        link.RequestNavigate += (_, e) =>
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        };
        PapsGroupText.Inlines.Add(link);
    }

    private void AuthGroups_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AuthGroupListUrl) { UseShellExecute = true });

    private void ImportGroup_Click(object sender, RoutedEventArgs e)
    {
        string? html = null;
        try
        {
            if (Clipboard.ContainsText(TextDataFormat.Html)) html = Clipboard.GetText(TextDataFormat.Html);
        }
        catch { /* clipboard busy */ }

        var groups = TableParser.ExtractAuthGroupLinks(html);
        if (groups.Count == 0)
        {
            MessageBox.Show(this,
                "No auth group links found on the clipboard. Click 'Auth Groups', press Ctrl+A then " +
                "Ctrl+C on that page, then try again.",
                "Import group", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Prefer the group whose name matches the member corp's name.
        (long GroupId, string Name, string? IconUrl)? chosen = null;
        string? memberCorpName = _settings.PilotMapCorps.FirstOrDefault(c => c.IsMemberCorp)?.CorpName;
        if (!string.IsNullOrEmpty(memberCorpName))
            foreach (var g in groups)
                if (g.Name.Contains(memberCorpName, StringComparison.OrdinalIgnoreCase) ||
                    memberCorpName.Contains(g.Name, StringComparison.OrdinalIgnoreCase))
                {
                    chosen = g;
                    break;
                }
        if (chosen == null && groups.Count == 1) chosen = groups[0];
        chosen ??= PickPapsGroup(groups);
        if (chosen == null) return;

        _settings.PapsGroupUrl = $"https://manager.goonfleet.com/auth-group/view/{chosen.Value.GroupId}";
        _settings.PapsGroupName = chosen.Value.Name;
        // The group's team icon doubles as the app's window icon.
        if (!string.IsNullOrEmpty(chosen.Value.IconUrl))
            _settings.CorpIconUrl = chosen.Value.IconUrl;
        DataStore.SaveSettings(_settings);
        _ = RefreshCorpIconAsync();
        UpdatePapsGroupText();
    }

    private async Task RefreshCorpIconAsync()
    {
        await CorpIcon.LoadAsync(_settings);
        CorpIcon.Apply(this);
        if (Owner != null) CorpIcon.Apply(Owner);
    }

    private (long GroupId, string Name, string? IconUrl)? PickPapsGroup(
        List<(long GroupId, string Name, string? IconUrl)> groups)
    {
        var combo = new ComboBox { Margin = new Thickness(0, 8, 0, 14) };
        foreach (var group in groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            combo.Items.Add(new ComboBoxItem { Content = group.Name, Tag = group.GroupId });
        combo.SelectedIndex = 0;

        var ok = new Button { Content = "Select", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Select your member corp's paps group:" });
        panel.Children.Add(combo);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = "Import group from clipboard",
            Content = panel,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Icon = CorpIcon.Current,
        };
        win.SetResourceReference(FontFamilyProperty, "AppFont");
        AppUi.Apply(win);
        ok.Click += (_, _) => win.DialogResult = true;

        if (win.ShowDialog() != true ||
            combo.SelectedItem is not ComboBoxItem { Tag: long id }) return null;
        var match = groups.FirstOrDefault(g => g.GroupId == id);
        return match.GroupId == 0 ? null : match;
    }

    private void UpdateLoginInfo()
    {
        LoginInfoText.Text = string.IsNullOrEmpty(_settings.RefreshToken)
            ? "Not logged in."
            : $"Logged in as {_settings.CharacterName} (corp ID {_settings.CorporationId}).";
        ForgetLoginButton.IsEnabled = !string.IsNullOrEmpty(_settings.RefreshToken);
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_login == null) return;
        if (!TryApplyFields()) return;

        LoginButton.IsEnabled = false;
        ForgetLoginButton.IsEnabled = false;
        LoginInfoText.Text = "Waiting for EVE SSO login in your browser…";
        var (ok, message) = await _login();
        LoginInfoText.Text = message;
        LoginButton.IsEnabled = true;
        ForgetLoginButton.IsEnabled = !string.IsNullOrEmpty(_settings.RefreshToken);
        if (!ok)
            MessageBox.Show(this, message, "EVE SSO login failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ForgetLogin_Click(object sender, RoutedEventArgs e)
    {
        _settings.RefreshToken = null;
        _settings.CharacterId = 0;
        _settings.CharacterName = null;
        _settings.CorporationId = 0;
        UpdateLoginInfo();
    }

    private void DeleteImported_Click(object sender, RoutedEventArgs e)
    {
        if (_deleteImportedData == null) return;
        if (MessageBox.Show(this,
                "Delete ALL stored data - paps, pilot map, ESI members, the mining ledger, " +
                "citadel fuel, the corp list, and the paps group?\n\n" +
                "Only your Client ID, callback URL, and login are kept. Corps can be " +
                "re-detected afterwards with 'Corps' in the Pilot Map window.",
                "Delete all data", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _deleteImportedData();
        UpdatePapsGroupText();
        MessageBox.Show(this, "All data has been deleted. You're on a clean slate - your login is still active.",
            "Done", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TryApplyFields()) DialogResult = true;
    }

    /// <summary>Validates the input fields and writes them into the settings object.</summary>
    private bool TryApplyFields()
    {
        if (!int.TryParse(InactiveDaysBox.Text, out int days) || days < 1)
        {
            MessageBox.Show(this, "Inactive days must be a positive number.",
                "Invalid threshold", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!double.TryParse(MinPapsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minPaps)
            && !double.TryParse(MinPapsBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out minPaps))
        {
            MessageBox.Show(this, "Minimum paps must be a number.",
                "Invalid threshold", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _settings.InactiveDaysThreshold = days;
        _settings.MinPaps30 = minPaps;
        return true;
    }
}
