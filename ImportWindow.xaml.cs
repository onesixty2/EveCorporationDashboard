using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using EveCorporationDashboard.Models;
using EveCorporationDashboard.Services;

namespace EveCorporationDashboard;

public class ImportField
{
    public string Key { get; }
    public string Label { get; }
    public bool Required { get; }
    public string[] Keywords { get; }

    public ImportField(string key, string label, bool required, params string[] keywords)
    {
        Key = key; Label = label; Required = required; Keywords = keywords;
    }
}

public class ImportResult
{
    public Dictionary<string, int> Mapping { get; } = new();
    public List<string[]> DataRows { get; set; } = new();
}

/// <summary>
/// One-click clipboard import: parses the copied page, auto-maps the expected columns by
/// header keywords, and applies immediately. Fails loudly when the clipboard doesn't look
/// like the expected page.
/// </summary>
public partial class ImportWindow : Window
{
    private readonly List<ImportField> _fields;
    private readonly Func<ImportResult, string?, string> _apply;
    private readonly Func<string, DateTime?>? _lastImport;
    private readonly Func<Task<(bool Ok, string Message)>>? _syncCorps;
    private readonly Func<List<(string Label, string Url, string? Ticker)>>? _sourcesProvider;
    private readonly Func<string, bool>? _labelConfirmed;
    private readonly Dictionary<string, TextBlock> _sourceDateTexts = new();
    private readonly List<(string Label, string Url, string? Ticker)> _sources = new();
    private string? _lastSourceLabel;

    public ImportWindow(string title, List<ImportField> fields,
        IReadOnlyList<(string Label, string Url, string? Ticker)> sources,
        Func<ImportResult, string?, string> applyCallback,
        Func<string, DateTime?>? lastImportLookup = null,
        Func<Task<(bool Ok, string Message)>>? syncCorps = null,
        Func<List<(string Label, string Url, string? Ticker)>>? sourcesProvider = null,
        Func<string, bool>? labelConfirmed = null,
        string? instructions = null,
        string? modeNote = null)
    {
        InitializeComponent();
        Title = title;
        CorpIcon.Apply(this);
        AppUi.Apply(this);
        _fields = fields;
        _apply = applyCallback;
        _lastImport = lastImportLookup;
        _syncCorps = syncCorps;
        _sourcesProvider = sourcesProvider;
        _labelConfirmed = labelConfirmed;

        if (instructions != null) InstructionsText.Text = instructions;
        if (modeNote != null) ImportModeText.Text = modeNote;
        if (_syncCorps != null) CheckCorpsPanel.Visibility = Visibility.Visible;

        _sources.AddRange(sources);
        RebuildLinksPanel();
    }

    // ---------- Import ----------

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var rows = TryReadClipboardTable();
        if (rows == null || rows.Count < 2)
        {
            ShowFail("No table found on the clipboard - open the page above, press Ctrl+A then Ctrl+C, " +
                     "and try again.");
            return;
        }

        var mapping = AutoMap(rows[0], out string? missingColumn);
        if (missingColumn != null)
        {
            ShowFail($"That doesn't look like the right page - couldn't find a '{missingColumn}' column.");
            return;
        }

        var result = new ImportResult { DataRows = rows.Skip(1).ToList() };
        foreach (var (key, column) in mapping) result.Mapping[key] = column;

        string status = _apply(result, _lastSourceLabel);
        _lastSourceLabel = null;

        // Labels can change during an apply (CEO alt corrected to forum name) - re-pull, but
        // ordering is stable so nothing moves.
        if (_sourcesProvider != null)
        {
            _sources.Clear();
            _sources.AddRange(_sourcesProvider());
            RebuildLinksPanel();
        }
        else
        {
            UpdateSourceDates();
        }
        ShowSuccess(status);
    }

    /// <summary>Maps each expected field to a column by header keywords; first unclaimed match wins.</summary>
    private Dictionary<string, int> AutoMap(string[] headers, out string? missingColumn)
    {
        var mapping = new Dictionary<string, int>();
        var taken = new HashSet<int>();
        missingColumn = null;
        foreach (var field in _fields)
        {
            int found = -1;
            foreach (var keyword in field.Keywords)
            {
                for (int i = 0; i < headers.Length && found < 0; i++)
                    if (!taken.Contains(i) &&
                        headers[i].Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        found = i;
                if (found >= 0) break;
            }
            if (found >= 0)
            {
                mapping[field.Key] = found;
                taken.Add(found);
            }
            else if (field.Required)
            {
                missingColumn = field.Label;
                return mapping;
            }
        }
        return mapping;
    }

    private void ShowSuccess(string message)
    {
        ImportStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
        ImportStatusText.Text = "✔ " + message;
    }

    private void ShowFail(string message)
    {
        ImportStatusText.Foreground = Brushes.Firebrick;
        ImportStatusText.Text = "✖ " + message;
    }

    private static List<string[]>? TryReadClipboardTable()
    {
        string? html = null, text = null;
        try
        {
            if (Clipboard.ContainsText(TextDataFormat.Html)) html = Clipboard.GetText(TextDataFormat.Html);
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch { /* clipboard can be locked by another process; treat as empty */ }
        return TableParser.ParseClipboard(html, text);
    }

    // ---------- Source links ----------

    private void RebuildLinksPanel()
    {
        LinksPanel.Children.Clear();
        _sourceDateTexts.Clear();
        LinksPanel.Visibility = Visibility.Visible;
        if (_sources.Count == 0)
        {
            if (_syncCorps != null)
                LinksPanel.Children.Add(new TextBlock
                {
                    Text = "Import corps to get started",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.Firebrick,
                });
            else
                LinksPanel.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var (label, url, ticker) in _sources)
            AddSourceBlock(label, url, ticker);
        UpdateSourceDates();
    }

    private void AddSourceBlock(string label, string url, string? ticker)
    {
        // The ticker is the headline link; the owner's forum name sits underneath.
        var link = new Hyperlink(new Run(ticker ?? label)) { NavigateUri = new Uri(url), ToolTip = url, Tag = label };
        link.RequestNavigate += SourceLink_RequestNavigate;
        var text = new TextBlock();
        text.Inlines.Add(link);
        if (_labelConfirmed != null && !_labelConfirmed(label))
        {
            text.Inlines.Add(new Run(" (!)")
            {
                Foreground = Brushes.Firebrick,
                FontWeight = FontWeights.Bold,
                ToolTip = "This owner isn't confirmed by the pilot map yet - the name shown may be a " +
                          "CEO alt. It auto-corrects to the owner's forum name once this corp is imported.",
            });
        }

        var block = new StackPanel { Margin = new Thickness(0, 0, 16, 4) };
        block.Children.Add(text);
        if (ticker != null)
        {
            var owner = new TextBlock { Text = label, FontSize = 10 };
            owner.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            block.Children.Add(owner);
        }
        if (_lastImport != null)
        {
            var dateText = new TextBlock { FontSize = 10 };
            _sourceDateTexts[label] = dateText;
            block.Children.Add(dateText);
        }
        LinksPanel.Children.Add(block);
    }

    private void UpdateSourceDates()
    {
        if (_lastImport == null) return;
        foreach (var (label, text) in _sourceDateTexts)
        {
            DateTime? date = _lastImport(label);
            if (date.HasValue)
            {
                text.Text = $"{date.Value.ToLocalTime():MM/dd}";
                text.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            }
            else
            {
                text.Text = "never";
                text.Foreground = Brushes.Firebrick;
            }
        }
    }

    private void SourceLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // Remember which corp's page was opened last so the import confirmation can name it.
        if (sender is Hyperlink { Tag: string label }) _lastSourceLabel = label;
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async void SyncCorps_Click(object sender, RoutedEventArgs e)
    {
        if (_syncCorps == null) return;
        SyncCorpsButton.IsEnabled = false;
        try
        {
            var (ok, message) = await _syncCorps();
            if (ok && _sourcesProvider != null)
            {
                _sources.Clear();
                _sources.AddRange(_sourcesProvider());
                RebuildLinksPanel();
            }
            if (ok) ShowSuccess(message);
            else ShowFail(message);
        }
        finally
        {
            SyncCorpsButton.IsEnabled = true;
        }
    }

    private void CorpListLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
