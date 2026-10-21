using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ePriTrackerBackend.Services
{
    public class EventCacheService : IEventCacheService
    {
        private readonly ePriTrackerContext _context;
        private readonly IProductRepository _productRepository;
        private readonly IMemoryCache _cache;

        public EventCacheService(ePriTrackerContext context, IProductRepository productRepository, IMemoryCache cache)
        {
            _context = context;
            _productRepository = productRepository;
            _cache = cache;
        }

        public async Task RefreshLiveEventProductsCacheAsync()
        {
            // 1. Chỉ lấy các sự kiện ĐANG HIỂN THỊ (IsPublished = true)
            var activeEvents = await _context.Event
                .Where(e => e.IsPublished == true)
                .ToListAsync();

            foreach (var evt in activeEvents)
            {
                try
                {
                    // 2. Gọi Crawler để lấy dữ liệu mới nhất từ Tiki
                    var products = await _productRepository.GetLiveProductsFromEventAsync(evt.EventLink);

                    if (products != null && products.Count > 0)
                    {
                        // 3. Cấu hình thời gian sống của Cache (ví dụ: 30 phút)
                        var cacheOptions = new MemoryCacheEntryOptions()
                            .SetAbsoluteExpiration(TimeSpan.FromMinutes(30));

                        // 4. Lưu vào RAM với Key là EventId
                        string cacheKey = $"EventProducts_{evt.EventId}";
                        _cache.Set(cacheKey, products, cacheOptions);
                    }
                }
                catch (Exception ex)
                {
                    // Ghi log lỗi tại đây để theo dõi nếu 1 sự kiện nào đó bị lỗi cào
                    Console.WriteLine($"Lỗi khi cào sự kiện {evt.EventId}: {ex.Message}");
                }
            }
        }
    }
}
