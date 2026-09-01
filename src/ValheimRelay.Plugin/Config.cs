using System;
using BepInEx.Configuration;
using UnityEngine;
using ValheimRelay.Core.Session;

namespace ValheimRelay.Plugin
{
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
                "Relay WebSocket URL. wss:// for the hosted relay, ws:// for a local one.");

            MapUrl = file.Bind("General", "MapUrl", DefaultMapUrl,
                "Web map base URL, used to build the copyable link. The code is appended as a fragment.");

            AnnounceInChat = file.Bind("General", "AnnounceInChat", true,
                "Print the session code in chat when the session starts. Local only — other players do not see it.");

            ShareMyPosition = file.Bind("Privacy", "ShareMyPosition", true,
                "Broadcast your position. Turning this off keeps you in the session and still shows you everyone else.");

            ShareHealth = file.Bind("Privacy", "ShareHealth", true,
                "Include health in position updates.");

            AcceptMapMarkers = file.Bind("Privacy", "AcceptMapMarkers", true,
                "Let the web map place pins on your in-game minimap.");

            PositionInterval = file.Bind("Performance", "PositionInterval", 1.0f,
                new ConfigDescription(
                    "Seconds between position updates. Clamped to at least 0.5.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            ToggleKey = file.Bind("UI", "ToggleKey", KeyCode.F9,
                "Shows and hides the relay panel.");
        }

        // §11.2 is still open: shipping a default means hosting an instance and
        // owning its capacity, and shipping none means every user edits a config,
        // which breaks the "nothing to edit" goal. Left pointing at a local relay
        // so the mod is runnable today and the decision stays visible.
        public const string DefaultRelayUrl = "ws://localhost:8080/ws";
        public const string DefaultMapUrl = "";

        public ConfigEntry<bool> Enabled { get; }
        public ConfigEntry<string> RelayUrl { get; }
        public ConfigEntry<string> MapUrl { get; }
        public ConfigEntry<bool> AnnounceInChat { get; }
        public ConfigEntry<bool> ShareMyPosition { get; }
        public ConfigEntry<bool> ShareHealth { get; }
        public ConfigEntry<bool> AcceptMapMarkers { get; }
        public ConfigEntry<float> PositionInterval { get; }
        public ConfigEntry<KeyCode> ToggleKey { get; }

        public SessionOptions ToSessionOptions()
        {
            var options = new SessionOptions
            {
                RelayUrl = NormaliseRelayUrl(RelayUrl.Value),
                PositionInterval = TimeSpan.FromSeconds(PositionInterval.Value),
                SharePosition = ShareMyPosition.Value
            };

            options.Normalise();
            return options;
        }

        /// <summary>
        /// Accepts what a player is likely to paste. The rules live in Core's
        /// <see cref="RelayUrl"/> so they can be tested without the game.
        /// </summary>
        public static string NormaliseRelayUrl(string raw) => RelayUrl.Normalise(raw, DefaultRelayUrl);

        /// <summary>The one copyable thing to hand a player: a link if one is configured, else the code.</summary>
        public string BuildShareText(string code)
        {
            var mapUrl = (MapUrl.Value ?? string.Empty).Trim();
            if (mapUrl.Length == 0) return code;
            return mapUrl.TrimEnd('/') + "/#" + code;
        }
    }
}
