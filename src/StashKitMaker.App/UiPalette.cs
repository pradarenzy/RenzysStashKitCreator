using System.Globalization;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace StashKitMaker.App;

internal static class UiPalette
{
    public const string DefaultAccent = "#A66F83";

    public static string NormalizeOrDefault(string? value) => TryNormalize(value, out var normalized) ? normalized : DefaultAccent;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = DefaultAccent;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim();
        if (candidate.Length == 6 && candidate[0] != '#') candidate = "#" + candidate;
        if (candidate.Length != 7 || candidate[0] != '#') return false;
        if (!byte.TryParse(candidate.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(candidate.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(candidate.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue)) return false;
        normalized = $"#{red:X2}{green:X2}{blue:X2}";
        return true;
    }

    public static void Apply(string? value)
    {
        var normalized = NormalizeOrDefault(value);
        _ = TryNormalize(normalized, out _);
        var seed = Parse(normalized);
        var grey = (byte)Math.Clamp((int)Math.Round(seed.R * .2126 + seed.G * .7152 + seed.B * .0722), 0, 255);
        var muted = ConstrainLuminance(Mix(seed, Color.FromRgb(grey, grey, grey), .43));
        var active = muted;
        var accent = Mix(active, Color.FromRgb(70, 65, 67), .18);
        var primaryText = Color.FromRgb(69, 65, 66);
        var accentForeground = Contrast(active, Colors.White) >= Contrast(active, primaryText) ? Colors.White : primaryText;

        SetBrush("WindowBackground", Color.FromArgb(140, 255, 255, 255));
        SetTitleBarBrush();
        SetBrush("SidebarBackground", WithAlpha(Mix(active, Colors.White, .95), 160));
        SetBrush("PanelBackground", WithAlpha(Mix(active, Colors.White, .91), 175));
        SetBrush("ControlBackground", WithAlpha(Mix(active, Colors.White, .87), 190));
        SetBrush("ControlHover", WithAlpha(Mix(active, Colors.White, .78), 200));
        SetBrush("GridBackground", Color.FromArgb(160, 255, 255, 255));
        SetBrush("PopupBackground", Mix(active, Colors.White, .96));
        SetBrush("GridAlternate", WithAlpha(Mix(active, Colors.White, .96), 170));
        SetBrush("GridHeader", WithAlpha(Mix(active, Colors.White, .68), 180));
        SetBrush("DisabledBackground", WithAlpha(Mix(active, Colors.White, .94), 175));
        SetBrush("Border", WithAlpha(Mix(Mix(active, Colors.White, .78), Color.FromRgb(202, 199, 200), .62), 120));
        SetBrush("Accent", accent);
        SetBrush("AccentBackground", WithAlpha(active, 190));
        SetBrush("AccentForeground", accentForeground);
        SetBrush("Selection", WithAlpha(Mix(active, Colors.White, .52), 195));
        SetBrush("ButtonHoverOverlay", Color.FromArgb(30, 255, 255, 255));
    }

    public static System.Drawing.Color ToDrawingColor(string? value)
    {
        var color = Parse(NormalizeOrDefault(value));
        return System.Drawing.Color.FromArgb(color.R, color.G, color.B);
    }

    private static void SetBrush(string key, Color color) => System.Windows.Application.Current.Resources[key] = new SolidColorBrush(color);
    private static void SetTitleBarBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(.5, 0),
            EndPoint = new System.Windows.Point(.5, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(184, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(156, 255, 255, 255), .55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(140, 255, 255, 255), 1));
        System.Windows.Application.Current.Resources["TitleBarBackground"] = brush;
    }
    private static Color Parse(string value) => Color.FromRgb(Convert.ToByte(value.Substring(1, 2), 16), Convert.ToByte(value.Substring(3, 2), 16), Convert.ToByte(value.Substring(5, 2), 16));
    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Mix(Color first, Color second, double secondWeight)
    {
        secondWeight = Math.Clamp(secondWeight, 0, 1);
        var firstWeight = 1 - secondWeight;
        return Color.FromRgb(
            (byte)Math.Round(first.R * firstWeight + second.R * secondWeight),
            (byte)Math.Round(first.G * firstWeight + second.G * secondWeight),
            (byte)Math.Round(first.B * firstWeight + second.B * secondWeight));
    }

    private static Color ConstrainLuminance(Color color)
    {
        var luminance = RelativeLuminance(color);
        if (luminance > .66) color = Mix(color, Color.FromRgb(70, 65, 67), .22);
        else if (luminance < .18) color = Mix(color, Colors.White, .25);
        return color;
    }

    private static double Contrast(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + .05) / (darker + .05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte component)
        {
            var value = component / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
