//using System.Collections.Concurrent;
//using System.Diagnostics;
//using System.Globalization;
//using System.Text.Json;
//using System.Text.RegularExpressions;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Options;
//using Microsoft.Playwright;
//using ePriTrackerBackend.Models.DTOs;
//using ePriTrackerBackend.Models.Entities;

//namespace ePriTrackerBackend.Services
//{
//    public enum ShopeeBrowserMode
//    {
//        RealChromeCdp,
//        PlaywrightChromium
//    }

//    public class ShopeeBrowserOptions
//    {
//        public ShopeeBrowserMode Mode { get; set; } = ShopeeBrowserMode.RealChromeCdp;
//        public bool Headless { get; set; } = false;
//        public bool HideWindow { get; set; } = true;
//        public string? BrowserExecutablePath { get; set; }
//        public int CdpPort { get; set; } = 9333;
//        public string ChromeProfileDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), ".shopee-chrome");
//        public string UserDataDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), ".shopee-profile");
//        public string? Channel { get; set; }
//        public string? DebugDirectory { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "logs", "shopee-debug");
//        public int NavigationTimeoutMs { get; set; } = 30_000;

//        /// <summary>Tổng thời gian chờ API sau khi đã "động chuột" (tính cho mỗi lượt, có 2 lượt).</summary>
//        public int ApiWaitTimeoutMs { get; set; } = 15_000;

//        /// <summary>Đường nhanh: chờ API ngay sau khi vào trang, chưa humanize. Hết thời gian này mới humanize rồi chờ tiếp.</summary>
//        public int QuickApiWaitMs { get; set; } = 4_000;

//        public int DomWaitTimeoutMs { get; set; } = 8_000;

//        /// <summary>Thời gian tối đa chờ mục "MÔ TẢ SẢN PHẨM" render sau khi API trả về.</summary>
//        public int DescriptionWaitMs { get; set; } = 3_000;

//        public int PollIntervalMs { get; set; } = 100;

//        /// <summary>Số trang chạy song song. Shopee nhạy với bot nên mặc định thấp; tăng dần và theo dõi lỗi 90309999.</summary>
//        public int MaxParallelPages { get; set; } = 2;

//        /// <summary>Khoảng cách tối thiểu giữa 2 lần BẮT ĐẦU request (không phải giữa 2 request hoàn tất).</summary>
//        public int MinRequestIntervalMs { get; set; } = 1_000;
//        public int RequestJitterMs { get; set; } = 500;

//        /// <summary>
//        /// Chặn ảnh/media/font/tracker bằng route interception. LƯU Ý: Playwright TẮT HTTP cache khi có route,
//        /// nên mỗi trang phải tải lại toàn bộ JS bundle của Shopee (chậm hơn). Mặc định false; dùng DisableImages thay thế.
//        /// </summary>
//        public bool BlockHeavyResources { get; set; } = false;

//        /// <summary>
//        /// Tắt tải ảnh bằng cờ Chrome (--blink-settings=imagesEnabled=false), KHÔNG dùng route nên vẫn giữ HTTP cache.
//        /// Cửa sổ đăng nhập luôn bật ảnh để bạn giải captcha được.
//        /// </summary>
//        public bool DisableImages { get; set; } = true;

//        /// <summary>false (mặc định): chỉ warm-up (mở trang chủ) khi profile mới tinh. true: luôn warm-up như bản cũ.</summary>
//        public bool AlwaysWarmUp { get; set; } = false;

//        public int MaxAttempts { get; set; } = 2;
//        public bool AutoLoginOnBlocked { get; set; } = true;
//        public int LoginTimeoutMinutes { get; set; } = 5;

//        /// <summary>
//        /// Tìm kiếm của Shopee gần như luôn trả lỗi 90309999 nếu chưa đăng nhập. true: chưa có phiên đăng nhập thì báo ngay
//        /// (và mở cửa sổ đăng nhập) thay vì mất ~40s chờ rồi thất bại. Nếu Shopee đổi tên cookie khiến luôn bị hỏi đăng nhập thì đặt false.
//        /// </summary>
//        public bool RequireLoginForSearch { get; set; } = true;

//        /// <summary>Sau khi bị gắn cờ bot, các request tiếp theo thất bại ngay trong khoảng này (tránh dồn dập làm Shopee khóa lâu hơn). 0 = tắt.</summary>
//        public int AntiBotCooldownSeconds { get; set; } = 20;

//        /// <summary>Cache kết quả tìm kiếm theo keyword (phút). 0 = tắt. Chỉ cache kết quả không rỗng.</summary>
//        public int SearchCacheMinutes { get; set; } = 10;

//        /// <summary>Số keyword (dài → ngắn) thử tối đa cho mỗi sản phẩm.</summary>
//        public int MaxKeywordAttempts { get; set; } = 3;

//        /// <summary>Khi API tìm kiếm trả danh sách rỗng, chờ DOM xác nhận trong khoảng này trước khi coi là "thật sự không có kết quả".</summary>
//        public int EmptyResultConfirmMs { get; set; } = 1_500;
//    }

//    public class ShopeeBlockedException : Exception
//    {
//        public string? FinalUrl { get; }
//        public ShopeeBlockedException(string message, string? finalUrl) : base(message)
//        {
//            FinalUrl = finalUrl;
//        }
//    }

//    public class ShopeeAntiBotException : Exception
//    {
//        public long ErrorCode { get; }
//        public ShopeeAntiBotException(long errorCode)
//            : base($"Shopee từ chối request lấy dữ liệu (error {errorCode}). Trình duyệt hoặc IP đang bị gắn cờ bot")
//        {
//            ErrorCode = errorCode;
//        }
//    }

//    public class ShopeeBrowserService : IEcommerceService, IAsyncDisposable
//    {
//        public string PlatformName => "Shopee";

//        private const string ShopeeBaseUrl = "https://shopee.vn";
//        private const string ShopeeLoginUrl = "https://shopee.vn/buyer/login";
//        private const string ImageCdn = "https://down-vn.img.susercontent.com/file/";

//        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

//        private readonly ILogger<ShopeeBrowserService> _logger;
//        private readonly ShopeeBrowserOptions _options;
//        private readonly ScrapeScheduler _scheduler;
//        private readonly SemaphoreSlim _ctxLock = new(1, 1);

//        private IPlaywright? _playwright;
//        private IBrowser? _cdpBrowser;
//        private Process? _chromeProcess;
//        private IBrowserContext? _context;
//        private bool _usingCdp;
//        private int _generation;
//        private int _loginInProgress;
//        private bool _disposed;

//        // Circuit breaker: sau khi bị gắn cờ bot thì tạm nghỉ thay vì tiếp tục dồn request.
//        private long _blockedUntil;
//        private long _lastAntiBotCode;

//        // Cache + gộp các lần tìm kiếm trùng keyword đang chạy đồng thời.
//        private readonly ConcurrentDictionary<string, (long ExpireAt, List<SuggestionCandidate> Items)> _searchCache = new(StringComparer.OrdinalIgnoreCase);
//        private readonly ConcurrentDictionary<string, Lazy<Task<List<SuggestionCandidate>>>> _searchInFlight = new(StringComparer.OrdinalIgnoreCase);

//        private string CdpEndpoint => $"http://127.0.0.1:{_options.CdpPort}";

//        private static readonly Regex IdRegexDotted = new(@"i\.(\d+)\.(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
//        private static readonly Regex IdRegexPath = new(@"/product/(\d+)/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

//        private sealed record ShopeeIds(string ShopId, string ItemId);
//        private sealed record ScrapedProduct(string Name, decimal Price, string ImageUrl, string Description = "");
//        private sealed record SuggestionCandidate(string Name, decimal Price, string ImageUrl, string Link);

//        /// <summary>Trạng thái thu thập dùng chung giữa handler response và luồng chính của một lần cào.</summary>
//        private sealed class CaptureState
//        {
//            public long AntiBot;       // error code chưa xử lý (0 = không có)
//            public long LastAntiBot;   // error code đã ghi nhận ở lượt trước
//            public int EmptySeen;      // 1 = API tìm kiếm vừa trả danh sách rỗng
//            public string? LastBody;
//            public readonly ConcurrentQueue<string> ApiLog = new();
//        }

//        private const string MinimalInitScript = "Object.defineProperty(navigator, 'webdriver', { get: () => undefined });";

//        private const string DomExtractorJs = """
//            () => {
//                const result = { name: '', price: 0, imageUrl: '', description: '', source: '' };
//                const meta = (sel) => (document.querySelector(sel)?.content || '').trim();
//                let ldDescription = '';

//                for (const s of document.querySelectorAll('script[type="application/ld+json"]')) {
//                    try {
//                        const data = JSON.parse(s.textContent);
//                        const list = Array.isArray(data) ? data : [data];
//                        for (const d of list) {
//                            if (!d) continue;
//                            const t = d['@type'];
//                            const isProduct = t === 'Product' || (Array.isArray(t) && t.includes('Product'));
//                            if (!isProduct) continue;
//                            if (d.name) result.name = d.name;
//                            if (d.description && !ldDescription) ldDescription = String(d.description);
//                            const img = Array.isArray(d.image) ? d.image[0] : d.image;
//                            if (img) result.imageUrl = img;
//                            let offers = d.offers;
//                            if (Array.isArray(offers)) offers = offers[0];
//                            const n = parseFloat(offers && (offers.price ?? offers.lowPrice));
//                            if (n > 0) { result.price = n; result.source = 'jsonld'; }
//                        }
//                    } catch (e) { }
//                }

//                if (!(result.price > 0)) {
//                    const re = /^₫?\s*\d{1,3}(?:\.\d{3})*(?:\s*-\s*₫?\s*\d{1,3}(?:\.\d{3})*)?$/;
//                    const isPrice = (el) => {
//                        const t = (el.innerText || '').trim();
//                        return t.length > 0 && t.length < 40 && t.includes('₫') && re.test(t);
//                    };
//                    const all = Array.from(document.body.querySelectorAll('*')).filter(isPrice);
//                    const deepest = all.filter(el => !Array.from(el.children).some(isPrice));

//                    let best = null, bestSize = 0;
//                    for (const el of deepest) {
//                        if (el.offsetParent === null && getComputedStyle(el).position !== 'fixed') continue;

//                        let struck = false, p = el;
//                        for (let i = 0; i < 3 && p; i++, p = p.parentElement) {
//                            const st = getComputedStyle(p);
//                            if ((st.textDecorationLine || st.textDecoration || '').includes('line-through')) { struck = true; break; }
//                        }
//                        if (struck) continue;

//                        const size = parseFloat(getComputedStyle(el).fontSize) || 0;
//                        if (size > bestSize) { best = el; bestSize = size; }
//                    }
//                    if (best) {
//                        const first = best.innerText.split('-')[0];
//                        const n = parseInt(first.replace(/[^\d]/g, ''), 10);
//                        if (n > 0) { result.price = n; result.source = 'dom'; }
//                    }
//                }

//                if (!result.name) {
//                    const h1 = document.querySelector('h1');
//                    if (h1) result.name = h1.textContent.trim();
//                }
//                if (!result.name) result.name = meta('meta[property="og:title"]');
//                if (!result.imageUrl) result.imageUrl = meta('meta[property="og:image"]');

//                // Mô tả: ưu tiên mục "MÔ TẢ SẢN PHẨM" trên trang > JSON-LD > meta (thường chỉ là đoạn tóm tắt)
//                try {
//                    const norm = (s) => (s || '').normalize('NFC').trim().toLowerCase();
//                    const head = Array.from(document.querySelectorAll('h1, h2, h3, h4, div, span'))
//                        .find(e => e.children.length === 0 && norm(e.textContent) === 'mô tả sản phẩm');
//                    if (head) {
//                        const box = head.nextElementSibling || (head.parentElement ? head.parentElement.nextElementSibling : null);
//                        const t = ((box && box.innerText) || '').trim();
//                        if (t.length > 0) result.description = t;
//                    }
//                } catch (e) { }
//                if (!result.description) result.description = ldDescription;
//                if (!result.description) result.description = meta('meta[property="og:description"]') || meta('meta[name="description"]');

//                return result;
//            }
//            """;

//        // Bản nhẹ chỉ đọc mục "MÔ TẢ SẢN PHẨM" — dùng để poll nhanh (không quét toàn bộ DOM để tìm giá như DomExtractorJs).
//        private const string DescriptionOnlyJs = """
//            () => {
//                const norm = (s) => (s || '').normalize('NFC').trim().toLowerCase();
//                const head = Array.from(document.querySelectorAll('h1, h2, h3, h4, div, span'))
//                    .find(e => e.children.length === 0 && norm(e.textContent) === 'mô tả sản phẩm');
//                if (!head) return '';
//                const box = head.nextElementSibling || (head.parentElement ? head.parentElement.nextElementSibling : null);
//                return ((box && box.innerText) || '').trim();
//            }
//            """;

//        // Fallback đọc danh sách kết quả tìm kiếm từ DOM khi không bắt được API search_items.
//        private const string SearchExtractorJs = """
//            () => {
//                const items = [];
//                const seen = new Set();
//                const idRe = /(?:-i\.|\/product\/)(\d+)[.\/](\d+)/;
//                const re = /^₫?\s*\d{1,3}(?:\.\d{3})*(?:\s*-\s*₫?\s*\d{1,3}(?:\.\d{3})*)?$/;
//                const isPrice = (el) => {
//                    const t = (el.innerText || '').trim();
//                    return t.length > 0 && t.length < 40 && t.includes('₫') && re.test(t);
//                };
//                const isStruck = (el, root) => {
//                    let p = el;
//                    for (let i = 0; i < 3 && p; i++, p = p.parentElement) {
//                        const st = getComputedStyle(p);
//                        if ((st.textDecorationLine || st.textDecoration || '').includes('line-through')) return true;
//                        if (p === root) break;
//                    }
//                    return false;
//                };

//                for (const a of document.querySelectorAll('a[href*="-i."], a[href*="/product/"]')) {
//                    const m = (a.getAttribute('href') || '').match(idRe);
//                    if (!m || seen.has(m[2])) continue;

//                    const all = Array.from(a.querySelectorAll('*')).filter(isPrice);
//                    const leaves = all.filter(el => !Array.from(el.children).some(isPrice));
//                    const priceEl = leaves.find(el => !isStruck(el, a));
//                    if (!priceEl) continue;

//                    const n = parseInt(priceEl.innerText.split('-')[0].replace(/[^\d]/g, ''), 10);
//                    if (!(n > 0)) continue;

//                    const img = a.querySelector('img');
//                    const lines = (a.innerText || '').split('\n').map(s => s.trim()).filter(s => s && !s.includes('₫'));
//                    lines.sort((x, y) => y.length - x.length);
//                    const name = (img && img.getAttribute('alt')) || lines[0] || '';

//                    seen.add(m[2]);
//                    items.push({ name: name, price: n, imageUrl: img ? (img.getAttribute('src') || '') : '', shopId: m[1], itemId: m[2] });
//                    if (items.length >= 30) break;
//                }
//                return items;
//            }
//            """;

//        // Phân biệt "tìm không ra thật" với "bị soft-block trả danh sách rỗng".
//        private const string NoResultJs = """
//            () => {
//                const t = (document.body ? document.body.innerText : '').normalize('NFC').toLowerCase();
//                return t.includes('không tìm thấy kết quả') || t.includes('không có kết quả') || t.includes('no results found');
//            }
//            """;

//        public ShopeeBrowserService(ILogger<ShopeeBrowserService> logger, IOptions<ShopeeBrowserOptions>? options = null)
//        {
//            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
//            _options = options?.Value ?? new ShopeeBrowserOptions();
//            _scheduler = new ScrapeScheduler(_options.MaxParallelPages, _options.MinRequestIntervalMs, _options.RequestJitterMs);
//        }

//        public bool CanHandle(string url) => url.Contains("shopee.vn", StringComparison.OrdinalIgnoreCase);

//        // ============================== Public API ==============================

//        public async Task<Product> FetchProductDataAsync(string shopIdAndItemId, string normalizedLink)
//        {
//            _ = shopIdAndItemId;
//            var scraped = await ScrapeWithRetryAsync(normalizedLink, includeDetails: true);
//            var ids = TryParseIds(normalizedLink);

//            return new Product
//            {
//                ProductLink = normalizedLink,
//                ProductName = string.IsNullOrWhiteSpace(scraped.Name) ? $"Shopee {ids?.ItemId ?? "product"}" : scraped.Name,
//                ImageURL = scraped.ImageUrl,
//                Description = scraped.Description,
//                InitialPrice = scraped.Price,
//                LatestPrice = scraped.Price,
//                AddedAt = DateTime.UtcNow,
//                LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
//            };
//        }

//        public async Task<decimal?> FetchCurrentPriceAsync(string productLink)
//        {
//            try
//            {
//                // Cập nhật giá không cần description → includeDetails=false.
//                var scraped = await ScrapeWithRetryAsync(productLink, includeDetails: false);
//                return scraped.Price > 0 ? scraped.Price : null;
//            }
//            catch (ShopeeBlockedException ex)
//            {
//                _logger.LogWarning("[Shopee] Bị chặn / cần đăng nhập khi cập nhật giá {Link}: {Msg}", productLink, ex.Message);
//                return null;
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning(ex, "[Shopee] Không thể cập nhật giá cho {Link}", productLink);
//                return null;
//            }
//        }

//        public async Task<List<SuggestionProduct>> CrawlSuggestionsAsync(Guid productId, string searchKeyword, decimal initialPrice)
//        {
//            var result = new List<SuggestionProduct>();
//            if (string.IsNullOrWhiteSpace(searchKeyword) || initialPrice <= 0) return result;

//            try
//            {
//                var keywords = ScraperTextHelper.BuildKeywordCandidates(searchKeyword).Take(Math.Max(1, _options.MaxKeywordAttempts));

//                foreach (var keyword in keywords)
//                {
//                    var found = await SearchCachedAsync(keyword);

//                    var kept = found
//                        .Where(c => ScraperTextHelper.IsAcceptableSuggestionPrice(c.Price, initialPrice))
//                        .Take(ScraperTextHelper.MaxSuggestions)
//                        .ToList();

//                    _logger.LogInformation("[Shopee] Gợi ý q='{Keyword}': {Total} kết quả, {Kept} hợp lệ (giá gốc {Price})", keyword, found.Count, kept.Count, initialPrice);
//                    if (kept.Count == 0) continue;

//                    foreach (var c in kept)
//                    {
//                        result.Add(new SuggestionProduct
//                        {
//                            SuggestionProductId = Guid.NewGuid(),
//                            ProductId = productId,
//                            ProductName = c.Name,
//                            Price = c.Price,
//                            ImageURL = c.ImageUrl,
//                            ProductLink = c.Link,
//                            LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
//                        });
//                    }
//                    break;
//                }
//            }
//            catch (ShopeeBlockedException ex)
//            {
//                _logger.LogWarning("[Shopee] Bị chặn / cần đăng nhập khi tìm gợi ý: {Msg}", ex.Message);
//            }
//            catch (ShopeeAntiBotException ex)
//            {
//                _logger.LogWarning("[Shopee] Bị gắn cờ bot khi tìm gợi ý: {Msg}", ex.Message);
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning(ex, "[Shopee] Không lấy được gợi ý cho productId {Id}", productId);
//            }

//            return result;
//        }

//        public Task<List<LiveEventProductDTO>> GetLiveProductsFromEventAsync(string eventLink)
//            => Task.FromResult(new List<LiveEventProductDTO>());

//        public async Task InteractiveLoginAsync(TimeSpan? timeout = null, CancellationToken ct = default, string? targetUrl = null)
//        {
//            // Lấy toàn bộ slot: chờ các trang đang cào xong rồi mới đóng/mở lại trình duyệt để đăng nhập.
//            using var exclusive = await _scheduler.AcquireExclusiveAsync(ct);
//            await LoginCoreAsync(timeout ?? TimeSpan.FromMinutes(_options.LoginTimeoutMinutes), targetUrl, ct);

//            // Đã đăng nhập xong → xoá trạng thái "nghỉ" để lần cào kế tiếp chạy ngay.
//            Interlocked.Exchange(ref _blockedUntil, 0);
//        }

//        // ============================== Tìm kiếm: cache + gộp request ==============================

//        private async Task<List<SuggestionCandidate>> SearchCachedAsync(string keyword)
//        {
//            var key = keyword.Trim();
//            var ttlMs = Math.Max(0, _options.SearchCacheMinutes) * 60_000L;

//            if (ttlMs > 0 && _searchCache.TryGetValue(key, out var hit) && hit.ExpireAt > Environment.TickCount64)
//                return hit.Items;

//            // Nhiều sản phẩm cùng keyword chạy đồng thời → chỉ cào một lần.
//            var lazy = _searchInFlight.GetOrAdd(key, k => new Lazy<Task<List<SuggestionCandidate>>>(
//                () => RunScrapeAsync(ctx => ScrapeSearchOnceAsync(ctx, k))));

//            try
//            {
//                var items = await lazy.Value;

//                if (ttlMs > 0 && items.Count > 0)
//                {
//                    _searchCache[key] = (Environment.TickCount64 + ttlMs, items);
//                    if (_searchCache.Count > 300) PruneSearchCache();
//                }

//                return items;
//            }
//            finally
//            {
//                _searchInFlight.TryRemove(new KeyValuePair<string, Lazy<Task<List<SuggestionCandidate>>>>(key, lazy));
//            }
//        }

//        private void PruneSearchCache()
//        {
//            var now = Environment.TickCount64;
//            foreach (var kv in _searchCache)
//            {
//                if (kv.Value.ExpireAt <= now) _searchCache.TryRemove(kv.Key, out _);
//            }
//        }

//        // ============================== Đăng nhập ==============================

//        private async Task LoginCoreAsync(TimeSpan wait, string? targetUrl, CancellationToken ct)
//        {
//            await CloseContextAsync();
//            if (_options.Mode == ShopeeBrowserMode.RealChromeCdp) await ShutdownChromeAsync();

//            var ctx = await LaunchBrowserContextAsync(forLogin: true);
//            try
//            {
//                // Nếu bị chặn bởi trang xác minh/captcha (không phải trang login) thì mở thẳng trang đó để bạn giải.
//                var target = ShopeeLoginUrl;
//                if (!string.IsNullOrWhiteSpace(targetUrl)
//                    && IsBlockedUrl(targetUrl)
//                    && !targetUrl.Contains("/buyer/login", StringComparison.OrdinalIgnoreCase)
//                    && Uri.TryCreate(targetUrl, UriKind.Absolute, out var u)
//                    && u.Host.EndsWith("shopee.vn", StringComparison.OrdinalIgnoreCase))
//                {
//                    target = targetUrl;
//                }

//                var page = await ctx.NewPageAsync();
//                await page.GotoAsync(target, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = _options.NavigationTimeoutMs });
//                _logger.LogInformation("[Shopee] Hãy đăng nhập / xác minh trong cửa sổ Chrome vừa mở (tối đa {Min} phút)...", wait.TotalMinutes);
//                await page.WaitForURLAsync(url => !IsBlockedUrl(url), new PageWaitForURLOptions { Timeout = (float)wait.TotalMilliseconds });
//                await Task.Delay(3000, ct);

//                if (await IsLoggedInAsync(ctx))
//                    _logger.LogInformation("[Shopee] Đăng nhập xong, phiên đã được lưu.");
//                else
//                    _logger.LogWarning("[Shopee] Đã rời trang đăng nhập nhưng chưa thấy cookie phiên. Nếu bị hỏi đăng nhập lặp lại, hãy đặt RequireLoginForSearch=false.");
//            }
//            finally
//            {
//                if (_usingCdp) await ShutdownChromeAsync();
//                else { try { await ctx.CloseAsync(); } catch { } }
//                _usingCdp = false;
//            }
//        }

//        private void StartBackgroundLogin(string? targetUrl)
//        {
//            if (Interlocked.CompareExchange(ref _loginInProgress, 1, 0) != 0) return;

//            _ = Task.Run(async () =>
//            {
//                try
//                {
//                    await InteractiveLoginAsync(null, default, targetUrl);
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogError(ex, "[Shopee] Không hoàn tất được đăng nhập (máy không có màn hình / hết thời gian?).");
//                }
//                finally
//                {
//                    Interlocked.Exchange(ref _loginInProgress, 0);
//                }
//            });
//        }

//        /// <summary>Phiên đăng nhập Shopee có cookie SPC_EC/SPC_ST/SPC_U khác rỗng và khác "-" (giá trị của khách chưa đăng nhập). Không xác định được → coi như đã đăng nhập.</summary>
//        private static async Task<bool> IsLoggedInAsync(IBrowserContext? ctx)
//        {
//            if (ctx == null) return true;
//            try
//            {
//                var cookies = await ctx.CookiesAsync(new[] { ShopeeBaseUrl });
//                return cookies.Any(c =>
//                    (c.Name == "SPC_EC" || c.Name == "SPC_ST" || c.Name == "SPC_U")
//                    && !string.IsNullOrWhiteSpace(c.Value)
//                    && c.Value != "-");
//            }
//            catch
//            {
//                return true;
//            }
//        }

//        // ============================== Vòng retry / đăng nhập / circuit breaker ==============================

//        private Task<ScrapedProduct> ScrapeWithRetryAsync(string link, bool includeDetails)
//            => RunScrapeAsync(ctx => ScrapeOnceAsync(ctx, link, includeDetails));

//        private async Task<T> RunScrapeAsync<T>(Func<IBrowserContext, Task<T>> action)
//        {
//            if (_disposed) throw new ObjectDisposedException(nameof(ShopeeBrowserService));

//            var maxAttempts = Math.Max(1, _options.MaxAttempts);
//            for (var attempt = 1; ; attempt++)
//            {
//                var gen = 0;

//                // Đang trong thời gian nghỉ sau khi bị gắn cờ bot → thất bại ngay, không dồn thêm request.
//                if (Environment.TickCount64 < Interlocked.Read(ref _blockedUntil))
//                    throw new ShopeeAntiBotException(Interlocked.Read(ref _lastAntiBotCode));

//                try
//                {
//                    if (Volatile.Read(ref _loginInProgress) == 1)
//                        throw new ShopeeBlockedException("Đang chờ bạn đăng nhập Shopee trong cửa sổ Chrome vừa mở. Đăng nhập xong hãy bấm TRACK lại", null);

//                    // Slot chỉ giữ trong lúc cào; thời gian chờ retry không chiếm slot.
//                    using var lease = await _scheduler.AcquireAsync();
//                    var (ctx, g) = await EnsureContextAsync();
//                    gen = g;
//                    return await action(ctx);
//                }
//                catch (ShopeeBlockedException ex) when (_options.AutoLoginOnBlocked && Volatile.Read(ref _loginInProgress) == 0)
//                {
//                    _logger.LogWarning("[Shopee] Bị chặn ({Msg}) tại {Url} → mở cửa sổ đăng nhập.", ex.Message, ex.FinalUrl);
//                    StartBackgroundLogin(ex.FinalUrl);
//                    throw new ShopeeBlockedException("Shopee yêu cầu đăng nhập. Đã mở cửa sổ Chrome: hãy đăng nhập rồi bấm TRACK lại", ex.FinalUrl);
//                }
//                catch (ShopeeBlockedException) { throw; }
//                catch (ShopeeAntiBotException ex)
//                {
//                    Interlocked.Exchange(ref _lastAntiBotCode, ex.ErrorCode);
//                    Interlocked.Exchange(ref _blockedUntil, Environment.TickCount64 + Math.Max(0, _options.AntiBotCooldownSeconds) * 1000L);

//                    // Bị gắn cờ bot khi chưa đăng nhập → gần như chắc chắn cần đăng nhập, mở cửa sổ luôn.
//                    if (_options.AutoLoginOnBlocked && Volatile.Read(ref _loginInProgress) == 0 && !await IsLoggedInAsync(_context))
//                    {
//                        _logger.LogWarning("[Shopee] Bị gắn cờ bot (error {Code}) khi chưa đăng nhập → mở cửa sổ đăng nhập.", ex.ErrorCode);
//                        StartBackgroundLogin(null);
//                        throw new ShopeeBlockedException("Shopee yêu cầu đăng nhập. Đã mở cửa sổ Chrome: hãy đăng nhập rồi bấm TRACK lại", null);
//                    }

//                    throw;
//                }
//                catch (Exception ex) when (attempt < maxAttempts)
//                {
//                    _logger.LogWarning(ex, "[Shopee] Lần {Attempt}/{Max} thất bại, thử lại...", attempt, maxAttempts);

//                    // Chỉ khởi động lại trình duyệt khi nó thực sự chết.
//                    if (BrowserTuning.IsBrowserDead(ex)) await CloseContextAsync(gen);
//                    await Task.Delay(1000 * attempt);
//                }
//            }
//        }

//        // ============================== Cào sản phẩm ==============================

//        private async Task<ScrapedProduct> ScrapeOnceAsync(IBrowserContext context, string link, bool includeDetails)
//        {
//            var ids = TryParseIds(link);
//            var page = await context.NewPageAsync();

//            var st = new CaptureState();
//            var apiResult = new TaskCompletionSource<ScrapedProduct>(TaskCreationOptions.RunContinuationsAsynchronously);

//            async void OnResponse(object? sender, IResponse response)
//            {
//                try
//                {
//                    var url = response.Url;
//                    if (st.ApiLog.Count < 60 && (url.Contains("/api/v4/", StringComparison.OrdinalIgnoreCase) || url.Contains("/api/v2/", StringComparison.OrdinalIgnoreCase)))
//                    {
//                        st.ApiLog.Enqueue($"{response.Status} {Shorten(url)}");
//                    }

//                    if (!IsCandidateApi(url, ids)) return;
//                    if (response.Status != 200)
//                    {
//                        _logger.LogWarning("[Shopee] API sản phẩm trả HTTP {Status}: {Url}", response.Status, Shorten(url));
//                        return;
//                    }

//                    var text = await response.TextAsync();
//                    st.LastBody = text.Length > 200_000 ? text[..200_000] : text;

//                    try
//                    {
//                        using var doc = JsonDocument.Parse(text);
//                        var code = ReadErrorCode(doc.RootElement);
//                        if (code != 0)
//                        {
//                            Interlocked.Exchange(ref st.AntiBot, code);
//                            _logger.LogWarning("[Shopee] API báo lỗi error={Code}: {Url}", code, Shorten(url));
//                        }

//                        // Endpoint "lạ" (không phải pdp/item) chỉ đọc theo đường dẫn rõ ràng, không quét sâu để tránh lấy nhầm giá sản phẩm khác.
//                        var parsed = ParseApiPayload(doc.RootElement, allowDeepScan: IsStrictProductApi(url));
//                        if (parsed is { Price: > 0 })
//                        {
//                            apiResult.TrySetResult(parsed);
//                        }
//                        else if (code == 0)
//                        {
//                            var snippet = text.Length > 300 ? text[..300] : text;
//                            _logger.LogWarning("[Shopee] API trả JSON nhưng không đọc được giá. Đầu body: {Body}", snippet);
//                        }
//                    }
//                    catch (JsonException)
//                    {
//                        _logger.LogWarning("[Shopee] API không trả về JSON hợp lệ.");
//                    }
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogDebug(ex, "[Shopee] Lỗi khi xử lý response");
//                }
//            }

//            // Gắn vào page (không phải context) để nhiều trang chạy song song không lẫn response của nhau.
//            page.Response += OnResponse;

//            try
//            {
//                if (_options.Headless) await FixHeadlessUserAgentAsync(page);
//                _logger.LogInformation("[Shopee] Bắt đầu cào: {Url}", link);

//                // Commit: trả về ngay khi server bắt đầu phản hồi. Giá nằm trong API (bắt bằng listener) nên không cần chờ DOMContentLoaded.
//                var navResponse = await page.GotoAsync(link, new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = _options.NavigationTimeoutMs });
//                _logger.LogInformation("[Shopee] Status={Status} FinalUrl={Url}", navResponse?.Status, page.Url);

//                if (navResponse is { Status: 403 or 429 })
//                    throw new ShopeeBlockedException($"HTTP {navResponse.Status}", page.Url);

//                for (var pass = 0; pass < 2; pass++)
//                {
//                    if (pass == 1)
//                    {
//                        _logger.LogWarning("[Shopee] Chưa thấy API sản phẩm, reload trang thử lại...");
//                        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Commit, Timeout = _options.NavigationTimeoutMs });
//                    }

//                    // Đường nhanh: chờ API ngay, chưa humanize. Dừng sớm nếu Shopee đã báo lỗi anti-bot.
//                    if (await WaitUntilAsync(page, apiResult.Task, () => Interlocked.Read(ref st.AntiBot) != 0, _options.QuickApiWaitMs))
//                        return await OnApiSuccessAsync(page, apiResult.Task.Result, includeDetails);

//                    if (Interlocked.Read(ref st.AntiBot) != 0)
//                    {
//                        st.LastAntiBot = Interlocked.Exchange(ref st.AntiBot, 0);
//                        await Task.Delay(Random.Shared.Next(800, 1500));
//                        continue; // có lỗi rõ ràng → reload luôn, khỏi mất thời gian humanize
//                    }

//                    // Đường chậm: động chuột/cuộn rồi chờ tiếp
//                    await HumanizeAsync(page);
//                    if (await WaitUntilAsync(page, apiResult.Task, () => Interlocked.Read(ref st.AntiBot) != 0, _options.ApiWaitTimeoutMs))
//                        return await OnApiSuccessAsync(page, apiResult.Task.Result, includeDetails);

//                    if (Interlocked.Read(ref st.AntiBot) != 0)
//                        st.LastAntiBot = Interlocked.Exchange(ref st.AntiBot, 0);
//                }

//                _logger.LogWarning("[Shopee] Không bắt được API sản phẩm, chuyển sang đọc DOM...");
//                if (IsBlockedUrl(page.Url)) throw new ShopeeBlockedException("Shopee yêu cầu đăng nhập / captcha", page.Url);

//                var antiBot = Interlocked.Read(ref st.AntiBot);
//                if (antiBot == 0) antiBot = st.LastAntiBot;

//                // Đã biết bị anti-bot thì DOM gần như chắc chắn rỗng → chỉ thử nhanh, không chờ lâu.
//                var fromDom = await ExtractFromDomAsync(page, antiBot != 0 ? 500 : _options.DomWaitTimeoutMs);
//                if (fromDom is { Price: > 0 })
//                {
//                    _logger.LogInformation("[Shopee] Lấy giá từ DOM: {Price}", fromDom.Price);
//                    return fromDom;
//                }

//                if (antiBot != 0) throw new ShopeeAntiBotException(antiBot);

//                var title = await SafeTitleAsync(page);
//                throw new InvalidOperationException($"Không lấy được giá sản phẩm. FinalUrl={page.Url}, Title='{title}'");
//            }
//            catch (Exception ex)
//            {
//                var tag = ex switch { ShopeeBlockedException => "blocked", ShopeeAntiBotException => "antibot", _ => "failed" };
//                await SaveDebugAsync(page, tag, st.ApiLog, st.LastBody);
//                throw;
//            }
//            finally
//            {
//                page.Response -= OnResponse;
//                try { await page.CloseAsync(); } catch { }
//            }
//        }

//        private async Task<ScrapedProduct> OnApiSuccessAsync(IPage page, ScrapedProduct fromApi, bool includeDetails)
//        {
//            _logger.LogInformation("[Shopee] Lấy giá từ API: {Price}", fromApi.Price);
//            return await FillMissingFromDomAsync(page, fromApi, includeDetails);
//        }

//        // ============================== Gợi ý sản phẩm (tìm kiếm) ==============================

//        private async Task<List<SuggestionCandidate>> ScrapeSearchOnceAsync(IBrowserContext context, string keyword)
//        {
//            // Tìm kiếm Shopee yêu cầu đăng nhập: báo ngay thay vì mất ~40s chờ lỗi 90309999.
//            if (_options.RequireLoginForSearch && !await IsLoggedInAsync(context))
//                throw new ShopeeBlockedException("Chưa đăng nhập Shopee (tìm kiếm yêu cầu đăng nhập)", null);

//            var page = await context.NewPageAsync();

//            var st = new CaptureState();
//            var apiResult = new TaskCompletionSource<List<SuggestionCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);

//            async void OnResponse(object? sender, IResponse response)
//            {
//                try
//                {
//                    var url = response.Url;
//                    if (!url.Contains("search_items", StringComparison.OrdinalIgnoreCase)) return;

//                    if (st.ApiLog.Count < 60) st.ApiLog.Enqueue($"{response.Status} {Shorten(url)}");
//                    if (response.Status != 200)
//                    {
//                        _logger.LogWarning("[Shopee] API tìm kiếm trả HTTP {Status}: {Url}", response.Status, Shorten(url));
//                        return;
//                    }

//                    var text = await response.TextAsync();
//                    st.LastBody = text.Length > 200_000 ? text[..200_000] : text;

//                    try
//                    {
//                        using var doc = JsonDocument.Parse(text);
//                        var code = ReadErrorCode(doc.RootElement);
//                        if (code != 0)
//                        {
//                            Interlocked.Exchange(ref st.AntiBot, code);
//                            _logger.LogWarning("[Shopee] API tìm kiếm báo lỗi error={Code}: {Url}", code, Shorten(url));
//                            return;
//                        }

//                        var parsed = ParseSearchPayload(doc.RootElement);
//                        if (parsed.Count > 0)
//                        {
//                            apiResult.TrySetResult(parsed);
//                        }
//                        else
//                        {
//                            // Rỗng chưa chắc là "không có kết quả": Shopee hay trả rỗng khi soft-block → xác nhận bằng DOM ở luồng chính.
//                            Interlocked.Exchange(ref st.EmptySeen, 1);
//                            _logger.LogInformation("[Shopee] API tìm kiếm trả danh sách rỗng (chờ xác nhận): {Url}", Shorten(url));
//                        }
//                    }
//                    catch (JsonException)
//                    {
//                        _logger.LogWarning("[Shopee] API tìm kiếm không trả về JSON hợp lệ.");
//                    }
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogDebug(ex, "[Shopee] Lỗi khi xử lý response tìm kiếm");
//                }
//            }

//            page.Response += OnResponse;

//            // Chờ API; trả về danh sách nếu có kết quả đáng tin, null nếu chưa có.
//            async Task<List<SuggestionCandidate>?> TryResolveAsync(int waitMs)
//            {
//                var ok = await WaitUntilAsync(page, apiResult.Task,
//                    () => Interlocked.Read(ref st.AntiBot) != 0 || Volatile.Read(ref st.EmptySeen) != 0, waitMs);
//                if (ok) return apiResult.Task.Result;

//                if (Volatile.Read(ref st.EmptySeen) != 0)
//                {
//                    var dom = await ExtractSearchFromDomAsync(page, _options.EmptyResultConfirmMs);
//                    if (dom.Count > 0) return dom;
//                    if (await LooksLikeNoResultAsync(page)) return new List<SuggestionCandidate>();

//                    _logger.LogWarning("[Shopee] API trả rỗng nhưng trang không xác nhận 'không có kết quả' → nghi bị soft-block, thử lại.");
//                    Interlocked.Exchange(ref st.EmptySeen, 0);
//                }

//                return null;
//            }

//            try
//            {
//                if (_options.Headless) await FixHeadlessUserAgentAsync(page);

//                var searchUrl = $"{ShopeeBaseUrl}/search?keyword={Uri.EscapeDataString(keyword)}";
//                _logger.LogInformation("[Shopee] Tìm gợi ý: {Url}", searchUrl);

//                var navResponse = await page.GotoAsync(searchUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = _options.NavigationTimeoutMs });
//                _logger.LogInformation("[Shopee] Tìm kiếm Status={Status} FinalUrl={Url}", navResponse?.Status, page.Url);

//                if (navResponse is { Status: 403 or 429 })
//                    throw new ShopeeBlockedException($"HTTP {navResponse.Status}", page.Url);

//                for (var pass = 0; pass < 2; pass++)
//                {
//                    if (pass == 1)
//                    {
//                        _logger.LogWarning("[Shopee] Chưa có kết quả tìm kiếm đáng tin, reload trang thử lại...");
//                        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Commit, Timeout = _options.NavigationTimeoutMs });
//                    }

//                    var r = await TryResolveAsync(_options.QuickApiWaitMs);
//                    if (r != null) return r;

//                    if (Interlocked.Read(ref st.AntiBot) != 0)
//                    {
//                        st.LastAntiBot = Interlocked.Exchange(ref st.AntiBot, 0);
//                        await Task.Delay(Random.Shared.Next(800, 1500));
//                        continue;
//                    }

//                    await HumanizeAsync(page);
//                    r = await TryResolveAsync(_options.ApiWaitTimeoutMs);
//                    if (r != null) return r;

//                    if (Interlocked.Read(ref st.AntiBot) != 0)
//                        st.LastAntiBot = Interlocked.Exchange(ref st.AntiBot, 0);
//                }

//                _logger.LogWarning("[Shopee] Không bắt được API tìm kiếm, chuyển sang đọc DOM...");
//                if (IsBlockedUrl(page.Url)) throw new ShopeeBlockedException("Shopee yêu cầu đăng nhập / captcha", page.Url);

//                var antiBot = Interlocked.Read(ref st.AntiBot);
//                if (antiBot == 0) antiBot = st.LastAntiBot;

//                var fromDom = await ExtractSearchFromDomAsync(page, antiBot != 0 ? 500 : _options.DomWaitTimeoutMs);
//                if (fromDom.Count > 0) return fromDom;

//                if (antiBot != 0) throw new ShopeeAntiBotException(antiBot);

//                _logger.LogWarning("[Shopee] Không đọc được kết quả tìm kiếm cho '{Keyword}' (FinalUrl={Url})", keyword, page.Url);
//                return new List<SuggestionCandidate>();
//            }
//            catch (Exception ex)
//            {
//                var tag = ex switch { ShopeeBlockedException => "search_blocked", ShopeeAntiBotException => "search_antibot", _ => "search_failed" };
//                await SaveDebugAsync(page, tag, st.ApiLog, st.LastBody);
//                throw;
//            }
//            finally
//            {
//                page.Response -= OnResponse;
//                try { await page.CloseAsync(); } catch { }
//            }
//        }

//        private static readonly string[] ItemContainerNames = { "item_basic", "item_card_displayed_asset", "item_card_display_price", "item_data", "item" };

//        /// <summary>
//        /// Duyệt đệ quy toàn bộ JSON và gom mọi object có (itemid + shopid + tên + giá), bất kể bọc trong items[], data.items[],
//        /// item_basic, item_card_displayed_asset... → chịu được việc Shopee đổi cấu trúc payload.
//        /// </summary>
//        private static List<SuggestionCandidate> ParseSearchPayload(JsonElement root)
//        {
//            var list = new List<SuggestionCandidate>();
//            CollectItems(root, 0, list, new HashSet<string>());
//            return list;
//        }

//        private static void CollectItems(JsonElement el, int depth, List<SuggestionCandidate> list, HashSet<string> seen)
//        {
//            if (depth > 8 || list.Count >= 100) return;

//            switch (el.ValueKind)
//            {
//                case JsonValueKind.Array:
//                    foreach (var x in el.EnumerateArray()) CollectItems(x, depth + 1, list, seen);
//                    break;

//                case JsonValueKind.Object:
//                    if (TryReadSearchItem(el, out var cand))
//                    {
//                        if (seen.Add(cand.Link)) list.Add(cand);
//                        return;
//                    }
//                    foreach (var p in el.EnumerateObject())
//                    {
//                        if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
//                            CollectItems(p.Value, depth + 1, list, seen);
//                    }
//                    break;
//            }
//        }

//        private static bool TryReadSearchItem(JsonElement el, out SuggestionCandidate candidate)
//        {
//            candidate = default!;
//            if (el.ValueKind != JsonValueKind.Object) return false;

//            var boxes = new List<JsonElement>(6);
//            foreach (var n in ItemContainerNames)
//            {
//                if (el.TryGetProperty(n, out var c) && c.ValueKind == JsonValueKind.Object) boxes.Add(c);
//            }
//            boxes.Add(el);

//            var itemId = FirstId(boxes, "itemid", "item_id");
//            var shopId = FirstId(boxes, "shopid", "shop_id");
//            if (itemId.Length == 0 || shopId.Length == 0) return false;

//            var name = CleanName(FirstString(boxes, "name", "title"));
//            var raw = FirstNumber(boxes, "price", "price_min", "single_value", "range_min");
//            if (name.Length == 0 || raw is null) return false;

//            var price = NormalizeApiPrice(raw.Value);
//            if (price <= 0) return false;

//            // Dạng /product/{shopid}/{itemid} được TryParseIds nhận diện lại khi theo dõi giá về sau.
//            var link = $"{ShopeeBaseUrl}/product/{shopId}/{itemId}";
//            candidate = new SuggestionCandidate(name, price, NormalizeImageUrl(FirstString(boxes, "image", "thumbnail")), link);
//            return true;
//        }

//        private static string FirstString(List<JsonElement> boxes, params string[] keys)
//        {
//            foreach (var b in boxes)
//            {
//                foreach (var k in keys)
//                {
//                    if (b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
//                    {
//                        var s = v.GetString();
//                        if (!string.IsNullOrWhiteSpace(s)) return s;
//                    }
//                }
//            }
//            return string.Empty;
//        }

//        private static string FirstId(List<JsonElement> boxes, params string[] keys)
//        {
//            foreach (var b in boxes)
//            {
//                foreach (var k in keys)
//                {
//                    if (!b.TryGetProperty(k, out var v)) continue;
//                    if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
//                    if (v.ValueKind == JsonValueKind.String)
//                    {
//                        var s = v.GetString()?.Trim();
//                        if (!string.IsNullOrEmpty(s) && s.All(char.IsDigit)) return s;
//                    }
//                }
//            }
//            return string.Empty;
//        }

//        private static decimal? FirstNumber(List<JsonElement> boxes, params string[] keys)
//        {
//            foreach (var b in boxes)
//            {
//                foreach (var k in keys)
//                {
//                    if (!b.TryGetProperty(k, out var v)) continue;
//                    if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d > 0) return d;
//                    if (v.ValueKind == JsonValueKind.String
//                        && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) && s > 0) return s;
//                }
//            }
//            return null;
//        }

//        private async Task<List<SuggestionCandidate>> ExtractSearchFromDomAsync(IPage page, int waitMs)
//        {
//            var list = new List<SuggestionCandidate>();

//            if (waitMs > 0)
//            {
//                try
//                {
//                    await page.WaitForSelectorAsync("a[href*='-i.'], a[href*='/product/']", new PageWaitForSelectorOptions { Timeout = waitMs });
//                }
//                catch (PlaywrightException) { }
//            }

//            try
//            {
//                var el = await page.EvaluateAsync<JsonElement>(SearchExtractorJs);
//                if (el.ValueKind != JsonValueKind.Array) return list;

//                var seen = new HashSet<string>();
//                foreach (var it in el.EnumerateArray())
//                {
//                    var name = CleanName(GetString(it, "name"));
//                    var price = Math.Round(GetNumber(it, "price") ?? 0m);
//                    var shopId = GetIdString(it, "shopId");
//                    var itemId = GetIdString(it, "itemId");
//                    if (name.Length == 0 || price <= 0 || shopId.Length == 0 || itemId.Length == 0) continue;

//                    var link = $"{ShopeeBaseUrl}/product/{shopId}/{itemId}";
//                    if (!seen.Add(link)) continue;

//                    list.Add(new SuggestionCandidate(name, price, NormalizeImageUrl(GetString(it, "imageUrl")), link));
//                }
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning("[Shopee] Không thể bóc tách DOM tìm kiếm: {Msg}", ex.Message);
//            }

//            return list;
//        }

//        private static async Task<bool> LooksLikeNoResultAsync(IPage page)
//        {
//            try { return await page.EvaluateAsync<bool>(NoResultJs); }
//            catch (PlaywrightException) { return false; }
//        }

//        // ============================== Chờ / humanize / UA ==============================

//        /// <summary>
//        /// Chờ task hoàn tất. Trả về true nếu hoàn tất thành công; false nếu hết giờ hoặc điều kiện abort bật (vd. đã có lỗi anti-bot).
//        /// Ném ShopeeBlockedException nếu bị đẩy sang trang đăng nhập/captcha.
//        /// </summary>
//        private async Task<bool> WaitUntilAsync(IPage page, Task done, Func<bool>? abort, int timeoutMs)
//        {
//            var sw = Stopwatch.StartNew();
//            while (sw.ElapsedMilliseconds < timeoutMs)
//            {
//                if (done.IsCompleted) return done.IsCompletedSuccessfully;
//                if (abort?.Invoke() == true) return false;
//                if (IsBlockedUrl(page.Url)) throw new ShopeeBlockedException("Shopee yêu cầu đăng nhập / captcha", page.Url);
//                await Task.WhenAny(done, Task.Delay(_options.PollIntervalMs));
//            }
//            return done.IsCompletedSuccessfully;
//        }

//        private static async Task HumanizeAsync(IPage page)
//        {
//            try
//            {
//                var rnd = Random.Shared;
//                await Task.Delay(rnd.Next(300, 800));
//                for (var i = 0; i < 3; i++)
//                {
//                    await page.Mouse.MoveAsync(rnd.Next(200, 1100), rnd.Next(150, 600), new MouseMoveOptions { Steps = rnd.Next(8, 20) });
//                }
//                await page.Mouse.WheelAsync(0, rnd.Next(250, 600));
//                await Task.Delay(rnd.Next(200, 500));
//            }
//            catch { }
//        }

//        private async Task FixHeadlessUserAgentAsync(IPage page)
//        {
//            try
//            {
//                var ua = await page.EvaluateAsync<string>("() => navigator.userAgent");
//                if (string.IsNullOrEmpty(ua) || !ua.Contains("HeadlessChrome", StringComparison.Ordinal)) return;

//                var cdp = await page.Context.NewCDPSessionAsync(page);
//                await cdp.SendAsync("Network.setUserAgentOverride", new Dictionary<string, object>
//                {
//                    ["userAgent"] = ua.Replace("HeadlessChrome", "Chrome"),
//                    ["acceptLanguage"] = "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7"
//                });
//            }
//            catch (Exception ex)
//            {
//                _logger.LogDebug(ex, "[Shopee] Không đổi được UA headless");
//            }
//        }

//        // ============================== Đọc DOM / mô tả ==============================

//        private async Task<ScrapedProduct> FillMissingFromDomAsync(IPage page, ScrapedProduct fromApi, bool includeDetails)
//        {
//            var needDescription = includeDetails && string.IsNullOrWhiteSpace(fromApi.Description);
//            var needBasics = string.IsNullOrWhiteSpace(fromApi.Name) || string.IsNullOrWhiteSpace(fromApi.ImageUrl);
//            if (!needBasics && !needDescription) return fromApi;

//            try
//            {
//                var result = fromApi;
//                ScrapedProduct? dom = null;

//                if (needBasics)
//                {
//                    dom = await ExtractFromDomAsync(page, _options.DomWaitTimeoutMs);
//                    if (dom != null)
//                    {
//                        result = result with
//                        {
//                            Name = string.IsNullOrWhiteSpace(result.Name) ? dom.Name : result.Name,
//                            ImageUrl = string.IsNullOrWhiteSpace(result.ImageUrl) ? dom.ImageUrl : result.ImageUrl
//                        };
//                    }
//                }

//                if (needDescription)
//                {
//                    // Poll mục "MÔ TẢ SẢN PHẨM" (cuộn xuống để nó render) thay cho Delay cố định.
//                    var description = await WaitForDescriptionAsync(page);
//                    if (description.Length == 0)
//                    {
//                        // Không có mục mô tả → thử JSON-LD/meta qua bộ bóc tách đầy đủ
//                        dom ??= await ExtractFromDomAsync(page, _options.DomWaitTimeoutMs);
//                        description = dom?.Description ?? string.Empty;
//                    }
//                    result = result with { Description = description };
//                }

//                return result;
//            }
//            catch { return fromApi; }
//        }

//        private async Task<string> WaitForDescriptionAsync(IPage page)
//        {
//            var sw = Stopwatch.StartNew();
//            for (var i = 0; ; i++)
//            {
//                if (i % 4 == 0)
//                {
//                    try { await page.Mouse.WheelAsync(0, 1200); } catch { }
//                }

//                try
//                {
//                    var raw = await page.EvaluateAsync<string>(DescriptionOnlyJs);
//                    if (!string.IsNullOrWhiteSpace(raw)) return ScraperTextHelper.ToPlainText(raw);
//                }
//                catch (PlaywrightException) { }

//                if (sw.ElapsedMilliseconds >= _options.DescriptionWaitMs) return string.Empty;
//                await Task.Delay(250);
//            }
//        }

//        private async Task<ScrapedProduct?> ExtractFromDomAsync(IPage page, int waitMs)
//        {
//            if (waitMs > 0)
//            {
//                try
//                {
//                    await page.WaitForFunctionAsync("() => document.body && document.body.innerText.includes('₫')", null, new PageWaitForFunctionOptions { Timeout = waitMs });
//                }
//                catch (PlaywrightException) { }
//            }

//            try
//            {
//                var el = await page.EvaluateAsync<JsonElement>(DomExtractorJs);
//                var name = CleanName(GetString(el, "name"));
//                var price = GetNumber(el, "price") ?? 0m;
//                var image = NormalizeImageUrl(GetString(el, "imageUrl"));
//                var description = ScraperTextHelper.ToPlainText(GetString(el, "description"));
//                return new ScrapedProduct(name, Math.Round(price), image, description);
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning("[Shopee] Không thể bóc tách DOM: {Msg}", ex.Message);
//                return null;
//            }
//        }

//        // ============================== Parse JSON ==============================

//        private static bool IsCandidateApi(string url, ShopeeIds? ids)
//        {
//            if (!url.Contains("/api/", StringComparison.OrdinalIgnoreCase)) return false;
//            if (ids is not null) return url.Contains(ids.ItemId, StringComparison.Ordinal);
//            return IsStrictProductApi(url);
//        }

//        private static bool IsStrictProductApi(string url)
//            => url.Contains("/pdp/", StringComparison.OrdinalIgnoreCase) || url.Contains("/item/get", StringComparison.OrdinalIgnoreCase);

//        private static long ReadErrorCode(JsonElement root)
//            => root.ValueKind == JsonValueKind.Object
//               && root.TryGetProperty("error", out var err)
//               && err.ValueKind == JsonValueKind.Number
//               && err.TryGetInt64(out var code)
//                ? code
//                : 0;

//        private static ScrapedProduct? ParseApiPayload(JsonElement root, bool allowDeepScan)
//        {
//            if (root.ValueKind != JsonValueKind.Object || Navigate(root, "data") is not { ValueKind: JsonValueKind.Object } data) return null;

//            var name = CleanName(GetString(root, "data.item.title", "data.item.name", "data.title", "data.name"));
//            var raw = GetNumber(root, "data.product_price.price.single_value", "data.product_price.price.range_min", "data.item.price", "data.item.price_min", "data.price", "data.price_min");
//            if (raw is null && allowDeepScan) raw = FindPriceDeep(data);

//            var price = raw.HasValue ? NormalizeApiPrice(raw.Value) : 0m;
//            var image = NormalizeImageUrl(GetString(root, "data.item.image", "data.image"));
//            var description = ScraperTextHelper.ToPlainText(GetString(root, "data.item.description", "data.description", "data.item.desc"));

//            return new ScrapedProduct(name, price, image, description);
//        }

//        private static decimal? FindPriceDeep(JsonElement data)
//        {
//            var keys = new[] { "single_value", "price", "price_min", "range_min" };
//            var queue = new Queue<(JsonElement El, int Depth)>();
//            queue.Enqueue((data, 0));

//            while (queue.Count > 0)
//            {
//                var (el, depth) = queue.Dequeue();
//                if (depth > 6) continue;

//                if (el.ValueKind == JsonValueKind.Object)
//                {
//                    foreach (var key in keys)
//                    {
//                        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d >= 100_000_000m)
//                            return d;
//                    }

//                    foreach (var prop in el.EnumerateObject())
//                    {
//                        if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) queue.Enqueue((prop.Value, depth + 1));
//                    }
//                }
//                else if (el.ValueKind == JsonValueKind.Array)
//                {
//                    foreach (var item in el.EnumerateArray().Take(5))
//                    {
//                        if (item.ValueKind == JsonValueKind.Object) queue.Enqueue((item, depth + 1));
//                    }
//                }
//            }
//            return null;
//        }

//        private static decimal NormalizeApiPrice(decimal raw) => Math.Round(raw >= 100_000_000m ? raw / 100_000m : raw);

//        private static string NormalizeImageUrl(string value)
//        {
//            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
//            value = value.Trim();
//            if (value.StartsWith("//")) return "https:" + value;
//            if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return value;
//            return ImageCdn + value;
//        }

//        private static string CleanName(string name)
//        {
//            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
//            name = name.Trim();

//            if (name.StartsWith("Shopee Việt Nam", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Đăng nhập", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Login", StringComparison.OrdinalIgnoreCase)) return string.Empty;
//            return name;
//        }

//        private static JsonElement? Navigate(JsonElement root, string dottedPath)
//        {
//            var cur = root;
//            foreach (var segment in dottedPath.Split('.'))
//            {
//                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(segment, out var next)) return null;
//                cur = next;
//            }
//            return cur;
//        }

//        private static string GetString(JsonElement root, params string[] paths)
//        {
//            foreach (var path in paths)
//            {
//                if (Navigate(root, path) is { ValueKind: JsonValueKind.String } el)
//                {
//                    var s = el.GetString();
//                    if (!string.IsNullOrWhiteSpace(s)) return s;
//                }
//            }
//            return string.Empty;
//        }

//        private static string GetIdString(JsonElement root, params string[] paths)
//        {
//            foreach (var path in paths)
//            {
//                if (Navigate(root, path) is not { } el) continue;
//                if (el.ValueKind == JsonValueKind.Number) return el.GetRawText();
//                if (el.ValueKind == JsonValueKind.String)
//                {
//                    var s = el.GetString();
//                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
//                }
//            }
//            return string.Empty;
//        }

//        private static decimal? GetNumber(JsonElement root, params string[] paths)
//        {
//            foreach (var path in paths)
//            {
//                if (Navigate(root, path) is not { } el) continue;
//                if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d) && d > 0) return d;
//                if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) && s > 0) return s;
//            }
//            return null;
//        }

//        // ============================== Quản lý trình duyệt ==============================

//        private async Task<(IBrowserContext Ctx, int Gen)> EnsureContextAsync()
//        {
//            if (_context is { } existing) return (existing, _generation);

//            await _ctxLock.WaitAsync();
//            try
//            {
//                if (_context is { } again) return (again, _generation);

//                var fresh = IsFreshProfile();
//                var ctx = await LaunchBrowserContextAsync(forLogin: false);

//                if (_options.BlockHeavyResources)
//                    await BrowserTuning.BlockHeavyResourcesAsync(ctx);

//                _context = ctx;
//                _generation++;

//                // Bỏ warm-up (mở trang chủ + humanize) nếu profile đã có cookie/phiên đăng nhập từ lần chạy trước.
//                if (fresh || _options.AlwaysWarmUp) await WarmUpAsync(ctx);
//                return (ctx, _generation);
//            }
//            finally
//            {
//                _ctxLock.Release();
//            }
//        }

//        private bool IsFreshProfile()
//        {
//            try
//            {
//                var useCdp = _options.Mode == ShopeeBrowserMode.RealChromeCdp && ResolveBrowserExecutable() != null;
//                var dir = useCdp ? _options.ChromeProfileDir : _options.UserDataDir;
//                return !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();
//            }
//            catch { return true; }
//        }

//        private async Task<IBrowserContext> LaunchBrowserContextAsync(bool forLogin)
//        {
//            if (_options.Mode == ShopeeBrowserMode.RealChromeCdp)
//            {
//                var exe = ResolveBrowserExecutable();
//                if (exe != null) return await LaunchViaCdpAsync(exe, visible: forLogin || !_options.HideWindow, forLogin);
//                _logger.LogWarning("[Shopee] Không tìm thấy Chrome/Edge → dùng Chromium của Playwright.");
//            }
//            return await LaunchViaPlaywrightAsync(headless: !forLogin && _options.Headless, disableImages: !forLogin && _options.DisableImages);
//        }

//        private async Task<IBrowserContext> LaunchViaCdpAsync(string exe, bool visible, bool forLogin)
//        {
//            if (!await IsCdpAliveAsync())
//            {
//                Directory.CreateDirectory(_options.ChromeProfileDir);
//                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
//                psi.ArgumentList.Add($"--remote-debugging-port={_options.CdpPort}");
//                psi.ArgumentList.Add($"--user-data-dir={_options.ChromeProfileDir}");
//                psi.ArgumentList.Add("--no-first-run");
//                psi.ArgumentList.Add("--no-default-browser-check");
//                psi.ArgumentList.Add("--lang=vi-VN");
//                psi.ArgumentList.Add("--window-size=1366,768");

//                // navigator.webdriver = false ngay từ gốc (không cần vá bằng script, vốn dễ bị phát hiện hơn).
//                psi.ArgumentList.Add("--disable-blink-features=AutomationControlled");

//                // Cửa sổ đặt ngoài màn hình / bị che sẽ bị Chrome coi là "occluded" và throttle timer, rAF, IntersectionObserver
//                // → SPA của Shopee render/gọi API chậm hoặc không chạy. Tắt các cơ chế này để trang chạy đều như đang hiển thị.
//                psi.ArgumentList.Add("--disable-features=CalculateNativeWinOcclusion,Translate");
//                psi.ArgumentList.Add("--disable-renderer-backgrounding");
//                psi.ArgumentList.Add("--disable-backgrounding-occluded-windows");
//                psi.ArgumentList.Add("--disable-background-timer-throttling");

//                // Tắt ảnh bằng cờ (không dùng route nên giữ nguyên HTTP cache). Cửa sổ đăng nhập luôn bật ảnh để giải captcha.
//                if (!forLogin && _options.DisableImages) psi.ArgumentList.Add("--blink-settings=imagesEnabled=false");

//                if (!visible) psi.ArgumentList.Add("--window-position=-2400,-2400");
//                if (_options.Headless && !visible) psi.ArgumentList.Add("--headless=new");

//                _logger.LogInformation("[Shopee] Mở trình duyệt thật: {Exe}", exe);
//                _chromeProcess = Process.Start(psi) ?? throw new InvalidOperationException("Không khởi chạy được Chrome/Edge");

//                // Poll 200ms để vào việc ngay khi cổng CDP mở.
//                var ready = false;
//                for (var i = 0; i < 100 && !ready; i++)
//                {
//                    ready = await IsCdpAliveAsync();
//                    if (ready) break;
//                    if (_chromeProcess.HasExited) throw new InvalidOperationException("Chrome thoát ngay sau khi mở.");
//                    await Task.Delay(200);
//                }
//                if (!ready) throw new InvalidOperationException($"Chrome không mở cổng CDP {_options.CdpPort} trong 20 giây");
//            }

//            _playwright ??= await Playwright.CreateAsync();
//            _cdpBrowser = await _playwright.Chromium.ConnectOverCDPAsync(CdpEndpoint);
//            _usingCdp = true;
//            _cdpBrowser.Disconnected += (_, _) => { _context = null; _cdpBrowser = null; };
//            return _cdpBrowser.Contexts.FirstOrDefault() ?? await _cdpBrowser.NewContextAsync();
//        }

//        private async Task<bool> IsCdpAliveAsync()
//        {
//            try
//            {
//                using var resp = await Http.GetAsync($"{CdpEndpoint}/json/version");
//                return resp.IsSuccessStatusCode;
//            }
//            catch { return false; }
//        }

//        private async Task ShutdownChromeAsync()
//        {
//            try
//            {
//                if (_cdpBrowser == null && await IsCdpAliveAsync())
//                {
//                    _playwright ??= await Playwright.CreateAsync();
//                    _cdpBrowser = await _playwright.Chromium.ConnectOverCDPAsync(CdpEndpoint);
//                }

//                if (_cdpBrowser != null)
//                {
//                    try { var session = await _cdpBrowser.NewBrowserCDPSessionAsync(); await session.SendAsync("Browser.close"); } catch { }
//                }
//            }
//            catch { }

//            _cdpBrowser = null; _context = null;
//            var proc = _chromeProcess; _chromeProcess = null;

//            if (proc != null)
//            {
//                try { var exited = await Task.Run(() => proc.WaitForExit(5000)); if (!exited) proc.Kill(entireProcessTree: true); } catch { } finally { proc.Dispose(); }
//            }
//        }

//        private string? ResolveBrowserExecutable()
//        {
//            if (!string.IsNullOrWhiteSpace(_options.BrowserExecutablePath) && File.Exists(_options.BrowserExecutablePath)) return _options.BrowserExecutablePath;
//            var candidates = new List<string>();

//            if (OperatingSystem.IsWindows())
//            {
//                var roots = new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("LocalAppData") };
//                foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r))) candidates.Add(Path.Combine(root!, "Google", "Chrome", "Application", "chrome.exe"));
//                foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r))) candidates.Add(Path.Combine(root!, "Microsoft", "Edge", "Application", "msedge.exe"));
//            }
//            else if (OperatingSystem.IsMacOS())
//            {
//                candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
//                candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
//            }
//            else
//            {
//                candidates.AddRange(new[] { "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/microsoft-edge", "/usr/bin/chromium", "/usr/bin/chromium-browser" });
//            }
//            return candidates.FirstOrDefault(File.Exists);
//        }

//        private async Task<IBrowserContext> LaunchViaPlaywrightAsync(bool headless, bool disableImages)
//        {
//            _playwright ??= await Playwright.CreateAsync();
//            Directory.CreateDirectory(_options.UserDataDir);

//            var args = new List<string> { "--disable-blink-features=AutomationControlled", "--disable-dev-shm-usage" };
//            if (OperatingSystem.IsLinux()) args.Add("--no-sandbox");
//            if (disableImages) args.Add("--blink-settings=imagesEnabled=false");

//            var ctx = await _playwright.Chromium.LaunchPersistentContextAsync(
//                _options.UserDataDir,
//                new BrowserTypeLaunchPersistentContextOptions
//                {
//                    Headless = headless,
//                    Channel = _options.Channel,
//                    Locale = "vi-VN",
//                    TimezoneId = "Asia/Ho_Chi_Minh",
//                    ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
//                    IgnoreDefaultArgs = new[] { "--enable-automation" },
//                    Args = args
//                });

//            await ctx.AddInitScriptAsync(MinimalInitScript);
//            ctx.Close += (_, _) => { if (ReferenceEquals(_context, ctx)) _context = null; };
//            _usingCdp = false;
//            return ctx;
//        }

//        private async Task WarmUpAsync(IBrowserContext ctx)
//        {
//            try
//            {
//                _logger.LogInformation("[Shopee] Khởi tạo session Shopee VN...");
//                var page = await ctx.NewPageAsync();
//                try
//                {
//                    if (_options.Headless) await FixHeadlessUserAgentAsync(page);
//                    await page.GotoAsync(ShopeeBaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
//                    await HumanizeAsync(page);
//                }
//                finally { try { await page.CloseAsync(); } catch { } }
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning("[Shopee] Warm-up thất bại (bỏ qua): {Msg}", ex.Message);
//            }
//        }

//        /// <param name="onlyIfGeneration">Chỉ đóng nếu context hiện tại đúng là context mà lần cào lỗi đã dùng (tránh một request lỗi làm chết các request khác đang chạy).</param>
//        private async Task CloseContextAsync(int? onlyIfGeneration = null)
//        {
//            await _ctxLock.WaitAsync();
//            try
//            {
//                if (onlyIfGeneration.HasValue && onlyIfGeneration.Value != _generation) return;

//                var ctx = _context;
//                _context = null;

//                if (_usingCdp) await ShutdownChromeAsync();
//                else if (ctx != null) { try { await ctx.CloseAsync(); } catch { } }
//                _usingCdp = false;
//            }
//            finally
//            {
//                _ctxLock.Release();
//            }
//        }

//        // ============================== Helpers ==============================

//        private static ShopeeIds? TryParseIds(string link)
//        {
//            var m = IdRegexDotted.Match(link);
//            if (!m.Success) m = IdRegexPath.Match(link);
//            return m.Success ? new ShopeeIds(m.Groups[1].Value, m.Groups[2].Value) : null;
//        }

//        private static bool IsBlockedUrl(string url)
//        {
//            if (string.IsNullOrEmpty(url)) return false;
//            var u = url.ToLowerInvariant();
//            return u.Contains("/buyer/login") || u.Contains("/verify/") || u.Contains("captcha") || u.Contains("/traffic/error");
//        }

//        private static string Shorten(string url)
//        {
//            try { var u = new Uri(url); var s = u.PathAndQuery; return s.Length > 160 ? s[..160] + "…" : s; }
//            catch { return url.Length > 160 ? url[..160] + "…" : url; }
//        }

//        private static async Task<string> SafeTitleAsync(IPage page)
//        {
//            try { return await page.TitleAsync(); } catch { return string.Empty; }
//        }

//        private async Task SaveDebugAsync(IPage page, string tag, ConcurrentQueue<string> apiLog, string? apiBody)
//        {
//            if (string.IsNullOrWhiteSpace(_options.DebugDirectory)) return;

//            try
//            {
//                Directory.CreateDirectory(_options.DebugDirectory);
//                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
//                var basePath = Path.Combine(_options.DebugDirectory, $"{stamp}_{tag}");
//                var title = await SafeTitleAsync(page);
//                _logger.LogWarning("[Shopee] DEBUG Url={Url} Title='{Title}' → {Path}.*", page.Url, title, basePath);

//                var lines = new List<string> { $"Url: {page.Url}", $"Title: {title}", "--- API đã thấy ---" };
//                lines.AddRange(apiLog.ToArray());
//                await File.WriteAllLinesAsync(basePath + "_apicalls.txt", lines);

//                if (!string.IsNullOrEmpty(apiBody)) await File.WriteAllTextAsync(basePath + "_api.json", apiBody);
//                await page.ScreenshotAsync(new PageScreenshotOptions { Path = basePath + ".png", FullPage = true });
//                await File.WriteAllTextAsync(basePath + ".html", await page.ContentAsync());
//            }
//            catch (Exception ex)
//            {
//                _logger.LogDebug(ex, "[Shopee] Không lưu được debug artifacts");
//            }
//        }

//        public async ValueTask DisposeAsync()
//        {
//            if (_disposed) return;
//            _disposed = true;

//            await CloseContextAsync();
//            if (_options.Mode == ShopeeBrowserMode.RealChromeCdp) await ShutdownChromeAsync();
//            _playwright?.Dispose();
//            _playwright = null;
//            _searchCache.Clear();
//            _searchInFlight.Clear();
//            _scheduler.Dispose();
//            _ctxLock.Dispose();
//        }
//    }
//}