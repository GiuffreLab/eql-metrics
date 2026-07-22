using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EqlMetrics.Core;

namespace EqlMetrics
{
    /// <summary>Persists learned buff durations/categories to %APPDATA%\EqlMetrics\buffs.json.</summary>
    public static class BuffStore
    {
        private sealed class Data
        {
            public Dictionary<string, double> durations { get; set; } = new();
            public Dictionary<string, string> categories { get; set; } = new();
            public List<string> songs { get; set; } = new();   // buffs that pulse (bard songs) — suppressed from popups
        }

        private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EqlMetrics");
        private static string FilePath => Path.Combine(Dir, "buffs.json");

        public static (Dictionary<string, double>, Dictionary<string, BuffCat>, HashSet<string>) Load()
        {
            var dur = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var cat = new Dictionary<string, BuffCat>(StringComparer.OrdinalIgnoreCase);
            var songs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                {
                    var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
                    if (d != null)
                    {
                        foreach (var kv in d.durations) dur[kv.Key] = kv.Value;
                        foreach (var kv in d.categories)
                            if (Enum.TryParse<BuffCat>(kv.Value, out var c)) cat[kv.Key] = c;
                        if (d.songs != null) foreach (var s in d.songs) songs.Add(s);
                    }
                }
            }
            catch { }
            return (dur, cat, songs);
        }

        public static void Save(Dictionary<string, double> durations, Dictionary<string, BuffCat> categories, HashSet<string> songs)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var d = new Data();
                foreach (var kv in durations) d.durations[kv.Key] = kv.Value;
                foreach (var kv in categories) d.categories[kv.Key] = kv.Value.ToString();
                foreach (var s in songs) d.songs.Add(s);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
