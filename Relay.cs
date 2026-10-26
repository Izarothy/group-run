using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GroupRun
{
    public sealed class Message
    {
        public Dictionary<string, object> Body;
        public TimeSpan Age; // how long ago the hub got it (0 for live messages, more for a replay)
    }

    public enum Link
    {
        Connected,
        NoCode,      // no (valid) join code in the settings
        Connecting,  // first attempt
        Retrying,    // never got through yet: the admin's app is probably off
        Lost,        // was connected, dropped, reconnecting
        Denied,      // the hub refused the join code: retrying won't help
    }

    /// <summary>How the app talks to the rest of the group: publish small JSON messages, receive everyone's.</summary>
    public interface IRelay : IDisposable
    {
        ConcurrentQueue<Message> Inbox { get; }
        Link Link { get; }
        DateTime RetryAt { get; }   // when the next attempt is, while Retrying or Lost
        int Connections { get; }    // how many times a connection was made: a change means "announce yourself again"
        string LastError { get; }
        void Publish(Dictionary<string, object> body);
    }

    /// <summary>
    /// Where to connect, as one code to paste: where to find the admin's hub and a secret. Normally the first part
    /// is the group's ntfy topic ("grouprun-8f3k2a9c1d7e4b6f/k3f9x2qa1b"), where the admin's app posts its current
    /// tunnel address (see <see cref="Pointer"/>), so the code never changes. It can also be an address directly:
    /// a tunnel ("name.trycloudflare.com/secret") or, for the sim and self-test, "127.0.0.1:port/secret".
    /// The secret keeps strangers out and signs the posted address.
    /// </summary>
    public sealed class JoinCode
    {
        public string Host;    // an ntfy topic, "name.trycloudflare.com", or "127.0.0.1:port"
        public string Secret;

        public override string ToString() => $"{Host}/{Secret}";

        /// <summary>A topic to look the address up in, rather than an address.</summary>
        public bool IsPointer => Host.IndexOf('.') < 0 && Host.IndexOf(':') < 0;

        public static Uri UriFor(string host) =>
            new Uri((host.StartsWith("127.0.0.1") || host.StartsWith("localhost") ? "ws://" : "wss://") + host + "/");

        public static bool TryParse(string text, out JoinCode code)
        {
            code = null;
            var t = (text ?? "").Trim();
            foreach (var scheme in new[] { "https://", "http://", "wss://", "ws://" })
                if (t.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) t = t.Substring(scheme.Length);
            var slash = t.LastIndexOf('/');
            if (slash <= 0 || slash == t.Length - 1)
                return false;
            code = new JoinCode { Host = t.Substring(0, slash), Secret = t.Substring(slash + 1) };
            return true;
        }

        public static string NewSecret() => Guid.NewGuid().ToString("N").Substring(0, 10);
    }

    /// <summary>
    /// The relay, run by the admin's app: a WebSocket server on this PC only (127.0.0.1), reached by runners
    /// through the tunnel. Each message is one line of JSON; the hub passes it on to everyone and replays
    /// everything so far to anyone who (re)connects. "detail" messages, the full-resolution stream for the run
    /// history, only go to the admin. Messages from the hub are "ageMs TAB json".
    /// </summary>
    public sealed class Hub : IDisposable
    {
        sealed class Entry
        {
            public string Json;
            public long At; // hub stopwatch ms
        }

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly TimeSpan JournalKept = TimeSpan.FromHours(1); // untouched longer than that, it's an old session

        readonly WsServer server;
        readonly string secret;
        readonly string journal;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly List<Entry> history = new List<Entry>();
        readonly Dictionary<string, Entry> latestUpdate = new Dictionary<string, Entry>(); // runner id: their entry in the history
        readonly List<Peer> peers = new List<Peer>();
        readonly CancellationTokenSource stop = new CancellationTokenSource();

        /// <summary>The admin's own copy of the app reads from here, detail messages included.</summary>
        public readonly ConcurrentQueue<Message> Local = new ConcurrentQueue<Message>();

        public int Port => server.Port;

        /// <param name="port">0 picks a free port (the sim does that).</param>
        /// <param name="journal">The admin's app: a file that keeps the history too, so that the app started again
        /// after a crash (or a close by mistake) carries on with the run where it was. Null keeps it in memory only.</param>
        public Hub(int port, string secret, string journal = null)
        {
            this.secret = secret;
            this.journal = journal;
            server = new WsServer(port);
            Restore();
            Task.Run(Accept);
        }

        public void Publish(Dictionary<string, object> body) => Pass(Json.Serialize(body), body);

        void Pass(string json, Dictionary<string, object> body)
        {
            var forRunners = (body["t"] as string) != "detail";
            lock (history) // one order for everyone: concurrent senders must not interleave differently per peer
            {
                if (forRunners)
                {
                    Keep(json, body, clock.ElapsedMilliseconds);
                    Journal(json, body);
                }
                Local.Enqueue(new Message { Body = body, Age = TimeSpan.Zero });
                if (forRunners)
                    lock (peers)
                        foreach (var peer in peers)
                            peer.Send("0\t" + json);
            }
        }

        /// <summary>
        /// Into the history that is replayed to everyone who (re)connects. Detail only goes to the admin, live, so it
        /// isn't kept. Of a runner's updates only the latest matters to a replay, so it takes the place of the one
        /// before, in its place (join order picks the colours): a few hours' heartbeats and XP updates would
        /// otherwise be thousands of lines for every reconnect. Everything else (controls, the split list and each
        /// split's time) is kept as it came, in order.
        /// </summary>
        void Keep(string json, Dictionary<string, object> body, long at)
        {
            var runner = (body["t"] as string) == "runner" && body.TryGetValue("id", out var id) ? id as string : null;
            if (runner != null && latestUpdate.TryGetValue(runner, out var known))
            {
                known.Json = json;
                known.At = at;
                return;
            }
            var entry = new Entry { Json = json, At = at };
            history.Add(entry);
            if (runner != null)
                latestUpdate[runner] = entry;
        }

        /// <summary>One line per message: when (UTC ticks), a tab, the JSON. A reset starts it over.</summary>
        void Journal(string json, Dictionary<string, object> body)
        {
            if (journal == null)
                return;
            var line = DateTime.UtcNow.Ticks + "\t" + json + Environment.NewLine;
            var reset = (body["t"] as string) == "control" && (body.TryGetValue("action", out var a) ? a as string : null) == "reset";
            try
            {
                if (reset)
                    File.WriteAllText(journal, line); // a new run: nothing before it matters to a restart
                else
                    File.AppendAllText(journal, line);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // the run goes on; only a restart would miss this message
            }
        }

        /// <summary>
        /// Started again: the journal's messages go back into the history, aged by how long ago they were, so the
        /// admin's own board (through <see cref="Local"/>) and every runner's replay rebuild the run as it was,
        /// a finished one included (the runners' boards still show it finished). A journal untouched for
        /// <see cref="JournalKept"/> is an earlier session and is dropped.
        /// </summary>
        void Restore()
        {
            if (journal == null || !File.Exists(journal))
                return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(journal);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }
            var now = DateTime.UtcNow.Ticks;
            var restored = new List<(long AgeMs, string Json, Dictionary<string, object> Body)>();
            foreach (var line in lines)
            {
                var tab = line.IndexOf('\t');
                if (tab < 0 || !long.TryParse(line.Substring(0, tab), out var ticks))
                    continue;
                var json = line.Substring(tab + 1);
                try
                {
                    var body = Json.Deserialize<Dictionary<string, object>>(json);
                    if (body != null && body.ContainsKey("t"))
                        restored.Add((Math.Max(1, (now - ticks) / TimeSpan.TicksPerMillisecond), json, body));
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                {
                    // the line the crash cut off
                }
            }
            if (restored.Count == 0 || TimeSpan.FromMilliseconds(restored.Last().AgeMs) > JournalKept)
            {
                try { File.Delete(journal); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                return;
            }
            var at = clock.ElapsedMilliseconds;
            lock (history)
                foreach (var (ageMs, json, body) in restored)
                {
                    Keep(json, body, at - ageMs);
                    Local.Enqueue(new Message { Body = body, Age = TimeSpan.FromMilliseconds(ageMs) });
                }
        }

        async Task Accept()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await server.AcceptAsync();
                }
                catch (Exception) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e) when (e is SocketException || e is IOException || e is ObjectDisposedException)
                {
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    var channel = await WsServer.HandshakeAsync(client);
                    if (channel != null)
                        await Serve(channel);
                });
            }
        }

        async Task Serve(IChannel channel)
        {
            var peer = new Peer(channel);
            try
            {
                if (await channel.ReceiveAsync() != "hello " + secret)
                {
                    await peer.SendAsync("denied");
                    return;
                }
                lock (history)
                {
                    var now = clock.ElapsedMilliseconds;
                    peer.Send("ok"); // sends go out in order: the welcome, then the replay
                    foreach (var e in history)
                        peer.Send($"{now - e.At}\t{e.Json}");
                    lock (peers)
                        peers.Add(peer);
                }
                string line;
                while ((line = await channel.ReceiveAsync()) != null)
                {
                    Dictionary<string, object> body;
                    try
                    {
                        body = Json.Deserialize<Dictionary<string, object>>(line);
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                    {
                        continue; // not JSON, or not an object: skip it rather than drop the runner
                    }
                    if (body != null && body.ContainsKey("t"))
                        Pass(line, body);
                }
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is SocketException)
            {
                // the runner dropped; they reconnect and get the replay
            }
            finally
            {
                lock (peers)
                    peers.Remove(peer);
                peer.Dispose();
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            server.Dispose();
            lock (peers)
                foreach (var peer in peers)
                    peer.Dispose();
        }
    }

    /// <summary>One connection; sends are queued in order so a slow runner never blocks the others.</summary>
    sealed class Peer : IDisposable
    {
        readonly IChannel channel;
        readonly SemaphoreSlim sending = new SemaphoreSlim(1, 1);

        public Peer(IChannel channel) => this.channel = channel;

        public IChannel Channel => channel;

        public async void Send(string line) => await SendAsync(line);

        public async Task SendAsync(string line)
        {
            await sending.WaitAsync();
            try
            {
                await channel.SendAsync(line);
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException || e is System.Net.WebSockets.WebSocketException)
            {
                // the receiving side notices the drop and cleans up
            }
            finally
            {
                sending.Release();
            }
        }

        public void Dispose() => channel.Dispose();
    }

    /// <summary>The admin's side: talks to its own hub without a socket.</summary>
    public sealed class HubRelay : IRelay
    {
        readonly Hub hub;

        public HubRelay(Hub hub) => this.hub = hub;

        public ConcurrentQueue<Message> Inbox => hub.Local;
        public Link Link => Link.Connected;
        public DateTime RetryAt => DateTime.MinValue;
        public int Connections => 1;
        public string LastError => "";
        public void Publish(Dictionary<string, object> body) => hub.Publish(body);
        public void Dispose() => hub.Dispose();
    }

    /// <summary>A runner's side: one WebSocket to the admin's hub, reconnecting with backoff.</summary>
    public sealed class WsRelay : IRelay
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        readonly JoinCode code;
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        volatile Peer peer;
        volatile string lastError = "";
        volatile bool everConnected, denied, waiting;
        long retryAt; // ticks; DateTime isn't volatile-safe
        int connections;
        List<string> found = new List<string>(); // what the pointer last said

        public ConcurrentQueue<Message> Inbox { get; } = new ConcurrentQueue<Message>();
        public string LastError => lastError;
        public DateTime RetryAt => new DateTime(Interlocked.Read(ref retryAt));
        public int Connections => Volatile.Read(ref connections);

        public Link Link =>
            code == null ? Link.NoCode
            : denied ? Link.Denied
            : peer != null ? Link.Connected
            : !waiting ? Link.Connecting
            : everConnected ? Link.Lost
            : Link.Retrying;

        /// <param name="code">null when there's no valid join code: the relay just reports that.</param>
        public WsRelay(JoinCode code)
        {
            this.code = code;
            if (code != null)
                Task.Run(Run);
        }

        /// <summary>Messages sent while disconnected are dropped: the next update carries the current state anyway.</summary>
        public void Publish(Dictionary<string, object> body) => peer?.Send(Json.Serialize(body));

        /// <summary>The first of the addresses that answers; the error of the last one if none does.</summary>
        async Task<ClientChannel> ConnectAny(List<string> hosts)
        {
            for (var i = 0; ; i++)
            {
                try
                {
                    return await ClientChannel.ConnectAsync(JoinCode.UriFor(hosts[i]), stop.Token);
                }
                catch (Exception) when (i < hosts.Count - 1 && !stop.IsCancellationRequested)
                {
                }
            }
        }

        /// <summary>
        /// Where the pointer says the admin is; when ntfy can't be reached (down, rate-limited), what it said last
        /// time, so a runner who drops mid-run gets back in while the admin's tunnel is still up.
        /// </summary>
        async Task<List<string>> Resolve()
        {
            try
            {
                return found = await Pointer.ResolveAsync(code.Host, code.Secret);
            }
            catch (Exception) when (found.Count > 0 && !stop.IsCancellationRequested)
            {
                return found;
            }
        }

        async Task Run()
        {
            var delay = TimeSpan.FromSeconds(1);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var hosts = code.IsPointer ? await Resolve() : new List<string> { code.Host };
                    if (hosts.Count == 0)
                        throw new IOException("the admin isn't hosting");
                    var channel = await ConnectAny(hosts);
                    var connection = new Peer(channel);
                    await connection.SendAsync("hello " + code.Secret);
                    var first = await channel.ReceiveAsync();
                    if (first == "denied")
                    {
                        denied = true;
                        connection.Dispose();
                        return;
                    }
                    if (first != "ok")
                        throw new IOException("the admin's app closed the connection");
                    peer = connection;
                    everConnected = true;
                    Interlocked.Increment(ref connections);
                    lastError = "";
                    delay = TimeSpan.FromSeconds(1);
                    string line;
                    while ((line = await channel.ReceiveAsync()) != null)
                    {
                        var tab = line.IndexOf('\t');
                        if (tab < 0) continue;
                        Inbox.Enqueue(new Message
                        {
                            Age = TimeSpan.FromMilliseconds(long.Parse(line.Substring(0, tab))),
                            Body = Json.Deserialize<Dictionary<string, object>>(line.Substring(tab + 1)),
                        });
                    }
                    lastError = "the admin closed the connection";
                }
                catch (Exception e) when (!stop.IsCancellationRequested)
                {
                    lastError = e.GetBaseException().Message;
                }
                peer?.Dispose();
                peer = null;
                waiting = true;
                Interlocked.Exchange(ref retryAt, (DateTime.Now + delay).Ticks);
                try
                {
                    await Task.Delay(delay, stop.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            peer?.Dispose();
        }
    }
}
