using System;
using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.Models
{
    public class RootFolder
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [MaxLength(1000)]
        public string Path { get; set; } = string.Empty;

        public bool IsDefault { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        public string? AudiobookshelfLibraryId { get; set; }
    }
}