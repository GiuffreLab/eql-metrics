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

        public readonly List<FadeEvent> Fades = new();   // recent worn-off events, for transient alerts

        private readonly Dictionary<string, Buff> _active = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastCast = new(StringComparer.OrdinalIgnoreCase);

        public void UseShared(Dictionary<string, double> durations, Dictionary<string, BuffCat> categories)
        {
            Durations = durations;
            Categories = categories;
        }

        public void ResetActive() { _active.Clear(); _lastCast.Clear(); Fades.Clear(); }

        public void BeginCast(string spell, DateTime t)
        {
            _lastCast[spell] = t;   // record for duration learning even if not yet known
            if (Categories.TryGetValue(spell, out var cat))
            {
                _active[spell] = new Buff
                {
                    Name = spell,
                    Category = cat,
                    Start = t,
                    Duration = Durations.TryGetValue(spell, out var d) ? d : (double?)null
                };
            }
        }

        public void Fail(string spell) => _active.Remove(spell);

        // ---- self-buffs driven by wiki-sourced flavor text (known durations) ----
        public void SelfApply(string spell, double durationSec, DateTime t)
        {
            _lastCast[spell] = t;
            Categories[spell] = BuffCat.Self;
            if (durationSec > 0) Durations[spell] = durationSec;
            _active[spell] = new Buff
            {
                Name = spell,
                Category = BuffCat.Self,
                Start = t,
                Duration = durationSec > 0 ? durationSec : (double?)null
            };
        }

        public void SelfFade(string spell, DateTime t) => WornOff(spell, BuffCat.Self, "", t, learn: false);

        /// <summary>Given candidate spells sharing a fade line, return the one currently active.</summary>
        public string? PickActive(IEnumerable<string> spells)
        {
            foreach (var s in spells) if (_active.ContainsKey(s)) return s;
            return null;
        }

        public void WornOff(string spell, BuffCat cat, string target, DateTime t, bool learn = true)
        {
            Categories[spell] = cat;                    // learn it's a buff/debuff of this category
            if (learn && _lastCast.TryGetValue(spell, out var st))
            {
                double dur = (t - st).TotalSeconds;
                if (dur > 0 && dur < 36000) Durations[spell] = dur;   // learn duration (ignore absurd)
            }
            _active.Remove(spell);

            Fades.Add(new FadeEvent { Name = spell, Category = cat, Time = t, Target = target ?? "" });
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
