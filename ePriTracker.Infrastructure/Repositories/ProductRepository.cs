using ePriTracker.Domain.Interfaces;
using ePriTracker.Domain.Models.Entities;
using ePriTracker.Infrastructure.Context;
using Microsoft.EntityFrameworkCore;

namespace ePriTracker.Infrastructure.Repositories;

public class ProductRepository : IProductRepository
{
    private readonly ePriTrackerContext _context;

    public ProductRepository(ePriTrackerContext context)
    {
        _context = context;
    }

    public async Task AddProductAsync(Product product)
    {
        _context.Products.Add(product);
        await _context.SaveChangesAsync();
    }

    public async Task<Product?> GetByLinkAsync(string normalizedLink)
    {
        return await _context.Products
            .Include(p => p.PriceHistories)
            .FirstOrDefaultAsync(p => p.ProductUrl == normalizedLink);
    }

    public async Task<Product?> GetByIdAsync(long id)
    {
        return await _context.Products
            .Include(p => p.PriceHistories)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProductId == id);
    }

    // Đã sửa tên hàm từ GetAllAsync thành GetProductsByUserEmailAsync để khớp với Interface[cite: 8]
    public async Task<List<Product>> GetProductsByUserEmailAsync(string userEmail)
    {
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == userEmail);
        if (user == null) return new List<Product>();

        var trackedProductIds = await _context.UserTrackedItems
            .AsNoTracking()
            .Where(i => i.UserId == user.UserId)
            .Select(i => i.ProductId)
            .ToListAsync();

        return await _context.Products
            .Include(p => p.PriceHistories)
            .AsNoTracking()
            .Where(p => trackedProductIds.Contains(p.ProductId))
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
    }

    // Đã sửa kiểu tham số từ List<> thành IEnumerable<> để khớp với Interface[cite: 8]
    public async Task AddProductRelationsAsync(IEnumerable<ProductRelation> relations)
    {
        if (relations.Any())
        {
            await _context.ProductRelations.AddRangeAsync(relations);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<bool> DeleteProductAsync(long id, string userEmail)
    {
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == userEmail);
        if (user == null) return false;

        var productItem = await _context.UserTrackedItems
            .FirstOrDefaultAsync(x => x.ProductId == id && x.UserId == user.UserId);

        if (productItem == null) return false;

        _context.UserTrackedItems.Remove(productItem);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<List<ProductRelation>> GetRelationsBySourceIdAsync(long sourceProductId)
    {
        return await _context.ProductRelations
            .Include(pr => pr.TargetProduct)
                .ThenInclude(tp => tp.PriceHistories)
            .Where(pr => pr.SourceProductId == sourceProductId && pr.RelationType == "SUGGESTION")
            .OrderByDescending(pr => pr.MatchScore)
            .ToListAsync();
    }

    public async Task LinkUserToProductAsync(long userId, long productId, decimal initialPrice)
    {
        var existingItem = await _context.UserTrackedItems
            .FirstOrDefaultAsync(u => u.UserId == userId && u.ProductId == productId);

        if (existingItem == null)
        {
            var trackedItem = new UserTrackedItem
            {
                UserId = userId,
                ProductId = productId,
                InitialPrice = initialPrice,
                IsNotificationEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await _context.UserTrackedItems.AddAsync(trackedItem);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<User?> GetUserByEmailAsync(string email)
    {
        return await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
    }
}