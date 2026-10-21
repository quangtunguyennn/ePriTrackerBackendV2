using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ePriTracker.Domain.Models.Entities;

[Table("products", Schema = "tracker")]
[Index("ProductId", "PlatformId", Name = "uk_products_id_platform", IsUnique = true)]
[Index("PlatformId", "ExternalSku", Name = "uk_products_platform_sku", IsUnique = true)]
[Index("ProductUrl", Name = "ix_products_product_url")]
public partial class Product
{
    [Key]
    [Column("product_id")]
    public long ProductId { get; set; }

    [Column("platform_id")]
    public int PlatformId { get; set; }

    [Column("external_sku")]
    [StringLength(100)]
    [Unicode(false)]
    public string ExternalSku { get; set; } = null!;

    [Column("product_name")]
    [StringLength(500)]
    public string ProductName { get; set; } = null!;

    [Column("product_url")]
    [StringLength(2048)]
    [Unicode(false)]
    public string ProductUrl { get; set; } = null!;

    [Column("image_url")]
    [StringLength(2048)]
    [Unicode(false)]
    public string? ImageUrl { get; set; }

    [Column("youtube_review_links")]
    public string? YoutubeReviewLinks { get; set; }

    [Column("tech_specs")]
    public string? TechSpecs { get; set; }

    [Column("status")]
    public byte Status { get; set; }

    [Column("created_at")]
    [Precision(3)]
    public DateTimeOffset CreatedAt { get; set; }

    [InverseProperty("Product")]
    public virtual ICollection<EventProduct> EventProducts { get; set; } = new List<EventProduct>();

    [ForeignKey("PlatformId")]
    [InverseProperty("Products")]
    public virtual Platform Platform { get; set; } = null!;

    [InverseProperty("Product")]
    public virtual ICollection<PriceHistory> PriceHistories { get; set; } = new List<PriceHistory>();

    [InverseProperty("Product")]
    public virtual ProductCrawlState? ProductCrawlState { get; set; }

    [InverseProperty("SourceProduct")]
    public virtual ICollection<ProductRelation> ProductRelationSourceProducts { get; set; } = new List<ProductRelation>();

    [InverseProperty("TargetProduct")]
    public virtual ICollection<ProductRelation> ProductRelationTargetProducts { get; set; } = new List<ProductRelation>();

    [InverseProperty("Product")]
    public virtual ICollection<UserTrackedItem> UserTrackedItems { get; set; } = new List<UserTrackedItem>();
}
