using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;

namespace ePriTrackerBackend.Repositories
{
    public interface IEventRepository
    {
        Task<List<Event>> GetCurrentTikiEvents();
    }
}