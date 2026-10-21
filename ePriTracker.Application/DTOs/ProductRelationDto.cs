namespace ePriTracker.Application.DTOs;

public class ProductRelationDto
{
    public long RelationId { get; set; }
    public long SourceProductId { get; set; }
    public long TargetProductId { get; set; }

    // Đổi tên PrimaryProductId thành SourceProductId cho đồng bộ (nếu bạn có dùng trước đó)
    // public long PrimaryProductId { get; set; } 

    public string RelatedProductName { get; set; } = string.Empty;
    public string RelatedProductUrl { get; set; } = string.Empty;
    public string? RelatedProductImageUrl { get; set; }
    public decimal RelatedProductPrice { get; set; }
    public string RelationType { get; set; } = string.Empty;
    public decimal MatchScore { get; set; }
}