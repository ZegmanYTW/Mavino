using System;

namespace Mavino.Models
{
    public class Track
    {
        public string FilePath { get; set; } = string.Empty;
        public string Title { get; set; } = "Unknown Title";
        public string Artist { get; set; } = "Unknown Artist";
        public string Album { get; set; } = "Unknown Album";
        public TimeSpan Duration { get; set; }
        public string? AlbumArtPath { get; set; }
        public byte[]? AlbumArtData { get; set; }
        public string? LyricsPath { get; set; }
        public bool IsFavorite { get; set; }
    }
}
