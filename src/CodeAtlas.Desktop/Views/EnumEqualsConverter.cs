using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace CodeAtlas.Desktop.Views;

/// <summary>
/// Compares a bound value with the converter parameter. Used both to show the section
/// the sidebar has selected and to bind the sidebar's radio buttons back to it, which
/// is why <see cref="ConvertBack"/> returns the parameter rather than a boolean.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : BindingOperations.DoNothing;
}
