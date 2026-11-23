using System.Windows;
using Application = System.Windows.Application;

namespace MyASR;

internal static class AppTheme
{
    private static readonly Uri Light = new("/MyASR;component/Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri Dark = new("/MyASR;component/Themes/Dark.xaml", UriKind.Relative);

    public static void Apply(Application app, bool dark)
    {
        var source = dark ? Dark : Light;
#pragma warning disable WPF0001 // .NET 11 exposes native Fluent themes through this experimental API.
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source == Light || d.Source == Dark);
        if (existing?.Source == source) return;
        var palette = new ResourceDictionary { Source = source };
        if (existing == null) dictionaries.Add(palette);
        else dictionaries[dictionaries.IndexOf(existing)] = palette;
    }
}
