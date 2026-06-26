using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using MixCut.Models;

namespace MixCut.Services.Dubbing;

/// <summary>渲染好的字幕 PNG 信息。</summary>
public sealed record CaptionImage(string Path, int PixelWidth, int PixelHeight);

/// <summary>
/// 字幕渲染（v0.5.0，libass-free）：用 GDI+ 把台词渲染成透明 PNG，再由 ffmpeg overlay 叠到画面。
/// 对应 mac「系统文字渲染成透明 PNG」。<b>按输出像素渲染</b>（不经 DPI 缩放），避免高分屏字糊/错位。
/// </summary>
public static class CaptionRenderer
{
    private const string FontFamily = "Microsoft YaHei"; // 微软雅黑，Win10/11 自带，中文清晰

    /// <summary>
    /// 把字幕渲染到 PNG。<paramref name="canvasWidth"/> = 字幕区宽（≈输出宽×0.9）。
    /// <paramref name="withBackdrop"/>=true 时加半透明黑底（提升可读性；纯色遮挡模式已有底，传 false）。
    /// 返回实际像素尺寸（供布局定位）。
    /// </summary>
    public static CaptionImage RenderToFile(string text, int canvasWidth, bool withBackdrop, string outPath)
    {
        text = (text ?? string.Empty).Trim();
        canvasWidth = Math.Max(2, canvasWidth);

        // 字号按字幕区宽度比例（输出像素），并夹在合理范围。
        var fontSize = Math.Clamp(canvasWidth * 0.052f, 18f, 64f);
        var padding = (int)(fontSize * 0.5f);
        var textMaxWidth = canvasWidth - padding * 2;

        using var font = new Font(FontFamily, fontSize, FontStyle.Bold, GraphicsUnit.Pixel);

        // 先量文本换行后的高度
        var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            FormatFlags = 0, // 允许换行
            Trimming = StringTrimming.None,
        };

        SizeF measured;
        using (var tmp = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(tmp))
        {
            mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            measured = mg.MeasureString(text, font, textMaxWidth, format);
        }

        var textH = (int)Math.Ceiling(measured.Height);
        var canvasHeight = textH + padding * 2;

        using var bmp = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            if (withBackdrop)
            {
                using var backdrop = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                using var path = RoundedRect(new Rectangle(0, 0, canvasWidth, canvasHeight), (int)(fontSize * 0.4f));
                g.FillPath(backdrop, path);
            }

            var layoutRect = new RectangleF(padding, padding, textMaxWidth, textH);

            // 黑色描边（四向偏移）提升任意画面上的可读性
            var outline = Math.Max(1, (int)(fontSize * 0.06f));
            using (var black = new SolidBrush(Color.FromArgb(230, 0, 0, 0)))
            {
                for (var dx = -outline; dx <= outline; dx++)
                for (var dy = -outline; dy <= outline; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    g.DrawString(text, font, black, new RectangleF(layoutRect.X + dx, layoutRect.Y + dy, layoutRect.Width, layoutRect.Height), format);
                }
            }
            using (var white = new SolidBrush(Color.White))
            {
                g.DrawString(text, font, white, layoutRect, format);
            }
        }

        bmp.Save(outPath, ImageFormat.Png);
        return new CaptionImage(outPath, canvasWidth, canvasHeight);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>字幕 PNG 在画面上的叠放位置。对应 mac CaptionLayout。</summary>
public static class CaptionLayout
{
    /// <summary>
    /// 水平居中；纵向：若有遮挡框则与其垂直居中对齐，否则放在画面底部 ~82% 处。返回 overlay 左上角像素坐标。
    /// </summary>
    public static (int X, int Y) OverlayOrigin(int outputWidth, int outputHeight, SubtitleMaskRect maskRect,
        int captionWidth, int captionHeight)
    {
        var x = Math.Max(0, (outputWidth - captionWidth) / 2);

        int y;
        if (maskRect.Width > 0 && maskRect.Height > 0)
        {
            var maskY = (int)Math.Round(maskRect.Y * outputHeight);
            var maskH = (int)Math.Round(maskRect.Height * outputHeight);
            y = maskY + (maskH - captionHeight) / 2;
        }
        else
        {
            y = (int)Math.Round(outputHeight * 0.82) - captionHeight;
        }
        y = Math.Clamp(y, 0, Math.Max(0, outputHeight - captionHeight));
        return (x, y);
    }
}
