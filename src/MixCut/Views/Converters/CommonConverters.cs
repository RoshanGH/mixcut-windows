using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MixCut.Models;

namespace MixCut.Views.Converters;

/// <summary>
/// Stage 0 通用 Converter 集合。所有 IValueConverter 都是无状态 + 线程安全；
/// 推荐在 ResourceDictionary 中以 StaticResource 形式只实例化一次。
/// </summary>

/// <summary>null → Collapsed；非 null → Visible。可用于「有数据才显示」场景。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null || (value is string s && string.IsNullOrEmpty(s));
        var visible = Invert ? isNull : !isNull;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>空集合 → Collapsed；非空 → Visible。</summary>
public sealed class EmptyCollectionToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value switch
        {
            null => true,
            ICollection c => c.Count == 0,
            IEnumerable e => !e.GetEnumerator().MoveNext(),
            _ => false,
        };
        var visible = Invert ? isEmpty : !isEmpty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>取反 bool（用于 IsEnabled / Visibility 等需要反向语义的场景）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>InverseBool + Visibility 一步到位（true → Collapsed）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        return b ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// 质量分 → Brush。阈值与 Mac QualityBadge.badgeColor 严格对齐（9/8/7/&lt;7 → 绿/蓝/橙/红）。
/// 失败时返回 TextTertiaryBrush。
/// </summary>
public sealed class QualityScoreToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double score)
        {
            if (!double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out score))
            {
                return Application.Current.Resources["TextTertiaryBrush"]!;
            }
        }
        var key = score switch
        {
            >= 9.0 => "SuccessGreenBrush",
            >= 8.0 => "AccentBlueBrush",
            >= 7.0 => "WarningOrangeBrush",
            _ => "DangerRedBrush",
        };
        return Application.Current.Resources[key]!;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>SemanticType → Brush（11 种类型）。对齐 Mac SemanticTypeTag.color。</summary>
public sealed class SemanticTypeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SemanticType t)
        {
            return Application.Current.Resources["TextTertiaryBrush"]!;
        }
        var key = t switch
        {
            SemanticType.Hook => "SemHookBrush",
            SemanticType.PainPoint => "SemPainPointBrush",
            SemanticType.Solution => "SemProductBrush",
            SemanticType.Results => "SemResultsBrush",
            SemanticType.SocialProof => "SemSocialProofBrush",
            SemanticType.PriceAnchor => "SemPriceBrush",
            SemanticType.Promotion => "SemPromotionBrush",
            SemanticType.CallToAction => "SemCtaBrush",
            SemanticType.ProductPositioning => "SemPositioningBrush",
            SemanticType.UsageEducation => "SemUsageBrush",
            SemanticType.Transition => "SemTransitionBrush",
            _ => "TextTertiaryBrush",
        };
        return Application.Current.Resources[key]!;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>SemanticType → Brush with alpha（chip 背景用 12%）。</summary>
public sealed class SemanticTypeToTintBrushConverter : IValueConverter
{
    private static readonly Dictionary<SemanticType, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SemanticType t)
        {
            return Brushes.Transparent;
        }
        if (Cache.TryGetValue(t, out var cached)) return cached;

        var key = t switch
        {
            SemanticType.Hook => "SemHookBrush",
            SemanticType.PainPoint => "SemPainPointBrush",
            SemanticType.Solution => "SemProductBrush",
            SemanticType.Results => "SemResultsBrush",
            SemanticType.SocialProof => "SemSocialProofBrush",
            SemanticType.PriceAnchor => "SemPriceBrush",
            SemanticType.Promotion => "SemPromotionBrush",
            SemanticType.CallToAction => "SemCtaBrush",
            SemanticType.ProductPositioning => "SemPositioningBrush",
            SemanticType.UsageEducation => "SemUsageBrush",
            SemanticType.Transition => "SemTransitionBrush",
            _ => "TextTertiaryBrush",
        };
        if (Application.Current.Resources[key] is SolidColorBrush src)
        {
            var tinted = new SolidColorBrush(Color.FromArgb(0x28, src.Color.R, src.Color.G, src.Color.B));
            tinted.Freeze();
            Cache[t] = tinted;
            return tinted;
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>double 秒数 → "10.8s"。可用 parameter 控制小数位数（默认 1）。</summary>
public sealed class DurationToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double seconds) return "—";
        var digits = 1;
        if (parameter is string p && int.TryParse(p, out var d)) digits = d;
        return seconds.ToString($"F{digits}", CultureInfo.InvariantCulture) + "s";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>bool → Visibility。Visible / Collapsed。可设 Invert=true 反转。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var v = value is bool b && b;
        if (Invert) v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var v = value is Visibility vis && vis == Visibility.Visible;
        return Invert ? !v : v;
    }
}

/// <summary>选中状态 → CardBorder Brush（选中蓝 / 否则默认灰）。</summary>
public sealed class SelectedToBorderBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var selected = value is bool b && b;
        var key = selected ? "AccentBlueBrush" : "BorderPrimaryBrush";
        return Application.Current.Resources[key]!;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>选中状态 → Card Background Brush（选中淡蓝 / 否则白）。</summary>
public sealed class SelectedToBackgroundBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var selected = value is bool b && b;
        var key = selected ? "AccentBlueAlpha10Brush" : "BgPrimaryBrush";
        return Application.Current.Resources[key]!;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>SemanticType → 中文 label。</summary>
public sealed class SemanticTypeToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SemanticType t ? t.ToLabel() : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>PositionType → 中文 label。</summary>
public sealed class PositionTypeToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PositionType t ? t.ToLabel() : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
