using Microsoft.Playwright;

namespace ePriTrackerBackend.Services
{
    /// <summary>
    /// Thay cho SemaphoreSlim(1,1) + ThrottleAsync cũ.
    /// - Cho phép tối đa N trang chạy song song (maxParallel).
    /// - Chỉ giãn cách thời điểm BẮT ĐẦU request (minIntervalMs + jitter), không bắt request sau chờ request trước xong.
    /// - AcquireExclusiveAsync lấy toàn bộ slot (dùng cho đăng nhập / xác minh captcha / đóng trình duyệt).
    /// </summary>
    internal sealed class ScrapeScheduler : IDisposable
    {
        private readonly SemaphoreSlim _slots;
        private readonly int _maxParallel;
        private readonly int _minIntervalMs;
        private readonly int _jitterMs;
        private readonly object _lock = new();
        private long _nextStartAt;

        public ScrapeScheduler(int maxParallel, int minIntervalMs, int jitterMs)
        {
            _maxParallel = Math.Max(1, maxParallel);
            _minIntervalMs = Math.Max(0, minIntervalMs);
            _jitterMs = Math.Max(0, jitterMs);
            _slots = new SemaphoreSlim(_maxParallel, _maxParallel);
        }

        public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
        {
            await _slots.WaitAsync(ct);
            try
            {
                var delay = ReserveStartDelay();
                if (delay > 0) await Task.Delay(delay, ct);
            }
            catch
            {
                _slots.Release();
                throw;
            }
            return new Lease(_slots, 1);
        }

        public async Task<IDisposable> AcquireExclusiveAsync(CancellationToken ct = default)
        {
            var acquired = 0;
            try
            {
                for (; acquired < _maxParallel; acquired++) await _slots.WaitAsync(ct);
            }
            catch
            {
                if (acquired > 0) _slots.Release(acquired);
                throw;
            }
            return new Lease(_slots, _maxParallel);
        }

        private int ReserveStartDelay()
        {
            lock (_lock)
            {
                var now = Environment.TickCount64;
                var start = Math.Max(now, _nextStartAt);
                _nextStartAt = start + _minIntervalMs + (_jitterMs > 0 ? Random.Shared.Next(0, _jitterMs + 1) : 0);
                return (int)(start - now);
            }
        }

        public void Dispose() => _slots.Dispose();

        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim? _sem;
            private readonly int _count;

            public Lease(SemaphoreSlim sem, int count)
            {
                _sem = sem;
                _count = count;
            }

            public void Dispose()
            {
                var sem = Interlocked.Exchange(ref _sem, null);
                if (sem == null) return;
                try { sem.Release(_count); } catch (ObjectDisposedException) { }
            }
        }
    }

    internal static class BrowserTuning
    {
        private static readonly string[] TrackerParts =
        {
            "google-analytics.com", "googletagmanager.com", "doubleclick.net",
            "connect.facebook.net", "hotjar.com", "clarity.ms"
        };

        /// <summary>Chặn ảnh / media / font / tracker để trang tải nhanh hơn (chỉ cần JSON, JSON-LD, text).</summary>
        public static Task BlockHeavyResourcesAsync(IBrowserContext context)
            => context.RouteAsync("**/*", async route =>
            {
                try
                {
                    var req = route.Request;
                    var type = req.ResourceType;
                    if (type is "image" or "media" or "font" || IsTracker(req.Url))
                        await route.AbortAsync();
                    else
                        await route.ContinueAsync();
                }
                catch
                {
                    // request đã được xử lý hoặc trang đã đóng
                }
            });

        private static bool IsTracker(string url)
        {
            foreach (var part in TrackerParts)
                if (url.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Lỗi cho thấy trình duyệt/context đã chết → mới cần khởi động lại; lỗi khác chỉ cần thử lại bằng trang mới.</summary>
        public static bool IsBrowserDead(Exception ex)
        {
            if (ex is ObjectDisposedException) return true;
            if (ex is not PlaywrightException) return false;

            var m = ex.Message;
            return m.Contains("has been closed", StringComparison.OrdinalIgnoreCase)
                   || m.Contains("Target closed", StringComparison.OrdinalIgnoreCase)
                   || m.Contains("Browser closed", StringComparison.OrdinalIgnoreCase)
                   || m.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
                   || m.Contains("Connection closed", StringComparison.OrdinalIgnoreCase);
        }
    }
}