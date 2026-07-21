using System;
using System.IO;
using System.Text.Json;

namespace EqlMetrics.Core
{
    /// <summary>Persisted user settings (window position, opacity, pet name, etc.).</summary>
    public sealed class Settings
    {
        public string LastLogPath { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public string PetName { get; set; } = "";
        public double PanelAlpha { get; set; } = 0.42;   // 0..1 backdrop opacity
        public double Left { get; set; } = 60;
        public double Top { get; set; } = 60;
        public bool Expanded { get; set; } = true;
        public bool LootExpanded { get; set; } = false;   // loot list: false = last 2, true = last 10
        public bool ClickThrough { get; set; } = false;
        public bool FollowFromStart { get; set; } = false; // start at end of file (live) by default

        // ---- notification toggles (all default on) ----
        public bool NotifMaster { get; set; } = true;      // master switch for all center-screen alerts
        public bool NotifBuffs { get; set; } = true;       // buff/debuff gained & faded
        public bool NotifStealth { get; set; } = true;     // hide/sneak success & failure
        public bool NotifSkills { get; set; } = true;      // backstab / kick / strike / cleave pop-ups
        public bool NotifQuickBuff { get; set; } = true;   // Quick Buff ready
        public bool NotifMend { get; set; } = true;        // Mend self-heal
        public int NotifMaxOnScreen { get; set; } = 3;     // 1..5 rising notifications at once

        private static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EqlMetrics");
        private static string FilePath => Path.Combine(Dir, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch { /* fall through to defaults */ }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* non-fatal */ }
        }
    }
}
