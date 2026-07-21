using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EqlMetrics.Core
{
    public enum StealthKind { Hide, Sneak, HideFail, SneakFail }

    /// <summary>A hide/sneak skill-check result, for the transient center-screen flash.</summary>
    public struct StealthEvent { public StealthKind Kind; public DateTime Time; }

    /// <summary>A "notable" skill attempt (e.g. Backstab) by the player — hit, crit, or miss — for the skill-proc flash.</summary>
    public struct SkillProc { public string Skill; public long Damage; public bool Crit; public bool Miss; public string Target; public DateTime Time; }

    /// <summary>One landed AoE-capable melee hit (Cleave, or Kick=round kick) by the player or pet. A burst
    /// of these across 2+ targets in the same window is the AoE ability firing (single-target = a normal swing).</summary>
    public struct CleaveHit { public string Actor; public string Skill; public long Damage; public bool Crit; public string Target; public DateTime Time; }

    /// <summary>
    /// Parses an EverQuest log into live combat/session stats plus a rolling
    /// history of encounters (fights). Feed raw lines with Apply().
    /// Timing comes from the log's own timestamps, so it works live or on replay.
    /// </summary>
    public sealed class SessionStats
    {
        public string PlayerName = "You";
        public string PetName = "";
        public double EncounterTimeoutSec = 10.0;
        public const int MaxEncounters = 20;

        public CombatAggregate Session { get; private set; }
        public BuffTracker Buffs { get; } = new();
        private Encounter? _cur;
        public readonly List<Encounter> Encounters = new();   // finalized, oldest first

        // session-only counters (not part of combat aggregate)
        public double TotalPlat;
        public int MoteCount;
        public readonly Dictionary<string, int> MotesByTier = new(StringComparer.OrdinalIgnoreCase);
        public double TotalXpPct;
        public int Kills;
        public int AbilityPoints;
        public readonly List<LootEntry> Loot = new();
        public readonly List<StealthEvent> StealthEvents = new();   // hide/sneak skill-check results
        public DateTime? QuickBuffCastAt;                            // last "You activate Quick Buff." (10-min cooldown)
        public const double QuickBuffCooldownSec = 600;

        public readonly List<SkillProc> SkillProcs = new();         // landed notable skills (backstab, ...)
        public readonly List<CleaveHit> CleaveHits = new();         // player/pet AoE-melee hits (grouped into AoE flashes)
        public readonly List<DateTime> MendEvents = new();          // monk Mend self-heals (log gives no amount)
        // skills that get their own proc flash; add class skills here as their log text is learned
        private static readonly HashSet<string> NotableSkills = new(StringComparer.OrdinalIgnoreCase) { "Backstab" };
        // melee verbs whose hits we collect into per-activation "burst" flashes. Cleave is AoE-only
        // (single = normal swing, silent); Kick/Strike are monk specials shown every activation. The UI
        // (FinalizeCleave) decides which, and folds in double/triple attacks and multi-target splashes.
        private static readonly HashSet<string> BurstSkills = new(StringComparer.OrdinalIgnoreCase) { "Cleave", "Kick", "Strike" };

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public void RecordStealth(StealthKind kind, DateTime dt)
        {
            StealthEvents.Add(new StealthEvent { Kind = kind, Time = dt });
            if (StealthEvents.Count > 50) StealthEvents.RemoveAt(0);
        }

        public void RecordSkillProc(string skill, long dmg, bool crit, string target, DateTime dt, bool miss = false)
        {
            SkillProcs.Add(new SkillProc { Skill = skill, Damage = dmg, Crit = crit, Miss = miss, Target = target ?? "", Time = dt });
            if (SkillProcs.Count > 50) SkillProcs.RemoveAt(0);
        }

        public void RecordCleaveHit(string actor, string skill, long dmg, bool crit, string target, DateTime dt)
        {
            CleaveHits.Add(new CleaveHit { Actor = actor, Skill = skill, Damage = dmg, Crit = crit, Target = target ?? "", Time = dt });
            if (CleaveHits.Count > 100) CleaveHits.RemoveAt(0);
        }

        public void RecordMend(DateTime dt)
        {
            MendEvents.Add(dt);
            if (MendEvents.Count > 50) MendEvents.RemoveAt(0);
        }

        public SessionStats() { Session = new CombatAggregate(this); }

        public bool IsPlayerToken(string s) =>
            s.Equals("You", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("YOU", StringComparison.Ordinal) ||
            s.Equals("Your", StringComparison.OrdinalIgnoreCase);

        public bool IsPetToken(string s) =>
            !string.IsNullOrEmpty(PetName) && s.Equals(PetName, StringComparison.OrdinalIgnoreCase);

        // ---------- melee verbs ----------
        private static readonly Dictionary<string, string> MeleeVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hit"]="Hitting", ["slash"]="Slashing", ["pierce"]="Piercing", ["crush"]="Crushing",
            ["bash"]="Bash", ["kick"]="Kick", ["backstab"]="Backstab", ["bite"]="Bite",
            ["claw"]="Claw", ["gore"]="Gore", ["sting"]="Sting", ["smash"]="Crushing",
            ["slam"]="Slam", ["punch"]="Hand to Hand", ["maul"]="Maul", ["rend"]="Rend",
            ["slice"]="Slashing", ["cleave"]="Cleave", ["chomp"]="Chomp", ["frenzies on"]="Frenzy",
            ["strike"]="Strike"   // monk Tiger Claw / Eagle Strike / Dragon Punch all log as "strike"
        };
        private static readonly string VerbAlt = BuildVerbAlt();
        private static string BuildVerbAlt()
        {
            var forms = new List<string>();
            foreach (var v in MeleeVerbs.Keys)
            {
                forms.Add(Regex.Escape(v));
                if (v.EndsWith("h") || v.EndsWith("s") || v.EndsWith("ch")) forms.Add(Regex.Escape(v) + "es");
                else forms.Add(Regex.Escape(v) + "s");
            }
            return string.Join("|", forms.OrderByDescending(f => f.Length));
        }

        // ---------- regexes ----------
        private const string Mod = @"(?:\s\((?<mod>[A-Za-z ]+)\))?";
        private static readonly Regex RxPrefix = new(@"^\[(?<ts>[^\]]+)\]\s?(?<msg>.*)$", RegexOptions.Compiled);
        private static readonly Regex RxMelee = new(@"^(?<a>.+?) (?<verb>" + VerbAlt + @") (?<t>.+?) for (?<d>\d+) points? of damage\." + Mod + "$", RegexOptions.Compiled);
        private static readonly Regex RxMissThird = new(@"^(?<a>.+?) tries to (?<verb>[A-Za-z]+) (?<t>.+?), but misses!$", RegexOptions.Compiled);
        private static readonly Regex RxMissYou = new(@"^You try to (?<verb>[A-Za-z]+) (?<t>.+?), but miss!$", RegexOptions.Compiled);
        // you actively avoid an incoming swing: "<mob> tries to <verb> YOU, but YOU dodge!"
        private static readonly Regex RxAvoidYou = new(@"^.+? tries to [A-Za-z]+ YOU, but YOU (?<how>dodge|parry|block|riposte)!$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RxNuke = new(@"^(?<a>.+?) hits? (?<t>.+?) for (?<d>\d+) points? of (?<type>[A-Za-z]+) damage by (?<spell>.+?)\." + Mod + "$", RegexOptions.Compiled);
        private static readonly Regex RxDot = new(@"^(?<t>.+?) has taken (?<d>\d+) damage from (?<rest>.+?)\." + Mod + "$", RegexOptions.Compiled);
        private static readonly Regex RxHeal = new(@"^(?<healer>.+?) healed (?<t>.+?)(?: over time)? for (?<amt>\d+)(?: \((?<pot>\d+)\))? hit points by (?<spell>.+?)\.$", RegexOptions.Compiled);
        private static readonly Regex RxLoot = new(@"^You (?:have )?looted an? (?<item>.+?) from .+?corpse", RegexOptions.Compiled);
        private static readonly Regex RxMote = new(@"Mote of (?<tier>.+?) Potential", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RxXp = new(@"^You gain (?:party )?experience!(?: \((?<pct>[\d.]+)%\))?", RegexOptions.Compiled);
        private static readonly Regex RxSlainBy = new(@"^.+? has been slain by .+?!$", RegexOptions.Compiled);
        private static readonly Regex RxYouSlain = new(@"^You have slain .+?!$", RegexOptions.Compiled);
        private static readonly Regex RxAa = new(@"^You have gained (?:an|(?<n>\d+)) ability points?!", RegexOptions.Compiled);
        private static readonly Regex RxCoin = new(@"(\d+)\s+(platinum|gold|silver|copper)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ---- buffs / debuffs ----
        private static readonly Regex RxCast = new(@"^You begin casting (?<spell>.+?)\.$", RegexOptions.Compiled);
        private static readonly Regex RxFizzle = new(@"^Your (?<spell>.+?) spell fizzles\.", RegexOptions.Compiled);
        private static readonly Regex RxInterrupt = new(@"^Your (?<spell>.+?) spell is interrupted\.", RegexOptions.Compiled);
        private static readonly Regex RxForget = new(@"^You forget (?<spell>.+?)\.$", RegexOptions.Compiled);
        private static readonly Regex RxResist = new(@"^Your target resisted the (?<spell>.+?) spell\.", RegexOptions.Compiled);
        private static readonly Regex RxWornPet = new(@"^Your pet's (?<spell>.+?) spell has worn off\.$", RegexOptions.Compiled);
        private static readonly Regex RxWorn = new(@"^Your (?<spell>.+?) spell has worn off(?: of (?<tgt>.+?))?\.$", RegexOptions.Compiled);
        private static readonly Regex RxMemorize = new(@"^You have finished memorizing (?<spell>.+?)\.$", RegexOptions.Compiled);

        public void Reset()
        {
            Session = new CombatAggregate(this);
            Buffs.ResetActive();
            _cur = null;
            Encounters.Clear();
            TotalPlat = 0; MoteCount = 0; MotesByTier.Clear();
            TotalXpPct = 0; Kills = 0; AbilityPoints = 0;
            Loot.Clear();
            StealthEvents.Clear();
            SkillProcs.Clear();
            CleaveHits.Clear();
            MendEvents.Clear();
            QuickBuffCastAt = null;
        }

        public static bool TryParseTime(string ts, out DateTime dt)
        {
            string norm = Regex.Replace(ts.Trim(), @"\s+", " ");
            return DateTime.TryParseExact(norm, "ddd MMM d HH:mm:ss yyyy", Inv, DateTimeStyles.AllowWhiteSpaces, out dt);
        }

        // ---------- encounter management ----------
        private Encounter Ensure(DateTime t)
        {
            if (_cur == null || (t - _cur.Agg.LastTime).TotalSeconds > EncounterTimeoutSec)
            {
                FinalizeCurrent();
                _cur = new Encounter(this) { Start = t };
            }
            return _cur;
        }
        private void FinalizeCurrent()
        {
            if (_cur != null && _cur.Agg.FirstTime != null)
            {
                Encounters.Add(_cur);
                while (Encounters.Count > MaxEncounters) Encounters.RemoveAt(0);
            }
            _cur = null;
        }
        private static int Sec(Encounter e, DateTime t) => (int)Math.Floor((t - e.Start).TotalSeconds);

        // ---------- routing ----------
        private void RouteOutgoing(string attacker, string ability, DamageKind kind, long dmg, bool crit, string target, DateTime dt)
        {
            Session.Outgoing(attacker, ability, kind, dmg, crit, target, dt);
            var e = Ensure(dt);
            e.Agg.Outgoing(attacker, ability, kind, dmg, crit, target, dt);
            if (IsPlayerToken(attacker) || IsPetToken(attacker)) e.Bucket(e.DpsBuckets, Sec(e, dt), dmg);
        }
        private void RouteIncomingMe(long dmg, string attacker, string ability, DamageKind kind, DateTime dt)
        {
            Session.IncomingMe(dmg, attacker, ability, kind, dt);
            var e = Ensure(dt);
            e.Agg.IncomingMe(dmg, attacker, ability, kind, dt);
            e.Bucket(e.InBuckets, Sec(e, dt), dmg);
        }
        private void RouteIncomingPet(long dmg, string attacker, string ability, DamageKind kind, DateTime dt)
        {
            Session.IncomingPet(dmg, attacker, ability, kind, dt);
            var e = Ensure(dt);
            e.Agg.IncomingPet(dmg, attacker, ability, kind, dt);
            e.Bucket(e.InBuckets, Sec(e, dt), dmg);
        }
        private void RoutePlayerHeal(string spell, long eff, long pot, DateTime dt)
        {
            Session.PlayerHeal(spell, eff, pot, dt);
            if (_cur != null && (dt - _cur.Agg.LastTime).TotalSeconds <= EncounterTimeoutSec)
                _cur.Agg.PlayerHeal(spell, eff, pot, dt);
        }
        private void RouteNpHeal(string healer, string target, long eff, DateTime dt)
        {
            Session.NpHeal(healer, target, eff, dt);
            if (_cur != null && (dt - _cur.Agg.LastTime).TotalSeconds <= EncounterTimeoutSec)
                _cur.Agg.NpHeal(healer, target, eff, dt);
        }
        private void RouteMiss(string attacker, string skill)
        {
            Session.Miss(attacker, skill, DamageKind.Melee);
            _cur?.Agg.Miss(attacker, skill, DamageKind.Melee);
        }

        // ---- incoming-melee avoidance (survivability), routed to session + current encounter ----
        private void RouteAvoidByYou(string how, DateTime dt)
        {
            Session.AvoidedByYou(how, dt);
            Ensure(dt).Agg.AvoidedByYou(how, dt);
        }
        private void RouteIncomingMiss(DateTime dt)
        {
            Session.IncomingMiss(dt);
            Ensure(dt).Agg.IncomingMiss(dt);
        }
        private void RouteStun(DateTime dt)
        {
            Session.Stunned(dt);
            Ensure(dt).Agg.Stunned(dt);
        }

        // ---------- main entry ----------
        public bool Apply(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            var m = RxPrefix.Match(line);
            if (!m.Success) return false;
            if (!TryParseTime(m.Groups["ts"].Value, out var dt)) return false;
            string msg = m.Groups["msg"].Value.Trim();
            if (msg.StartsWith("--")) msg = msg.Substring(2);
            if (msg.EndsWith("--")) msg = msg.Substring(0, msg.Length - 2);
            msg = msg.Trim();

            Session.MarkTime(dt);   // session clock spans all lines

            // ---- stealth skill checks (hide / sneak, success + failure) ----
            if (msg == "You have hidden yourself from view.") { RecordStealth(StealthKind.Hide, dt); return true; }
            if (msg == "You are as quiet as a cat stalking its prey.") { RecordStealth(StealthKind.Sneak, dt); return true; }
            if (msg == "You failed to hide yourself.") { RecordStealth(StealthKind.HideFail, dt); return true; }
            if (msg == "You are as quiet as a herd of running elephants.") { RecordStealth(StealthKind.SneakFail, dt); return true; }

            // ---- Quick Buff activation (no "ready" line exists, so we time the 10-min cooldown ourselves) ----
            if (msg == "You activate Quick Buff.") { QuickBuffCastAt = dt; return true; }

            // ---- Mend (monk self-heal; the log gives no amount, just that it fired) ----
            if (msg == "You mend your wounds and heal some damage.") { RecordMend(dt); return true; }

            // ---- incoming-melee avoidance (you dodged/parried/blocked/riposted a swing) + stuns ----
            var av = RxAvoidYou.Match(msg);
            if (av.Success) { RouteAvoidByYou(av.Groups["how"].Value.ToLowerInvariant(), dt); return true; }
            if (msg == "You are stunned!") { RouteStun(dt); return true; }

            // ---- melee ----
            var mm = RxMelee.Match(msg);
            if (mm.Success)
            {
                string a = mm.Groups["a"].Value, t = mm.Groups["t"].Value;
                long d = long.Parse(mm.Groups["d"].Value, Inv);
                bool crit = mm.Groups["mod"].Value.Equals("Critical", StringComparison.OrdinalIgnoreCase);
                string skill = SkillFor(mm.Groups["verb"].Value);
                if (IsPlayerToken(t)) RouteIncomingMe(d, a, skill, DamageKind.Melee, dt);
                else if (IsPetToken(t)) RouteIncomingPet(d, a, skill, DamageKind.Melee, dt);
                else RouteOutgoing(a, skill, DamageKind.Melee, d, crit, t, dt);
                if (IsPlayerToken(a) && NotableSkills.Contains(skill)) RecordSkillProc(skill, d, crit, t, dt);
                // cleave / kick / strike hits by you or your pet feed the per-activation burst flash (grouped in the UI)
                if (BurstSkills.Contains(skill) && (IsPlayerToken(a) || IsPetToken(a))) RecordCleaveHit(a, skill, d, crit, t, dt);
                return true;
            }

            // ---- misses ----
            var my = RxMissYou.Match(msg);
            if (my.Success)
            {
                string mskill = SkillFor(my.Groups["verb"].Value);
                RouteMiss("You", mskill);
                if (NotableSkills.Contains(mskill)) RecordSkillProc(mskill, 0, false, my.Groups["t"].Value, dt, miss: true);
                return true;
            }
            var mt = RxMissThird.Match(msg);
            if (mt.Success)
            {
                if (IsMeleeVerb(mt.Groups["verb"].Value))
                {
                    if (IsPlayerToken(mt.Groups["t"].Value)) RouteIncomingMiss(dt);   // a swing at you that whiffed (avoidance)
                    else RouteMiss(mt.Groups["a"].Value, SkillFor(mt.Groups["verb"].Value));
                }
                return true;
            }

            // ---- spell nuke ----
            var nu = RxNuke.Match(msg);
            if (nu.Success)
            {
                string a = nu.Groups["a"].Value, t = nu.Groups["t"].Value;
                long d = long.Parse(nu.Groups["d"].Value, Inv);
                bool crit = nu.Groups["mod"].Value.Equals("Critical", StringComparison.OrdinalIgnoreCase);
                string spell = nu.Groups["spell"].Value;
                if (IsPlayerToken(t)) RouteIncomingMe(d, a, spell, DamageKind.Nuke, dt);
                else if (IsPetToken(t)) RouteIncomingPet(d, a, spell, DamageKind.Nuke, dt);
                else RouteOutgoing(a, spell, DamageKind.Nuke, d, crit, t, dt);
                return true;
            }

            // ---- damage over time ----
            var dm = RxDot.Match(msg);
            if (dm.Success)
            {
                long d = long.Parse(dm.Groups["d"].Value, Inv);
                string rest = dm.Groups["rest"].Value.Trim();
                string owner, spell;
                if (rest.StartsWith("your ", StringComparison.OrdinalIgnoreCase)) { owner = "You"; spell = rest.Substring(5); }
                else
                {
                    int by = rest.LastIndexOf(" by ", StringComparison.Ordinal);
                    if (by < 0) return true;
                    spell = rest.Substring(0, by);
                    owner = rest.Substring(by + 4);
                }
                string dtgt = dm.Groups["t"].Value;
                if (IsPlayerToken(dtgt)) RouteIncomingMe(d, IsPlayerToken(owner) ? "" : owner, spell.Trim(), DamageKind.Dot, dt);
                else if (IsPetToken(dtgt)) RouteIncomingPet(d, IsPlayerToken(owner) ? "" : owner, spell.Trim(), DamageKind.Dot, dt);
                else RouteOutgoing(owner, spell.Trim(), DamageKind.Dot, d, false, dtgt, dt);
                return true;
            }

            // ---- heals ----
            var hl = RxHeal.Match(msg);
            if (hl.Success)
            {
                string healer = hl.Groups["healer"].Value.Trim();
                long eff = long.Parse(hl.Groups["amt"].Value, Inv);
                long pot = hl.Groups["pot"].Success ? long.Parse(hl.Groups["pot"].Value, Inv) : eff;
                if (IsPlayerToken(healer)) RoutePlayerHeal(hl.Groups["spell"].Value.Trim(), eff, pot, dt);
                else RouteNpHeal(healer, hl.Groups["t"].Value.Trim(), eff, dt);
                return true;
            }

            // ---- buffs / debuffs ----
            var bc = RxCast.Match(msg);
            if (bc.Success) { Buffs.BeginCast(bc.Groups["spell"].Value.Trim(), dt); return true; }
            var wp = RxWornPet.Match(msg);
            if (wp.Success) { Buffs.WornOff(wp.Groups["spell"].Value.Trim(), BuffCat.Pet, "", dt); return true; }
            var wo = RxWorn.Match(msg);
            if (wo.Success)
            {
                string tgt = wo.Groups["tgt"].Success ? wo.Groups["tgt"].Value.Trim() : "";
                Buffs.WornOff(wo.Groups["spell"].Value.Trim(), tgt.Length > 0 ? BuffCat.Debuff : BuffCat.Self, tgt, dt);
                return true;
            }
            var fz = RxFizzle.Match(msg); if (fz.Success) { Buffs.Fail(fz.Groups["spell"].Value.Trim()); return true; }
            var it = RxInterrupt.Match(msg); if (it.Success) { Buffs.Fail(it.Groups["spell"].Value.Trim()); return true; }
            var rs = RxResist.Match(msg); if (rs.Success) { Buffs.Fail(rs.Groups["spell"].Value.Trim()); return true; }
            var mem = RxMemorize.Match(msg); if (mem.Success) { Buffs.Memorize(mem.Groups["spell"].Value.Trim()); return true; }
            var fg = RxForget.Match(msg); if (fg.Success) { Buffs.Forget(fg.Groups["spell"].Value.Trim()); return true; }

            // ---- currency ----
            if (msg.Contains("from the corpse") || msg.Contains("sold it for"))
                foreach (Match cm in RxCoin.Matches(msg))
                    TotalPlat += ToPlat(long.Parse(cm.Groups[1].Value, Inv), cm.Groups[2].Value);

            // ---- loot / motes ----
            var lt = RxLoot.Match(msg);
            if (lt.Success)
            {
                string item = lt.Groups["item"].Value.Trim();
                var mote = RxMote.Match(item);
                bool isMote = mote.Success;
                bool isCoin = msg.Contains("sold it for");
                if (isMote)
                {
                    MoteCount++;
                    string tier = mote.Groups["tier"].Value.Trim();
                    MotesByTier[tier] = MotesByTier.GetValueOrDefault(tier) + 1;
                }
                Loot.Add(new LootEntry { Time = dt, Text = item, IsMote = isMote, IsCoin = isCoin });
                if (Loot.Count > 200) Loot.RemoveAt(0);
                return true;
            }

            // ---- xp ----
            var xp = RxXp.Match(msg);
            if (xp.Success)
            {
                if (xp.Groups["pct"].Success) TotalXpPct += double.Parse(xp.Groups["pct"].Value, Inv);
                return true;
            }

            // ---- kills ----
            if (RxSlainBy.IsMatch(msg) || RxYouSlain.IsMatch(msg)) { Kills++; return true; }

            // ---- ability points ----
            var aa = RxAa.Match(msg);
            if (aa.Success) { AbilityPoints += aa.Groups["n"].Success ? int.Parse(aa.Groups["n"].Value, Inv) : 1; return true; }

            // ---- self-buff apply / fade (flavor text, from wiki spell data) ----
            if (BuffData.ByApply.TryGetValue(msg, out var cands) && cands.Count > 0)
            {
                // shared apply lines (e.g. Quickness vs Alacrity) are disambiguated by the
                // "You begin casting <Spell>" line that just preceded this apply.
                var def = cands.Count == 1 ? cands[0] : (Buffs.ResolveApply(cands, dt) ?? cands[0]);
                Buffs.SelfApply(def.Spell, def.DurationSec, dt);
                return true;
            }
            if (BuffData.FadeToSpells.TryGetValue(msg, out var fspells))
            {
                Buffs.SelfFade(Buffs.PickActive(fspells) ?? fspells[0], dt);
                return true;
            }

            return false;
        }

        // ---------- verb helpers ----------
        private static bool IsMeleeVerb(string verb) => BaseVerb(verb) != null;
        private static string? BaseVerb(string verb)
        {
            string v = verb.ToLowerInvariant();
            if (MeleeVerbs.ContainsKey(v)) return v;
            if (v.EndsWith("s") && MeleeVerbs.ContainsKey(v[..^1])) return v[..^1];
            if (v.EndsWith("es") && MeleeVerbs.ContainsKey(v[..^2])) return v[..^2];
            return null;
        }
        private static string SkillFor(string verb)
        {
            string? b = BaseVerb(verb);
            if (b != null) return MeleeVerbs[b];
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

        // ---------- session read-side (delegates to Session aggregate) ----------
        public DateTime? FirstTime => Session.FirstTime;
        public DateTime LastTime => Session.LastTime;
        public double SessionSeconds => Session.Seconds;
        public double SessionHours => Session.Hours;

        public IEnumerable<Combatant> Friendlies => Session.Friendlies;
        public IEnumerable<Combatant> Combatants => Session.Combatants.Values;
        public bool IsFriendly(Combatant c) => Session.IsFriendly(c);
        public Combatant? Player => Session.Player;
        public Combatant? Pet => Session.Pet;
        public bool HasPet => Session.HasPet;
        public double DpsSession(Combatant c) => Session.DpsOf(c);

        public double PlayerDps => Session.PlayerDps;
        public double PetDps => Session.PetDps;
        public double CombinedDps => Session.CombinedDps;
        public double Hps => Session.Hps;
        public double IncomingDpsMe => Session.IncomingDpsMe;
        public double IncomingDpsPet => Session.IncomingDpsPet;
        public long EnemyHealing => Session.EnemyHealing;
        public double EnemyHps => Session.EnemyHps;
        public long DamageTaken => Session.DamageTaken;
        public long DamageTakenPet => Session.DamageTakenPet;
        public long HealingDone => Session.HealingDone;
        public double DamageTakenPerHour => DamageTaken / SessionHours;

        // ---- incoming-melee avoidance (survivability) ----
        public long Dodged => Session.Dodged;
        public long Parried => Session.Parried;
        public long Blocked => Session.Blocked;
        public long Riposted => Session.Riposted;
        public long IncomingMissed => Session.IncomingMissed;
        public long MeleeSwingsLanded => Session.MeleeSwingsLanded;
        public long SwingsAtYou => Session.SwingsAtYou;
        public long ActiveAvoids => Session.ActiveAvoids;
        public double AvoidedPct => Session.AvoidedPct;
        public double ActiveAvoidPct => Session.ActiveAvoidPct;
        public long StunsTaken => Session.StunsTaken;
        public IEnumerable<HealStat> HealsByAmount => Session.HealsByAmount;
        public double OverhealPct => Session.OverhealPct;

        public double CoinPerHour => TotalPlat / SessionHours;
        public double MotesPerHour => MoteCount / SessionHours;
        public double XpPerHour => TotalXpPct / SessionHours;
        public double KillsPerHour => Kills / SessionHours;
        public double AaPerHour => AbilityPoints / SessionHours;
        public double? HoursToLevel => XpPerHour > 0 ? 100.0 / XpPerHour : (double?)null;

        // biggest single events (session)
        public AbilityStat? BiggestMelee => Session.BiggestMelee;
        public AbilityStat? BiggestSpell => Session.BiggestSpell;
        public HealStat? BiggestHeal => Session.BiggestHeal;
        public long BiggestHitTaken => Session.BiggestHitTaken;
        public string BiggestHitTakenFrom => Session.BiggestHitTakenFrom;

        // encounters
        public Encounter? Current => _cur;
        public bool EncounterActive => _cur != null && (LastTime - _cur.Agg.LastTime).TotalSeconds <= EncounterTimeoutSec;
        public Encounter? Latest => _cur ?? (Encounters.Count > 0 ? Encounters[^1] : null);
        public IEnumerable<Encounter> EncountersNewestFirst
        {
            get
            {
                if (_cur != null && _cur.Agg.FirstTime != null) yield return _cur;
                for (int i = Encounters.Count - 1; i >= 0; i--) yield return Encounters[i];
            }
        }
    }
}
