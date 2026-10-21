namespace ePriTrackerBackend.Services
{
    public interface IEventCacheService
    {
        Task RefreshLiveEventProductsCacheAsync();
    }
}
