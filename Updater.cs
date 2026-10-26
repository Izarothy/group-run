using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GroupRun
{
    public sealed class Release
    {
        public Version Version;
        public string DownloadUrl;
        public string Sha256;   // from GitHub's own digest of the uploaded file
    }

    /// <summary>
    /// Updates come from the latest GitHub release of <see cref="Repo"/>: the exe attached to it as GroupRun.exe.
    /// Nothing happens on its own; the runner clicks Check for updates. The running exe can't be overwritten
    /// but can be renamed, so the update renames it to .old, puts the new one in its place and restarts.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "Izarothy/group-run";
        const string AssetName = "GroupRun.exe";

        static readonly HttpClient Http = CreateClient();

        public static Version Current => Normalize(typeof(Updater).Assembly.GetName().Version);

        static string ExePath => Process.GetCurrentProcess().MainModule.FileName;

        static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GroupRun/" + Current);
            return http;
        }

        /// <summary>"0.2" and "0.2.0.0" are the same version.</summary>
        public static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        public static bool TryParse(string text, out Version version)
        {
            version = null;
            if (!Version.TryParse((text ?? "").TrimStart('v', 'V'), out var parsed))
                return false;
            version = Normalize(parsed);
            return true;
        }

        /// <summary>The latest release, or null when there is none with an exe attached.</summary>
        public static async Task<Release> LatestAsync()
        {
            var text = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            var release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            if (!TryParse(release["tag_name"] as string, out var version))
                return null;
            var asset = ((IEnumerable)release["assets"]).Cast<Dictionary<string, object>>()
                .FirstOrDefault(a => (a["name"] as string) == AssetName);
            if (asset == null)
                return null;
            var digest = asset.TryGetValue("digest", out var d) ? d as string ?? "" : "";
            return new Release
            {
                Version = version,
                DownloadUrl = (string)asset["browser_download_url"],
                Sha256 = digest.StartsWith("sha256:") ? digest.Substring(7) : "",
            };
        }

        /// <summary>Download, check and swap in the new exe, then start it. The caller exits afterwards.</summary>
        public static async Task InstallAsync(Release release)
        {
            if (release.Sha256.Length == 0)
                throw new InvalidOperationException("The release has no checksum, so the download can't be verified.");
            var bytes = await Http.GetByteArrayAsync(release.DownloadUrl);
            string actual;
            using (var sha = SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (actual != release.Sha256.ToLowerInvariant())
                throw new InvalidOperationException("The download doesn't match the release's checksum.");

            var exe = ExePath;
            var fresh = exe + ".new";
            var old = exe + ".old";
            File.WriteAllBytes(fresh, bytes);
            if (File.Exists(old))
                File.Delete(old);
            File.Move(exe, old);
            File.Move(fresh, exe);
            Process.Start(exe);
        }

        /// <summary>Remove the previous exe left behind by an update, once it is no longer running.</summary>
        public static void CleanUp()
        {
            try
            {
                var old = ExePath + ".old";
                if (File.Exists(old))
                    File.Delete(old);
            }
            catch (IOException)
            {
                // still running during a quick restart; the next launch removes it
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
