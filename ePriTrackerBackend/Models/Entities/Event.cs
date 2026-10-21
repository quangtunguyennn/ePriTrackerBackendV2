using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Models.Entities;

[Table("events", Schema = "tracker")]
[Index("Status", "StartTime", "EndTime", Name = "ix_events_active_time")]
[Index("EventId", "PlatformId", Name = "uk_events_id_platform", IsUnique = true)]
public partial class Event
{
    [Key]
    [Column("event_id")]
    public int EventId { get; set; }

    [Column("platform_id")]
    public int PlatformId { get; set; }

    [Column("event_name")]
    [StringLength(255)]
    public string EventName { get; set; } = null!;

    [Column("event_url")]
    [StringLength(2048)]
    [Unicode(false)]
    public string EventUrl { get; set; } = null!;

    [Column("banner_url")]
    [StringLength(2048)]
    [Unicode(false)]
    public string? BannerUrl { get; set; }

    [Column("start_time")]
    [Precision(3)]
    public DateTimeOffset StartTime { get; set; }

    [Column("end_time")]
    [Precision(3)]
    public DateTimeOffset EndTime { get; set; }

    [Column("status")]
    public byte Status { get; set; }

    [Column("created_at")]
    [Precision(3)]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    [Precision(3)]
    public DateTimeOffset UpdatedAt { get; set; }

    [InverseProperty("Event")]
    public virtual ICollection<EventProduct> EventProducts { get; set; } = new List<EventProduct>();

    [ForeignKey("PlatformId")]
    [InverseProperty("Events")]
    public virtual Platform Platform { get; set; } = null!;
}
