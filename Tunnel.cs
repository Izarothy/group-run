using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GroupRun
{
    /// <summary>
    /// A Cloudflare quick tunnel in front of the admin's hub: cloudflared gives the hub a public
    /// https://name.trycloudflare.com address, so runners connect to Cloudflare and never see the admin's IP, and
    /// no port is opened on the router. Free and without an account; the address is new every time. cloudflared
    /// is Cloudflare's own program, next to GroupRun.exe or on PATH; the app can fetch it from Cloudflare's
    /// GitHub releases (checked against GitHub's checksum). It's tied to this app's lifetime by a job object,
    /// so it can't outlive the app even after a crash.
    /// </summary>
    sealed class Tunnel : IDisposable
    {
        const string Asset = "cloudflared-windows-amd64.exe";
        static readonly Regex Address = new Regex(@"https://(?!api\.)([a-z0-9-]+\.trycloudflare\.com)", RegexOptions.Compiled); // not the API in its error lines

        readonly Process process;
        volatile bool disposed;

        Tunnel(Process process) => this.process = process;

        /// <summary>cloudflared quit on its own after handing out the address: that address is dead now.</summary>
        public event Action Died;

        public static string LocalPath => Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");

        /// <summary>cloudflared next to the app, else on PATH, else null.</summary>
        public static string Find()
        {
            if (File.Exists(LocalPath))
                return LocalPath;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "cloudflared.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>Start a quick tunnel to the hub and return it with its public host name, once Cloudflare has assigned one.</summary>
        public static async Task<(Tunnel Tunnel, string Host)> StartAsync(string cloudflared, int port)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(cloudflared, $"tunnel --no-autoupdate --url http://127.0.0.1:{port}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
            };
            var tunnel = new Tunnel(process);
            var found = new TaskCompletionSource<string>();
            var log = new List<string>();
            DataReceivedEventHandler watch = (s, e) =>
            {
                if (e.Data == null) return;
                lock (log) { log.Add(e.Data); if (log.Count > 30) log.RemoveAt(0); }
                var m = Address.Match(e.Data);
                if (m.Success) found.TrySetResult(m.Groups[1].Value);
            };
            process.ErrorDataReceived += watch; // cloudflared logs to stderr
            process.OutputDataReceived += watch;
            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) return;
                // stderr closed, so cloudflared has exited: before the address, that's a failure to report now
                if (!found.TrySetResult(null) && !tunnel.disposed)
                    tunnel.Died?.Invoke();
            };
            process.Start();
            Job.Attach(process);
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            var winner = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(40)));
            if (winner != found.Task || found.Task.Result == null)
            {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                string last;
                lock (log) last = log.LastOrDefault(l => l.Contains("ERR")) ?? "no address from Cloudflare";
                throw new IOException(last);
            }
            return (tunnel, found.Task.Result);
        }

        /// <summary>Download cloudflared from Cloudflare's latest GitHub release next to the app, checked against GitHub's checksum.</summary>
        public static async Task<string> DownloadAsync()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("GroupRun");
                var release = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(
                    await http.GetStringAsync("https://api.github.com/repos/cloudflare/cloudflared/releases/latest"));
                var asset = ((IEnumerable)release["assets"]).Cast<Dictionary<string, object>>().FirstOrDefault(a => (a["name"] as string) == Asset)
                    ?? throw new IOException("no Windows build in Cloudflare's latest release");
                var digest = asset.TryGetValue("digest", out var d) ? d as string ?? "" : "";
                if (!digest.StartsWith("sha256:"))
                    throw new IOException("the release has no checksum");
                var bytes = await http.GetByteArrayAsync((string)asset["browser_download_url"]);
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != digest.Substring(7).ToLowerInvariant())
                        throw new IOException("the download doesn't match Cloudflare's checksum");
                File.WriteAllBytes(LocalPath, bytes);
                return LocalPath;
            }
        }

        public void Dispose()
        {
            disposed = true;
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose();
        }

        /// <summary>A job object that kills its processes when this app's last handle to it closes, crash or not.</summary>
        static class Job
        {
            static readonly IntPtr Handle = Create();

            static IntPtr Create()
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
                var size = Marshal.SizeOf(info);
                var pointer = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(info, pointer, false);
                SetInformationJobObject(job, 9, pointer, (uint)size); // JobObjectExtendedLimitInformation
                Marshal.FreeHGlobal(pointer);
                return job;
            }

            public static void Attach(Process process) => AssignProcessToJobObject(Handle, process.Handle);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
            [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
            [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [StructLayout(LayoutKind.Sequential)]
            struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass, SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

            [StructLayout(LayoutKind.Sequential)]
            struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
            }
        }
    }
}
