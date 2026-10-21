using ePriTracker.Application.DTOs;

namespace ePriTracker.Application.Interfaces;

public interface IProductService
{
    Task<ProductDto> AddProductAsync(string productUrl, string userEmail);
    Task<List<ProductDto>> GetAllProductsAsync(string userEmail);
    Task<ProductDto?> GetProductByIdAsync(long id, string userEmail);
    Task<List<ProductRelationDto>> GetSuggestionsAsync(long productId, string userEmail);
    Task<bool> DeleteProductAsync(long id, string userEmail);
}