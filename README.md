# ValheimInfoPusher — the Valheim mod

A BepInEx plugin that reports player telemetry to the ValheimRelay WebSocket
relay and draws back what the web map sends. [PLAN.md](PLAN.md) is the design
brief; **[§12](PLAN.md#12-addendum--gaps-found-while-implementing) records what
implementing it turned up**, and is the first thing to read after the plan.

## Status

| Milestone | State |
|---|---|
| M0 — spikes | **Open.** Neither spike can be run without the game; see below |
| M1 — `Core` and its tests | **Done.** 172 tests, no game required |
| M2 — single player, outbound | **Code complete, unverified in-game** |
| M3 — multiplayer, zero typing | **Code complete, unverified in-game** |
| M4 — inbound markers and pings | **Code complete, unverified in-game** |
| M5 — resilience | **Done in `Core`**, unverified in-game |
| M6 — packaging | Manifest, README and changelog written; no icon yet |

Everything marked "unverified in-game" is written and reviewed but has never
been loaded into Valheim, because no machine here has the game. That is the
honest state: the logic is covered by tests and by end-to-end runs against a
local relay, and the parts that touch `Player`, `ZNet`, `Minimap` and `Chat`
are untested against a real build.

### The two M0 spikes

Both are unresolved and both need the game:

**(a) Can a `wss://` WebSocket be opened from inside Valheim's runtime, kept
alive past 60 s, and answer control pings?** Partly answered. `ClientWebSocket`
turns out to be in the netstandard2.0 surface, so
[`ClientWebSocketTransport`](src/ValheimRelay.Core/Session/ClientWebSocketTransport.cs)
needs no bundled library and no package reference at all — the best possible
outcome for §4.4. It is verified end-to-end on .NET 8, including surviving past
the ping interval. Whether Valheim's Mono build carries a working
`ClientWebSocket`, TLS included, is still open.

**(b) Does a vanilla dedicated server forward an unknown routed RPC?** Not
answered, and unanswerable here. The mod degrades rather than depending on it:
it tries the RPC, and if no peer answers within the discovery window it switches
to the chat channel automatically.

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

The integration tests drive the real session over a real socket against that
relay, and skip themselves when the Go toolchain is absent.

## What is worth reviewing first

- [`RelaySession`](src/ValheimRelay.Core/Session/RelaySession.cs) — the state
  machine, and where most of the design decisions live.
- [`CodeArbiter`](src/ValheimRelay.Core/Election/CodeArbiter.cs) — the tiebreak,
  and the two cases §5.1's rule alone gets wrong.
- [PLAN.md §12](PLAN.md#12-addendum--gaps-found-while-implementing) — the gaps.
