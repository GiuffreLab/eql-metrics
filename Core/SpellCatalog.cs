using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace EqlMetrics.Core
{
    /// <summary>
    /// One scraped spell row, matching the shape of spells.json (snake_case keys so
    /// System.Text.Json round-trips it against the file the scraper writes).
    /// </summary>
    public sealed class SpellRow
    {
        public string spell { get; set; } = "";
        public string duration_text { get; set; } = "";
        public double duration_sec { get; set; }
        public string target_type { get; set; } = "";
        public string spell_type { get; set; } = "";
        public string mana { get; set; } = "";
        public string cast_on_you { get; set; } = "";
        public string cast_on_other { get; set; } = "";
        public string wears_off { get; set; } = "";
    }

    /// <summary>
    /// Applies a set of scraped spell rows to <see cref="BuffData"/>: authoritative
    /// durations for ALL spells (pet/debuff too) plus the self-buff apply/fade table.
    /// Shared by SpellStore (disk load) and SpellScraper (live update) so both paths
    /// filter identically.
    /// </summary>
    public static class SpellCatalog
    {
        /// <summary>Returns the number of self-buffs recognized (0 = nothing applied, defaults kept).</summary>
        public static int ApplyRows(IReadOnlyList<SpellRow> rows)
        {
            if (rows == null || rows.Count == 0) return 0;

            // authoritative durations for ALL spells (base-name keyed inside BuffData)
            var durs = new List<KeyValuePair<string, double>>();
            foreach (var r in rows)
                if (!string.IsNullOrWhiteSpace(r.spell) && r.duration_sec > 0)
                    durs.Add(new KeyValuePair<string, double>(r.spell, r.duration_sec));

            // self-buffs: beneficial, with a "you feel..." apply and a wear-off, not a short HoT tick
            var defs = new List<BuffDef>();
            foreach (var r in rows)
            {
                if (!string.Equals(r.spell_type, "Beneficial", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(r.cast_on_you) || string.IsNullOrWhiteSpace(r.wears_off)) continue;
                // reject scraper artifacts (a mis-captured "| param =" line)
                if (r.cast_on_you.TrimStart().StartsWith("|") || r.wears_off.TrimStart().StartsWith("|")) continue;
                if (r.duration_sec > 0 && r.duration_sec < 30) continue;   // skip HoT ticks / very short
                defs.Add(new BuffDef
                {
                    Spell = r.spell,
                    Apply = r.cast_on_you.Trim(),
                    Fade = r.wears_off.Trim(),
                    DurationSec = r.duration_sec
                });
            }

            // pet buffs: the landing line names the pet ("Someone goes berserk." -> "<pet> goes berserk."), so build
            // a matcher from cast_on_other with "Someone" as the target capture. Their fades still come from the
            // "Your pet's <spell> spell has worn off" line, so we only need the gain side here.
            var others = new List<OtherBuffDef>();
            foreach (var r in rows)
            {
                if (!string.Equals(r.target_type, "Pet", StringComparison.OrdinalIgnoreCase)) continue;
                string co = (r.cast_on_other ?? "").Trim();
                if (co.Length == 0 || co.StartsWith("|")) continue;
                if (co.IndexOf("Someone", StringComparison.Ordinal) < 0) continue;   // need the target placeholder
                var rx = BuildOtherRx(co);
                if (rx == null) continue;
                others.Add(new OtherBuffDef { Spell = r.spell, Category = BuffCat.Pet, DurationSec = r.duration_sec, Match = rx });
            }

            if (durs.Count > 0) BuffData.AddDurations(durs);
            if (defs.Count > 0) BuffData.Rebuild(defs);   // only replace the table when we actually parsed some
            BuffData.SetOtherApply(others);
            return defs.Count;
        }

        // "Someone  goes berserk." -> ^(?<t>.+?) goes berserk\.$   ("Someone" = target capture)
        private static Regex? BuildOtherRx(string castOnOther)
        {
            try
            {
                string norm = Regex.Replace(castOnOther.Trim(), @"\s+", " ");   // collapse the wiki's double-spaces
                string[] parts = Regex.Split(norm, "Someone");                  // literal split on the placeholder
                var sb = new StringBuilder("^");
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) sb.Append("(?<t>.+?)");
                    sb.Append(Regex.Escape(parts[i]));
                }
                sb.Append('$');
                return new Regex(sb.ToString(), RegexOptions.Compiled);
            }
            catch { return null; }
        }
    }
}
