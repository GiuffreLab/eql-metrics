using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqlMetrics.Core;

namespace EqlMetrics
{
    /// <summary>
    /// A large, review-oriented window showing everything about one encounter.
    /// Built entirely in code (no XAML) and populated once from a snapshot of the
    /// encounter, so it is safe to construct while holding the parser lock.
    /// </summary>
    public sealed class EncounterWindow : Window
    {
        public EncounterWindow(Encounter e)
        {
            Title = "Encounter — " + e.Title;
            Width = 800; Height = 880; MinWidth = 560; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = EqlUi.B("#0B0D11");
            Foreground = EqlUi.Text;
            ResizeMode = ResizeMode.CanResizeWithGrip;

            var root = new StackPanel { Margin = new Thickness(18, 14, 18, 24) };

            // ---- header ----
            root.Children.Add(new TextBlock { Text = e.Title, Foreground = EqlUi.Text, FontSize = 22, FontWeight = FontWeights.Bold });
            root.Children.Add(new TextBlock
            {
                Text = $"{e.Start:ddd HH:mm:ss} – {e.End:HH:mm:ss}   ·   {EqlUi.FmtClock(e.Seconds)}",
                Foreground = EqlUi.Dim, FontSize = 12, Margin = new Thickness(0, 2, 0, 4)
            });

            var a = e.Agg;
            double sec = e.Seconds;

            // ---- summary tiles ----
            root.Children.Add(EqlUi.Section("SUMMARY"));
            var tiles = new WrapPanel();
            void T(string k, string v, Brush b) => tiles.Children.Add(EqlUi.StatBox(k, v, b));
            T("You DPS", a.PlayerDps.ToString("0.0"), EqlUi.YouT);
            if (a.HasPet) T("Pet DPS", a.PetDps.ToString("0.0"), EqlUi.PetT);
            T("Combined", a.CombinedDps.ToString("0.0"), EqlUi.You);
            T("Your dmg", a.Player?.TotalDamage.ToString("0") ?? "0", EqlUi.YouT);
            T("HPS", a.Hps.ToString("0.0"), EqlUi.Heal);
            T("Overheal", a.OverhealPct.ToString("0") + "%", EqlUi.Heal);
            T("Dmg taken", a.DamageTaken.ToString("0"), EqlUi.DmgIn);
            if (a.HasPet) T("Pet taken", a.DamageTakenPet.ToString("0"), EqlUi.DmgIn);
            T("Biggest taken", a.BiggestHitTaken.ToString("0"), EqlUi.DmgIn);
            T("Enemy HPS", a.EnemyHps.ToString("0.0"), EqlUi.Nuke);
            T("Enemies", a.EnemyNames.Count.ToString("0"), EqlUi.Text);
            root.Children.Add(tiles);

            // ---- timeline ----
            root.Children.Add(EqlUi.Section("DPS OVER TIME"));
            root.Children.Add(EqlUi.Timeline(e.DpsBuckets, e.InBuckets, 720, 130));

            // ---- friendlies with full ability breakdown ----
            root.Children.Add(EqlUi.Section("YOUR SIDE — abilities"));
            var friendlies = a.Friendlies.OrderBy(c => c.IsPlayer ? 0 : c.IsPet ? 1 : 2).ThenByDescending(c => c.TotalDamage).ToList();
            long party = Math.Max(1, friendlies.Sum(c => c.TotalDamage));
            if (friendlies.Count == 0) root.Children.Add(EqlUi.Hint("no damage recorded"));
            foreach (var c in friendlies)
            {
                Brush accent = c.IsPlayer ? EqlUi.You : c.IsPet ? EqlUi.Pet : EqlUi.Grp;
                string role = c.IsPlayer ? "you" : c.IsPet ? "pet" : "group";
                root.Children.Add(EqlUi.Sub($"{c.Name}  ·  {role}  ·  {a.DpsOf(c):0.0} dps  ·  {c.TotalDamage} dmg  ·  {100.0 * c.TotalDamage / party:0}%", accent));
                AddAbilities(root, c, sec);
            }

            // ---- your healing ----
            root.Children.Add(EqlUi.Section("YOUR HEALING — by spell"));
            var heals = a.HealsByAmount.Where(h => h.Effective > 0).ToList();
            if (heals.Count == 0) root.Children.Add(EqlUi.Hint("no healing this fight"));
            else
            {
                long top = Math.Max(1, heals[0].Effective);
                long tot = Math.Max(1, heals.Sum(h => h.Effective));
                foreach (var h in heals)
                    root.Children.Add(EqlUi.Row(h.Name, $"x{h.Casts}  avg {h.Avg:0.0}  max {h.Max}  overheal {h.OverhealPct:0}%",
                        (h.Effective / sec).ToString("0.0"), (100.0 * h.Effective / tot).ToString("0") + "%",
                        (double)h.Effective / top, EqlUi.Heal, EqlUi.Heal, "HEAL", EqlUi.Heal));
            }

            // ---- enemies with full attack breakdown ----
            root.Children.Add(EqlUi.Section("ENEMIES — damage to your party & attacks"));
            var enemies = a.Enemies.OrderByDescending(c => c.TotalDamage).ToList();
            if (enemies.Count == 0) root.Children.Add(EqlUi.Hint("no enemy activity recorded"));
            foreach (var c in enemies)
            {
                root.Children.Add(EqlUi.Sub($"{c.Name}  ·  {c.TotalDamage} dmg to party  ·  {c.TotalDamage / sec:0.0} dps", EqlUi.DmgIn));
                AddAbilities(root, c, sec);
            }

            // ---- enemy healing detail ----
            var enemyHeals = a.NpHeals
                .Where(h => a.EnemyNames.Contains(h.Healer) || a.EnemyNames.Contains(h.Target))
                .GroupBy(h => h.Healer)
                .Select(g => new { Healer = g.Key, Eff = g.Sum(x => x.Eff), Count = g.Count() })
                .OrderByDescending(g => g.Eff).ToList();
            if (enemyHeals.Count > 0)
            {
                root.Children.Add(EqlUi.Section("ENEMY HEALING — by healer"));
                long top = Math.Max(1, enemyHeals[0].Eff);
                foreach (var g in enemyHeals)
                    root.Children.Add(EqlUi.Row(g.Healer, $"x{g.Count}", (g.Eff / sec).ToString("0.0"), g.Eff + " hp",
                        (double)g.Eff / top, EqlUi.Nuke, EqlUi.Nuke, "HEAL", EqlUi.Nuke));
            }

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = root
            };
        }

        private static void AddAbilities(Panel host, Combatant c, double seconds)
        {
            var abs = c.Abilities.Values.Where(x => x.Total > 0).OrderByDescending(x => x.Total).ToList();
            if (abs.Count == 0) { host.Children.Add(EqlUi.Hint("no abilities")); return; }
            long top = Math.Max(1, abs[0].Total);
            long tot = Math.Max(1, abs.Sum(x => x.Total));
            foreach (var ab in abs)
            {
                var (bt, bc) = EqlUi.BadgeFor(ab.Kind);
                string sub = $"x{ab.Hits}  avg {ab.Avg:0.0}  max {ab.Max}"
                    + (ab.Crits > 0 ? $"  crit {ab.CritPct:0}%" : "")
                    + (ab.Misses > 0 ? $"  miss {ab.MissPct:0}%" : "");
                host.Children.Add(EqlUi.Row(ab.Name, sub, (ab.Total / seconds).ToString("0.0"),
                    (100.0 * ab.Total / tot).ToString("0") + "%", (double)ab.Total / top, bc, bc, bt, bc));
            }
        }
    }
}
