using Microsoft.EntityFrameworkCore.Metadata.Internal;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ePriTrackerBackend.Models.Entities
{
    public class SuggestionProduct
    {
        [Key]
        public Guid SuggestionProductId { get; set; }
        [Required]
        public Guid ProductId { get; set; }
        [JsonIgnore]
        public Product? Product { get; set; }
        [Required]
        public string ProductName { get; set; }
        [Required]
        public decimal Price { get; set; }
        [Required]
        public string ImageURL { get; set; }
        [Required]
        public string ProductLink { get; set; }
        public DateTimeOffset? LastUpdatedAt { get; set; }
        
    }
}
