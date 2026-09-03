# Changelog

## 0.1.0 — unreleased

First working build. Not yet verified in-game (see the repository README for
what M0 still has to settle).

- Session lifecycle: create, join, reclaim after a crash, and rotate when the
  creator leaves for good.
- Zero-typing code sharing between modded clients, over a routed RPC with an
  automatic chat fallback.
- Position, ping and marker telemetry to the web map.
- Pings and markers from the map appear on the in-game minimap.
- Shift+F8 panel with the code, connection state and a copy button. Opening it puts
  the map link on the clipboard, and draws it as a QR code so a phone can open
  the map without typing. The QR is generated in-process; the session code is
  never sent to a QR service.
- Reconnects with exponential backoff and jitter; every relay close code handled.
