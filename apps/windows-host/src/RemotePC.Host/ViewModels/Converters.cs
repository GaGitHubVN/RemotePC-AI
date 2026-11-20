using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RemotePC.Host.ViewModels;

public sealed class PageVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (string)value == (string)parameter ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.FindResource(value is true ? "Success" : "TextSecondary");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ZeroToVisible : IValueConverter
{
    public static ZeroToVisible Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LocKey : IValueConverter
{
    public static LocKey Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Loc.Instance[(string)value];
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class ThemeManager
{
    public static void Apply(string theme, bool reducedMotion)
    {
        var dark = theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") is 0,
        };
        var dicts = Application.Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary { Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        Application.Current.Resources["DurFast"] = new Duration(reducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(120));
        Application.Current.Resources["DurMedium"] = new Duration(reducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(220));
        ReducedMotion = reducedMotion || !SystemParameters.ClientAreaAnimation;
    }

    public static bool ReducedMotion { get; private set; }

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
