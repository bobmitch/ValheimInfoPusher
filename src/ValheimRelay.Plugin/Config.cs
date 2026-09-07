using System;
using BepInEx.Configuration;
using UnityEngine;
using ValheimRelay.Core.Session;

// The ConfigEntry property below is also called RelayUrl, and a member name
// beats a type name in lookup, so inside this class the bare name is the
// property. That is what the two uses of this alias need to get around; the
// property keeps its name because it is what appears in the config file.
using CoreRelayUrl = ValheimRelay.Core.Session.RelayUrl;

namespace ValheimRelay.Plugin
{
    /// <summary>
    /// How far down <see cref="GameBridge.ShowPing"/> is allowed to start. Each
    /// value is a step down in fidelity, and each is still there as the one
    /// below's fallback — so this only exists to skip a rung that a game update
    /// has broken, without waiting for a new build of the mod.
    /// </summary>
    public enum PingStyle
    {
        /// <summary>The game's own ping: marker, sound and world text.</summary>
        Auto = 0,

        /// <summary>The minimap marker only.</summary>
        Map = 1,

        /// <summary>A short-lived pin and a chat line.</summary>
        Pin = 2,
    }

    /// <summary>
    /// The config surface of PLAN.md §7. Every entry is defaulted so a fresh
    /// install needs no edits — that is the whole product goal in §2, and an
    /// entry that has to be filled in breaks it.
    /// </summary>
    public sealed class PluginConfig
    {
        public PluginConfig(ConfigFile file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));

            Enabled = file.Bind("General", "Enabled", true,
                "Master switch. Turn this off and the mod does nothing at all.");

            RelayUrl = file.Bind("General", "RelayUrl", DefaultRelayUrl,
                "Relay WebSocket URL. Leave this alone unless you run your own relay.");

            MapUrl = file.Bind("General", "MapUrl", DefaultMapUrl,
                "Web map base URL, used to build the copyable link. The code is appended as a fragment.");

            AnnounceInChat = file.Bind("General", "AnnounceInChat", true,
                "Print the session code in chat when the session starts. Local only — other players do not see it.");

            // First in the section on purpose: it is the answer to the question
            // most people open this file with, and reading it first tells you
            // that the toggles under it are the fine-grained version of it.
            StreamerMode = file.Bind("Privacy", "StreamerMode", false,
                "One-way mode, for streaming or any code that reaches people you do not know. "
                + "Viewers still see you exactly as before; nothing they do reaches your game. "
                + "Overrides AcceptMapPings and AcceptMapMarkers while it is on WITHOUT changing "
                + "them, so turning it off gives you back the settings you had. It deliberately "
                + "does not touch what you send: being watched is the point.");

            ShareMyPosition = file.Bind("Privacy", "ShareMyPosition", true,
                "Broadcast your position. Turning this off keeps you in the session and still shows you everyone else.");

            ShareHealth = file.Bind("Privacy", "ShareHealth", true,
                "Include health in position updates.");

            AcceptMapMarkers = file.Bind("Privacy", "AcceptMapMarkers", true,
                "Let the web map place pins on your in-game minimap.");

            AcceptMapPings = file.Bind("Privacy", "AcceptMapPings", true,
                "Let the web map ping your game. Turning this off also drops the relayed copies of "
                + "other modded players' pings, which costs you nothing while you are in the world "
                + "with them: Valheim delivers those itself, and the relayed copy is the duplicate "
                + "the mod already suppresses.");

            ShareMyPings = file.Bind("Privacy", "ShareMyPings", true,
                "Send the pings you make in game to the web map. Separate from ShareMyPosition: a ping is "
                + "something you chose to do, so turning off the position stream does not turn this off. "
                + "Pings from other players are never forwarded by you, only your own.");

            PositionInterval = file.Bind("Performance", "PositionInterval", 1.0f,
                new ConfigDescription(
                    "Seconds between position updates. Clamped to at least 0.5.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            // F9 is a stock Valheim bind and a popular one among other mods, so
            // the panel sits on F8 plus Shift: an unbound key made two-handed,
            // which is about as far out of the way as a hotkey gets.
            ToggleKey = file.Bind("UI", "ToggleKey", KeyCode.F8,
                "Shows and hides the relay panel. Held together with Shift unless ToggleRequiresShift is off.");

            ToggleRequiresShift = file.Bind("UI", "ToggleRequiresShift", true,
                "Require Shift to be held with ToggleKey. Turn this off for a bare keypress.");

            PingStyle = file.Bind("UI", "PingStyle", ValheimRelay.Plugin.PingStyle.Auto,
                "How a ping from the web map is shown. Auto hands it to the game's own ping code, "
                + "so it looks, sounds and reads exactly like a player's ping. Map draws the minimap "
                + "marker only. Pin drops a short-lived pin and writes a chat line. Drop down a level "
                + "if a game update breaks the one above it.");
        }

        // §11.2, settled: the mod ships pointed at the hosted relay, which is
        // what keeps §2's "nothing to edit" promise. The address lives in Core
        // beside the normalisation rules so it is covered by tests.
        public const string DefaultRelayUrl = CoreRelayUrl.Default;

        // §11.3, settled. Shipped alongside the relay default: one without the
        // other leaves the player holding a bare code with nowhere to put it.
        public const string DefaultMapUrl = MapLink.Default;

        public ConfigEntry<bool> Enabled { get; }
        public ConfigEntry<string> RelayUrl { get; }
        public ConfigEntry<string> MapUrl { get; }
        public ConfigEntry<bool> AnnounceInChat { get; }
        public ConfigEntry<bool> ShareMyPosition { get; }
        public ConfigEntry<bool> ShareHealth { get; }
        public ConfigEntry<bool> StreamerMode { get; }
        public ConfigEntry<bool> AcceptMapMarkers { get; }
        public ConfigEntry<bool> AcceptMapPings { get; }
        public ConfigEntry<bool> ShareMyPings { get; }
        public ConfigEntry<float> PositionInterval { get; }
        public ConfigEntry<KeyCode> ToggleKey { get; }
        public ConfigEntry<bool> ToggleRequiresShift { get; }
        public ConfigEntry<PingStyle> PingStyle { get; }

        /// <summary>
        /// Whether a ping from the room is allowed to reach the game.
        /// <para>
        /// This and <see cref="AcceptsMapMarkers"/> are the only things the mod
        /// asks about inbound frames, so <see cref="StreamerMode"/> is applied
        /// in exactly two places rather than remembered at every call site. An
        /// inbound frame type added later is one property here, not a grep.
        /// </para>
        /// </summary>
        public bool AcceptsMapPings => !StreamerMode.Value && AcceptMapPings.Value;

        /// <summary>Whether a marker from the room is allowed onto the minimap.</summary>
        public bool AcceptsMapMarkers => !StreamerMode.Value && AcceptMapMarkers.Value;

        public SessionOptions ToSessionOptions()
        {
            var options = new SessionOptions
            {
                RelayUrl = NormaliseRelayUrl(RelayUrl.Value),
                PositionInterval = TimeSpan.FromSeconds(PositionInterval.Value),
                SharePosition = ShareMyPosition.Value,
                SharePings = ShareMyPings.Value
            };

            options.Normalise();
            return options;
        }

        /// <summary>
        /// Accepts what a player is likely to paste. The rules live in Core's
        /// <see cref="RelayUrl"/> so they can be tested without the game.
        /// </summary>
        public static string NormaliseRelayUrl(string raw) => CoreRelayUrl.Normalise(raw, DefaultRelayUrl);

        /// <summary>
        /// The one copyable thing to hand a player: a link if a map is
        /// configured, the bare code if not. The rules live in Core's
        /// <see cref="MapLink"/> so they can be tested without the game.
        /// </summary>
        public string BuildShareText(string code, string? seed = null) => MapLink.Build(MapUrl.Value, code, seed);

        /// <summary>True when there is a map to link to, so the UI can say "link" rather than "code".</summary>
        public bool HasMapLink => MapLink.Normalise(MapUrl.Value).Length > 0;
    }
}
