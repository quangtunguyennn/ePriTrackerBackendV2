using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;

namespace ePriTrackerBackend.Services
{
    /// <summary>Tiki chặn bot (403/429/trang captcha) và đã hết số lần thử.</summary>
    public class TikiBlockedException : Exception
    {
        public int? StatusCode { get; }
        public TikiBlockedException(string message, int? statusCode = null) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    /// <summary>Sản phẩm không còn tồn tại trên Tiki (404/410).</summary>
    public class TikiProductNotFoundException : Exception
    {
        public TikiProductNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Crawl Tiki theo nhiều tầng để vừa nhanh vừa bền:
    ///   1) HTTP trực tiếp tới API JSON (HTTP/2, nén, tái sử dụng kết nối) - đường nhanh, không cần trình duyệt.
    ///   2) Nếu bị chặn (401/403/trả HTML) → tự chuyển sang fetch() trong trình duyệt thật (Playwright, khởi tạo lười).
    ///   3) Nếu API không cho giá → đọc trang sản phẩm qua HTTP (JSON-LD / __NEXT_DATA__), rồi qua trình duyệt.
    /// Có circuit breaker: khi đường HTTP bị chặn liên tục thì tạm đi thẳng trình duyệt, hết thời gian chờ sẽ thử lại HTTP.
    /// </summary>
    public class TikiBrowserService : IEcommerceService, IAsyncDisposable
    {
        public string PlatformName => "Tiki";

        private const string TikiBaseUrl = "https://tiki.vn";

        private const int MaxAttempts = 5;
        private const int RequestTimeoutMs = 12_000;
        private const int PageFallbackTimeoutMs = 8_000;

        // Đường HTTP: request nhẹ, HTTP/2 ghép kênh → song song cao. Có tạm dừng toàn cục khi gặp 429.
        // Nếu thấy HTTP 429 nhiều thì giảm HttpMaxParallel hoặc tăng HttpMinIntervalMs.
        private const int HttpMaxParallel = 8;
        private const int HttpMinIntervalMs = 50;
        private const int HttpJitterMs = 50;

        // Đường trình duyệt (dự phòng): giữ mức thận trọng như bản cũ.
        private const int BrowserMaxParallel = 4;
        private const int BrowserMinIntervalMs = 150;
        private const int BrowserJitterMs = 150;

        // Circuit breaker cho đường HTTP.
        private const int BreakerThreshold = 3;
        private const int BreakerCooldownMs = 60_000;
        private const int SessionWarmIntervalMs = 300_000;

        private const int SearchPageSize = 40;
        private const int MaxSearchPages = 2;
        private const int MaxSuggestions = 10;
        private const int MaxKeywordLength = 100;

        private readonly ILogger<TikiBrowserService> _logger;
        private readonly ScrapeScheduler _httpScheduler = new(HttpMaxParallel, HttpMinIntervalMs, HttpJitterMs);
        private readonly ScrapeScheduler _browserScheduler = new(BrowserMaxParallel, BrowserMinIntervalMs, BrowserJitterMs);
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private readonly SemaphoreSlim _warmLock = new(1, 1);

        // Gộp các request trùng nhau đang chạy (vd FetchProductData + FetchCurrentPrice cùng lúc cho một sản phẩm).
        private readonly ConcurrentDictionary<string, Lazy<Task<TikiProductData>>> _inflight = new();

        private long _pauseUntilMs;           // tạm dừng toàn cục (rate limit)
        private int _httpBlockStreak;
        private long _httpDisabledUntilMs;
        private long _lastWarmMs = -1;

        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private IBrowserContext? _context;
        private IPage? _page;
        private int _generation;
        private bool _disposed;

        private enum Route { Http, Browser }

        private sealed record ApiResponse(int Status, string Body, TimeSpan? RetryAfter = null);
        private sealed record ProductRef(string ProductId, string? Spid);
        private sealed record TikiProductData(string Name, decimal Price, string ImageUrl, string Description);
        private sealed record SuggestionItem(string Id, string Name, decimal Price, string ImageUrl, string Link);

        private static readonly Regex ProductIdDashRegex = new(@"-p(\d+)\.html", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ProductIdPathRegex = new(@"/p(\d+)(?:[/?#]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SpidRegex = new(@"[?&]spid=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex JsonLdRegex = new(
            @"<script[^>]*type=[""']application/ld\+json[""'][^>]*>(.*?)</script>",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex NextDataRegex = new(
            @"<script[^>]*id=""__NEXT_DATA__""[^>]*>(.*?)</script>",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private const string StealthScript = """
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            if (!window.chrome) { window.chrome = { runtime: {} }; }
            Object.defineProperty(navigator, 'languages', { get: () => ['vi-VN', 'vi', 'en-US', 'en'] });
            """;

        // Gọi API same-origin ngay trong trang tiki.vn, luôn trả {status, body} để biết lý do thất bại.
        private const string FetchJs = """
            async (p) => {
                const ctrl = new AbortController();
                const timer = setTimeout(() => ctrl.abort(), 20000);
                try {
                    const r = await fetch(p, {
                        method: 'GET',
                        credentials: 'include',
                        signal: ctrl.signal,
                        headers: { 'Accept': 'application/json, text/plain, */*' }
                    });
                    return JSON.stringify({ status: r.status, body: await r.text() });
                } catch (e) {
                    return JSON.stringify({ status: 0, body: String(e) });
                } finally {
                    clearTimeout(timer);
                }
            }
            """;

        // Dự phòng cuối: đọc JSON-LD / DOM của trang sản phẩm trong trình duyệt.
        private const string PageExtractorJs = """
            () => {
                const out = { name: '', price: 0, imageUrl: '', source: '' };
                const meta = (sel) => (document.querySelector(sel)?.content || '').trim();
                const parsePrice = (v) => {
                    if (v === null || v === undefined) return 0;
                    if (typeof v === 'number') return isFinite(v) ? v : 0;
                    let s = String(v).replace(/[^\d.,]/g, '');
                    if (!s) return 0;
                    if (/^\d{1,3}(\.\d{3})+(,\d+)?$/.test(s)) s = s.replace(/\./g, '').replace(',', '.');
                    else if (/^\d{1,3}(,\d{3})+(\.\d+)?$/.test(s)) s = s.replace(/,/g, '');
                    else s = s.replace(',', '.');
                    const n = parseFloat(s);
                    return isFinite(n) ? n : 0;
                };
                const imgOf = (img) => {
                    if (!img) return '';
                    if (Array.isArray(img)) return imgOf(img[0]);
                    if (typeof img === 'object') return img.url || img.contentUrl || '';
                    return String(img);
                };

                const nodes = [];
                const push = (d) => {
                    if (!d) return;
                    if (Array.isArray(d)) { d.forEach(push); return; }
                    nodes.push(d);
                    if (d['@graph']) push(d['@graph']);
                };
                for (const s of document.querySelectorAll('script[type="application/ld+json"]')) {
                    try { push(JSON.parse(s.textContent)); } catch (e) { }
                }
                for (const d of nodes) {
                    const t = d['@type'];
                    const isProduct = t === 'Product' || (Array.isArray(t) && t.includes('Product'));
                    if (!isProduct) continue;
                    if (d.name && !out.name) out.name = String(d.name);
                    const img = imgOf(d.image);
                    if (img && !out.imageUrl) out.imageUrl = img;
                    let offers = d.offers;
                    if (Array.isArray(offers)) offers = offers[0];
                    const p = parsePrice(offers && (offers.price ?? offers.lowPrice ?? offers.priceSpecification?.price));
                    if (p > 0 && !(out.price > 0)) { out.price = p; out.source = 'jsonld'; }
                }

                if (!(out.price > 0)) {
                    const sels = ['.product-price__current-price', '[class*="product-price__current-price"]'];
                    for (const sel of sels) {
                        for (const el of document.querySelectorAll(sel)) {
                            const p = parsePrice((el.innerText || el.textContent || '').split('-')[0]);
                            if (p > 0) { out.price = p; out.source = 'dom'; break; }
                        }
                        if (out.price > 0) break;
                    }
                }

                if (!out.name) out.name = (document.querySelector('h1')?.textContent || '').trim();
                if (!out.name) out.name = meta('meta[property="og:title"]');
                if (!out.imageUrl) out.imageUrl = meta('meta[property="og:image"]');
                return out;
            }
            """;

        // ScraperMetricsService để optional: nếu chưa đăng ký trong DI thì service vẫn khởi tạo được.
        public TikiBrowserService(ILogger<TikiBrowserService> logger, ScraperMetricsService? metrics = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ = metrics;

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true,
                MaxConnectionsPerServer = HttpMaxParallel * 2,
                UseCookies = true,                 // giữ cookie phiên giữa các request
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };

            _http = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan, // timeout từng request do CancellationTokenSource riêng quản lý
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", TikiBaseUrl + "/");
        }

        public bool CanHandle(string url) => url.Contains("tiki.vn", StringComparison.OrdinalIgnoreCase);

        // ============================== Public API ==============================

        public async Task<Product> FetchProductDataAsync(string tikiProductId, string normalizedLink)
        {
            var productRef = ResolveRef(tikiProductId, normalizedLink)
                ?? throw new ArgumentException($"Không xác định được product id Tiki từ link: {normalizedLink}", nameof(normalizedLink));

            var data = await GetProductDataAsync(productRef, normalizedLink);
            if (data.Price <= 0)
                throw new InvalidOperationException($"Tiki không trả về giá hợp lệ cho sản phẩm {productRef.ProductId}.");

            return new Product
            {
                ProductLink = normalizedLink,
                ProductName = string.IsNullOrWhiteSpace(data.Name) ? $"Tiki {productRef.ProductId}" : data.Name,
                ImageURL = data.ImageUrl,
                Description = data.Description,
                InitialPrice = data.Price,
                LatestPrice = data.Price,
                AddedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
            };
        }

        public async Task<decimal?> FetchCurrentPriceAsync(string productLink)
        {
            try
            {
                var productRef = ResolveRef(null, productLink);
                if (productRef == null)
                {
                    _logger.LogWarning("[Tiki] Không tách được product id từ link: {Link}", productLink);
                    return null;
                }

                var data = await GetProductDataAsync(productRef, productLink);
                return data.Price > 0 ? data.Price : null;
            }
            catch (TikiProductNotFoundException ex)
            {
                _logger.LogWarning("[Tiki] Sản phẩm không còn tồn tại {Link}: {Msg}", productLink, ex.Message);
                return null;
            }
            catch (TikiBlockedException ex)
            {
                _logger.LogWarning("[Tiki] Bị chặn khi cập nhật giá {Link}: {Msg}", productLink, ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Tiki] Không thể cập nhật giá cho {Link}", productLink);
                return null;
            }
        }

        public async Task<List<SuggestionProduct>> CrawlSuggestionsAsync(Guid productId, string searchKeyword, decimal initialPrice)
        {
            var result = new List<SuggestionProduct>();
            var keyword = NormalizeKeyword(searchKeyword);
            if (keyword.Length == 0 || initialPrice <= 0) return result;

            try
            {
                var seen = new HashSet<string>();

                // Một request thường đã đủ; chỉ lật trang khi sau khi lọc theo giá vẫn chưa đủ gợi ý.
                for (var page = 1; page <= MaxSearchPages && result.Count < MaxSuggestions; page++)
                {
                    var path = $"/api/v2/products?limit={SearchPageSize}&page={page}&q={Uri.EscapeDataString(keyword)}";

                    (List<SuggestionItem> Items, int RawCount) parsed;
                    try
                    {
                        parsed = await CallApiAsync(path, ParseSuggestions);
                    }
                    catch (Exception ex) when (page > 1)
                    {
                        // Trang 2 lỗi thì giữ lại những gì đã có ở trang 1.
                        _logger.LogWarning("[Tiki] Lỗi khi lật trang {Page} gợi ý: {Msg}", page, FirstLine(ex.Message));
                        break;
                    }

                    foreach (var item in parsed.Items)
                    {
                        if (!seen.Add(item.Id)) continue;
                        if (item.Price <= 0 || item.Price > initialPrice) continue;
                        if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Link)) continue;

                        result.Add(new SuggestionProduct
                        {
                            SuggestionProductId = Guid.NewGuid(),
                            ProductId = productId,
                            ProductName = item.Name,
                            Price = item.Price,
                            ImageURL = item.ImageUrl,
                            ProductLink = item.Link,
                            LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
                        });

                        if (result.Count >= MaxSuggestions) break;
                    }

                    if (parsed.RawCount < SearchPageSize) break; // đã hết kết quả
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Tiki] Không lấy được gợi ý cho productId {Id}", productId);
            }

            return result;
        }

        public Task<List<LiveEventProductDTO>> GetLiveProductsFromEventAsync(string eventLink)
            => Task.FromResult(new List<LiveEventProductDTO>());

        // ============================== Lấy dữ liệu sản phẩm ==============================

        private async Task<TikiProductData> GetProductDataAsync(ProductRef productRef, string link)
        {
            var key = $"{productRef.ProductId}:{productRef.Spid}";
            var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<TikiProductData>>(() => LoadProductAsync(productRef, link)));
            try
            {
                return await lazy.Value;
            }
            finally
            {
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<TikiProductData>>>(key, lazy));
            }
        }

        private async Task<TikiProductData> LoadProductAsync(ProductRef productRef, string link)
        {
            // Tầng 1+2: API (HTTP → trình duyệt tự động bên trong CallApiAsync)
            try
            {
                var data = await CallApiAsync(BuildProductPath(productRef), ParseProduct);
                if (data.Price > 0) return data;
                _logger.LogWarning("[Tiki] API không trả giá hợp lệ cho {Id}, thử đọc trang sản phẩm...", productRef.ProductId);
            }
            catch (TikiProductNotFoundException) { throw; }
            catch (ObjectDisposedException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("[Tiki] API lỗi cho {Id} ({Msg}), thử đọc trang sản phẩm...", productRef.ProductId, FirstLine(ex.Message));
            }

            var pageUrl = BuildPageUrl(link, productRef);

            // Tầng 3: đọc trang sản phẩm qua HTTP (nhanh, không cần trình duyệt)
            try
            {
                var data = await FetchFromProductPageHttpAsync(pageUrl, productRef);
                if (data.Price > 0) return data;
            }
            catch (TikiProductNotFoundException) { throw; }
            catch (ObjectDisposedException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug("[Tiki] Đọc trang qua HTTP thất bại cho {Id} ({Msg})", productRef.ProductId, FirstLine(ex.Message));
            }

            // Tầng 4: đọc trang bằng trình duyệt thật
            return await FetchFromProductPageAsync(pageUrl);
        }

        private static string BuildProductPath(ProductRef r)
            => string.IsNullOrEmpty(r.Spid)
                ? $"/api/v2/products/{r.ProductId}?platform=web"
                : $"/api/v2/products/{r.ProductId}?platform=web&spid={r.Spid}";

        private static string BuildPageUrl(string link, ProductRef r)
        {
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri)
                && uri.Host.EndsWith("tiki.vn", StringComparison.OrdinalIgnoreCase))
                return uri.AbsoluteUri;

            return string.IsNullOrEmpty(r.Spid)
                ? $"{TikiBaseUrl}/p{r.ProductId}"
                : $"{TikiBaseUrl}/p{r.ProductId}?spid={r.Spid}";
        }

        private static ProductRef? ResolveRef(string? idHint, string link)
        {
            string? id = null;
            string? spid = null;

            if (!string.IsNullOrWhiteSpace(link))
            {
                var m = ProductIdDashRegex.Match(link);
                if (!m.Success) m = ProductIdPathRegex.Match(link);
                if (m.Success) id = m.Groups[1].Value;

                var s = SpidRegex.Match(link);
                if (s.Success) spid = s.Groups[1].Value;
            }

            if (id == null && !string.IsNullOrWhiteSpace(idHint))
            {
                var digits = Regex.Match(idHint, @"\d+");
                if (digits.Success) id = digits.Value;
            }

            return id == null ? null : new ProductRef(id, spid);
        }

        private static TikiProductData ParseProduct(JsonElement root)
        {
            var name = GetString(root, "name");

            var price = GetNumber(root, "price");
            if (price <= 0) price = GetNumber(root, "current_seller", "price");
            if (price <= 0) price = GetNumber(root, "min_price");

            var image = GetString(root, "thumbnail_url");
            if (string.IsNullOrEmpty(image) && Navigate(root, "images") is { ValueKind: JsonValueKind.Array } images)
            {
                foreach (var img in images.EnumerateArray())
                {
                    image = GetString(img, "base_url");
                    if (string.IsNullOrEmpty(image)) image = GetString(img, "large_url");
                    if (!string.IsNullOrEmpty(image)) break;
                }
            }

            return new TikiProductData(name, Math.Round(price), NormalizeImageUrl(image), GetString(root, "description"));
        }

        private static (List<SuggestionItem> Items, int RawCount) ParseSuggestions(JsonElement root)
        {
            var list = new List<SuggestionItem>();

            if (Navigate(root, "data") is not { ValueKind: JsonValueKind.Array } data) return (list, 0);

            var raw = 0;
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                raw++;

                var id = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : string.Empty;
                if (string.IsNullOrEmpty(id)) continue;

                var urlPath = GetString(item, "url_path");
                string link;
                if (!string.IsNullOrEmpty(urlPath))
                {
                    link = urlPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? urlPath
                        : $"{TikiBaseUrl}/{urlPath.TrimStart('/')}";
                }
                else if (id.All(char.IsDigit))
                {
                    // Không có url_path thì dựng link từ id (+ spid nếu có) để vẫn dùng được.
                    var spid = item.TryGetProperty("seller_product_id", out var sp) ? sp.ToString() : string.Empty;
                    link = string.IsNullOrEmpty(spid) || !spid.All(char.IsDigit)
                        ? $"{TikiBaseUrl}/p{id}"
                        : $"{TikiBaseUrl}/p{id}?spid={spid}";
                }
                else
                {
                    link = string.Empty;
                }

                list.Add(new SuggestionItem(
                    id,
                    GetString(item, "name").Trim(),
                    Math.Round(GetNumber(item, "price")),
                    NormalizeImageUrl(GetString(item, "thumbnail_url")),
                    link));
            }

            return (list, raw);
        }

        // ============================== Gọi API: HTTP trước, trình duyệt khi cần ==============================

        private async Task<T> CallApiAsync<T>(string path, Func<JsonElement, T> map)
        {
            var route = HttpPathOpen ? Route.Http : Route.Browser;
            var httpTransient = 0;
            var warmTried = false;

            var lastMessage = string.Empty;
            var lastBlock = false;
            int? lastStatus = null;
            Exception? lastError = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(TikiBrowserService));

                ApiResponse? resp = null;
                Exception? error = null;
                var message = string.Empty;
                var isBlock = false;
                var isRateLimit = false;
                var needReset = false;
                var gen = 0;
                var delayMs = 0;

                await WaitGateAsync();

                try
                {
                    // Slot chỉ giữ trong lúc gọi; thời gian chờ retry không chiếm slot.
                    if (route == Route.Http)
                    {
                        using var lease = await _httpScheduler.AcquireAsync();
                        resp = await SendHttpAsync(path, "application/json, text/plain, */*");
                    }
                    else
                    {
                        using var lease = await _browserScheduler.AcquireAsync();
                        var (page, g) = await EnsurePageReadyAsync();
                        gen = g;
                        resp = await FetchJsonInBrowserAsync(page, path);
                    }
                }
                catch (TikiBlockedException ex)
                {
                    error = ex; message = ex.Message; isBlock = true; needReset = route == Route.Browser;
                }
                catch (ObjectDisposedException) { throw; }
                catch (OperationCanceledException ex)
                {
                    error = ex; message = $"Request quá {RequestTimeoutMs}ms";
                }
                catch (HttpRequestException ex)
                {
                    error = ex; message = $"Lỗi mạng: {FirstLine(ex.Message)}";
                }
                catch (Exception ex)
                {
                    error = ex; message = $"Lỗi {route}: {FirstLine(ex.Message)}"; needReset = route == Route.Browser;
                }

                if (resp != null)
                {
                    if (resp.Status == 200)
                    {
                        JsonDocument? doc = null;
                        try { doc = JsonDocument.Parse(resp.Body); }
                        catch (JsonException)
                        {
                            // HTTP 200 nhưng không phải JSON (thường là trang captcha/HTML chặn bot)
                            message = $"Tiki trả nội dung không phải JSON: {Shorten(resp.Body)}";
                            isBlock = true;
                            needReset = route == Route.Browser;
                        }

                        if (doc != null)
                        {
                            using (doc)
                            {
                                var root = doc.RootElement;
                                if (root.ValueKind == JsonValueKind.Object
                                    && root.TryGetProperty("error", out var err)
                                    && err.ValueKind == JsonValueKind.Object)
                                {
                                    var code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci) ? ci : 0;
                                    if (code is 404 or 410) throw new TikiProductNotFoundException($"Tiki báo lỗi {code}: {path}");
                                    message = $"Tiki trả lỗi trong body: {Shorten(resp.Body)}";
                                }
                                else
                                {
                                    if (route == Route.Http) OnHttpOk();
                                    return map(root); // lỗi từ map được ném thẳng ra ngoài
                                }
                            }
                        }
                    }
                    else if (resp.Status is 404 or 410)
                    {
                        throw new TikiProductNotFoundException($"Tiki trả HTTP {resp.Status}: {path}");
                    }
                    else if (resp.Status is 401 or 403)
                    {
                        message = $"Tiki từ chối request (HTTP {resp.Status}): {Shorten(resp.Body)}";
                        isBlock = true;
                        needReset = route == Route.Browser;
                    }
                    else if (resp.Status == 429)
                    {
                        message = "Tiki giới hạn tần suất (HTTP 429)";
                        isBlock = true;
                        isRateLimit = true;
                        var retryMs = (int)Math.Clamp(resp.RetryAfter?.TotalMilliseconds ?? 3_000, 1_000, 15_000);
                        PauseAll(retryMs + Random.Shared.Next(0, 500)); // mọi luồng cùng chờ, tránh dồn dập
                    }
                    else if (resp.Status is 0 or >= 500)
                    {
                        message = resp.Status == 0
                            ? $"Fetch trong trình duyệt thất bại: {Shorten(resp.Body)}"
                            : $"Tiki lỗi máy chủ (HTTP {resp.Status})";
                    }
                    else
                    {
                        throw new InvalidOperationException($"Tiki API trả HTTP {resp.Status}: {Shorten(resp.Body)}");
                    }
                }

                lastMessage = message;
                lastBlock = isBlock;
                lastStatus = resp?.Status;
                lastError = error;

                if (attempt >= MaxAttempts) break;

                if (route == Route.Http)
                {
                    if (isRateLimit)
                    {
                        // Đã tạm dừng toàn cục ở trên; thử lại cùng đường HTTP.
                    }
                    else if (isBlock)
                    {
                        OnHttpBlocked();

                        // 401/403 có thể do thiếu cookie phiên: làm mới phiên một lần rồi thử lại HTTP.
                        var stay = false;
                        if (!warmTried && resp?.Status is 401 or 403)
                        {
                            warmTried = true;
                            stay = await WarmHttpSessionAsync();
                        }
                        if (!stay) route = Route.Browser; // chuyển ngay sang trình duyệt, không chờ
                    }
                    else
                    {
                        // Lỗi tạm thời (mạng/5xx/timeout): thử lại HTTP vài lần rồi mới chuyển sang trình duyệt.
                        if (++httpTransient >= 2) route = Route.Browser;
                        delayMs = 250 * attempt + Random.Shared.Next(0, 150);
                    }
                }
                else
                {
                    if (needReset) await ResetIfCurrentAsync(gen);
                    delayMs = 800 * attempt;
                }

                _logger.LogWarning("[Tiki] Lần {Attempt}/{Max} thất bại: {Msg}. Thử lại bằng {Route}...", attempt, MaxAttempts, message, route);
                if (delayMs > 0) await Task.Delay(delayMs);
            }

            if (lastBlock) throw new TikiBlockedException(lastMessage, lastStatus);
            throw new InvalidOperationException(lastMessage, lastError);
        }

        // ---------- Tạm dừng toàn cục & circuit breaker ----------

        private async Task WaitGateAsync()
        {
            var wait = Interlocked.Read(ref _pauseUntilMs) - Environment.TickCount64;
            if (wait > 0) await Task.Delay((int)Math.Min(wait, 15_000));
        }

        private void PauseAll(int ms)
        {
            var target = Environment.TickCount64 + ms;
            long cur;
            while ((cur = Interlocked.Read(ref _pauseUntilMs)) < target
                   && Interlocked.CompareExchange(ref _pauseUntilMs, target, cur) != cur)
            {
            }
        }

        private bool HttpPathOpen => Environment.TickCount64 >= Interlocked.Read(ref _httpDisabledUntilMs);

        private void OnHttpOk() => Interlocked.Exchange(ref _httpBlockStreak, 0);

        private void OnHttpBlocked()
        {
            if (Interlocked.Increment(ref _httpBlockStreak) < BreakerThreshold) return;

            Interlocked.Exchange(ref _httpDisabledUntilMs, Environment.TickCount64 + BreakerCooldownMs);
            // Sau thời gian chờ, chỉ cần một lần bị chặn nữa là ngắt tiếp (half-open).
            Interlocked.Exchange(ref _httpBlockStreak, BreakerThreshold - 1);
            _logger.LogWarning("[Tiki] Đường HTTP bị chặn liên tục → dùng trình duyệt trong {Sec}s.", BreakerCooldownMs / 1000);
        }

        // ---------- Đường HTTP ----------

        private async Task<ApiResponse> SendHttpAsync(string pathOrUrl, string accept)
        {
            var url = pathOrUrl.StartsWith('/') ? TikiBaseUrl + pathOrUrl : pathOrUrl;

            using var cts = new CancellationTokenSource(RequestTimeoutMs);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", accept);
            if (accept.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            }

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var body = await res.Content.ReadAsStringAsync(cts.Token);

            TimeSpan? retryAfter = null;
            var ra = res.Headers.RetryAfter;
            if (ra?.Delta is { } delta) retryAfter = delta;
            else if (ra?.Date is { } date) retryAfter = date - DateTimeOffset.UtcNow;

            return new ApiResponse((int)res.StatusCode, body, retryAfter);
        }

        /// <summary>Làm mới cookie phiên bằng một lần truy cập trang chủ (tối đa mỗi 5 phút). Trả true nếu vừa thực hiện.</summary>
        private async Task<bool> WarmHttpSessionAsync()
        {
            bool Recent() => Interlocked.Read(ref _lastWarmMs) >= 0
                             && Environment.TickCount64 - Interlocked.Read(ref _lastWarmMs) < SessionWarmIntervalMs;

            if (Recent()) return false;

            await _warmLock.WaitAsync();
            try
            {
                if (Recent()) return false;

                using var cts = new CancellationTokenSource(RequestTimeoutMs);
                using var req = new HttpRequestMessage(HttpMethod.Get, TikiBaseUrl);
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                Interlocked.Exchange(ref _lastWarmMs, Environment.TickCount64);
                _logger.LogInformation("[Tiki] Làm mới cookie phiên HTTP (HTTP {Status}).", (int)res.StatusCode);
                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _lastWarmMs, Environment.TickCount64);
                _logger.LogDebug(ex, "[Tiki] Không làm mới được phiên HTTP");
                return false;
            }
            finally
            {
                _warmLock.Release();
            }
        }

        // ---------- Đường trình duyệt ----------

        private static async Task<ApiResponse> FetchJsonInBrowserAsync(IPage page, string path)
        {
            var json = await page.EvaluateAsync<string>(FetchJs, path);
            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("status").GetInt32();
            var body = doc.RootElement.GetProperty("body").GetString() ?? string.Empty;
            return new ApiResponse(status, body);
        }

        // ============================== Fallback: đọc trang sản phẩm qua HTTP ==============================

        private async Task<TikiProductData> FetchFromProductPageHttpAsync(string pageUrl, ProductRef productRef)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TikiBrowserService));
            await WaitGateAsync();

            ApiResponse resp;
            using (var lease = await _httpScheduler.AcquireAsync())
            {
                resp = await SendHttpAsync(pageUrl, "text/html,application/xhtml+xml");
            }

            if (resp.Status is 404 or 410) throw new TikiProductNotFoundException($"Trang sản phẩm trả HTTP {resp.Status}");
            if (resp.Status is 401 or 403 or 429) throw new TikiBlockedException($"Trang sản phẩm trả HTTP {resp.Status}", resp.Status);
            if (resp.Status != 200) throw new InvalidOperationException($"Trang sản phẩm trả HTTP {resp.Status}");

            return ExtractFromHtml(resp.Body, productRef.ProductId)
                ?? throw new InvalidOperationException("Không tìm thấy giá trong HTML trang sản phẩm.");
        }

        private static TikiProductData? ExtractFromHtml(string html, string productId)
        {
            // 1) JSON-LD (schema.org Product)
            foreach (Match m in JsonLdRegex.Matches(html))
            {
                try
                {
                    using var doc = JsonDocument.Parse(m.Groups[1].Value);
                    if (FindNode(doc.RootElement, IsProductNode) is not { } node) continue;

                    var offers = Navigate(node, "offers");
                    if (offers is { ValueKind: JsonValueKind.Array } oa && oa.GetArrayLength() > 0) offers = oa[0];

                    var price = 0m;
                    if (offers is { } o)
                    {
                        price = GetNumber(o, "price");
                        if (price <= 0) price = GetNumber(o, "lowPrice");
                        if (price <= 0) price = GetNumber(o, "priceSpecification", "price");
                    }

                    if (price > 0)
                        return new TikiProductData(GetString(node, "name"), Math.Round(price), NormalizeImageUrl(ImageOf(node)), GetString(node, "description"));
                }
                catch (JsonException) { }
            }

            // 2) __NEXT_DATA__ (Next.js): tìm object có id trùng và có giá
            var nd = NextDataRegex.Match(html);
            if (nd.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(nd.Groups[1].Value);
                    var node = FindNode(doc.RootElement, el =>
                        el.TryGetProperty("id", out var idEl) && idEl.ToString() == productId && GetNumber(el, "price") > 0);

                    if (node is { } n)
                    {
                        var image = GetString(n, "thumbnail_url");
                        return new TikiProductData(GetString(n, "name"), Math.Round(GetNumber(n, "price")), NormalizeImageUrl(image), GetString(n, "description"));
                    }
                }
                catch (JsonException) { }
            }

            return null;
        }

        private static bool IsProductNode(JsonElement el)
        {
            if (!el.TryGetProperty("@type", out var t)) return false;
            if (t.ValueKind == JsonValueKind.String) return t.GetString() == "Product";
            if (t.ValueKind == JsonValueKind.Array)
                foreach (var x in t.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String && x.GetString() == "Product") return true;
            return false;
        }

        private static string ImageOf(JsonElement node)
        {
            if (Navigate(node, "image") is not { } img) return string.Empty;
            if (img.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in img.EnumerateArray())
                {
                    var s = x.ValueKind == JsonValueKind.String ? x.GetString() ?? string.Empty : GetString(x, "url");
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                return string.Empty;
            }
            return img.ValueKind == JsonValueKind.String ? img.GetString() ?? string.Empty : GetString(img, "url");
        }

        private static JsonElement? FindNode(JsonElement el, Func<JsonElement, bool> predicate, int depth = 0)
        {
            if (depth > 14) return null;

            if (el.ValueKind == JsonValueKind.Object)
            {
                if (predicate(el)) return el;
                foreach (var p in el.EnumerateObject())
                {
                    var found = FindNode(p.Value, predicate, depth + 1);
                    if (found != null) return found;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    var found = FindNode(item, predicate, depth + 1);
                    if (found != null) return found;
                }
            }

            return null;
        }

        // ============================== Fallback cuối: đọc trang sản phẩm bằng trình duyệt ==============================

        private async Task<TikiProductData> FetchFromProductPageAsync(string link)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
                || !uri.Host.EndsWith("tiki.vn", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Không thể đọc trang sản phẩm: link Tiki không hợp lệ ({link}).");

            if (_disposed) throw new ObjectDisposedException(nameof(TikiBrowserService));

            using var lease = await _browserScheduler.AcquireAsync();
            var (shared, _) = await EnsurePageReadyAsync();
            var page = await shared.Context.NewPageAsync();
            try
            {
                var nav = await page.GotoAsync(uri.AbsoluteUri, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000
                });

                var status = nav?.Status;
                if (status is 404 or 410) throw new TikiProductNotFoundException($"Trang sản phẩm trả HTTP {status}");
                if (status is 403 or 429) throw new TikiBlockedException($"Trang sản phẩm trả HTTP {status}", status);

                var deadline = DateTime.UtcNow.AddMilliseconds(PageFallbackTimeoutMs);
                while (true)
                {
                    try
                    {
                        var el = await page.EvaluateAsync<JsonElement>(PageExtractorJs);
                        var price = Math.Round(GetNumber(el, "price"));
                        if (price > 0)
                        {
                            _logger.LogInformation("[Tiki] Lấy giá từ trang sản phẩm ({Source}): {Price}", GetString(el, "source"), price);
                            return new TikiProductData(
                                GetString(el, "name"),
                                price,
                                NormalizeImageUrl(GetString(el, "imageUrl")),
                                string.Empty);
                        }
                    }
                    catch (PlaywrightException)
                    {
                        // trang đang điều hướng, thử lại ở vòng sau
                    }

                    if (DateTime.UtcNow >= deadline) break;
                    await Task.Delay(250);
                }

                throw new InvalidOperationException($"Không đọc được giá từ trang sản phẩm. FinalUrl={page.Url}");
            }
            finally
            {
                try { await page.CloseAsync(); } catch { }
            }
        }

        // ============================== Quản lý trình duyệt (khởi tạo lười, chỉ khi cần) ==============================

        private static bool IsHealthy(IPage? page, IBrowser? browser, IBrowserContext? context)
            => page is { IsClosed: false } && context != null && browser is { IsConnected: true };

        /// <summary>Trả về trang tiki.vn dùng chung và "thế hệ" của nó. Chỉ một luồng khởi tạo; các luồng khác chờ rồi dùng lại.</summary>
        private async Task<(IPage Page, int Gen)> EnsurePageReadyAsync()
        {
            if (IsHealthy(_page, _browser, _context)) return (_page!, _generation);

            await _initLock.WaitAsync();
            try
            {
                if (IsHealthy(_page, _browser, _context)) return (_page!, _generation);

                await ResetCoreAsync();

                var browser = await LaunchBrowserAsync();
                _browser = browser;

                try
                {
                    var context = await browser.NewContextAsync(new BrowserNewContextOptions
                    {
                        Locale = "vi-VN",
                        TimezoneId = "Asia/Ho_Chi_Minh",
                        ViewportSize = new ViewportSize { Width = 1366, Height = 768 }
                    });
                    _context = context;

                    await context.AddInitScriptAsync(StealthScript);
                    await BrowserTuning.BlockHeavyResourcesAsync(context);

                    var page = await context.NewPageAsync();
                    await FixHeadlessUserAgentAsync(page);
                    await WarmUpAsync(page);

                    _page = page;
                    _generation++;
                    return (page, _generation);
                }
                catch
                {
                    await ResetCoreAsync();
                    throw;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        private async Task<IBrowser> LaunchBrowserAsync()
        {
            _playwright ??= await Playwright.CreateAsync();

            var args = new List<string> { "--disable-blink-features=AutomationControlled", "--disable-dev-shm-usage" };
            if (OperatingSystem.IsLinux()) args.Add("--no-sandbox");
            var ignore = new[] { "--enable-automation" };

            try
            {
                // Ưu tiên Chrome thật (ít bị gắn cờ bot hơn Chromium bundle)
                return await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Channel = "chrome",
                    Args = args,
                    IgnoreDefaultArgs = ignore
                });
            }
            catch (PlaywrightException ex)
            {
                _logger.LogInformation("[Tiki] Không mở được Chrome thật ({Msg}) → dùng Chromium của Playwright.", FirstLine(ex.Message));
                return await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args = args,
                    IgnoreDefaultArgs = ignore
                });
            }
        }

        private async Task WarmUpAsync(IPage page)
        {
            _logger.LogInformation("[Tiki] Khởi tạo session trình duyệt tiki.vn...");
            try
            {
                var resp = await page.GotoAsync(TikiBaseUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000
                });

                if (resp is { Status: 403 or 429 })
                    throw new TikiBlockedException($"Trang chủ Tiki trả HTTP {resp.Status}", resp.Status);
            }
            catch (Microsoft.Playwright.PlaywrightException ex) when (page.Url.StartsWith(TikiBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning($"[Tiki] Trang chủ lỗi Playwright: {ex.Message}");
            }

            // Cho JS của trang kịp đặt cookie
            await Task.Delay(Random.Shared.Next(300, 600));

            // fetch same-origin chỉ hoạt động khi trang đang ở tiki.vn
            if (!page.Url.StartsWith(TikiBaseUrl, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Không vào được tiki.vn (url hiện tại: {page.Url})");
        }

        private async Task FixHeadlessUserAgentAsync(IPage page)
        {
            try
            {
                var ua = await page.EvaluateAsync<string>("() => navigator.userAgent");
                if (string.IsNullOrEmpty(ua) || !ua.Contains("HeadlessChrome", StringComparison.Ordinal)) return;

                var cdp = await page.Context.NewCDPSessionAsync(page);
                await cdp.SendAsync("Network.setUserAgentOverride", new Dictionary<string, object>
                {
                    ["userAgent"] = ua.Replace("HeadlessChrome", "Chrome"),
                    ["acceptLanguage"] = "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7"
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Tiki] Không đổi được UA headless");
            }
        }

        /// <summary>Chỉ reset nếu trình duyệt hiện tại vẫn là bản mà lần gọi lỗi đã dùng (tránh một request lỗi làm chết các request song song khác).</summary>
        private async Task ResetIfCurrentAsync(int generation)
        {
            await _initLock.WaitAsync();
            try
            {
                if (generation == _generation) await ResetCoreAsync();
            }
            finally
            {
                _initLock.Release();
            }
        }

        // Gọi khi đang giữ _initLock
        private async Task ResetCoreAsync()
        {
            var context = _context;
            var browser = _browser;
            _page = null;
            _context = null;
            _browser = null;

            try { if (context != null) await context.CloseAsync(); } catch { }
            try { if (browser != null) await browser.CloseAsync(); } catch { }
        }

        // ============================== Helpers ==============================

        private static string NormalizeKeyword(string? keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return string.Empty;
            var k = Regex.Replace(keyword.Trim(), @"\s+", " ");
            return k.Length > MaxKeywordLength ? k[..MaxKeywordLength].Trim() : k;
        }

        private static string NormalizeImageUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = value.Trim();
            if (value.StartsWith("//")) return "https:" + value;
            return value;
        }

        private static JsonElement? Navigate(JsonElement root, params string[] path)
        {
            var cur = root;
            foreach (var segment in path)
            {
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(segment, out var next)) return null;
                cur = next;
            }
            return cur;
        }

        private static string GetString(JsonElement root, params string[] path)
        {
            if (Navigate(root, path) is { ValueKind: JsonValueKind.String } el)
                return el.GetString() ?? string.Empty;
            return string.Empty;
        }

        private static decimal GetNumber(JsonElement root, params string[] path)
        {
            if (Navigate(root, path) is not { } el) return 0m;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d > 0 ? d : 0m;
            if (el.ValueKind == JsonValueKind.String
                && decimal.TryParse(el.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s))
                return s > 0 ? s : 0m;
            return 0m;
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
            return oneLine.Length > 200 ? oneLine[..200] + "…" : oneLine;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var idx = text.IndexOf('\n');
            return idx > 0 ? text[..idx].Trim() : text.Trim();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _initLock.WaitAsync();
            try
            {
                await ResetCoreAsync();
                try { _playwright?.Dispose(); } catch { }
                _playwright = null;
            }
            finally
            {
                _initLock.Release();
            }

            _http.Dispose();
            _httpScheduler.Dispose();
            _browserScheduler.Dispose();
            _warmLock.Dispose();
            _initLock.Dispose();
        }
    }
}