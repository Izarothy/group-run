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
            public bool ForRunners;
            public long At; // hub stopwatch ms
        }

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        readonly WsServer server;
        readonly string secret;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly List<Entry> history = new List<Entry>();
        readonly List<Peer> peers = new List<Peer>();
        readonly CancellationTokenSource stop = new CancellationTokenSource();

        /// <summary>The admin's own copy of the app reads from here, detail messages included.</summary>
        public readonly ConcurrentQueue<Message> Local = new ConcurrentQueue<Message>();

        public int Port => server.Port;

        /// <param name="port">0 picks a free port (the sim does that).</param>
        public Hub(int port, string secret)
        {
            this.secret = secret;
            server = new WsServer(port);
            Task.Run(Accept);
        }

        public void Publish(Dictionary<string, object> body) => Pass(Json.Serialize(body), body);

        void Pass(string json, Dictionary<string, object> body)
        {
            var entry = new Entry
            {
                Json = json,
                ForRunners = (body["t"] as string) != "detail",
                At = clock.ElapsedMilliseconds,
            };
            List<Peer> targets;
            lock (history)
            {
                history.Add(entry);
                lock (peers)
                    targets = entry.ForRunners ? peers.ToList() : new List<Peer>();
            }
            Local.Enqueue(new Message { Body = body, Age = TimeSpan.Zero });
            foreach (var peer in targets)
                peer.Send("0\t" + json);
        }

        async Task Accept()
        {
            while (!stop.IsCancellationRequested)
            {
                IChannel channel;
                try
                {
                    channel = await server.AcceptAsync();
                }
                catch (Exception) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e) when (e is SocketException || e is IOException || e is ObjectDisposedException)
                {
                    continue;
                }
                _ = Task.Run(() => Serve(channel));
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
                    foreach (var e in history.Where(e => e.ForRunners))
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
                    catch (ArgumentException)
                    {
                        continue; // not JSON: skip it rather than drop the runner
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

        async Task Run()
        {
            var delay = TimeSpan.FromSeconds(1);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var hosts = code.IsPointer ? await Pointer.ResolveAsync(code.Host, code.Secret) : new List<string> { code.Host };
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
