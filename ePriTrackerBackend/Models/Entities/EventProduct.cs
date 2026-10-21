using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Models.Entities;

[PrimaryKey("EventId", "ProductId")]
[Table("event_products", Schema = "tracker")]
[Index("ProductId", Name = "ix_event_products_product")]
public partial class EventProduct
{
    [Key]
    [Column("event_id")]
    public int EventId { get; set; }

    [Column("platform_id")]
    public int PlatformId { get; set; }

    [Key]
    [Column("product_id")]
    public long ProductId { get; set; }

    [Column("event_price", TypeName = "decimal(18, 2)")]
    public decimal EventPrice { get; set; }

    [Column("original_price", TypeName = "decimal(18, 2)")]
    public decimal? OriginalPrice { get; set; }

    [Column("is_flash_sale")]
    public bool IsFlashSale { get; set; }

    [Column("added_at")]
    [Precision(3)]
    public DateTimeOffset AddedAt { get; set; }

    [ForeignKey("EventId, PlatformId")]
    [InverseProperty("EventProducts")]
    public virtual Event Event { get; set; } = null!;

    [ForeignKey("ProductId, PlatformId")]
    [InverseProperty("EventProducts")]
    public virtual Product Product { get; set; } = null!;
}
