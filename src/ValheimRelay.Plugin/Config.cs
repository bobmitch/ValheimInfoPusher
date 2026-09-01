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
        public static string NormaliseRelayUrl(string raw) => CoreRelayUrl.Normalise(raw, DefaultRelayUrl);

        /// <summary>
        /// The one copyable thing to hand a player: a link if a map is
        /// configured, the bare code if not. The rules live in Core's
        /// <see cref="MapLink"/> so they can be tested without the game.
        /// </summary>
        public string BuildShareText(string code) => MapLink.Build(MapUrl.Value, code);

        /// <summary>True when there is a map to link to, so the UI can say "link" rather than "code".</summary>
        public bool HasMapLink => MapLink.Normalise(MapUrl.Value).Length > 0;
    }
}
