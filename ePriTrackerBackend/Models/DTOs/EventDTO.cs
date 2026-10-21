namespace ePriTrackerBackend.Models.DTOs
{
    public class EventDTO
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public string EventLink { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string GroupZone { get; set; } = string.Empty;
    }
}
