using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using ValheimRelay.Core.Session;

namespace ValheimRelay.Plugin
{
    /// <summary>
    /// Carries the session code between modded clients over Valheim's own
    /// network (PLAN.md §5.1), so nobody has to type it.
    /// <para>
    /// Two channels, tried in order. The routed RPC is the clean answer, and
    /// whether a vanilla dedicated server forwards an RPC whose name it does not
    /// know is §6 — the project's main open question, which M0(b) settles
    /// empirically. Until it is settled this degrades rather than breaks: if no
    /// peer acknowledges over RPC within the discovery window, it falls back to
    /// chat, which is itself a routed RPC the server demonstrably relays.
    /// </para>
    /// <para>
    /// The fallback has a cost §8 does not spell out but should: the code is the
    /// credential, and a chat-borne code is visible to <em>unmodded</em> players
    /// in the world, who will see one odd line. They are already in your world,
    /// so the exposure is small, but it is real — which is why the chat channel
    /// is a fallback and not the default, and why it sends once rather than on
    /// the heartbeat.
    /// </para>
    /// </summary>
    public sealed class GameCodeChannel : IGameChannel
    {
        private const string RpcAnnounce = "ValheimRelay_Code";
        private const string RpcRequest = "ValheimRelay_CodeRequest";

        /// <summary>Short, because unmodded players may see it (§8).</summary>
        private const string ChatPrefix = "[vrelay]";

        private readonly ILog _log;
        private readonly Func<bool> _chatFallbackEnabled;

        private bool _registered;
        private bool _rpcAcknowledged;
        private bool _useChatFallback;

        public GameCodeChannel(ILog log, Func<bool>? chatFallbackEnabled = null)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _chatFallbackEnabled = chatFallbackEnabled ?? (() => true);
        }

        public bool IsReady
        {
            get
            {
                if (!_registered) return false;
                try
                {
                    return HasRouter();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>True once a peer has answered over RPC, so §6 is settled for this session.</summary>
        public bool RpcWorks => _rpcAcknowledged;

        public event Action<CodeAnnouncement>? CodeAnnounced;
        public event Action? CodeRequested;

        /// <summary>Called from the <c>Game.Start</c> patch — client and server both (§4.3).</summary>
        public void Register()
        {
            if (_registered) return;

            try
            {
                if (!RegisterHandlers())
                {
                    _log.Warn("no ZRoutedRpc yet; the code channel will register later");
                    return;
                }

                _registered = true;

                if (EverybodyFromGame)
                {
                    _log.Info("code channel registered");
                }
                else
                {
                    _log.Warn(
                        "code channel registered, but this build has no readable ZRoutedRpc.Everybody, so the "
                        + "broadcast target is assumed to be " + EverybodyFallback + ". If peers never see each "
                        + "other's codes, this is the first thing to check against the current game version.");
                }
            }
            catch (Exception ex)
            {
                // Fail soft (§11.4): without the RPC the chat fallback still works.
                _log.Warn("could not register the code RPC; falling back to chat: " + ex.Message);
                _useChatFallback = true;
            }
        }

        public void Reset()
        {
            _rpcAcknowledged = false;
            _useChatFallback = false;
        }

        /// <summary>
        /// Called when the discovery window closes with no RPC traffic seen.
        /// Switching here rather than at registration time is what makes the
        /// degradation automatic instead of a config the player has to find.
        /// </summary>
        public void EnableChatFallback()
        {
            if (_useChatFallback || _rpcAcknowledged) return;
            _useChatFallback = true;
            _log.Info("no peer answered over RPC; using the chat channel for the session code");
        }

        public void RequestCode()
        {
            try
            {
                SendRequestRpc();
            }
            catch (Exception ex)
            {
                _log.Warn("could not ask peers for the code: " + ex.Message);
            }

            if (_useChatFallback && _chatFallbackEnabled())
            {
                SendChat(ChatPrefix + " ?");
            }
        }

        public void AnnounceCode(string code, long epoch)
        {
            if (string.IsNullOrEmpty(code)) return;

            try
            {
                SendAnnounceRpc(code, epoch);
            }
            catch (Exception ex)
            {
                _log.Warn("could not announce the code over RPC: " + ex.Message);
            }

            if (_useChatFallback && _chatFallbackEnabled())
            {
                SendChat(ChatPrefix + " " + code + " " + epoch.ToString(CultureInfo.InvariantCulture));
            }
        }

        // ------------------------------------------------- the game's RPC router

        /// <summary>
        /// Every call below is isolated behind its own <see cref="MethodImplOptions.NoInlining"/>
        /// method, and every caller wraps the call in a try/catch. That looks
        /// like ceremony and is not.
        /// <para>
        /// Mono resolves field and method tokens when it COMPILES a method, not
        /// when it reaches the offending instruction, and it reports the failure
        /// at the call site in the caller rather than inside the callee. So a
        /// try/catch wrapped around a renamed game symbol in the SAME method
        /// never runs — the method throws on entry, before its catch block is
        /// live. That is how an unresolvable <c>ZRoutedRpc.Everybody</c> escaped
        /// this file's existing handlers, propagated out of the
        /// <c>Player.OnSpawned</c> postfix, and aborted <c>Game.SpawnPlayer</c>
        /// mid-spawn — leaving the game in an endless respawn loop.
        /// </para>
        /// <para>
        /// Splitting each game touch into its own uninlinable method moves that
        /// compile-time failure to a call instruction that IS inside a try
        /// block. NoInlining is load-bearing: inline the helper back into its
        /// caller and the failure moves back up to the caller's entry with it.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool HasRouter() => ZRoutedRpc.instance != null;

        /// <summary>False when there is no router yet, so the caller can try again later.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool RegisterHandlers()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return false;

            rpc.Register<string, long>(RpcAnnounce, OnRpcAnnounce);
            rpc.Register(RpcRequest, OnRpcRequest);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendRequestRpc()
            => ZRoutedRpc.instance?.InvokeRoutedRPC(EverybodyPeer, RpcRequest);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendAnnounceRpc(string code, long epoch)
            => ZRoutedRpc.instance?.InvokeRoutedRPC(EverybodyPeer, RpcAnnounce, code, epoch);

        /// <summary>
        /// The "route this to every peer" target id.
        /// <para>
        /// Looked up rather than referenced, because the compile-time reference
        /// to <c>ZRoutedRpc.Everybody</c> is exactly what the Valheim 1.0
        /// (Unity 6) update broke. The field did not move or get renamed: it
        /// changed from <c>public static readonly long</c> to <c>public const
        /// long</c>. A const has no storage, so the <c>ldsfld</c> this mod was
        /// compiled to emit has nothing to bind to, and every client running it
        /// dropped into an endless respawn loop. Recompiling against the 1.0
        /// assemblies fixes it too — the compiler emits the literal instead —
        /// but only for the build it was compiled against.
        /// </para>
        /// <para>
        /// Reflection is what makes one build work on both. A const is a
        /// <c>static literal</c> field in metadata, so <c>GetField</c> finds it
        /// and <c>GetValue(null)</c> returns the constant out of the metadata
        /// without any storage to read — the same call that reads the old
        /// <c>static readonly</c> field on a pre-1.0 install.
        /// </para>
        /// </summary>
        private static readonly long EverybodyPeer;

        /// <summary>False when <see cref="EverybodyPeer"/> is the fallback rather than the build's own value.</summary>
        private static readonly bool EverybodyFromGame;

        static GameCodeChannel()
        {
            EverybodyPeer = ResolveEverybody(out var fromGame);
            EverybodyFromGame = fromGame;
        }

        /// <summary>
        /// Zero is what "everybody" has been for the life of the game, 1.0
        /// included, so it is the fallback — but it is a guess, and a guess that
        /// silently routes every announcement to one peer would look like the
        /// channel simply not working. <see cref="Register"/> says so in the log
        /// when it is used.
        /// </summary>
        private const long EverybodyFallback = 0L;

        private static long ResolveEverybody(out bool fromGame)
        {
            fromGame = false;

            try
            {
                const BindingFlags anyStatic =
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;

                // A field first — const or static readonly, both are fields in
                // metadata and both read the same way — then a property, in case
                // some later build turns it into one.
                object? raw = typeof(ZRoutedRpc).GetField("Everybody", anyStatic)?.GetValue(null)
                    ?? typeof(ZRoutedRpc).GetProperty("Everybody", anyStatic)?.GetValue(null);

                if (raw == null) return EverybodyFallback;

                // Converted rather than cast: the width of the id is not the
                // part worth being strict about.
                if (raw is long value)
                {
                    fromGame = true;
                    return value;
                }

                if (raw is IConvertible convertible)
                {
                    fromGame = true;
                    return convertible.ToInt64(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
                // Nothing here can log: this runs from a static initialiser,
                // before any instance exists. Register reports it instead.
            }

            return EverybodyFallback;
        }

        // ---------------------------------------------------------------- RPC

        private void OnRpcAnnounce(long sender, string code, long epoch)
        {
            _rpcAcknowledged = true;
            Raise(code, epoch, sender);
        }

        private void OnRpcRequest(long sender)
        {
            _rpcAcknowledged = true;
            CodeRequested?.Invoke();
        }

        // --------------------------------------------------------------- chat

        /// <summary>
        /// Consumes a magic-prefixed chat line. Returns true when the line was
        /// ours and the caller should hide it — the Harmony patch does nothing
        /// but forward here.
        /// </summary>
        public bool TryConsumeChat(long sender, string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            var line = text!.Trim();
            if (!line.StartsWith(ChatPrefix, StringComparison.Ordinal)) return false;

            var rest = line.Substring(ChatPrefix.Length).Trim();
            if (rest == "?")
            {
                CodeRequested?.Invoke();
                return true;
            }

            var parts = rest.Split(' ');
            if (parts.Length >= 1 && parts[0].Length > 0)
            {
                var epoch = 1L;
                if (parts.Length >= 2) long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch);
                Raise(parts[0], epoch, sender);
            }

            return true;
        }

        private void SendChat(string message)
        {
            try
            {
                SendChatLine(message);
            }
            catch (Exception ex)
            {
                _log.Warn("could not send on the chat channel: " + ex.Message);
            }
        }

        /// <summary>Isolated for the reason given on <see cref="HasRouter"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendChatLine(string message)
        {
            if (Player.m_localPlayer == null) return;
            Chat.instance?.SendText(Talker.Type.Normal, message);
        }

        private void Raise(string code, long epoch, long sender)
        {
            if (string.IsNullOrEmpty(code)) return;

            // Pass the code through untouched: §1.1 has the relay normalise
            // forgivingly, and a second implementation of those rules here is a
            // second thing to keep in sync.
            CodeAnnounced?.Invoke(new CodeAnnouncement(code, epoch, sender));
        }
    }
}
