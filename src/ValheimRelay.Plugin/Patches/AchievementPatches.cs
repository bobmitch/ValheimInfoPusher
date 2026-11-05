using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ValheimRelay.Core.Session;

namespace ValheimRelay.Plugin.Patches
{
    /// <summary>
    /// Keeps Steam achievements working with this mod installed, so that a
    /// player does not need a separate achievements mod as well.
    /// <para>
    /// Valheim 1.0 counts a modded game as a cheated one.
    /// <c>Achievements.IsCheatedAtAll</c> answers true whenever
    /// <c>Game.isModded</c> is set, and every achievement stat write goes
    /// through <c>Achievements.CanGetAchievements</c>, which refuses while it
    /// does. BepInEx sets that flag itself on every launch, so having the loader
    /// installed at all is enough to switch achievements off, whatever the mods
    /// actually do.
    /// </para>
    /// <para>
    /// This changes that one read and nothing else. Every other rule in
    /// <c>IsCheatedAtAll</c> is the game's own and still runs: devcommands used
    /// on the character, a world with cheat modifiers, and a cheated item in the
    /// inventory all still switch achievements off, exactly as they would in an
    /// unmodded game. An info mod has no business switching off the game's cheat
    /// protection, and it is not what anyone asks for when they ask for their
    /// achievements back.
    /// </para>
    /// <para>
    /// A TRANSPILER, NOT A FLAG FLIP. The usual fix clears <c>Game.isModded</c>
    /// in a prefix and puts it back in a postfix. That writes a flag Iron Gate
    /// asked mods to set for their support triage, and it does not compose: with
    /// two mods doing it, Harmony runs the postfixes in the same order as the
    /// prefixes, not in reverse, so the second one to restore puts back the
    /// <c>false</c> the first one wrote and the game is left believing it is
    /// unmodded. Swapping the read for a call leaves the flag alone, so the main
    /// menu still says the game is modded, and the patch is harmless alongside
    /// any achievements mod the player also has: those either reach the same
    /// answer or replace this method's answer outright.
    /// </para>
    /// <para>
    /// The replacement asks <see cref="PluginConfig.KeepAchievements"/> on every
    /// call rather than once at load, so turning the setting off gives the game
    /// its own rule back without a restart.
    /// </para>
    /// </summary>
    [HarmonyPatch]
    public static class KeepAchievementsPatch
    {
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // Both looked up by name rather than with typeof/nameof. Achievements
        // only exists from Valheim 1.0, and a dedicated server can lag behind
        // its clients; a compile-time reference to it would fail to load there
        // and take PatchAll down, and the whole mod with it (§11.4).
        // Looked up in the game's own assembly, so a mod that happens to define
        // a type of the same name cannot be picked instead.
        private static readonly MethodInfo? Target = FindTarget();

        private static readonly FieldInfo? IsModdedField = FindIsModdedField();

        private static readonly MethodInfo Replacement =
            AccessTools.Method(typeof(KeepAchievementsPatch), nameof(IsModdedForAchievements));

        private static bool _reported;

        [HarmonyPrepare]
        private static bool Prepare(MethodBase? original)
        {
            if (original != null) return true;
            if (Target != null && IsModdedField != null) return true;

            ValheimRelayPlugin.Instance?.Log.Warn(
                "Achievements.IsCheatedAtAll or Game.isModded was not found on this build, so KeepAchievements "
                + "has nothing to change: if this build has achievements, being modded will keep them switched off. "
                + "Everything else in the mod is unaffected.");
            return false;
        }

        [HarmonyTargetMethod]
        private static MethodBase? TargetMethod() => Target;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var field = IsModdedField;
            if (field == null) return code; // Prepare already refused; unreachable.

            var replaced = 0;
            foreach (var instruction in code)
            {
                if (!instruction.LoadsField(field)) continue;

                // Edited in place, so a label or exception block attached to the
                // read stays attached to the call. Both push one bool and take
                // nothing, so the stack is the same either side.
                instruction.opcode = OpCodes.Call;
                instruction.operand = Replacement;
                replaced++;
            }

            // Harmony runs a transpiler again every time anything patches or
            // unpatches the same method, which an achievements mod installed
            // alongside this one will do. The answer cannot change between
            // runs, so it is reported once.
            if (_reported) return code;
            _reported = true;

            var log = ValheimRelayPlugin.Instance?.Log;
            if (replaced == 0)
            {
                // Fail soft and say so. The likeliest cause is the game moving
                // the check somewhere else, which this patch cannot follow
                // blind; the method is left exactly as the game wrote it.
                log?.Warn(
                    "Achievements.IsCheatedAtAll no longer reads Game.isModded on this build, so KeepAchievements "
                    + "has nothing to change. If achievements are still switched off with mods installed, the game "
                    + "has moved that check and this patch needs updating.");
            }
            else
            {
                log?.Info(
                    "achievements: being modded no longer switches them off while KeepAchievements is on. "
                    + "Cheats, world modifiers and spawned items still do.");
            }

            return code;
        }

        /// <summary>
        /// What <c>Achievements.IsCheatedAtAll</c> reads in place of
        /// <c>Game.isModded</c>. Public because it is called from the game's own
        /// method body once the transpiler has run.
        /// </summary>
        public static bool IsModdedForAchievements()
        {
            if (!Game.isModded) return false;

            // No plugin means nothing is asking for achievements to be kept, so
            // the game's own answer stands.
            var settings = ValheimRelayPlugin.Instance?.Settings;
            return settings == null || !settings.KeepAchievements.Value;
        }

        private static MethodInfo? FindTarget()
        {
            var type = typeof(Game).Assembly.GetType("Achievements", throwOnError: false);
            var method = type?.GetMethod("IsCheatedAtAll", AnyStatic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return method != null && method.ReturnType == typeof(bool) ? method : null;
        }

        private static FieldInfo? FindIsModdedField()
        {
            var field = typeof(Game).GetField("isModded", AnyStatic);
            return field != null && field.FieldType == typeof(bool) ? field : null;
        }
    }
}
