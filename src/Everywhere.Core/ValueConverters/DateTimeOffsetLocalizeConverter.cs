using System.Globalization;
using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

public class DateTimeOffsetLocalizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not DateTimeOffset dto ? value : dto.ToLocalTime();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not DateTimeOffset dto ? value : dto.ToUniversalTime();
}