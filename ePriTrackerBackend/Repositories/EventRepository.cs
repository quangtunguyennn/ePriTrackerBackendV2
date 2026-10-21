//using ePriTrackerBackend.Models.Context;
//using ePriTrackerBackend.Models.DTOs;
//using ePriTrackerBackend.Models.Entities;
//using ePriTrackerBackend.Services;
//using Microsoft.EntityFrameworkCore;
//using Microsoft.Extensions.Logging;
//using System.Text.Json;

//namespace ePriTrackerBackend.Repositories
//{
//    public class EventRepository : IEventRepository
//    {
//        private readonly ePriTrackerContext _context;
//        private readonly ILogger<EventRepository> _logger;

//        private readonly ITikiBrowserService _tikiBrowser;

//        // Tái sử dụng HttpClient để tối ưu hiệu năng và tài nguyên mạng
//        private static readonly HttpClient _httpClient = new HttpClient();
//        private const string TikiBaseUrl = "https://tiki.vn";

//        static EventRepository()
//        {
//            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
//            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
//        }

//        public EventRepository(
//            ePriTrackerContext context,
//            ILogger<EventRepository> logger,
//            ITikiBrowserService tikiBrowser)
//        {
//            _context = context ?? throw new ArgumentNullException(nameof(context));
//            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
//            _tikiBrowser = tikiBrowser ?? throw new ArgumentNullException(nameof(tikiBrowser));
//        }

//        #region Private API Methods (2-Layer Crawl / Fallback)

//        // HÀM LÕI: Ưu tiên dùng HttpClient tốc độ cao, nếu bị Tiki chặn (403/WAF) sẽ tự chuyển sang Playwright
//        private async Task<JsonElement> FetchTikiApiWithFallbackAsync(string apiPath)
//        {
//            string fullUrl = $"{TikiBaseUrl}{apiPath}";
//            try
//            {
//                _logger.LogInformation("Đang thử gọi API Tiki qua HttpClient: {Url}", fullUrl);
//                var response = await _httpClient.GetAsync(fullUrl);

//                response.EnsureSuccessStatusCode();

//                string jsonString = await response.Content.ReadAsStringAsync();
//                using var document = JsonDocument.Parse(jsonString);

//                _logger.LogInformation("Lấy dữ liệu HttpClient thành công (Tốc độ cao)!");
//                return document.RootElement.Clone();
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning("HttpClient thất bại ({Msg}). Kích hoạt Playwright tàng hình cho URL: {Url}", ex.Message, fullUrl);
//                // Fallback sang Tầng 2 (Playwright)
//                return await _tikiBrowser.FetchTikiApiAsync(apiPath);
//            }
//        }

//        #endregion

//        #region Public Interface Implementations

//        // 1. Cào danh sách Sự kiện đang diễn ra trên Tiki để Admin xem trước
//        public async Task<List<Event>> GetCurrentTikiEvents()
//        {
//            string apiPath = "https://tka.tiki.vn/widget/api/v1/banners-group?group=banner_carousel_2_8";
//            var events = new List<Event>();

//            try
//            {
//                JsonElement root = await FetchTikiApiWithFallbackAsync(apiPath);

//                // Bắt lấy toàn bộ JSON thô mà Tiki trả về
//                string rawJson = root.ValueKind != JsonValueKind.Undefined ? root.GetRawText() : "Dữ liệu trả về rỗng (WAF chặn)";

//                if (root.TryGetProperty("data", out JsonElement dataArray) && dataArray.ValueKind == JsonValueKind.Array)
//                {
//                    foreach (var groupItem in dataArray.EnumerateArray())
//                    {
//                        if (groupItem.TryGetProperty("banners", out JsonElement bannersArray) && bannersArray.ValueKind == JsonValueKind.Array)
//                        {
//                            foreach (var item in bannersArray.EnumerateArray())
//                            {
//                                int tikiId = item.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
//                                string rawTitleUrl = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
//                                string imageUrl = item.TryGetProperty("image_url", out var imgProp) ? imgProp.GetString() ?? "" : "";
//                                string eventUrl = item.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";

//                                if (tikiId > 0 && !string.IsNullOrEmpty(eventUrl))
//                                {
//                                    events.Add(new Event
//                                    {
//                                        TikiEventId = tikiId,
//                                        Title = FormatEventTitleFromUrl(rawTitleUrl),
//                                        EventLink = eventUrl,
//                                        ImageUrl = imageUrl,
//                                        CreatedAt = DateTime.UtcNow
//                                    });
//                                }
//                            }
//                        }
//                    }
//                }

//                // BẪY BẮT LỖI: Nếu lấy được 0 sự kiện, ném thẳng file JSON của Tiki ra Swagger!
//                if (events.Count == 0)
//                {
//                    throw new Exception($"Tiki trả về: {rawJson}");
//                }

//                return events;
//            }
//            catch (Exception ex)
//            {
//                // Ném lỗi ra ngoài để Controller chụp lại và hiển thị lên Swagger
//                throw new Exception(ex.Message);
//            }
//        }
//        // HÀM HỖ TRỢ: Tự động bóc tách url "fonterra-chinh-hang" thành tên "Fonterra Chinh Hang"
//        private string FormatEventTitleFromUrl(string rawUrl)
//        {
//            if (string.IsNullOrEmpty(rawUrl)) return "Sự kiện Tiki Khuyến Mãi";

//            try
//            {
//                // Lấy phần đuôi cùng của URL (vd: fonterra-chinh-hang)
//                string slug = rawUrl.Split('/').LastOrDefault()?.Split('?').FirstOrDefault();
//                if (string.IsNullOrEmpty(slug)) return "Sự kiện Tiki Khuyến Mãi";

//                // Tách các dấu gạch ngang và viết hoa chữ cái đầu tiên của mỗi từ
//                var words = slug.Split('-')
//                    .Where(w => !string.IsNullOrEmpty(w))
//                    .Select(w => char.ToUpper(w[0]) + w.Substring(1));

//                return string.Join(" ", words);
//            }
//            catch
//            {
//                return "Sự kiện Tiki Khuyến Mãi";
//            }
//        }
//        // 2. LIVE CRAWL: Cào trực tiếp danh sách Sản phẩm từ trang Sự kiện (Không lưu DB)
//        //public async Task<List<LiveEventProductDTO>> GetLiveProductsFromEventAsync(string urlKey)
//        //{
//        //    string apiPath = $"/api/v2/products?limit=50&url_key={urlKey}";
//        //    var liveProducts = new List<LiveEventProductDTO>();

//        //    try
//        //    {
//        //        JsonElement root = await FetchTikiApiWithFallbackAsync(apiPath);

//        //        if (root.TryGetProperty("data", out JsonElement dataArray) && dataArray.ValueKind == JsonValueKind.Array)
//        //        {
//        //            foreach (var item in dataArray.EnumerateArray())
//        //            {
//        //                if (!item.TryGetProperty("id", out var idProp)) continue;

//        //                string productIdStr = idProp.GetInt64().ToString();
//        //                string urlPath = item.TryGetProperty("url_path", out var pathProp) ? pathProp.GetString() ?? "" : "";

//        //                liveProducts.Add(new LiveEventProductDTO
//        //                {
//        //                    ProductId = productIdStr,
//        //                    ProductName = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "Sản phẩm ẩn" : "Sản phẩm ẩn",
//        //                    Price = item.TryGetProperty("price", out var priceProp) && priceProp.ValueKind == JsonValueKind.Number ? priceProp.GetDecimal() : 0,
//        //                    ImageUrl = item.TryGetProperty("thumbnail_url", out var imgProp) ? imgProp.GetString() ?? "" : "",
//        //                    ProductLink = string.IsNullOrEmpty(urlPath) ? "" : $"{TikiBaseUrl}/{urlPath.TrimStart('/')}"
//        //                });
//        //            }
//        //        }

//        //        _logger.LogInformation("Lấy thành công {Count} sản phẩm live từ sự kiện: {UrlKey}", liveProducts.Count, urlKey);
//        //        return liveProducts;
//        //    }
//        //    catch (Exception ex)
//        //    {
//        //        _logger.LogError(ex, "Lỗi khi cào sản phẩm live từ trang sự kiện: {UrlKey}", urlKey);
//        //        return liveProducts;
//        //    }
//        //}

//        #endregion
//    }
//}