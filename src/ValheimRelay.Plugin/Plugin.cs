using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using ValheimRelay.Core.Session;

// BepInEx.Logging and Core both define a LogLevel and this file needs types from
// both, so the unqualified name is ambiguous. Aliased rather than fully
// qualified because BepInExLog exists precisely to map one enum onto the other:
// every mention of the Core one below is the load-bearing half of that mapping.
using CoreLogLevel = ValheimRelay.Core.Session.LogLevel;

namespace ValheimRelay.Plugin
{
    [BepInPlugin(PluginId, PluginName, PluginVersion)]
    [BepInProcess("valheim.exe")]
    [BepInProcess("valheim_server.exe")]
    public sealed class ValheimRelayPlugin : BaseUnityPlugin
    {
        public const string PluginId = "com.valheimrelay.mod";
        public const string PluginName = "ValheimRelay";
        public const string PluginVersion = "0.1.0";

        private Harmony? _harmony;

        public static ValheimRelayPlugin? Instance { get; private set; }

        public PluginConfig Settings { get; private set; } = null!;

        public BepInExLog Log { get; private set; } = null!;

        public RelayBehaviour? Behaviour { get; private set; }

        private void Awake()
        {
            Instance = this;
            Log = new BepInExLog(Logger);
            Settings = new PluginConfig(Config);

            if (!Settings.Enabled.Value)
            {
                Logger.LogInfo("ValheimRelay is disabled in config; not patching.");
                return;
            }

            try
            {
                _harmony = new Harmony(PluginId);
                _harmony.PatchAll(typeof(ValheimRelayPlugin).Assembly);
            }
            catch (Exception ex)
            {
                // §11.4: fail soft. A mod that refuses to load after a game
                // update leaves the player with no map and no explanation; a
                // clear log line and a dormant mod is the better failure.
                Logger.LogError(
                    "ValheimRelay could not apply its patches and will stay dormant. " +
                    "This usually means the game updated. Details: " + ex);
                return;
            }

            var host = new GameObject("ValheimRelay");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            Behaviour = host.AddComponent<RelayBehaviour>();

            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            Instance = null;
        }

        /// <summary>
        /// Where the reclaim store lives (§5.3). Beside the config, so a player
        /// clearing their config directory clears their stored session too.
        /// </summary>
        public string SessionStorePath => Path.Combine(Paths.ConfigPath, "ValheimRelay.session.json");
    }

    /// <summary>Bridges Core's log abstraction onto BepInEx's.</summary>
    public sealed class BepInExLog : ILog
    {
        private readonly ManualLogSource _source;

        public BepInExLog(ManualLogSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public void Log(CoreLogLevel level, string message)
        {
            switch (level)
            {
                case CoreLogLevel.Debug:
                    _source.LogDebug(message);
                    break;
                case CoreLogLevel.Warning:
                    _source.LogWarning(message);
                    break;
                case CoreLogLevel.Error:
                    _source.LogError(message);
                    break;
                default:
                    _source.LogInfo(message);
                    break;
            }
        }
    }

    /// <summary>
    /// Backs the reclaim store with a file. Writes are small and rare — once per
    /// created room — so nothing here needs to be clever.
    /// </summary>
    public sealed class FileReclaimStorage : IReclaimStorage
    {
        private readonly string _path;
        private readonly ILog _log;

        public FileReclaimStorage(string path, ILog log)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public string? Read() => File.Exists(_path) ? File.ReadAllText(_path) : null;

        public void Write(string contents)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write-then-move, so a crash mid-write cannot leave a truncated file
            // that costs the player their session on the next load.
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, contents);

            if (File.Exists(_path)) File.Delete(_path);
            File.Move(temporary, _path);
        }
    }
}
