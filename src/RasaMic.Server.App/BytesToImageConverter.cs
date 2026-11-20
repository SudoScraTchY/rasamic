using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace RasaMic.Server.App;

public sealed class BytesToImageConverter : IValueConverter
{
    public static readonly BytesToImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is byte[] bytes ? new Bitmap(new MemoryStream(bytes)) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}