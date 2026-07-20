using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace EqlMetrics
{
    /// <summary>
    /// Full-height, transparent, click-through overlay for all transient alerts. Each notification
    /// spawns near screen-center and floats upward, fading out near the top — up to 3 rising at once
    /// (extras queue). Borderless / topmost / WS_EX_NOACTIVATE so it never steals focus or clicks from
    /// the game. (File named StealthFlash.cs for git history; the class is CenterFlash.)
    /// </summary>
    public sealed class CenterFlash : Window
    {
        public readonly struct Item
        {
            public Item(string icon, string title, string subtitle, Color accent)
            { Icon = icon; Title = title; Subtitle = subtitle; Accent = accent; }
            public readonly string Icon, Title, Subtitle;
            public readonly Color Accent;
        }

        private sealed class Toast { public FrameworkElement El = null!; public DateTime Start; }

        private readonly Canvas _canvas = new();
        private readonly Queue<Item> _queue = new();
        private readonly List<Toast> _active = new();
        private readonly DispatcherTimer _pump;

        private const double W = 760;          // window / column width
        private const int Duration = 2600;     // ms: full center -> top travel (incl. fade)
        private const int FadeIn = 150;
        private const int FadeOut = 560;
        private const double SpawnGapMs = 700; // min spacing between successive toasts (keeps them ~a row apart)
        private const int MaxActive = 3;       // at most 3 rising at once
        private const int MaxQueue = 6;        // drop oldest waiting toast if a fight floods us

        public CenterFlash()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            SizeToContent = SizeToContent.Manual;
            FontFamily = new FontFamily("Segoe UI");
            Width = W;
            Content = _canvas;

            _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
            _pump.Tick += (_, __) => Pump();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var h = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }

        /// <summary>Queue a floating notification. (holdMs kept for call-site compatibility; the lifetime is the fixed rise time.)</summary>
        public void Flash(string icon, string title, string subtitle, Color accent, int holdMs = 0)
        {
            if (_queue.Count >= MaxQueue) _queue.Dequeue();   // flooded — drop the oldest waiting one
            _queue.Enqueue(new Item(icon, title, subtitle, accent));
            EnsureShown();
            if (!_pump.IsEnabled) _pump.Start();
            Pump();
        }

        private void EnsureShown()
        {
            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
            Left = (sw - W) / 2; Top = 0; Height = sh;
            _canvas.Width = W; _canvas.Height = sh;
            if (!IsVisible) Show();
        }

        private void Pump()
        {
            var now = DateTime.Now;

            for (int i = _active.Count - 1; i >= 0; i--)   // retire finished toasts
                if ((now - _active[i].Start).TotalMilliseconds >= Duration)
                {
                    _canvas.Children.Remove(_active[i].El);
                    _active.RemoveAt(i);
                }

            // spawn while there's room and the last one has risen far enough to leave a gap
            while (_queue.Count > 0 && _active.Count < MaxActive &&
                   (_active.Count == 0 || (now - _active[_active.Count - 1].Start).TotalMilliseconds >= SpawnGapMs))
                Spawn(_queue.Dequeue(), now);

            if (_queue.Count == 0 && _active.Count == 0)
            {
                _pump.Stop();
                if (IsVisible) Hide();
            }
        }

        private void Spawn(Item item, DateTime now)
        {
            double sh = _canvas.Height > 0 ? _canvas.Height : SystemParameters.PrimaryScreenHeight;
            double startY = sh * 0.46, endY = sh * 0.10;   // center -> near top

            var pill = BuildPill(item);
            pill.HorizontalAlignment = HorizontalAlignment.Center;
            var host = new Grid { Width = W };
            host.Children.Add(pill);

            _canvas.Children.Add(host);
            Canvas.SetLeft(host, 0);
            Canvas.SetTop(host, startY);

            host.BeginAnimation(Canvas.TopProperty, new DoubleAnimation
            {
                From = startY, To = endY, Duration = TimeSpan.FromMilliseconds(Duration),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });

            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(FadeIn))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Duration - FadeOut))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Duration))));
            host.BeginAnimation(OpacityProperty, fade);

            _active.Add(new Toast { El = host, Start = now });
        }

        private static Border BuildPill(Item item)
        {
            var ab = new SolidColorBrush(item.Accent);
            double tf = item.Title.Length > 16 ? 34 : item.Title.Length > 11 ? 40 : 46;
            DropShadowEffect Shadow() => new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Color = Colors.Black, Opacity = 0.9 };

            var icon = new TextBlock
            {
                Text = item.Icon ?? "", FontWeight = FontWeights.Bold, Foreground = ab, FontSize = tf * 0.8,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Effect = Shadow(),
                Visibility = string.IsNullOrEmpty(item.Icon) ? Visibility.Collapsed : Visibility.Visible
            };
            var title = new TextBlock
            {
                Text = item.Title, FontWeight = FontWeights.Bold, Foreground = ab, FontSize = tf,
                VerticalAlignment = VerticalAlignment.Center, Effect = Shadow()
            };
            var sub = new TextBlock
            {
                Text = item.Subtitle, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF7)), FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0), Opacity = 0.9
            };

            var line1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            line1.Children.Add(icon);
            line1.Children.Add(title);
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(line1);
            col.Children.Add(sub);

            return new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x0D, 0x11)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, item.Accent.R, item.Accent.G, item.Accent.B)),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(34, 16, 34, 18),
                Child = col
            };
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
