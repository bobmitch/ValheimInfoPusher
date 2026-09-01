using System;
using System.Globalization;
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

        public bool IsReady => _registered && ZRoutedRpc.instance != null;

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
                var rpc = ZRoutedRpc.instance;
                if (rpc == null)
                {
                    _log.Warn("no ZRoutedRpc yet; the code channel will register later");
                    return;
                }

                rpc.Register<string, long>(RpcAnnounce, OnRpcAnnounce);
                rpc.Register(RpcRequest, OnRpcRequest);
                _registered = true;
                _log.Info("code channel registered");
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
                ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcRequest);
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
                ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcAnnounce, code, epoch);
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
                var player = Player.m_localPlayer;
                if (player == null) return;
                Chat.instance?.SendText(Talker.Type.Normal, message);
            }
            catch (Exception ex)
            {
                _log.Warn("could not send on the chat channel: " + ex.Message);
            }
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
