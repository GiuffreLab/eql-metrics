using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EqlMetrics.Core
{
    /// <summary>Progress report emitted while scraping (Total = 0 until enumeration finishes).</summary>
    public sealed class ScrapeProgress
    {
        public string Phase = "";
        public int Done;
        public int Total;
    }

    /// <summary>
    /// In-app port of tools/scrape-spells.ps1. Walks Category:Spells on the EverQuest
    /// Legends wiki (eqlwiki.com) via the MediaWiki API, reads each page's
    /// {{Spellpage|...}} template, and produces the same rows the PowerShell script
    /// wrote to spells.json — so the overlay can refresh spell data with one click.
    /// </summary>
    public static class SpellScraper
    {
        public const string DefaultApi = "https://eqlwiki.com/api.php";

        private static readonly HttpClient Http = MakeClient();
        private static HttpClient MakeClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            h.DefaultRequestHeaders.Add("User-Agent", "eql-metrics-spell-scraper/1.0 (personal EQ overlay)");
            return h;
        }

        // ---- public entry points ----

        /// <summary>Scrape all spell pages into rows (sorted by name). Throws on network failure.</summary>
        public static async Task<List<SpellRow>> ScrapeAsync(IProgress<ScrapeProgress>? progress, CancellationToken ct, string api = DefaultApi)
        {
            progress?.Report(new ScrapeProgress { Phase = "Finding spells…", Done = 0, Total = 0 });
            var titles = await EnumerateAsync(api, ct).ConfigureAwait(false);

            var rows = new List<SpellRow>(titles.Count);
            for (int i = 0; i < titles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = await ParsePageAsync(api, titles[i], ct).ConfigureAwait(false);
                if (row != null) rows.Add(row);
                progress?.Report(new ScrapeProgress { Phase = "Reading spell pages…", Done = i + 1, Total = titles.Count });
                await Task.Delay(110, ct).ConfigureAwait(false);   // be polite to the wiki
            }
            rows.Sort((a, b) => string.Compare(a.spell, b.spell, StringComparison.OrdinalIgnoreCase));
            return rows;
        }

        public static string ToJson(IEnumerable<SpellRow> rows) =>
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });

        /// <summary>Scrape, write spells.json (UTF-8, no BOM), and apply to BuffData. Returns (selfBuffs, total).</summary>
        public static async Task<(int selfBuffs, int total)> UpdateAsync(string jsonPath, IProgress<ScrapeProgress>? progress, CancellationToken ct, string api = DefaultApi)
        {
            var rows = await ScrapeAsync(progress, ct, api).ConfigureAwait(false);
            if (rows.Count == 0) return (0, 0);
            var dir = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(jsonPath, ToJson(rows), new UTF8Encoding(false), ct).ConfigureAwait(false);
            int self = SpellCatalog.ApplyRows(rows);
            return (self, rows.Count);
        }

        // ---- internals ----

        private static async Task<List<string>> EnumerateAsync(string api, CancellationToken ct)
        {
            var titles = new List<string>();
            string? cont = null;
            do
            {
                ct.ThrowIfCancellationRequested();
                string u = api + "?action=query&list=categorymembers&cmtitle=" + Uri.EscapeDataString("Category:Spells")
                         + "&cmlimit=500&cmtype=page&format=json&formatversion=2";
                if (cont != null) u += "&cmcontinue=" + Uri.EscapeDataString(cont);

                using var doc = JsonDocument.Parse(await Http.GetStringAsync(u, ct).ConfigureAwait(false));
                var root = doc.RootElement;
                foreach (var m in root.GetProperty("query").GetProperty("categorymembers").EnumerateArray())
                    titles.Add(m.GetProperty("title").GetString() ?? "");
                cont = root.TryGetProperty("continue", out var c) && c.TryGetProperty("cmcontinue", out var cc) ? cc.GetString() : null;
            } while (cont != null);

            titles.RemoveAll(string.IsNullOrEmpty);
            return titles;
        }

        private static async Task<SpellRow?> ParsePageAsync(string api, string title, CancellationToken ct)
        {
            string u = api + "?action=parse&page=" + Uri.EscapeDataString(title) + "&prop=wikitext&format=json&formatversion=2";
            string wt;
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(u, ct).ConfigureAwait(false));
                wt = doc.RootElement.GetProperty("parse").GetProperty("wikitext").GetString() ?? "";
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }   // a page that won't parse just gets skipped, like the PS script

            if (wt.IndexOf("Spellpage", StringComparison.OrdinalIgnoreCase) < 0) return null;

            string name = Field(wt, "spellname");
            if (name.Length == 0) name = title;
            string dur = Field(wt, "duration");
            return new SpellRow
            {
                spell = name,
                duration_text = dur,
                duration_sec = ConvertDuration(dur),
                target_type = Field(wt, "target_type"),
                spell_type = Field(wt, "spell_type"),
                mana = Field(wt, "mana"),
                cast_on_you = Field(wt, "msg_cast_on_you"),
                cast_on_other = Field(wt, "msg_cast_on_other"),
                wears_off = Field(wt, "msg_wears_off"),
            };
        }

        // Mirror of Get-Field: [ \t]* (not \s*) around the value so an EMPTY field can't
        // let the matcher cross the newline and capture the next "| param =" line.
        private static string Field(string text, string name)
        {
            var m = Regex.Match(text, @"(?m)^[ \t]*\|[ \t]*" + Regex.Escape(name) + @"[ \t]*=[ \t]*(.*?)[ \t]*$");
            if (!m.Success) return "";
            var v = m.Groups[1].Value.Trim();
            if (v.StartsWith("|")) return "";   // guard against any stray template artifact
            return v;
        }

        // Mirror of Convert-Duration: instant/permanent/until/charge -> 0; first value of a
        // level-scaled range; MM:SS; hours/minutes/seconds/ticks(*6).
        public static double ConvertDuration(string d)
        {
            if (string.IsNullOrWhiteSpace(d)) return 0;
            if (Regex.IsMatch(d, "(?i)instant|permanent|until|charge")) return 0;
            d = Regex.Split(d, @"(?i)\bto\b")[0];

            var mm = Regex.Match(d, @"(\d+):(\d{2})");
            if (mm.Success)
                return int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                     + int.Parse(mm.Groups[2].Value, CultureInfo.InvariantCulture);

            // NB: case-insensitive to match PowerShell's -match default (e.g. "11 Min", "3 Ticks")
            const RegexOptions I = RegexOptions.IgnoreCase;
            double sec = 0; bool hit = false; Match x;
            if ((x = Regex.Match(d, @"(\d+(?:\.\d+)?)\s*(?:hours?|hrs?|hr)\b", I)).Success)     { sec += double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture) * 3600; hit = true; }
            if ((x = Regex.Match(d, @"(\d+(?:\.\d+)?)\s*(?:minutes?|mins?|min)\b", I)).Success) { sec += double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture) * 60;   hit = true; }
            if ((x = Regex.Match(d, @"(\d+(?:\.\d+)?)\s*(?:seconds?|secs?|sec)\b", I)).Success) { sec += double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);        hit = true; }
            if ((x = Regex.Match(d, @"(\d+)\s*ticks?\b", I)).Success)                          { sec += int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture) * 6;       hit = true; }
            if (!hit) return 0;
            return Math.Round(sec);
        }
    }
}
