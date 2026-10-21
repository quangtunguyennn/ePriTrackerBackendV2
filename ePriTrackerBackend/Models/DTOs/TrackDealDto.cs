namespace ePriTrackerBackend.Models.DTOs
{
    public class TrackDealDto
    {
        public string ProductLink { get; set; }
        public string ProductName { get; set; }
        public decimal CurrentPrice { get; set; }
        public string ImageURL { get; set; }
    }
}
