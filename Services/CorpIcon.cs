using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EveCorporationDashboard.Models;

namespace EveCorporationDashboard.Services;

/// <summary>
/// The director corp's icon, shared by every window. Once a director is logged in the corp
/// icon always wins over the built-in default: it is downloaded explicitly and cached on
/// disk, so it shows deterministically and survives offline launches.
/// Priority: team icon from the auth group page, then the ESI corp logo, then the default.
/// </summary>
public static class CorpIcon
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static bool _cacheChecked;

    public static ImageSource? Current { get; private set; }

    private static string CachePath => Path.Combine(DataStore.Directory, "corp-icon.png");

    public static async Task LoadAsync(AppSettings settings)
    {
        Current ??= LoadDefault();

        // The disk cache gives an instant, offline-safe corp icon from a previous run.
        if (!_cacheChecked)
        {
            _cacheChecked = true;
            try
            {
                if (File.Exists(CachePath))
                    Current = FromBytes(File.ReadAllBytes(CachePath)) ?? Current;
            }
            catch { /* unreadable cache: ignore */ }
        }

        string? url = !string.IsNullOrEmpty(settings.CorpIconUrl)
            ? settings.CorpIconUrl
            : settings.CorporationId != 0
                ? $"https://images.evetech.net/corporations/{settings.CorporationId}/logo?size=64"
                : null;
        if (url == null) return;

        try
        {
            byte[] bytes = await Http.GetByteArrayAsync(url);
            var image = FromBytes(bytes);
            if (image != null)
            {
                Current = image;
                System.IO.Directory.CreateDirectory(DataStore.Directory);
                File.WriteAllBytes(CachePath, bytes);
            }
        }
        catch { /* offline: cached or default icon stays */ }
    }

    /// <summary>Back to the built-in default (used by the delete-all wipe).</summary>
    public static void Reset()
    {
        Current = LoadDefault();
        _cacheChecked = false;
        try { File.Delete(CachePath); } catch { }
    }

    public static void Apply(Window window)
    {
        if (Current != null) window.Icon = Current;
    }

    private static ImageSource? LoadDefault()
    {
        try
        {
            var image = new BitmapImage(new Uri("pack://application:,,,/Assets/newbee.png"));
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static ImageSource? FromBytes(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
}
