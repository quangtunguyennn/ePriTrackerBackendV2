using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Repositories
{
    public interface IProductRepository
    {
        public Task AddProduct(string productLink, string userEmail);
        public Task<List<Product>> GetAll(string userEmail);
        public Task<Product> GetById(Guid id);
        public Task<List<SuggestionProductDTO>> GetAllBetterProducts(Guid productId);
        public Task<bool> DeleteProduct(Guid id, string userEmail);
        Task<List<SuggestionProductDTO>> RefreshSuggestions(Guid productId);
        Task<List<LiveEventProductDTO>> GetLiveProductsFromEventAsync(string urlKey);
    };

}
