using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ValheimRelay.Core.Json;

namespace ValheimRelay.Core.Session
{
    /// <summary>One world's reclaim credentials (§5.3).</summary>
    public sealed class ReclaimEntry
    {
        public ReclaimEntry(string code, string token, long epoch, long savedAtUnixMs)
        {
            Code = code;
            Token = token;
            Epoch = epoch;
            SavedAtUnixMs = savedAtUnixMs;
        }

        public string Code { get; }

        /// <summary>A secret. Never log it, never show it, never put it in chat (§5.3, §8).</summary>
        public string Token { get; }

        /// <summary>The generation this code belonged to, so a rotation keeps ordering (§12.3).</summary>
        public long Epoch { get; }

        public long SavedAtUnixMs { get; }
    }

    /// <summary>
    /// Abstracts the file so Core stays testable and free of IO. The plugin backs
    /// it with a file under the BepInEx config directory.
    /// </summary>
    public interface IReclaimStorage
    {
        string? Read();
        void Write(string contents);
    }

    /// <summary>
    /// <c>{ worldUid -> { code, token } }</c>, the only thing the mod persists
    /// beyond its config (§5.3, §8).
    /// </summary>
    public sealed class ReclaimStore
    {
        private const int CurrentVersion = 1;

        private readonly IReclaimStorage _storage;
        private readonly ILog _log;
        private readonly Dictionary<string, ReclaimEntry> _entries =
            new Dictionary<string, ReclaimEntry>(StringComparer.Ordinal);

        private string? _salt;
        private bool _loaded;

        public ReclaimStore(IReclaimStorage storage, ILog log)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Base64 install salt for <see cref="Identity.StableUid"/>, created on first use.</summary>
        public string Salt
        {
            get
            {
                EnsureLoaded();
                if (_salt == null)
                {
                    _salt = Identity.StableUid.EncodeSalt(Identity.StableUid.NewSalt());
                    Save();
                }
                return _salt;
            }
        }

        public ReclaimEntry? Get(string worldUid)
        {
            if (string.IsNullOrEmpty(worldUid)) return null;
            EnsureLoaded();
            return _entries.TryGetValue(worldUid, out var entry) ? entry : null;
        }

        public void Put(string worldUid, ReclaimEntry entry)
        {
            if (string.IsNullOrEmpty(worldUid)) return;
            EnsureLoaded();
            _entries[worldUid] = entry ?? throw new ArgumentNullException(nameof(entry));
            Save();
        }

        /// <summary>
        /// Forget a world's credentials. Called on 4003/4004 (§5.3) and — the case
        /// PLAN.md does not mention — when this client <em>loses</em> the
        /// two-creator tiebreak (§5.1). A losing creator still holds a valid token
        /// for the room it just abandoned; keeping it means the next load of this
        /// world reclaims the dead room and splits the group again.
        /// </summary>
        public void Forget(string worldUid)
        {
            if (string.IsNullOrEmpty(worldUid)) return;
            EnsureLoaded();
            if (_entries.Remove(worldUid)) Save();
        }

        private void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            string? raw;
            try
            {
                raw = _storage.Read();
            }
            catch (Exception ex)
            {
                _log.Warn("could not read reclaim store: " + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(raw)) return;
            if (!JsonParser.TryParse(raw!, out var root) || root.Kind != JsonKind.Object)
            {
                // A corrupt store is not worth failing over; the cost is one
                // fresh code. Do not echo the contents — it holds tokens.
                _log.Warn("reclaim store is not valid JSON; starting fresh");
                return;
            }

            _salt = root["salt"].AsString();

            var worlds = root["worlds"];
            if (worlds.Kind != JsonKind.Object) return;

            foreach (var worldUid in worlds.Keys)
            {
                var entry = worlds[worldUid];
                if (entry.Kind != JsonKind.Object) continue;
                var code = entry["code"].AsString();
                var token = entry["token"].AsString();
                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(token)) continue;
                _entries[worldUid] = new ReclaimEntry(code!, token!, entry["epoch"].AsLong(), entry["savedAt"].AsLong());
            }
        }

        private void Save()
        {
            var w = new JsonWriter();
            w.BeginObject()
                .Prop("version", CurrentVersion);

            if (_salt != null) w.Prop("salt", _salt);

            w.Name("worlds").BeginObject();
            foreach (var pair in _entries)
            {
                w.Name(pair.Key).BeginObject()
                    .Prop("code", pair.Value.Code)
                    .Prop("token", pair.Value.Token)
                    .Prop("epoch", pair.Value.Epoch)
                    .Prop("savedAt", pair.Value.SavedAtUnixMs)
                    .EndObject();
            }
            w.EndObject();

            try
            {
                _storage.Write(w.EndObject().ToString());
            }
            catch (Exception ex)
            {
                _log.Warn("could not write reclaim store: " + ex.Message);
            }
        }
    }
}
