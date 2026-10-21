using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;

namespace ePriTrackerBackend.Services
{
    /// <summary>Chotot chặn bot (403/429/trả HTML thay vì JSON) và đã hết số lần thử.</summary>
    public class ChototBlockedException : Exception
    {
        public int? StatusCode { get; }
        public ChototBlockedException(string message, int? statusCode = null) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    /// <summary>Tin đăng không còn tồn tại (404/410, đã xóa, đã bán, hết hạn).</summary>
    public class ChototProductNotFoundException : Exception
    {
        public ChototProductNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Crawl Chotot bằng HTTP trực tiếp tới gateway.chotot.com (API JSON công khai, không cần trình duyệt/proxy).
    /// Nhanh hơn nhiều so với Playwright: không khởi tạo browser, dùng HTTP/2 + nén + kết nối được tái sử dụng.
    /// </summary>
    public class ChototService : IEcommerceService, IAsyncDisposable
    {
        public string PlatformName => "Chotot";

        private const string WebBaseUrl = "https://www.chotot.com";
        private const string GatewayBaseUrl = "https://gateway.chotot.com";

        private const int MaxAttempts = 3;
        private const int RequestTimeoutMs = 12_000;

        // API JSON nhẹ, không cần trình duyệt → có thể song song cao hơn Tiki.
        // Nếu thấy HTTP 429 nhiều thì giảm MaxParallel hoặc tăng MinRequestIntervalMs.
        private const int MaxParallel = 8;
        private const int MinRequestIntervalMs = 60;
        private const int RequestJitterMs = 60;

        private const int SearchPageSize = 20;     // API trả tối đa ~20 tin/trang
        private const int MaxSearchPages = 3;      // chỉ lật trang tiếp khi chưa đủ gợi ý sau khi lọc
        private const int MaxSuggestions = 10;
        private const int MaxKeywordLength = 100;
        private const decimal MinValidPrice = 1_000m; // lọc tin giá "bait" (vd 0đ, 1đ, 121đ)

        // Trạng thái tin coi như không còn (giả định theo schema Chotot, cần đối chiếu response thực tế).
        private static readonly HashSet<string> InactiveStates = new(StringComparer.OrdinalIgnoreCase)
        {
            "deleted", "removed", "expired", "sold", "rejected", "hidden"
        };

        private readonly ILogger<ChototService> _logger;
        private readonly ScrapeScheduler _scheduler = new(MaxParallel, MinRequestIntervalMs, RequestJitterMs);
        private readonly HttpClient _http;

        // Gộp các request trùng nhau đang chạy (vd FetchProductData + FetchCurrentPrice cùng lúc cho một tin).
        private readonly ConcurrentDictionary<string, Lazy<Task<AdData>>> _inflight = new();

        private bool _disposed;

        private sealed record HttpResult(int Status, byte[] Body, TimeSpan? RetryAfter);
        private sealed record AdData(string Name, decimal Price, string ImageUrl, string Description);
        private sealed record SuggestionItem(string Id, string Name, decimal Price, string ImageUrl, string Link);

        private static readonly Regex AdIdRegex = new(@"/(\d{5,})\.htm(?:[/?#]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NextDataRegex = new(
            @"<script[^>]*id=""__NEXT_DATA__""[^>]*>(.*?)</script>",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex PriceTextRegex = new(
            @"(\d+(?:[.,]\d+)*)\s*(tỷ|tỉ|triệu|tr|nghìn|k)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ScraperMetricsService để optional: nếu chưa đăng ký trong DI thì service vẫn khởi tạo được.
        public ChototService(ILogger<ChototService> logger, ScraperMetricsService? metrics = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ = metrics;

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true,
                MaxConnectionsPerServer = MaxParallel * 2,
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
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", WebBaseUrl + "/");
        }

        public bool CanHandle(string url)
            => url.Contains("chotot.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("nhatot.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("xetot.com", StringComparison.OrdinalIgnoreCase);

        // ============================== Public API ==============================

        public async Task<Product> FetchProductDataAsync(string chototProductId, string normalizedLink)
        {
            var id = ResolveId(chototProductId, normalizedLink)
                ?? throw new ArgumentException($"Không xác định được id tin Chotot từ link: {normalizedLink}", nameof(normalizedLink));

            var data = await GetAdAsync(id);
            if (data.Price < MinValidPrice)
                throw new InvalidOperationException($"Tin Chotot {id} không có giá hợp lệ (thỏa thuận/miễn phí/giá ảo).");

            return new Product
            {
                ProductLink = normalizedLink,
                ProductName = string.IsNullOrWhiteSpace(data.Name) ? $"Chotot {id}" : data.Name,
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
                var id = ResolveId(null, productLink);
                if (id == null)
                {
                    _logger.LogWarning("[Chotot] Không tách được id tin từ link: {Link}", productLink);
                    return null;
                }

                var data = await GetAdAsync(id);
                return data.Price >= MinValidPrice ? data.Price : null;
            }
            catch (ChototProductNotFoundException ex)
            {
                _logger.LogWarning("[Chotot] Tin không còn tồn tại {Link}: {Msg}", productLink, ex.Message);
                return null;
            }
            catch (ChototBlockedException ex)
            {
                _logger.LogWarning("[Chotot] Bị chặn khi cập nhật giá {Link}: {Msg}", productLink, ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Chotot] Không thể cập nhật giá cho {Link}", productLink);
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

                // Lật trang chỉ khi chưa đủ gợi ý sau khi lọc theo giá; dừng sớm khi hết kết quả.
                for (var page = 0; page < MaxSearchPages && result.Count < MaxSuggestions; page++)
                {
                    var url = $"{GatewayBaseUrl}/v1/public/ad-listing?q={Uri.EscapeDataString(keyword)}&limit={SearchPageSize}&o={page * SearchPageSize}";

                    (List<SuggestionItem> Items, int RawCount) parsed;
                    try
                    {
                        parsed = await CallApiAsync(url, ParseSuggestions);
                    }
                    catch (Exception ex) when (page > 0)
                    {
                        // Trang 2+ lỗi thì giữ lại những gì đã có ở trang trước.
                        _logger.LogWarning("[Chotot] Lỗi khi lật trang {Page} gợi ý: {Msg}", page + 1, FirstLine(ex.Message));
                        break;
                    }

                    foreach (var item in parsed.Items)
                    {
                        if (!seen.Add(item.Id)) continue;
                        if (item.Price < MinValidPrice || item.Price > initialPrice) continue;
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
                _logger.LogWarning(ex, "[Chotot] Không lấy được gợi ý cho productId {Id}", productId);
            }

            return result;
        }

        public Task<List<LiveEventProductDTO>> GetLiveProductsFromEventAsync(string eventLink)
            => Task.FromResult(new List<LiveEventProductDTO>());

        // ============================== Lấy dữ liệu tin đăng ==============================

        private async Task<AdData> GetAdAsync(string id)
        {
            var lazy = _inflight.GetOrAdd(id, k => new Lazy<Task<AdData>>(() => LoadAdAsync(k)));
            try
            {
                return await lazy.Value;
            }
            finally
            {
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<AdData>>>(id, lazy));
            }
        }

        private async Task<AdData> LoadAdAsync(string id)
        {
            try
            {
                return await CallApiAsync($"{GatewayBaseUrl}/v1/public/ad-listing/{id}", root => ParseAdResponse(root, id));
            }
            catch (ChototProductNotFoundException) { throw; }
            catch (ChototBlockedException) { throw; }   // bị chặn thì không dồn thêm request trang HTML
            catch (ObjectDisposedException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("[Chotot] API lỗi cho {Id} ({Msg}), thử đọc dữ liệu từ trang tin...", id, FirstLine(ex.Message));
            }

            return await FetchFromPageAsync(id);
        }

        private static AdData ParseAdResponse(JsonElement root, string id)
        {
            // Response chi tiết thường dạng { "ad": {...}, "ad_params": {...} }; nếu không có "ad" thì coi root là ad.
            var ad = Navigate(root, "ad") is { ValueKind: JsonValueKind.Object } a ? a : root;
            var data = ParseAdNode(ad, id);

            if (string.IsNullOrEmpty(data.Name) && data.Price <= 0)
                throw new InvalidOperationException("Response API không chứa dữ liệu tin đăng (schema đổi?).");

            return data;
        }

        private static AdData ParseAdNode(JsonElement ad, string id)
        {
            var state = GetString(ad, "state");
            var status = GetString(ad, "status");
            if (InactiveStates.Contains(state) || InactiveStates.Contains(status))
                throw new ChototProductNotFoundException($"Tin {id} không còn hiển thị (state={state}, status={status}).");

            var name = GetString(ad, "subject");
            if (string.IsNullOrEmpty(name)) name = GetString(ad, "title");

            return new AdData(name.Trim(), ExtractPrice(ad), ExtractImage(ad), GetString(ad, "body"));
        }

        // ============================== Fallback: đọc __NEXT_DATA__ của trang tin ==============================

        private async Task<AdData> FetchFromPageAsync(string id)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ChototService));

            HttpResult resp;
            using (var lease = await _scheduler.AcquireAsync())
            {
                resp = await SendAsync($"{WebBaseUrl}/{id}.htm", "text/html,application/xhtml+xml");
            }

            if (resp.Status is 404 or 410) throw new ChototProductNotFoundException($"Trang tin trả HTTP {resp.Status}");
            if (resp.Status is 401 or 403 or 429) throw new ChototBlockedException($"Trang tin trả HTTP {resp.Status}", resp.Status);
            if (resp.Status != 200) throw new InvalidOperationException($"Trang tin trả HTTP {resp.Status}");

            var html = Encoding.UTF8.GetString(resp.Body);
            var m = NextDataRegex.Match(html);
            if (!m.Success) throw new InvalidOperationException("Không tìm thấy __NEXT_DATA__ trong trang tin.");

            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            var node = FindAdNode(doc.RootElement, id)
                ?? throw new InvalidOperationException("Không tìm thấy dữ liệu tin trong __NEXT_DATA__.");

            var data = ParseAdNode(node, id);
            if (string.IsNullOrEmpty(data.Name) && data.Price <= 0)
                throw new InvalidOperationException("Dữ liệu tin trong trang không hợp lệ.");

            _logger.LogInformation("[Chotot] Lấy dữ liệu từ trang tin (fallback) cho {Id}: {Price}", id, data.Price);
            return data;
        }

        private static JsonElement? FindAdNode(JsonElement el, string id, int depth = 0)
        {
            if (depth > 14) return null;

            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("list_id", out var l) && l.ToString() == id && el.TryGetProperty("subject", out _))
                    return el;

                foreach (var p in el.EnumerateObject())
                {
                    var found = FindAdNode(p.Value, id, depth + 1);
                    if (found != null) return found;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    var found = FindAdNode(item, id, depth + 1);
                    if (found != null) return found;
                }
            }

            return null;
        }

        // ============================== Parse danh sách gợi ý ==============================

        private static (List<SuggestionItem> Items, int RawCount) ParseSuggestions(JsonElement root)
        {
            var list = new List<SuggestionItem>();

            var ads = Navigate(root, "ads");
            if (ads is not { ValueKind: JsonValueKind.Array }) ads = Navigate(root, "data");
            if (ads is not { ValueKind: JsonValueKind.Array } arr) return (list, 0);

            var raw = 0;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                raw++;

                var id = item.TryGetProperty("list_id", out var idEl) ? idEl.ToString() : string.Empty;
                if (string.IsNullOrEmpty(id) || !id.All(char.IsDigit)) continue;

                var state = GetString(item, "state");
                if (InactiveStates.Contains(state)) continue;

                var name = GetString(item, "subject");
                if (string.IsNullOrEmpty(name)) name = GetString(item, "title");

                list.Add(new SuggestionItem(id, name.Trim(), ExtractPrice(item), ExtractImage(item), $"{WebBaseUrl}/{id}.htm"));
            }

            return (list, raw);
        }

        // ============================== Gọi API ==============================

        private async Task<T> CallApiAsync<T>(string url, Func<JsonElement, T> map)
        {
            for (var attempt = 1; ; attempt++)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ChototService));

                HttpResult? resp = null;
                Exception? error = null;
                var message = string.Empty;
                var isBlock = false;
                var delayMs = 400 * attempt + Random.Shared.Next(0, 200);

                try
                {
                    // Slot chỉ giữ trong lúc gọi; thời gian chờ retry không chiếm slot.
                    using var lease = await _scheduler.AcquireAsync();
                    resp = await SendAsync(url, "application/json, text/plain, */*");
                }
                catch (OperationCanceledException ex)
                {
                    error = ex; message = $"Request quá {RequestTimeoutMs}ms";
                }
                catch (HttpRequestException ex)
                {
                    error = ex; message = $"Lỗi mạng: {FirstLine(ex.Message)}";
                }

                if (resp != null)
                {
                    if (resp.Status == 200)
                    {
                        JsonDocument? doc = null;
                        try { doc = JsonDocument.Parse(resp.Body); }
                        catch (JsonException)
                        {
                            // HTTP 200 nhưng không phải JSON (thường là trang chặn bot/HTML lỗi)
                            message = $"Chotot trả nội dung không phải JSON: {Shorten(Encoding.UTF8.GetString(resp.Body))}";
                            isBlock = true;
                        }

                        if (doc != null)
                        {
                            using (doc) return map(doc.RootElement); // lỗi từ map (NotFound...) được ném thẳng ra ngoài
                        }
                    }
                    else if (resp.Status is 404 or 410)
                    {
                        throw new ChototProductNotFoundException($"Chotot trả HTTP {resp.Status}: {url}");
                    }
                    else if (resp.Status is 401 or 403)
                    {
                        message = $"Chotot từ chối request (HTTP {resp.Status}): {Shorten(Encoding.UTF8.GetString(resp.Body))}";
                        isBlock = true;
                    }
                    else if (resp.Status == 429)
                    {
                        message = "Chotot giới hạn tần suất (HTTP 429)";
                        isBlock = true;
                        var retryMs = (int)Math.Clamp(resp.RetryAfter?.TotalMilliseconds ?? 3_000, 1_000, 10_000);
                        delayMs += retryMs;
                    }
                    else if (resp.Status >= 500)
                    {
                        message = $"Chotot lỗi máy chủ (HTTP {resp.Status})";
                    }
                    else
                    {
                        throw new InvalidOperationException($"Chotot API trả HTTP {resp.Status}: {Shorten(Encoding.UTF8.GetString(resp.Body))}");
                    }
                }

                if (attempt >= MaxAttempts)
                {
                    if (isBlock) throw new ChototBlockedException(message, resp?.Status);
                    throw new InvalidOperationException(message, error);
                }

                _logger.LogWarning("[Chotot] Lần {Attempt}/{Max} thất bại: {Msg}. Thử lại...", attempt, MaxAttempts, message);
                await Task.Delay(delayMs);
            }
        }

        private async Task<HttpResult> SendAsync(string url, string accept)
        {
            using var cts = new CancellationTokenSource(RequestTimeoutMs);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", accept);
            if (url.StartsWith(GatewayBaseUrl, StringComparison.OrdinalIgnoreCase))
                req.Headers.TryAddWithoutValidation("Origin", WebBaseUrl);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var body = await res.Content.ReadAsByteArrayAsync(cts.Token);

            TimeSpan? retryAfter = null;
            var ra = res.Headers.RetryAfter;
            if (ra?.Delta is { } delta) retryAfter = delta;
            else if (ra?.Date is { } date) retryAfter = date - DateTimeOffset.UtcNow;

            return new HttpResult((int)res.StatusCode, body, retryAfter);
        }

        // ============================== Helpers ==============================

        private static string? ResolveId(string? idHint, string? link)
        {
            if (!string.IsNullOrWhiteSpace(link))
            {
                // Chỉ xét phần path, bỏ query như ?ad_source=recoad&event_source=...
                var path = Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.AbsolutePath : link;
                var m = AdIdRegex.Match(path);
                if (m.Success) return m.Groups[1].Value;
            }

            if (!string.IsNullOrWhiteSpace(idHint))
            {
                var digits = Regex.Match(idHint, @"\d+");
                if (digits.Success) return digits.Value;
            }

            return null;
        }

        private static string NormalizeKeyword(string? keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return string.Empty;
            var k = Regex.Replace(keyword.Trim(), @"\s+", " ");
            return k.Length > MaxKeywordLength ? k[..MaxKeywordLength].Trim() : k;
        }

        private static decimal ExtractPrice(JsonElement ad)
        {
            var price = GetNumber(ad, "price");
            if (price <= 0) price = ParsePriceText(GetString(ad, "price_string"));
            return Math.Round(price);
        }

        /// <summary>Parse chuỗi giá dạng "1.200.000 đ", "1,2 tỷ", "850 triệu". "Thỏa thuận"/"Miễn phí" → 0.</summary>
        private static decimal ParsePriceText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0m;

            var m = PriceTextRegex.Match(text);
            if (!m.Success) return 0m;

            var number = m.Groups[1].Value;
            var unit = m.Groups[2].Value.ToLowerInvariant();

            decimal multiplier = unit switch
            {
                "tỷ" or "tỉ" => 1_000_000_000m,
                "triệu" or "tr" => 1_000_000m,
                "nghìn" or "k" => 1_000m,
                _ => 1m
            };

            // Có đơn vị: dấu , hoặc . là phân cách thập phân (1,2 tỷ). Không đơn vị: là phân cách hàng nghìn (1.200.000).
            var normalized = multiplier > 1m
                ? number.Replace(',', '.')
                : number.Replace(".", string.Empty).Replace(",", string.Empty);

            return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0
                ? v * multiplier
                : 0m;
        }

        private static string ExtractImage(JsonElement ad)
        {
            var image = GetString(ad, "image");
            if (string.IsNullOrEmpty(image)) image = GetString(ad, "thumbnail_image");

            if (string.IsNullOrEmpty(image) && Navigate(ad, "images") is { ValueKind: JsonValueKind.Array } images)
            {
                foreach (var img in images.EnumerateArray())
                {
                    image = img.ValueKind == JsonValueKind.String ? img.GetString() ?? string.Empty
                          : img.ValueKind == JsonValueKind.Object ? GetString(img, "url") : string.Empty;
                    if (!string.IsNullOrEmpty(image)) break;
                }
            }

            return NormalizeImageUrl(image);
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

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;

            _http.Dispose();
            _scheduler.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}