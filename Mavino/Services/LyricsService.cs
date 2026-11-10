using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mavino.Models;

namespace Mavino.Services
{
    public class LyricsResult
    {
        public List<LrcLine> Lines { get; set; } = new();
        public string SourceId { get; set; } = "";
        public string SourceName { get; set; } = "";
        public bool IsSynced { get; set; }
    }

    public static class LyricsService
    {
        private static readonly HttpClient Http;

        static LyricsService()
        {
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(14) };
            Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mavino/1.1 (Windows)");
            Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        }

        public static async Task<List<LyricsResult>> GetAllSourcesAsync(Track track)
        {
            var results = new List<LyricsResult>();

            var local = TryLocal(track);
            if (local != null) results.Add(local);

            var (artist, title) = NormalizeMeta(track);

            // Run sources in parallel
            var tasks = new List<Task<LyricsResult?>>
            {
                TryLrclibRobust(artist, title, track),
                TryNetEase(artist, title),
                TryOvh(artist, title)
            };

            var online = await Task.WhenAll(tasks);
            foreach (var r in online)
            {
                if (r != null && r.Lines.Count > 0)
                    results.Add(r);
            }

            // Generic names without domains
            int n = 1;
            foreach (var r in results)
            {
                if (r.SourceId == "local")
                    r.SourceName = "Локальный";
                else
                    r.SourceName = $"Источник {n++}";
            }

            return results;
        }

        private static (string artist, string title) NormalizeMeta(Track track)
        {
            var artist = Clean(track.Artist);
            var title = Clean(track.Title);

            // If title is like "Artist - Song"
            if ((string.IsNullOrWhiteSpace(artist) || artist == "Unknown Artist") && title.Contains(" - "))
            {
                var parts = title.Split(new[] { " - " }, 2, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    artist = Clean(parts[0]);
                    title = Clean(parts[1]);
                }
            }

            // From filename if still weak
            if ((string.IsNullOrWhiteSpace(title) || title == "Unknown Title") &&
                !string.IsNullOrEmpty(track.FilePath))
            {
                var name = Path.GetFileNameWithoutExtension(track.FilePath);
                name = Clean(name);
                if (name.Contains(" - "))
                {
                    var parts = name.Split(new[] { " - " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        if (string.IsNullOrWhiteSpace(artist) || artist == "Unknown Artist")
                            artist = Clean(parts[0]);
                        title = Clean(parts[1]);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(name))
                {
                    title = name;
                }
            }

            // Strip feat. from title for search
            title = Regex.Replace(title, @"\s*[\(\[]?\s*feat\.?.*", "", RegexOptions.IgnoreCase).Trim();
            title = Regex.Replace(title, @"\s*[\(\[]?\s*ft\.?.*", "", RegexOptions.IgnoreCase).Trim();

            if (artist == "Unknown Artist") artist = "";
            if (title == "Unknown Title") title = "";

            return (artist, title);
        }

        private static LyricsResult? TryLocal(Track track)
        {
            string? path = track.LyricsPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                path = LrcParser.FindLrcForTrack(track.FilePath);

            if (path == null || !File.Exists(path)) return null;

            var lines = LrcParser.Parse(path);
            if (lines.Count == 0) return null;

            return new LyricsResult
            {
                Lines = lines,
                SourceId = "local",
                IsSynced = lines.Any(l => l.Timestamp > TimeSpan.Zero)
            };
        }

        private static async Task<LyricsResult?> TryLrclibRobust(string artist, string title, Track track)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                return null;

            // Build several query attempts
            var attempts = new List<Func<Task<LyricsResult?>>>();

            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
            {
                attempts.Add(() => LrclibSearchParams(artist, title));
                attempts.Add(() => LrclibSearchQ($"{artist} {title}"));
            }

            if (!string.IsNullOrWhiteSpace(title))
                attempts.Add(() => LrclibSearchQ(title));

            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                attempts.Add(() => LrclibExact(artist, title, track));

            // Filename fallback
            if (!string.IsNullOrEmpty(track.FilePath))
            {
                var fn = Clean(Path.GetFileNameWithoutExtension(track.FilePath));
                if (!string.IsNullOrWhiteSpace(fn))
                    attempts.Add(() => LrclibSearchQ(fn));
            }

            foreach (var attempt in attempts)
            {
                try
                {
                    var r = await attempt();
                    if (r != null && r.Lines.Count > 0)
                        return r;
                }
                catch { }
            }

            return null;
        }

        private static async Task<LyricsResult?> LrclibSearchParams(string artist, string title)
        {
            var url = $"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
            return await LrclibParseSearch(url);
        }

        private static async Task<LyricsResult?> LrclibSearchQ(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return null;
            var url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(q)}";
            return await LrclibParseSearch(url);
        }

        private static async Task<LyricsResult?> LrclibExact(string artist, string title, Track track)
        {
            var album = Clean(track.Album);
            var duration = (int)track.Duration.TotalSeconds;
            var url =
                $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}" +
                $"&track_name={Uri.EscapeDataString(title)}" +
                $"&album_name={Uri.EscapeDataString(album)}" +
                $"&duration={duration}";

            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            return ParseLrclibObject(json);
        }

        private static async Task<LyricsResult?> LrclibParseSearch(string url)
        {
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            LyricsResult? bestSynced = null;
            LyricsResult? bestPlain = null;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                // Prefer synced
                if (item.TryGetProperty("syncedLyrics", out var synced) &&
                    synced.ValueKind == JsonValueKind.String)
                {
                    var text = synced.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var lines = LrcParser.ParseFromString(text);
                        if (lines.Count > 0)
                        {
                            bestSynced = new LyricsResult
                            {
                                Lines = lines,
                                SourceId = "lrclib",
                                IsSynced = true
                            };
                            break; // first synced is enough
                        }
                    }
                }

                if (bestPlain == null &&
                    item.TryGetProperty("plainLyrics", out var plain) &&
                    plain.ValueKind == JsonValueKind.String)
                {
                    var text = plain.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(t => new LrcLine { Timestamp = TimeSpan.Zero, Text = t.Trim() })
                            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
                            .ToList();
                        if (lines.Count > 0)
                        {
                            bestPlain = new LyricsResult
                            {
                                Lines = lines,
                                SourceId = "lrclib",
                                IsSynced = false
                            };
                        }
                    }
                }
            }

            return bestSynced ?? bestPlain;
        }

        private static LyricsResult? ParseLrclibObject(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("syncedLyrics", out var synced) &&
                synced.ValueKind == JsonValueKind.String)
            {
                var text = synced.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var lines = LrcParser.ParseFromString(text);
                    if (lines.Count > 0)
                        return new LyricsResult { Lines = lines, SourceId = "lrclib", IsSynced = true };
                }
            }

            if (root.TryGetProperty("plainLyrics", out var plain) &&
                plain.ValueKind == JsonValueKind.String)
            {
                var text = plain.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => new LrcLine { Timestamp = TimeSpan.Zero, Text = t.Trim() })
                        .Where(l => !string.IsNullOrWhiteSpace(l.Text))
                        .ToList();
                    if (lines.Count > 0)
                        return new LyricsResult { Lines = lines, SourceId = "lrclib", IsSynced = false };
                }
            }

            return null;
        }

        private static async Task<LyricsResult?> TryNetEase(string artist, string title)
        {
            try
            {
                var keyword = $"{artist} {title}".Trim();
                if (string.IsNullOrWhiteSpace(keyword)) return null;

                var searchUrl = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(keyword)}&type=1&limit=8";
                var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                request.Headers.TryAddWithoutValidation("Referer", "https://music.163.com");

                using var searchResp = await Http.SendAsync(request);
                var searchJson = await searchResp.Content.ReadAsStringAsync();
                using var searchDoc = JsonDocument.Parse(searchJson);

                if (!searchDoc.RootElement.TryGetProperty("result", out var result)) return null;
                if (!result.TryGetProperty("songs", out var songs) || songs.GetArrayLength() == 0) return null;

                foreach (var song in songs.EnumerateArray().Take(4))
                {
                    if (!song.TryGetProperty("id", out var idProp)) continue;
                    var songId = idProp.GetInt64();

                    var lyricUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1";
                    var lyricReq = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
                    lyricReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                    lyricReq.Headers.TryAddWithoutValidation("Referer", "https://music.163.com");

                    using var lyricResp = await Http.SendAsync(lyricReq);
                    var lyricJson = await lyricResp.Content.ReadAsStringAsync();
                    using var lyricDoc = JsonDocument.Parse(lyricJson);

                    if (lyricDoc.RootElement.TryGetProperty("lrc", out var lrc) &&
                        lrc.TryGetProperty("lyric", out var lyricProp))
                    {
                        var text = lyricProp.GetString();
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        var lines = LrcParser.ParseFromString(text);
                        if (lines.Count > 2)
                            return new LyricsResult { Lines = lines, SourceId = "netease", IsSynced = true };

                        // plain fallback from same text without timestamps
                        var plainLines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => Regex.Replace(line, @"\[.*?\]", "").Trim())
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .Select(t => new LrcLine { Timestamp = TimeSpan.Zero, Text = t })
                            .ToList();
                        if (plainLines.Count > 2)
                            return new LyricsResult { Lines = plainLines, SourceId = "netease", IsSynced = false };
                    }
                }
            }
            catch { }
            return null;
        }

        private static async Task<LyricsResult?> TryOvh(string artist, string title)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title)) return null;
                var a = string.IsNullOrWhiteSpace(artist) ? "Unknown" : artist;
                var url = $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(a)}/{Uri.EscapeDataString(title)}";
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("lyrics", out var lyricsProp))
                {
                    var text = lyricsProp.GetString();
                    if (string.IsNullOrWhiteSpace(text)) return null;

                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => new LrcLine { Timestamp = TimeSpan.Zero, Text = t.Trim() })
                        .Where(l => !string.IsNullOrWhiteSpace(l.Text))
                        .ToList();

                    if (lines.Count > 0)
                        return new LyricsResult { Lines = lines, SourceId = "ovh", IsSynced = false };
                }
            }
            catch { }
            return null;
        }

        private static string Clean(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();

            var junk = new[]
            {
                "(Official Video)", "(Official Audio)", "(Official Music Video)",
                "[Official Video]", "[Official Audio]", "(Lyrics)", "[Lyrics]",
                "(Lyric Video)", "(Audio)", "(Visualizer)", "(Remastered)",
                "Official Video", "Official Audio", "Official Music Video",
                "(HD)", "(HQ)", "(Prod.", "prod. by"
            };
            foreach (var j in junk)
                s = s.Replace(j, "", StringComparison.OrdinalIgnoreCase);

            // Remove leftover empty brackets
            s = Regex.Replace(s, @"\[\s*\]", "");
            s = Regex.Replace(s, @"\(\s*\)", "");
            s = Regex.Replace(s, @"\s{2,}", " ");
            return s.Trim(" -\t".ToCharArray());
        }
    }
}
