using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace TransReader.App;

public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush? TrueBrush { get; set; }

    public Brush? FalseBrush { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? TrueBrush ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : FalseBrush ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
