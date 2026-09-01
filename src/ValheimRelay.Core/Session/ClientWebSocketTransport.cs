using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ValheimRelay.Core.Session
{
    /// <summary>
    /// <see cref="IRelayTransport"/> over <see cref="ClientWebSocket"/>.
    /// <para>
    /// §4.4 says to try <c>ClientWebSocket</c> first, because it means bundling
    /// nothing at all. It is in the netstandard2.0 surface and in the .NET
    /// Framework BCL from 4.5, so this compiles and runs with zero package
    /// references — which is the outcome that matters, since two mods shipping
    /// different builds of the same library into <c>BepInEx/plugins</c> is the
    /// classic way to break someone's modpack.
    /// </para>
    /// <para>
    /// <b>M0(a) is still open.</b> What is verified here is that this drives the
    /// relay correctly on a modern runtime. Whether Valheim's Mono build carries
    /// a working <c>ClientWebSocket</c> — TLS included — is a question only the
    /// game can answer, and §9's M0 spike is where it gets answered. If it does
    /// not, only this file is replaced.
    /// </para>
    /// <para>
    /// Control pings are answered by the runtime's own receive loop, so the 60 s
    /// server read deadline (§1.5) is satisfied as long as something is calling
    /// <see cref="ClientWebSocket.ReceiveAsync"/> — which the receive loop below
    /// always is. That is the failure §4.2 warns about, and it is structural
    /// here rather than something to remember.
    /// </para>
    /// </summary>
    public sealed class ClientWebSocketTransport : IRelayTransport, IDisposable
    {
        private readonly ILog _log;
        private readonly int _sendQueueCapacity;
        private readonly object _gate = new object();

        private ClientWebSocket? _socket;
        private CancellationTokenSource? _cancellation;
        private BlockingCollection<string>? _sendQueue;
        private int _generation;
        private bool _disposed;

        public ClientWebSocketTransport(ILog log, int sendQueueCapacity = 256)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _sendQueueCapacity = sendQueueCapacity;
        }

        public TransportState State { get; private set; } = TransportState.Closed;

        public event Action? Opened;
        public event Action<string>? Received;
        public event Action<int, string>? Closed;

        public void Connect(string relayUrl, string? code, string? token)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClientWebSocketTransport));

            // Abandon any previous connection. Its Closed event may still be in
            // flight; RelaySession expects that and ignores it.
            AbandonCurrent();

            var uri = BuildUri(relayUrl, code, token);
            var socket = new ClientWebSocket();
            var cancellation = new CancellationTokenSource();
            var queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), _sendQueueCapacity);
            int generation;

            lock (_gate)
            {
                _socket = socket;
                _cancellation = cancellation;
                _sendQueue = queue;
                generation = ++_generation;
                State = TransportState.Connecting;
            }

            _ = Task.Run(() => RunAsync(socket, queue, cancellation, uri, generation));
        }

        /// <summary>
        /// Builds the query of §1.1. The code is passed through exactly as the
        /// player typed it — the relay normalises forgivingly and the mod
        /// deliberately does not reimplement those rules.
        /// </summary>
        internal static Uri BuildUri(string relayUrl, string? code, string? token)
        {
            if (string.IsNullOrEmpty(relayUrl)) throw new ArgumentException("relay URL required", nameof(relayUrl));

            var builder = new UriBuilder(relayUrl);
            if (builder.Scheme == Uri.UriSchemeHttp) builder.Scheme = "ws";
            else if (builder.Scheme == Uri.UriSchemeHttps) builder.Scheme = "wss";

            var query = new StringBuilder(builder.Query.TrimStart('?'));
            void Append(string name, string value)
            {
                if (query.Length > 0) query.Append('&');
                query.Append(name).Append('=').Append(Uri.EscapeDataString(value));
            }

            Append("role", "mod");
            if (!string.IsNullOrEmpty(code)) Append("code", code!);
            if (!string.IsNullOrEmpty(token)) Append("token", token!);

            builder.Query = query.ToString();
            return builder.Uri;
        }

        public bool Send(string frame)
        {
            var queue = _sendQueue;
            if (queue == null || State != TransportState.Open) return false;

            try
            {
                // Non-blocking: a full queue is backpressure, which RelaySession
                // handles by holding the frame rather than losing it.
                return queue.TryAdd(frame);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public void Close(int code, string reason)
        {
            AbandonCurrent();
            SetClosed(code, reason);
        }

        private async Task RunAsync(
            ClientWebSocket socket,
            BlockingCollection<string> queue,
            CancellationTokenSource cancellation,
            Uri uri,
            int generation)
        {
            var closeCode = (int)WebSocketCloseStatus.NormalClosure;
            var closeReason = string.Empty;

            try
            {
                await socket.ConnectAsync(uri, cancellation.Token).ConfigureAwait(false);

                if (!IsCurrent(generation)) return;
                State = TransportState.Open;
                Opened?.Invoke();

                var sender = Task.Run(() => SendLoopAsync(socket, queue, cancellation.Token));
                await ReceiveLoopAsync(socket, cancellation, generation).ConfigureAwait(false);

                cancellation.Cancel();
                await sender.ConfigureAwait(false);

                if (socket.CloseStatus.HasValue)
                {
                    closeCode = (int)socket.CloseStatus.Value;
                    closeReason = socket.CloseStatusDescription ?? string.Empty;
                }
            }
            catch (OperationCanceledException)
            {
                return; // a deliberate abandon; Close already reported it
            }
            catch (WebSocketException ex)
            {
                // 1006: the connection went away without a close frame. The
                // session treats it as an ordinary drop and reconnects.
                closeCode = 1006;
                closeReason = ex.Message;
            }
            catch (Exception ex)
            {
                closeCode = 1006;
                closeReason = ex.Message;
                _log.Warn("relay transport error: " + ex.Message);
            }
            finally
            {
                queue.CompleteAdding();
                socket.Dispose();
            }

            if (IsCurrent(generation)) SetClosed(closeCode, closeReason);
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationTokenSource cancellation, int generation)
        {
            // One frame's worth; the relay caps frames at MAX_MESSAGE_BYTES.
            var buffer = new byte[Protocol.FrameCodec.MaxFrameBytes];
            var assembled = new StringBuilder();

            while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
            {
                var segment = new ArraySegment<byte>(buffer);
                var result = await socket.ReceiveAsync(segment, cancellation.Token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text) continue;

                assembled.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var text = assembled.ToString();
                assembled.Length = 0;

                if (IsCurrent(generation)) Received?.Invoke(text);
            }
        }

        private static async Task SendLoopAsync(ClientWebSocket socket, BlockingCollection<string> queue, CancellationToken token)
        {
            try
            {
                foreach (var frame in queue.GetConsumingEnumerable(token))
                {
                    if (socket.State != WebSocketState.Open) return;
                    var bytes = Encoding.UTF8.GetBytes(frame);
                    await socket.SendAsync(
                        new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
                // The receive loop reports the close; nothing to add here.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private bool IsCurrent(int generation)
        {
            lock (_gate) return _generation == generation;
        }

        private void AbandonCurrent()
        {
            ClientWebSocket? socket;
            CancellationTokenSource? cancellation;
            BlockingCollection<string>? queue;

            lock (_gate)
            {
                socket = _socket;
                cancellation = _cancellation;
                queue = _sendQueue;
                _socket = null;
                _cancellation = null;
                _sendQueue = null;
                _generation++;
            }

            try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
            try { queue?.CompleteAdding(); } catch (ObjectDisposedException) { }

            if (socket != null && socket.State == WebSocketState.Open)
            {
                try
                {
                    // Fire and forget: the session has already moved on, and a
                    // courteous close frame is not worth blocking a game frame.
                    _ = socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Nothing useful to do; the socket is going away regardless.
                }
            }
        }

        private void SetClosed(int code, string reason)
        {
            if (State == TransportState.Closed) return;
            State = TransportState.Closed;
            Closed?.Invoke(code, reason);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            AbandonCurrent();
            State = TransportState.Closed;
        }
    }
}
