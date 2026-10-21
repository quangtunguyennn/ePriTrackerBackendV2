using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ePriTracker.Domain.Models.Entities;

[Table("user_tracked_items", Schema = "tracker")]
[Index("UserId", "ProductId", Name = "uk_user_product", IsUnique = true)]
public partial class UserTrackedItem
{
    [Key]
    [Column("tracked_item_id")]
    public long TrackedItemId { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("product_id")]
    public long ProductId { get; set; }

    [Column("initial_price", TypeName = "decimal(18, 2)")]
    public decimal InitialPrice { get; set; }

    [Column("target_price", TypeName = "decimal(18, 2)")]
    public decimal? TargetPrice { get; set; }

    [Column("is_notification_enabled")]
    public bool IsNotificationEnabled { get; set; }

    [Column("created_at")]
    [Precision(3)]
    public DateTimeOffset CreatedAt { get; set; }

    [ForeignKey("ProductId")]
    [InverseProperty("UserTrackedItems")]
    public virtual Product Product { get; set; } = null!;

    [ForeignKey("UserId")]
    [InverseProperty("UserTrackedItems")]
    public virtual User User { get; set; } = null!;
}
