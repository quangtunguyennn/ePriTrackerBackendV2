using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Models.Entities;

[PrimaryKey("UserId", "RoleId")]
[Table("user_roles")]
public partial class UserRole
{
    [Key]
    [Column("user_id")]
    public long UserId { get; set; }

    [Key]
    [Column("role_id")]
    public int RoleId { get; set; }

    [Column("assigned_at")]
    [Precision(3)]
    public DateTimeOffset AssignedAt { get; set; }

    [ForeignKey("RoleId")]
    [InverseProperty("UserRoles")]
    public virtual Role Role { get; set; } = null!;

    [ForeignKey("UserId")]
    [InverseProperty("UserRoles")]
    public virtual User User { get; set; } = null!;
}
