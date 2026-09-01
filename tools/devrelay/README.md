# devrelay — a development fixture, not the production relay

PLAN.md is written as though the Go relay lives in this repository: §10 says to
"run this repository locally with `make run`", §9's M2 says to point the plugin
at `ws://localhost:8080/ws`, and §1 describes `main.go` and `main_test.go`. None
of that is here — this repository contains the **mod**, and the relay is
deployed from somewhere else.

That leaves the integration testing the plan depends on with nothing to run
against, so this directory holds a small stand-in that implements the contract
in PLAN.md §1 and nothing beyond it.

**It is a test fixture.** Do not deploy it, and do not treat it as a
specification. Where it disagrees with the real relay, the real relay is right —
and the disagreement is worth fixing here, because that is the whole point of
having it.

## Running

```sh
go run ./cmd/devrelay                 # listens on :8080
go run ./cmd/devrelay -addr :9000     # somewhere else
go run ./cmd/devrelay -v              # log every frame
```

Then point the plugin's `RelayUrl` at `ws://localhost:8080/ws`.

## Watching a session as the web map would

`stubmap` is the "small stub map client that connects with `role=map&code=…`
and prints frames" from §10.

```sh
go run ./cmd/stubmap -code K7MQ2XR4
go run ./cmd/stubmap -code K7MQ2XR4 -request-state   # exercise the §3.5 replay
go run ./cmd/stubmap -code K7MQ2XR4 -ping 100,-250   # drive an in-game ping
```

## What it implements

Everything the mod can observe, from §1:

- `role`, `code` and `token` query parameters; create, join and reclaim.
- Crockford base32 codes, with the forgiving inbound normalisation of §1.1
  (case, spaces, hyphens, underscores; `I`/`L` → `1`, `O` → `0`).
- The `welcome` frame, including `token` for the creator only, and the roster.
- `playerId` overwritten on every mod frame; every other field passed through.
- Mod frames reach every map, and reach other **mods** only for `ping` and
  `marker`.
- Map frames broadcast verbatim to every mod and to other maps.
- `player_joined` / `player_left`, to maps only.
- Close codes 4003, 4004, 4008 and 4013.
- `ROOM_TTL` and `RECLAIM_TTL` sweeping, `MAX_MESSAGE_BYTES`, and the
  54 s / 60 s ping and read deadlines — which is what makes this fixture able
  to catch the M0(a) failure where a client does not answer control pings and
  gets dropped once a minute.

Limits are flags, so a test can reach a limit without 16 clients:

```sh
go run ./cmd/devrelay -max-mods 2 -room-ttl 10s -max-rooms 1
```
