using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqlMetrics.Core;

namespace EqlMetrics
{
    /// <summary>Shared dark-overlay UI building blocks (used by the detail window).</summary>
    public static class EqlUi
    {
        public static SolidColorBrush B(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c); b.Freeze(); return b;
        }
        public static readonly Brush Dim = B("#8A97AB");
        public static readonly Brush Text = B("#E8EEF7");
        public static readonly Brush You = B("#5AA9FF"), YouT = B("#BCDCFF");
        public static readonly Brush Pet = B("#B48BFF"), PetT = B("#D8C6FF");
        public static readonly Brush Grp = B("#57D6A6"), GrpT = B("#B6F0D8");
        public static readonly Brush Nuke = B("#FF9F5A"), Dot = B("#C98BFF"), Melee = B("#5AD6C4");
        public static readonly Brush Gold = B("#F4C85B"), Mote = B("#8BE0FF"), Heal = B("#57D6A6"), Xp = B("#C9A6FF"), DmgIn = B("#FF7A7A");
        public static readonly Brush RowBg = B("#12FFFFFF"), RowStroke = B("#2A313B");

        public static (string, Brush) BadgeFor(DamageKind k) => k switch
        {
            DamageKind.Nuke => ("NUKE", Nuke),
            DamageKind.Dot => ("DOT", Dot),
            _ => ("MELEE", Melee),
        };

        public static Brush Tint(Brush b, double alpha)
        {
            var c = ((SolidColorBrush)b).Color;
            var nb = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B)); nb.Freeze(); return nb;
        }

        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        public static string FmtClock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        public static FrameworkElement Section(string t) => new TextBlock
        {
            Text = t, Foreground = B("#8B8D92"), FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 7)
        };

        public static FrameworkElement Sub(string t, Brush color) => new TextBlock
        {
            Text = t, Foreground = color, FontSize = 12.5, FontWeight = FontWeights.Bold, Margin = new Thickness(2, 8, 0, 5)
        };

        public static FrameworkElement Hint(string t) => new TextBlock { Text = t, Foreground = Dim, FontSize = 11, Margin = new Thickness(2, 2, 0, 6) };

        public static FrameworkElement Badge(string text, Brush color) => new Border
        {
            Margin = new Thickness(7, 0, 0, 0), Padding = new Thickness(5, 1, 5, 1), CornerRadius = new CornerRadius(10),
            Background = Tint(color, 0.12), BorderBrush = Tint(color, 0.35), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, Foreground = color, FontSize = 8.5, FontWeight = FontWeights.Bold }
        };

        public static FrameworkElement StatBox(string k, string v, Brush vb) => new Border
        {
            Width = 118, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 8, 10, 9), CornerRadius = new CornerRadius(9),
            Background = RowBg, BorderBrush = RowStroke, BorderThickness = new Thickness(1),
            Child = Stack(
                new TextBlock { Text = k.ToUpperInvariant(), Foreground = Dim, FontSize = 9 },
                new TextBlock { Text = v, Foreground = vb, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 0) })
        };

        private static StackPanel Stack(params UIElement[] kids)
        {
            var sp = new StackPanel();
            foreach (var k in kids) sp.Children.Add(k);
            return sp;
        }

        public static FrameworkElement Row(string left, string leftSub, string right, string rightSub,
            double frac, Brush accent, Brush rightBrush, string? badge = null, Brush? badgeBrush = null)
        {
            frac = Clamp(frac, 0.0001, 1.0);
            var barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - frac, GridUnitType.Star) });
            var bar = new System.Windows.Shapes.Rectangle { RadiusX = 6, RadiusY = 6, Fill = accent, Opacity = 0.16 };
            Grid.SetColumn(bar, 0); barGrid.Children.Add(bar);

            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            leftPanel.Children.Add(new TextBlock { Text = left, Foreground = Text, FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            if (badge != null) leftPanel.Children.Add(Badge(badge, badgeBrush ?? accent));
            var leftStack = new StackPanel();
            leftStack.Children.Add(leftPanel);
            if (!string.IsNullOrEmpty(leftSub)) leftStack.Children.Add(new TextBlock { Text = leftSub, Foreground = Dim, FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0) });

            var rl = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            rl.Children.Add(new TextBlock { Text = right, Foreground = rightBrush, FontSize = 15, FontWeight = FontWeights.Bold });
            if (!string.IsNullOrEmpty(rightSub)) rl.Children.Add(new TextBlock { Text = rightSub, Foreground = Dim, FontSize = 10.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom });
            var rightStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            rightStack.Children.Add(rl);

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(rightStack, Dock.Right);
            dock.Children.Add(rightStack);
            dock.Children.Add(leftStack);

            var content = new Grid();
            content.Children.Add(barGrid);
            content.Children.Add(dock);

            return new Border
            {
                CornerRadius = new CornerRadius(9), Background = RowBg, BorderBrush = RowStroke, BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(11, 8, 12, 8), ClipToBounds = true, Child = content
            };
        }

        public static FrameworkElement Timeline(IReadOnlyList<long> dps, IReadOnlyList<long> incoming, double W, double H)
        {
            var host = new Border
            {
                CornerRadius = new CornerRadius(9), Background = RowBg, BorderBrush = RowStroke, BorderThickness = new Thickness(1),
                Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8)
            };
            int n = Math.Max(dps.Count, incoming.Count);
            if (n < 2) { host.Child = Hint("fight too short to chart"); return host; }
            long max = 1;
            for (int i = 0; i < n; i++)
            {
                if (i < dps.Count) max = Math.Max(max, dps[i]);
                if (i < incoming.Count) max = Math.Max(max, incoming[i]);
            }
            var canvas = new Canvas { Width = W, Height = H };
            canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, Y1 = H, X2 = W, Y2 = H, Stroke = RowStroke, StrokeThickness = 1 });
            canvas.Children.Add(Line(dps, n, max, W, H, You));
            canvas.Children.Add(Line(incoming, n, max, W, H, DmgIn));

            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            legend.Children.Add(new TextBlock { Text = $"peak {max}/s", Foreground = Dim, FontSize = 10, Margin = new Thickness(0, 0, 10, 0) });
            legend.Children.Add(LegendDot(You, "your dps"));
            legend.Children.Add(LegendDot(DmgIn, "incoming"));

            var col = new StackPanel();
            col.Children.Add(canvas);
            col.Children.Add(legend);
            host.Child = col;
            return host;
        }

        private static System.Windows.Shapes.Polyline Line(IReadOnlyList<long> data, int n, long max, double W, double H, Brush stroke)
        {
            var pts = new PointCollection();
            double step = n > 1 ? W / (n - 1) : W;
            for (int i = 0; i < n; i++)
            {
                long v = i < data.Count ? data[i] : 0;
                pts.Add(new Point(i * step, H - (double)v / max * H));
            }
            return new System.Windows.Shapes.Polyline { Points = pts, Stroke = stroke, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        }

        private static FrameworkElement LegendDot(Brush c, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
            sp.Children.Add(new System.Windows.Shapes.Rectangle { Width = 9, Height = 9, RadiusX = 2, RadiusY = 2, Fill = c, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            sp.Children.Add(new TextBlock { Text = label, Foreground = Dim, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }
    }
}
