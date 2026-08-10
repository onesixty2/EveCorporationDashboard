using System.Windows;
using System.Windows.Media;

namespace EveCorporationDashboard.Services;

/// <summary>Swaps the application-level theme brushes between light and dark palettes.
/// The dark palette leans EVE client: near-black blue-gray with amber accents.</summary>
public static class ThemeManager
{
    public static void Apply(bool dark)
    {
        var resources = Application.Current.Resources;
        void Set(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            resources[key] = brush;
        }

        if (dark)
        {
            Set("ThWindowBg", "#0E1319");
            Set("ThPanelBg", "#151C24");
            Set("ThCardBg", "#131A21");
            Set("ThText", "#E6EDF3");
            Set("ThTextDim", "#9DB0BE");
            Set("ThGridBg", "#10161C");
            Set("ThGridLine", "#2A3540");
            Set("ThHeaderBg", "#1A222B");
            Set("ThBtnBg", "#1B242E");
            Set("ThBtnHover", "#24303C");
            Set("ThBtnPressed", "#0C1116");
            Set("ThBtnBorder", "#35424F");
            Set("ThFieldBg", "#0F151B");
            Set("ThAccent", "#D9A33C");
            Set("StAwol", "#6E2B2B");
            Set("StLoa", "#6E5230");
            Set("StLow", "#635D2B");
            Set("StNoPart", "#6E5A26");
            Set("StInactive", "#6E4A24");
            Set("StAfk", "#6E3535");
            Set("StActive", "#2B5E33");
            Set("StUnmapped", "#4E3D66");
        }
        else
        {
            Set("ThWindowBg", "#FFFFFF");
            Set("ThPanelBg", "#F2F2F2");
            Set("ThCardBg", "#FFFFFF");
            Set("ThText", "#111111");
            Set("ThTextDim", "#595959");
            Set("ThGridBg", "#FFFFFF");
            Set("ThGridLine", "#D6D6D6");
            Set("ThHeaderBg", "#EDEDED");
            Set("ThBtnBg", "#EDEDED");
            Set("ThBtnHover", "#DDE8F3");
            Set("ThBtnPressed", "#C8D9EA");
            Set("ThBtnBorder", "#ABABAB");
            Set("ThFieldBg", "#FFFFFF");
            Set("ThAccent", "#B87A1F");
            Set("StAwol", "#FFDBDB");
            Set("StLoa", "#FFE9CF");
            Set("StLow", "#FFF8CE");
            Set("StNoPart", "#FFE9A8");
            Set("StInactive", "#FFD59B");
            Set("StAfk", "#FFC4C4");
            Set("StActive", "#DFF5DF");
            Set("StUnmapped", "#EBE3F5");
        }
    }
}
