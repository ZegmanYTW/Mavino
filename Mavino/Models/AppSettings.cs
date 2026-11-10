namespace Mavino.Models
{
    public class AppSettings
    {
        public double Volume { get; set; } = 0.7;
        public bool Shuffle { get; set; } = false;
        public RepeatMode RepeatMode { get; set; } = RepeatMode.Off;
        public string? LastFolder { get; set; }
    }

    public enum RepeatMode
    {
        Off,
        All,
        One
    }
}
