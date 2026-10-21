using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ePriTracker.Domain.Models.Entities;

[Table("platforms", Schema = "tracker")]
[Index("PlatformCode", Name = "uk_platforms_code", IsUnique = true)]
public partial class Platform
{
    [Key]
    [Column("platform_id")]
    public int PlatformId { get; set; }

    [Column("platform_code")]
    [StringLength(50)]
    [Unicode(false)]
    public string PlatformCode { get; set; } = null!;

    [Column("platform_name")]
    [StringLength(100)]
    public string PlatformName { get; set; } = null!;

    [Column("domain_url")]
    [StringLength(255)]
    [Unicode(false)]
    public string DomainUrl { get; set; } = null!;

    [Column("is_active")]
    public bool IsActive { get; set; }

    [InverseProperty("Platform")]
    public virtual ICollection<Event> Events { get; set; } = new List<Event>();

    [InverseProperty("Platform")]
    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
