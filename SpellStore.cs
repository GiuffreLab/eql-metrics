using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EqlMetrics.Core;

namespace EqlMetrics
{
    /// <summary>
    /// Loads spells.json (produced by tools/scrape-spells.ps1) from
    /// %APPDATA%\EqlMetrics\ and, if present, overrides the baked-in self-buff
    /// table so refreshed wiki data is picked up without a rebuild.
    /// </summary>
    public static class SpellStore
    {
        private sealed class Row
        {
            public string spell { get; set; } = "";
            public double duration_sec { get; set; }
            public string spell_type { get; set; } = "";
            public string cast_on_you { get; set; } = "";
            public string wears_off { get; set; } = "";
        }

        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EqlMetrics", "spells.json");

        /// <summary>Returns the number of self-buffs loaded (0 = kept baked-in defaults).</summary>
        public static int LoadIntoBuffData()
        {
            try
            {
                if (!File.Exists(FilePath)) return 0;
                var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(FilePath));
                if (rows == null) return 0;

                var defs = new List<BuffDef>();
                foreach (var r in rows)
                {
                    // self-buffs: beneficial, with a "you feel..." apply and a wear-off, not a short HoT tick
                    if (!string.Equals(r.spell_type, "Beneficial", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(r.cast_on_you) || string.IsNullOrWhiteSpace(r.wears_off)) continue;
                    if (r.duration_sec > 0 && r.duration_sec < 30) continue;   // skip HoT ticks / very short
                    defs.Add(new BuffDef
                    {
                        Spell = r.spell,
                        Apply = r.cast_on_you.Trim(),
                        Fade = r.wears_off.Trim(),
                        DurationSec = r.duration_sec
                    });
                }
                if (defs.Count > 0) { BuffData.Rebuild(defs); return defs.Count; }
            }
            catch { /* keep baked-in defaults on any error */ }
            return 0;
        }
    }
}
