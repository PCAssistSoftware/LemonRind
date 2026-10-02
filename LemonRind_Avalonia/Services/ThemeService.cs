using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace LemonRindAvalonia.Services;

/// <summary>
/// Applies the Settings -> Interface -> Theme choice ("System" / "Light" / "Dark") and looks up the palette brushes
/// defined in App.axaml for code that builds brushes itself. XAML uses {DynamicResource ...} directly, which follows a
/// theme change live; anything looked up through <see cref="Brush"/> is a snapshot for the current theme.
/// </summary>
public static class ThemeService
{
    public static readonly string[] Options = ["System", "Light", "Dark"];

    public static void Apply(string? theme)
    {
        if (Application.Current is not { } app) return;
        // LEMONRIND_THEME=Light|Dark|System overrides the saved choice for one run - handy for testing a theme
        // without touching the settings file.
        theme = Environment.GetEnvironmentVariable("LEMONRIND_THEME") ?? theme;
        app.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>The palette brush for <paramref name="key"/> in the current theme, or <paramref name="fallback"/> if it isn't defined.</summary>
    public static IBrush Brush(string key, IBrush fallback)
    {
        var app = Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }
        return fallback;
    }
}
