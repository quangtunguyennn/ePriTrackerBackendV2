namespace ePriTracker.Application.DTOs;

public class ProductDto
{
    public long ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string ProductLink { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public decimal InitialPrice { get; set; }
    public decimal LatestPrice { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
}