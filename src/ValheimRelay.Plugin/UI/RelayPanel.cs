using System;
using UnityEngine;
using ValheimRelay.Core.Qr;
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
    /// <para>
    /// Opening the panel puts the link on the clipboard and draws it as a QR, so
    /// the two ways a player actually gets the map open — pasting into a browser
    /// on this machine, or pointing a phone at the screen — both cost nothing.
    /// </para>
    /// </summary>
    public sealed class RelayPanel
    {
        private const int Width = 330;

        /// <summary>The panel without a QR under it. <see cref="Height"/> adds the rest.</summary>
        private const int BaseHeight = 190;

        /// <summary>
        /// Roughly how wide the symbol should end up. Not exact: the module
        /// count depends on how long the link is, and a whole number of pixels
        /// per module matters more than a consistent size. Big enough to scan
        /// from a phone at arm's length, small enough that the panel does not
        /// swallow the minimap it sits over.
        /// </summary>
        private const int QrTargetPixels = 160;

        private const int QrGap = 10;

        private readonly RelayBehaviour _behaviour;
        private readonly PluginConfig _config;

        private bool _visible;
        private float _copiedAt = float.NegativeInfinity;
        private GUIStyle? _codeStyle;
        private GUIStyle? _noteStyle;

        // The snapshot the whole of Draw reads. See Refresh.
        private string? _code;
        private SessionState _state;
        private string? _shareText;
        private string? _copiedText;
        private bool _wasVisible;

        private Texture2D? _qr;
        private string? _qrFor;
        private int _qrPixels;

        public RelayPanel(RelayBehaviour behaviour, PluginConfig config)
        {
            _behaviour = behaviour ?? throw new ArgumentNullException(nameof(behaviour));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public void Toggle() => _visible = !_visible;

        /// <summary>Drops the symbol's texture. Called when the behaviour goes away.</summary>
        public void Release() => ReleaseQr();

        private int Height => BaseHeight + (_qr == null ? 0 : QrGap + _qrPixels);

        public void Draw()
        {
            if (!_config.Enabled.Value) return;

            EnsureStyles();

            // Everything below reads the snapshot rather than the session,
            // because GUILayout requires the same controls in the same order
            // across every event of one cycle and the session does not wait for
            // that. A code that arrives — or a §5.3 rotation that clears one —
            // between the Layout event and the Repaint event would otherwise
            // change how many labels this method emits, and IMGUI answers that
            // by throwing and taking the panel with it. Unity sends Layout
            // ahead of every other event, so settling it there settles it for
            // the whole cycle.
            if (Event.current.type == EventType.Layout) Refresh();

            DrawIndicator();

            if (!_visible) return;

            var height = Height;
            var top = Mathf.Max(10f, Mathf.Min(20f, Screen.height - height - 10f));
            var rect = new Rect(Screen.width - Width - 20, top, Width, height);

            GUI.Box(rect, "ValheimRelay");
            GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 26, rect.width - 24, rect.height - 38));

            if (_code == null)
            {
                GUILayout.Label(DescribeState(_state));
            }
            else
            {
                GUILayout.Label(_code, _codeStyle);
                GUILayout.Label(DescribeState(_state));

                GUILayout.BeginHorizontal();
                var copyLabel = Time.realtimeSinceStartup - _copiedAt < 2f ? "Copied to clipboard" : CopyButtonLabel();
                if (GUILayout.Button(copyLabel) && _shareText != null)
                {
                    CopyToClipboard(_shareText);
                }
                GUILayout.EndHorizontal();

                // Say what the code actually is. Treating it as a room name is
                // how people end up pasting it somewhere public (§8) — and the
                // square below is worse that way than the code above it, since
                // a camera reads it whether or not anyone was paying attention.
                GUILayout.Label(
                    "Anyone with this code can watch everyone in this session move, "
                    + "for as long as it lasts. Share it like a link, not a name"
                    + (_qr == null ? "." : " — and don't leave this panel up on stream."),
                    _noteStyle);

                DrawQr();
            }

            if (_state == SessionState.Blocked && GUILayout.Button("Retry"))
            {
                _behaviour.RetryAfterRoomFull();
            }

            GUILayout.EndArea();
        }

        private string CopyButtonLabel() => _config.HasMapLink ? "Copy map link" : "Copy code";

        // ---------------------------------------------------------- snapshot

        /// <summary>
        /// Reads the session once per cycle, and acts on what changed: the
        /// clipboard when the link is new or the panel has just been opened, the
        /// symbol's texture when the link is new.
        /// </summary>
        private void Refresh()
        {
            _code = _behaviour.Code;
            _state = _behaviour.State;

            var opened = _visible && !_wasVisible;
            _wasVisible = _visible;

            if (!_visible)
            {
                _shareText = null;
                return;
            }

            // Read through the behaviour rather than rebuilding it here: the
            // seed it folds in is read live, so a player who left one world for
            // another gets a link to the world they are actually in.
            _shareText = _code == null ? null : _behaviour.ShareText;

            // On open, and whenever the link itself changes under an open panel.
            // The second case is §5.3: a rotation leaves the old link pointing
            // at a dead room, and a clipboard still holding it is a trap.
            if (_shareText != null && (opened || _shareText != _copiedText))
            {
                CopyToClipboard(_shareText);
            }

            RefreshQr();
        }

        private void CopyToClipboard(string shareText)
        {
            _copiedText = shareText;
            _copiedAt = Time.realtimeSinceStartup;

            // Checked before writing. Setting the system clipboard is a
            // synchronous platform call, so opening the panel repeatedly should
            // not keep paying for it — and a player who has since copied
            // something else still gets the link put back.
            if (GUIUtility.systemCopyBuffer != shareText) GUIUtility.systemCopyBuffer = shareText;
        }

        // ---------------------------------------------------------------- QR

        private void RefreshQr()
        {
            // A bare code makes a symbol that scans to eight characters the
            // player would still have to type somewhere, which is not worth the
            // panel space. The symbol is for the case where it opens the map.
            if (_shareText == null || !_config.HasMapLink)
            {
                ReleaseQr();
                return;
            }

            if (_qrFor == _shareText) return;

            ReleaseQr();

            // Set even when the encoding fails, so a link that does not fit is
            // not re-encoded on every frame for as long as the panel is open.
            _qrFor = _shareText;

            var qr = QrCode.Encode(_shareText);
            if (qr == null) return;

            _qr = QrTexture.Create(qr, QrTargetPixels);
            _qrPixels = _qr.width;
        }

        private void ReleaseQr()
        {
            if (_qr != null) UnityEngine.Object.Destroy(_qr);

            _qr = null;
            _qrFor = null;
            _qrPixels = 0;
        }

        private void DrawQr()
        {
            if (_qr == null) return;

            GUILayout.Space(QrGap);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var area = GUILayoutUtility.GetRect(
                _qrPixels, _qrPixels, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (Event.current.type != EventType.Repaint) return;

            // Whole pixels. Centring hands back a fractional origin often
            // enough, and half a pixel of offset puts every module edge between
            // two texture pixels — undoing the point sampling the texture was
            // built for.
            area.x = Mathf.Round(area.x);
            area.y = Mathf.Round(area.y);

            // DrawTexture multiplies by GUI.color, and a tinted symbol is an
            // unscannable one. DrawIndicator below sets that colour, and so does
            // every other mod drawing in the same frame.
            var previous = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(area, _qr);
            GUI.color = previous;
        }

        // --------------------------------------------------------- indicator

        /// <summary>A small always-visible marker while the session is live (§7).</summary>
        private void DrawIndicator()
        {
            if (_state == SessionState.Idle || _state == SessionState.Stopped) return;

            var sharing = _config.ShareMyPosition.Value;
            var colour = _state switch
            {
                SessionState.Active => sharing ? new Color(0.49f, 0.78f, 0.89f) : new Color(0.7f, 0.7f, 0.7f),
                SessionState.Blocked => new Color(0.9f, 0.45f, 0.45f),
                _ => new Color(0.9f, 0.8f, 0.4f)
            };

            var label = _state switch
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
