using ePriTrackerBackend.Models.Entities;

namespace ePriTrackerBackend.Services
{
    public interface ISuggestionsCrawlerService
    {
        /// <summary>
        /// Tự động cào và cập nhật danh sách gợi ý sản phẩm liên quan từ Tiki cho tất cả sản phẩm đang được theo dõi.
        /// </summary>
        /// <param name="cancellationToken">Token quản lý hủy bỏ tiến trình từ Hangfire hoặc hệ thống.</param>
        Task UpdateAllTrackedProductSuggestionsAsync(CancellationToken cancellationToken = default);
    }
}