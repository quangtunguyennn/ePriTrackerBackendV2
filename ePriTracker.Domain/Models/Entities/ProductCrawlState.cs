using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ePriTracker.Domain.Models.Entities;

[Table("product_crawl_states", Schema = "tracker")]
public partial class ProductCrawlState
{
    [Key]
    [Column("product_id")]
    public long ProductId { get; set; }

    [Column("current_price", TypeName = "decimal(18, 2)")]
    public decimal? CurrentPrice { get; set; }

    [Column("priority_tier")]
    public byte PriorityTier { get; set; }

    [Column("next_crawl_at")]
    [Precision(3)]
    public DateTimeOffset NextCrawlAt { get; set; }

    [Column("last_crawled_at")]
    [Precision(3)]
    public DateTimeOffset? LastCrawledAt { get; set; }

    [Column("view_count_7d")]
    public int ViewCount7d { get; set; }

    [Column("view_count_30d")]
    public int ViewCount30d { get; set; }

    [Column("locked_by")]
    [StringLength(100)]
    [Unicode(false)]
    public string? LockedBy { get; set; }

    [Column("lease_until")]
    [Precision(3)]
    public DateTimeOffset? LeaseUntil { get; set; }

    [Column("retry_count")]
    public int RetryCount { get; set; }

    [ForeignKey("ProductId")]
    [InverseProperty("ProductCrawlState")]
    public virtual Product Product { get; set; } = null!;
}
