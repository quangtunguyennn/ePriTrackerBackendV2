using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ePriTrackerBackend.Repositories
{
    public class ProductRepository : IProductRepository
    {
        private readonly ePriTrackerContext _context;
        private readonly ILogger<ProductRepository> _logger;
        private readonly IEcommerceServiceFactory _serviceFactory;

        // Regex nhận diện ID sản phẩm đặc thù cho từng sàn
        private static readonly Regex TikiProductIdRegex = new Regex(@"-p(\d+)\.html", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ShopeeProductIdRegex = new Regex(@"i\.(\d+)\.(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SpecialCharRegex = new Regex(@"[^\p{L}\p{N}]", RegexOptions.Compiled);
        private static readonly Regex WhiteSpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        private static readonly HashSet<string> AccessoriesBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "op lung", "bao da", "kinh cuong luc", "mieng dan",
            "cap sac", "cu sac", "tai nghe", "day deo", "vo boc", "balo", "tui chong soc"
        };

        private const decimal MinPriceRatioThreshold = 0.4m;

        public ProductRepository(
            ePriTrackerContext context,
            ILogger<ProductRepository> logger,
            IEcommerceServiceFactory serviceFactory)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        }

        #region Public Interface Implementations

        public async Task AddProduct(string productLink, string userEmail)
        {
            if (string.IsNullOrWhiteSpace(productLink))
                throw new ArgumentException("Link sản phẩm không được trống.", nameof(productLink));
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new ArgumentException("Email người dùng không được trống.", nameof(userEmail));

            var user = await _context.User.AsNoTracking().FirstOrDefaultAsync(x => x.Email == userEmail);

            if (user == null)
            {
                _logger.LogWarning("Không tìm thấy người dùng với email: {Email}", userEmail);
                throw new Exception("Không tìm thấy thông tin người dùng.");
            }

            // Lấy Service tương ứng với nền tảng thông qua Factory
            var ecommerceService = _serviceFactory.GetService(productLink);
            string platformProductId = string.Empty;
            string normalizedLink = productLink;

            if (ecommerceService.PlatformName.Equals("Tiki", StringComparison.OrdinalIgnoreCase))
            {
                var match = TikiProductIdRegex.Match(productLink);
                if (!match.Success)
                {
                    _logger.LogWarning("Link Tiki không hợp lệ: {ProductLink}", productLink);
                    throw new Exception("Link Tiki không hợp lệ. Không tìm thấy Product ID.");
                }
                platformProductId = match.Groups[1].Value;
                normalizedLink = $"https://tiki.vn/product-p{platformProductId}.html";
            }
            else if (ecommerceService.PlatformName.Equals("Shopee", StringComparison.OrdinalIgnoreCase))
            {
                var match = ShopeeProductIdRegex.Match(productLink);
                if (!match.Success)
                {
                    _logger.LogWarning("Link Shopee không hợp lệ: {ProductLink}", productLink);
                    throw new Exception("Link Shopee không hợp lệ.");
                }
                platformProductId = $"{match.Groups[1].Value}.{match.Groups[2].Value}";

                // 🔥 Chuẩn hóa link Shopee: loại bỏ toàn bộ tham số query thừa (ví dụ: ?extraParams=...)
                try
                {
                    var uri = new Uri(productLink);
                    normalizedLink = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
                }
                catch
                {
                    normalizedLink = productLink.Split('?')[0];
                }
            }
            else
            {
                platformProductId = productLink;
            }

            var existingProduct = await _context.Product.FirstOrDefaultAsync(x => x.ProductLink == normalizedLink);
            Guid currentProductId;

            if (existingProduct == null)
            {
                _logger.LogInformation("Sản phẩm chưa có trong DB, tiến hành lấy dữ liệu từ {Platform}: {ProductId}", ecommerceService.PlatformName, platformProductId);

                var newProduct = await ecommerceService.FetchProductDataAsync(platformProductId, normalizedLink);

                _context.Product.Add(newProduct);
                await _context.SaveChangesAsync();
                currentProductId = newProduct.ProductId;
            }
            else
            {
                currentProductId = existingProduct.ProductId;

                // 🔥 Tự động cập nhật lại nếu dữ liệu cũ trong DB đang bị lỗi giá 0 đ
                if (existingProduct.LatestPrice <= 0)
                {
                    _logger.LogInformation("Sản phẩm đã tồn tại nhưng giá đang bằng 0, tiến hành cào lại: {ProductId}", platformProductId);
                    var refreshedProduct = await ecommerceService.FetchProductDataAsync(platformProductId, normalizedLink);

                    existingProduct.ProductName = string.IsNullOrEmpty(refreshedProduct.ProductName) ? existingProduct.ProductName : refreshedProduct.ProductName;
                    existingProduct.ImageURL = string.IsNullOrEmpty(refreshedProduct.ImageURL) ? existingProduct.ImageURL : refreshedProduct.ImageURL;
                    existingProduct.InitialPrice = refreshedProduct.InitialPrice > 0 ? refreshedProduct.InitialPrice : existingProduct.InitialPrice;
                    existingProduct.LatestPrice = refreshedProduct.LatestPrice > 0 ? refreshedProduct.LatestPrice : existingProduct.LatestPrice;
                    existingProduct.LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));

                    await _context.SaveChangesAsync();
                }
            }

            bool isTracking = await _context.Item.AnyAsync(x => x.UserId == user.UserId && x.ProductId == currentProductId);
            if (!isTracking)
            {
                _context.Item.Add(new Item { UserId = user.UserId, ProductId = currentProductId });
                await _context.SaveChangesAsync();

                // Tự động liên kết Sản phẩm với Sự kiện nếu có mã chiến dịch
                if (productLink.Contains("itm_campaign="))
                {
                    var campaignMatch = Regex.Match(productLink, @"itm_campaign=([^&]+)");
                    if (campaignMatch.Success)
                    {
                        string campaignCode = campaignMatch.Groups[1].Value.ToLower();

                        var matchedEvent = await _context.Event
                            .FirstOrDefaultAsync(e => e.EventLink.ToLower().Contains(campaignCode));

                        if (matchedEvent != null)
                        {
                            bool isMapped = await _context.Set<EventProduct>()
                                .AnyAsync(ep => ep.EventId == matchedEvent.EventId && ep.ProductId == currentProductId);

                            if (!isMapped)
                            {
                                _context.Set<EventProduct>().Add(new EventProduct
                                {
                                    EventId = matchedEvent.EventId,
                                    ProductId = currentProductId
                                });
                                await _context.SaveChangesAsync();
                            }
                        }
                    }
                }
                _logger.LogInformation("Đã thêm sản phẩm {ProductId} vào danh sách theo dõi của User {UserId}", currentProductId, user.UserId);
            }
        }

        public async Task<List<SuggestionProductDTO>> GetAllBetterProducts(Guid productId)
        {
            var suggestions = await _context.Set<SuggestionProduct>()
                .AsNoTracking()
                .Where(s => s.ProductId == productId)
                .ToListAsync();

            if (!suggestions.Any())
            {
                var product = await _context.Product.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == productId);
                if (product != null)
                {
                    _logger.LogInformation("Lazy Loading: Bắt đầu lấy sản phẩm gợi ý cho: {ProductName}", product.ProductName);

                    var ecommerceService = _serviceFactory.GetService(product.ProductLink);
                    string searchKeyword = ExtractSearchKeyword(product.ProductName);

                    var rawSuggestions = await ecommerceService.CrawlSuggestionsAsync(product.ProductId, searchKeyword, product.InitialPrice);

                    var newSuggestions = rawSuggestions
                        .Where(s => IsValidSimilarProduct(product.ProductName, s.ProductName) &&
                                    s.Price > 0 && s.Price <= product.InitialPrice &&
                                    s.Price > (product.InitialPrice * MinPriceRatioThreshold))
                        .ToList();

                    if (newSuggestions.Any())
                    {
                        _context.Set<SuggestionProduct>().AddRange(newSuggestions);
                        await _context.SaveChangesAsync();
                        suggestions = newSuggestions;
                    }
                }
            }

            return suggestions
                .OrderBy(s => s.Price)
                .Select(s => new SuggestionProductDTO
                {
                    ProductName = s.ProductName,
                    Price = s.Price,
                    ImageURL = s.ImageURL,
                    ProductLink = s.ProductLink,
                    LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
                })
                .ToList();
        }

        public async Task<List<SuggestionProductDTO>> RefreshSuggestions(Guid productId)
        {
            var product = await _context.Product.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == productId);
            if (product == null) throw new Exception("Không tìm thấy thông tin sản phẩm gốc.");

            _logger.LogInformation("Người dùng yêu cầu làm mới gợi ý, ép cào dữ liệu mới cho: {ProductName}", product.ProductName);

            var ecommerceService = _serviceFactory.GetService(product.ProductLink);
            string searchKeyword = ExtractSearchKeyword(product.ProductName);

            var rawSuggestions = await ecommerceService.CrawlSuggestionsAsync(product.ProductId, searchKeyword, product.InitialPrice);
            var freshSuggestions = rawSuggestions
                .Where(s => IsValidSimilarProduct(product.ProductName, s.ProductName) &&
                            s.Price > 0 && s.Price <= product.InitialPrice &&
                            s.Price > (product.InitialPrice * MinPriceRatioThreshold))
                .ToList();

            var oldSuggestions = await _context.Set<SuggestionProduct>().Where(s => s.ProductId == productId).ToListAsync();
            if (oldSuggestions.Any())
            {
                _context.Set<SuggestionProduct>().RemoveRange(oldSuggestions);
            }

            if (freshSuggestions.Any())
            {
                _context.Set<SuggestionProduct>().AddRange(freshSuggestions);
            }

            await _context.SaveChangesAsync();

            return freshSuggestions
                .OrderBy(s => s.Price)
                .Select(s => new SuggestionProductDTO
                {
                    ProductName = s.ProductName,
                    Price = s.Price,
                    ImageURL = s.ImageURL,
                    ProductLink = s.ProductLink,
                    LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
                })
                .ToList();
        }

        public async Task<List<Product>> GetAll(string userEmail)
        {
            var user = await _context.User.AsNoTracking().FirstOrDefaultAsync(x => x.Email == userEmail);
            if (user == null) return new List<Product>();

            var trackedProductIds = await _context.Item
                .AsNoTracking()
                .Where(i => i.UserId == user.UserId)
                .Select(i => i.ProductId)
                .ToListAsync();

            if (!trackedProductIds.Any()) return new List<Product>();

            return await _context.Product
                .AsNoTracking()
                .Where(p => trackedProductIds.Contains(p.ProductId))
                .OrderByDescending(p => p.AddedAt)
                .ToListAsync();
        }

        public async Task<Product?> GetById(Guid id)
        {
            return await _context.Product.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == id);
        }

        public async Task<bool> DeleteProduct(Guid id, string userEmail)
        {
            var user = await _context.User.AsNoTracking().FirstOrDefaultAsync(x => x.Email == userEmail);
            if (user == null) throw new Exception("Không tìm thấy người dùng");

            var productItem = await _context.Item.FirstOrDefaultAsync(x => x.ProductId == id && x.UserId == user.UserId);
            if (productItem == null) throw new Exception("Sản phẩm không tồn tại trong danh sách theo dõi.");

            _context.Item.Remove(productItem);
            await _context.SaveChangesAsync();
            return true;
        }

        #endregion

        #region Private Helper Methods (Text Processing & Algorithms)

        private string ExtractSearchKeyword(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return string.Empty;

            var splitChars = new char[] { '-', '|', '(', '[', ',' };
            string coreName = rawName.Split(splitChars, StringSplitOptions.RemoveEmptyEntries)[0];
            string keyword = coreName.ToLower();

            string[] marketingWords = {
                "chính hãng", "chinh hang", "nhập khẩu", "nhap khau",
                "bản quốc tế", "ban quoc te", "nguyên seal", "nguyen seal",
                "mới 100%", "moi 100%", "freeship", "tặng kèm", "tang kem",
                "vn/a", "ll/a", "fullbox", "giá rẻ", "gia re"
            };

            foreach (var word in marketingWords)
            {
                keyword = keyword.Replace(word, "");
            }

            keyword = SpecialCharRegex.Replace(keyword, " ");
            keyword = WhiteSpaceRegex.Replace(keyword, " ").Trim();

            var words = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(6));
        }

        private bool IsValidSimilarProduct(string originalName, string searchName)
        {
            if (string.IsNullOrEmpty(originalName) || string.IsNullOrEmpty(searchName)) return false;

            var lowerOriginalNoMark = RemoveVietnameseDiacritics(originalName.ToLower());
            var lowerSearchNoMark = RemoveVietnameseDiacritics(searchName.ToLower());

            bool originalIsAccessory = AccessoriesBlacklist.Any(x => lowerOriginalNoMark.Contains(x));
            bool searchIsAccessory = AccessoriesBlacklist.Any(x => lowerSearchNoMark.Contains(x));

            if (!originalIsAccessory && searchIsAccessory) return false;

            var originalWords = lowerOriginalNoMark.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (originalWords.Length >= 1)
            {
                string entityWord1 = originalWords[0];
                string entityWord2 = originalWords.Length > 1 ? originalWords[1] : "";

                bool containsWord1 = lowerSearchNoMark.Contains(entityWord1);
                bool containsWord2 = !string.IsNullOrEmpty(entityWord2) && lowerSearchNoMark.Contains(entityWord2);

                if (!containsWord1 && !containsWord2)
                {
                    return false;
                }
            }
            return true;
        }

        private string RemoveVietnameseDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

            foreach (char c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder
                .ToString()
                .Normalize(NormalizationForm.FormC)
                .Replace('đ', 'd')
                .Replace('Đ', 'D');
        }

        #endregion

        #region Live Event Scraping Integration

        public async Task<List<LiveEventProductDTO>> GetLiveProductsFromEventAsync(string eventLink)
        {
            try
            {
                var ecommerceService = _serviceFactory.GetService(eventLink);
                return await ecommerceService.GetLiveProductsFromEventAsync(eventLink);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi cào sự kiện từ ProductRepository: {Key}", eventLink);
                return new List<LiveEventProductDTO>();
            }
        }

        #endregion
    }
}