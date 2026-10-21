namespace ePriTrackerBackend.Models.DTOs
{
    public class SuggestionProductDTO
    {
        public string ProductName { get; set; }
        public decimal Price { get; set; }
        public string ImageURL { get; set; }
        public string ProductLink { get; set; }
        public DateTimeOffset LastUpdatedAt { get; set; }

    }
}
