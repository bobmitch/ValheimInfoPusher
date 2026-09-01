using System;
using UnityEngine;
using ValheimRelay.Core.Session;

namespace ValheimRelay.Plugin
{
    /// <summary>
    /// The hotkey panel and the always-visible indicator of §7.
    /// <para>
    /// The indicator is not decoration: a player should never be unsure whether
    /// their position is being broadcast, because the code is a share link and
    /// anyone holding it sees everyone's live position (§8). The panel says that
    /// in a few words rather than presenting the code as a harmless room name.
    /// </para>
    /// </summary>
    public sealed class RelayPanel
    {
        private const int Width = 330;
        private const int Height = 190;

        private readonly RelayBehaviour _behaviour;
        private readonly PluginConfig _config;

        private bool _visible;
        private float _copiedAt = float.NegativeInfinity;
        private GUIStyle? _codeStyle;
        private GUIStyle? _noteStyle;

        public RelayPanel(RelayBehaviour behaviour, PluginConfig config)
        {
            _behaviour = behaviour ?? throw new ArgumentNullException(nameof(behaviour));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public void Toggle() => _visible = !_visible;

        public void Draw()
        {
            if (!_config.Enabled.Value) return;

            EnsureStyles();
            DrawIndicator();

            if (!_visible) return;

            var rect = new Rect(Screen.width - Width - 20, 20, Width, Height);
            GUI.Box(rect, "ValheimRelay");
            GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 26, rect.width - 24, rect.height - 38));

            var code = _behaviour.Code;
            if (code == null)
            {
                GUILayout.Label(DescribeState(_behaviour.State));
            }
            else
            {
                GUILayout.Label(code, _codeStyle);
                GUILayout.Label(DescribeState(_behaviour.State));

                GUILayout.BeginHorizontal();
                var copyLabel = Time.realtimeSinceStartup - _copiedAt < 2f ? "Copied" : CopyButtonLabel();
                if (GUILayout.Button(copyLabel))
                {
                    _behaviour.CopyShareTextToClipboard();
                    _copiedAt = Time.realtimeSinceStartup;
                }
                GUILayout.EndHorizontal();

                // Say what the code actually is. Treating it as a room name is
                // how people end up pasting it somewhere public (§8).
                GUILayout.Label(
                    "Anyone with this code can watch everyone in this session move, "
                    + "for as long as it lasts. Share it like a link, not a name.",
                    _noteStyle);
            }

            if (_behaviour.State == SessionState.Blocked && GUILayout.Button("Retry"))
            {
                _behaviour.RetryAfterRoomFull();
            }

            GUILayout.EndArea();
        }

        private string CopyButtonLabel()
            => string.IsNullOrEmpty(_config.MapUrl.Value?.Trim()) ? "Copy code" : "Copy map link";

        /// <summary>A small always-visible marker while the session is live (§7).</summary>
        private void DrawIndicator()
        {
            var state = _behaviour.State;
            if (state == SessionState.Idle || state == SessionState.Stopped) return;

            var sharing = _config.ShareMyPosition.Value;
            var colour = state switch
            {
                SessionState.Active => sharing ? new Color(0.49f, 0.78f, 0.89f) : new Color(0.7f, 0.7f, 0.7f),
                SessionState.Blocked => new Color(0.9f, 0.45f, 0.45f),
                _ => new Color(0.9f, 0.8f, 0.4f)
            };

            var label = state switch
            {
                SessionState.Active => sharing ? "relay ●" : "relay ○ (hidden)",
                SessionState.Blocked => "relay ✕ full",
                _ => "relay …"
            };

            var previous = GUI.color;
            GUI.color = colour;
            GUI.Label(new Rect(Screen.width - 130, Screen.height - 28, 120, 20), label);
            GUI.color = previous;
        }

        private static string DescribeState(SessionState state) => state switch
        {
            SessionState.Discovering => "looking for a session…",
            SessionState.Creating => "creating a session…",
            SessionState.Joining => "joining…",
            SessionState.Active => "connected",
            SessionState.Reconnecting => "reconnecting…",
            SessionState.Blocked => "this session is full (16 players)",
            SessionState.Stopped => "not connected",
            _ => "idle"
        };

        private void EnsureStyles()
        {
            _codeStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                fontStyle = FontStyle.Bold
            };

            _noteStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                wordWrap = true
            };
        }
    }
}
