namespace ePriTrackerBackend.Models.DTOs
{
    public class LiveEventProductDTO
    {
        public string ProductId { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ImageURL { get; set; } = string.Empty; // Khớp với ProductRepository và React
        public string Desc { get; set; } = string.Empty;
        public decimal InitialPrice { get; set; }             // Giá gốc
        public decimal LatestPrice { get; set; }            // Giá sau giảm
        public string ProductLink { get; set; } = string.Empty;
        public DateTimeOffset LastUpdatedAt { get; set; }     // Dùng DateTimeOffset để khớp kiểu dữ liệu
    }
}