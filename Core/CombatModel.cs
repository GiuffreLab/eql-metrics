using System;
using System.Collections.Generic;
using System.Linq;

namespace EqlMetrics.Core
{
    public enum DamageKind { Melee, Nuke, Dot, Shield }

    /// <summary>Per-ability (spell or melee skill) rollup for a single combatant.</summary>
    public sealed class AbilityStat
    {
        public string Name = "";
        public DamageKind Kind;
        public long Total;
        public int Hits;
        public int Misses;     // flat "but miss!" (your accuracy roll failed)
        public int Avoided;    // the target dodged/parried/blocked/riposted your swing (their defense)
        public int Resisted;   // spell fully resisted by the target ("<t> resisted your <spell>!")
        public long Max;
        public int Crits;

        public int Attempts => Hits + Misses + Avoided;
        public double Avg => Hits > 0 ? (double)Total / Hits : 0;
        public double HitPct => Attempts > 0 ? 100.0 * Hits / Attempts : 0;
        public double MissPct => Attempts > 0 ? 100.0 * Misses / Attempts : 0;   // flat miss share of all swings
        public double AvoidPct => Attempts > 0 ? 100.0 * Avoided / Attempts : 0; // target-avoided share
        public double ResistPct => (Hits + Resisted) > 0 ? 100.0 * Resisted / (Hits + Resisted) : 0;   // of cast attempts, resisted
        public double CritPct => Hits > 0 ? 100.0 * Crits / Hits : 0;
        public string Key => Name + "|" + Kind;
    }

    /// <summary>Aggregated melee accuracy for a combatant (or the enemies hitting you).</summary>
    public struct AccuracyStat
    {
        public long Swings, Landed, FlatMiss, Avoided;
        public double HitPct => Swings > 0 ? 100.0 * Landed / Swings : 0;
        public double MissPct => Swings > 0 ? 100.0 * FlatMiss / Swings : 0;      // flat "but miss!"
        public double AvoidedPct => Swings > 0 ? 100.0 * Avoided / Swings : 0;    // dodge/parry/block/riposte by target
        public double MissedTotalPct => Swings > 0 ? 100.0 * (FlatMiss + Avoided) / Swings : 0;
    }

    /// <summary>Per-spell healing rollup for the player.</summary>
    public sealed class HealStat
    {
        public string Name = "";
        public long Effective;   // HP actually restored
        public long Potential;   // HP the heal could have restored (effective + overheal)
        public int Casts;
        public long Max;

        public double Avg => Casts > 0 ? (double)Effective / Casts : 0;
        public double OverhealPct => Potential > 0 ? 100.0 * (Potential - Effective) / Potential : 0;
    }

    /// <summary>One actor doing damage (you, a pet, a groupmate, etc.).</summary>
    public sealed class Combatant
    {
        public string Name = "";
        public bool IsPlayer;
        public bool IsPet;
        public long TotalDamage;
        public readonly Dictionary<string, AbilityStat> Abilities = new();
        public readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase); // who this actor damaged

        public void AddDamage(string ability, DamageKind kind, long dmg, bool crit = false)
        {
            string key = ability + "|" + kind;
            if (!Abilities.TryGetValue(key, out var a))
            {
                a = new AbilityStat { Name = ability, Kind = kind };
                Abilities[key] = a;
            }
            a.Total += dmg;
            a.Hits++;
            if (crit) a.Crits++;
            if (dmg > a.Max) a.Max = dmg;
            TotalDamage += dmg;
        }

        public void AddMiss(string ability, DamageKind kind)
        {
            string key = ability + "|" + kind;
            if (!Abilities.TryGetValue(key, out var a))
            {
                a = new AbilityStat { Name = ability, Kind = kind };
                Abilities[key] = a;
            }
            a.Misses++;
        }

        public void AddAvoided(string ability, DamageKind kind)
        {
            string key = ability + "|" + kind;
            if (!Abilities.TryGetValue(key, out var a))
            {
                a = new AbilityStat { Name = ability, Kind = kind };
                Abilities[key] = a;
            }
            a.Avoided++;
        }

        /// <summary>A spell was resisted. The resist line doesn't say the damage type, so match an existing
        /// entry by name (whatever kind it landed as); if the spell has never landed, record it as a Nuke.</summary>
        public void AddResisted(string ability)
        {
            foreach (var a in Abilities.Values)
                if (a.Name.Equals(ability, StringComparison.OrdinalIgnoreCase)) { a.Resisted++; return; }
            var na = new AbilityStat { Name = ability, Kind = DamageKind.Nuke };
            Abilities[na.Key] = na;
            na.Resisted++;
        }

        /// <summary>Total spells resisted by targets across this actor's abilities.</summary>
        public int SpellsResisted { get { int n = 0; foreach (var a in Abilities.Values) n += a.Resisted; return n; } }

        // ---- crit rate by category (crits / landed hits) ----
        private (long crits, long hits) CritSum(Func<DamageKind, bool> pick)
        {
            long c = 0, h = 0;
            foreach (var a in Abilities.Values) if (pick(a.Kind)) { c += a.Crits; h += a.Hits; }
            return (c, h);
        }
        public long MeleeCrits => CritSum(k => k == DamageKind.Melee).crits;
        public long MeleeHits => CritSum(k => k == DamageKind.Melee).hits;
        public long SpellCrits => CritSum(k => k == DamageKind.Nuke || k == DamageKind.Dot).crits;
        public long SpellHits => CritSum(k => k == DamageKind.Nuke || k == DamageKind.Dot).hits;
        public double MeleeCritPct => MeleeHits > 0 ? 100.0 * MeleeCrits / MeleeHits : 0;
        public double SpellCritPct => SpellHits > 0 ? 100.0 * SpellCrits / SpellHits : 0;

        /// <summary>Melee accuracy across this actor's melee abilities (spells don't "miss" the same way).</summary>
        public AccuracyStat MeleeAccuracy()
        {
            var acc = new AccuracyStat();
            foreach (var a in Abilities.Values)
                if (a.Kind == DamageKind.Melee) { acc.Landed += a.Hits; acc.FlatMiss += a.Misses; acc.Avoided += a.Avoided; }
            acc.Swings = acc.Landed + acc.FlatMiss + acc.Avoided;
            return acc;
        }

        public IEnumerable<AbilityStat> AbilitiesByDamage =>
            Abilities.Values.OrderByDescending(x => x.Total);
    }

    public sealed class LootEntry
    {
        public DateTime Time;
        public string Text = "";
        public bool IsMote;
        public bool IsCoin;
    }

    /// <summary>A single non-player heal (used to consolidate enemy healing).</summary>
    public struct HealEvent
    {
        public string Healer;
        public string Target;
        public long Eff;
    }
}
