using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ePriTrackerBackend.Services
{
    public class SuggestionCrawlService : ISuggestionsCrawlerService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IEcommerceServiceFactory _serviceFactory; // Thay thế Tiki độc quyền bằng Factory đa sàn
        private readonly ILogger<SuggestionCrawlService> _logger;

        private const decimal MinPriceRatioThreshold = 0.4m;
        private const int BatchSize = 3;
        private const int MaxRetries = 3;

        private static readonly Regex SpecialCharRegex = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
        private static readonly Regex WhiteSpaceRegex = new(@"\s+", RegexOptions.Compiled);

        private static readonly HashSet<string> AccessoriesBlacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            "op lung", "bao da", "kinh cuong luc", "mieng dan", "dan man hinh",
            "cap sac", "cu sac", "tai nghe", "day deo", "vo boc", "balo", "tui chong soc"
        };

        private static readonly string[] MarketingWords = {
            "chính hãng", "chinh hang", "nhập khẩu", "nhap khau",
            "bản quốc tế", "ban quoc te", "nguyên seal", "nguyen seal",
            "mới 100%", "moi 100%", "freeship", "tặng kèm", "tang kem",
            "vn/a", "ll/a", "fullbox", "giá rẻ", "gia re", "hàng cty"
        };

        public SuggestionCrawlService(
            IServiceScopeFactory scopeFactory,
            IEcommerceServiceFactory serviceFactory,
            ILogger<SuggestionCrawlService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task UpdateAllTrackedProductSuggestionsAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("🚀 [Hangfire/Multi-Platform] Bắt đầu tiến trình crawl gợi ý sản phẩm...");

            List<Product> productsToUpdate;

            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ePriTrackerContext>();
                productsToUpdate = await context.Product
                    .AsNoTracking()
                    .Where(p => context.Item.Any(i => i.ProductId == p.ProductId))
                    .ToListAsync(cancellationToken);
            }

            if (!productsToUpdate.Any()) return;

            var productChunks = productsToUpdate.Chunk(BatchSize);
            int successCount = 0;

            foreach (var chunk in productChunks)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var fetchTasks = chunk.Select(product => ProcessSingleProductSuggestionAsync(product, cancellationToken)).ToList();
                var chunkResults = await Task.WhenAll(fetchTasks);

                var validSuggestions = chunkResults.Where(r => r.Suggestions.Any()).ToList();

                if (validSuggestions.Any())
                {
                    await SaveBatchToDatabaseAsync(validSuggestions, cancellationToken);
                    successCount += validSuggestions.Count;
                }

                await Task.Delay(Random.Shared.Next(1500, 3000), cancellationToken);
            }

            _logger.LogInformation($"✅ [Hangfire] Đã cập nhật xong gợi ý cho {successCount}/{productsToUpdate.Count} sản phẩm.");
        }

        private async Task<(Guid ProductId, List<SuggestionProduct> Suggestions)> ProcessSingleProductSuggestionAsync(Product product, CancellationToken ct)
        {
            try
            {
                decimal basePrice = (product.InitialPrice > 0 ? product.InitialPrice : product.LatestPrice) ?? 0m;

                // Định tuyến qua Factory dựa vào link sản phẩm của từng sàn
                var ecommerceService = _serviceFactory.GetService(product.ProductLink);
                string searchKeyword = ExtractSearchKeyword(product.ProductName);

                var rawSuggestions = await ecommerceService.CrawlSuggestionsAsync(product.ProductId, searchKeyword, basePrice);

                var validSuggestions = rawSuggestions
                    .Where(s => IsValidSimilarProduct(product.ProductName, s.ProductName) &&
                                s.Price > 0 && s.Price <= basePrice &&
                                s.Price >= (basePrice * MinPriceRatioThreshold))
                    .ToList();

                return (product.ProductId, validSuggestions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Lỗi khi crawl gợi ý cho SP ID: {ProductId}", product.ProductId);
                return (product.ProductId, new List<SuggestionProduct>());
            }
        }

        private async Task SaveBatchToDatabaseAsync(
            List<(Guid ProductId, List<SuggestionProduct> Suggestions)> batchData,
            CancellationToken cancellationToken)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ePriTrackerContext>();
                using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    var productIdsInBatch = batchData.Select(b => b.ProductId).ToList();

                    await context.Set<SuggestionProduct>()
                        .Where(s => productIdsInBatch.Contains(s.ProductId))
                        .ExecuteDeleteAsync(cancellationToken);

                    var newSuggestions = batchData.SelectMany(b => b.Suggestions).ToList();

                    if (newSuggestions.Any())
                    {
                        await context.Set<SuggestionProduct>().AddRangeAsync(newSuggestions, cancellationToken);
                        await context.SaveChangesAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            }
        }

        private static string ExtractSearchKeyword(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
            var splitChars = new char[] { '-', '|', '(', ')', '[', ']', ',', '/', ':' };
            var parts = rawName.Split(splitChars, StringSplitOptions.RemoveEmptyEntries);
            string coreName = (parts.Length > 0 && parts[0].Trim().Length >= 5) ? parts[0] : rawName;
            coreName = coreName.ToLowerInvariant();

            foreach (var word in MarketingWords)
            {
                coreName = Regex.Replace(coreName, $@"\b{Regex.Escape(word)}\b", "", RegexOptions.IgnoreCase);
            }

            coreName = SpecialCharRegex.Replace(coreName, " ");
            coreName = WhiteSpaceRegex.Replace(coreName, " ").Trim();

            var words = coreName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(5));
        }

        private static bool IsValidSimilarProduct(string originalName, string searchName)
        {
            if (string.IsNullOrWhiteSpace(originalName) || string.IsNullOrWhiteSpace(searchName)) return false;
            var lowerOriginalNoMark = RemoveVietnameseDiacritics(originalName.ToLowerInvariant());
            var lowerSearchNoMark = RemoveVietnameseDiacritics(searchName.ToLowerInvariant());

            bool originalIsAccessory = AccessoriesBlacklist.Any(x => lowerOriginalNoMark.Contains(x));
            bool searchIsAccessory = AccessoriesBlacklist.Any(x => lowerSearchNoMark.Contains(x));

            if (!originalIsAccessory && searchIsAccessory) return false;
            return true;
        }

        private static string RemoveVietnameseDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

            foreach (char c in normalizedString)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }
            return stringBuilder.ToString().Normalize(NormalizationForm.FormC).Replace('đ', 'd').Replace('Đ', 'D');
        }
    }
}