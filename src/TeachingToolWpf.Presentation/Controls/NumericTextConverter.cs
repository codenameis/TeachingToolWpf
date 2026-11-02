using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace TeachingToolWpf.Controls;

/// <summary>Keeps numeric edit text intact while the binding updates its typed source on each keystroke.</summary>
public sealed class NumericTextConverter : MarkupExtension, IValueConverter
{
    private object? _lastNumber;
    private string? _lastText;

    // Each field owns its draft; sharing a converter between bindings would mix their edit text.
    public override object ProvideValue(IServiceProvider serviceProvider) => new NumericTextConverter();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Equals(value, _lastNumber) && _lastText is not null
            ? _lastText
            : System.Convert.ToString(value, culture) ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || !TryParse(text, targetType, culture, out var number))
        {
            // WPF reports a conversion validation error and keeps the invalid draft visible.
            return DependencyProperty.UnsetValue;
        }
        _lastNumber = number;
        _lastText = text;
        return number;
    }

    private static bool TryParse(string text, Type targetType, CultureInfo culture, out object number)
    {
        if (targetType == typeof(double) && double.TryParse(text, NumberStyles.Float, culture, out var doubleValue))
        {
            number = doubleValue;
            return true;
        }
        if (targetType == typeof(float) && float.TryParse(text, NumberStyles.Float, culture, out var floatValue))
        {
            number = floatValue;
            return true;
        }
        if (targetType == typeof(int) && int.TryParse(text, NumberStyles.Integer, culture, out var integerValue))
        {
            number = integerValue;
            return true;
        }
        if (targetType == typeof(byte) && byte.TryParse(text, NumberStyles.Integer, culture, out var byteValue))
        {
            number = byteValue;
            return true;
        }
        number = 0;
        return false;
    }
}
