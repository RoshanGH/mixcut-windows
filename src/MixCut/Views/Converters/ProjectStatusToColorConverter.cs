using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using MixCut.Models;

namespace MixCut.Views.Converters;

/// <summary>把项目状态映射为侧边栏状态点颜色。</summary>
public sealed class ProjectStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value is ProjectStatus s ? s : ProjectStatus.Created;
        var color = status switch
        {
            ProjectStatus.Importing or ProjectStatus.Analyzing or ProjectStatus.Generating
                => Color.FromRgb(0xF0, 0xAD, 0x4E),
            ProjectStatus.Ready => Color.FromRgb(0x1D, 0x6B, 0xE5),
            ProjectStatus.Completed => Color.FromRgb(0x2E, 0x8B, 0x57),
            ProjectStatus.Archived => Color.FromRgb(0xBB, 0xBB, 0xBB),
            _ => Color.FromRgb(0xCC, 0xCC, 0xCC),
        };
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
