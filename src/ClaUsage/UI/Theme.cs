using System.Windows;
using System.Windows.Media;
using ClaUsage.Core;

namespace ClaUsage.UI;

/// <summary>Colors for the chosen theme (or the Windows app theme), read each time a window opens.</summary>
public sealed class Theme
{
    public bool Dark { get; }
    public SolidColorBrush Background { get; }
    public SolidColorBrush Card { get; }
    public SolidColorBrush CardHover { get; }
    public SolidColorBrush Border { get; }
    public SolidColorBrush Text { get; }
    public SolidColorBrush Muted { get; }
    public SolidColorBrush Track { get; }
    public SolidColorBrush Pace { get; }
    public SolidColorBrush Ok { get; }
    public SolidColorBrush Warn { get; }
    public SolidColorBrush Critical { get; }
    public SolidColorBrush Accent { get; }
    public SolidColorBrush ButtonHover { get; }

    private Theme(bool dark)
    {
        Dark = dark;
        Background = B(dark ? "#F01C1C1E" : "#F7F7F7F9");
        Card = B(dark ? "#2A2A2D" : "#FFFFFF");
        CardHover = B(dark ? "#323236" : "#F2F2F5");
        Border = B(dark ? "#3A3A3E" : "#E2E2E6");
        Text = B(dark ? "#F2F2F4" : "#1C1C1E");
        Muted = B(dark ? "#9A9AA2" : "#6E6E76");
        Track = B(dark ? "#3C3C42" : "#E6E6EA");
        Pace = B(dark ? "#E6E6EA" : "#3C3C42");
        Ok = B(dark ? "#34C77B" : "#1FA463");
        Warn = B(dark ? "#FFA53A" : "#E68A00");
        Critical = B(dark ? "#FF5B57" : "#E0382F");
        Accent = B(dark ? "#5EA2FF" : "#2F6FEB");
        ButtonHover = B(dark ? "#3A3A3F" : "#E8E8EC");
    }

    /// <summary>The theme picked in Settings; <see cref="AppTheme.System"/> follows Windows.</summary>
    public static AppTheme Mode { get; set; }

    public static bool IsDark => Mode switch
    {
        AppTheme.Light => false,
        AppTheme.Dark => true,
        _ => WindowsTheme.IsDark,
    };

    public static Theme Current() => new(IsDark);

    public SolidColorBrush ForFraction(double fraction, AppSettings settings)
    {
        var percent = fraction * 100;
        if (percent >= settings.CriticalPercent) return Critical;
        if (percent >= settings.WarnPercent) return Warn;
        return Ok;
    }

    public static SolidColorBrush ProviderColor(ProviderKind kind) => kind switch
    {
        ProviderKind.Claude => B("#D97757"),
        ProviderKind.Codex => B("#0D0D0D"),
        ProviderKind.OpenRouter => B("#6467F2"),
        ProviderKind.DeepSeek => B("#4D6BFE"),
        _ => B("#888888"),
    };

    public void Apply(ResourceDictionary resources)
    {
        resources["BgBrush"] = Background;
        resources["CardBrush"] = Card;
        resources["CardHoverBrush"] = CardHover;
        resources["BorderBrush"] = Border;
        resources["TextBrush"] = Text;
        resources["MutedBrush"] = Muted;
        resources["AccentBrush"] = Accent;
        resources["ButtonHoverBrush"] = ButtonHover;
        resources["ScrollThumbBrush"] = B(Dark ? "#5A5A62" : "#C2C2C8");
    }

    private static SolidColorBrush B(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
