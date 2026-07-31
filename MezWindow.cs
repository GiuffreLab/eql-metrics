using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace EqlMetrics
{
    /// <summary>
    /// A small, movable, transparent overlay that lists active mez (crowd-control) count-up timers, themed to
    /// match the main HUD (charcoal backdrop + gold accent). Auto-shows when something is mezzed and hides when
    /// nothing is. Borderless, topmost, and WS_EX_NOACTIVATE so it never steals focus/taskbar; drag the title
    /// bar to move it (the caller persists its position).
    /// </summary>
    public sealed class MezWindow : Window
    {
        private static readonly Brush Text = EqlUi.Text, Dim = EqlUi.Dim, Gold = EqlUi.Gold;
        private static readonly Brush Grp = EqlUi.Grp, DmgIn = EqlUi.DmgIn;
        private static readonly Brush RowBg = EqlUi.B("#0DFFFFFF"), RowStroke = EqlUi.B("#2A313B");

        private readonly StackPanel _list;
        private readonly TextBlock _count;
        private readonly SolidColorBrush _backdrop;

        public MezWindow(double backdropAlpha)
        {
            Title = "EQL Mez";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;   // fixed width, height grows with the number of mez rows
            FontFamily = new FontFamily("Segoe UI");
            Width = 238;
            MinHeight = 44;

            _backdrop = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x11)) { Opacity = EqlUi.Clamp(backdropAlpha, 0.12, 0.95) };
            var root = new Border { CornerRadius = new CornerRadius(13), BorderBrush = EqlUi.B("#4A515B"), BorderThickness = new Thickness(1), Background = _backdrop };
            var shell = new StackPanel();

            // draggable title bar (matches the main overlay's gold-accented header)
            var titleBar = new Border
            {
                CornerRadius = new CornerRadius(12, 12, 0, 0),
                Padding = new Thickness(9, 6, 9, 6),
                BorderBrush = EqlUi.B("#55E0B020"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background = TitleGrad()
            };
            var td = new DockPanel();
            td.Children.Add(new TextBlock { Text = "◆", Foreground = Gold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            td.Children.Add(new TextBlock { Text = "MEZ", Foreground = Text, FontWeight = FontWeights.Bold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            _count = new TextBlock { Text = "", Foreground = Dim, FontSize = 11, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            td.Children.Add(_count);
            titleBar.Child = td;
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch { } } };
            shell.Children.Add(titleBar);

            _list = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
            shell.Children.Add(_list);

            root.Child = shell;
            Content = root;
        }

        private static Brush TitleGrad()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#18FFFFFF"), 0));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#04FFFFFF"), 1));
            g.Freeze();
            return g;
        }

        /// <summary>Keep the backdrop opacity in sync with the main overlay's transparency setting.</summary>
        public void SetBackdropAlpha(double a) => _backdrop.Opacity = EqlUi.Clamp(a, 0.12, 0.95);

        /// <summary>Rebuild the list from the current mez timers (longest-held first is the caller's job).</summary>
        public void Update(IReadOnlyList<(string target, double heldSec, double expectedSec)> mez)
        {
            _list.Children.Clear();
            _count.Text = mez.Count > 0 ? mez.Count.ToString() : "";
            foreach (var m in mez) _list.Children.Add(Bar(m));
        }

        private FrameworkElement Bar((string target, double heldSec, double expectedSec) m)
        {
            Brush c = MezBrush(m.heldSec, m.expectedSec);
            string who = m.target.Length > 18 ? m.target.Substring(0, 17) + "…" : m.target;
            string sub = m.expectedSec > 0 ? "/ " + EqlUi.FmtClock(m.expectedSec) : "held";

            var dock = new DockPanel { LastChildFill = false };
            var lab = new TextBlock
            {
                Text = who, Foreground = Text, FontSize = 12, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            };
            DockPanel.SetDock(lab, Dock.Left);
            var rp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 11, 0) };
            rp.Children.Add(new TextBlock { Text = EqlUi.FmtClock(m.heldSec), Foreground = c, FontSize = 15, FontWeight = FontWeights.Bold });
            rp.Children.Add(new TextBlock { Text = sub, Foreground = Dim, FontSize = 9, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(5, 0, 0, 1) });
            DockPanel.SetDock(rp, Dock.Right);
            dock.Children.Add(lab);
            dock.Children.Add(rp);

            return new Border
            {
                Height = 32, CornerRadius = new CornerRadius(9), Background = RowBg, BorderBrush = RowStroke, BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 6), Child = dock
            };
        }

        // age cue: warm toward red as held approaches the expected wear-off (about to break); else fixed thresholds
        private static Brush MezBrush(double held, double expected)
        {
            if (expected > 0) { double f = held / expected; return f < 0.6 ? Grp : f < 0.9 ? Gold : DmgIn; }
            return held < 25 ? Grp : held < 50 ? Gold : DmgIn;
        }

        // keep it off the taskbar/alt-tab (a tool window); ShowActivated=false already stops it stealing focus when
        // it pops up. We leave it activatable so the title-bar DragMove is reliable.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var h = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        }
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
