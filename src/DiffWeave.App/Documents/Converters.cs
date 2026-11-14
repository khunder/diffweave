using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DiffWeave.App.Documents;

/// <summary>Value converters used by views.</summary>
public static class Converters
{
    /// <summary>double → Thickness(left, 0, 0, 0).</summary>
    public static IValueConverter LeftMargin { get; } = new FuncValueConverter<double, Thickness>(v => new Thickness(v, 0, 0, 0));

    /// <summary>Resource key → brush from the application resources (theme aware).</summary>
    public static IValueConverter ResourceBrush { get; } = new ResourceBrushConverter();

    private sealed class ResourceBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string key && Application.Current is { } app
                && app.TryGetResource(key, app.ActualThemeVariant, out object? res) && res is IBrush b)
            {
                return b;
            }

            return Brushes.Gray;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

/// <summary>Converters comparing a value with the converter parameter (radio menu items).</summary>
public static class EqualityConverters
{
    /// <summary>True when value.ToString() equals the parameter.</summary>
    public static IValueConverter EqualsParameter { get; } = new EqualsParameterConverter();

    private sealed class EqualsParameterConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
