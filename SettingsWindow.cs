using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EqlMetrics.Core;

namespace EqlMetrics
{
    /// <summary>
    /// Standalone settings window (opened by the gear icon). Reads/writes the shared
    /// Settings object on MainWindow and applies changes live; MainWindow persists on close.
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        private readonly MainWindow _m;
        private readonly Settings _s;
        private TextBlock _spellStatus = null!;

        private static SolidColorBrush B(string h) { var c = (Color)ColorConverter.ConvertFromString(h); var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private static readonly Brush Bg = B("#0B0D11"), Stroke = B("#2A313B"), Dim = B("#8A97AB"), Text = B("#E8EEF7"),
            Accent = B("#C9A24B"), Green = B("#57D6A6"), Red = B("#FF7A7A");

        public SettingsWindow(MainWindow m)
        {
            _m = m; _s = m.AppSettings;
            Title = "EQL Metrics — Settings";
            Width = 460; Height = 680; MinWidth = 380; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Bg; Foreground = Text; FontFamily = new FontFamily("Segoe UI");
            ResizeMode = ResizeMode.CanResize;

            var root = new StackPanel { Margin = new Thickness(20, 16, 20, 22) };
            root.Children.Add(new TextBlock { Text = "Settings", Foreground = Text, FontSize = 19, FontWeight = FontWeights.Bold });

            root.Children.Add(Section("OVERLAY"));
            root.Children.Add(SliderRow("Transparency", 0.12, 0.95, _m.BackdropOpacity, v => _m.SetBackdropOpacity(v), pctOfRange: true));
            root.Children.Add(Toggle("Click-through (Ctrl+Alt+X)", _s.ClickThrough, on => _m.SetClickThrough(on)));

            root.Children.Add(Section("NOTIFICATIONS"));
            root.Children.Add(Toggle("All notifications", _s.NotifMaster, on => { _s.NotifMaster = on; Save(); }));
            root.Children.Add(Toggle("Buffs & debuffs", _s.NotifBuffs, on => { _s.NotifBuffs = on; Save(); }));
            root.Children.Add(Toggle("Hide / sneak", _s.NotifStealth, on => { _s.NotifStealth = on; Save(); }));
            root.Children.Add(Toggle("Skill pop-ups (backstab / kick / strike / cleave)", _s.NotifSkills, on => { _s.NotifSkills = on; Save(); }));
            root.Children.Add(Toggle("Quick Buff ready", _s.NotifQuickBuff, on => { _s.NotifQuickBuff = on; Save(); }));
            root.Children.Add(Toggle("Mend", _s.NotifMend, on => { _s.NotifMend = on; Save(); }));
            root.Children.Add(SliderRow("Max on screen", 1, 5, _s.NotifMaxOnScreen, v => { _s.NotifMaxOnScreen = (int)Math.Round(v); Save(); }, whole: true));

            root.Children.Add(Section("CHARACTER & PARSE"));
            root.Children.Add(TextRow("Pet name", _s.PetName, val => _m.SetPetName(val)));
            root.Children.Add(Toggle("Read whole log on load (else live only)", _s.FollowFromStart, on => { _s.FollowFromStart = on; Save(); }));
            root.Children.Add(LogRow());

            root.Children.Add(Section("SPELL DATA"));
            _spellStatus = new TextBlock { Text = _m.SpellDataStatus, Foreground = Dim, FontSize = 11, Margin = new Thickness(2, 0, 0, 7), TextWrapping = TextWrapping.Wrap };
            root.Children.Add(_spellStatus);
            root.Children.Add(ActionBtn("Update spell data now", Accent, () =>
            {
                _m.RunSpellUpdate();
                _spellStatus.Text = "updating… progress shows on the overlay";
            }));

            root.Children.Add(Section("RESET"));
            root.Children.Add(ActionBtn("Reset session stats", Red, () => _m.ResetSessionNow()));
            root.Children.Add(ActionBtn("Reset learned buff timers", Red, () => _m.ResetLearnedBuffs()));

            root.Children.Add(new TextBlock { Text = "changes save automatically", Foreground = Dim, FontSize = 10, Margin = new Thickness(2, 14, 0, 0) });

            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root, Background = Bg };
        }

        private void Save() => _m.SaveSettings();

        // ---------- control builders ----------
        private FrameworkElement Section(string t) => new TextBlock
        { Text = t, Foreground = Accent, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 18, 0, 8) };

        private FrameworkElement Toggle(string label, bool initial, Action<bool> onChange)
        {
            bool state = initial;
            var check = new TextBlock { Text = "✓", Foreground = B("#0B0D11"), FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = state ? Visibility.Visible : Visibility.Collapsed };
            var box = new Border { Width = 19, Height = 19, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center, Child = check };
            void Paint() { box.BorderBrush = state ? Green : Stroke; box.Background = state ? Tint(Green, 0.55) : Brushes.Transparent; check.Visibility = state ? Visibility.Visible : Visibility.Collapsed; }
            Paint();

            var lbl = new TextBlock { Text = label, Foreground = Text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9), Cursor = Cursors.Hand };
            row.Children.Add(box); row.Children.Add(lbl);
            row.MouseLeftButtonUp += (_, __) => { state = !state; Paint(); onChange(state); };
            return row;
        }

        private FrameworkElement SliderRow(string label, double min, double max, double init, Action<double> onChange, bool whole = false, bool pctOfRange = false)
        {
            string Fmt(double v) => whole ? ((int)Math.Round(v)).ToString() : pctOfRange ? $"{100.0 * (v - min) / (max - min):0}%" : v.ToString("0.00");
            var val = new TextBlock { Text = Fmt(init), Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Right };
            var slider = new Slider { Minimum = min, Maximum = max, Value = init, IsSnapToTickEnabled = whole, TickFrequency = whole ? 1 : 0.01, Width = 190, VerticalAlignment = VerticalAlignment.Center, Foreground = Accent };
            slider.ValueChanged += (_, e) => { val.Text = Fmt(e.NewValue); onChange(e.NewValue); };
            var lbl = new TextBlock { Text = label, Foreground = Text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Width = 120 };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            DockPanel.SetDock(lbl, Dock.Left); DockPanel.SetDock(val, Dock.Right);
            row.Children.Add(lbl); row.Children.Add(val); row.Children.Add(slider);
            return row;
        }

        private FrameworkElement TextRow(string label, string init, Action<string> onCommit)
        {
            var tb = new TextBox { Text = init, Width = 190, Background = B("#10FFFFFF"), Foreground = Text, BorderBrush = Stroke, BorderThickness = new Thickness(1), Padding = new Thickness(6, 3, 6, 3), VerticalAlignment = VerticalAlignment.Center, CaretBrush = Text };
            tb.LostFocus += (_, __) => onCommit(tb.Text);
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) onCommit(tb.Text); };
            var lbl = new TextBlock { Text = label, Foreground = Text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Width = 120 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
            row.Children.Add(lbl); row.Children.Add(tb);
            return row;
        }

        private FrameworkElement LogRow()
        {
            string Name() => string.IsNullOrEmpty(_m.CurrentLogPath) ? "(none)" : System.IO.Path.GetFileName(_m.CurrentLogPath);
            var path = new TextBlock { Text = Name(), Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210 };
            var btn = ActionBtnInline("Change…", Accent, () => { _m.PickLog(); path.Text = Name(); });
            var lbl = new TextBlock { Text = "Log file", Foreground = Text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Width = 120 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
            row.Children.Add(lbl); row.Children.Add(btn); row.Children.Add(new TextBlock { Width = 8 }); row.Children.Add(path);
            return row;
        }

        private FrameworkElement ActionBtn(string label, Brush accent, Action onClick)
        {
            var b = ActionBtnInline(label, accent, onClick);
            b.Margin = new Thickness(0, 0, 0, 8);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            return b;
        }

        private Border ActionBtnInline(string label, Brush accent, Action onClick)
        {
            var host = new Border
            {
                CornerRadius = new CornerRadius(8), Background = Tint(accent, 0.14), BorderBrush = Tint(accent, 0.5), BorderThickness = new Thickness(1),
                Padding = new Thickness(13, 6, 13, 6), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = label, Foreground = accent, FontSize = 12, FontWeight = FontWeights.SemiBold }
            };
            host.MouseLeftButtonUp += (_, __) => onClick();
            return host;
        }

        private static Brush Tint(Brush b, double a) { var c = ((SolidColorBrush)b).Color; var nb = new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B)); nb.Freeze(); return nb; }
    }
}
