using System.Windows;
using System.Windows.Media;

namespace EveCorporationDashboard.Services;

/// <summary>User-adjustable UI scale, applied as a layout zoom on every window.</summary>
public static class AppUi
{
    public const double MinScale = 1.0;
    public const double MaxScale = 2.0;

    public static double Scale { get; set; } = 1.0;

    public static double Clamp(double scale) => Math.Clamp(scale, MinScale, MaxScale);

    public static void Apply(Window window)
    {
        if (window.Content is FrameworkElement root)
            root.LayoutTransform = Math.Abs(Scale - 1.0) < 0.01
                ? null
                : new ScaleTransform(Scale, Scale);

        // Grow fixed-width windows with the scale, and never let any window outgrow the screen.
        if (!double.IsNaN(window.Width)) window.Width *= Scale;
        window.MaxHeight = SystemParameters.WorkArea.Height;
        window.MaxWidth = SystemParameters.WorkArea.Width;
    }
}
