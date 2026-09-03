# Valheim Mod — Implementation Plan

This is the plan for the **in-game half** of ValheimRelay: a BepInEx plugin that
reports player telemetry to the relay in this repository and draws back what the
web map sends. The relay is finished and deployed; the map is a separate piece.
This document is written to be handed to an implementer (human or agent) as the
baseline brief, so it restates everything about the relay that the mod needs and
does not assume the reader has read `main.go`.

**Scope of this document:** the mod only. The web map is out of scope except
where the wire format has to be agreed between them — that format is defined
here, in §3, because the relay deliberately does not define it.

---

## 1. What already exists, and what it guarantees

The relay is a Go WebSocket server. It knows about **codes, membership and
fan-out** and nothing else. It has no database, no accounts, no config to edit
per-user, and it never parses the meaning of a frame. Every semantic decision —
what a position looks like, what a marker means — belongs to the mod and the
map, which is why §3 exists.

### 1.1 Connecting

Clients connect to `/ws` with query parameters:

| Parameter | Values         | Meaning                                            |
|-----------|----------------|----------------------------------------------------|
| `role`    | `mod` \| `map` | In-game player mod, or browser map                 |
| `code`    | issued code    | Room to join; **omit** on `role=mod` to create one |
| `token`   | reclaim token  | Optional, `role=mod` only, always with `code`      |

- `role=mod` with no `code` → **create**. The relay issues a code *and* a token.
- `role=mod` with `code` → **join** an existing room.
- `role=mod` with `code` + `token` → **reclaim** a room that has expired.
- `role=map` with `code` → join. Browsers never create rooms.

Codes are 8 characters of Crockford base32 (`0123456789ABCDEFGHJKMNPQRSTVWXYZ`
— no I, L, O or U, so they survive being read aloud over voice chat). The relay
normalises inbound codes forgivingly: case, surrounding whitespace, spaces,
hyphens and underscores are ignored, and `I`/`L` fold to `1`, `O` to `0`. **The
mod should not implement its own normalisation** — pass through whatever the
player typed and let the relay sort it out. The mod *should* display the code in
the canonical uppercase form the relay hands back in `welcome`.

### 1.2 The welcome frame

Every client gets exactly one `welcome` frame immediately on connect, before
anything else:

```json
{
  "type": "welcome",
  "code": "K7MQ2XR4",
  "playerId": "6f1c…",
  "token": "9f2b…",
  "players": [ { "playerId": "…", "name": "Bob", "uid": "…" } ]
}
```

- `code` is authoritative. On a create, this is how the mod learns its code.
- `playerId` is a server-assigned UUID, **new on every connection**. It is not a
  stable identity and must never be persisted or used to recognise a player
  across a reconnect.
- `token` is present **only** for the mod that created or reclaimed the room.
  Joining mods get no token. This is the single most important thing to persist
  (see §5.3).
- `players` is the roster of the other mods currently in the room, so a client
  joining mid-session has everyone immediately instead of waiting for them to
  move. It carries `name` and `uid` from each peer's `hello` frame.

### 1.3 Routing rules the relay enforces

- **`playerId` is overwritten** on every frame from a mod with the server-assigned
  UUID. Whatever the mod puts there is discarded. Every other field passes
  through untouched. A mod therefore cannot impersonate another player, and also
  cannot usefully set `playerId` itself — don't bother sending it.
- A mod may send `{"type":"hello","name":"…","uid":"…"}`. The relay records
  `name` and `uid`, echoes them in later rosters, **and relays the frame like any
  other**. `uid` is the mod's own persistent id and is the only stable identity
  across reconnects.
- **Mod frames always reach every map.** They additionally reach the *other mods*
  in the room only when `type` is exactly `ping` or `marker`. Position telemetry
  is deliberately not fanned out to peers — it would be noise in another
  player's game, and it is the highest-rate traffic.
- **Map frames are broadcast verbatim** to every mod and to the other maps.
- Maps (not mods) receive `{"type":"player_joined"|"player_left","playerId":"…"}`.

The consequence worth internalising: **a mod cannot talk to another mod except
via `ping` and `marker`.** Any peer-to-peer mod feature has to be expressed as
one of those two types, or it needs a relay change.

### 1.4 Close codes

| Code | Meaning                        | What the mod must do                                  |
|------|--------------------------------|-------------------------------------------------------|
| 4003 | Reclaim token does not match   | Discard the stored token+code, fall back to create     |
| 4004 | Unknown or expired code        | Creator: create fresh. Joiner: wait for a new code     |
| 4008 | Room is full                   | Stop retrying, tell the player, offer manual retry     |
| 4013 | Relay is at its room limit     | Back off and retry with jitter; this is transient      |

`4008` and `4013` are the two that must *not* be retried tightly. See §5.2.

### 1.5 Limits and timings

| Thing                        | Default | Consequence for the mod                          |
|------------------------------|---------|---------------------------------------------------|
| `MAX_MESSAGE_BYTES`          | 8192    | Hard cap on a single frame. Budget for it (§3.6)   |
| `MAX_MODS_PER_ROOM`          | 16      | 17th player gets 4008                             |
| `MAX_MAPS_PER_ROOM`          | 8       | Browsers, not the mod's problem                   |
| `ROOM_TTL`                   | 5m      | A room survives 5 min with nobody connected       |
| `RECLAIM_TTL`                | 30m     | A swept code answers to its token for 30 min      |
| server ping / read deadline  | 54s/60s | The mod must answer WebSocket pings (§4.2)        |

The room outliving its last client by `ROOM_TTL` is why a brief drop-out, an
alt-tab, or a quick game restart needs no special handling at all: reconnecting
with the code alone resumes the same session. The token only covers the case
where the game was down *longer* than `ROOM_TTL`.

---

## 2. Design goals and non-goals

The whole design follows from one rule, inherited from the relay:

> **Codes travel *out* of the game and *into* browsers, never the other way.**

Which gives the product goal: **no logins, nothing to edit in a mod config, and
at most one paste into one browser textbox.**

### Goals

1. A player installs the mod, loads a world, and a code appears in-game.
2. Every *other* modded player in that world joins the same session with **zero
   typing** — the code reaches them over the game's own network.
3. Pasting that code into the web map shows everyone moving in real time.
4. A crash, a restart, or an alt-tab resumes the same session and the same code.
5. The mod is safe to run with unmodded players present, and safe to run when
   the dedicated server is unmodded (see §6 — this is the main open risk).

### Non-goals

- The web map. Not built here; only the wire contract in §3 is agreed here.
- Any authentication, account, or persistent server-side state.
- Replacing Valheim's own map/ping features. The mod *augments* them.
- Cross-world or cross-session features. One world load = one session.

---

## 3. Wire protocol (mod ↔ map)

**This section is the deliverable that the map also has to implement.** The relay
is agnostic, so if it isn't written down here it isn't defined anywhere.

Every frame is a single JSON object, UTF-8, one WebSocket text message, ≤ 8192
bytes. Every frame carries `"v": 1`. Receivers **must ignore unknown `type`
values and unknown fields** rather than erroring — that is what makes it possible
to ship a map change and a mod change on different days.

Coordinates are Valheim world coordinates: `x` east, `z` north, `y` altitude,
origin at world centre, playable radius ≈ 10 000. `rot` is degrees clockwise
from north. Timestamps `t` are Unix milliseconds, sender's clock, advisory only.

### 3.1 `hello` — mod → relay, first frame on every connection

```json
{
  "type": "hello",
  "v": 1,
  "name": "Bob",
  "uid": "vh_7f3c9a21",
  "mod": "1.0.0",
  "world": { "name": "Midgard", "seed": "hAbC12dEf", "seedInt": -1234567, "uid": "5713…" }
}
```

- `name` and `uid` are the two fields the relay itself reads; everything else
  just passes through to maps.
- `uid` is **stable per character save**, derived from the player profile id
  (§4.3). It is what lets a map recognise a returning player whose `playerId`
  changed. Prefix it (`vh_`) and do not send a raw Steam ID — see §8.
- `world` is what lets the map render the correct terrain. `seed`/`seedInt` are
  what world-generator sites key off. Send it once here rather than on every
  position frame.

### 3.2 `position` — mod → relay, ~1 Hz

```json
{ "type": "position", "v": 1, "x": 123.4, "z": -456.7, "y": 31.2,
  "rot": 183.5, "biome": "BlackForest", "hp": 78, "maxHp": 100, "t": 1725148800123 }
```

The highest-volume frame; keep it lean. Do **not** include `uid` — the map
already has the `playerId` → `uid` mapping from `welcome`'s roster and from
`hello`. Do not include `playerId`; the relay overwrites it anyway.

`hp`/`maxHp` are gated behind a config toggle (§7). `biome` is a convenience for
the map so it doesn't have to reimplement world generation.

### 3.3 `ping` — either direction

```json
{ "type": "ping", "v": 1, "x": 123.4, "z": -456.7, "name": "Bob", "t": 172514… }
```

A transient "look here". From a mod it reaches maps **and peer mods** (which show
it on the in-game minimap). From a map it reaches every mod. Mirrors Valheim's
own map ping and should feel identical in-game.

### 3.4 `marker` — either direction

```json
{ "type": "marker", "v": 1, "op": "add", "id": "vh_7f3c9a21:m4",
  "x": 123.4, "z": -456.7, "label": "silver here", "icon": "ore", "t": 172514… }
```

- `op` is `add` or `remove`. On `remove`, only `id` is required.
- `id` must be globally unique; namespace it with the sender's `uid` (or a
  browser-generated equivalent) so two clients cannot collide.
- `icon` is from a fixed vocabulary the mod maps onto `Minimap.PinType`; start
  with `dot`, `ore`, `boss`, `home`, `death`, `danger` and treat anything
  unrecognised as `dot`.

Markers are persistent for the life of the session. Neither the relay nor the mod
stores them across sessions.

### 3.5 `request_state` — map → relay

```json
{ "type": "request_state", "v": 1 }
```

Sent by a map right after its `welcome`. Every mod replies with its `hello` and
an immediate `position`. This exists because `welcome`'s roster carries only
`playerId`/`name`/`uid` — a map joining mid-session has no `world` block and
therefore does not know which world to draw. Mods must rate-limit their reply
(at most one per 5 s) so eight browsers reloading at once cannot amplify.

As a belt-and-braces measure the mod also re-sends `hello` every 60 s.

### 3.6 Budget

At 16 players × 1 Hz a `position` frame is roughly 130 bytes, so the room's
steady-state is ~2 KB/s per map — comfortable. The 8192-byte cap only becomes a
risk if someone adds a batched or array-valued frame later; if that day comes,
split it rather than raising the limit.

---

## 4. Mod architecture

### 4.1 Two assemblies, and why

Split the plugin so that the interesting logic can be tested without launching
Valheim:

```
src/
  ValheimRelay.Core/        pure C#, zero Unity and zero game references
    Protocol/               frame types, JSON writer/reader
    Session/                connection state machine, backoff, reclaim
    Election/               who creates the room (§5.1), driven by injected time
  ValheimRelay.Plugin/      the BepInEx plugin
    Patches/                Harmony patches, kept thin
    GameBridge.cs           reads Player/ZNet/Minimap, adapts to Core interfaces
    RelayBehaviour.cs       MonoBehaviour pump: drains queues on the main thread
    UI/                     code panel, chat line, clipboard
    Config.cs
tests/
  ValheimRelay.Core.Tests/  xUnit, deterministic clock, fake transport
```

`Core` takes its dependencies as interfaces — `IRelayTransport`, `IGameChannel`,
`IClock`, `ILog` — so the whole session lifecycle (create → broadcast → join →
drop → reclaim → rotate) is exercised in unit tests in milliseconds. Harmony
patches contain no logic beyond forwarding into `GameBridge`. This mirrors how
the relay itself is built and is what makes the project reviewable.

### 4.2 Threading

Unity APIs are main-thread only; WebSocket callbacks are not on the main thread.

- **Inbound:** socket thread → `ConcurrentQueue<string>` → drained in
  `RelayBehaviour.Update()`, which is the only place that touches game objects.
- **Outbound:** frames are *built* on the main thread (reading player state needs
  it), then pushed to a **bounded** queue that the socket thread drains.
- Under backpressure the outbound queue drops the **oldest `position` frame**
  and never drops a `ping` or `marker`. Position is lossy by nature; a dropped
  marker is a bug the player sees.
- Answer WebSocket control pings. If the library does not do it automatically,
  the 60 s server read deadline will drop the connection every minute and the
  bug will look like a network problem. Verify this early.

### 4.3 Game data to read

Confirm each symbol against the game version being targeted — Valheim renames
and re-signatures things between patches, and this list is written from the
generally-known API surface, not from a decompile of your build. Treat a
mismatch as expected work, not as a surprise.

| Need              | Expected source                                                     |
|-------------------|---------------------------------------------------------------------|
| Position / heading| `Player.m_localPlayer.transform.position` / `.rotation`             |
| Health            | `Player.m_localPlayer.GetHealth()` / `.GetMaxHealth()`              |
| Display name      | `Player.m_localPlayer.GetPlayerName()`                              |
| Stable `uid`      | `Game.instance.GetPlayerProfile().GetPlayerID()`, hashed (§8)       |
| World name / uid  | `ZNet.instance.GetWorldName()` / `.GetWorldUID()`                   |
| World seed        | `WorldGenerator.instance` → `m_world.m_seedName` / `m_seed`         |
| Biome at a point  | `WorldGenerator.instance.GetBiome(x, z)` → `Heightmap.Biome`        |
| Am I the host?    | `ZNet.instance.IsServer()`                                          |
| Peers             | `ZNet.instance.GetPeers()`, peer/self ids via `ZNet.instance.GetUID()` |
| Add/remove a pin  | `Minimap.instance.AddPin(...)` / `.RemovePin(...)`, `Minimap.PinType` |

Some of these are private fields and will need
`BepInEx.AssemblyPublicizer.MSBuild` (or equivalent) at build time.

Hook points to patch:

- `Game.Start` — register the custom RPC (§5.1); runs on client *and* server.
- `ZNet.Awake` / world load complete — begin the session state machine.
- `Player.OnSpawned` — we have a local player; start sending position.
- `ZNet.Shutdown` / `Game.Logout` — disconnect cleanly, stop the state machine.
- `Minimap.Start` — safe point to install map-driven pins.
- Ping capture: the chat/ping path (`Chat.OnNewChatMessage`, filtered to
  `Talker.Type.Ping`). **This signature has changed across game versions** — do
  not assume; check it, and fail soft if the patch does not apply.

### 4.4 Dependencies

Keep the runtime dependency surface as close to zero as possible. Two mods
shipping different versions of the same library into `BepInEx/plugins` is the
classic way to break someone's whole modpack.

- **JSON:** hand-roll a minimal writer and parser inside `Core`. The schema is
  small, fixed, and fully known; this is maybe 200 lines and removes the single
  most common assembly-conflict source (Newtonsoft). Do not take a NuGet
  dependency for this.
- **WebSocket:** try `System.Net.WebSockets.ClientWebSocket` first — no bundling
  at all if the runtime supports it. If it does not, bundle `websocket-sharp`
  with an internalised/renamed assembly so it cannot clash. This is a spike
  (§9, M0).
- **TLS:** `wss://` on Mono can fail if the certificate store is not populated.
  If it does, ship a CA bundle and validate against it, **or** pin the relay's
  certificate. Do **not** install a blanket
  `ServerCertificateValidationCallback` that returns `true` — that turns every
  player's session into an interceptable one, and it is a global setting that
  would silently weaken every other mod in the process.

### 4.5 Build

- Target the framework current Thunderstore templates use for BepInEx 5 on
  Valheim (`net462`/`net472`/`net48` depending on the game's Unity version) —
  confirm rather than guess.
- Reference game assemblies from `<Valheim>/valheim_Data/Managed/` resolved
  through an environment variable in `Directory.Build.props`. **Never commit
  game DLLs.**
- Package for Thunderstore: `manifest.json`, 256×256 `icon.png`, `README.md`,
  `CHANGELOG.md`. State clearly whether the dedicated server also needs the mod
  (§6 decides this).

---

## 5. Session lifecycle

### 5.1 Getting one code to every player

This is the part with real design content, because the zero-typing goal means
the code has to travel over Valheim's own network.

**Discovery, then election, then broadcast:**

1. On world load every modded client enters a **discovery window** (≈5 s). It
   asks over the game channel "does anyone have a code?" and listens.
2. If a code arrives during the window → join it with `role=mod&code=…`. Done,
   nothing was typed.
3. If the window closes with no code, the client checks whether it is the
   **elected creator**. If yes → connect with no code and create. If no → keep
   listening (someone else is creating) and re-ask on a slow timer.
4. The creator broadcasts the code on join, whenever a new peer connects, and on
   a 30 s heartbeat. Late joiners therefore converge without asking.

**Election rule:** if a player is the host (`ZNet.instance.IsServer()`), they
create. Otherwise the connected peer with the numerically lowest peer id creates.
This is deterministic, needs no negotiation, and every client computes the same
answer from state it already has.

**Race handling:** two clients can still both create — a network hiccup during
discovery, or two players loading simultaneously. Resolve it with a deterministic
tiebreak: **the lexicographically smaller code wins.** A client holding the
losing code disconnects and joins the winner; the losing room is empty and the
relay sweeps it after `ROOM_TTL` with no cleanup needed. This must be handled,
not just documented, or groups will silently split into two sessions.

**Transport for the code, in order of preference:**

- **A custom routed RPC** (`ZRoutedRpc.instance.Register` /
  `InvokeRoutedRPC(ZNetView.Everybody, …)`). This is the clean answer *if* a
  vanilla dedicated server forwards a routed RPC whose name it does not know.
  **That is the project's main open question** — see §6.
- **The chat channel**, as a fallback that provably works on a vanilla server:
  chat is already a routed RPC the server relays. Send a magic-prefixed message
  and have receiving mods consume and hide it via a Harmony patch. The cost is
  that *unmodded* players in the world see one odd line, so the prefix should be
  short and the message sent once rather than on a heartbeat.
- **Derive the code deterministically** from world name + server password, the
  fallback already documented in the README. Everyone who can join the world can
  compute it with no coordination at all. The costs: the code is effectively
  permanent (rotating it means changing the server password), and it cannot be
  reclaimed with a token because nobody created it. Keep this as the last
  resort.

**ZDO custom data is *not* a good fit here** even though it looks like one: ZDOs
replicate by sector proximity, so a player on the far side of the map may simply
never receive the creator's player ZDO. Do not build the code channel on it.

### 5.2 Connection state machine

```
Idle → Discovering → (Creating | Joining) → Active → Reconnecting → …
                                              ↑__________|
```

Reconnect with exponential backoff **and jitter**: 1s, 2, 4, 8, 16, capped at
30 s. Reset the backoff after 60 s of a healthy connection. Stop entirely when
the player leaves the world — a mod retrying against the relay from the main menu
is a bug.

Close-code handling is the table in §1.4. The two that need care:

- `4008` (room full) is not transient — the 17th player will never fit. Stop
  retrying, surface it to the player, and offer a manual retry.
- `4013` (relay full) *is* transient. Back off hard with jitter; if every client
  retries on the same cadence they will keep arriving in a thundering herd.

### 5.3 Reclaim, and code rotation

The creator persists `{ worldUid → { code, token } }` to a file under the plugin's
config directory. On the next load of that world the creator tries
`code + token` **first**; on `4003` or `4004` it discards the entry and creates
fresh. The point of this is narrow but valuable: a browser left open on the old
code keeps working after the game crashed and restarted.

Treat the token as a secret. It is not a password to an account, but anyone
holding it can seize the room's identity — do not log it, do not put it in chat,
do not show it in the UI.

**Rotation:** if the creator leaves for good, the room dies after `ROOM_TTL` and
the remaining players start getting `4004`. They must fall back into discovery,
elect a new creator, create a **new** code, and — importantly — tell the player
in-game that the code changed, since any open browser is now pointed at a dead
room. This is the one flow where the zero-typing promise cannot be kept, and the
UI should be explicit about it rather than failing quietly.

---

## 6. The open risk, and how to settle it

The zero-typing multiplayer flow rests on one assumption:

> A vanilla dedicated server forwards a routed RPC whose name it does not know.

Valheim's server routes a `RoutedRPCData` to its targets *before* looking the
method up locally, which is why co-op sync mods can pass custom data through
unmodded servers — but the Valheim modding wiki's own guidance is that a mod
using custom RPCs should be installed on the server too, and the behaviour is
worth confirming rather than assuming, because the whole flow depends on it.

**Settle this first (M0), empirically, against a genuinely vanilla dedicated
server.** Two modded clients, one custom RPC, one log line. The result decides
the shape of §5.1 and it decides what the Thunderstore page has to tell people
about server installs. Do not build the rest on an unverified assumption.

Whatever the outcome, the mod should degrade rather than break: try the RPC
channel, detect that no peer acknowledged within the discovery window, and fall
back to the chat channel automatically.

---

## 7. In-game UX

- **On session start:** one chat line — `ValheimRelay: map code K7MQ2XR4` —
  local-only, plus the map URL if one is configured.
- **A hotkey-toggled panel** (default Shift+F8, rebindable) showing: the code, a
  **Copy** button (`GUIUtility.systemCopyBuffer`), connection state, player count
  and map count. This is the answer to "I closed the chat and lost the code".
  Opening the panel copies the share text itself, so the button is the recovery
  path rather than a required step, and the panel draws the link as a **QR code**
  (`ValheimRelay.Core.Qr`, generated in-process — §8's credential never reaches a
  QR service) so a phone can open the map without typing. The QR is drawn only
  when a `MapUrl` is configured: a symbol carrying a bare code scans to eight
  characters with nowhere to put them.
- **A small always-visible indicator** while the session is live. Players should
  never be unsure whether their position is being broadcast.
- **Notify on state changes** that matter: disconnected, reconnecting, code
  changed (§5.3), room full.

Config entries (BepInEx config, all with sane defaults so a fresh install needs
no edits):

| Key                  | Default              | Notes                            |
|----------------------|----------------------|----------------------------------|
| `RelayUrl`           | the hosted relay     | `wss://…/ws`                     |
| `MapUrl`             | the hosted map       | Used to build a copyable link    |
| `Enabled`            | `true`               | Master switch                    |
| `ShareMyPosition`    | `true`               | Opt out, stay in the session      |
| `ShareHealth`        | `true`               | Gates `hp`/`maxHp` in §3.2       |
| `PositionInterval`   | `1.0` s              | Clamp to ≥ 0.5 s                 |
| `AcceptMapMarkers`   | `true`               | Map → in-game pins               |
| `ToggleKey`          | `F8`                 | Held with Shift by default        |
| `ToggleRequiresShift`| `true`               | Off for a bare keypress           |
| `AnnounceInChat`     | `true`               |                                   |

---

## 8. Privacy and safety

Worth being deliberate about, because the failure mode is social rather than
technical.

- **The code is the credential.** Anyone holding it sees every player's live
  position for as long as the session lives. Treat it accordingly in the UI: it
  is a share link, not a room name, and the panel should say so in a few words.
- **`ShareMyPosition = false` must still let the player stay in the session** and
  see others. An all-or-nothing switch pushes people to uninstall instead.
- **Do not send platform account identifiers.** `uid` should be a hash of the
  local profile id, not a raw Steam ID — the map has no need for a real account
  id, and it would otherwise end up in every browser in the room.
- **Never log the reclaim token** (§5.3), and remember that BepInEx logs get
  pasted into support threads routinely.
- The relay keeps nothing on disk and nothing survives a session except a code
  and a hash of its token. The mod should not be the component that introduces
  persistence — the only thing it stores is the creator's own `{code, token}`.

---

## 9. Milestones

Each milestone is independently demonstrable. Ship in this order; the first one
is deliberately a spike, because it can invalidate design decisions in §5.

**M0 — Spikes (timeboxed).** Two questions, two throwaway plugins, no product
code: (a) can we open a `wss://` WebSocket from inside Valheim's runtime, keep it
alive past 60 s, and answer control pings? (b) does a vanilla dedicated server
forward an unknown routed RPC between two modded clients (§6)? Record both
answers back into this document.

**M1 — `Core` and its tests.** Protocol types, JSON, state machine, backoff,
election, tiebreak. Runs entirely on a build machine with no game. Done when the
full create → broadcast → join → drop → reclaim → rotate cycle is covered by
xUnit tests against a fake transport and a deterministic clock.

**M2 — Single player, outbound.** Plugin loads, connects to a locally-running
relay (`make run` in this repo, `ws://localhost:8080/ws`), creates a room, shows
the code in chat and in the panel, and streams `hello` + `position`. Verified
with a stub map client that prints frames.

**M3 — Multiplayer, zero typing.** Discovery, election, code broadcast, late
joiners, the two-creator tiebreak. Verified with two clients on a host, and again
on a dedicated server, per M0's answer.

**M4 — Inbound.** Markers and pings from the map appear on the in-game minimap;
pings from a peer mod appear too. Marker `remove` works. Unknown `icon` values
degrade to `dot`.

**M5 — Resilience.** Reconnect with backoff, token persistence and reclaim, every
close code from §1.4 handled, code rotation when the creator leaves, clean
shutdown on logout. Verified by killing things: the relay, the network, the game.

**M6 — Packaging.** Thunderstore manifest, icon, README with the privacy note
from §8, changelog, and a clear statement about whether the dedicated server
needs the mod.

---

## 10. Testing

- **Unit (`Core`)** — the bulk of the logic, no game required. This is the point
  of the split in §4.1.
- **Integration against the real relay** — run this repository locally with
  `make run`, point the plugin at `ws://localhost:8080/ws`. A small stub map
  client that connects with `role=map&code=…` and prints frames is worth building
  early and is useful for the whole project; the relay's own `main_test.go` shows
  exactly how to drive a client.
- **Manual matrix**, per release: single player; host + one client; vanilla
  dedicated server; modded dedicated server; a map joining mid-session; game
  crash and restart within `ROOM_TTL`; game restart *after* `ROOM_TTL` (reclaim);
  17th player (4008); unmodded player present in the world.
- **Performance** — confirm no measurable frame-time cost at the default 1 Hz.
  JSON is built on the main thread; if it ever shows up in a profile, move the
  serialisation (not the game-state read) onto the socket thread.

---

## 11. Decisions still to make

These are genuinely open and should be settled explicitly rather than by
accident:

1. **Does the dedicated server need the mod?** M0/§6 answers this, and it changes
   the install instructions and the support burden.
2. **What is the default `RelayUrl`?** Shipping a default means hosting an
   instance and owning its capacity (`MAX_ROOMS` = 1000). Shipping no default
   means every user edits a config, which breaks the "nothing to edit" goal.
3. **Map URL format.** `https://map.example/#K7MQ2XR4` lets the mod offer one
   copyable link instead of a code plus instructions. Needs the map to agree.
4. **Minimum game version supported**, and what the mod does when a Harmony patch
   fails to apply after a game update — fail soft with a clear log line, or
   refuse to load?
5. **Exploration/fog sharing** (`Minimap.m_explored`) is an obvious v2 feature and
   an obvious way to blow past the 8192-byte frame limit. Out of scope for v1;
   if it happens, it needs its own chunked design.

---

## 12. Addendum — gaps found while implementing

Written after building §§1–5 and M1. Each entry is something the plan as
originally written either gets wrong, leaves undefined at a point where two
implementers would diverge, or does not mention at all. Where the fix is already
implemented it says so and names the test that holds it.

Numbering is stable; treat these as amendments to the sections they cite.

### 12.1 `uid` as specified is not anonymous

§8 says `uid` "should be a hash of the local profile id, not a raw Steam ID". A
bare hash does not achieve what that sentence is for. A Valheim profile id
derives from a Steam ID, and the space of real Steam IDs is small and
enumerable — anyone with a list can hash the lot and invert an unsalted SHA-256
by lookup in seconds. The `uid` would then be a raw account identifier wearing a
disguise, in every browser in the room, which is the exact outcome §8 wants to
avoid.

**Fix, implemented:** HMAC-SHA256 keyed with a random 32-byte per-install salt,
truncated to 64 bits. `uid` stays stable for as long as the install lives, which
is all §3.1 asks of it, and carries no recoverable account identifier. The salt
sits beside the config; losing it costs nothing but a new identity.

`StableUid`, and `StableUidTests.IsNotAnUnsaltedHashOfTheProfileId`.

### 12.2 The double-create race the tiebreak is for cannot be staggered by rank

§5.1 elects the host, or the lowest peer id, and §5.1's tiebreak cleans up when
two clients create anyway. The obvious cheap mitigation — delay creation by the
client's rank in the election ordering — does not work, and it is worth writing
down why, because it looks like it should.

The race is two clients loading simultaneously, each with a peer list that does
not yet contain the other. Both therefore believe they are the lowest id, and
both are **rank 0**. A client at rank above 0 is by definition not the elected
creator and never reaches the creation path at all, so a rank-based stagger
delays nobody.

**Fix, implemented:** stagger by a deterministic mix of the client's own peer
id, spread over a few seconds, with the host always at zero. Two clients that
cannot see each other still get different delays, so the later one hears the
earlier one's announcement during its wait and joins instead of creating. The
tiebreak remains for when this does not save us.

`CreatorElection.CreationStagger`, and
`ACodeArrivingDuringTheStaggerIsJoinedInsteadOfCreating`.

### 12.3 The tiebreak needs a generation, and needs to forget the losing token

Two problems with "the lexicographically smaller code wins" taken alone.

**The losing creator still holds a token.** §5.1 says it disconnects and joins
the winner, and stops there. But it persisted `{code, token}` for this world
under §5.3, and that entry now points at the room it just abandoned. On the next
load of that world it will reclaim that dead room in preference to discovering
the live one — and split the group again, silently, every single time.

**A rotation inverts the rule.** After §5.3's rotation the group is deliberately
on a *new* code, which may sort larger than the dead one. A peer that has not
noticed the rotation re-announces the old code, and "smaller wins" hands the
whole group back to a room the relay has already swept.

**Fix, implemented:** announcements carry a monotonic epoch. A later generation
always beats an earlier one; the code comparison only breaks ties *within* a
generation, which is the case it was actually written for. A new room claims one
past anything heard. Separately, codes that answered with 4004 are remembered as
dead — keyed by generation, so a creator legitimately reclaiming its old code in
a later generation is still heard. And the losing creator discards its stored
entry at the moment it loses.

`CodeArbiter`, and `TheLosingCreatorAlsoDiscardsItsTokenForTheAbandonedRoom`,
`ALaterGenerationBeatsASmallerCode`, `ADeadCodeIsNotRejoinedWhenAPeerAnnouncesItAgain`.

### 12.4 Markers do not survive the thing players actually do

§3.4 says markers are "persistent for the life of the session". Nothing stores
them. §3.5 has `request_state` replay `hello` and a `position` and nothing else.
So the moment a browser reloads — the single most likely thing that happens to a
web map — every marker in the session is gone, and the protocol says they should
not be.

**Fix, implemented:** each mod keeps the markers it created and replays them
alongside its `hello`. A mod replays only its *own*; map-originated markers are
the map's to remember, and a mod re-announcing them would let two maps
resurrect a marker a third client deleted. Capped at 64 per mod so the replay
cannot become every new map's join cost.

`MarkerStore`, and `RequestStateAlsoReplaysOurMarkersSoAReloadedMapDoesNotLoseThem`.

**Open, for the map:** the map must persist its own markers locally. Nothing in
this repository can make that happen.

### 12.5 `request_state` inside the cooldown

§3.5 caps the reply at one per 5 s but does not say what happens to a request
that arrives inside that window. Dropped, or answered late? Two implementers
will choose differently, and dropping is worse than it looks: a map that reloads
one second after another map gets no world block at all until the 60 s `hello`
heartbeat, which reads as a broken map.

**Fix, implemented:** coalesced, not dropped. A request inside the cooldown sets
a pending flag and one reply goes out when the window expires. Eight browsers
reloading at once still produce exactly one replay, which is what the cap is for.

`ARequestArrivingInsideTheCooldownIsAnsweredWhenItExpires`.

**Also, and this belongs in §3.5's wording:** a map sends `request_state` right
after `welcome` — not optionally. The harness here modelled it as optional at
first and two integration tests failed, which is exactly how a map author would
discover it.

### 12.6 States the wire format cannot express

Three things the map needs to distinguish and §3 gives it no way to.

**Opted out vs. gone.** §7's `ShareMyPosition = false` is specified to keep the
player in the session (§8, correctly — an all-or-nothing switch pushes people to
uninstall). But that player's `position` frames simply stop, which on the wire is
indistinguishable from a client that has frozen or dropped. The map shows them as
a ghost standing wherever they last were.
**Fix, implemented:** `hello` carries `"share": false`. Omitted when true, so
older maps are unaffected.

**Dead vs. standing very still.** A dead player's last position is their corpse.
Without a flag the map draws them as alive and stationary, indefinitely.
**Fix, implemented:** `position` carries `"dead": true` when dead. Note this
cannot be inferred from `hp`, which §7 gates behind `ShareHealth`.

**Still vs. stopped.** Once positions are sent only on movement (below), silence
becomes ambiguous.
**Fix, implemented:** a keepalive position every 10 s regardless of movement.

### 12.7 There is no local player most of the time you might ask

Not mentioned anywhere in §4.3, and cheap to get wrong. `Player.m_localPlayer` is
null while loading, while dead before respawn, and in menus. The tempting
handling — send a default sample — is badly wrong here, because the world origin
is a real place in Valheim: every such player would appear standing on the spawn
stone, and a map watching a group mid-load would show a crowd on it.

**Fix, implemented:** `GameBridge.TryReadPosition` returns false and the frame is
simply not sent.

### 12.8 §3.6's budget assumes telemetry nobody needs

§3.6 budgets 16 players × 1 Hz unconditionally. Most of those frames are a player
standing at a workbench sending byte-identical data. A dead-band — 1 m of
movement, 5° of turn, any health or biome or death change — cuts the steady-state
cost to near zero for a stationary group at no cost in fidelity, with the
keepalive in §12.6 underneath it so silence stays meaningful.

`PositionThrottle`.

### 12.9 The relay is not in this repository

§10 says to test "against the real relay" by running this repository locally with
`make run`; §9's M2 says to point the plugin at `ws://localhost:8080/ws`; §1 is
written as a restatement of a `main.go` that lives here. None of it is here. This
repository contains PLAN.md and the mod; the relay is deployed from elsewhere.

As written, then, the plan's entire integration testing strategy has nothing to
run against.

**Fix, implemented:** `tools/devrelay` implements the §1 contract as a clearly
labelled development fixture, with its limits exposed as flags so a test can
reach `MAX_MODS_PER_ROOM` with two clients. It is not a specification — where it
disagrees with the production relay, the production relay is right, and the
disagreement is a bug here.

**Still open:** whether the real relay should be vendored, submoduled, or left
where it is. That is a decision about how these two repositories relate and it
should be made deliberately rather than by leaving §10 pointing at nothing.

### 12.10 Backpressure as described cannot be implemented as described

§4.2 says the outbound queue "drops the oldest `position` frame and never drops
a `ping` or `marker`". A single FIFO cannot do that without scanning, and the
description quietly assumes one.

**Fix, implemented:** split the structure instead — one overwrite slot for the
latest position, one bounded FIFO for frames that must not be lost, reliable
drained first. That is the intended policy exactly, in O(1), and it makes
"a marker never waits behind telemetry" true as well as "a marker is never
dropped". When the reliable lane is genuinely full it refuses the *newest*
frame rather than the oldest, because dropping the head would reorder a marker
add/remove pair into a resurrection.

`OutboundQueue`.

### 12.11 Smaller things, decided

- **A deliberate close looks exactly like a dropped connection.** Migrating to a
  winning code closes the socket, and the resulting close event was being read as
  connection loss and scheduling a reconnect over the top of the join. Deliberate
  closes are now counted and ignored. `MigratingDoesNotLeaveAPhantomReconnectBehind`.
- **A reconnect must resume the room, not create a new one.** After a generic
  drop, a creator that reconnects with no code gets a *new* room and strands
  every peer and browser on the old code. The code and token from `welcome` are
  now retained for exactly this.
  `AGenericDropReconnectsToTheSameRoomRatherThanCreatingANewOne`.
- **A rotation was being announced as a fresh session.** The player-facing "your
  code changed" in §5.3 depends on remembering the previous code across the
  disconnect that killed it, which the obvious implementation does not.
  `ARotatedCodeIsAnnouncedToThePlayerAsAChange`.
- **`RelayUrl` needs normalising.** Players will paste `https://`, or a bare
  host, or omit `/ws`. All three are absorbed; a scheme-less host defaults to
  `wss://` so nobody ends up unencrypted by accident. `RelayUrl`.
- **The chat fallback exposes the credential to unmodded players.** §5.1 notes
  they "see one odd line" but frames it as cosmetic. It is not only cosmetic:
  §8 establishes the code *is* the credential, so the fallback broadcasts it to
  everyone in the world, modded or not. They are already in your world so the
  exposure is small — but it is why chat is the fallback rather than the default,
  why the message is sent once rather than on the heartbeat, and it belongs in
  the Thunderstore privacy note.
- **`ClientWebSocket` is in netstandard2.0.** So §4.4's preferred option needs no
  bundled library and no package reference. M0(a) is narrowed to "does Valheim's
  Mono runtime carry a working one, TLS included".

### 12.13 Defects found in review, and fixed

A review pass over `Core` after M1 found nine. Recording them because most are
not visible from the code they affect, and three would have shipped as bugs a
player would report but nobody could reproduce.

- **A refused frame was re-sent out of order.** The drain loop dequeued, and on
  a transport refusal re-enqueued — which appends to the *tail*. A marker
  add/remove pair refused mid-drain came back as remove-then-add, leaving an
  undeletable phantom marker on every map. A refused *position* was worse: it
  had no lane to go back to and was promoted into the reliable queue, inverting
  the §4.2 drop policy. Replaced with peek-send-commit, so a refused frame never
  moves at all.
- **Shutdown swallowed the next run's first disconnect.** `Stop()` counted a
  deliberate close that the handler never consumed, and the credit survived into
  the next session: the first genuine 1006 was ignored and the session sat
  `Active` behind a dead socket for ever.
- **A token rejection permanently split the group.** Entering discovery did not
  clear the arbiter, so it kept *defending* the code it had just been thrown off,
  and ignored every announcement of the live one.
- **A full marker replay overflowed its own queue.** `hello` plus 64 markers is
  65 frames against a capacity of 64, so the replay dropped its own tail — the
  exact failure §12.4 was written to prevent. The capacity is now derived from
  `MarkerStore.MaxOwnedMarkers` rather than being a number that happened to look
  large enough.
- **A socket that opened but never sent `welcome` hung for ever.** No deadline on
  `Creating`/`Joining`: no retry, no notice, nothing logged. A wedged proxy or a
  relay mid-restart would do this.
- **The creator's heartbeat restarted joins in progress.** A second announcement
  of the code being joined tore down the in-flight connect and started again —
  and the creator announces every 30 s, so this was the normal case, not an edge.
- **A corrupt identity salt threw on every world load.** `Salt` returned what it
  read without validating; `Derive` throws on a bad one, and nothing caught it.
  Now validated and regenerated, and the plugin checks rather than assumes.
- **`/ws` was appended after the query string**, turning
  `wss://host/ws?tenant=abc` into `…?tenant=abc/ws`, which disagreed with the
  transport's own query builder.
- **Inbound fragment reassembly was unbounded.** A peer that never sets
  `EndOfMessage` grew the buffer indefinitely; it is now capped and the
  connection dropped.

Each has a test in `RegressionTests` named for the failure it prevents.

### 12.14 The fixture was never committed, and CI did not notice

Worth recording as a process finding rather than a code one, because the same
shape will recur.

`.gitignore` carried `tools/**/devrelay` and `tools/**/stubmap`, meant to keep
compiled Go binaries out of the repository. `**` matches zero or more path
segments, so those patterns also matched the `tools/devrelay` **directory** —
and the entire dev relay and stub map were never committed. Three commits
described them in detail. `git status` stayed clean throughout, because ignored
files are not reported as untracked.

CI did not catch it, and the reason is the more useful half: the integration
tests skip themselves when the Go toolchain is unavailable, and
`DevRelay.TryStart` swallowed *every* exception on the way to that decision —
including "the fixture directory does not exist". So seven tests reported as
skipped, the job reported success, and the only thing exercising the real
transport had silently been doing nothing.

Three fixes:

- The ignore rules are now anchored to the exact binary paths, so no pattern can
  match a directory.
- `TryStart` returns null only for a genuinely absent `go` binary. Every other
  failure propagates and fails the test, because a test that cannot run must say
  so loudly enough to fail a build.
- CI installs Go and **fails the job if any test skipped**. A skipped test is not
  a passing test, and the integration suite is the only thing covering the real
  transport and the real relay contract.

The general lesson: `git status` being clean is not evidence that work is
committed. `git ls-files` on a new directory is, and it costs nothing.

### 12.12 Still open, and needing a decision

1. ~~**§11.2, the default `RelayUrl`.**~~ **Settled:**
   `wss://valheimrelay.bobmitch.com/ws`, shipped as the default, which is what
   keeps §2's "nothing to edit" promise. Two consequences worth being explicit
   about rather than discovering later:
   - That instance now carries every installed copy of the mod, bounded by its
     own `MAX_ROOMS` (1000 by default, §1.5). A player who reaches that limit
     sees close code 4013, which the mod already backs off from hard — but the
     limit is now an operational concern for whoever runs the relay, not a
     theoretical one.
   - The address is compiled into every install, so changing it later strands
     old versions. If it is ever likely to move, it wants a stable hostname in
     front of it rather than the deployment's own.

   ~~It has **not** been verified from a machine that can reach it.~~ **Now
   verified.** The handshake was checked against the host directly — create
   returns a welcome, an unknown code closes 4004 as §1.4 requires — and then a
   real game session started against it through the shipped default, which is
   the check that counts. This is no longer the thing left to confirm before
   release; what remains in-game is the list the README keeps, headed by whether
   a Mono-side session outlives the ping interval.
2. **§11.1, the dedicated server.** Needs M0(b). The mod degrades automatically
   either way, so this is a documentation and support-burden question rather than
   a design one now.
3. ~~**§11.3, the map URL format.**~~ **Settled:** `https://bobmitch.com/valheim`,
   with the code as a fragment — `https://bobmitch.com/valheim#K7MQ2XR4`. §2's
   "one paste into one browser textbox" is now met: the panel copies a link.

   Two corrections to the form §11.3 sketched:
   - It wrote `<base>/#<code>`, which is right for a map at the root of a host
     and wrong for one at a path. `…/valheim/#CODE` depends on the server
     redirecting the trailing slash, and plenty do not. The slash is now added
     only when there is no path.
   - The fragment is not a stylistic choice and should not be "improved" into a
     query parameter later. It is the only part of a URL a browser does not send
     to the server, so it keeps the code — which §8 establishes is the
     credential — out of the map's access logs, out of referrer headers, and out
     of whatever analytics the page loads. `?code=` would leak it to all three.

   The map must read the code from `location.hash`. Nothing in this repository
   can enforce that, so it is the one remaining thing the two halves have to
   agree on.
4. **Reclaim only fires for the elected creator.** A client that created the
   session last time but is not elected this time leaves its stored entry unused
   and the group gets a new code. Correct, but it quietly narrows §5.3's promise
   in multiplayer; in single-player, where the promise matters most, it always
   holds.
5. **Marker ids are not reused across a reconnect.** The sequence resets with the
   session, and ids are namespaced by `uid`, so a reconnecting client can emit an
   id it used earlier in the same session. Harmless today because a reconnect
   replays the markers anyway, but it is an assumption worth not building on.
