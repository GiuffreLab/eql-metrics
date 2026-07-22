using System;
using System.Collections.Generic;
using System.Linq;

namespace EqlMetrics.Core
{
    public enum BuffCat { Self, Pet, Debuff }

    /// <summary>
    /// Tracks active buffs/debuffs and learns their durations from cast→fade pairs.
    /// A spell is only tracked once it's KNOWN to be a buff (i.e. we've seen it wear
    /// off at least once, or it was preloaded). Durations/Categories are shared refs
    /// so the host can persist and reload learned data across sessions.
    /// </summary>
    public sealed class BuffTracker
    {
        public sealed class Buff
        {
            public string Name = "";
            public BuffCat Category;
            public DateTime Start;
            public double? Duration;   // learned seconds, if known
            public string Target = "";

            public double Elapsed(DateTime now) => Math.Max(0, (now - Start).TotalSeconds);
            public double? Remaining(DateTime now) => Duration.HasValue ? Math.Max(0, Duration.Value - Elapsed(now)) : (double?)null;
            public double Fraction(DateTime now)
            {
                if (!Duration.HasValue || Duration.Value <= 0) return 1;
                double r = Remaining(now) ?? 0;
                double f = r / Duration.Value;
                return f < 0 ? 0 : f > 1 ? 1 : f;
            }
        }

        public struct FadeEvent { public string Name; public BuffCat Category; public DateTime Time; public string Target; }

        // shared/persisted learned data (host owns the files)
        public Dictionary<string, double> Durations = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, BuffCat> Categories = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Songs = new(StringComparer.OrdinalIgnoreCase);   // buffs that PULSE (bard songs) — no gain popups

        private readonly Dictionary<string, int> _pulseCount = new(StringComparer.OrdinalIgnoreCase);
        private const double SongPulseWindowSec = 10;   // a re-apply this soon after the last = a pulse, not a refresh

        public readonly List<FadeEvent> Fades = new();   // recent worn-off events, for transient alerts
        public readonly List<FadeEvent> Gains = new();   // recent "landed" events, for transient alerts

        private readonly Dictionary<string, Buff> _active = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastCast = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _memorized = new(StringComparer.OrdinalIgnoreCase);  // base names on the spell bar

        public void Memorize(string spell) => _memorized.Add(BaseName(spell));
        public void Forget(string spell) => _memorized.Remove(BaseName(spell));

        public void UseShared(Dictionary<string, double> durations, Dictionary<string, BuffCat> categories, HashSet<string> songs)
        {
            Durations = durations;
            Categories = categories;
            Songs = songs;
        }

        public void ResetActive() { _active.Clear(); _lastCast.Clear(); Fades.Clear(); Gains.Clear(); _memorized.Clear(); }

        public void BeginCast(string spell, DateTime t)
        {
            string bn = BaseName(spell);
            _lastCast[bn] = t;   // base-keyed; used for learning + cast-correlation
            if (!Categories.TryGetValue(bn, out var cat)) return;
            if (cat == BuffCat.Self) return;   // self-buffs are driven by their apply flavor line, not the cast
            _active[bn] = new Buff { Name = bn, Category = cat, Start = t, Duration = EffectiveDuration(bn) };
        }

        public void Fail(string spell) => _active.Remove(BaseName(spell));

        /// <summary>Did we see "You begin casting &lt;spell&gt;" within the last <paramref name="windowSec"/> seconds?
        /// Used to confirm a pet-buff landing ("&lt;pet&gt; goes berserk.") came from YOUR cast, not someone else's.</summary>
        public bool WasCastRecently(string spell, DateTime now, double windowSec)
            => _lastCast.TryGetValue(BaseName(spell), out var t)
               && (now - t).TotalSeconds >= 0 && (now - t).TotalSeconds <= windowSec;

        /// <summary>A buff you cast landed on your pet (or another target). Activates it and fires a "gained" event.</summary>
        public void PetApply(string spell, double durationSec, string target, DateTime t)
        {
            string bn = BaseName(spell);
            _lastCast[bn] = t;
            Categories[bn] = BuffCat.Pet;
            double? eff = EffectiveDuration(bn) ?? (durationSec > 0 ? durationSec : (double?)null);
            _active[bn] = new Buff { Name = bn, Category = BuffCat.Pet, Start = t, Duration = eff, Target = target ?? "" };
            RecordGain(bn, BuffCat.Pet, target ?? "", t);
        }

        // observed (learned) duration includes focus/AA extension, so it wins once we trust it;
        // the wiki base seeds the timer beforehand and bounds the learned value against mis-pairs.
        private double? EffectiveDuration(string bn)
        {
            double? wiki = BuffData.DurationFor(bn);
            if (Durations.TryGetValue(bn, out var learned) && learned > 0)
            {
                if (!wiki.HasValue) return learned;
                double r = learned / wiki.Value;
                if (r >= 0.5 && r <= 2.5) return learned;   // plausible (focus/AA); else the learn was a mis-pair
                return wiki;
            }
            return wiki;
        }

        // ---- self-buffs driven by wiki-sourced flavor text (known durations) ----
        public void SelfApply(string spell, double durationSec, DateTime t)
        {
            string bn = BaseName(spell);
            Categories[bn] = BuffCat.Self;
            // wiki duration (durationSec) seeds the timer; a learned value, if trusted, wins
            double? eff = EffectiveDuration(bn) ?? (durationSec > 0 ? durationSec : (double?)null);

            // Bard songs pulse (re-apply their flavor every ~6s) — detect the fast re-application and mark them as
            // songs so they never spam "buff gained". A genuine buff refresh happens near expiry, not seconds in.
            bool wasActive = _active.TryGetValue(bn, out var prev) && prev.Remaining(t) > 0;
            bool fastReapply = wasActive && prev.Elapsed(t) < SongPulseWindowSec;
            if (fastReapply)
            {
                int n = _pulseCount.TryGetValue(bn, out var c) ? c + 1 : 1;
                _pulseCount[bn] = n;
                if (n >= 2) Songs.Add(bn);   // two rapid pulses = definitely a song (not an accidental double-cast)
            }
            else _pulseCount[bn] = 0;

            _lastCast[bn] = t;
            _active[bn] = new Buff { Name = bn, Category = BuffCat.Self, Start = t, Duration = eff };

            // Only pop on a genuine fresh landing: not a known song, not a pulse, not a still-active refresh.
            if (Songs.Contains(bn) || wasActive) return;
            RecordGain(bn, BuffCat.Self, "", t);
        }

        private void RecordGain(string name, BuffCat cat, string target, DateTime t)
        {
            Gains.Add(new FadeEvent { Name = name, Category = cat, Time = t, Target = target ?? "" });
            if (Gains.Count > 100) Gains.RemoveAt(0);
        }

        /// <summary>"Landed" events within the last <paramref name="lingerSec"/> seconds, newest first.</summary>
        public IEnumerable<FadeEvent> RecentGains(DateTime now, double lingerSec)
        {
            for (int i = Gains.Count - 1; i >= 0; i--)
            {
                double age = (now - Gains[i].Time).TotalSeconds;
                if (age >= 0 && age <= lingerSec) yield return Gains[i];
            }
        }

        public void SelfFade(string spell, DateTime t) => WornOff(spell, BuffCat.Self, "", t, learn: true);

        /// <summary>Given candidate spells sharing a fade line, return the one currently active.</summary>
        public string? PickActive(IEnumerable<string> spells)
        {
            foreach (var s in spells) if (_active.ContainsKey(BaseName(s))) return s;
            return null;
        }

        // strip a trailing rank ("Quickness IV" -> "Quickness", "Echo of Health III" -> "Echo of Health")
        private static readonly System.Text.RegularExpressions.Regex RxRank =
            new(@"\s+(?:[IVXLCDM]+|\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);
        public static string BaseName(string spell) => RxRank.Replace(spell, "").Trim();

        /// <summary>
        /// When several spells share an apply line, pick the one whose cast we saw most
        /// recently (the log names the spell at cast time, e.g. "You begin casting Alacrity.").
        /// </summary>
        public BuffDef? ResolveApply(IReadOnlyList<BuffDef> candidates, DateTime now, double windowSec = 15)
        {
            // 1. most recent matching cast wins (the log names the spell at cast time)
            BuffDef? best = null;
            DateTime bestT = DateTime.MinValue;
            foreach (var kv in _lastCast)
            {
                double age = (now - kv.Value).TotalSeconds;
                if (age < 0 || age > windowSec) continue;
                string b = BaseName(kv.Key);
                foreach (var d in candidates)
                    if (b.Equals(d.Spell, StringComparison.OrdinalIgnoreCase) && kv.Value > bestT)
                    {
                        best = d;
                        bestT = kv.Value;
                    }
            }
            if (best != null) return best;

            // 2. no cast line (e.g. Quick Buff mass-cast): fall back to whichever candidate is memorized
            foreach (var d in candidates)
                if (_memorized.Contains(d.Spell)) return d;

            return null;
        }

        public void WornOff(string spell, BuffCat cat, string target, DateTime t, bool learn = true)
        {
            string bn = BaseName(spell);
            Categories[bn] = cat;                    // learn it's a buff/debuff of this category
            if (learn && _lastCast.TryGetValue(bn, out var st))
            {
                double dur = (t - st).TotalSeconds;
                if (dur > 0 && dur < 36000) Durations[bn] = dur;   // learn duration (ignore absurd)
            }
            _active.Remove(bn);

            Fades.Add(new FadeEvent { Name = bn, Category = cat, Time = t, Target = target ?? "" });
            if (Fades.Count > 100) Fades.RemoveAt(0);
        }

        /// <summary>Fades within the last <paramref name="lingerSec"/> seconds, newest first.</summary>
        public IEnumerable<FadeEvent> RecentFades(DateTime now, double lingerSec)
        {
            for (int i = Fades.Count - 1; i >= 0; i--)
            {
                double age = (now - Fades[i].Time).TotalSeconds;
                if (age >= 0 && age <= lingerSec) yield return Fades[i];
            }
        }

        /// <summary>Active buffs, soonest-to-expire first (unknown-duration last).</summary>
        public IReadOnlyList<Buff> Active(DateTime now) =>
            _active.Values.OrderBy(b => b.Remaining(now) ?? double.MaxValue).ToList();

        public int ActiveCount => _active.Count;
    }
}
