using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EqlMetrics.Core;
using Microsoft.Win32;

namespace EqlMetrics
{
    public partial class MainWindow : Window
    {
        private readonly object _lock = new();
        private SessionStats _stats = new();
        private LogTailer? _tailer;
        private Settings _settings = new();
        private readonly DispatcherTimer _timer;
        private HwndSource? _source;

        private string _selected = "";        // combatant selected in Breakdown
        private string _tab = "Overview";
        private DateTime? _selEncStart;        // selected encounter (null = latest)
        private Dictionary<string, double> _buffDur = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, BuffCat> _buffCat = new(StringComparer.OrdinalIgnoreCase);
        // center-screen notifications (stealth, buffs, Quick Buff) via CenterFlash
        private CenterFlash? _flash;
        private DateTime _lastStealthTime = DateTime.MinValue;
        private DateTime _lastGainTime = DateTime.MinValue;
        private DateTime _lastFadeTime = DateTime.MinValue;
        private DateTime _lastSkillTime = DateTime.MinValue;
        private DateTime _lastMissFlash = DateTime.MinValue;
        private const double MissFlashGapSec = 3.0;   // throttle miss popups (frontal backstab misses spam otherwise)
        private DateTime _lastCleaveTime = DateTime.MinValue;          // cursor into s.CleaveHits
        private readonly List<CleaveHit> _cleaveBurst = new();         // pending same-actor+skill AoE burst
        private DateTime _cleaveBurstWall = DateTime.MinValue;         // wall-clock of the last hit added (for settle)
        private DateTime _lastMendTime = DateTime.MinValue;            // cursor into s.MendEvents
        private DateTime? _qbNotifiedFor;   // Quick Buff activation we've already flashed "ready" for
        private const double FreshSec = 8;  // ignore events older than this (skips historical log replay on load)

        // ---- palette ----
        private static SolidColorBrush B(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c); b.Freeze(); return b;
        }
        private static readonly Brush Dim = B("#8A97AB");
        private static readonly Brush Text = B("#E8EEF7");
        private static readonly Brush You = B("#5AA9FF"), YouT = B("#BCDCFF");
        private static readonly Brush Pet = B("#B48BFF"), PetT = B("#D8C6FF");
        private static readonly Brush Grp = B("#57D6A6"), GrpT = B("#B6F0D8");
        private static readonly Brush Nuke = B("#FF9F5A"), Dot = B("#C98BFF"), Melee = B("#5AD6C4");
        private static readonly Brush Gold = B("#F4C85B"), Mote = B("#8BE0FF"), Heal = B("#57D6A6"), Xp = B("#C9A6FF"), DmgIn = B("#FF7A7A");
        private static readonly Brush RowBg = B("#0DFFFFFF"), RowStroke = B("#2A313B"), RowStrokeSel = B("#66C9A24B");
        private static readonly Brush TabSel = B("#2AC9A24B");
        private static readonly Brush AccentGold = B("#C9A24B");   // subtle default_modern chrome accent

        // ---- center-flash accent colors ----
        private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
        private static readonly Color FlHide = C(0xA8, 0x8B, 0xFF);   // violet
        private static readonly Color FlSneak = C(0x7F, 0xE0, 0xD0);  // teal
        private static readonly Color FlFail = C(0xFF, 0x6B, 0x6B);   // red (hide/sneak failed)
        private static readonly Color FlGain = C(0x57, 0xD6, 0xA6);   // green (buff gained)
        private static readonly Color FlFade = C(0xFF, 0x7A, 0x7A);   // red (buff faded)
        private static readonly Color FlDebuff = C(0xFF, 0x9F, 0x5A); // orange (debuff wore off)
        private static readonly Color FlQuickBuff = C(0xF4, 0xC8, 0x5B); // gold (Quick Buff ready)
        private static readonly Color FlSkill = C(0x5A, 0xD6, 0xC4);     // teal (skill proc, e.g. backstab)
        private static readonly Color FlSkillCrit = C(0xFF, 0xC8, 0x5B); // bright gold (skill crit)
        private static readonly Color FlSkillMiss = C(0xC9, 0x7A, 0x7A); // muted red (skill miss)
        private static readonly Color FlCleave = C(0xFF, 0x8A, 0x5A);    // orange (multi-target cleave / round kick)
        private static readonly Color FlMend = C(0x57, 0xD6, 0xA6);      // green (Mend self-heal)

        public MainWindow()
        {
            InitializeComponent();
            _settings = Settings.Load();
            (_buffDur, _buffCat) = BuffStore.Load();
            SpellStore.LoadIntoBuffData();   // override self-buff table from scraped spells.json if present
            Backdrop.Opacity = Clamp(_settings.PanelAlpha, 0.12, 0.95);
            _selected = string.IsNullOrEmpty(_settings.PlayerName) ? "" : _settings.PlayerName;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _timer.Tick += (_, __) => Refresh();
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        // ================= startup / shutdown =================
        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            Left = _settings.Left; Top = _settings.Top;
            ApplyExpanded(_settings.Expanded);
            SelectTab(_tab);

            string path = _settings.LastLogPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) path = AutoDetectLog();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) LoadLog(path);
            else StatusText.Text = "click the folder icon to pick your log";

            _timer.Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);
            try { RegisterHotKey(hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_X); } catch { }
            ApplyClickThrough(_settings.ClickThrough);
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _settings.Left = Left; _settings.Top = Top;
            _settings.PanelAlpha = Backdrop.Opacity;
            _settings.Save();
            BuffStore.Save(_buffDur, _buffCat);
            try { _spellCts?.Cancel(); } catch { }
            try { _flash?.Close(); } catch { }
            try { _tailer?.Stop(); } catch { }
            try { UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID); } catch { }
        }

        private static string AutoDetectLog()
        {
            foreach (var dir in new[]
            {
                @"E:\EverQuest Legends\Logs", @"C:\EverQuest Legends\Logs",
                @"C:\Program Files\EverQuest Legends\Logs", @"C:\Program Files (x86)\EverQuest Legends\Logs",
            })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    var newest = new DirectoryInfo(dir).GetFiles("eqlog_*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                    if (newest != null) return newest.FullName;
                }
                catch { }
            }
            return "";
        }

        private void LoadLog(string path)
        {
            try { _tailer?.Stop(); } catch { }
            string player = DerivePlayerName(path) ?? (string.IsNullOrEmpty(_settings.PlayerName) ? "You" : _settings.PlayerName);
            _settings.LastLogPath = path;
            _settings.PlayerName = player;
            if (string.IsNullOrEmpty(_selected)) _selected = player;

            lock (_lock)
            {
                _stats = new SessionStats { PlayerName = player, PetName = _settings.PetName };
                _stats.Buffs.UseShared(_buffDur, _buffCat);   // share learned durations across sessions
            }

            CharLabel.Text = player + (string.IsNullOrEmpty(_settings.PetName) ? "" : "  +  " + _settings.PetName);
            StatusText.Text = Path.GetFileName(path);
            _tailer = new LogTailer(path, _settings.FollowFromStart, OnLine);
            _tailer.Start();
        }

        private void OnLine(string line) { lock (_lock) { _stats.Apply(line); } }

        private static string? DerivePlayerName(string path)
        {
            var m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"^eqlog_(?<name>[^_]+)_", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups["name"].Value : null;
        }

        // ================= refresh =================
        private void Refresh()
        {
            lock (_lock)
            {
                var s = _stats;
                if (!s.FirstTime.HasValue) { BigDps.Text = "0"; return; }

                var cur = s.Current;
                double headline = cur != null ? cur.Agg.CombinedDps : s.CombinedDps;
                BigDps.Text = Math.Round(headline).ToString("0");
                DpsSplit.Text = s.HasPet
                    ? $"you {(cur?.Agg.PlayerDps ?? s.PlayerDps):0}  ·  pet {(cur?.Agg.PetDps ?? s.PetDps):0}"
                    : $"session {s.PlayerDps:0.0}";

                EncounterName.Text = s.EncounterActive && cur != null ? "vs " + cur.Title
                    : (s.Latest != null ? "last: " + s.Latest.Title : "no fight yet");
                EncounterTime.Text = s.EncounterActive && cur != null ? "⚔ " + FmtClock(cur.Seconds) : "idle";
                SessionTimer.Text = FmtClock(s.SessionSeconds);

                BuildCoreRates(s);

                CheckNotifications(s);
                if (BiggestStrip.Visibility == Visibility.Visible) BuildBiggest(s);

                if (MaxPanel.Visibility == Visibility.Visible)
                {
                    switch (_tab)
                    {
                        case "Overview": BuildOverview(s); break;
                        case "Breakdown": BuildBreakdown(s); break;
                        case "Avoidance": BuildAvoidance(s); break;
                        case "Encounters": BuildEncounters(s); break;
                        case "Loot": BuildLoot(s); break;
                    }
                }
            }
        }

        private void BuildCoreRates(SessionStats s)
        {
            CoreRates.Children.Clear();
            CoreRates.Children.Add(Chip("HPS", s.Hps.ToString("0"), "", Heal));
            CoreRates.Children.Add(Chip("IN DPS", s.IncomingDpsMe.ToString("0"), "", DmgIn));
            if (s.SwingsAtYou > 0) CoreRates.Children.Add(Chip("AVOID", s.AvoidedPct.ToString("0") + "%", "", Melee));
            CoreRates.Children.Add(Chip("ENEMY HPS", s.EnemyHps.ToString("0"), "", Nuke));
            CoreRates.Children.Add(Chip("XP/HR", s.XpPerHour.ToString("0.0") + "%", "", Xp));
        }

        private static string WhoFor(SessionStats s, BuffTracker.FadeEvent f) => f.Category switch
        {
            BuffCat.Pet => string.IsNullOrEmpty(s.PetName) ? "your pet" : s.PetName,
            BuffCat.Debuff => string.IsNullOrEmpty(f.Target) ? "target" : f.Target,
            _ => string.IsNullOrEmpty(s.PlayerName) ? "you" : s.PlayerName,
        };

        // All transient alerts go through the center-screen CenterFlash. Each source is
        // gated by a "last seen" timestamp and a freshness window so the historical log
        // replay on load doesn't flash; live bursts (mass buff cast) are coalesced.
        private void CheckNotifications(SessionStats s)
        {
            var now = DateTime.Now;
            _flash ??= new CenterFlash { Owner = this };   // closes with the main window
            _flash.MaxActive = (int)Clamp(_settings.NotifMaxOnScreen, 1, 5);
            if (!_settings.NotifMaster) return;            // all alerts off (cursors advance on re-enable via freshness gate)

            if (_settings.NotifStealth) NotifyStealth(s, now);
            if (_settings.NotifBuffs) NotifyBuffs(s, now);
            if (_settings.NotifSkills) NotifySkills(s, now);
            if (_settings.NotifMend) NotifyMend(s, now);
            if (_settings.NotifQuickBuff) NotifyQuickBuff(s, now);
        }

        // ---- hide / sneak (success + failure) ----
        private void NotifyStealth(SessionStats s, DateTime now)
        {
            for (int i = 0; i < s.StealthEvents.Count; i++)
            {
                var ev = s.StealthEvents[i];
                if (ev.Time <= _lastStealthTime) continue;
                _lastStealthTime = ev.Time;
                if ((now - ev.Time).TotalSeconds > FreshSec) continue;
                switch (ev.Kind)
                {
                    case StealthKind.Hide:      _flash!.Flash("", "HIDDEN", "hide successful", FlHide); break;
                    case StealthKind.Sneak:     _flash!.Flash("", "SNEAKING", "sneak successful", FlSneak); break;
                    case StealthKind.HideFail:  _flash!.Flash("⚠", "HIDE FAILED", "you are exposed", FlFail); break;
                    case StealthKind.SneakFail: _flash!.Flash("⚠", "SNEAK FAILED", "you are making noise", FlFail); break;
                }
            }
        }

        // ---- buff / debuff gained & faded ----
        private void NotifyBuffs(SessionStats s, DateTime now)
        {
            var gains = FreshSince(s.Buffs.Gains, ref _lastGainTime, now);
            if (gains.Count == 1) _flash!.Flash("", gains[0].Name, "buff gained", FlGain);
            else if (gains.Count > 1) _flash!.Flash("✦", gains.Count + " buffs", "gained", FlGain);

            var fades = FreshSince(s.Buffs.Fades, ref _lastFadeTime, now);
            if (fades.Count > 2) _flash!.Flash("", fades.Count + " buffs", "faded", FlFade);
            else foreach (var f in fades)
                {
                    if (f.Category == BuffCat.Debuff) _flash!.Flash("", f.Name, "wore off " + WhoFor(s, f), FlDebuff);
                    else _flash!.Flash("", f.Name, "faded from " + WhoFor(s, f), FlFade);
                }
        }

        // ---- skill pop-ups: backstab procs + cleave/kick/strike AoE bursts ----
        private void NotifySkills(SessionStats s, DateTime now)
        {
            var fresh = new List<SkillProc>();
            for (int i = 0; i < s.SkillProcs.Count; i++)
            {
                var sp = s.SkillProcs[i];
                if (sp.Time <= _lastSkillTime) continue;
                _lastSkillTime = sp.Time;
                if ((now - sp.Time).TotalSeconds <= FreshSec) fresh.Add(sp);
            }
            for (int i = 0; i < fresh.Count;)
            {
                var p = fresh[i];
                if (p.Miss)
                {
                    if ((now - _lastMissFlash).TotalSeconds >= MissFlashGapSec)   // throttle miss spam
                    {
                        _lastMissFlash = now;
                        _flash!.Flash("✕", "MISS", p.Skill.ToUpperInvariant(), FlSkillMiss, holdMs: 850);
                    }
                    i++;
                    continue;
                }
                long total = p.Damage; bool crit = p.Crit; int hits = 1; var last = p; int j = i + 1;
                while (j < fresh.Count && !fresh[j].Miss
                       && string.Equals(fresh[j].Skill, p.Skill, StringComparison.OrdinalIgnoreCase)
                       && (fresh[j].Time - last.Time).TotalSeconds <= 1.5)
                {
                    total += fresh[j].Damage; crit |= fresh[j].Crit; hits++; last = fresh[j]; j++;
                }
                string name = p.Skill.ToUpperInvariant();
                string label = hits == 1 ? name : hits == 2 ? "DOUBLE " + name : hits + "× " + name;
                if (crit)
                    _flash!.Flash("✦", total.ToString(), label + " — CRITICAL!", FlSkillCrit, holdMs: 1600);
                else
                    _flash!.Flash(hits > 1 ? "⚔⚔" : "⚔", total.ToString(), label, FlSkill, holdMs: hits > 1 ? 1400 : 1100);
                i = j;
            }

            for (int i = 0; i < s.CleaveHits.Count; i++)
            {
                var ch = s.CleaveHits[i];
                if (ch.Time <= _lastCleaveTime) continue;
                _lastCleaveTime = ch.Time;
                if ((now - ch.Time).TotalSeconds > FreshSec) continue;
                if (_cleaveBurst.Count > 0 &&
                    (!string.Equals(_cleaveBurst[_cleaveBurst.Count - 1].Actor, ch.Actor, StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(_cleaveBurst[_cleaveBurst.Count - 1].Skill, ch.Skill, StringComparison.OrdinalIgnoreCase)
                     || (ch.Time - _cleaveBurst[_cleaveBurst.Count - 1].Time).TotalSeconds > 1.5))
                    FinalizeCleave(s);
                _cleaveBurst.Add(ch);
                _cleaveBurstWall = now;
            }
            if (_cleaveBurst.Count > 0 && (now - _cleaveBurstWall).TotalSeconds >= 1.0) FinalizeCleave(s);   // settle
        }

        // ---- Mend (monk self-heal; no amount in the log, just confirm it fired) ----
        private void NotifyMend(SessionStats s, DateTime now)
        {
            for (int i = 0; i < s.MendEvents.Count; i++)
            {
                var mt = s.MendEvents[i];
                if (mt <= _lastMendTime) continue;
                _lastMendTime = mt;
                if ((now - mt).TotalSeconds > FreshSec) continue;
                _flash!.Flash("✚", "MEND", "wounds mended", FlMend, holdMs: 1200);
            }
        }

        // ---- Quick Buff ready (timed off the activation line; no "ready" log exists) ----
        private void NotifyQuickBuff(SessionStats s, DateTime now)
        {
            if (s.QuickBuffCastAt is DateTime qb)
            {
                var ready = qb.AddSeconds(SessionStats.QuickBuffCooldownSec);
                if (_qbNotifiedFor != qb && now >= ready && (now - ready).TotalSeconds < 20)
                {
                    _qbNotifiedFor = qb;
                    _flash!.Flash("✦", "QUICK BUFF READY", "cooldown up — recast", FlQuickBuff, holdMs: 1500);
                }
            }
        }

        // Events newer than 'last' (advancing it to the newest seen), returning only the
        // fresh ones — so historical lines advance the cursor without triggering a flash.
        private static List<BuffTracker.FadeEvent> FreshSince(List<BuffTracker.FadeEvent> list, ref DateTime last, DateTime now)
        {
            var fresh = new List<BuffTracker.FadeEvent>();
            var newest = last;
            foreach (var e in list)
            {
                if (e.Time <= last) continue;
                if (e.Time > newest) newest = e.Time;
                if ((now - e.Time).TotalSeconds <= FreshSec) fresh.Add(e);
            }
            last = newest;
            return fresh;
        }

        // Flush the pending burst (one activation of cleave / kick / strike). Folds in double/triple
        // attacks (multiple hits on one target) and multi-target splashes (hits on 2+ targets).
        private void FinalizeCleave(SessionStats s)
        {
            if (_cleaveBurst.Count == 0) return;
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0; bool crit = false; int hits = _cleaveBurst.Count; var first = _cleaveBurst[0];
            foreach (var h in _cleaveBurst)
            {
                if (!string.IsNullOrEmpty(h.Target)) targets.Add(h.Target);
                total += h.Damage; crit |= h.Crit;
            }
            _cleaveBurst.Clear();
            int nt = targets.Count;

            bool aoeOnly = string.Equals(first.Skill, "Cleave", StringComparison.OrdinalIgnoreCase);
            if (aoeOnly && nt < 2) return;   // a lone cleave is just the pet's normal swing — stay silent

            // base name: a multi-target kick is definitively a round kick; cleave stays cleave; else the verb
            string baseName = string.Equals(first.Skill, "Kick", StringComparison.OrdinalIgnoreCase)
                ? (nt >= 2 ? "ROUND KICK" : "KICK")
                : first.Skill.ToUpperInvariant();

            // double/triple only makes sense on a single target; multi-target shows the target count instead
            string prefix = nt == 1 ? (hits == 2 ? "DOUBLE " : hits == 3 ? "TRIPLE " : hits > 3 ? hits + "× " : "") : "";
            string tgtSuffix = nt >= 2 ? $" — {nt} targets" : "";
            string sub = (s.IsPlayerToken(first.Actor) ? "" : "PET ") + prefix + baseName + tgtSuffix + (crit ? " — CRIT!" : "");

            Color color = crit ? FlSkillCrit : nt >= 2 ? FlCleave : FlSkill;   // gold crit / orange AoE / teal single
            string icon = crit ? "✦" : nt >= 2 ? "✷" : "⚔";
            _flash!.Flash(icon, total.ToString(), sub, color, holdMs: (crit || nt >= 2) ? 1500 : 1100);
        }

        private void BuildBiggest(SessionStats s)
        {
            BiggestPanel.Children.Clear();
            BiggestPanel.Children.Add(Chip("MELEE", (s.BiggestMelee?.Max ?? 0).ToString("0"), s.BiggestMelee?.Name ?? "—", Melee));
            BiggestPanel.Children.Add(Chip("SPELL", (s.BiggestSpell?.Max ?? 0).ToString("0"), s.BiggestSpell?.Name ?? "—", Nuke));
            BiggestPanel.Children.Add(Chip("HIT TAKEN", s.BiggestHitTaken.ToString("0"), s.BiggestHitTakenFrom.Length > 0 ? s.BiggestHitTakenFrom : "—", DmgIn));
            BiggestPanel.Children.Add(Chip("HEAL", (s.BiggestHeal?.Max ?? 0).ToString("0"), s.BiggestHeal?.Name ?? "—", Heal));
        }

        // ================= Overview tab =================
        private void BuildOverview(SessionStats s)
        {
            var p = OverviewPanel;
            p.Children.Clear();

            p.Children.Add(SectionHeader("CONSOLIDATED"));
            var cons = new WrapPanel();
            cons.Children.Add(StatBox("Damage out", s.CombinedDps.ToString("0.0"), YouT));
            cons.Children.Add(StatBox("Healing out", s.Hps.ToString("0.0"), Heal));
            cons.Children.Add(StatBox("Damage in", s.IncomingDpsMe.ToString("0.0"), DmgIn));
            if (s.SwingsAtYou > 0) cons.Children.Add(StatBox("Avoided", s.AvoidedPct.ToString("0") + "%", Melee));
            cons.Children.Add(StatBox("Enemy heal", s.EnemyHps.ToString("0.0"), Nuke));
            p.Children.Add(cons);

            p.Children.Add(SectionHeader("SESSION"));
            long bestHit = s.Combatants.Where(c => c.IsPlayer).SelectMany(c => c.Abilities.Values).Select(a => a.Max).DefaultIfEmpty(0).Max();
            var grid = new WrapPanel();
            void G(string k, string v, Brush b) => grid.Children.Add(StatBox(k, v, b));
            G("Kills/hr", s.KillsPerHour.ToString("0"), Text);
            G("XP/hr", s.XpPerHour.ToString("0.0") + "%", Xp);
            G("To level", s.HoursToLevel.HasValue ? FmtHours(s.HoursToLevel.Value) : "—", Text);
            G("AA gained", s.AbilityPoints.ToString("0"), Xp);
            G("AA/hr", s.AaPerHour.ToString("0.0"), Xp);
            G("Coin/hr", s.CoinPerHour.ToString("0.0") + "p", Gold);
            G("Motes/hr", s.MotesPerHour.ToString("0"), Mote);
            G("Overheal", s.OverhealPct.ToString("0") + "%", Heal);
            G("Best hit", bestHit.ToString("0"), Text);
            p.Children.Add(grid);

            p.Children.Add(SectionHeader("TOP DAMAGE"));
            var pl = s.Player;
            var topDmg = (pl?.Abilities.Values ?? Enumerable.Empty<AbilityStat>()).Where(a => a.Total > 0).OrderByDescending(a => a.Total).Take(3).ToList();
            if (topDmg.Count == 0) p.Children.Add(Hint("no damage yet"));
            else
            {
                long top = Math.Max(1, topDmg[0].Total);
                foreach (var a in topDmg)
                {
                    (string bt, Brush bc) = BadgeFor(a.Kind);
                    p.Children.Add(Row(a.Name, $"x{a.Hits}  avg {a.Avg:0.0}  max {a.Max}" + (a.Crits > 0 ? $"  crit {a.CritPct:0}%" : ""),
                        (a.Total / s.SessionSeconds).ToString("0.0"), "dps", (double)a.Total / top, bc, bc, badge: bt, badgeBrush: bc));
                }
            }

            p.Children.Add(SectionHeader("TOP HEALING"));
            var topHeal = s.HealsByAmount.Where(h => h.Effective > 0).Take(3).ToList();
            if (topHeal.Count == 0) p.Children.Add(Hint("no healing yet"));
            else
            {
                long top = Math.Max(1, topHeal[0].Effective);
                foreach (var h in topHeal)
                    p.Children.Add(Row(h.Name, $"x{h.Casts}  avg {h.Avg:0.0}  max {h.Max}  overheal {h.OverhealPct:0}%",
                        (h.Effective / s.SessionSeconds).ToString("0.0"), "hps", (double)h.Effective / top, Heal, Heal, badge: "HEAL", badgeBrush: Heal));
            }

            p.Children.Add(SectionHeader("BIGGEST"));
            var big = new WrapPanel();
            big.Children.Add(Chip("MELEE", (s.BiggestMelee?.Max ?? 0).ToString("0"), s.BiggestMelee?.Name ?? "—", Melee));
            big.Children.Add(Chip("SPELL", (s.BiggestSpell?.Max ?? 0).ToString("0"), s.BiggestSpell?.Name ?? "—", Nuke));
            big.Children.Add(Chip("HEAL", (s.BiggestHeal?.Max ?? 0).ToString("0"), s.BiggestHeal?.Name ?? "—", Heal));
            big.Children.Add(Chip("HIT TAKEN", s.BiggestHitTaken.ToString("0"), s.BiggestHitTakenFrom.Length > 0 ? s.BiggestHitTakenFrom : "—", DmgIn));
            p.Children.Add(big);
        }

        // ================= Breakdown tab =================
        private void BuildBreakdown(SessionStats s)
        {
            var p = BreakdownPanel;
            p.Children.Clear();

            p.Children.Add(SectionHeader("PARTY DAMAGE"));
            var list = s.Friendlies.OrderByDescending(c => c.TotalDamage).ToList();
            if (list.Count == 0) p.Children.Add(Hint("no combat yet"));
            else
            {
                long top = Math.Max(1, list[0].TotalDamage);
                long grand = Math.Max(1, list.Sum(c => c.TotalDamage));
                foreach (var c in list)
                {
                    Brush accent = c.IsPlayer ? You : c.IsPet ? Pet : Grp;
                    Brush accentT = c.IsPlayer ? YouT : c.IsPet ? PetT : GrpT;
                    string role = c.IsPlayer ? "you" : c.IsPet ? "pet" : "group";
                    string name = c.Name;
                    p.Children.Add(Row(name, role, s.DpsSession(c).ToString("0.0"),
                        (100.0 * c.TotalDamage / grand).ToString("0") + "%", (double)c.TotalDamage / top, accent, accentT,
                        selected: string.Equals(name, _selected, StringComparison.OrdinalIgnoreCase),
                        onClick: () => { _selected = name; Refresh(); }));
                }
            }

            var sel = s.Combatants.FirstOrDefault(x => string.Equals(x.Name, _selected, StringComparison.OrdinalIgnoreCase)) ?? s.Player;
            p.Children.Add(SectionHeader("ABILITIES — " + (sel?.Name ?? "—")));
            AddAbilityRows(p, sel, s.SessionSeconds);

            p.Children.Add(SectionHeader("HEALING"));
            AddHealRows(p, s.HealsByAmount.Where(h => h.Effective > 0).ToList(), s.SessionSeconds, s.Hps, s.OverhealPct);

            p.Children.Add(SectionHeader("INCOMING / ENEMY"));
            var en = new WrapPanel();
            en.Children.Add(StatBox("Incoming me", s.IncomingDpsMe.ToString("0.0"), DmgIn));
            if (s.HasPet) en.Children.Add(StatBox("Incoming pet", s.IncomingDpsPet.ToString("0.0"), DmgIn));
            en.Children.Add(StatBox("Enemy HPS", s.EnemyHps.ToString("0.0"), Nuke));
            p.Children.Add(en);
        }

        private void AddAbilityRows(Panel p, Combatant? c, double seconds)
        {
            var abilities = (c?.Abilities.Values ?? Enumerable.Empty<AbilityStat>()).Where(a => a.Total > 0).OrderByDescending(a => a.Total).ToList();
            if (abilities.Count == 0) { p.Children.Add(Hint("no damage yet")); return; }
            long top = Math.Max(1, abilities[0].Total);
            long tot = Math.Max(1, abilities.Sum(a => a.Total));
            foreach (var a in abilities)
            {
                (string bt, Brush bc) = BadgeFor(a.Kind);
                string sub = $"x{a.Hits}  avg {a.Avg:0.0}  max {a.Max}" + (a.Crits > 0 ? $"  crit {a.CritPct:0}%" : "") + (a.Misses > 0 ? $"  miss {a.MissPct:0}%" : "");
                p.Children.Add(Row(a.Name, sub, (a.Total / seconds).ToString("0.0"), (100.0 * a.Total / tot).ToString("0") + "%",
                    (double)a.Total / top, bc, bc, badge: bt, badgeBrush: bc));
            }
        }

        private void AddHealRows(Panel p, List<HealStat> heals, double seconds, double hps, double overheal)
        {
            if (heals.Count == 0) { p.Children.Add(Hint("no healing yet")); return; }
            long top = Math.Max(1, heals[0].Effective);
            long tot = Math.Max(1, heals.Sum(h => h.Effective));
            foreach (var h in heals)
                p.Children.Add(Row(h.Name, $"x{h.Casts}  avg {h.Avg:0.0}  max {h.Max}  overheal {h.OverhealPct:0}%",
                    (h.Effective / seconds).ToString("0.0"), (100.0 * h.Effective / tot).ToString("0") + "%",
                    (double)h.Effective / top, Heal, Heal, badge: "HEAL", badgeBrush: Heal));
        }

        // ================= Avoidance tab =================
        private void BuildAvoidance(SessionStats s)
        {
            var p = AvoidancePanel;
            p.Children.Clear();

            p.Children.Add(SectionHeader("SURVIVABILITY"));
            var sum = new WrapPanel();
            sum.Children.Add(StatBox("Avoided", s.AvoidedPct.ToString("0") + "%", Melee));
            sum.Children.Add(StatBox("Swings at you", s.SwingsAtYou.ToString("0"), Text));
            sum.Children.Add(StatBox("Dmg taken", s.DamageTaken.ToString("0"), DmgIn));
            sum.Children.Add(StatBox("DTPS", s.IncomingDpsMe.ToString("0.0"), DmgIn));
            sum.Children.Add(StatBox("Biggest hit", s.BiggestHitTaken.ToString("0"), DmgIn));
            sum.Children.Add(StatBox("Stuns", s.StunsTaken.ToString("0"), Nuke));
            p.Children.Add(sum);

            p.Children.Add(SectionHeader("HOW YOU AVOID"));
            if (s.SwingsAtYou == 0) p.Children.Add(Hint("no incoming swings yet"));
            else
            {
                long swings = Math.Max(1, s.SwingsAtYou);
                void AvoidRow(string name, long n, Brush c) => p.Children.Add(Row(name, n + " of " + s.SwingsAtYou + " swings",
                    (100.0 * n / swings).ToString("0") + "%", "", (double)n / swings, c, c));
                AvoidRow("Dodge", s.Dodged, Grp);
                AvoidRow("Parry", s.Parried, Melee);
                AvoidRow("Block", s.Blocked, You);
                if (s.Riposted > 0) AvoidRow("Riposte", s.Riposted, Xp);
                AvoidRow("Missed (mob)", s.IncomingMissed, Dim);
                AvoidRow("Hit (landed)", s.MeleeSwingsLanded, DmgIn);
            }

            p.Children.Add(SectionHeader("INCOMING BY ENEMY"));
            var en = s.Session.Enemies.Where(c => c.TotalDamage > 0).OrderByDescending(c => c.TotalDamage).Take(6).ToList();
            if (en.Count == 0) p.Children.Add(Hint("—"));
            else
            {
                long top = Math.Max(1, en[0].TotalDamage);
                foreach (var c in en)
                    p.Children.Add(Row(c.Name, "dealt to party", c.TotalDamage.ToString("0"), "dmg", (double)c.TotalDamage / top, DmgIn, DmgIn));
            }

            p.Children.Add(new TextBlock
            {
                Text = "avoidance only — the log shows final damage, so armor mitigation / AC can't be measured",
                Foreground = Dim, FontSize = 9.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 2)
            });
        }

        // ================= Encounters tab =================
        private Encounter? SelectedEncounter(SessionStats s)
        {
            var all = s.EncountersNewestFirst.ToList();
            if (all.Count == 0) return null;
            if (_selEncStart.HasValue)
            {
                var m = all.FirstOrDefault(e => e.Start == _selEncStart.Value);
                if (m != null) return m;
            }
            return all[0];
        }

        private void BuildEncounters(SessionStats s)
        {
            var p = EncountersPanel;
            p.Children.Clear();

            var e = SelectedEncounter(s);
            if (e == null) { p.Children.Add(Hint("no encounters yet")); return; }

            p.Children.Add(SectionHeader("ENCOUNTER — " + e.Title));
            var sum = new WrapPanel();
            sum.Children.Add(StatBox("Duration", FmtClock(e.Seconds), Text));
            sum.Children.Add(StatBox("You+pet DPS", e.Agg.CombinedDps.ToString("0.0"), YouT));
            sum.Children.Add(StatBox("HPS", e.Agg.Hps.ToString("0.0"), Heal));
            sum.Children.Add(StatBox("Dmg taken", e.Agg.DamageTaken.ToString("0"), DmgIn));
            if (e.Agg.SwingsAtYou > 0) sum.Children.Add(StatBox("Avoided", e.Agg.AvoidedPct.ToString("0") + "%", Melee));
            sum.Children.Add(StatBox("Enemy HPS", e.Agg.EnemyHps.ToString("0.0"), Nuke));
            p.Children.Add(sum);

            p.Children.Add(SectionHeader("DPS TIMELINE"));
            p.Children.Add(BuildTimeline(e));

            p.Children.Add(SectionHeader("YOUR PARTY"));
            var fr = e.Agg.Friendlies.OrderByDescending(c => c.TotalDamage).ToList();
            if (fr.Count == 0) p.Children.Add(Hint("—"));
            else
            {
                long top = Math.Max(1, fr[0].TotalDamage);
                foreach (var c in fr)
                {
                    Brush accent = c.IsPlayer ? You : c.IsPet ? Pet : Grp;
                    p.Children.Add(Row(c.Name, c.IsPlayer ? "you" : c.IsPet ? "pet" : "group",
                        e.Agg.DpsOf(c).ToString("0.0"), "dps", (double)c.TotalDamage / top, accent, accent));
                }
            }

            var eh = e.Agg.HealsByAmount.Where(h => h.Effective > 0).ToList();
            if (eh.Count > 0) { p.Children.Add(SectionHeader("HEALS THIS FIGHT")); AddHealRows(p, eh, e.Seconds, e.Agg.Hps, e.Agg.OverhealPct); }

            p.Children.Add(SectionHeader("ENEMIES"));
            var enemies = e.Agg.Enemies.OrderByDescending(c => c.TotalDamage).ToList();
            if (enemies.Count == 0) p.Children.Add(Hint("—"));
            else
            {
                long top = Math.Max(1, enemies[0].TotalDamage);
                foreach (var c in enemies)
                    p.Children.Add(Row(c.Name, "dmg to party", (c.TotalDamage / e.Seconds).ToString("0.0"), "dps",
                        (double)c.TotalDamage / top, DmgIn, DmgIn));
            }

            p.Children.Add(SectionHeader("RECENT FIGHTS  ·  click to open detail"));
            var fightsHost = new StackPanel();
            foreach (var enc in s.EncountersNewestFirst.Take(20))
            {
                bool selr = enc.Start == e.Start;
                var encc = enc;
                fightsHost.Children.Add(Row(enc.Title, enc.Start.ToString("HH:mm:ss") + "  ·  " + FmtClock(enc.Seconds),
                    enc.Agg.CombinedDps.ToString("0.0"), "dps", Clamp(enc.Seconds / 60.0, 0.05, 1.0), You, YouT,
                    selected: selr, onClick: () => OpenEncounterDetail(encc)));
            }
            p.Children.Add(new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = fightsHost });
        }

        private void OpenEncounterDetail(Encounter enc)
        {
            _selEncStart = enc.Start;
            try
            {
                EncounterWindow w;
                lock (_lock) { w = new EncounterWindow(enc); }   // build snapshot under lock
                w.Owner = this;
                w.Show();
            }
            catch { }
            Refresh();
        }

        private FrameworkElement BuildTimeline(Encounter e)
        {
            const double W = 424, H = 66;
            int n = Math.Max(e.DpsBuckets.Count, e.InBuckets.Count);
            var host = new Border
            {
                CornerRadius = new CornerRadius(9), Background = RowBg, BorderBrush = RowStroke, BorderThickness = new Thickness(1),
                Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8)
            };
            if (n < 2) { host.Child = Hint("fight too short to chart"); return host; }

            long max = 1;
            for (int i = 0; i < n; i++)
            {
                if (i < e.DpsBuckets.Count) max = Math.Max(max, e.DpsBuckets[i]);
                if (i < e.InBuckets.Count) max = Math.Max(max, e.InBuckets[i]);
            }

            var canvas = new Canvas { Width = W, Height = H };
            // baseline
            var bl = new System.Windows.Shapes.Line { X1 = 0, Y1 = H, X2 = W, Y2 = H, Stroke = RowStroke, StrokeThickness = 1 };
            canvas.Children.Add(bl);
            canvas.Children.Add(MakeLine(e.DpsBuckets, n, max, W, H, You));
            canvas.Children.Add(MakeLine(e.InBuckets, n, max, W, H, DmgIn));

            var lbl = new TextBlock { Text = $"peak {max}/s   ", Foreground = Dim, FontSize = 9 };
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            legend.Children.Add(lbl);
            legend.Children.Add(LegendDot(You, "your dps"));
            legend.Children.Add(LegendDot(DmgIn, "incoming"));

            var col = new StackPanel();
            col.Children.Add(canvas);
            col.Children.Add(legend);
            host.Child = col;
            return host;
        }

        private static System.Windows.Shapes.Polyline MakeLine(List<long> data, int n, long max, double W, double H, Brush stroke)
        {
            var pts = new PointCollection();
            double step = n > 1 ? W / (n - 1) : W;
            for (int i = 0; i < n; i++)
            {
                long v = i < data.Count ? data[i] : 0;
                double x = i * step;
                double y = H - (double)v / max * H;
                pts.Add(new Point(x, y));
            }
            return new System.Windows.Shapes.Polyline { Points = pts, Stroke = stroke, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        }

        private FrameworkElement LegendDot(Brush c, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            sp.Children.Add(new System.Windows.Shapes.Rectangle { Width = 8, Height = 8, RadiusX = 2, RadiusY = 2, Fill = c, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            sp.Children.Add(new TextBlock { Text = label, Foreground = Dim, FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        // ================= Loot tab =================
        private void BuildLoot(SessionStats s)
        {
            var p = LootPanel;
            p.Children.Clear();
            p.Children.Add(SectionHeader("LAST 20 LOOTED"));
            var recent = s.Loot.AsEnumerable().Reverse().Take(20).ToList();
            if (recent.Count == 0) { p.Children.Add(Hint("nothing looted yet")); return; }
            foreach (var l in recent)
            {
                Brush ic = l.IsMote ? Mote : l.IsCoin ? Gold : Dim;
                p.Children.Add(LootRow(l.Time.ToString("HH:mm:ss"), l.Text, ic, l.IsMote ? "✦" : l.IsCoin ? "🪙" : "•"));
            }
        }

        // ================= element builders =================
        private FrameworkElement SectionHeader(string t)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
            dp.Children.Add(new TextBlock { Text = t, Foreground = B("#8B8D92"), FontSize = 10, FontWeight = FontWeights.Bold });
            return dp;
        }

        private FrameworkElement Row(string left, string leftSub, string right, string rightSub,
            double frac, Brush accent, Brush rightBrush, bool selected = false,
            string? badge = null, Brush? badgeBrush = null, Action? onClick = null)
        {
            frac = Clamp(frac, 0.0001, 1.0);
            var barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - frac, GridUnitType.Star) });
            var bar = new System.Windows.Shapes.Rectangle { RadiusX = 6, RadiusY = 6, Fill = accent, Opacity = 0.16 };
            Grid.SetColumn(bar, 0); barGrid.Children.Add(bar);

            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            leftPanel.Children.Add(new TextBlock { Text = left, Foreground = Text, FontSize = 12.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            if (badge != null) leftPanel.Children.Add(Badge(badge, badgeBrush ?? accent));
            var leftStack = new StackPanel();
            leftStack.Children.Add(leftPanel);
            if (!string.IsNullOrEmpty(leftSub)) leftStack.Children.Add(new TextBlock { Text = leftSub, Foreground = Dim, FontSize = 10, Margin = new Thickness(0, 1, 0, 0) });

            var rl = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            rl.Children.Add(new TextBlock { Text = right, Foreground = rightBrush, FontSize = 15, FontWeight = FontWeights.Bold });
            if (!string.IsNullOrEmpty(rightSub)) rl.Children.Add(new TextBlock { Text = rightSub, Foreground = Dim, FontSize = 10, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom });
            var rightStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            rightStack.Children.Add(rl);

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(rightStack, Dock.Right);
            dock.Children.Add(rightStack);
            dock.Children.Add(leftStack);

            var content = new Grid();
            content.Children.Add(barGrid);
            content.Children.Add(dock);

            var host = new Border
            {
                CornerRadius = new CornerRadius(9), Background = RowBg,
                BorderBrush = selected ? RowStrokeSel : RowStroke, BorderThickness = new Thickness(selected ? 1.5 : 1),
                Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 7, 11, 7), ClipToBounds = true, Child = content
            };
            if (onClick != null) { host.Cursor = Cursors.Hand; host.MouseLeftButtonUp += (_, __) => onClick(); }
            return host;
        }

        private FrameworkElement Badge(string text, Brush color) => new Border
        {
            Margin = new Thickness(7, 0, 0, 0), Padding = new Thickness(5, 1, 5, 1), CornerRadius = new CornerRadius(10),
            Background = Tint(color, 0.12), BorderBrush = Tint(color, 0.35), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, Foreground = color, FontSize = 8.5, FontWeight = FontWeights.Bold }
        };

        private FrameworkElement StatBox(string k, string v, Brush vb)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = k.ToUpperInvariant(), Foreground = Dim, FontSize = 9 });
            sp.Children.Add(new TextBlock { Text = v, Foreground = vb, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 0) });
            return new Border { Width = 100, Margin = new Thickness(0, 0, 7, 7), Padding = new Thickness(9, 7, 9, 8), CornerRadius = new CornerRadius(9), Background = RowBg, BorderBrush = RowStroke, BorderThickness = new Thickness(1), Child = sp };
        }

        private FrameworkElement Chip(string k, string v, string sub, Brush vb)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = k, Foreground = Dim, FontSize = 9 });
            sp.Children.Add(new TextBlock { Text = v, Foreground = vb, FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 1, 0, 0) });
            if (!string.IsNullOrEmpty(sub)) sp.Children.Add(new TextBlock { Text = sub, Foreground = Dim, FontSize = 8.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 82, Margin = new Thickness(0, 1, 0, 0) });
            return new Border { Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(8), Background = RowBg, BorderBrush = RowStroke, BorderThickness = new Thickness(1), Child = sp };
        }

        private FrameworkElement LootRow(string time, string text, Brush ic, string icon)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var t = new TextBlock { Text = time, Foreground = Dim, FontSize = 10, Width = 54, VerticalAlignment = VerticalAlignment.Center };
            var i = new TextBlock { Text = icon, Foreground = ic, FontSize = 11, Width = 18, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var x = new TextBlock { Text = text, Foreground = Text, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(t, Dock.Left); DockPanel.SetDock(i, Dock.Left);
            dp.Children.Add(t); dp.Children.Add(i); dp.Children.Add(x);
            return new Border { Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(7), Background = B("#08FFFFFF"), Child = dp };
        }

        private FrameworkElement Hint(string t) => new TextBlock { Text = t, Foreground = Dim, FontSize = 11, Margin = new Thickness(2, 2, 0, 6) };

        private static (string, Brush) BadgeFor(DamageKind k) => k switch
        {
            DamageKind.Nuke => ("NUKE", Nuke),
            DamageKind.Dot => ("DOT", Dot),
            _ => ("MELEE", Melee),
        };

        private static Brush Tint(Brush b, double alpha)
        {
            var c = ((SolidColorBrush)b).Color;
            var nb = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B)); nb.Freeze(); return nb;
        }

        // ================= tabs =================
        private void Tab_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string tag) SelectTab(tag);
        }

        private void SelectTab(string tab)
        {
            _tab = tab;
            foreach (var (border, name) in new[] { (TabOverview, "Overview"), (TabBreakdown, "Breakdown"), (TabAvoidance, "Avoidance"), (TabEncounters, "Encounters"), (TabLoot, "Loot") })
            {
                bool on = name == tab;
                border.Background = on ? TabSel : Brushes.Transparent;
                if (border.Child is TextBlock tb) tb.Foreground = on ? Text : Dim;
            }
            OverviewPanel.Visibility = tab == "Overview" ? Visibility.Visible : Visibility.Collapsed;
            BreakdownPanel.Visibility = tab == "Breakdown" ? Visibility.Visible : Visibility.Collapsed;
            AvoidancePanel.Visibility = tab == "Avoidance" ? Visibility.Visible : Visibility.Collapsed;
            EncountersPanel.Visibility = tab == "Encounters" ? Visibility.Visible : Visibility.Collapsed;
            LootPanel.Visibility = tab == "Loot" ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        }

        // ================= buttons / window =================
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch { } }
        }

        private void BtnPick_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "EverQuest logs (eqlog_*.txt)|eqlog_*.txt|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Pick your EverQuest character log"
            };
            var guess = _settings.LastLogPath;
            if (!string.IsNullOrEmpty(guess) && File.Exists(guess)) dlg.InitialDirectory = Path.GetDirectoryName(guess);
            else foreach (var d in new[] { @"E:\EverQuest Legends\Logs", @"C:\EverQuest Legends\Logs" })
                    if (Directory.Exists(d)) { dlg.InitialDirectory = d; break; }
            if (dlg.ShowDialog() == true) { _selected = ""; _selEncStart = null; LoadLog(dlg.FileName); }
        }

        // ================= spell-data update =================
        private CancellationTokenSource? _spellCts;
        private bool _updatingSpells;
        private DispatcherTimer? _toastTimer;

        public bool IsUpdatingSpells => _updatingSpells;

        public async void RunSpellUpdate()
        {
            if (_updatingSpells) { try { _spellCts?.Cancel(); } catch { } return; }   // called again cancels
            _updatingSpells = true;
            _spellCts = new CancellationTokenSource();
            _toastTimer?.Stop();

            ShowToast("⬇", "Contacting the wiki…", AccentGold);
            ToastBar.Visibility = Visibility.Visible;
            ToastBar.IsIndeterminate = true;

            var progress = new Progress<ScrapeProgress>(p =>
            {
                if (p.Total > 0)
                {
                    ToastBar.IsIndeterminate = false;
                    ToastBar.Maximum = p.Total;
                    ToastBar.Value = p.Done;
                    ToastText.Text = $"{p.Phase}  {p.Done}/{p.Total}";
                }
                else ToastText.Text = p.Phase;
            });

            try
            {
                var rows = await SpellScraper.ScrapeAsync(progress, _spellCts.Token);
                if (rows.Count == 0) { ShowToastDone("✗", "No spells returned — try again later.", DmgIn); return; }

                await File.WriteAllTextAsync(SpellStore.SpellsPath, SpellScraper.ToJson(rows),
                    new System.Text.UTF8Encoding(false), _spellCts.Token);

                int self;
                lock (_lock) { self = SpellCatalog.ApplyRows(rows); }   // apply under the parser lock (no race with tailing)
                ShowToastDone("✓", $"Updated — {rows.Count} spells, {self} self-buffs", Heal);
            }
            catch (OperationCanceledException) { ShowToastDone("✗", "Spell update canceled.", Dim); }
            catch (Exception ex) { ShowToastDone("✗", "Update failed: " + Short(ex.Message), DmgIn); }
            finally
            {
                _updatingSpells = false;
                try { _spellCts?.Dispose(); } catch { }
                _spellCts = null;
                ToastBar.IsIndeterminate = false;
            }
        }

        private void ShowToast(string icon, string text, Brush accent)
        {
            ToastIcon.Text = icon; ToastIcon.Foreground = accent;
            ToastText.Text = text;
            ToastArea.Visibility = Visibility.Visible;
        }

        private void ShowToastDone(string icon, string text, Brush accent)
        {
            ShowToast(icon, text, accent);
            ToastBar.Visibility = Visibility.Collapsed;
            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _toastTimer.Tick += (_, __) => { _toastTimer!.Stop(); ToastArea.Visibility = Visibility.Collapsed; };
            _toastTimer.Start();
        }

        private static string Short(string s) => s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        private void BtnExpand_Click(object sender, RoutedEventArgs e) => ApplyExpanded(MaxPanel.Visibility != Visibility.Visible);

        // ================= settings window =================
        private SettingsWindow? _settingsWin;
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWin != null) { try { _settingsWin.Activate(); return; } catch { _settingsWin = null; } }
            _settingsWin = new SettingsWindow(this) { Owner = this };
            _settingsWin.Closed += (_, __) => { _settings.Save(); _settingsWin = null; };
            _settingsWin.Show();
        }

        // ---- surface the Settings window drives ----
        public Settings AppSettings => _settings;
        public double BackdropOpacity => Backdrop.Opacity;
        public bool ClickThroughOn => _settings.ClickThrough;
        public string CurrentLogPath => _settings.LastLogPath;
        public string SpellDataStatus => SpellStore.Status();

        public void SetBackdropOpacity(double v) { Backdrop.Opacity = Clamp(v, 0.12, 0.95); _settings.PanelAlpha = Backdrop.Opacity; }
        public void SetClickThrough(bool on) => ApplyClickThrough(on);
        public void SaveSettings() => _settings.Save();
        public void ResetSessionNow() { lock (_lock) { _stats.Reset(); } _selEncStart = null; }
        public void ResetLearnedBuffs() { _buffDur.Clear(); _buffCat.Clear(); BuffStore.Save(_buffDur, _buffCat); }
        public void PickLog() => BtnPick_Click(this, new RoutedEventArgs());

        public void SetPetName(string name)
        {
            _settings.PetName = (name ?? "").Trim();
            lock (_lock) { _stats.PetName = _settings.PetName; }
            CharLabel.Text = _settings.PlayerName + (string.IsNullOrEmpty(_settings.PetName) ? "" : "  +  " + _settings.PetName);
        }

        private void ApplyExpanded(bool expanded)
        {
            MaxPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            BiggestStrip.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            Width = expanded ? 470 : 340;
            _settings.Expanded = expanded;
        }

        private void BtnClickThru_Click(object sender, RoutedEventArgs e) => ToggleClickThrough();
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ================= click-through (Win32) =================
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 0xB001;
        private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002;
        private const uint VK_X = 0x58;

        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private void ToggleClickThrough() => ApplyClickThrough(!_settings.ClickThrough);

        private void ApplyClickThrough(bool on)
        {
            _settings.ClickThrough = on;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (on) ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
            else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(hwnd, GWL_EXSTYLE, ex);
            BtnClickThru.Foreground = on ? AccentGold : Dim;
            TitleBar.Opacity = on ? 0.55 : 1.0;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID) { ToggleClickThrough(); handled = true; }
            return IntPtr.Zero;
        }

        // ================= formatting =================
        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
        private static string FmtClock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
        private static string FmtHours(double hours)
        {
            var t = TimeSpan.FromHours(hours);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
        }
    }
}
