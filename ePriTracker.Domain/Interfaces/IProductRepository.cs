using ePriTracker.Domain.Models.Entities;

namespace ePriTracker.Domain.Interfaces;

public interface IProductRepository
{
    Task<Product?> GetByLinkAsync(string url);
    Task<Product?> GetByIdAsync(long id);
    Task AddProductAsync(Product product);
    Task LinkUserToProductAsync(long userId, long productId, decimal initialPrice);
    Task<User?> GetUserByEmailAsync(string email);
    Task<List<ProductRelation>> GetRelationsBySourceIdAsync(long sourceProductId);
    Task AddProductRelationsAsync(IEnumerable<ProductRelation> relations);
    Task<List<Product>> GetProductsByUserEmailAsync(string userEmail);
    Task<bool> DeleteProductAsync(long id, string userEmail);
}