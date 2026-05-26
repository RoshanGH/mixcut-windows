using System.Diagnostics;
using System.Windows.Media;

namespace MixCut.Infrastructure;

/// <summary>
/// 帧级性能探针。基于 <see cref="CompositionTarget.Rendering"/>（WPF 唯一可靠的渲染时钟）。
/// 仅在 Debug build 启用（Release 调用 <see cref="BeginMeasure"/> 返回 NoOp Disposer）。
///
/// 用法：
///   using (FrameLatencyProbe.BeginMeasure("adjust_start_time")) {
///       ...
///   }
/// 输出格式：[Perf] adjust_start_time: elapsed=12ms / frames=1 / max_frame_gap=8ms
/// </summary>
public static class FrameLatencyProbe
{
    /// <summary>是否启用（Debug build 默认开启，Release build 关闭）。可手动覆盖。</summary>
    public static bool Enabled { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    private static readonly Action<string> DefaultLogger =
        msg => Serilog.Log.Information(msg);

    /// <summary>外部可注入自定义 logger（如直接 Console.WriteLine）。</summary>
    public static Action<string> Logger { get; set; } = DefaultLogger;

    public static IDisposable BeginMeasure(string scenario)
    {
        if (!Enabled)
        {
            return NoOpDisposable.Instance;
        }
        return new Probe(scenario);
    }

    private sealed class Probe : IDisposable
    {
        private readonly string _scenario;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private int _frameCount;
        private long _lastFrameTicks;
        private long _maxFrameGapMs;
        private readonly EventHandler _onRender;
        private bool _disposed;

        public Probe(string scenario)
        {
            _scenario = scenario;
            _lastFrameTicks = _sw.ElapsedTicks;
            _onRender = (_, _) =>
            {
                var now = _sw.ElapsedTicks;
                var gapMs = (now - _lastFrameTicks) * 1000 / Stopwatch.Frequency;
                if (gapMs > _maxFrameGapMs) _maxFrameGapMs = gapMs;
                _lastFrameTicks = now;
                _frameCount++;
            };
            CompositionTarget.Rendering += _onRender;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CompositionTarget.Rendering -= _onRender;
            _sw.Stop();
            try
            {
                Logger($"[Perf] {_scenario}: elapsed={_sw.ElapsedMilliseconds}ms / frames={_frameCount} / max_frame_gap={_maxFrameGapMs}ms");
            }
            catch { /* 不让性能日志的失败破坏业务流 */ }
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}
