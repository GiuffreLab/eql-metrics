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
        public int Misses;
        public long Max;
        public int Crits;

        public double Avg => Hits > 0 ? (double)Total / Hits : 0;
        public double MissPct => (Hits + Misses) > 0 ? 100.0 * Misses / (Hits + Misses) : 0;
        public double CritPct => Hits > 0 ? 100.0 * Crits / Hits : 0;
        public string Key => Name + "|" + Kind;
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
