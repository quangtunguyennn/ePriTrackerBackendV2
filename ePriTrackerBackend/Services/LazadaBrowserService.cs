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
//    public class LazadaBrowserOptions
//    {
//        public bool Headless { get; set; } = true;

//        /// <summary>"chrome" = dùng Chrome thật đã cài trên máy (khuyến nghị). Null/rỗng = Chromium của Playwright. Nếu không tìm thấy Chrome sẽ tự rơi về Chromium.</summary>
//        public string? Channel { get; set; } = "chrome";

//        /// <summary>Profile lưu cookie. Vượt captcha thủ công một lần bằng InteractiveVerifyAsync, các lần sau dùng lại phiên này.</summary>
//        public string UserDataDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), ".lazada-profile");

//        public string? DebugDirectory { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "logs", "lazada-debug");
//        public bool BlockHeavyResources { get; set; } = true;
//        public int NavigationTimeoutMs { get; set; } = 30_000;

//        /// <summary>Tổng thời gian chờ trang sinh ra giá sau khi DOMContentLoaded.</summary>
//        public int ExtractTimeoutMs { get; set; } = 12_000;

//        /// <summary>Thời gian ưu tiên chờ giá từ DOM trước khi chấp nhận nguồn phụ (JSON-LD/module/API), để tránh đổi nguồn gây lệch giá.</summary>
//        public int DomGraceMs { get; set; } = 3_500;

//        /// <summary>Chu kỳ poll khi chờ giá / mô tả.</summary>
//        public int PollIntervalMs { get; set; } = 150;

//        /// <summary>Thời gian tối đa chờ mục "Mô tả sản phẩm" render (Lazada lazy-load khi cuộn).</summary>
//        public int DescriptionWaitMs { get; set; } = 4_000;

//        /// <summary>Số trang chạy song song. Tăng = nhanh hơn nhưng dễ bị captcha hơn.</summary>
//        public int MaxParallelPages { get; set; } = 3;

//        /// <summary>Khoảng cách tối thiểu giữa 2 lần BẮT ĐẦU request (không phải giữa 2 request hoàn tất).</summary>
//        public int MinRequestIntervalMs { get; set; } = 500;
//        public int RequestJitterMs { get; set; } = 300;

//        /// <summary>false (mặc định): chỉ warm-up (mở trang chủ) khi profile mới tinh. true: luôn warm-up như bản cũ.</summary>
//        public bool AlwaysWarmUp { get; set; } = false;

//        public int MaxAttempts { get; set; } = 2;
//        public int VerifyTimeoutMinutes { get; set; } = 5;
//    }

//    /// <summary>Lazada hiện captcha / chặn bot / yêu cầu đăng nhập.</summary>
//    public class LazadaBlockedException : Exception
//    {
//        public string? FinalUrl { get; }
//        public LazadaBlockedException(string message, string? finalUrl) : base(message)
//        {
//            FinalUrl = finalUrl;
//        }
//    }

//    /// <summary>Sản phẩm không còn tồn tại trên Lazada (404/410).</summary>
//    public class LazadaProductNotFoundException : Exception
//    {
//        public LazadaProductNotFoundException(string message) : base(message) { }
//    }

//    public class LazadaBrowserService : IEcommerceService, IAsyncDisposable
//    {
//        public string PlatformName => "Lazada";

//        private const string LazadaHomeUrl = "https://www.lazada.vn";

//        private readonly ILogger<LazadaBrowserService> _logger;
//        private readonly LazadaBrowserOptions _options;
//        private readonly ScrapeScheduler _scheduler;
//        private readonly SemaphoreSlim _ctxLock = new(1, 1);
//        private readonly SemaphoreSlim _workerLock = new(1, 1);

//        private IPlaywright? _playwright;
//        private IBrowserContext? _context;
//        private IPage? _workerPage; // trang luôn mở ở lazada.vn, dùng để fetch JSON tìm kiếm (nhanh hơn nhiều so với mở trang search)
//        private int _generation;
//        private bool _disposed;

//        private sealed record ScrapedProduct(string Name, decimal Price, string ImageUrl, string Source, string Description = "");
//        private sealed record SuggestionCandidate(string Name, decimal Price, string ImageUrl, string Link);

//        private static readonly Regex ItemIdRegex = new(@"-i(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

//        private const string StealthScript = """
//            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
//            if (!window.chrome) { window.chrome = { runtime: {} }; }
//            Object.defineProperty(navigator, 'languages', { get: () => ['vi-VN', 'vi', 'en-US', 'en'] });
//            """;

//        // Thứ tự ưu tiên nguồn giá: dom (giá đang hiển thị, khớp với biến thể trong link) > jsonld > module (__moduleData__).
//        private const string ExtractorJs = """
//            () => {
//                const out = { name: '', price: 0, imageUrl: '', source: '' };
//                const meta = (sel) => (document.querySelector(sel)?.content || '').trim();
//                const parsePrice = (v) => {
//                    if (v === null || v === undefined) return 0;
//                    if (typeof v === 'number') return isFinite(v) ? v : 0;
//                    let s = String(v).replace(/[^\d.,]/g, '');
//                    if (!s) return 0;
//                    if (/^\d{1,3}(\.\d{3})+(,\d+)?$/.test(s)) s = s.replace(/\./g, '').replace(',', '.');
//                    else if (/^\d{1,3}(,\d{3})+(\.\d+)?$/.test(s)) s = s.replace(/,/g, '');
//                    else s = s.replace(',', '.');
//                    const n = parseFloat(s);
//                    return isFinite(n) ? n : 0;
//                };
//                const imgOf = (img) => {
//                    if (!img) return '';
//                    if (Array.isArray(img)) return imgOf(img[0]);
//                    if (typeof img === 'object') return img.url || img.contentUrl || '';
//                    return String(img);
//                };

//                // 1) DOM: lấy giá hiện tại, bỏ qua giá gốc bị gạch (pdp-price_type_deleted)
//                let domPrice = 0;
//                const sels = ['.pdp-price_type_normal', '.pdp-product-price .pdp-price:not(.pdp-price_type_deleted)'];
//                for (const sel of sels) {
//                    for (const el of document.querySelectorAll(sel)) {
//                        if (el.classList.contains('pdp-price_type_deleted')) continue;
//                        const text = (el.innerText || el.textContent || '').trim();
//                        if (!text) continue;
//                        const p = parsePrice(text.split('-')[0]);
//                        if (p > 0) { domPrice = p; break; }
//                    }
//                    if (domPrice > 0) break;
//                }

//                // 2) JSON-LD
//                const ld = { name: '', price: 0, imageUrl: '' };
//                const nodes = [];
//                const push = (d) => {
//                    if (!d) return;
//                    if (Array.isArray(d)) { d.forEach(push); return; }
//                    nodes.push(d);
//                    if (d['@graph']) push(d['@graph']);
//                };
//                for (const s of document.querySelectorAll('script[type="application/ld+json"]')) {
//                    try { push(JSON.parse(s.textContent)); } catch (e) { }
//                }
//                for (const d of nodes) {
//                    const t = d['@type'];
//                    const isProduct = t === 'Product' || (Array.isArray(t) && t.includes('Product'));
//                    if (!isProduct) continue;
//                    if (d.name && !ld.name) ld.name = String(d.name);
//                    const img = imgOf(d.image);
//                    if (img && !ld.imageUrl) ld.imageUrl = img;
//                    let offers = d.offers;
//                    if (Array.isArray(offers)) offers = offers[0];
//                    const p = parsePrice(offers && (offers.price ?? offers.lowPrice ?? offers.priceSpecification?.price));
//                    if (p > 0 && !(ld.price > 0)) ld.price = p;
//                }

//                // 3) __moduleData__ (cấu trúc có thể thay đổi, đã bọc try)
//                let modPrice = 0;
//                try {
//                    const infos = window.__moduleData__?.data?.root?.fields?.skuInfos;
//                    if (infos && typeof infos === 'object') {
//                        const m = location.pathname.match(/-s(\d+)\.html/);
//                        const keys = Object.keys(infos);
//                        const info = (m && infos[m[1]]) || infos['0'] || infos[keys[0]];
//                        const sale = info?.price?.salePrice;
//                        modPrice = parsePrice(sale?.value ?? sale?.text);
//                    }
//                } catch (e) { }

//                if (domPrice > 0) { out.price = domPrice; out.source = 'dom'; }
//                else if (ld.price > 0) { out.price = ld.price; out.source = 'jsonld'; }
//                else if (modPrice > 0) { out.price = modPrice; out.source = 'module'; }

//                out.name = (document.querySelector('.pdp-mod-product-badge-title')?.textContent || '').trim()
//                    || ld.name
//                    || (document.querySelector('h1')?.textContent || '').trim()
//                    || meta('meta[property="og:title"]');
//                out.imageUrl = ld.imageUrl
//                    || meta('meta[property="og:image"]')
//                    || (document.querySelector('.gallery-preview-panel__image')?.getAttribute('src') || '');
//                return out;
//            }
//            """;

//        // Mô tả sản phẩm. source: module/dom = đầy đủ (dừng chờ); highlights/jsonld/meta = tóm tắt (chỉ dùng nếu không có gì tốt hơn).
//        private const string DescriptionJs = """
//            () => {
//                const clean = (s) => (s || '').replace(/\u00a0/g, ' ').trim();
//                const textOf = (el) => clean(el ? (el.innerText || el.textContent) : '');
//                const res = (text, source) => ({ text: text, source: source });

//                // 1) __moduleData__: mô tả đầy đủ dạng HTML, không cần cuộn trang
//                try {
//                    const p = window.__moduleData__?.data?.root?.fields?.product;
//                    if (p && typeof p.desc === 'string' && p.desc.trim().length >= 20) return res(p.desc, 'module');
//                } catch (e) { }

//                // 2) DOM: mục "Mô tả sản phẩm" (Lazada chỉ render khi cuộn tới)
//                const sels = ['#module_product_detail .html-content', '#module_product_detail .detail-content',
//                              '.pdp-product-detail .html-content', '.pdp-product-desc', '.detail-content'];
//                for (const sel of sels) {
//                    for (const el of document.querySelectorAll(sel)) {
//                        const t = textOf(el);
//                        if (t.length >= 20) return res(t, 'dom');
//                    }
//                }

//                // 3) Fallback yếu
//                try {
//                    const h = window.__moduleData__?.data?.root?.fields?.product?.highlights;
//                    if (typeof h === 'string' && h.trim().length >= 20) return res(h, 'highlights');
//                } catch (e) { }
//                for (const s of document.querySelectorAll('script[type="application/ld+json"]')) {
//                    try {
//                        const data = JSON.parse(s.textContent);
//                        const list = Array.isArray(data) ? data : [data];
//                        for (const d of list) {
//                            if (d && typeof d.description === 'string' && d.description.trim().length >= 20) return res(d.description, 'jsonld');
//                        }
//                    } catch (e) { }
//                }
//                const meta = (document.querySelector('meta[property="og:description"]')?.content
//                    || document.querySelector('meta[name="description"]')?.content || '').trim();
//                return res(meta, meta ? 'meta' : '');
//            }
//            """;

//        private const string ScrollToDetailJs = """
//            () => {
//                const el = document.querySelector('#module_product_detail, .pdp-product-detail');
//                if (el) el.scrollIntoView();
//                else window.scrollTo(0, document.body.scrollHeight * 0.6);
//            }
//            """;

//        // fetch same-origin ngay trong trang lazada.vn, luôn trả {status, body}
//        private const string FetchJs = """
//            async (p) => {
//                const ctrl = new AbortController();
//                const timer = setTimeout(() => ctrl.abort(), 15000);
//                try {
//                    const r = await fetch(p, {
//                        method: 'GET',
//                        credentials: 'include',
//                        signal: ctrl.signal,
//                        headers: { 'Accept': 'application/json, text/plain, */*', 'X-Requested-With': 'XMLHttpRequest' }
//                    });
//                    return JSON.stringify({ status: r.status, body: await r.text() });
//                } catch (e) {
//                    return JSON.stringify({ status: 0, body: String(e) });
//                } finally {
//                    clearTimeout(timer);
//                }
//            }
//            """;

//        // Fallback 1 khi mở trang tìm kiếm: dữ liệu nhúng sẵn trong window.pageData
//        private const string SearchPageDataJs = """
//            () => {
//                try {
//                    const items = window.pageData?.mods?.listItems;
//                    if (Array.isArray(items) && items.length) return JSON.stringify(items);
//                } catch (e) { }
//                return '';
//            }
//            """;

//        // Fallback 2: đọc thẻ sản phẩm trong DOM
//        private const string SearchDomJs = """
//            () => {
//                const out = [];
//                const seen = new Set();
//                for (const a of document.querySelectorAll('a[href*="-i"][href*=".html"]')) {
//                    const href = a.getAttribute('href') || '';
//                    const m = href.match(/-i(\d+)(?:-s\d+)?\.html/);
//                    if (!m || seen.has(m[1])) continue;

//                    const card = a.closest('[data-qa-locator="product-item"]') || a.closest('[data-item-id]') || a.parentElement?.parentElement || a;
//                    const lines = (card.innerText || '').split('\n').map(s => s.trim()).filter(Boolean);
//                    const priceLine = lines.find(l => l.includes('₫') && /\d/.test(l)) || '';
//                    const pm = priceLine.match(/₫?\s*\d[\d.,]*/);
//                    if (!pm) continue;

//                    const img = card.querySelector('img');
//                    const name = a.getAttribute('title') || (img && img.getAttribute('alt')) || lines.find(l => !l.includes('₫') && l.length > 8) || '';
//                    seen.add(m[1]);
//                    out.push({
//                        name: name,
//                        priceText: pm[0],
//                        imageUrl: img ? (img.getAttribute('src') || img.getAttribute('data-src') || '') : '',
//                        href: href
//                    });
//                    if (out.length >= 20) break;
//                }
//                return out;
//            }
//            """;

//        private const string BlockedDomJs =
//            "() => !!document.querySelector('#baxia-dialog-content, #nocaptcha, #nc_1_wrapper, .baxia-dialog, .nc-container')";

//        public LazadaBrowserService(ILogger<LazadaBrowserService> logger, IOptions<LazadaBrowserOptions>? options = null)
//        {
//            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
//            _options = options?.Value ?? new LazadaBrowserOptions();
//            _scheduler = new ScrapeScheduler(_options.MaxParallelPages, _options.MinRequestIntervalMs, _options.RequestJitterMs);
//        }

//        public bool CanHandle(string url) => url.Contains("lazada.vn", StringComparison.OrdinalIgnoreCase);

//        // ============================== Public API ==============================

//        public async Task<Product> FetchProductDataAsync(string platformId, string normalizedLink)
//        {
//            _ = platformId;
//            try
//            {
//                var scraped = await ScrapeProductAsync(normalizedLink, includeDetails: true);
//                if (scraped.Price <= 0)
//                    throw new InvalidOperationException("Lazada không trả về giá hợp lệ.");

//                var itemId = ItemIdRegex.Match(normalizedLink) is { Success: true } m ? m.Groups[1].Value : null;
//                var name = string.IsNullOrWhiteSpace(scraped.Name)
//                    ? (itemId != null ? $"Lazada {itemId}" : "Sản phẩm Lazada")
//                    : scraped.Name;

//                return new Product
//                {
//                    ProductLink = normalizedLink,
//                    ProductName = name,
//                    ImageURL = scraped.ImageUrl,
//                    Description = scraped.Description,
//                    InitialPrice = scraped.Price,
//                    LatestPrice = scraped.Price,
//                    AddedAt = DateTime.UtcNow,
//                    LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
//                };
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "[Lazada] Lỗi khi khởi tạo dữ liệu sản phẩm mới {Link}", normalizedLink);
//                throw;
//            }
//        }

//        public async Task<decimal?> FetchCurrentPriceAsync(string productLink)
//        {
//            try
//            {
//                // Cập nhật giá không cần mô tả → includeDetails=false (không cuộn trang, không chờ lazy-load)
//                var scraped = await ScrapeProductAsync(productLink, includeDetails: false);
//                return scraped.Price > 0 ? scraped.Price : null;
//            }
//            catch (LazadaBlockedException ex)
//            {
//                _logger.LogWarning("[Lazada] Bị chặn / cần xác minh khi cập nhật giá {Link}: {Msg}", productLink, ex.Message);
//                return null;
//            }
//            catch (LazadaProductNotFoundException ex)
//            {
//                _logger.LogWarning("[Lazada] Sản phẩm không còn tồn tại {Link}: {Msg}", productLink, ex.Message);
//                return null;
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning(ex, "[Lazada] Không thể cập nhật giá cho {Link}", productLink);
//                return null;
//            }
//        }

//        public async Task<List<SuggestionProduct>> CrawlSuggestionsAsync(Guid productId, string searchKeyword, decimal initialPrice)
//        {
//            var result = new List<SuggestionProduct>();
//            if (string.IsNullOrWhiteSpace(searchKeyword) || initialPrice <= 0) return result;

//            try
//            {
//                foreach (var keyword in ScraperTextHelper.BuildKeywordCandidates(searchKeyword))
//                {
//                    var found = await RunAsync(ctx => SearchAsync(ctx, keyword));

//                    var kept = found
//                        .Where(c => ScraperTextHelper.IsAcceptableSuggestionPrice(c.Price, initialPrice))
//                        .Take(ScraperTextHelper.MaxSuggestions)
//                        .ToList();

//                    _logger.LogInformation("[Lazada] Gợi ý q='{Keyword}': {Total} kết quả, {Kept} hợp lệ (giá gốc {Price})", keyword, found.Count, kept.Count, initialPrice);
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
//            catch (LazadaBlockedException ex)
//            {
//                _logger.LogWarning("[Lazada] Bị chặn / cần xác minh khi tìm gợi ý: {Msg}", ex.Message);
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning(ex, "[Lazada] Không lấy được gợi ý cho productId {Id}", productId);
//            }

//            return result;
//        }

//        public Task<List<LiveEventProductDTO>> GetLiveProductsFromEventAsync(string eventLink)
//        {
//            _logger.LogInformation("[Lazada] Tính năng quét Live Event chưa được triển khai.");
//            return Task.FromResult(new List<LiveEventProductDTO>());
//        }

//        /// <summary>
//        /// Mở cửa sổ Chrome thật (có giao diện) để bạn tự vượt captcha / đăng nhập Lazada.
//        /// Cookie được lưu vào UserDataDir nên các lần cào headless sau dùng lại phiên này.
//        /// Gọi khi gặp LazadaBlockedException (ví dụ từ một endpoint admin).
//        /// </summary>
//        public async Task InteractiveVerifyAsync(string? url = null, TimeSpan? timeout = null, CancellationToken ct = default)
//        {
//            if (_disposed) throw new ObjectDisposedException(nameof(LazadaBrowserService));

//            var wait = timeout ?? TimeSpan.FromMinutes(_options.VerifyTimeoutMinutes);
//            var target = string.IsNullOrWhiteSpace(url) ? LazadaHomeUrl : NormalizeLink(url);

//            using var exclusive = await _scheduler.AcquireExclusiveAsync(ct);
//            await CloseContextAsync();

//            var ctx = await LaunchContextAsync(headless: false, blockHeavy: false);
//            try
//            {
//                var page = await ctx.NewPageAsync();
//                await page.GotoAsync(target, new PageGotoOptions
//                {
//                    WaitUntil = WaitUntilState.DOMContentLoaded,
//                    Timeout = _options.NavigationTimeoutMs
//                });

//                _logger.LogInformation("[Lazada] Hãy vượt captcha (nếu có) trong cửa sổ Chrome vừa mở (tối đa {Min} phút)...", wait.TotalMinutes);

//                var sw = Stopwatch.StartNew();
//                var clearStreak = 0;
//                while (sw.Elapsed < wait && clearStreak < 4)
//                {
//                    ct.ThrowIfCancellationRequested();
//                    await Task.Delay(1000, ct);
//                    clearStreak = await IsBlockedPageAsync(page, unknownAsBlocked: true) ? 0 : clearStreak + 1;
//                }

//                if (clearStreak < 4)
//                    throw new System.TimeoutException("Hết thời gian chờ xác minh Lazada.");

//                await Task.Delay(2000, ct);
//                _logger.LogInformation("[Lazada] Xác minh xong, phiên đã được lưu.");
//            }
//            finally
//            {
//                try { await ctx.CloseAsync(); } catch { }
//            }
//        }

//        // ============================== Vòng retry dùng chung ==============================

//        private async Task<T> RunAsync<T>(Func<IBrowserContext, Task<T>> action)
//        {
//            if (_disposed) throw new ObjectDisposedException(nameof(LazadaBrowserService));

//            var maxAttempts = Math.Max(1, _options.MaxAttempts);
//            for (var attempt = 1; ; attempt++)
//            {
//                var gen = 0;
//                try
//                {
//                    // Slot chỉ giữ trong lúc cào; thời gian chờ retry không chiếm slot.
//                    using var lease = await _scheduler.AcquireAsync();
//                    var (ctx, g) = await EnsureContextAsync();
//                    gen = g;
//                    return await action(ctx);
//                }
//                catch (LazadaBlockedException) { throw; }
//                catch (LazadaProductNotFoundException) { throw; }
//                catch (Exception ex) when (attempt < maxAttempts)
//                {
//                    _logger.LogWarning(ex, "[Lazada] Lần {Attempt}/{Max} thất bại, thử lại...", attempt, maxAttempts);

//                    // Chỉ khởi động lại trình duyệt khi nó thực sự chết; lỗi thường chỉ cần trang mới.
//                    if (BrowserTuning.IsBrowserDead(ex)) await CloseContextAsync(gen);
//                    await Task.Delay(500 * attempt);
//                }
//            }
//        }

//        private Task<ScrapedProduct> ScrapeProductAsync(string link, bool includeDetails)
//        {
//            var url = NormalizeLink(link);
//            return RunAsync(ctx => ScrapeOnceAsync(ctx, url, includeDetails));
//        }

//        // ============================== Cào sản phẩm ==============================

//        private async Task<ScrapedProduct> ScrapeOnceAsync(IBrowserContext context, string link, bool includeDetails)
//        {
//            var page = await context.NewPageAsync();

//            var apiResult = new TaskCompletionSource<ScrapedProduct>(TaskCreationOptions.RunContinuationsAsynchronously);
//            var apiLog = new ConcurrentQueue<string>();
//            string? lastApiBody = null;

//            async void OnResponse(object? sender, IResponse response)
//            {
//                try
//                {
//                    var url = response.Url;
//                    var interesting = url.Contains("pdp", StringComparison.OrdinalIgnoreCase)
//                                      || url.Contains("mtop", StringComparison.OrdinalIgnoreCase);
//                    if (!interesting) return;

//                    if (apiLog.Count < 60) apiLog.Enqueue($"{response.Status} {Shorten(url)}");
//                    if (response.Status != 200) return;
//                    if (!url.Contains("pdp/layout", StringComparison.OrdinalIgnoreCase)) return;

//                    var text = await response.TextAsync();
//                    lastApiBody = text.Length > 200_000 ? text[..200_000] : text;

//                    using var doc = JsonDocument.Parse(text);
//                    var parsed = ParsePdpLayout(doc.RootElement);
//                    if (parsed is { Price: > 0 }) apiResult.TrySetResult(parsed);
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogDebug(ex, "[Lazada] Lỗi khi xử lý response");
//                }
//            }

//            page.Response += OnResponse;

//            try
//            {
//                if (_options.Headless) await FixHeadlessUserAgentAsync(page);

//                _logger.LogInformation("[Lazada] Bắt đầu cào: {Url}", link);
//                var nav = await page.GotoAsync(link, new PageGotoOptions
//                {
//                    WaitUntil = WaitUntilState.DOMContentLoaded,
//                    Timeout = _options.NavigationTimeoutMs
//                });

//                var status = nav?.Status;
//                _logger.LogInformation("[Lazada] Status={Status} FinalUrl={Url}", status, page.Url);

//                if (status is 404 or 410)
//                    throw new LazadaProductNotFoundException($"HTTP {status}");
//                if (status is 403 or 429)
//                    throw new LazadaBlockedException($"HTTP {status}", page.Url);
//                if (IsBlockedUrl(page.Url))
//                    throw new LazadaBlockedException("Lazada yêu cầu xác minh / đăng nhập", page.Url);

//                // Đường nhanh: poll ngay, KHÔNG humanize trước. Chỉ "động chuột" nếu 2.5s vẫn chưa có giá.
//                var sw = Stopwatch.StartNew();
//                ScrapedProduct? picked = null;
//                ScrapedProduct? candidate = null;
//                var bestName = string.Empty;
//                var bestImage = string.Empty;
//                var nudged = false;

//                while (sw.ElapsedMilliseconds < _options.ExtractTimeoutMs)
//                {
//                    var extracted = await TryExtractAsync(page);

//                    if (extracted != null)
//                    {
//                        if (!string.IsNullOrWhiteSpace(extracted.Name)) bestName = extracted.Name;
//                        if (!string.IsNullOrWhiteSpace(extracted.ImageUrl)) bestImage = extracted.ImageUrl;
//                    }

//                    if (extracted is { Price: > 0 })
//                    {
//                        if (extracted.Source == "dom") { picked = extracted; break; }
//                        candidate ??= extracted;
//                    }
//                    else if (await IsBlockedPageAsync(page, unknownAsBlocked: false))
//                    {
//                        throw new LazadaBlockedException("Lazada hiện captcha / chặn bot", page.Url);
//                    }

//                    if (candidate is null && apiResult.Task.IsCompletedSuccessfully)
//                        candidate = apiResult.Task.Result;

//                    if (candidate != null && sw.ElapsedMilliseconds >= _options.DomGraceMs) break;

//                    if (!nudged && candidate is null && sw.ElapsedMilliseconds >= 2_500)
//                    {
//                        nudged = true;
//                        await HumanizeAsync(page);
//                    }

//                    await Task.Delay(_options.PollIntervalMs);
//                }

//                var chosen = picked ?? candidate;
//                if (chosen == null)
//                {
//                    if (await IsBlockedPageAsync(page, unknownAsBlocked: false))
//                        throw new LazadaBlockedException("Lazada hiện captcha / chặn bot", page.Url);

//                    var title = await SafeTitleAsync(page);
//                    throw new InvalidOperationException($"Không lấy được giá sản phẩm. FinalUrl={page.Url}, Title='{title}'");
//                }

//                var result = Complete(chosen, bestName, bestImage);

//                if (includeDetails)
//                {
//                    var description = await ExtractDescriptionAsync(page);
//                    result = result with { Description = description };
//                    _logger.LogInformation("[Lazada] Mô tả: {Len} ký tự", description.Length);
//                }

//                return result;
//            }
//            catch (Exception ex)
//            {
//                if (ex is not LazadaProductNotFoundException)
//                {
//                    var tag = ex is LazadaBlockedException ? "blocked" : "failed";
//                    await SaveDebugAsync(page, tag, apiLog, lastApiBody);
//                }
//                throw;
//            }
//            finally
//            {
//                page.Response -= OnResponse;
//                try { await page.CloseAsync(); } catch { }
//            }
//        }

//        private ScrapedProduct Complete(ScrapedProduct product, string fallbackName, string fallbackImage)
//        {
//            var name = CleanName(string.IsNullOrWhiteSpace(product.Name) ? fallbackName : product.Name);
//            var image = NormalizeImageUrl(string.IsNullOrWhiteSpace(product.ImageUrl) ? fallbackImage : product.ImageUrl);

//            if (product.Price < 1_000_000_000m)
//            {
//                _logger.LogInformation("[Lazada] Lấy giá từ {Source}: {Price}", product.Source, product.Price);
//                return product with { Name = name, ImageUrl = image };
//            }

//            throw new InvalidOperationException($"Giá bất thường ({product.Price}) từ nguồn {product.Source}.");
//        }

//        private async Task<ScrapedProduct?> TryExtractAsync(IPage page)
//        {
//            try
//            {
//                var el = await page.EvaluateAsync<JsonElement>(ExtractorJs);
//                var price = Math.Round(GetNumber(el, "price"));
//                return new ScrapedProduct(GetString(el, "name"), price, GetString(el, "imageUrl"), GetString(el, "source"));
//            }
//            catch (PlaywrightException)
//            {
//                // trang đang điều hướng / execution context bị hủy → thử lại vòng sau
//                return null;
//            }
//        }

//        private static ScrapedProduct? ParsePdpLayout(JsonElement root)
//        {
//            var priceText = GetStringOrNumber(root, "module", "price", "features", "price");
//            var price = ParsePrice(priceText);
//            if (price <= 0) return null;

//            var name = GetString(root, "module", "item", "features", "title");
//            var image = GetString(root, "module", "image", "features", "primaryImage");
//            return new ScrapedProduct(name, price, image, "api");
//        }

//        // ============================== Mô tả sản phẩm ==============================

//        private async Task<string> ExtractDescriptionAsync(IPage page)
//        {
//            var fallback = string.Empty;
//            var sw = Stopwatch.StartNew();

//            while (true)
//            {
//                var (text, source) = await TryDescriptionAsync(page);
//                if (text.Length > 0)
//                {
//                    if (source is "module" or "dom") return ScraperTextHelper.ToPlainText(text);
//                    if (fallback.Length == 0) fallback = text;
//                }

//                if (sw.ElapsedMilliseconds >= _options.DescriptionWaitMs) break;

//                // Lazada chỉ render mục mô tả khi cuộn tới nó
//                try { await page.EvaluateAsync(ScrollToDetailJs); } catch (PlaywrightException) { }
//                await Task.Delay(250);
//            }

//            return ScraperTextHelper.ToPlainText(fallback);
//        }

//        private static async Task<(string Text, string Source)> TryDescriptionAsync(IPage page)
//        {
//            try
//            {
//                var el = await page.EvaluateAsync<JsonElement>(DescriptionJs);
//                return (GetString(el, "text").Trim(), GetString(el, "source"));
//            }
//            catch (PlaywrightException)
//            {
//                return (string.Empty, string.Empty);
//            }
//        }

//        // ============================== Gợi ý sản phẩm (tìm kiếm) ==============================

//        private async Task<List<SuggestionCandidate>> SearchAsync(IBrowserContext ctx, string keyword)
//        {
//            // Đường nhanh: fetch JSON trong trang lazada.vn đang mở sẵn (~vài trăm ms).
//            var fast = await TrySearchViaFetchAsync(ctx, keyword);
//            if (fast != null) return fast;

//            _logger.LogInformation("[Lazada] Fetch tìm kiếm không dùng được, chuyển sang mở trang tìm kiếm...");
//            return await SearchViaPageAsync(ctx, keyword);
//        }

//        private async Task<List<SuggestionCandidate>?> TrySearchViaFetchAsync(IBrowserContext ctx, string keyword)
//        {
//            try
//            {
//                var page = await GetWorkerPageAsync(ctx);
//                var path = $"/catalog/?ajax=true&q={Uri.EscapeDataString(keyword)}";
//                var json = await page.EvaluateAsync<string>(FetchJs, path);

//                using var wrapper = JsonDocument.Parse(json);
//                var status = wrapper.RootElement.GetProperty("status").GetInt32();
//                var body = wrapper.RootElement.GetProperty("body").GetString() ?? string.Empty;
//                if (status != 200)
//                {
//                    _logger.LogDebug("[Lazada] fetch tìm kiếm trả HTTP {Status}", status);
//                    return null;
//                }

//                using var doc = JsonDocument.Parse(body);
//                var root = doc.RootElement;
//                if (root.ValueKind != JsonValueKind.Object) return null;
//                if (Navigate(root, "mods") is not { ValueKind: JsonValueKind.Object } mods) return null; // thường là JSON chặn bot (rgv587_flag...)

//                if (Navigate(mods, "listItems") is { ValueKind: JsonValueKind.Array } items)
//                    return ParseListItems(items);

//                return new List<SuggestionCandidate>(); // tìm không ra kết quả
//            }
//            catch (LazadaBlockedException) { throw; }
//            catch (JsonException) { return null; }
//            catch (PlaywrightException)
//            {
//                _workerPage = null;
//                return null;
//            }
//        }

//        private async Task<IPage> GetWorkerPageAsync(IBrowserContext ctx)
//        {
//            if (_workerPage is { IsClosed: false } existing) return existing;

//            await _workerLock.WaitAsync();
//            try
//            {
//                if (_workerPage is { IsClosed: false } again) return again;

//                var page = await ctx.NewPageAsync();
//                try
//                {
//                    if (_options.Headless) await FixHeadlessUserAgentAsync(page);
//                    await page.GotoAsync(LazadaHomeUrl, new PageGotoOptions
//                    {
//                        WaitUntil = WaitUntilState.DOMContentLoaded,
//                        Timeout = _options.NavigationTimeoutMs
//                    });

//                    if (IsBlockedUrl(page.Url))
//                        throw new LazadaBlockedException("Lazada yêu cầu xác minh / đăng nhập", page.Url);
//                }
//                catch
//                {
//                    try { await page.CloseAsync(); } catch { }
//                    throw;
//                }

//                _workerPage = page;
//                return page;
//            }
//            finally
//            {
//                _workerLock.Release();
//            }
//        }

//        private async Task<List<SuggestionCandidate>> SearchViaPageAsync(IBrowserContext ctx, string keyword)
//        {
//            var page = await ctx.NewPageAsync();
//            try
//            {
//                if (_options.Headless) await FixHeadlessUserAgentAsync(page);

//                var url = $"{LazadaHomeUrl}/catalog/?q={Uri.EscapeDataString(keyword)}";
//                _logger.LogInformation("[Lazada] Tìm gợi ý: {Url}", url);

//                var nav = await page.GotoAsync(url, new PageGotoOptions
//                {
//                    WaitUntil = WaitUntilState.DOMContentLoaded,
//                    Timeout = _options.NavigationTimeoutMs
//                });

//                if (nav is { Status: 403 or 429 })
//                    throw new LazadaBlockedException($"HTTP {nav.Status}", page.Url);
//                if (IsBlockedUrl(page.Url))
//                    throw new LazadaBlockedException("Lazada yêu cầu xác minh / đăng nhập", page.Url);

//                var sw = Stopwatch.StartNew();
//                while (sw.ElapsedMilliseconds < _options.ExtractTimeoutMs)
//                {
//                    try
//                    {
//                        var json = await page.EvaluateAsync<string>(SearchPageDataJs);
//                        if (!string.IsNullOrEmpty(json))
//                        {
//                            using var doc = JsonDocument.Parse(json);
//                            var parsed = ParseListItems(doc.RootElement);
//                            if (parsed.Count > 0) return parsed;
//                        }

//                        var el = await page.EvaluateAsync<JsonElement>(SearchDomJs);
//                        if (el.ValueKind == JsonValueKind.Array)
//                        {
//                            var parsed = ParseDomItems(el);
//                            if (parsed.Count > 0) return parsed;
//                        }
//                    }
//                    catch (PlaywrightException) { }
//                    catch (JsonException) { }

//                    if (await IsBlockedPageAsync(page, unknownAsBlocked: false))
//                        throw new LazadaBlockedException("Lazada hiện captcha / chặn bot", page.Url);

//                    await Task.Delay(300);
//                }

//                _logger.LogWarning("[Lazada] Không đọc được kết quả tìm kiếm cho '{Keyword}' (FinalUrl={Url})", keyword, page.Url);
//                await SaveDebugAsync(page, "search_empty", new ConcurrentQueue<string>(), null);
//                return new List<SuggestionCandidate>();
//            }
//            catch (LazadaBlockedException)
//            {
//                await SaveDebugAsync(page, "search_blocked", new ConcurrentQueue<string>(), null);
//                throw;
//            }
//            finally
//            {
//                try { await page.CloseAsync(); } catch { }
//            }
//        }

//        private static List<SuggestionCandidate> ParseListItems(JsonElement items)
//        {
//            var list = new List<SuggestionCandidate>();
//            if (items.ValueKind != JsonValueKind.Array) return list;

//            var seen = new HashSet<string>();
//            foreach (var item in items.EnumerateArray())
//            {
//                if (item.ValueKind != JsonValueKind.Object) continue;

//                var name = CleanName(GetString(item, "name"));
//                var price = ParsePrice(GetStringOrNumber(item, "price"));
//                if (price <= 0) price = ParsePrice(GetStringOrNumber(item, "priceShow"));

//                var rawLink = GetString(item, "itemUrl");
//                if (rawLink.Length == 0) rawLink = GetString(item, "productUrl");
//                var link = BuildProductLink(rawLink);

//                if (name.Length == 0 || price <= 0 || link.Length == 0 || !seen.Add(link)) continue;

//                list.Add(new SuggestionCandidate(name, price, NormalizeImageUrl(GetString(item, "image")), link));
//            }
//            return list;
//        }

//        private static List<SuggestionCandidate> ParseDomItems(JsonElement items)
//        {
//            var list = new List<SuggestionCandidate>();
//            var seen = new HashSet<string>();

//            foreach (var item in items.EnumerateArray())
//            {
//                var name = CleanName(GetString(item, "name"));
//                var price = ParsePrice(GetString(item, "priceText"));
//                var link = BuildProductLink(GetString(item, "href"));

//                if (name.Length == 0 || price <= 0 || link.Length == 0 || !seen.Add(link)) continue;

//                list.Add(new SuggestionCandidate(name, price, NormalizeImageUrl(GetString(item, "imageUrl")), link));
//            }
//            return list;
//        }

//        /// <summary>"//www.lazada.vn/products/abc-i123-s456.html?search=1" → "https://www.lazada.vn/products/abc-i123-s456.html" (bỏ query theo dõi).</summary>
//        private static string BuildProductLink(string raw)
//        {
//            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

//            var s = raw.Trim();
//            if (s.StartsWith("//")) s = "https:" + s;
//            else if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
//                s = LazadaHomeUrl + (s.StartsWith('/') ? string.Empty : "/") + s;

//            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return string.Empty;
//            if (!(uri.Host.Equals("lazada.vn", StringComparison.OrdinalIgnoreCase)
//                  || uri.Host.EndsWith(".lazada.vn", StringComparison.OrdinalIgnoreCase))) return string.Empty;

//            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
//        }

//        // ============================== Trình duyệt ==============================

//        private async Task<(IBrowserContext Ctx, int Gen)> EnsureContextAsync()
//        {
//            if (_context is { } existing) return (existing, _generation);

//            await _ctxLock.WaitAsync();
//            try
//            {
//                if (_context is { } again) return (again, _generation);

//                var fresh = IsFreshProfile();
//                var ctx = await LaunchContextAsync(_options.Headless, blockHeavy: true);
//                _context = ctx;
//                _generation++;

//                // Bỏ warm-up (mở trang chủ ~3-5s) nếu profile đã có cookie từ lần chạy trước.
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
//                var dir = _options.UserDataDir;
//                return !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();
//            }
//            catch { return true; }
//        }

//        private async Task<IBrowserContext> LaunchContextAsync(bool headless, bool blockHeavy)
//        {
//            _playwright ??= await Playwright.CreateAsync();
//            Directory.CreateDirectory(_options.UserDataDir);

//            var args = new List<string> { "--disable-blink-features=AutomationControlled", "--disable-dev-shm-usage", "--lang=vi-VN" };
//            if (OperatingSystem.IsLinux()) args.Add("--no-sandbox");

//            BrowserTypeLaunchPersistentContextOptions Build(string? channel) => new()
//            {
//                Headless = headless,
//                Channel = channel,
//                Locale = "vi-VN",
//                TimezoneId = "Asia/Ho_Chi_Minh",
//                ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
//                IgnoreDefaultArgs = new[] { "--enable-automation" },
//                Args = args
//            };

//            IBrowserContext ctx;
//            if (!string.IsNullOrWhiteSpace(_options.Channel))
//            {
//                try
//                {
//                    ctx = await _playwright.Chromium.LaunchPersistentContextAsync(_options.UserDataDir, Build(_options.Channel));
//                }
//                catch (PlaywrightException ex)
//                {
//                    _logger.LogInformation("[Lazada] Không mở được Chrome '{Channel}' ({Msg}) → dùng Chromium của Playwright.", _options.Channel, FirstLine(ex.Message));
//                    ctx = await _playwright.Chromium.LaunchPersistentContextAsync(_options.UserDataDir, Build(null));
//                }
//            }
//            else
//            {
//                ctx = await _playwright.Chromium.LaunchPersistentContextAsync(_options.UserDataDir, Build(null));
//            }

//            await ctx.AddInitScriptAsync(StealthScript);

//            if (blockHeavy && _options.BlockHeavyResources)
//                await BrowserTuning.BlockHeavyResourcesAsync(ctx);

//            ctx.Close += (_, _) =>
//            {
//                if (ReferenceEquals(_context, ctx))
//                {
//                    _context = null;
//                    _workerPage = null;
//                }
//            };
//            return ctx;
//        }

//        private async Task WarmUpAsync(IBrowserContext ctx)
//        {
//            try
//            {
//                _logger.LogInformation("[Lazada] Khởi tạo session Lazada VN...");
//                var page = await ctx.NewPageAsync();
//                try
//                {
//                    if (_options.Headless) await FixHeadlessUserAgentAsync(page);
//                    await page.GotoAsync(LazadaHomeUrl, new PageGotoOptions
//                    {
//                        WaitUntil = WaitUntilState.DOMContentLoaded,
//                        Timeout = 30_000
//                    });
//                    await HumanizeAsync(page);

//                    if (IsBlockedUrl(page.Url))
//                        _logger.LogWarning("[Lazada] Trang chủ đã bị chuyển tới trang xác minh ({Url}). Có thể cần InteractiveVerifyAsync.", page.Url);
//                }
//                finally
//                {
//                    try { await page.CloseAsync(); } catch { }
//                }
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning("[Lazada] Warm-up thất bại (bỏ qua): {Msg}", FirstLine(ex.Message));
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
//                _workerPage = null;
//                if (ctx != null)
//                {
//                    try { await ctx.CloseAsync(); } catch { }
//                }
//            }
//            finally
//            {
//                _ctxLock.Release();
//            }
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
//                _logger.LogDebug(ex, "[Lazada] Không đổi được UA headless");
//            }
//        }

//        private static async Task HumanizeAsync(IPage page)
//        {
//            try
//            {
//                var rnd = Random.Shared;
//                await Task.Delay(rnd.Next(300, 700));
//                for (var i = 0; i < 2; i++)
//                {
//                    await page.Mouse.MoveAsync(rnd.Next(200, 1100), rnd.Next(150, 600), new MouseMoveOptions { Steps = rnd.Next(8, 20) });
//                }
//                await page.Mouse.WheelAsync(0, rnd.Next(200, 500));
//                await Task.Delay(rnd.Next(200, 400));
//            }
//            catch { }
//        }

//        // ============================== Phát hiện chặn ==============================

//        private static bool IsBlockedUrl(string url)
//        {
//            if (string.IsNullOrEmpty(url)) return false;
//            var u = url.ToLowerInvariant();
//            return u.Contains("_____tmd_____")
//                   || u.Contains("/punish")
//                   || u.Contains("captcha")
//                   || u.Contains("login.lazada")
//                   || u.Contains("/user/login")
//                   || u.Contains("/member/login");
//        }

//        private static async Task<bool> IsBlockedPageAsync(IPage page, bool unknownAsBlocked)
//        {
//            if (IsBlockedUrl(page.Url)) return true;
//            try
//            {
//                return await page.EvaluateAsync<bool>(BlockedDomJs);
//            }
//            catch (PlaywrightException)
//            {
//                return unknownAsBlocked;
//            }
//        }

//        // ============================== Debug ==============================

//        private async Task SaveDebugAsync(IPage page, string tag, ConcurrentQueue<string> apiLog, string? apiBody)
//        {
//            if (string.IsNullOrWhiteSpace(_options.DebugDirectory)) return;

//            try
//            {
//                Directory.CreateDirectory(_options.DebugDirectory);
//                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
//                var basePath = Path.Combine(_options.DebugDirectory, $"{stamp}_{tag}");
//                var title = await SafeTitleAsync(page);
//                _logger.LogWarning("[Lazada] DEBUG Url={Url} Title='{Title}' → {Path}.*", page.Url, title, basePath);

//                var lines = new List<string> { $"Url: {page.Url}", $"Title: {title}", "--- API đã thấy ---" };
//                lines.AddRange(apiLog.ToArray());
//                await File.WriteAllLinesAsync(basePath + "_apicalls.txt", lines);

//                if (!string.IsNullOrEmpty(apiBody)) await File.WriteAllTextAsync(basePath + "_api.json", apiBody);
//                await page.ScreenshotAsync(new PageScreenshotOptions { Path = basePath + ".png", FullPage = true });
//                await File.WriteAllTextAsync(basePath + ".html", await page.ContentAsync());
//            }
//            catch (Exception ex)
//            {
//                _logger.LogDebug(ex, "[Lazada] Không lưu được debug artifacts");
//            }
//        }

//        private static async Task<string> SafeTitleAsync(IPage page)
//        {
//            try { return await page.TitleAsync(); } catch { return string.Empty; }
//        }

//        // ============================== Helpers ==============================

//        private static string NormalizeLink(string link)
//        {
//            if (string.IsNullOrWhiteSpace(link))
//                throw new ArgumentException("Link Lazada trống.", nameof(link));

//            var s = link.Trim();
//            if (s.StartsWith("//")) s = "https:" + s;
//            if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;

//            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)
//                || !(uri.Host.Equals("lazada.vn", StringComparison.OrdinalIgnoreCase)
//                     || uri.Host.EndsWith(".lazada.vn", StringComparison.OrdinalIgnoreCase)))
//                throw new ArgumentException($"Link không phải Lazada VN: {link}", nameof(link));

//            return uri.AbsoluteUri;
//        }

//        /// <summary>
//        /// Parse giá bất kể định dạng: "129000", "129000.00", "129.000", "1.299.000", "99,000", "129.000,50".
//        /// Luôn dùng InvariantCulture để không bị lệch theo culture của máy (vd vi-VN coi "." là phân cách hàng nghìn).
//        /// </summary>
//        private static decimal ParsePrice(string? raw)
//        {
//            if (string.IsNullOrWhiteSpace(raw)) return 0m;

//            var s = Regex.Replace(raw, @"[^\d.,]", "");
//            if (s.Length == 0) return 0m;

//            if (Regex.IsMatch(s, @"^\d{1,3}(\.\d{3})+(,\d+)?$"))
//                s = s.Replace(".", "").Replace(',', '.');
//            else if (Regex.IsMatch(s, @"^\d{1,3}(,\d{3})+(\.\d+)?$"))
//                s = s.Replace(",", "");
//            else
//                s = s.Replace(',', '.');

//            return decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && d > 0
//                ? Math.Round(d)
//                : 0m;
//        }

//        private static string CleanName(string? name)
//        {
//            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
//            name = Regex.Replace(name.Trim(), @"\s*[|\-–]\s*Lazada(\.vn)?\s*$", "", RegexOptions.IgnoreCase).Trim();

//            if (name.Equals("Lazada", StringComparison.OrdinalIgnoreCase)
//                || name.Equals("Lazada.vn", StringComparison.OrdinalIgnoreCase)
//                || name.StartsWith("Lazada Việt Nam", StringComparison.OrdinalIgnoreCase)
//                || name.StartsWith("Đăng nhập", StringComparison.OrdinalIgnoreCase)
//                || name.StartsWith("Login", StringComparison.OrdinalIgnoreCase))
//                return string.Empty;

//            return name;
//        }

//        private static string NormalizeImageUrl(string? value)
//        {
//            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
//            value = value.Trim();
//            if (value.StartsWith("//")) return "https:" + value;
//            return value;
//        }

//        private static JsonElement? Navigate(JsonElement root, params string[] path)
//        {
//            var cur = root;
//            foreach (var segment in path)
//            {
//                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(segment, out var next)) return null;
//                cur = next;
//            }
//            return cur;
//        }

//        private static string GetString(JsonElement root, params string[] path)
//        {
//            if (Navigate(root, path) is { ValueKind: JsonValueKind.String } el)
//                return el.GetString() ?? string.Empty;
//            return string.Empty;
//        }

//        private static string GetStringOrNumber(JsonElement root, params string[] path)
//        {
//            if (Navigate(root, path) is not { } el) return string.Empty;
//            return el.ValueKind switch
//            {
//                JsonValueKind.String => el.GetString() ?? string.Empty,
//                JsonValueKind.Number => el.GetRawText(),
//                _ => string.Empty
//            };
//        }

//        private static decimal GetNumber(JsonElement root, params string[] path)
//        {
//            if (Navigate(root, path) is not { } el) return 0m;
//            if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d > 0 ? d : 0m;
//            if (el.ValueKind == JsonValueKind.String) return ParsePrice(el.GetString());
//            return 0m;
//        }

//        private static string Shorten(string url)
//        {
//            try { var u = new Uri(url); var s = u.PathAndQuery; return s.Length > 160 ? s[..160] + "…" : s; }
//            catch { return url.Length > 160 ? url[..160] + "…" : url; }
//        }

//        private static string FirstLine(string text)
//        {
//            if (string.IsNullOrEmpty(text)) return string.Empty;
//            var idx = text.IndexOf('\n');
//            return idx > 0 ? text[..idx].Trim() : text.Trim();
//        }

//        public async ValueTask DisposeAsync()
//        {
//            if (_disposed) return;
//            _disposed = true;

//            await CloseContextAsync();
//            try { _playwright?.Dispose(); } catch { }
//            _playwright = null;
//            _scheduler.Dispose();
//            _ctxLock.Dispose();
//            _workerLock.Dispose();
//        }
//    }
//}