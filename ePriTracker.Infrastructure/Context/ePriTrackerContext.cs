using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using ePriTracker.Domain.Models.Entities;

namespace ePriTracker.Infrastructure.Context;

public partial class ePriTrackerContext : DbContext
{
    public ePriTrackerContext(DbContextOptions<ePriTrackerContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Event> Events { get; set; }
    public virtual DbSet<EventProduct> EventProducts { get; set; }
    public virtual DbSet<Platform> Platforms { get; set; }
    public virtual DbSet<PriceHistory> PriceHistories { get; set; }
    public virtual DbSet<Product> Products { get; set; }
    public virtual DbSet<ProductCrawlState> ProductCrawlStates { get; set; }
    public virtual DbSet<ProductRelation> ProductRelations { get; set; }
    public virtual DbSet<Role> Roles { get; set; }
    public virtual DbSet<User> Users { get; set; }
    public virtual DbSet<UserRole> UserRoles { get; set; }
    public virtual DbSet<UserTrackedItem> UserTrackedItems { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Event>(entity =>
        {
            entity.HasKey(e => e.EventId).HasName("pk_events");
            entity.ToTable("events", "tracker", tb => tb.HasTrigger("trg_events_updated_at"));
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysdatetimeoffset())");
            entity.Property(e => e.Status).HasDefaultValue((byte)1);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("(sysdatetimeoffset())");
            entity.HasOne(d => d.Platform).WithMany(p => p.Events)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_events_platforms");
        });

        modelBuilder.Entity<EventProduct>(entity =>
        {
            entity.HasKey(e => new { e.EventId, e.ProductId }).HasName("pk_event_products");
            entity.Property(e => e.AddedAt).HasDefaultValueSql("(sysdatetimeoffset())");
            entity.HasOne(d => d.Event).WithMany(p => p.EventProducts)
                .HasPrincipalKey(p => new { p.EventId, p.PlatformId })
                .HasForeignKey(d => new { d.EventId, d.PlatformId })
                .HasConstraintName("fk_eventprod_events");
            entity.HasOne(d => d.Product).WithMany(p => p.EventProducts)
                .HasPrincipalKey(p => new { p.ProductId, p.PlatformId })
                .HasForeignKey(d => new { d.ProductId, d.PlatformId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_eventprod_products");
        });

        modelBuilder.Entity<Platform>(entity =>
        {
            entity.HasKey(e => e.PlatformId).HasName("pk_platforms");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<PriceHistory>(entity =>
        {
            entity.HasKey(e => new { e.HistoryId, e.CrawledAt }).HasName("pk_price_histories");
            entity.Property(e => e.HistoryId).ValueGeneratedOnAdd();
            entity.Property(e => e.CrawledAt).HasDefaultValueSql("(sysdatetimeoffset())");
            entity.HasOne(d => d.Product).WithMany(p => p.PriceHistories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_pricehist_products");
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.ProductId).HasName("pk_products");
            entity.ToTable(tb => tb.IsTemporal(ttb =>
            {
                ttb.UseHistoryTable("products_history", "tracker");
                ttb.HasPeriodStart("SysStartTime").HasColumnName("SysStartTime");
                ttb.HasPeriodEnd("SysEndTime").HasColumnName("SysEndTime");
            }));
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysdatetimeoffset())");
            entity.Property(e => e.Status).HasDefaultValue((byte)1);
            entity.HasOne(d => d.Platform).WithMany(p => p.Products)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_products_platforms");
        });

        modelBuilder.Entity<ProductCrawlState>(entity =>
        {
            entity.HasKey(e => e.ProductId).HasName("pk_product_crawl_states");
            entity.HasIndex(e => new { e.PriorityTier, e.NextCrawlAt }, "ix_crawl_scheduler").HasFilter("([retry_count]<(5))");
            entity.Property(e => e.ProductId).ValueGeneratedNever();
            entity.Property(e => e.NextCrawlAt).HasDefaultValueSql("(sysdatetimeoffset())");
            entity.Property(e => e.PriorityTier).HasDefaultValue((byte)3);
            entity.HasOne(d => d.Product).WithOne(p => p.ProductCrawlState).HasConstraintName("fk_crawl_states_products");
        });

        modelBuilder.Entity<ProductRelation>(entity =>
        {
            entity.HasKey(e => e.RelationId).HasName("pk_product_relations");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysdatetimeoffset())");
            entity.HasOne(d => d.SourceProduct).WithMany(p => p.ProductRelationSourceProducts).HasConstraintName("fk_relations_source");
            entity.HasOne(d => d.TargetProduct).WithMany(p => p.ProductRelationTargetProducts)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_relations_target");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.RoleId).HasName("pk_roles");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("pk_users");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysdatetimeoffset())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.RoleId }).HasName("pk_user_roles");
            entity.Property(e => e.AssignedAt).HasDefaultValueSql("(sysdatetimeoffset())");
            entity.HasOne(d => d.Role).WithMany(p => p.UserRoles).HasConstraintName("fk_userroles_roles");
            entity.HasOne(d => d.User).WithMany(p => p.UserRoles).HasConstraintName("fk_userroles_users");
        });

        modelBuilder.Entity<UserTrackedItem>(entity =>
        {
            entity.HasKey(e => e.TrackedItemId).HasName("pk_user_tracked_items");
            entity.HasIndex(e => e.ProductId, "ix_tracked_items_product_alert").HasFilter("([is_notification_enabled]=(1))");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysdatetimeoffset())");
            entity.Property(e => e.IsNotificationEnabled).HasDefaultValue(true);
            entity.HasOne(d => d.Product).WithMany(p => p.UserTrackedItems)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_tracked_items_products");
            entity.HasOne(d => d.User).WithMany(p => p.UserTrackedItems).HasConstraintName("fk_tracked_items_users");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}