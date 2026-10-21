using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Services
{
    public class PriceCrawlerService : IPriceCrawlerService
    {
        private readonly ePriTrackerContext _context;
        private readonly IEcommerceServiceFactory _serviceFactory; // Sử dụng Factory đa nền tảng thay vì Tiki độc quyền
        private readonly ILogger<PriceCrawlerService> _logger;
        private readonly ScraperMetricsService _metrics;

        public PriceCrawlerService(
            ePriTrackerContext context,
            IEcommerceServiceFactory serviceFactory,
            ILogger<PriceCrawlerService> logger,
            ScraperMetricsService metrics)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        public async Task UpdateAllTrackedProductPricesAsync()
        {
            _logger.LogInformation("🚀 [Hangfire/Multi-Platform] Bắt đầu tiến trình crawl cập nhật giá định kỳ...");

            // Lấy danh sách sản phẩm cần update (có người theo dõi)
            var productsToUpdate = await _context.Product
                .Where(p => _context.Item.Select(i => i.ProductId).Contains(p.ProductId))
                .ToListAsync();

            if (!productsToUpdate.Any())
            {
                _logger.LogInformation("ℹ️ [Hangfire] Không có sản phẩm nào đang được theo dõi.");
                return;
            }

            int successCount = 0;
            int batchSize = 5; // Xử lý 5 sản phẩm cùng lúc song song
            var productChunks = productsToUpdate.Chunk(batchSize);

            foreach (var chunk in productChunks)
            {
                // Gọi song song cho từng Batch thông qua Factory tương ứng với từng sàn
                var fetchTasks = chunk.Select(product => FetchAndProcessPriceAsync(product)).ToList();
                var results = await Task.WhenAll(fetchTasks);

                var validUpdates = results.Where(r => r.IsSuccess).ToList();

                foreach (var update in validUpdates)
                {
                    var product = update.Product;
                    product.LatestPrice = update.NewPrice;
                    product.LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));

                    _context.PriceHistory.Add(new PriceHistory
                    {
                        ProductId = product.ProductId,
                        Price = update.NewPrice,
                        CheckedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
                    });

                    successCount++;
                }

                // Lưu DB sau mỗi Batch để tránh tràn RAM
                if (validUpdates.Any())
                {
                    await _context.SaveChangesAsync();
                }

                // Delay ngẫu nhiên giữa các Lô để bảo vệ IP và tránh WAF chặn
                await Task.Delay(Random.Shared.Next(1500, 3000));
            }

            _logger.LogInformation("✅ [Hangfire/Multi-Platform] Đã cập nhật thành công giá cho {Success}/{Total} sản phẩm.", successCount, productsToUpdate.Count);
        }

        /// <summary>
        /// Wrapper xử lý gọi lấy giá linh hoạt dựa vào Factory cho từng sản phẩm
        /// </summary>
        private async Task<(bool IsSuccess, Product Product, decimal NewPrice)> FetchAndProcessPriceAsync(Product product)
        {
            try
            {
                // Tự động định tuyến sang Service của sàn tương ứng (Tiki, Shopee,...)
                var ecommerceService = _serviceFactory.GetService(product.ProductLink);

                decimal? newPrice = await ecommerceService.FetchCurrentPriceAsync(product.ProductLink);

                if (newPrice.HasValue && newPrice > 0 && newPrice != product.LatestPrice)
                {
                    _metrics.RecordHttpClientSuccess();
                    return (true, product, newPrice.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Lỗi khi crawl cập nhật giá cho SP ID: {ProductId} ({Link})", product.ProductId, product.ProductLink);
                _metrics.RecordFailure();
            }

            return (false, product, 0);
        }
    }
}