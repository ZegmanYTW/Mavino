using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mavino.Models;

namespace Mavino.Services
{
    public static class LrcParser
    {
        private static readonly Regex TimeRegex = new Regex(@"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\]", RegexOptions.Compiled);

        public static List<LrcLine> Parse(string filePath)
        {
            if (!File.Exists(filePath)) return new List<LrcLine>();
            try
            {
                return ParseFromString(File.ReadAllText(filePath));
            }
            catch
            {
                return new List<LrcLine>();
            }
        }

        public static List<LrcLine> ParseFromString(string content)
        {
            var lines = new List<LrcLine>();
            if (string.IsNullOrWhiteSpace(content)) return lines;

            try
            {
                foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var matches = TimeRegex.Matches(line);
                    if (matches.Count == 0) continue;

                    var text = TimeRegex.Replace(line, "").Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    foreach (Match match in matches)
                    {
                        int min = int.Parse(match.Groups[1].Value);
                        int sec = int.Parse(match.Groups[2].Value);
                        int ms = 0;
                        if (match.Groups[3].Success)
                        {
                            var msStr = match.Groups[3].Value.PadRight(3, '0').Substring(0, 3);
                            ms = int.Parse(msStr);
                        }
                        var ts = new TimeSpan(0, 0, min, sec, ms);
                        lines.Add(new LrcLine { Timestamp = ts, Text = text });
                    }
                }
                return lines.OrderBy(l => l.Timestamp).ToList();
            }
            catch
            {
                return lines;
            }
        }

        public static string? FindLrcForTrack(string audioPath)
        {
            var dir = Path.GetDirectoryName(audioPath);
            var name = Path.GetFileNameWithoutExtension(audioPath);
            if (dir == null) return null;

            var candidates = new[]
            {
                Path.Combine(dir, name + ".lrc"),
                Path.Combine(dir, name + ".LRC"),
                Path.Combine(dir, "lyrics", name + ".lrc")
            };

            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
