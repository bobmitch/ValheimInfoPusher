using System;
using System.Reflection;
using HarmonyLib;
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
}
