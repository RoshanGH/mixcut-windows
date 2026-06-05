using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MixCut.Infrastructure;
using MixCut.Services.VideoProcessing;
using NAudio.Wave;

namespace MixCut.Views.Components;

/// <summary>
/// 进程外 ffmpeg.exe 裸帧播放器（替代 WPF MediaElement）。
///
/// 出发点：CLAUDE.md §兼容性总纲 —— 预览用我们自带的 ffmpeg.exe 解码，与导出同源、
/// 彻底不碰系统编解码器（消灭 0xC00D109B）。ffmpeg 把视频解成 bgra 裸帧经 stdout 管道
/// 流出，后台线程读帧写入 <see cref="WriteableBitmap"/>，UI 线程只做 WritePixels。
///
/// 本版（Task 2/3）：仅视频，墙钟节流，支持暂停/恢复/seek。音频 + 音频主时钟同步在后续 Task 加。
/// 防卡死：进程/管道 IO 全在后台线程；子进程挂 <see cref="ChildProcessTracker"/> 防孤儿；
/// Stop 必 kill 整棵进程树。
/// 暂停=不再读帧 → OS 管道背压自然让 ffmpeg 阻塞；seek=用新起点重启 ffmpeg（帧泵无法随机定位）。
/// </summary>
public class FfmpegFramePlayer : Image
{
    private Process? _proc;
    private Thread? _reader;
    private WriteableBitmap? _bmp;
    // 音频：独立 ffmpeg PCM 管道 → NAudio 声卡输出（硬件自然定速，与视频各自跟真实时间）
    private Process? _audioProc;
    private Thread? _audioReader;
    private IWavePlayer? _audioOut;
    private BufferedWaveProvider? _audioBuf;
    private volatile bool _stop;
    private volatile bool _paused;
    private int _w, _h;
    private readonly int _fps = 30;
    private string? _path;
    private double _clipStart;     // 片段在源文件中的起点（秒）
    private double _clipEndAbs;     // 片段在源文件中的终点（绝对秒）；<=0 表示播到 EOF（完整视频）
    private double _positionSec;    // 当前绝对位置（源文件时间，秒）= _clipStart + 已播

    /// <summary>首帧就绪（对应 MediaElement.MediaOpened）。</summary>
    public event EventHandler? Opened;
    /// <summary>正常播放到结尾（对应 MediaElement.MediaEnded）。</summary>
    public event EventHandler? Ended;
    /// <summary>解码/播放失败（对应 MediaElement.MediaFailed），参数为人话可读原因。</summary>
    public event EventHandler<string>? Failed;

    /// <summary>当前绝对播放位置（源文件时间）。对齐 MediaElement.Position 语义。</summary>
    public TimeSpan Position => TimeSpan.FromSeconds(_positionSec);

    /// <summary>是否正在播放（子进程存活）。</summary>
    public bool IsPlaying => _proc is { HasExited: false };

    /// <summary>是否暂停中。</summary>
    public bool IsPaused => _paused;

    /// <summary>已渲染帧数（诊断/自测用，UI 线程累加）。</summary>
    public long FramesRendered { get; private set; }

    public FfmpegFramePlayer()
    {
        Stretch = Stretch.Uniform;
        // 控件卸载时务必停掉子进程，防止泄漏/孤儿
        Unloaded += (_, _) => Stop();
    }

    /// <summary>
    /// 开始播放 <paramref name="path"/> 从 <paramref name="start"/> 起 <paramref name="dur"/> 秒
    /// （dur&lt;=0 播到 EOF）。若已在播会先 Stop。
    /// </summary>
    public void Open(string path, double start, double dur)
    {
        Stop();
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || !BundledBinaries.FfmpegAvailable)
        {
            Failed?.Invoke(this, "找不到视频文件或解码器");
            return;
        }

        // 目标像素尺寸：按控件实际尺寸 × DPI，取偶数，给合理下限/上限
        var dpi = VisualTreeHelper.GetDpi(this);
        _w = ClampEven((int)Math.Round(Math.Max(ActualWidth, 160) * dpi.DpiScaleX), 16, 1280);
        _h = ClampEven((int)Math.Round(Math.Max(ActualHeight, 90) * dpi.DpiScaleY), 16, 1280);
        _path = path;
        _clipStart = start;
        _clipEndAbs = dur > 0 ? start + dur : 0;
        _positionSec = start;
        _stop = false;
        _paused = false;
        FramesRendered = 0;

        try
        {
            var psi = new ProcessStartInfo(BundledBinaries.Ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in FramePipeArgs.Video(path, start, dur <= 0 ? 36000 : dur, _w, _h, _fps))
            {
                psi.ArgumentList.Add(a);
            }
            _proc = Process.Start(psi)!;
            ChildProcessTracker.AddProcess(_proc); // 铁律：防孤儿
            _proc.BeginErrorReadLine();            // 丢弃 stderr，避免管道缓冲写满阻塞子进程

            _bmp = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgra32, null);
            Source = _bmp;

            var proc = _proc;
            _reader = new Thread(() => ReadLoop(proc)) { IsBackground = true, Name = "FfmpegFramePump" };
            _reader.Start();

            StartAudio(path, start, dur); // 音频失败不影响视频
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[InlinePlayDiag] 启动 ffmpeg 解码失败 path={Path}", path);
            Failed?.Invoke(this, "无法启动解码");
            Stop();
        }
    }

    /// <summary>启动音频管道 → NAudio 声卡输出（声卡按 44.1kHz 自然定速）。无音轨视频会静默，不影响视频。</summary>
    private void StartAudio(string path, double start, double dur)
    {
        try
        {
            var psi = new ProcessStartInfo(BundledBinaries.Ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in FramePipeArgs.Audio(path, start, dur <= 0 ? 36000 : dur))
            {
                psi.ArgumentList.Add(a);
            }
            _audioProc = Process.Start(psi)!;
            ChildProcessTracker.AddProcess(_audioProc);
            _audioProc.BeginErrorReadLine();

            _audioBuf = new BufferedWaveProvider(new WaveFormat(44100, 16, 2))
            {
                BufferDuration = TimeSpan.FromSeconds(12),
                DiscardOnBufferOverflow = true,
            };
            _audioOut = new WaveOutEvent();
            _audioOut.Init(_audioBuf);
            _audioOut.Play();

            var ap = _audioProc;
            _audioReader = new Thread(() => AudioReadLoop(ap)) { IsBackground = true, Name = "FfmpegAudioPump" };
            _audioReader.Start();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[InlinePlayDiag] 音频启动失败，仅视频播放");
        }
    }

    /// <summary>后台读 PCM → 喂 NAudio 缓冲（带背压，防内存涨）。</summary>
    private void AudioReadLoop(Process proc)
    {
        try
        {
            var stream = proc.StandardOutput.BaseStream;
            var buf = new byte[16384];
            while (!_stop)
            {
                if (_paused)
                {
                    Thread.Sleep(20);
                    continue;
                }
                var n = stream.Read(buf, 0, buf.Length);
                if (n <= 0)
                {
                    break; // EOF / 无音轨
                }
                // 背压：缓冲过满则等，避免长视频把 PCM 全堆进内存
                while (!_stop && _audioBuf is not null && _audioBuf.BufferedDuration.TotalSeconds > 3)
                {
                    Thread.Sleep(10);
                }
                _audioBuf?.AddSamples(buf, 0, n);
            }
        }
        catch (Exception)
        {
            // 停止时管道被关闭抛异常，属正常
        }
    }

    /// <summary>暂停：停止读帧（OS 管道背压让视频 ffmpeg 阻塞）+ 暂停声卡输出。</summary>
    public void Pause()
    {
        _paused = true;
        try { _audioOut?.Pause(); } catch (Exception) { }
    }

    /// <summary>恢复读帧 + 声卡输出。</summary>
    public void Resume()
    {
        _paused = false;
        try { _audioOut?.Play(); } catch (Exception) { }
    }

    /// <summary>seek 到绝对位置：帧泵无法随机定位，用新起点重启 ffmpeg（保持原片段终点）。</summary>
    public void Seek(double absSec)
    {
        if (_path is null)
        {
            return;
        }
        var dur = _clipEndAbs > 0 ? Math.Max(0.1, _clipEndAbs - absSec) : 0;
        Open(_path, absSec, dur);
    }

    /// <summary>后台读帧循环：按定长切帧、暂停感知墙钟节流、UI 线程 WritePixels。</summary>
    private void ReadLoop(Process proc)
    {
        var frameBytes = FramePipeArgs.FrameBytes(_w, _h);
        var stream = proc.StandardOutput.BaseStream;
        var sw = Stopwatch.StartNew();
        double pausedAccumMs = 0;
        long frameIndex = 0;
        var opened = false;

        try
        {
            while (!_stop)
            {
                // 暂停：停读 → ffmpeg 背压阻塞；记录暂停时长以校正节流
                if (_paused)
                {
                    var pStart = sw.Elapsed.TotalMilliseconds;
                    while (_paused && !_stop)
                    {
                        Thread.Sleep(20);
                    }
                    pausedAccumMs += sw.Elapsed.TotalMilliseconds - pStart;
                }
                if (_stop)
                {
                    break;
                }

                var buf = new byte[frameBytes];
                if (!ReadFull(stream, buf, frameBytes))
                {
                    break; // EOF / 不足一帧 → 正常结束
                }

                // 墙钟节流（扣除暂停时长）：第 N 帧应在 N/fps 秒呈现
                var targetMs = frameIndex * 1000.0 / _fps;
                var delay = targetMs - (sw.Elapsed.TotalMilliseconds - pausedAccumMs);
                if (delay > 1 && !_stop)
                {
                    Thread.Sleep((int)Math.Min(delay, 200));
                }
                if (_stop)
                {
                    break;
                }

                var absSec = _clipStart + frameIndex / (double)_fps;
                var first = !opened;
                Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    if (_stop || _bmp is null)
                    {
                        return;
                    }
                    _bmp.WritePixels(new Int32Rect(0, 0, _w, _h), buf, _w * 4, 0);
                    _positionSec = absSec;
                    FramesRendered++;
                    if (first)
                    {
                        Opened?.Invoke(this, EventArgs.Empty);
                    }
                });
                opened = true;
                frameIndex++;
            }

            if (!_stop)
            {
                Dispatcher.BeginInvoke(() => Ended?.Invoke(this, EventArgs.Empty));
            }
        }
        catch (Exception ex)
        {
            if (!_stop)
            {
                Serilog.Log.Warning(ex, "[InlinePlayDiag] 读帧异常");
                Dispatcher.BeginInvoke(() => Failed?.Invoke(this, "预览解码中断"));
            }
        }
    }

    /// <summary>停止播放：kill 子进程树、停读帧线程、释放。</summary>
    public void Stop()
    {
        _stop = true;
        _paused = false;
        var proc = _proc;
        _proc = null;
        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception) { /* 已退出 */ }
            try { proc.Dispose(); } catch (Exception) { }
        }
        var reader = _reader;
        _reader = null;
        if (reader is not null && reader.IsAlive && reader.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            reader.Join(TimeSpan.FromMilliseconds(500));
        }

        // 音频清理
        var ap = _audioProc;
        _audioProc = null;
        if (ap is not null)
        {
            try { if (!ap.HasExited) { ap.Kill(entireProcessTree: true); } } catch (Exception) { }
            try { ap.Dispose(); } catch (Exception) { }
        }
        try { _audioOut?.Stop(); _audioOut?.Dispose(); } catch (Exception) { }
        _audioOut = null;
        _audioBuf = null;
        var areader = _audioReader;
        _audioReader = null;
        if (areader is not null && areader.IsAlive && areader.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            areader.Join(TimeSpan.FromMilliseconds(500));
        }
    }

    /// <summary>从流读满 <paramref name="count"/> 字节；流结束返回 false。</summary>
    private static bool ReadFull(Stream s, byte[] buf, int count)
    {
        var off = 0;
        while (off < count)
        {
            var n = s.Read(buf, off, count - off);
            if (n <= 0)
            {
                return false;
            }
            off += n;
        }
        return true;
    }

    private static int ClampEven(int v, int min, int max)
    {
        v = Math.Clamp(v, min, max);
        return v % 2 == 0 ? v : v - 1;
    }
}
