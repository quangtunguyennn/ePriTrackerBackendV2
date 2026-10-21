using ePriTracker.Domain.Models.Entities;

namespace ePriTracker.Domain.Interfaces;

/// <summary>
/// Defines the contract for platform-specific e-commerce crawler strategies.
/// Each supported retailer site (Shopee, Tiki, HACOM, etc.) must implement this interface.
/// </summary>
public interface IEcommerceCrawlerStrategy
{
    /// <summary>
    /// The display name of the target platform (e.g., "Shopee", "Tiki", "HACOM").
    /// </summary>
    string PlatformName { get; }

    /// <summary>
    /// Determines whether this strategy can handle scraping for the specified product or event URL.
    /// </summary>
    /// <param name="url">The target URL to validate.</param>
    /// <returns>True if the strategy handles the domain/URL pattern; otherwise, false.</returns>
    bool CanHandle(string url);

    /// <summary>
    /// Scrapes live product data from the provided URL and maps it to a domain Product entity.
    /// </summary>
    /// <param name="url">The full URL of the product page.</param>
    /// <returns>A populated <see cref="Product"/> entity containing extracted details.</returns>
    Task<Product> FetchProductDataAsync(string url);

    /// <summary>
    /// Crawls alternative or cheaper related products based on a search keyword and current price threshold.
    /// </summary>
    /// <param name="productId">The unique identifier of the target primary product.</param>
    /// <param name="searchKeyword">Sanitized keyword derived from the original product name.</param>
    /// <param name="currentPrice">The price threshold to discover better or cheaper options.</param>
    /// <returns>A list of discovered <see cref="ProductRelation"/> entities.</returns>
    Task<List<ProductRelation>> CrawlSuggestionsAsync(long productId, string searchKeyword, decimal currentPrice);
}