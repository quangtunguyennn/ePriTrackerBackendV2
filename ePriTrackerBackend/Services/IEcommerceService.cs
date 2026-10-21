using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;

public interface IEcommerceService
{
    string PlatformName { get; }
    bool CanHandle(string url);
    Task<Product> FetchProductDataAsync(string productIdOrPath, string normalizedLink);
    Task<decimal?> FetchCurrentPriceAsync(string productLink); // Thêm dòng này để PriceCrawlerService gọi chung
    Task<List<SuggestionProduct>> CrawlSuggestionsAsync(Guid productId, string searchKeyword, decimal initialPrice);
    Task<List<LiveEventProductDTO>> GetLiveProductsFromEventAsync(string eventLink);
}