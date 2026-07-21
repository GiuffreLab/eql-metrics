using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EqlMetrics.Core
{
    /// <summary>Result of a GitHub "is there a newer release?" check.</summary>
    public sealed class UpdateInfo
    {
        public string CurrentVersion = "";
        public string LatestVersion = "";   // GitHub tag, normalized (leading 'v' stripped)
        public string LatestTag = "";        // raw tag as published (e.g. "v0.6.0")
        public string ReleaseName = "";
        public string ReleaseUrl = "";
        public bool UpdateAvailable;
        public bool Ok;                      // true if the check completed (network + parse) successfully
        public string? Error;                // set when Ok == false
    }

    /// <summary>
    /// Checks GitHub for a newer published release and compares it to the running build.
    /// The parse/compare logic is pure (and unit-tested from the harness); only <see cref="CheckAsync"/>
    /// touches the network. It never self-updates — the app is distributed as clone-and-run, so a
    /// positive result just points the user at the releases page to <c>git pull</c>.
    /// </summary>
    public static class UpdateChecker
    {
        public const string DefaultOwner = "GiuffreLab";
        public const string DefaultRepo = "eql-metrics";

        private static readonly HttpClient Http = MakeClient();
        private static HttpClient MakeClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            // GitHub's REST API requires a User-Agent and is happiest with an explicit API version.
            h.DefaultRequestHeaders.Add("User-Agent", "eql-metrics-update-check");
            h.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            h.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            return h;
        }

        /// <summary>Query releases/latest and decide whether it's newer than <paramref name="currentVersion"/>.</summary>
        public static async Task<UpdateInfo> CheckAsync(string currentVersion, CancellationToken ct = default,
            string owner = DefaultOwner, string repo = DefaultRepo)
        {
            var info = new UpdateInfo { CurrentVersion = Normalize(currentVersion) };
            try
            {
                string url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
                string body = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                info.LatestTag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
                info.ReleaseName = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                info.ReleaseUrl = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? "") : "";
                info.LatestVersion = Normalize(info.LatestTag);

                if (info.LatestVersion.Length == 0) { info.Error = "no tag in latest release"; return info; }
                info.UpdateAvailable = IsNewer(info.LatestVersion, info.CurrentVersion);
                info.Ok = true;
                return info;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { info.Error = ex.Message; return info; }
        }

        // ---- pure version logic (unit-tested) ----

        /// <summary>Strip a leading 'v'/'V' and surrounding whitespace: "v0.5.0" → "0.5.0".</summary>
        public static string Normalize(string? s)
        {
            s = (s ?? "").Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);
            return s.Trim();
        }

        /// <summary>True if <paramref name="latest"/> is a strictly newer version than <paramref name="current"/>.</summary>
        public static bool IsNewer(string latest, string current) => Compare(latest, current) > 0;

        /// <summary>
        /// Compare two dotted versions numerically ("0.10.0" &gt; "0.9.0"). A pre-release suffix
        /// (e.g. "-beta") is treated as older than the same release number. Unparseable numeric
        /// parts count as 0, so a malformed remote tag never falsely triggers an update.
        /// </summary>
        public static int Compare(string a, string b)
        {
            var (na, pa) = Split(Normalize(a));
            var (nb, pb) = Split(Normalize(b));
            int len = Math.Max(na.Length, nb.Length);
            for (int i = 0; i < len; i++)
            {
                int va = i < na.Length ? na[i] : 0;
                int vb = i < nb.Length ? nb[i] : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            // Equal numeric core: a build WITHOUT a pre-release suffix outranks one WITH it.
            if (pa == pb) return 0;
            if (pa && !pb) return -1;   // a is pre-release, b is final → a older
            if (!pa && pb) return 1;
            return 0;
        }

        // "0.5.0-beta.2" → ([0,5,0], hasPrerelease:true)
        private static (int[] nums, bool prerelease) Split(string v)
        {
            bool pre = false;
            int dash = v.IndexOf('-');
            if (dash >= 0) { pre = true; v = v.Substring(0, dash); }
            string[] parts = v.Length == 0 ? Array.Empty<string>() : v.Split('.');
            var nums = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                // tolerate junk like "1a" → take leading digits only
                int j = 0; while (j < parts[i].Length && char.IsDigit(parts[i][j])) j++;
                int.TryParse(parts[i].Substring(0, j), out nums[i]);
            }
            return (nums, pre);
        }
    }
}
