using System;
using System.Collections.Generic;
using System.Linq;

namespace EqlMetrics.Core
{
    /// <summary>
    /// A reusable rollup of combat events. Used both for the whole session and
    /// for each individual encounter, so the two never drift apart.
    /// Configuration (player/pet names) is read from the owning SessionStats.
    /// </summary>
    public sealed class CombatAggregate
    {
        private readonly SessionStats _o;
        public CombatAggregate(SessionStats owner) { _o = owner; }

        public readonly Dictionary<string, Combatant> Combatants = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> EnemyNames = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, HealStat> PlayerHeals = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<HealEvent> NpHeals = new();

        public long DamageTaken;      // to the player
        public long DamageTakenPet;   // to the pet
        public long HealingDone;      // by the player
        public long BiggestHitTaken;
        public string BiggestHitTakenFrom = "";

        // ---- incoming-melee avoidance (survivability). Mitigation/AC is NOT derivable (log shows only final dmg). ----
        public long MeleeSwingsLanded;                       // melee hits that landed on the player
        public long Dodged, Parried, Blocked, Riposted;      // the player actively avoided a swing
        public long IncomingMissed;                          // the mob simply missed
        public long StunsTaken;

        public void AvoidedByYou(string how, DateTime t)
        {
            Touch(t);
            switch (how)
            {
                case "dodge": Dodged++; break;
                case "parry": Parried++; break;
                case "block": Blocked++; break;
                case "riposte": Riposted++; break;
            }
        }
        public void IncomingMiss(DateTime t) { Touch(t); IncomingMissed++; }
        public void Stunned(DateTime t) { Touch(t); StunsTaken++; }

        public long ActiveAvoids => Dodged + Parried + Blocked + Riposted;
        public long SwingsAtYou => MeleeSwingsLanded + ActiveAvoids + IncomingMissed;
        public long AvoidedTotal => ActiveAvoids + IncomingMissed;   // every swing that didn't land
        public double AvoidedPct => SwingsAtYou > 0 ? 100.0 * AvoidedTotal / SwingsAtYou : 0;
        public double ActiveAvoidPct => SwingsAtYou > 0 ? 100.0 * ActiveAvoids / SwingsAtYou : 0;
        public double DamageTakenPerHour => DamageTaken / Hours;

        public DateTime? FirstTime;
        public DateTime LastTime;
        public string PrimaryEnemy = "";

        private void Touch(DateTime t) { FirstTime ??= t; if (t > LastTime) LastTime = t; }

        /// <summary>Advance this aggregate's clock (used by the session aggregate on every line).</summary>
        public void MarkTime(DateTime t) => Touch(t);

        public void Miss(string attacker, string ability, DamageKind kind)
        {
            GetC(attacker).AddMiss(ability, kind);
        }

        private Combatant GetC(string rawName)
        {
            string name = rawName;
            bool isPlayer = _o.IsPlayerToken(rawName);
            if (isPlayer) name = _o.PlayerName;
            if (!Combatants.TryGetValue(name, out var c))
            {
                c = new Combatant
                {
                    Name = name,
                    IsPlayer = isPlayer,
                    IsPet = _o.IsPetToken(name)
                };
                Combatants[name] = c;
            }
            return c;
        }

        public void Outgoing(string attacker, string ability, DamageKind kind, long dmg, bool crit, string target, DateTime t)
        {
            Touch(t);
            var c = GetC(attacker);
            c.AddDamage(ability, kind, dmg, crit);
            c.Targets.Add(target);
            if (c.IsPlayer) AddEnemy(target);
        }

        public void IncomingMe(long dmg, string attacker, string ability, DamageKind kind, DateTime t)
        {
            Touch(t);
            DamageTaken += dmg;
            if (kind == DamageKind.Melee) MeleeSwingsLanded++;   // a landed swing (for avoidance rate)
            if (dmg > BiggestHitTaken) { BiggestHitTaken = dmg; BiggestHitTakenFrom = attacker; }
            AttributeEnemy(attacker, ability, kind, dmg, _o.PlayerName);
        }

        public void IncomingPet(long dmg, string attacker, string ability, DamageKind kind, DateTime t)
        {
            Touch(t);
            DamageTakenPet += dmg;
            AttributeEnemy(attacker, ability, kind, dmg, _o.PetName);
        }

        // record an enemy's attack so its per-attack breakdown / damage-to-party is available
        private void AttributeEnemy(string attacker, string ability, DamageKind kind, long dmg, string victim)
        {
            AddEnemy(attacker);
            if (string.IsNullOrEmpty(attacker)) return;
            var c = GetC(attacker);
            c.AddDamage(ability, kind, dmg, false);
            if (!string.IsNullOrEmpty(victim)) c.Targets.Add(victim);
        }

        public void PlayerHeal(string spell, long eff, long pot, DateTime t)
        {
            Touch(t);
            if (!PlayerHeals.TryGetValue(spell, out var hs)) { hs = new HealStat { Name = spell }; PlayerHeals[spell] = hs; }
            hs.Effective += eff; hs.Potential += pot; hs.Casts++;
            if (eff > hs.Max) hs.Max = eff;
            HealingDone += eff;
        }

        public void NpHeal(string healer, string target, long eff, DateTime t)
        {
            Touch(t);
            NpHeals.Add(new HealEvent { Healer = healer, Target = target, Eff = eff });
        }

        public void AddEnemy(string name)
        {
            if (EnemyNames.Add(name) && PrimaryEnemy.Length == 0) PrimaryEnemy = name;
        }

        // ---------- reads ----------
        public double Seconds => FirstTime.HasValue ? Math.Max(1, (LastTime - FirstTime.Value).TotalSeconds) : 1;
        public double Hours => Math.Max(1.0 / 3600, Seconds / 3600.0);

        public Combatant? Player => Combatants.Values.FirstOrDefault(c => c.IsPlayer);
        public Combatant? Pet => string.IsNullOrEmpty(_o.PetName) ? null :
            Combatants.Values.FirstOrDefault(c => c.Name.Equals(_o.PetName, StringComparison.OrdinalIgnoreCase));
        public bool HasPet => Pet != null;

        public bool IsFriendly(Combatant c) => c.IsPlayer || c.IsPet || c.Targets.Any(EnemyNames.Contains);
        public IEnumerable<Combatant> Friendlies => Combatants.Values.Where(IsFriendly);
        public IEnumerable<Combatant> Enemies => Combatants.Values.Where(c => !IsFriendly(c));

        public double DpsOf(Combatant c) => c.TotalDamage / Seconds;
        public double PlayerDps => Player is null ? 0 : DpsOf(Player);
        public double PetDps => Pet is null ? 0 : DpsOf(Pet);
        public double CombinedDps => PlayerDps + PetDps;
        public double Hps => HealingDone / Seconds;
        public double IncomingDpsMe => DamageTaken / Seconds;
        public double IncomingDpsPet => DamageTakenPet / Seconds;

        public long EnemyHealing => NpHeals
            .Where(h => EnemyNames.Contains(h.Healer) || EnemyNames.Contains(h.Target))
            .Sum(h => h.Eff);
        public double EnemyHps => EnemyHealing / Seconds;

        public IEnumerable<HealStat> HealsByAmount => PlayerHeals.Values.OrderByDescending(h => h.Effective);
        public long TotalHealPotential => PlayerHeals.Values.Sum(h => h.Potential);
        public double OverhealPct => TotalHealPotential > 0 ? 100.0 * (TotalHealPotential - HealingDone) / TotalHealPotential : 0;

        // biggest single events by the player
        private IEnumerable<AbilityStat> PlayerAbilities => Player?.Abilities.Values ?? Enumerable.Empty<AbilityStat>();
        public AbilityStat? BiggestMelee => PlayerAbilities.Where(a => a.Kind == DamageKind.Melee).OrderByDescending(a => a.Max).FirstOrDefault();
        public AbilityStat? BiggestSpell => PlayerAbilities.Where(a => a.Kind != DamageKind.Melee).OrderByDescending(a => a.Max).FirstOrDefault();
        public HealStat? BiggestHeal => PlayerHeals.Values.OrderByDescending(h => h.Max).FirstOrDefault();

        // highest crit hit by the player (single ability with the largest crit-capable max)
        public AbilityStat? TopCritAbility => PlayerAbilities.Where(a => a.Crits > 0).OrderByDescending(a => a.Max).FirstOrDefault();

        public string EnemyTitle => PrimaryEnemy.Length == 0 ? "" :
            PrimaryEnemy + (EnemyNames.Count > 1 ? $"  +{EnemyNames.Count - 1}" : "");
    }

    /// <summary>One combat encounter (a fight), delimited by an idle gap.</summary>
    public sealed class Encounter
    {
        public readonly CombatAggregate Agg;
        public DateTime Start;
        public Encounter(SessionStats owner) { Agg = new CombatAggregate(owner); }

        public DateTime End => Agg.LastTime;
        public double Seconds => Agg.Seconds;
        public string Title => Agg.EnemyTitle.Length > 0 ? Agg.EnemyTitle : "combat";

        // per-second timeline buckets
        public readonly List<long> DpsBuckets = new();  // player+pet outgoing / sec
        public readonly List<long> InBuckets = new();   // incoming to player+pet / sec

        public void Bucket(List<long> b, int sec, long v)
        {
            if (sec < 0) sec = 0;
            while (b.Count <= sec) b.Add(0);
            b[sec] += v;
        }
    }
}
