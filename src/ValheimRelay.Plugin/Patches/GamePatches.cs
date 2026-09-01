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
    }

    /// <summary>§4.3: register the code RPC. Runs on client and server alike.</summary>
    [HarmonyPatch(typeof(Game), nameof(Game.Start))]
    internal static class GameStartPatch
    {
        private static void Postfix() => PatchHelpers.Behaviour?.OnGameStart();
    }

    /// <summary>
    /// §4.3: we have a local player, so there is something to report. Starting
    /// here rather than on world load avoids the window where <c>ZNet</c> exists
    /// but <c>Player.m_localPlayer</c> is still null.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
    internal static class PlayerOnSpawnedPatch
    {
        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            PatchHelpers.Behaviour?.StartSession();
        }
    }

    /// <summary>§4.3, §5.2: leaving the world stops the machine. A mod retrying from the main menu is a bug.</summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    internal static class ZNetShutdownPatch
    {
        private static void Prefix() => PatchHelpers.Behaviour?.StopSession("left the world");
    }

    [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
    internal static class GameLogoutPatch
    {
        private static void Prefix() => PatchHelpers.Behaviour?.StopSession("logged out");
    }
}
