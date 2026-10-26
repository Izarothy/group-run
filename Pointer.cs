using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GroupRun
{
    /// <summary>
    /// Keeps the join code permanent although the tunnel's address changes every time the admin hosts: the admin's
    /// app posts the current address to a fixed, random ntfy.sh topic, and runners' apps look it up there before
    /// each connection attempt. Only the address goes through ntfy, signed with the group's secret so nobody else
    /// can plant one; ntfy keeps it 12 hours, so the host posts again every 6.
    /// </summary>
    static class Pointer
    {
        const string Server = "https://ntfy.sh/";
        static readonly HttpClient Http = Create();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        static HttpClient Create()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        public static string NewTopic() => "grouprun-" + Guid.NewGuid().ToString("N").Substring(0, 16);

        static string Sign(string secret, string host)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
                return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(host))).Replace("-", "").ToLowerInvariant();
        }

        public static async Task PublishAsync(string topic, string secret, string host)
        {
            var response = await Http.PostAsync(Server + topic, new StringContent(host + "|" + Sign(secret, host), Encoding.UTF8));
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Every address the admin posted in the last 12 hours, newest first; empty when none. Not just the newest:
        /// an admin app that crashed, or a second one started by mistake, leaves a post for a tunnel that's gone,
        /// so runners try them in turn.
        /// </summary>
        public static async Task<List<string>> ResolveAsync(string topic, string secret)
        {
            var text = await Http.GetStringAsync($"{Server}{topic}/json?poll=1&since=12h");
            var hosts = new List<string>();
            foreach (var line in text.Split('\n').Where(l => l.Trim().Length > 0))
            {
                var envelope = Json.Deserialize<Dictionary<string, object>>(line);
                if ((envelope["event"] as string) != "message") continue;
                var parts = ((string)envelope["message"]).Split('|');
                if (parts.Length == 2 && parts[1] == Sign(secret, parts[0]))
                    hosts.Insert(0, parts[0]); // messages come oldest first
            }
            return hosts.Distinct().ToList();
        }
    }
}
