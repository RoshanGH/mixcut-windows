using System.Globalization;
using System.Windows.Data;
using MixCut.Models;

namespace MixCut.Views.Converters;

/// <summary>把项目状态枚举值转换为中文标签（用于 ToolTip 等场景）。</summary>
public sealed class ProjectStatusToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ProjectStatus status)
        {
            return status.ToLabel();
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
