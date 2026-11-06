# Changelog

## Unreleased

- Fixed the panel sometimes staying on "looking for a session" for ever,
  mostly on dedicated servers. If no session code arrives from another player
  within about 12 seconds, the mod now starts a session itself.
- While it is looking, the panel says what it is waiting for.

## 0.1.2

- Steam achievements keep working with the mod installed, so no separate
  achievements mod is needed. Only Valheim's "modded" rule is lifted;
  devcommands, cheat world modifiers and spawned items still switch
  achievements off. `KeepAchievements` (on by default) turns it off.

## 0.1.1

First release.

- Session lifecycle: create, join, reclaim after a crash, and rotate when the
  creator leaves for good.
- Zero-typing code sharing between modded clients, over a routed RPC with an
  automatic chat fallback.
- Position, ping and marker telemetry to the web map.
- Pings and markers from the map appear on the in-game minimap, and
  `StreamerMode` turns that half off in one switch for a code you are sharing
  publicly.
- Shift+F8 panel with the code, connection state and a copy button. Opening it puts
  the map link on the clipboard, and draws it as a QR code so a phone can open
  the map without typing. The QR is generated in-process; the session code is
  never sent to a QR service.
- Reconnects with exponential backoff and jitter; every relay close code handled.
