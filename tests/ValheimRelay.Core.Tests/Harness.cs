using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ValheimRelay.Core.Json;
using ValheimRelay.Core.Protocol;
using ValheimRelay.Core.Session;

namespace ValheimRelay.Core.Tests
{
    /// <summary>Runs <c>tools/devrelay</c> on an ephemeral port for the duration of a test.</summary>
    public sealed class DevRelay : IDisposable
    {
        private readonly Process _process;

        private DevRelay(Process process, string url)
        {
            _process = process;
            Url = url;
        }

        /// <summary>The <c>ws://…/ws</c> URL the relay is actually listening on.</summary>
        public string Url { get; }

        /// <summary>
        /// Returns null only when the Go toolchain is genuinely absent, which is
        /// the one condition worth skipping for.
        /// <para>
        /// Everything else propagates and fails the test. Swallowing all
        /// exceptions here once let the entire fixture go missing from the
        /// repository — a .gitignore glob had un-tracked it — while CI stayed
        /// green, because seven integration tests quietly skipped instead of
        /// reporting that they had nothing to run against. A test that cannot
        /// run must say so loudly enough to fail a build.
        /// </para>
        /// </summary>
        public static DevRelay? TryStart(string extraArgs = "")
        {
            try
            {
                return Start(extraArgs);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No `go` on PATH.
                return null;
            }
        }

        public static DevRelay Start(string extraArgs = "")
        {
            var dir = FindDevRelayDirectory();

            var info = new ProcessStartInfo("go")
            {
                // :0 lets the OS pick a free port, so tests never collide.
                Arguments = "run ./cmd/devrelay -addr 127.0.0.1:0 " + extraArgs,
                WorkingDirectory = dir,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            var process = Process.Start(info) ?? throw new InvalidOperationException("could not start the go toolchain");

            // The relay logs its listening address in a fixed form; `go run`
            // also has to compile first, so allow generously for a cold cache.
            var url = ReadListenUrl(process, TimeSpan.FromSeconds(90));
            if (url == null)
            {
                TryKill(process);
                throw new InvalidOperationException("devrelay did not report a listen address");
            }

            return new DevRelay(process, url);
        }

        private static string? ReadListenUrl(Process process, TimeSpan timeout)
        {
            var result = new TaskCompletionSource<string?>();

            var reader = Task.Run(() =>
            {
                string? line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    var match = Regex.Match(line, @"(ws://[^\s]+/ws)");
                    if (match.Success)
                    {
                        result.TrySetResult(match.Groups[1].Value);
                        break;
                    }
                }
                result.TrySetResult(null);
            });

            return result.Task.Wait(timeout) ? result.Task.Result : null;
        }

        private static string FindDevRelayDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "tools", "devrelay");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                "could not locate tools/devrelay from " + AppContext.BaseDirectory +
                ". The dev relay fixture is missing from the working tree — check it is tracked in git.");
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        public void Dispose()
        {
            TryKill(_process);
            _process.Dispose();
        }
    }

    /// <summary>
    /// A <see cref="RelaySession"/> with a real transport, pumped on a
    /// background thread the way <c>RelayBehaviour.Update</c> would pump it.
    /// </summary>
    public sealed class SessionHost : IDisposable
    {
        private readonly ClientWebSocketTransport _transport;
        private readonly FakeGameChannel _channel = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ConcurrentQueue<Action<RelaySession>> _pending = new();
        private readonly Task _pump;

        private readonly object _positionGate = new();
        private double _x;
        private double _z;
        private volatile bool _moving;

        public SessionHost(string relayUrl, string playerName = "Bob", string uid = "vh_bob")
        {
            Log = new FakeLog();
            _transport = new ClientWebSocketTransport(Log);

            Options = new SessionOptions
            {
                RelayUrl = relayUrl,
                DiscoveryWindow = TimeSpan.FromMilliseconds(200),
                CreationStaggerSpread = TimeSpan.Zero,
                DiscoveryRetryInterval = TimeSpan.FromSeconds(1),
                PositionInterval = TimeSpan.FromMilliseconds(100)
            };

            PlayerName = playerName;
            Uid = uid;

            Session = new RelaySession(
                Options, _transport, _channel, new FakePeerView { SelfPeerId = 1 },
                new SystemClock(), Log, new ReclaimStore(new InMemoryReclaimStorage(), Log));

            _pump = Task.Run(PumpAsync);
        }

        public RelaySession Session { get; }
        public SessionOptions Options { get; }
        public FakeLog Log { get; }
        public string PlayerName { get; }
        public string Uid { get; }

        public void Start()
        {
            Invoke(s => s.Start(new SessionIdentity(
                PlayerName, Uid, "1.0.0-test",
                new WorldInfo("Midgard", "hAbC12dEf", -1234567, "world-" + Uid))));
        }

        /// <summary>Queues work onto the pump, so nothing touches the session off-thread.</summary>
        public void Invoke(Action<RelaySession> action) => _pending.Enqueue(action);

        public void Announce(string code) => _channel.PeerAnnounces(code, epoch: 1);

        public void Move(double x, double z)
        {
            lock (_positionGate)
            {
                _x = x;
                _z = z;
            }
            _moving = true;
        }

        /// <summary>Kill the socket without a close frame, as a network drop would.</summary>
        public void ForceDrop() => Invoke(_ => _transport.Close(1006, "test drop"));

        public async Task<string> StartAndWaitForCodeAsync()
        {
            Start();
            await WaitForStateAsync(SessionState.Active);
            return Session.Code ?? throw new InvalidOperationException("active with no code");
        }

        public async Task JoinAsync(string code)
        {
            Start();
            Announce(code);
            await WaitForStateAsync(SessionState.Active);
        }

        public async Task WaitForStateAsync(SessionState state, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (DateTime.UtcNow < deadline)
            {
                if (Session.State == state) return;
                await Task.Delay(25);
            }
            throw new TimeoutException($"session stayed in {Session.State}, expected {state}");
        }

        public async Task<T> WaitAsync<T>(Func<T?> poll, TimeSpan? timeout = null) where T : struct
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (DateTime.UtcNow < deadline)
            {
                if (poll() is T value) return value;
                await Task.Delay(25);
            }
            throw new TimeoutException("expected value never arrived");
        }

        public async Task PumpForAsync(TimeSpan duration) => await Task.Delay(duration);

        private async Task PumpAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                while (_pending.TryDequeue(out var action))
                {
                    action(Session);
                }

                if (_moving)
                {
                    double x, z;
                    lock (_positionGate)
                    {
                        x = _x;
                        z = _z;
                    }

                    Session.SubmitPosition(new PositionSample(
                        x, z, 30, 0, "Meadows", 100, 100, true, false,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                }

                Session.Tick();
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            Session.Dispose();
            _transport.Dispose();
            _cancellation.Dispose();
        }
    }

    /// <summary>A browser map, for asserting what actually reached the far side.</summary>
    public sealed class StubMap : IDisposable
    {
        private readonly ClientWebSocket _socket;
        private readonly ConcurrentQueue<JsonValue> _frames = new();
        private readonly CancellationTokenSource _cancellation = new();

        private StubMap(ClientWebSocket socket)
        {
            _socket = socket;
            _ = Task.Run(ReceiveAsync);
        }

        /// <param name="requestState">
        /// §3.5: a map sends <c>request_state</c> right after its welcome. It is
        /// on by default because a map that does not is simply a broken map —
        /// the roster in <c>welcome</c> carries no world block, so without it the
        /// map does not know which world to draw until the 60 s hello heartbeat.
        /// </param>
        public static async Task<StubMap> ConnectAsync(string relayUrl, string code, bool requestState = true)
        {
            var builder = new UriBuilder(relayUrl) { Query = "role=map&code=" + Uri.EscapeDataString(code) };
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(builder.Uri, CancellationToken.None);

            var map = new StubMap(socket);
            if (requestState)
            {
                await map.WaitForAsync(FrameTypes.Welcome);
                await map.SendAsync("{\"type\":\"request_state\",\"v\":1}");
            }
            return map;
        }

        public async Task SendAsync(string frame)
        {
            var bytes = Encoding.UTF8.GetBytes(frame);
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public void Drain()
        {
            while (_frames.TryDequeue(out _)) { }
        }

        public async Task<JsonValue> WaitForAsync(string type, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (DateTime.UtcNow < deadline)
            {
                if (_frames.TryDequeue(out var frame))
                {
                    if (frame["type"].AsString() == type) return frame;
                    continue;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException($"no {type} frame arrived");
        }

        private async Task ReceiveAsync()
        {
            var buffer = new byte[FrameCodec.MaxFrameBytes];
            try
            {
                while (_socket.State == WebSocketState.Open && !_cancellation.IsCancellationRequested)
                {
                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellation.Token);
                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (JsonParser.TryParse(text, out var frame)) _frames.Enqueue(frame);
                }
            }
            catch (Exception)
            {
                // The test is finishing.
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _socket.Dispose();
            _cancellation.Dispose();
        }
    }

    /// <summary>The real clock, for the integration tests only.</summary>
    public sealed class SystemClock : IClock
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public long UnixTimeMilliseconds => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
