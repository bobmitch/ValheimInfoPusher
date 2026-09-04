using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimRelay.Core.Session;

namespace ValheimRelay.Plugin.Patches
{
    /// <summary>
    /// The chat channel of §5.1 — the fallback that provably works on a vanilla
    /// dedicated server, because chat is already a routed RPC the server relays.
    /// <para>
    /// §4.3 warns that this signature has changed across game versions and says
    /// not to assume it. So this patch is declared with
    /// <see cref="HarmonyPatch"/>'s optional-target form: if the method is not
    /// found, Harmony skips it and the mod keeps working over RPC alone, rather
    /// than the whole <c>PatchAll</c> throwing and taking every other patch with
    /// it. That is §11.4's "fail soft" applied where it actually matters.
    /// </para>
    /// </summary>
    [HarmonyPatch]
    internal static class ChatMessagePatch
    {
        [HarmonyPrepare]
        private static bool Prepare(MethodBase? original)
        {
            if (original != null) return true;

            // Called once before targeting: report whether the target exists.
            var found = AccessTools.Method(typeof(Chat), "OnNewChatMessage") != null;
            if (!found)
            {
                ValheimRelayPlugin.Instance?.Log.Warn(
                    "Chat.OnNewChatMessage was not found, so the chat fallback for the session code is unavailable. " +
                    "The routed RPC channel still works; if the game has updated, this patch needs its signature checked.");
            }
            return found;
        }

        [HarmonyTargetMethod]
        private static MethodBase? TargetMethod() => AccessTools.Method(typeof(Chat), "OnNewChatMessage");

        /// <summary>
        /// Returning false hides the line. Consuming our own traffic is what
        /// keeps the fallback from spamming everyone's chat; unmodded players
        /// still see it, which is why chat is the fallback and not the default.
        /// </summary>
        private static bool Prefix(object[] __args)
        {
            var behaviour = PatchHelpers.Behaviour;
            if (behaviour == null) return true;

            try
            {
                // Read defensively by shape rather than by position: the
                // parameter list is exactly what has changed between versions.
                long sender = 0;
                string? text = null;

                foreach (var arg in __args)
                {
                    switch (arg)
                    {
                        case long id when sender == 0:
                            sender = id;
                            break;
                        case string s when text == null && s.Length > 0:
                            text = s;
                            break;
                    }
                }

                if (text == null) return true;
                return !behaviour.TryConsumeChat(sender, text);
            }
            catch (Exception ex)
            {
                ValheimRelayPlugin.Instance?.Log.Warn("chat patch error: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// Ping capture (PLAN.md §4.4's "the chat/ping path, filtered to
    /// <c>Talker.Type.Ping</c>"), which is what makes §3.3 bidirectional: a
    /// player pinging in game puts it on every browser watching.
    /// <para>
    /// A ping is not a method of its own. Everything the player recognises as
    /// one — the pulsing marker, the sound, the world text — is what
    /// <c>Chat.OnNewChatMessage</c> does with a <c>Talker.Type.Ping</c> message,
    /// so that is where it can be observed, and §4.3's warning about that
    /// signature applies here exactly as it does to
    /// <see cref="GameBridge.ShowPing"/>. Both ends share
    /// <see cref="GameBridge.FindChatPingMethod"/> so they cannot resolve
    /// different overloads.
    /// </para>
    /// <para>
    /// IT NEVER CONSUMES THE LINE. The prefix returns void, so the game draws
    /// the ping exactly as it always did; this only watches. Capture that hid
    /// the player's own ping to send it elsewhere would be a strictly worse
    /// game.
    /// </para>
    /// </summary>
    [HarmonyPatch]
    [HarmonyPriority(Priority.First)]
    internal static class ChatPingPatch
    {
        [HarmonyPrepare]
        private static bool Prepare(MethodBase? original)
        {
            if (original != null) return true;

            if (GameBridge.FindChatPingMethod() != null) return true;

            ValheimRelayPlugin.Instance?.Log.Warn(
                "no Chat.OnNewChatMessage overload on this build carries a position and a ping talker type, "
                + "so pings made in game will not reach the web map. Everything else, including pings FROM "
                + "the map, is unaffected.");
            return false;
        }

        [HarmonyTargetMethod]
        private static MethodBase? TargetMethod() => GameBridge.FindChatPingMethod();

        /// <summary>
        /// Runs at <see cref="Priority.First"/> so it observes the message even
        /// when a later prefix — this file's own code-channel patch, or another
        /// mod's — consumes it. Harmony's rule for what happens to the prefixes
        /// after one returns false has moved between versions, and this patch
        /// should not be the thing that depends on which.
        /// </summary>
        private static void Prefix(object[] __args)
        {
            // The mod's own render of an INBOUND ping comes through this very
            // method. Forwarding it would put it back on the wire, and §3.3's
            // peer fan-out would hand it to every other mod to render and
            // forward in turn. First test, before anything is read.
            if (GameBridge.IsRenderingPing) return;

            var behaviour = PatchHelpers.Behaviour;
            if (behaviour == null) return;

            try
            {
                if (!GameBridge.TryReadPingArgs(__args, out Vector3 position, out var senderId)) return;
                behaviour.OnGamePing(position, senderId);
            }
            catch (Exception ex)
            {
                ValheimRelayPlugin.Instance?.Log.Warn("ping capture error: " + ex.Message);
            }
        }
    }
}
