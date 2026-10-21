using ePriTracker.Application.DTOs;
using ePriTracker.Application.Interfaces;
using ePriTracker.Domain.Interfaces;
using ePriTracker.Domain.Models.Entities;
using Microsoft.Extensions.Logging;

namespace ePriTracker.Application.Services;

public class ProductService : IProductService
{
    private readonly IProductRepository _productRepo;
    private readonly ICacheService _cacheService;
    private readonly ICrawlerFactory _crawlerFactory;
    private readonly ILogger<ProductService> _logger;

    public ProductService(
        IProductRepository productRepo,
        ICacheService cacheService,
        ICrawlerFactory crawlerFactory,
        ILogger<ProductService> logger)
    {
        _productRepo = productRepo ?? throw new ArgumentNullException(nameof(productRepo));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _crawlerFactory = crawlerFactory ?? throw new ArgumentNullException(nameof(crawlerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ProductDto> AddProductAsync(string productUrl, string userEmail)
    {
        if (string.IsNullOrWhiteSpace(productUrl)) throw new ArgumentException("Product URL cannot be empty.", nameof(productUrl));
        if (string.IsNullOrWhiteSpace(userEmail)) throw new ArgumentException("User email cannot be empty.", nameof(userEmail));

        // 1. Lấy thông tin User
        var user = await _productRepo.GetUserByEmailAsync(userEmail)
            ?? throw new KeyNotFoundException($"User with email '{userEmail}' not found.");

        string normalizedUrl = NormalizeUrl(productUrl);
        string urlCacheKey = $"product:url:{normalizedUrl}";

        // 2. CHECK REDIS CACHE THEO URL TRƯỚC (Fast Path - Phản hồi siêu nhanh)
        var cachedProduct = await _cacheService.GetAsync<ProductDto>(urlCacheKey);
        if (cachedProduct != null)
        {
            _logger.LogInformation("Cache HIT for URL: {Url}", normalizedUrl);

            // Vẫn tạo liên kết User-Product dưới DB nếu user chưa theo dõi
            await _productRepo.LinkUserToProductAsync(user.UserId, cachedProduct.ProductId, cachedProduct.LatestPrice);
            await _cacheService.RemoveAsync($"user_products:{userEmail}");

            return cachedProduct;
        }

        // 3. CACHE MISS -> MỚI CHUI XUỐNG DB
        var dbProduct = await _productRepo.GetByLinkAsync(normalizedUrl);

        // 4. DB MISS -> MỚI KÍCH HOẠT SCRAPER CÀO TĨNH
        if (dbProduct == null)
        {
            _logger.LogInformation("Product not found in DB. Executing live scraper for: {Url}", normalizedUrl);
            var crawler = _crawlerFactory.GetStrategy(normalizedUrl);
            dbProduct = await crawler.FetchProductDataAsync(normalizedUrl)
                ?? throw new InvalidOperationException("Failed to extract product data from the provided URL.");

            await _productRepo.AddProductAsync(dbProduct);
        }

        // 5. Link User với Product
        decimal currentPrice = dbProduct.PriceHistories.OrderByDescending(p => p.CrawledAt).FirstOrDefault()?.Price ?? 0;
        await _productRepo.LinkUserToProductAsync(user.UserId, dbProduct.ProductId, currentPrice);

        var productDto = MapToProductDto(dbProduct);

        // 6. ĐỦ MẶT CACHE: Đẩy dữ liệu vào Redis theo cả URL lẫn Product ID
        await _cacheService.SetAsync(urlCacheKey, productDto, TimeSpan.FromHours(6));
        await _cacheService.SetAsync($"product:id:{dbProduct.ProductId}", productDto, TimeSpan.FromHours(6));

        // Invalidate Cache danh sách cá nhân của User
        await _cacheService.RemoveAsync($"user_products:{userEmail}");

        return productDto;
    }

    public async Task<List<ProductDto>> GetAllProductsAsync(string userEmail)
    {
        string userProductsCacheKey = $"user_products:{userEmail}";

        var cachedList = await _cacheService.GetAsync<List<ProductDto>>(userProductsCacheKey);
        if (cachedList != null) return cachedList;

        var products = await _productRepo.GetProductsByUserEmailAsync(userEmail);
        var dtoList = products.Select(MapToProductDto).ToList();

        await _cacheService.SetAsync(userProductsCacheKey, dtoList, TimeSpan.FromMinutes(30));
        return dtoList;
    }

    public async Task<ProductDto?> GetProductByIdAsync(long id, string userEmail)
    {
        string cacheKey = $"product:id:{id}";
        var cachedProduct = await _cacheService.GetAsync<ProductDto>(cacheKey);
        if (cachedProduct != null) return cachedProduct;

        var product = await _productRepo.GetByIdAsync(id);
        if (product == null) return null;

        var dto = MapToProductDto(product);
        await _cacheService.SetAsync(cacheKey, dto, TimeSpan.FromHours(2));
        return dto;
    }

    public async Task<List<ProductRelationDto>> GetSuggestionsAsync(long productId, string userEmail)
    {
        string cacheKey = $"suggestions:{productId}";

        // 1. Kiểm tra Cache
        var cachedData = await _cacheService.GetAsync<List<ProductRelationDto>>(cacheKey);
        if (cachedData != null && cachedData.Any()) return cachedData;

        // 2. Lấy từ Database
        var dbSuggestions = await _productRepo.GetRelationsBySourceIdAsync(productId);
        if (dbSuggestions.Any())
        {
            var result = dbSuggestions.Select(MapToRelationDto).ToList();
            await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromHours(2));
            return result;
        }

        // 3. Fallback: KÍCH HOẠT CÀO ĐA NỀN TẢNG (CROSS-PLATFORM CRAWL)
        return await ExecuteCrossPlatformCrawlAsync(productId, cacheKey);
    }

    public async Task<bool> DeleteProductAsync(long id, string userEmail)
    {
        bool result = await _productRepo.DeleteProductAsync(id, userEmail);
        if (result)
        {
            // Invalidate toàn bộ Cache liên quan
            await _cacheService.RemoveAsync($"product:id:{id}");
            await _cacheService.RemoveAsync($"suggestions:{id}");
            await _cacheService.RemoveAsync($"user_products:{userEmail}");
        }
        return result;
    }

    // =====================================================================
    //  CROSS-PLATFORM CRAWL & HELPERS
    // =====================================================================

    private async Task<List<ProductRelationDto>> ExecuteCrossPlatformCrawlAsync(long productId, string cacheKey)
    {
        var product = await _productRepo.GetByIdAsync(productId);
        if (product == null) return new List<ProductRelationDto>();

        string searchKeyword = ExtractSearchKeyword(product.ProductName);
        decimal latestPrice = product.PriceHistories.OrderByDescending(p => p.CrawledAt).FirstOrDefault()?.Price ?? 0;

        // Lấy toàn bộ danh sách Strategy đã đăng ký (HACOM, AnPhat, ...)
        var allStrategies = _crawlerFactory.GetAllStrategies();

        // Chạy cào song song trên tất cả các sàn (Parallel Scrape)
        var crawlTasks = allStrategies.Select(strategy =>
            strategy.CrawlSuggestionsAsync(productId, searchKeyword, latestPrice)
        );

        var resultsArray = await Task.WhenAll(crawlTasks);

        // Gộp kết quả cào từ tất cả các sàn
        var allNewRelations = resultsArray.SelectMany(r => r).ToList();

        if (allNewRelations.Any())
        {
            // Lưu toàn bộ gợi ý vào DB
            await _productRepo.AddProductRelationsAsync(allNewRelations);

            var resultDtos = allNewRelations
                .Select(MapToRelationDto)
                .OrderByDescending(r => r.RelatedProductPrice < latestPrice) // Ưu tiên giá rẻ hơn
                .ToList();

            // Set Cache
            await _cacheService.SetAsync(cacheKey, resultDtos, TimeSpan.FromHours(2));
            return resultDtos;
        }

        return new List<ProductRelationDto>();
    }

    private static string NormalizeUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".ToLowerInvariant();
        }
        catch { return url.Split('?')[0].ToLowerInvariant(); }
    }

    private static string ExtractSearchKeyword(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
        var splitChars = new[] { '-', '|', '(', '[', ',' };
        string coreName = rawName.Split(splitChars, StringSplitOptions.RemoveEmptyEntries)[0];
        var words = coreName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Take(5));
    }

    private static ProductDto MapToProductDto(Product product)
    {
        var latestPriceRecord = product.PriceHistories.OrderByDescending(p => p.CrawledAt).FirstOrDefault();
        var initialPriceRecord = product.PriceHistories.OrderBy(p => p.CrawledAt).FirstOrDefault();
        return new ProductDto
        {
            ProductId = product.ProductId,
            ProductName = product.ProductName,
            ProductLink = product.ProductUrl,
            ImageUrl = product.ImageUrl ?? string.Empty,
            InitialPrice = initialPriceRecord?.Price ?? 0,
            LatestPrice = latestPriceRecord?.Price ?? 0,
            LastUpdatedAt = latestPriceRecord?.CrawledAt ?? product.CreatedAt
        };
    }

    private static ProductRelationDto MapToRelationDto(ProductRelation relation)
    {
        var targetPrice = relation.TargetProduct?.PriceHistories
            .OrderByDescending(p => p.CrawledAt)
            .FirstOrDefault()?.Price ?? 0;

        return new ProductRelationDto
        {
            RelationId = relation.RelationId,
            SourceProductId = relation.SourceProductId,
            TargetProductId = relation.TargetProductId,
            RelatedProductName = relation.TargetProduct?.ProductName ?? string.Empty,
            RelatedProductUrl = relation.TargetProduct?.ProductUrl ?? string.Empty,
            RelatedProductImageUrl = relation.TargetProduct?.ImageUrl,
            RelatedProductPrice = targetPrice,
            RelationType = relation.RelationType,
            MatchScore = relation.MatchScore
        };
    }
}