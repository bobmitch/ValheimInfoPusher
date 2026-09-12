using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ValheimRelay.Plugin.Patches
{
    /// <summary>
    /// Every patch in this file does one thing: forward into
    /// <see cref="RelayBehaviour"/>. No logic lives here, so a game update that
    /// changes a signature costs an attribute change and nothing else (§4.1).
    /// </summary>
    internal static class PatchHelpers
    {
        internal static RelayBehaviour? Behaviour => ValheimRelayPlugin.Instance?.Behaviour;

        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Runs a patch body so that nothing it does can escape into the game.
        /// <para>
        /// THIS IS THE MOST IMPORTANT METHOD IN THE MOD, and it is here because
        /// its absence cost players their game. An exception thrown in a Harmony
        /// prefix or postfix propagates out of the method being patched, so a
        /// mod bug becomes a bug in the game's own code. The
        /// <c>Player.OnSpawned</c> postfix is the worst place for that: it runs
        /// inside <c>Game.SpawnPlayer</c>, itself inside <c>Game.UpdateRespawn</c>
        /// on <c>FixedUpdate</c>. A throw there abandons the spawn half-finished
        /// and the respawn never completes, so the game destroys the player and
        /// tries again on the next physics tick, forever — which reaches the
        /// player as a camera spinning wildly around a character standing
        /// somewhere it should not be.
        /// </para>
        /// <para>
        /// §11.4 says the mod should fail soft. Failing soft has to mean the
        /// GAME keeps working, not merely that the mod logs something on its way
        /// down.
        /// </para>
        /// <para>
        /// The body is a delegate rather than inline code on purpose. Mono
        /// resolves field and method tokens when it compiles a method and
        /// reports the failure at the caller's call site, so a game symbol
        /// touched directly in a patch method would throw on entry — before this
        /// try block was ever live. Inside a delegate invoked from here, it
        /// throws at a call instruction that this try covers. See
        /// <see cref="GameCodeChannel"/> for the failure that taught us this.
        /// </para>
        /// </summary>
        /// <param name="what">
        /// Names the site for the log, and is the key that keeps a failure on a
        /// per-frame path from writing a stack trace per frame. One report per
        /// site: a flood of identical traces is how a diagnosable problem turns
        /// into an unreadable log and a stalled game.
        /// </param>
        internal static void Guard(string what, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                try
                {
                    bool first;
                    lock (Reported) first = Reported.Add(what);
                    if (!first) return;

                    ValheimRelayPlugin.Instance?.Log.Error(
                        "ValheimRelay failed in " + what + " and is letting the game carry on without it. "
                        + "This usually means the game has updated; further failures here will not be logged. "
                        + "Details: " + ex);
                }
                catch (Exception)
                {
                    // Logging is not worth a second throw out of a patch.
                }
            }
        }

        /// <summary>
        /// <see cref="Guard(string, Action)"/> for a prefix that decides whether
        /// the original runs. On failure it returns <paramref name="fallback"/>,
        /// which every caller sets to "let the game do what it always did".
        /// </summary>
        internal static T Guard<T>(string what, Func<T> body, T fallback)
        {
            var result = fallback;
            Guard(what, () => result = body());
            return result;
        }
    }

    /// <summary>§4.3: register the code RPC. Runs on client and server alike.</summary>
    [HarmonyPatch(typeof(Game), nameof(Game.Start))]
    internal static class GameStartPatch
    {
        private static void Postfix() => PatchHelpers.Guard(
            "Game.Start", () => PatchHelpers.Behaviour?.OnGameStart());
    }

    /// <summary>
    /// §4.3: we have a local player, so there is something to report. Starting
    /// here rather than on world load avoids the window where <c>ZNet</c> exists
    /// but <c>Player.m_localPlayer</c> is still null.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
    internal static class PlayerOnSpawnedPatch
    {
        private static void Postfix(Player __instance) => PatchHelpers.Guard("Player.OnSpawned", () =>
        {
            if (__instance != Player.m_localPlayer) return;
            PatchHelpers.Behaviour?.StartSession();
        });
    }

    /// <summary>§4.3, §5.2: leaving the world stops the machine. A mod retrying from the main menu is a bug.</summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    internal static class ZNetShutdownPatch
    {
        private static void Prefix() => PatchHelpers.Guard(
            "ZNet.Shutdown", () => PatchHelpers.Behaviour?.StopSession("left the world"));
    }

    [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
    internal static class GameLogoutPatch
    {
        private static void Prefix() => PatchHelpers.Guard(
            "Game.Logout", () => PatchHelpers.Behaviour?.StopSession("logged out"));
    }
}
