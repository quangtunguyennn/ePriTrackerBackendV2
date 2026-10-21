using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Models.Entities;

[PrimaryKey("HistoryId", "CrawledAt")]
[Table("price_histories", Schema = "tracker")]
[Index("ProductId", "CrawledAt", Name = "ix_price_histories_chart", IsDescending = new[] { false, true })]
public partial class PriceHistory
{
    [Key]
    [Column("history_id")]
    public long HistoryId { get; set; }

    [Column("product_id")]
    public long ProductId { get; set; }

    [Column("price", TypeName = "decimal(18, 2)")]
    public decimal Price { get; set; }

    [Column("discount_percentage")]
    public byte DiscountPercentage { get; set; }

    [Key]
    [Column("crawled_at")]
    [Precision(3)]
    public DateTimeOffset CrawledAt { get; set; }

    [ForeignKey("ProductId")]
    [InverseProperty("PriceHistories")]
    public virtual Product Product { get; set; } = null!;
}
