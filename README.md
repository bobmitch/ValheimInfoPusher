# ValheimInfoPusher — the Valheim mod

A BepInEx plugin that reports player telemetry to the ValheimRelay WebSocket
relay and draws back what the web map sends. [PLAN.md](PLAN.md) is the design
brief; **[§12](PLAN.md#12-addendum--gaps-found-while-implementing) records what
implementing it turned up**, and is the first thing to read after the plan.

## Status

| Milestone | State |
|---|---|
| M0 — spikes | **(a) answered: yes**, in-game. (b) still open — needs a dedicated server |
| M1 — `Core` and its tests | **Done.** 226 tests, no game required |
| M2 — single player, outbound | **Loads and starts a session in-game.** Whether position frames arrive is untested — there is no map yet to receive them |
| M3 — multiplayer, zero typing | Code complete; patches apply on the current game build. Never run with a second player |
| M4 — inbound markers and pings | Code complete. Not testable until a map exists to send them |
| M5 — resilience | **Done in `Core`.** Reconnect and reclaim unverified in-game |
| M6 — packaging | Manifest, README and changelog written; no icon yet |

**The web map does not exist yet.** Only the mod and the relay have been built,
so `https://bobmitch.com/valheim` is a default pointing at nothing. Everything
inbound (§4 markers, pings) and the whole point of the outbound stream are
consequently unverified end to end: the mod sends, and nothing is reading.

The relay at `wss://valheimrelay.bobmitch.com/ws` **is up**, and the handshake
contract is confirmed against it rather than only against the fixture: `role=mod`
with no code creates a session and returns a welcome, and an unknown `code=`
closes 4004 as §1.4 requires. So a fresh install needs no config edits (§11.2),
and F9 copies a link that will work once there is something at the other end
(§11.3).

### What running it in Valheim has now shown

Loaded through BepInEx on a real install, single player. From `LogOutput.log`:
the plugin loads, `PatchAll` applies cleanly, the routed RPC registers, and a
session starts with a code — `map code ########`, [`RelaySession`
§SessionStarted](src/ValheimRelay.Core/Session/RelaySession.cs). (Redacted: a
live code is a credential, per the privacy note in `packaging/README.md`.)

Reaching that line means a `wss://` socket opened from Valheim's Mono runtime
with TLS working, and that the relay's `welcome` frame parsed. Absent from the
log is any "stayed dormant" line or the `Chat.OnNewChatMessage was not found`
warning, so the chat signature §4.3 warns is version-volatile **matches this
game build**.

Still unverified in-game, in rough order of risk: that the connection survives
past the ping interval; that reconnect and code reclaim work across a restart
(§5.3); that a second player joins without typing (§5.1); and every inbound
path, which needs the map.

### The two M0 spikes

**(a) Can a `wss://` WebSocket be opened from inside Valheim's runtime, kept
alive past 60 s, and answer control pings? Opening it: answered, yes.**
`ClientWebSocket` turns out to be in the netstandard2.0 surface, so
[`ClientWebSocketTransport`](src/ValheimRelay.Core/Session/ClientWebSocketTransport.cs)
needs no bundled library and no package reference at all — the best possible
outcome for §4.4. It is verified end-to-end on .NET 8 including past the ping
interval, and now verified to connect and complete a session handshake from
Valheim's Mono build over TLS. **The longevity half is not confirmed there
yet**: nobody has watched a Mono-side session outlive the ping interval.

**(b) Does a vanilla dedicated server forward an unknown routed RPC?** Still
unanswered; it needs a dedicated server. The mod degrades rather than depending
on it: it tries the RPC, and if no peer answers within the discovery window it
switches to the chat channel automatically — and that chat patch is now known to
attach on the current build.

## Layout

```
src/ValheimRelay.Core/      pure C#, zero Unity and zero game references
src/ValheimRelay.Plugin/    the BepInEx plugin — needs a Valheim install to build
tests/ValheimRelay.Core.Tests/
tools/devrelay/             a development relay fixture + the stub map client
packaging/                  Thunderstore manifest, README, changelog
```

## Building

`Core` and its tests build and run anywhere:

```sh
dotnet test ValheimRelay.sln
```

The plugin needs the game's assemblies and is deliberately **not** in the
solution, so that CI and a fresh clone work without it:

```sh
export VALHEIM_INSTALL="$HOME/.steam/steam/steamapps/common/Valheim"
dotnet build src/ValheimRelay.Plugin -c Release
```

Game DLLs are never committed.

Restore pulls `BepInEx.Core` and `BepInEx.PluginInfoProps` from BepInEx's own
feed, which `NuGet.config` declares — they are not on nuget.org, and without
that file the plugin fails to restore with `NU1101`.

### On Windows

`build.ps1` does the whole thing: checks the toolchain, finds the Steam library
Valheim actually lives in, builds, and optionally installs the result into
`BepInEx/plugins`.

```powershell
winget install --id Microsoft.DotNet.SDK.8 -e   # once, then reopen the shell
.\build.ps1 -Deploy
```

- `-Clean` after a game update — the publicized copy of `assembly_valheim` is
  cached under `obj/` and is otherwise reused against the new version.
- `-ValheimInstall <path>` if the search misses it.
- `-PluginsDir <path>` to install into an r2modman profile rather than the
  Steam folder.

BepInEx still has to be installed in the game itself (Thunderstore:
`denikson-BepInExPack_Valheim`); the script warns if it is missing rather than
leaving you with a plugin that silently never loads.

## Running it locally

The relay this mod talks to is **not in this repository** — see
[§12.9](PLAN.md#129-the-relay-is-not-in-this-repository). `tools/devrelay` is a
stand-in implementing the §1 contract so there is something to develop against:

```sh
cd tools/devrelay
go run ./cmd/devrelay                    # ws://localhost:8080/ws
go run ./cmd/stubmap -code K7MQ2XR4      # watch a session as the map would
```

Point `RelayUrl` at `ws://localhost:8080/ws` and the plugin will connect to it.

`stubmap` points at whatever relay you give it, but **it cannot talk to the
hosted relay**: it asks for `role=map` and the deployed relay names that role
`web`, so the handshake is refused before it upgrades. Against the fixture it is
the quickest way to watch a session.

To check the hosted relay instead, ask it directly for the two things the mod
does — create, then rejoin:

```sh
# create: expect 101, then a welcome frame
curl -isS -H "Connection: Upgrade" -H "Upgrade: websocket" \
  -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" \
  "https://valheimrelay.bobmitch.com/ws?role=mod"

# rejoin an unknown code: expect 101, then close 4004
curl -isS -H "Connection: Upgrade" -H "Upgrade: websocket" \
  -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" \
  "https://valheimrelay.bobmitch.com/ws?role=mod&code=AAAAAAAA"
```

The integration tests do not touch the hosted relay. Each one starts its own
`tools/devrelay` on `127.0.0.1` and drives the real session over a real socket
against that, so they need the Go toolchain and skip themselves without it.

## What is worth reviewing first

- [`RelaySession`](src/ValheimRelay.Core/Session/RelaySession.cs) — the state
  machine, and where most of the design decisions live.
- [`CodeArbiter`](src/ValheimRelay.Core/Election/CodeArbiter.cs) — the tiebreak,
  and the two cases §5.1's rule alone gets wrong.
- [PLAN.md §12](PLAN.md#12-addendum--gaps-found-while-implementing) — the gaps.
