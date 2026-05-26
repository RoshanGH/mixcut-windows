using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MixCut.Infrastructure;

/// <summary>
/// 全局缩略图缓存。对应 macOS 版 ThumbnailCache（NSCache）。
///
/// 设计：MemoryCache 风格的 LRU，超出容量自动淘汰最旧条目。
/// 单例，多视图共享，避免反复从磁盘加载同一图片导致 UI 卡顿。
/// </summary>
public sealed class ThumbnailCache
{
    public static ThumbnailCache Shared { get; } = new();

    private readonly object _gate = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, (LinkedListNode<string> Node, BitmapImage Image)> _store = new();

    /// <summary>全局并发读盘限制 = 4。避免 31+ 张图同时抢盘导致全部慢。</summary>
    private readonly System.Threading.SemaphoreSlim _diskGate = new(4, 4);

    /// <summary>同 path 加载去重：第一次 LoadAsync 创建 TCS，后续重复调用 await 同一个。</summary>
    private readonly Dictionary<string, Task<ImageSource?>> _inflight = new();

    /// <summary>某张图加载完成后触发，参数是 path。CardVM 订阅这个事件刷新 binding。</summary>
    public event Action<string>? ImageLoaded;

    /// <summary>最多缓存张数。</summary>
    private const int CountLimit = 300;

    private ThumbnailCache() { }

    /// <summary>
    /// 异步加载：cache 命中立即返回；未命中走 SemaphoreSlim 限制的并发队列，加载完触发 ImageLoaded 事件。
    /// 同一 path 并发调用会被去重（共享同一 inflight Task）。
    /// </summary>
    public Task<ImageSource?> LoadAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return Task.FromResult<ImageSource?>(null);

        // 已 cached
        var hit = PeekImage(path);
        if (hit is not null) return Task.FromResult<ImageSource?>(hit);

        // inflight 去重
        lock (_inflight)
        {
            if (_inflight.TryGetValue(path, out var existing)) return existing;
            var t = LoadAsyncCore(path);
            _inflight[path] = t;
            t.ContinueWith(_ =>
            {
                lock (_inflight) _inflight.Remove(path);
            }, TaskScheduler.Default);
            return t;
        }
    }

    private async Task<ImageSource?> LoadAsyncCore(string path)
    {
        await _diskGate.WaitAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var img = await Task.Run(() => GetImage(path));
            sw.Stop();
            var name = System.IO.Path.GetFileName(path);
            if (img is null)
            {
                Serilog.Log.Warning("[ThumbCache] load FAIL {Name} ({Ms}ms)", name, sw.ElapsedMilliseconds);
            }
            else
            {
                Serilog.Log.Information("[ThumbCache] load OK {Name} ({Ms}ms)", name, sw.ElapsedMilliseconds);
            }
            // 关键：无论加载成功失败都触发事件，让等待的 CardVM 至少能停止等
            try { ImageLoaded?.Invoke(path); } catch { /* listener 异常不阻塞 */ }
            return img;
        }
        finally
        {
            _diskGate.Release();
        }
    }

    /// <summary>非阻塞查询：cache 有就返回，没有立刻返回 null（不读盘）。用于 binding 防 UI 卡顿。</summary>
    public ImageSource? PeekImage(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (_gate)
        {
            if (_store.TryGetValue(path, out var hit))
            {
                _lru.Remove(hit.Node);
                _lru.AddFirst(hit.Node);
                return hit.Image;
            }
        }
        return null;
    }

    /// <summary>
    /// 同步取缩略图：已缓存就直接返回，未缓存就读磁盘 + 缓存。
    /// 加载失败返回 null。
    /// </summary>
    public ImageSource? GetImage(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        lock (_gate)
        {
            if (_store.TryGetValue(path, out var hit))
            {
                // 触达：把节点挪到链表头（最近使用）。
                _lru.Remove(hit.Node);
                _lru.AddFirst(hit.Node);
                return hit.Image;
            }
        }

        if (!File.Exists(path)) return null;

        try
        {
            // 读盘走 OnLoad，避免后续访问失败；Freeze 让跨线程使用安全。
            using var stream = File.OpenRead(path);
            var bm = new BitmapImage();
            bm.BeginInit();
            bm.CacheOption = BitmapCacheOption.OnLoad;
            bm.StreamSource = stream;
            bm.EndInit();
            bm.Freeze();

            lock (_gate)
            {
                if (_store.TryGetValue(path, out var concurrent))
                {
                    // 另一个线程已经加载了：用它的避免重复缓存
                    _lru.Remove(concurrent.Node);
                    _lru.AddFirst(concurrent.Node);
                    return concurrent.Image;
                }

                var node = new LinkedListNode<string>(path);
                _lru.AddFirst(node);
                _store[path] = (node, bm);

                // 超出容量：淘汰链表尾（最久未用）。
                while (_lru.Count > CountLimit)
                {
                    var tail = _lru.Last;
                    if (tail is null) break;
                    _lru.RemoveLast();
                    _store.Remove(tail.Value);
                }
            }
            return bm;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>预热：后台异步预加载一批路径，加速首屏渲染。</summary>
    public void Prewarm(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        Task.Run(() =>
        {
            foreach (var p in list)
            {
                _ = GetImage(p);
            }
        });
    }

    /// <summary>异步预热，返回 Task 让调用方可 await。用于"等缩略图都进 cache 再渲染卡片"避免首帧黑屏。</summary>
    public Task PrewarmAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return Task.CompletedTask;
        return Task.Run(() =>
        {
            foreach (var p in list)
            {
                _ = GetImage(p);
            }
        });
    }

    public void Clear()
    {
        lock (_gate)
        {
            _store.Clear();
            _lru.Clear();
        }
    }
}
