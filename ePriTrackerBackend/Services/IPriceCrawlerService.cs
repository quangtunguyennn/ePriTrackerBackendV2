namespace ePriTrackerBackend.Services
{
    public interface IPriceCrawlerService
    {
        Task UpdateAllTrackedProductPricesAsync();
    }
}