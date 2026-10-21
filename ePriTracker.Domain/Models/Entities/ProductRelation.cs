using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ePriTracker.Domain.Models.Entities;

[Table("product_relations", Schema = "tracker")]
[Index("SourceProductId", "RelationType", Name = "ix_product_relations_lookup")]
[Index("TargetProductId", "RelationType", Name = "ix_product_relations_target")]
[Index("SourceProductId", "TargetProductId", "RelationType", Name = "uk_source_target", IsUnique = true)]
public partial class ProductRelation
{
    [Key]
    [Column("relation_id")]
    public long RelationId { get; set; }

    [Column("source_product_id")]
    public long SourceProductId { get; set; }

    [Column("target_product_id")]
    public long TargetProductId { get; set; }

    [Column("relation_type")]
    [StringLength(20)]
    [Unicode(false)]
    public string RelationType { get; set; } = null!;

    [Column("match_score", TypeName = "decimal(5, 2)")]
    public decimal MatchScore { get; set; }

    [Column("created_at")]
    [Precision(3)]
    public DateTimeOffset CreatedAt { get; set; }

    [ForeignKey("SourceProductId")]
    [InverseProperty("ProductRelationSourceProducts")]
    public virtual Product SourceProduct { get; set; } = null!;

    [ForeignKey("TargetProductId")]
    [InverseProperty("ProductRelationTargetProducts")]
    public virtual Product TargetProduct { get; set; } = null!;
}
