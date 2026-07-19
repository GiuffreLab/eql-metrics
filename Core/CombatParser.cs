using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EqlMetrics.Core
{
    /// <summary>
    /// Aggregates an EverQuest log into live combat/session stats.
    /// Feed it raw log lines with Apply(); read a snapshot with Snapshot().
    /// All timing is derived from the log's own timestamps, so it works both
    /// live and when replaying an existing file.
    /// </summary>
    public sealed class SessionStats
    {
        // ----- configuration -----
        public string PlayerName = "You";
        public string PetName = "";               // set for pet classes; empty = no pet
        public double EncounterTimeoutSec = 10.0;  // idle gap that ends an encounter

        // ----- combat -----
        private readonly Dictionary<string, Combatant> _combatants = new(StringComparer.OrdinalIgnoreCase);

        // ----- session totals -----
        public double TotalPlat;
        public int MoteCount;
        public readonly Dictionary<string, int> MotesByTier = new(StringComparer.OrdinalIgnoreCase);
        public double TotalXpPct;
        public int Kills;
        public long DamageTaken;
        public long HealingDone;
        public string LastTarget = "";
        public readonly List<LootEntry> Loot = new();

        // ----- timing -----
        public DateTime? FirstTime;
        public DateTime LastTime;
        private DateTime _encStart;
        private DateTime _encLast;
        private bool _encActive;
        private readonly Dictionary<string, long> _encDamage = new(StringComparer.OrdinalIgnoreCase);

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Known melee verbs -> displayed skill name. Anchoring on this set lets us
        // correctly handle multi-word attacker/target names ("orc centurion").
        private static readonly Dictionary<string, string> MeleeVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hit"]="Hitting", ["slash"]="Slashing", ["pierce"]="Piercing", ["crush"]="Crushing",
            ["bash"]="Bash", ["kick"]="Kick", ["backstab"]="Backstab", ["bite"]="Bite",
            ["claw"]="Claw", ["gore"]="Gore", ["sting"]="Sting", ["smash"]="Crushing",
            ["slam"]="Slam", ["punch"]="Hand to Hand", ["maul"]="Maul", ["rend"]="Rend",
            ["slice"]="Slashing", ["cleave"]="Cleave", ["chomp"]="Chomp", ["frenzies on"]="Frenzy"
        };
        private static readonly string VerbAlt = BuildVerbAlt();

        private static string BuildVerbAlt()
        {
            // For each base verb, allow the third-person form too (slash|slashes ...).
            var forms = new List<string>();
            foreach (var v in MeleeVerbs.Keys)
            {
                forms.Add(Regex.Escape(v));
                if (v.EndsWith("h") || v.EndsWith("s") || v.EndsWith("ch")) forms.Add(Regex.Escape(v) + "es");
                else forms.Add(Regex.Escape(v) + "s");
            }
            // longest first so "frenzies on" wins over shorter partials
            return string.Join("|", forms.OrderByDescending(f => f.Length));
        }

        // ----- regexes -----
        private static readonly Regex RxPrefix = new(@"^\[(?<ts>[^\]]+)\]\s?(?<msg>.*)$", RegexOptions.Compiled);

        private static readonly Regex RxMelee = new(
            @"^(?<a>.+?) (?<verb>" + VerbAlt + @") (?<t>.+?) for (?<d>\d+) points? of damage\.$",
            RegexOptions.Compiled);

        private static readonly Regex RxMissThird = new(
            @"^(?<a>.+?) tries to (?<verb>[A-Za-z]+) (?<t>.+?), but misses!$", RegexOptions.Compiled);
        private static readonly Regex RxMissYou = new(
            @"^You try to (?<verb>[A-Za-z]+) (?<t>.+?), but miss!$", RegexOptions.Compiled);

        private static readonly Regex RxNuke = new(
            @"^(?<a>.+?) hits? (?<t>.+?) for (?<d>\d+) points? of (?<type>[A-Za-z]+) damage by (?<spell>.+?)\.$",
            RegexOptions.Compiled);

        private static readonly Regex RxDot = new(
            @"^(?<t>.+?) has taken (?<d>\d+) damage from (?<rest>.+)\.$", RegexOptions.Compiled);

        private static readonly Regex RxHeal = new(
            @"^You healed (?<t>.+?) for (?<amt>\d+)(?: \(\d+\))? hit points by (?<spell>.+?)\.$",
            RegexOptions.Compiled);

        private static readonly Regex RxLoot = new(
            @"^You (?:have )?looted an? (?<item>.+?) from .+?corpse", RegexOptions.Compiled);

        private static readonly Regex RxXp = new(
            @"^You gain (?:party )?experience!(?: \((?<pct>[\d.]+)%\))?", RegexOptions.Compiled);

        private static readonly Regex RxSlainBy = new(@"^.+? has been slain by .+?!$", RegexOptions.Compiled);
        private static readonly Regex RxYouSlain = new(@"^You have slain .+?!$", RegexOptions.Compiled);

        private static readonly Regex RxCoin = new(@"(\d+)\s+(platinum|gold|silver|copper)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public void Reset()
        {
            _combatants.Clear();
            _encDamage.Clear();
            TotalPlat = 0; MoteCount = 0; MotesByTier.Clear();
            TotalXpPct = 0; Kills = 0; DamageTaken = 0; HealingDone = 0;
            Loot.Clear();
            FirstTime = null; _encActive = false;
        }

        private bool IsPlayerToken(string s) =>
            s.Equals("You", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("YOU", StringComparison.Ordinal) ||
            s.Equals("Your", StringComparison.OrdinalIgnoreCase);

        private Combatant GetCombatant(string rawName)
        {
            string name = rawName;
            bool isPlayer = IsPlayerToken(rawName);
            if (isPlayer) name = PlayerName;
            if (!_combatants.TryGetValue(name, out var c))
            {
                c = new Combatant
                {
                    Name = name,
                    IsPlayer = isPlayer,
                    IsPet = !string.IsNullOrEmpty(PetName) && name.Equals(PetName, StringComparison.OrdinalIgnoreCase)
                };
                _combatants[name] = c;
            }
            return c;
        }

        public static bool TryParseTime(string ts, out DateTime dt)
        {
            // "Sun Jul 19 07:47:02 2026"  (day may be space-padded: "Jul  9")
            string norm = Regex.Replace(ts.Trim(), @"\s+", " ");
            return DateTime.TryParseExact(norm, "ddd MMM d HH:mm:ss yyyy", Inv,
                DateTimeStyles.AllowWhiteSpaces, out dt);
        }

        /// <summary>Feed one raw log line. Returns true if it advanced any stat.</summary>
        public bool Apply(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            var m = RxPrefix.Match(line);
            if (!m.Success) return false;
            if (!TryParseTime(m.Groups["ts"].Value, out var dt)) return false;
            string msg = m.Groups["msg"].Value;

            FirstTime ??= dt;
            if (dt > LastTime) LastTime = dt;

            // ---- melee ----
            var mm = RxMelee.Match(msg);
            if (mm.Success)
            {
                string a = mm.Groups["a"].Value, t = mm.Groups["t"].Value;
                long d = long.Parse(mm.Groups["d"].Value, Inv);
                string skill = SkillFor(mm.Groups["verb"].Value);
                if (IsPlayerToken(t)) { DamageTaken += d; return true; }   // incoming
                if (IsPlayerToken(a) || !IsPlayerToken(t))                  // outgoing / group / pet
                {
                    var c = GetCombatant(a);
                    c.AddDamage(skill, DamageKind.Melee, d);
                    Encounter(c.Name, d, dt);
                    if (c.IsPlayer) LastTarget = t;
                }
                return true;
            }

            // ---- misses (accuracy only) ----
            var my = RxMissYou.Match(msg);
            if (my.Success) { GetCombatant("You").AddMiss(SkillFor(my.Groups["verb"].Value), DamageKind.Melee); return true; }
            var mt = RxMissThird.Match(msg);
            if (mt.Success)
            {
                string a = mt.Groups["a"].Value;
                if (!IsPlayerToken(mt.Groups["t"].Value) && IsMeleeVerb(mt.Groups["verb"].Value))
                    GetCombatant(a).AddMiss(SkillFor(mt.Groups["verb"].Value), DamageKind.Melee);
                return true;
            }

            // ---- spell nuke ----
            var nu = RxNuke.Match(msg);
            if (nu.Success)
            {
                string a = nu.Groups["a"].Value, t = nu.Groups["t"].Value;
                long d = long.Parse(nu.Groups["d"].Value, Inv);
                string spell = nu.Groups["spell"].Value;
                if (IsPlayerToken(t)) { DamageTaken += d; return true; }
                var c = GetCombatant(a);
                c.AddDamage(spell, DamageKind.Nuke, d);
                Encounter(c.Name, d, dt);
                if (c.IsPlayer) LastTarget = t;
                return true;
            }

            // ---- damage-over-time ----
            var dm = RxDot.Match(msg);
            if (dm.Success)
            {
                long d = long.Parse(dm.Groups["d"].Value, Inv);
                string rest = dm.Groups["rest"].Value.Trim();
                string owner; string spell;
                if (rest.StartsWith("your ", StringComparison.OrdinalIgnoreCase))
                { owner = "You"; spell = rest.Substring(5); }
                else
                {
                    int by = rest.LastIndexOf(" by ", StringComparison.Ordinal);
                    if (by < 0) return true; // unknown owner
                    spell = rest.Substring(0, by);
                    owner = rest.Substring(by + 4);
                }
                var c = GetCombatant(owner);
                c.AddDamage(spell.Trim(), DamageKind.Dot, d);
                Encounter(c.Name, d, dt);
                return true;
            }

            // ---- heals (player) ----
            var hl = RxHeal.Match(msg);
            if (hl.Success) { HealingDone += long.Parse(hl.Groups["amt"].Value, Inv); return true; }

            // ---- currency ----
            if (msg.Contains("from the corpse") || msg.Contains("sold it for"))
                foreach (Match cm in RxCoin.Matches(msg))
                    TotalPlat += ToPlat(long.Parse(cm.Groups[1].Value, Inv), cm.Groups[2].Value);

            // ---- loot / motes ----
            var lt = RxLoot.Match(msg);
            if (lt.Success)
            {
                string item = lt.Groups["item"].Value.Trim();
                bool isMote = item.IndexOf("mote of potential", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isCoin = msg.Contains("sold it for");
                if (isMote)
                {
                    MoteCount++;
                    string tier = item.Split(' ')[0];
                    MotesByTier[tier] = MotesByTier.GetValueOrDefault(tier) + 1;
                }
                Loot.Add(new LootEntry { Time = dt, Text = item, IsMote = isMote, IsCoin = isCoin });
                if (Loot.Count > 100) Loot.RemoveAt(0);
                return true;
            }

            // ---- xp ----
            var xp = RxXp.Match(msg);
            if (xp.Success)
            {
                if (xp.Groups["pct"].Success)
                    TotalXpPct += double.Parse(xp.Groups["pct"].Value, Inv);
                return true;
            }

            // ---- kills ----
            if (RxSlainBy.IsMatch(msg) || RxYouSlain.IsMatch(msg)) { Kills++; return true; }

            return false;
        }

        // Map a melee verb (any conjugation) to its base form if we know it.
        private static bool IsMeleeVerb(string verb) => BaseVerb(verb) != null;

        private static string? BaseVerb(string verb)
        {
            string v = verb.ToLowerInvariant();
            if (MeleeVerbs.ContainsKey(v)) return v;
            if (v.EndsWith("s") && MeleeVerbs.ContainsKey(v[..^1])) return v[..^1];   // cleaves -> cleave, hits -> hit
            if (v.EndsWith("es") && MeleeVerbs.ContainsKey(v[..^2])) return v[..^2];  // slashes -> slash
            return null;
        }

        private static string SkillFor(string verb)
        {
            string? b = BaseVerb(verb);
            if (b != null) return MeleeVerbs[b];
            // unknown verb: strip a plural 's' and title-case it
            string v = verb.ToLowerInvariant();
            string s = v.EndsWith("es") ? v[..^2] : v.EndsWith("s") ? v[..^1] : v;
            return s.Length == 0 ? verb : char.ToUpper(s[0]) + s.Substring(1);
        }

        private static double ToPlat(long amt, string coin) => coin.ToLowerInvariant() switch
        {
            "platinum" => amt,
            "gold" => amt * 0.1,
            "silver" => amt * 0.01,
            "copper" => amt * 0.001,
            _ => 0
        };

        private void Encounter(string combatant, long dmg, DateTime dt)
        {
            if (!_encActive || (dt - _encLast).TotalSeconds > EncounterTimeoutSec)
            {
                _encActive = true;
                _encStart = dt;
                _encDamage.Clear();
            }
            _encLast = dt;
            _encDamage[combatant] = _encDamage.GetValueOrDefault(combatant) + dmg;
        }

        // ----- read side -----
        public bool EncounterActive => _encActive && (LastTime - _encLast).TotalSeconds <= EncounterTimeoutSec;

        public double SessionSeconds =>
            FirstTime.HasValue ? Math.Max(1, (LastTime - FirstTime.Value).TotalSeconds) : 1;
        public double SessionHours => Math.Max(1.0 / 3600, SessionSeconds / 3600.0);
        public double EncounterSeconds => _encActive ? Math.Max(1, (_encLast - _encStart).TotalSeconds) : 1;

        public IReadOnlyCollection<Combatant> Combatants => _combatants.Values;
        public Combatant? Player => _combatants.Values.FirstOrDefault(c => c.IsPlayer);
        public Combatant? Pet =>
            string.IsNullOrEmpty(PetName) ? null :
            _combatants.Values.FirstOrDefault(c => c.Name.Equals(PetName, StringComparison.OrdinalIgnoreCase));

        public double DpsSession(Combatant c) => c.TotalDamage / SessionSeconds;
        public double DpsEncounter(string name) =>
            _encDamage.TryGetValue(name, out var d) ? d / EncounterSeconds : 0;

        public double PlayerDps => Player is null ? 0 : DpsSession(Player);
        public double PetDps => Pet is null ? 0 : DpsSession(Pet);
        public double CombinedDps => PlayerDps + PetDps;
        public double Hps => HealingDone / SessionSeconds;
        public double CoinPerHour => TotalPlat / SessionHours;
        public double MotesPerHour => MoteCount / SessionHours;
        public double XpPerHour => TotalXpPct / SessionHours;
        public double KillsPerHour => Kills / SessionHours;
        public double DamageTakenPerHour => DamageTaken / SessionHours;
        public double? HoursToLevel => XpPerHour > 0 ? 100.0 / XpPerHour : (double?)null;
    }
}
