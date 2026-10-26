using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GroupRun
{
    /// <summary>One connection carrying text messages, one line of the relay protocol each.</summary>
    interface IChannel : IDisposable
    {
        /// <summary>The next message, or null once the other side has gone.</summary>
        Task<string> ReceiveAsync();
        Task SendAsync(string text);
    }

    /// <summary>
    /// A minimal WebSocket server on 127.0.0.1 for the hub: the HTTP upgrade handshake and text frames, enough for
    /// browsers' and .NET's clients and for Cloudflare's tunnel in front of it. Listening only on loopback means
    /// nothing outside this PC can reach it directly; runners come in through the tunnel.
    /// </summary>
    sealed class WsServer : IDisposable
    {
        const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        readonly TcpListener listener;

        public WsServer(int port)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        /// <summary>The next client that completed the WebSocket handshake.</summary>
        public async Task<IChannel> AcceptAsync()
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                try
                {
                    var stream = client.GetStream();
                    var key = await ReadHandshake(stream);
                    if (key == null)
                    {
                        var refuse = Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(refuse, 0, refuse.Length);
                        client.Close();
                        continue;
                    }
                    string accept;
                    using (var sha = SHA1.Create())
                        accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + Magic)));
                    var reply = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                    await stream.WriteAsync(reply, 0, reply.Length);
                    return new ServerChannel(client);
                }
                catch (IOException)
                {
                    client.Close();
                }
            }
        }

        /// <summary>Read the HTTP request headers; the Sec-WebSocket-Key, or null if it isn't a WebSocket upgrade.</summary>
        static async Task<string> ReadHandshake(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, length, buffer.Length - length);
                if (read == 0) return null;
                length += read;
                var text = Encoding.ASCII.GetString(buffer, 0, length);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0) continue;
                foreach (var line in text.Substring(0, end).Split(new[] { "\r\n" }, StringSplitOptions.None))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0 && line.Substring(0, colon).Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                        return line.Substring(colon + 1).Trim();
                }
                return null;
            }
            return null;
        }

        public void Dispose() => listener.Stop();

        /// <summary>The server end: unmasked frames out, masked frames in (RFC 6455).</summary>
        sealed class ServerChannel : IChannel
        {
            readonly TcpClient client;
            readonly NetworkStream stream;
            readonly SemaphoreSlim writing = new SemaphoreSlim(1, 1);

            public ServerChannel(TcpClient client)
            {
                this.client = client;
                client.NoDelay = true;
                stream = client.GetStream();
            }

            public async Task<string> ReceiveAsync()
            {
                var message = new MemoryStream();
                while (true)
                {
                    var head = await ReadExactly(2);
                    if (head == null) return null;
                    var fin = (head[0] & 0x80) != 0;
                    var opcode = head[0] & 0x0F;
                    var masked = (head[1] & 0x80) != 0;
                    long length = head[1] & 0x7F;
                    if (length == 126)
                    {
                        var ext = await ReadExactly(2);
                        if (ext == null) return null;
                        length = (ext[0] << 8) | ext[1];
                    }
                    else if (length == 127)
                    {
                        var ext = await ReadExactly(8);
                        if (ext == null) return null;
                        length = 0;
                        for (var i = 0; i < 8; i++) length = (length << 8) | ext[i];
                    }
                    if (length > 1 << 20) return null; // nothing in this protocol is that big
                    var mask = masked ? await ReadExactly(4) : null;
                    if (masked && mask == null) return null;
                    var payload = length == 0 ? new byte[0] : await ReadExactly((int)length);
                    if (payload == null) return null;
                    if (masked)
                        for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                    switch (opcode)
                    {
                        case 0x8: // close
                            return null;
                        case 0x9: // ping: answer with the same payload
                            await Send(0xA, payload);
                            continue;
                        case 0xA: // pong
                            continue;
                        default: // text, binary or a continuation
                            message.Write(payload, 0, payload.Length);
                            if (fin)
                                return Encoding.UTF8.GetString(message.ToArray());
                            continue;
                    }
                }
            }

            public Task SendAsync(string text) => Send(0x1, Encoding.UTF8.GetBytes(text));

            async Task Send(int opcode, byte[] payload)
            {
                var header = new MemoryStream();
                header.WriteByte((byte)(0x80 | opcode));
                if (payload.Length < 126)
                    header.WriteByte((byte)payload.Length);
                else if (payload.Length <= ushort.MaxValue)
                {
                    header.WriteByte(126);
                    header.WriteByte((byte)(payload.Length >> 8));
                    header.WriteByte((byte)payload.Length);
                }
                else
                {
                    header.WriteByte(127);
                    for (var i = 7; i >= 0; i--) header.WriteByte((byte)((long)payload.Length >> (8 * i)));
                }
                await writing.WaitAsync();
                try
                {
                    var h = header.ToArray();
                    await stream.WriteAsync(h, 0, h.Length);
                    await stream.WriteAsync(payload, 0, payload.Length);
                    await stream.FlushAsync();
                }
                finally
                {
                    writing.Release();
                }
            }

            async Task<byte[]> ReadExactly(int count)
            {
                var buffer = new byte[count];
                var read = 0;
                while (read < count)
                {
                    var n = await stream.ReadAsync(buffer, read, count - read);
                    if (n == 0) return null;
                    read += n;
                }
                return buffer;
            }

            public void Dispose() => client.Close();
        }
    }

    /// <summary>The runner's end, on .NET's own WebSocket client (ws:// locally, wss:// through the tunnel).</summary>
    sealed class ClientChannel : IChannel
    {
        readonly ClientWebSocket socket;
        readonly SemaphoreSlim writing = new SemaphoreSlim(1, 1);

        ClientChannel(ClientWebSocket socket) => this.socket = socket;

        public static async Task<ClientChannel> ConnectAsync(Uri uri, CancellationToken cancel)
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20); // keeps the tunnel from closing an idle connection
            socket.Options.Proxy = null; // skips Windows' proxy auto-detection, which costs seconds per connection
            await socket.ConnectAsync(uri, cancel);
            return new ClientChannel(socket);
        }

        public async Task<string> ReceiveAsync()
        {
            var buffer = new byte[16384];
            var message = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    return null;
                }
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(message.ToArray());
            }
        }

        public async Task SendAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await writing.WaitAsync();
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                writing.Release();
            }
        }

        public void Dispose()
        {
            try { socket.Abort(); } catch (Exception) { }
            socket.Dispose();
        }
    }
}
