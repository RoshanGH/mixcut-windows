using System.Globalization;
using System.Text;

namespace MixCut.Services.Dubbing;

/// <summary>单分镜中间片的 ffmpeg 滤镜图。对应 mac DubSegmentGraph。</summary>
public sealed record DubSegmentGraph(string FilterComplex, string VideoMapLabel, string AudioMapLabel);

/// <summary>
/// 构建「切片 → 9:16 标准化 → 遮挡旧字幕 → 叠新字幕 → 换音轨(克隆配音+BGM混音)」滤镜图。
/// 对应 mac DubSegmentGraphBuilder。input 0=源视频；字幕 PNG=captionInputIndex；配音 m4a=dubAudioInputIndex；BGM=bgmInputIndex。
/// </summary>
public static class DubSegmentGraphBuilder
{
    private const string Loudnorm = "loudnorm=I=-16:TP=-1.5:LRA=11";
    private const double SilenceEpsilon = 0.001;
    /// <summary>混音时 BGM 相对音量（人声为主、BGM 垫底）。</summary>
    public const double BgmGain = 0.6;

    public static DubSegmentGraph Build(
        SubtitleMaskMode mode,
        int startFrame, int endFrame, double fps,
        int outputWidth, int outputHeight,
        PixelRect maskPixel,
        (int X, int Y)? captionOrigin,
        int captionInputIndex,
        bool keepOriginalAudio,
        int dubAudioInputIndex,
        int freezePadFrames,
        double trailingSilence,
        int? bgmInputIndex = null)
    {
        int w = outputWidth, h = outputHeight;
        var fpsInt = (int)Math.Round(fps);
        var parts = new List<string>();
        string F(double v) => v.ToString("F5", CultureInfo.InvariantCulture);
        string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

        // 1) 视频：精确切片 + 9:16 标准化 → [base]
        parts.Add(
            $"[0:v]trim=start_frame={startFrame}:end_frame={endFrame},setpts=PTS-STARTPTS," +
            $"scale={w}:{h}:force_original_aspect_ratio=decrease," +
            $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps={fpsInt}[base]");

        // 2) 遮挡旧字幕 → [masked]
        int mx = maskPixel.X, my = maskPixel.Y, mw = maskPixel.Width, mh = maskPixel.Height;
        switch (mode)
        {
            case SubtitleMaskMode.None:
                parts.Add("[base]null[masked]");
                break;
            case SubtitleMaskMode.Blur:
                parts.Add("[base]split=2[mb0][mb1]");
                parts.Add($"[mb1]crop={mw}:{mh}:{mx}:{my},boxblur=20:1[mbb]");
                parts.Add($"[mb0][mbb]overlay={mx}:{my}[masked]");
                break;
            case SubtitleMaskMode.Solid:
                parts.Add($"[base]drawbox=x={mx}:y={my}:w={mw}:h={mh}:color=0x1A1A1A@1.0:t=fill[masked]");
                break;
            default: // Dim（已砍，兼容）
                parts.Add($"[base]drawbox=x={mx}:y={my}:w={mw}:h={mh}:color=0x000000@0.5:t=fill[masked]");
                break;
        }

        // 3) 叠新字幕 PNG → [capped]（锁定段一律不叠）
        var effectiveCaption = keepOriginalAudio ? null : captionOrigin;
        string videoBeforePad;
        if (effectiveCaption is { } origin)
        {
            parts.Add($"[masked][{captionInputIndex}:v]overlay={origin.X}:{origin.Y}[capped]");
            videoBeforePad = "capped";
        }
        else
        {
            videoBeforePad = "masked";
        }

        // 4) 末尾定格补帧（freezePad 恒 0，保留逻辑）→ [vout]
        if (freezePadFrames > 0)
        {
            var dur = freezePadFrames / fps;
            parts.Add($"[{videoBeforePad}]tpad=stop_mode=clone:stop_duration={F3(dur)}[vout]");
        }
        else
        {
            parts.Add($"[{videoBeforePad}]null[vout]");
        }

        // 5) 音频 → [aout]
        if (keepOriginalAudio)
        {
            var aStart = startFrame / fps;
            var aEnd = endFrame / fps;
            parts.Add($"[0:a]atrim=start={F(aStart)}:end={F(aEnd)},asetpts=PTS-STARTPTS,aresample=44100[aout]");
        }
        else
        {
            var padSuffix = trailingSilence > SilenceEpsilon ? $",apad=pad_dur={F3(trailingSilence)}" : "";
            if (bgmInputIndex is { } bgmIdx)
            {
                var aStart = startFrame / fps;
                var aEnd = endFrame / fps;
                parts.Add($"[{dubAudioInputIndex}:a]aresample=44100,{Loudnorm}{padSuffix}[voice]");
                parts.Add(
                    $"[{bgmIdx}:a]atrim=start={F(aStart)}:end={F(aEnd)},asetpts=PTS-STARTPTS,aresample=44100," +
                    $"volume={BgmGain.ToString("F2", CultureInfo.InvariantCulture)}[bgmcut]");
                parts.Add("[voice][bgmcut]amix=inputs=2:duration=first:normalize=0[aout]");
            }
            else
            {
                parts.Add($"[{dubAudioInputIndex}:a]aresample=44100,{Loudnorm}{padSuffix}[aout]");
            }
        }

        return new DubSegmentGraph(string.Join(";", parts), "[vout]", "[aout]");
    }
}
