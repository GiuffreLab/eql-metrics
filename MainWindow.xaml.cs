using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
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
        private string _selected = "";        // combatant selected for the breakdown
        private HwndSource? _source;

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
        private static readonly Brush RowBg = B("#0DFFFFFF"), RowStroke = B("#22A0C0FF"), RowStrokeSel = B("#66A0C0FF");

        public MainWindow()
        {
            InitializeComponent();

            _settings = Settings.Load();
            Backdrop.Opacity = Clamp(_settings.PanelAlpha, 0.12, 0.95);
            _selected = string.IsNullOrEmpty(_settings.PlayerName) ? "" : _settings.PlayerName;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _timer.Tick += (_, __) => Refresh();

            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        // ============ startup / shutdown ============
        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            Left = _settings.Left; Top = _settings.Top;
            ApplyExpanded(_settings.Expanded);

            string path = _settings.LastLogPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                path = AutoDetectLog();

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                LoadLog(path);
            else
                StatusText.Text = "click the folder icon to pick your log";

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
            try { _tailer?.Stop(); } catch { }
            try { UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID); } catch { }
        }

        private static string AutoDetectLog()
        {
            foreach (var dir in new[]
            {
                @"E:\EverQuest Legends\Logs",
                @"C:\EverQuest Legends\Logs",
                @"C:\Program Files\EverQuest Legends\Logs",
                @"C:\Program Files (x86)\EverQuest Legends\Logs",
            })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    var newest = new DirectoryInfo(dir).GetFiles("eqlog_*.txt")
                        .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
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
            }

            CharLabel.Text = player + (string.IsNullOrEmpty(_settings.PetName) ? "" : "  +  " + _settings.PetName);
            StatusText.Text = Path.GetFileName(path);

            _tailer = new LogTailer(path, _settings.FollowFromStart, OnLine);
            _tailer.Start();
        }

        private void OnLine(string line)
        {
            lock (_lock) { _stats.Apply(line); }   // runs on the tailer thread
        }

        private static string? DerivePlayerName(string path)
        {
            var m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"^eqlog_(?<name>[^_]+)_", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups["name"].Value : null;
        }

        // ============ refresh loop ============
        private void Refresh()
        {
            lock (_lock)
            {
                var s = _stats;
                if (!s.FirstTime.HasValue)
                {
                    BigDps.Text = "0";
                    return;
                }

                var player = s.Player;
                var pet = s.Pet;
                double encCombined = (player != null ? s.DpsEncounter(player.Name) : 0)
                                   + (pet != null ? s.DpsEncounter(pet.Name) : 0);

                BigDps.Text = Math.Round(encCombined).ToString("0");
                string youPart = player != null ? $"You {s.DpsEncounter(player.Name):0}" : "";
                DpsSplit.Text = pet != null
                    ? $"{youPart}  ·  Pet {s.DpsEncounter(pet.Name):0}"
                    : (player != null ? $"{youPart}  ·  session {s.PlayerDps:0.0}" : "");

                EncounterName.Text = s.EncounterActive && s.LastTarget.Length > 0 ? "vs " + s.LastTarget
                    : (s.LastTarget.Length > 0 ? "last: " + s.LastTarget : "no fight yet");
                EncounterTime.Text = s.EncounterActive ? "⚔ " + FmtClock(s.EncounterSeconds) : "idle";
                SessionTimer.Text = FmtClock(s.SessionSeconds);

                BuildMiniRates(s);

                if (DetailPanel.Visibility == Visibility.Visible)
                {
                    BuildCombatants(s);
                    BuildBreakdown(s);
                    BuildSessionStats(s);
                    BuildLoot(s);
                }
            }
        }

        private void BuildMiniRates(SessionStats s)
        {
            MiniRates.Children.Clear();
            MiniRates.Children.Add(MiniRate("HPS", s.Hps.ToString("0"), Heal));
            MiniRates.Children.Add(MiniRate("MOTES/HR", s.MotesPerHour.ToString("0"), Mote));
            MiniRates.Children.Add(MiniRate("COIN/HR", s.CoinPerHour.ToString("0.0") + "p", Gold));
            MiniRates.Children.Add(MiniRate("XP/HR", s.XpPerHour.ToString("0.0") + "%", Xp));
        }

        private void BuildCombatants(SessionStats s)
        {
            CombatantList.Children.Clear();
            var list = s.Combatants.OrderByDescending(c => c.TotalDamage).ToList();
            if (list.Count == 0) { CombatantList.Children.Add(Hint("no combat recorded yet")); return; }
            long top = Math.Max(1, list[0].TotalDamage);
            long grand = Math.Max(1, list.Sum(c => c.TotalDamage));

            foreach (var c in list)
            {
                Brush accent = c.IsPlayer ? You : c.IsPet ? Pet : Grp;
                Brush accentT = c.IsPlayer ? YouT : c.IsPet ? PetT : GrpT;
                string role = c.IsPlayer ? "you" : c.IsPet ? "pet" : "group";
                double frac = (double)c.TotalDamage / top;
                string dps = s.DpsSession(c).ToString("0.0");
                string pct = (100.0 * c.TotalDamage / grand).ToString("0") + "%";

                string name = c.Name;
                CombatantList.Children.Add(Row(
                    left: name, leftSub: role, right: dps, rightSub: pct,
                    frac: frac, accent: accent, rightBrush: accentT,
                    selected: string.Equals(name, _selected, StringComparison.OrdinalIgnoreCase),
                    onClick: () => { _selected = name; Refresh(); }));
            }
        }

        private void BuildBreakdown(SessionStats s)
        {
            AbilityList.Children.Clear();
            var c = s.Combatants.FirstOrDefault(x => string.Equals(x.Name, _selected, StringComparison.OrdinalIgnoreCase))
                    ?? s.Player ?? s.Combatants.FirstOrDefault();
            if (c == null) { BreakdownWho.Text = ""; AbilityList.Children.Add(Hint("no data")); return; }
            BreakdownWho.Text = c.Name;

            var abilities = c.AbilitiesByDamage.Where(a => a.Total > 0).ToList();
            if (abilities.Count == 0) { AbilityList.Children.Add(Hint("no damage yet")); return; }
            long top = Math.Max(1, abilities[0].Total);
            long tot = Math.Max(1, c.TotalDamage);

            foreach (var a in abilities)
            {
                (string bt, Brush bc) = a.Kind switch
                {
                    DamageKind.Nuke => ("NUKE", Nuke),
                    DamageKind.Dot => ("DOT", Dot),
                    _ => ("MELEE", Melee),
                };
                double dps = a.Total / s.SessionSeconds;
                string sub = $"x{a.Hits}   avg {a.Avg:0.0}   max {a.Max}" + (a.Misses > 0 ? $"   miss {a.MissPct:0}%" : "");
                AbilityList.Children.Add(Row(
                    left: a.Name, leftSub: sub, right: dps.ToString("0.0"),
                    rightSub: (100.0 * a.Total / tot).ToString("0") + "%",
                    frac: (double)a.Total / top, accent: bc, rightBrush: bc,
                    badge: bt, badgeBrush: bc));
            }
        }

        private void BuildSessionStats(SessionStats s)
        {
            SessionStatsPanel.Children.Clear();
            long bestHit = s.Combatants.Where(c => c.IsPlayer)
                .SelectMany(c => c.Abilities.Values).Select(a => a.Max).DefaultIfEmpty(0).Max();

            void Add(string k, string v, Brush vb) => SessionStatsPanel.Children.Add(StatBox(k, v, vb));
            Add("Session DPS", s.PlayerDps.ToString("0.0"), YouT);
            Add("HPS", s.Hps.ToString("0"), Heal);
            Add("Kills/hr", s.KillsPerHour.ToString("0"), Text);
            Add("XP/hr", s.XpPerHour.ToString("0.0") + "%", Xp);
            Add("To level", s.HoursToLevel.HasValue ? FmtHours(s.HoursToLevel.Value) : "—", Text);
            Add("Dmg taken/hr", FmtNum(s.DamageTakenPerHour), DmgIn);
            Add("Coin/hr", s.CoinPerHour.ToString("0.0") + "p", Gold);
            Add("Motes/hr", s.MotesPerHour.ToString("0"), Mote);
            Add("Best hit", bestHit.ToString("0"), Text);
        }

        private void BuildLoot(SessionStats s)
        {
            LootList.Children.Clear();
            var recent = s.Loot.AsEnumerable().Reverse().Take(6).ToList();
            if (recent.Count == 0) { LootList.Children.Add(Hint("nothing looted yet")); return; }
            foreach (var l in recent)
            {
                Brush ic = l.IsMote ? Mote : l.IsCoin ? Gold : Dim;
                LootList.Children.Add(LootRow(l.Time.ToString("HH:mm:ss"), l.Text, ic, l.IsMote ? "✦" : l.IsCoin ? "🪙" : "•"));
            }
        }

        // ============ element builders ============
        private FrameworkElement Row(string left, string leftSub, string right, string rightSub,
            double frac, Brush accent, Brush rightBrush, bool selected = false,
            string? badge = null, Brush? badgeBrush = null, Action? onClick = null)
        {
            frac = Clamp(frac, 0.0001, 1.0);

            var barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - frac, GridUnitType.Star) });
            var bar = new Rectangle { RadiusX = 6, RadiusY = 6, Fill = accent, Opacity = 0.16 };
            Grid.SetColumn(bar, 0);
            barGrid.Children.Add(bar);

            // left content
            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            leftPanel.Children.Add(new TextBlock { Text = left, Foreground = Text, FontSize = 12.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            if (badge != null)
                leftPanel.Children.Add(Badge(badge, badgeBrush ?? accent));
            var leftStack = new StackPanel();
            leftStack.Children.Add(leftPanel);
            if (!string.IsNullOrEmpty(leftSub))
                leftStack.Children.Add(new TextBlock { Text = leftSub, Foreground = Dim, FontSize = 10, Margin = new Thickness(0, 1, 0, 0) });

            // right content
            var rightStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var rl = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            rl.Children.Add(new TextBlock { Text = right, Foreground = rightBrush, FontSize = 15, FontWeight = FontWeights.Bold });
            if (!string.IsNullOrEmpty(rightSub))
                rl.Children.Add(new TextBlock { Text = rightSub, Foreground = Dim, FontSize = 10, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom });
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
                CornerRadius = new CornerRadius(9),
                Background = RowBg,
                BorderBrush = selected ? RowStrokeSel : RowStroke,
                BorderThickness = new Thickness(selected ? 1.5 : 1),
                Margin = new Thickness(0, 0, 0, 7),
                Padding = new Thickness(10, 7, 11, 7),
                ClipToBounds = true,
                Child = content
            };
            if (onClick != null)
            {
                host.Cursor = Cursors.Hand;
                host.MouseLeftButtonUp += (_, __) => onClick();
            }
            return host;
        }

        private FrameworkElement Badge(string text, Brush color)
        {
            return new Border
            {
                Margin = new Thickness(7, 0, 0, 0),
                Padding = new Thickness(5, 1, 5, 1),
                CornerRadius = new CornerRadius(10),
                Background = Tint(color, 0.12),
                BorderBrush = Tint(color, 0.35),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, Foreground = color, FontSize = 8.5, FontWeight = FontWeights.Bold }
            };
        }

        private FrameworkElement StatBox(string k, string v, Brush vb)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = k.ToUpperInvariant(), Foreground = Dim, FontSize = 9 });
            sp.Children.Add(new TextBlock { Text = v, Foreground = vb, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 0) });
            return new Border
            {
                Width = 122,
                Margin = new Thickness(0, 0, 7, 7),
                Padding = new Thickness(9, 7, 9, 8),
                CornerRadius = new CornerRadius(9),
                Background = RowBg,
                BorderBrush = RowStroke,
                BorderThickness = new Thickness(1),
                Child = sp
            };
        }

        private FrameworkElement LootRow(string time, string text, Brush ic, string icon)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var t = new TextBlock { Text = time, Foreground = Dim, FontSize = 10, Width = 54, VerticalAlignment = VerticalAlignment.Center };
            var i = new TextBlock { Text = icon, Foreground = ic, FontSize = 11, Width = 18, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var x = new TextBlock { Text = text, Foreground = Text, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(t, Dock.Left);
            DockPanel.SetDock(i, Dock.Left);
            dp.Children.Add(t); dp.Children.Add(i); dp.Children.Add(x);
            return new Border
            {
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 0, 3),
                CornerRadius = new CornerRadius(7),
                Background = B("#08FFFFFF"),
                Child = dp
            };
        }

        private FrameworkElement MiniRate(string k, string v, Brush vb)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = k, Foreground = Dim, FontSize = 9 });
            sp.Children.Add(new TextBlock { Text = v, Foreground = vb, FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 1, 0, 0) });
            return new Border
            {
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(8),
                Background = RowBg,
                BorderBrush = RowStroke,
                BorderThickness = new Thickness(1),
                Child = sp
            };
        }

        private FrameworkElement Hint(string t) =>
            new TextBlock { Text = t, Foreground = Dim, FontSize = 11, Margin = new Thickness(2, 2, 0, 6) };

        private static Brush Tint(Brush b, double alpha)
        {
            var c = ((SolidColorBrush)b).Color;
            var nb = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
            nb.Freeze(); return nb;
        }

        // ============ buttons / window ============
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

            if (dlg.ShowDialog() == true) { _selected = ""; LoadLog(dlg.FileName); }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            lock (_lock) { _stats.Reset(); }
        }

        private void BtnDimDown_Click(object sender, RoutedEventArgs e) => Backdrop.Opacity = Clamp(Backdrop.Opacity - 0.08, 0.12, 0.95);
        private void BtnDimUp_Click(object sender, RoutedEventArgs e) => Backdrop.Opacity = Clamp(Backdrop.Opacity + 0.08, 0.12, 0.95);

        private void BtnExpand_Click(object sender, RoutedEventArgs e) => ApplyExpanded(DetailPanel.Visibility != Visibility.Visible);

        private void ApplyExpanded(bool expanded)
        {
            DetailPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            Width = expanded ? 448 : 330;
            _settings.Expanded = expanded;
        }

        private void BtnClickThru_Click(object sender, RoutedEventArgs e) => ToggleClickThrough();
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ============ click-through (Win32) ============
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
            BtnClickThru.Foreground = on ? You : Dim;
            // when click-through is on you can't press buttons; opacity nudges so you know it's active
            TitleBar.Opacity = on ? 0.55 : 1.0;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID) { ToggleClickThrough(); handled = true; }
            return IntPtr.Zero;
        }

        // ============ formatting ============
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

        private static string FmtNum(double n)
        {
            if (n >= 1_000_000) return (n / 1_000_000).ToString("0.0") + "M";
            if (n >= 1_000) return (n / 1_000).ToString("0.0") + "k";
            return n.ToString("0");
        }
    }
}
