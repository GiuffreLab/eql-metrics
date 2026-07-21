using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EqlMetrics.Core
{
    public sealed class BuffDef
    {
        public string Spell = "";
        public string Apply = "";   // "cast on you" flavor line
        public string Fade = "";    // "wears off" flavor line
        public double DurationSec;  // 0 = unknown / permanent (no countdown)
    }

    /// <summary>A buff whose landing line names the TARGET (e.g. a pet buff: "&lt;pet&gt; goes berserk." for Burnout).
    /// The wiki's "cast on other" text uses "Someone" as the target placeholder; <see cref="Match"/> captures it as &lt;t&gt;.</summary>
    public sealed class OtherBuffDef
    {
        public string Spell = "";
        public BuffCat Category;    // Pet (target_type == "Pet")
        public double DurationSec;
        public Regex Match = null!; // built from cast_on_other: "Someone goes berserk." -> ^(?<t>.+?)\s+goes\s+berserk\.$
    }

    /// <summary>
    /// Self-buff message + duration table sourced from the EverQuest Legends wiki
    /// (eqlwiki.com). Self-buffs fade with spell-specific flavor text and no spell
    /// name, so this table maps that flavor back to the spell + duration.
    /// Only persistent beneficial self-buffs are listed (not nukes, heals, HoTs,
    /// debuffs, or utility). Debuffs/pet buffs are handled via "worn off" lines.
    /// </summary>
    public static class BuffData
    {
        public static readonly BuffDef[] Buffs =
        {
            new BuffDef { Spell = "Holy Armor",             Apply = "You feel the favor of the gods upon you.",            Fade = "You no longer feel blessed.",       DurationSec = 1620 },
            new BuffDef { Spell = "Strengthen",             Apply = "You feel stronger.",                                  Fade = "Your strength fades.",              DurationSec = 1620 },
            new BuffDef { Spell = "Center",                 Apply = "You feel magnanimous of spirit.",                     Fade = "Your sense of center fades.",       DurationSec = 1620 },
            new BuffDef { Spell = "Courage",                Apply = "You feel a rush of courage.",                         Fade = "You feel less courageous.",         DurationSec = 1620 },
            new BuffDef { Spell = "Skin like Wood",         Apply = "Your skin turns hard as wood.",                       Fade = "Your skin returns to normal.",      DurationSec = 1620 },
            new BuffDef { Spell = "Skin like Rock",         Apply = "Your skin turns hard as stone.",                      Fade = "Your skin returns to normal.",      DurationSec = 1620 },
            new BuffDef { Spell = "Quickness",              Apply = "You feel much faster.",                               Fade = "Your speed returns to normal.",     DurationSec = 660 },
            new BuffDef { Spell = "Spirit of Wolf",         Apply = "You feel the spirit of wolf enter you.",              Fade = "The spirit of wolf leaves you.",    DurationSec = 1620 },
            new BuffDef { Spell = "Lesser Shielding",       Apply = "You feel armored.",                                   Fade = "Your shielding fades.",             DurationSec = 0 },
            new BuffDef { Spell = "Yaulp",                  Apply = "You feel a surge of strength as you let forth a mighty yaulp.", Fade = "Your surge of strength fades.", DurationSec = 0 },
            new BuffDef { Spell = "Blessing of Piety",      Apply = "Your thoughts quicken as reverence fills your mind.",  Fade = "Your thoughts slow.",               DurationSec = 2400 },
            new BuffDef { Spell = "Breeze",                 Apply = "A light breeze slips through your mind.",              Fade = "The light breeze fades.",           DurationSec = 1626 },
            new BuffDef { Spell = "Intellectual Advancement",Apply = "Your mind sharpens.",                                Fade = "The intellectual advancement fades.",DurationSec = 1620 },
            new BuffDef { Spell = "See Invisible",          Apply = "Your eyes tingle.",                                   Fade = "Your eyes stop tingling.",          DurationSec = 1620 },
            new BuffDef { Spell = "Mist",                   Apply = "Your image blurs.",                                   Fade = "You come into focus.",              DurationSec = 1620 },
            new BuffDef { Spell = "Blessing of the Page",   Apply = "Your hands glow a dull gold.",                        Fade = "The golden glow fades.",            DurationSec = 1800 },
            new BuffDef { Spell = "Shield of Thistles",     Apply = "You are surrounded by a thorny barrier.",             Fade = "The brambles fall away.",           DurationSec = 54 },
        };

        // one apply/fade line may map to several spells (e.g. Quickness & Alacrity share text)
        public static readonly Dictionary<string, List<BuffDef>> ByApply = new(StringComparer.OrdinalIgnoreCase);
        public static readonly Dictionary<string, List<string>> FadeToSpells = new(StringComparer.OrdinalIgnoreCase);
        // pet/other-target buffs, matched by their target-naming landing line (populated from spells.json)
        public static readonly List<OtherBuffDef> OtherApply = new();
        public static void SetOtherApply(IEnumerable<OtherBuffDef> defs)
        {
            OtherApply.Clear();
            OtherApply.AddRange(defs);
        }
        // authoritative spell -> duration (seconds), base-name keyed, for ALL spells (pet/debuff too)
        public static readonly Dictionary<string, double> DurationBySpell = new(StringComparer.OrdinalIgnoreCase);

        static BuffData()
        {
            Rebuild(Buffs);
            foreach (var b in Buffs) if (b.DurationSec > 0) DurationBySpell[BuffTracker.BaseName(b.Spell)] = b.DurationSec;
        }

        /// <summary>Merge authoritative durations (from spells.json, all spells) keyed by base name.</summary>
        public static void AddDurations(IEnumerable<KeyValuePair<string, double>> pairs)
        {
            foreach (var kv in pairs)
                if (kv.Value > 0) DurationBySpell[BuffTracker.BaseName(kv.Key)] = kv.Value;
        }

        public static double? DurationFor(string spell) =>
            DurationBySpell.TryGetValue(BuffTracker.BaseName(spell), out var d) ? d : (double?)null;

        /// <summary>Replace the active self-buff table (e.g. from a scraped spells.json).</summary>
        public static void Rebuild(IEnumerable<BuffDef> defs)
        {
            ByApply.Clear();
            FadeToSpells.Clear();
            foreach (var b in defs)
            {
                if (string.IsNullOrEmpty(b.Apply)) continue;
                if (!ByApply.TryGetValue(b.Apply, out var al)) { al = new List<BuffDef>(); ByApply[b.Apply] = al; }
                if (!al.Exists(x => x.Spell.Equals(b.Spell, StringComparison.OrdinalIgnoreCase))) al.Add(b);
                if (b.Fade.Length > 0)
                {
                    if (!FadeToSpells.TryGetValue(b.Fade, out var list)) { list = new List<string>(); FadeToSpells[b.Fade] = list; }
                    if (!list.Contains(b.Spell)) list.Add(b.Spell);
                }
            }
        }
    }
}
