using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EqlMetrics.Core;

namespace EqlMetrics
{
    /// <summary>
    /// Loads spells.json (produced by the in-app updater / SpellScraper) from
    /// %APPDATA%\EqlMetrics\ and, if present, overrides the baked-in self-buff table
    /// plus all-spell durations so refreshed wiki data is picked up without a rebuild.
    /// </summary>
    public static class SpellStore
    {
        /// <summary>Full path of the user's spells.json (in %APPDATA%, writable without admin).</summary>
        public static string SpellsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EqlMetrics", "spells.json");

        /// <summary>Human-readable state of the downloaded spell data (for the Settings window).</summary>
        public static string Status()
        {
            try
            {
                if (!File.Exists(SpellsPath)) return "not downloaded yet";
                int count = 0;
                try { count = JsonSerializer.Deserialize<List<SpellRow>>(File.ReadAllText(SpellsPath))?.Count ?? 0; } catch { }
                return $"{count} spells · updated {File.GetLastWriteTime(SpellsPath):yyyy-MM-dd}";
            }
            catch { return "unknown"; }
        }

        /// <summary>Returns the number of self-buffs loaded (0 = kept baked-in defaults).</summary>
        public static int LoadIntoBuffData()
        {
            try
            {
                if (!File.Exists(SpellsPath)) return 0;
                var rows = JsonSerializer.Deserialize<List<SpellRow>>(File.ReadAllText(SpellsPath));
                if (rows == null) return 0;
                return SpellCatalog.ApplyRows(rows);   // shared filter/apply (same path as the live updater)
            }
            catch { /* keep baked-in defaults on any error */ }
            return 0;
        }
    }
}
