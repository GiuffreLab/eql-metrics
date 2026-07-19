using System;
using System.Collections.Generic;
using System.Linq;

namespace EqlMetrics.Core
{
    public enum DamageKind { Melee, Nuke, Dot }

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
        public string Key => Name + "|" + Kind;
    }

    /// <summary>One actor doing damage (you, a pet, a groupmate, etc.).</summary>
    public sealed class Combatant
    {
        public string Name = "";
        public bool IsPlayer;
        public bool IsPet;
        public long TotalDamage;
        public readonly Dictionary<string, AbilityStat> Abilities = new();

        public void AddDamage(string ability, DamageKind kind, long dmg)
        {
            string key = ability + "|" + kind;
            if (!Abilities.TryGetValue(key, out var a))
            {
                a = new AbilityStat { Name = ability, Kind = kind };
                Abilities[key] = a;
            }
            a.Total += dmg;
            a.Hits++;
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
}
