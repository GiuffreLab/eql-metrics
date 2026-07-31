using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EqlMetrics.Core
{
    /// <summary>
    /// EQL spells can be upgraded 0–10, and the game names an upgraded cast with a trailing
    /// rank ("Enthrall II" = Enthrall upgraded to level 2). The eqlwiki spell slider computes
    /// each stat as base × (1 + rate × level); the per-category rates below mirror the wiki's
    /// wgSpellLevelSliderRules. We use it to turn the base (level-0) wiki duration into the
    /// duration for the level actually cast, read straight from the log's rank numeral.
    /// </summary>
    public static class SpellScaling
    {
        public const int MaxLevel = 10;

        /// <summary>Per-upgrade-level rate for a spell category (fraction of base added per level).
        /// Duration: DoT/HoT +5%/level, Debuff/Charm-Mez/Buff +10%/level, Nuke/Heal none.</summary>
        public static double DurationRate(string category) => (category ?? "").ToLowerInvariant() switch
        {
            "dot" or "hot" => 0.05,
            "debuff" or "charm_mez" or "buff" => 0.10,
            _ => 0.0,   // nuke_lifetap, heal, unknown → duration doesn't scale
        };

        /// <summary>base × (1 + rate × level), clamped to level 0..MaxLevel. level ≤ 0 returns base unchanged.</summary>
        public static double Scale(double baseVal, double rate, int level)
        {
            if (level <= 0 || rate == 0) return baseVal;
            if (level > MaxLevel) level = MaxLevel;
            return baseVal * (1 + rate * level);
        }

        // trailing " <roman>" or " <number>" = the upgrade level ("Enthrall II" → 2). Uppercase Roman only,
        // matching BuffTracker.RxRank so base-name stripping and level parsing stay in agreement.
        private static readonly Regex RxRankSuffix = new(@"\s+([IVXLCDM]+|\d+)$", RegexOptions.Compiled);

        /// <summary>The upgrade level embedded in a cast name (0 if none). "Enthrall II" → 2, "Charm IV" → 4.</summary>
        public static int RankLevel(string spell)
        {
            if (string.IsNullOrEmpty(spell)) return 0;
            var m = RxRankSuffix.Match(spell);
            if (!m.Success) return 0;
            string tok = m.Groups[1].Value;
            if (int.TryParse(tok, out var n)) return n;
            return RomanToInt(tok);
        }

        private static int RomanToInt(string s)
        {
            var map = new Dictionary<char, int> { ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100, ['D'] = 500, ['M'] = 1000 };
            int total = 0, prev = 0;
            for (int i = s.Length - 1; i >= 0; i--)
            {
                if (!map.TryGetValue(s[i], out var v)) return 0;
                if (v < prev) total -= v; else { total += v; prev = v; }
            }
            return total;
        }

        /// <summary>Best-effort classification into the wiki's slider categories, from scraped fields.
        /// Only the duration split matters for the tracker (per-tick → 5%, other persistent → 10%).</summary>
        public static string DeriveCategory(string spellType, IReadOnlyList<string> slots, double durationSec, string wearsOff, string castOnOther)
        {
            bool ben = string.Equals(spellType, "Beneficial", StringComparison.OrdinalIgnoreCase);
            bool perTick = false;
            if (slots != null)
                foreach (var s in slots)
                    if (s != null && s.IndexOf("per tick", StringComparison.OrdinalIgnoreCase) >= 0) { perTick = true; break; }

            string blob = ((slots != null ? string.Join(" ", slots) : "") + " " + (wearsOff ?? "") + " " + (castOnOther ?? ""));
            bool charmMez = Regex.IsMatch(blob, "(?i)mesmeriz|\\bcharm|enthrall");
            bool hasDur = durationSec > 0;

            if (charmMez) return "charm_mez";
            if (perTick) return ben ? "hot" : "dot";
            if (!hasDur) return ben ? "heal" : "nuke_lifetap";
            return ben ? "buff" : "debuff";
        }
    }
}
