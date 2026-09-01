# ValheimRelay

A live web map of your Valheim world. Load a world, a code appears in-game,
paste it into the map, and everyone shows up moving in real time.

## How it works

- Install the mod, load a world, and a code appears in chat and in the panel.
- **Other modded players in that world join automatically.** Nobody types
  anything — the code travels over the game's own network.
- Paste the code into the web map to watch everyone move.
- A crash, a restart, or an alt-tab resumes the same session and the same code.

Press **F9** for the panel: the code, a copy button, connection state and player
count. It is rebindable in the config.

## Do other players need the mod?

Only players who want to appear on the map. The mod is safe to run with unmodded
players present, and it does not change anything they can see.

**Whether the dedicated server needs it is not settled yet.** The mod tries a
custom network message first and falls back to the chat channel automatically if
the server does not forward it, so it works either way — but on the fallback path
unmodded players in the world will see one short `[vrelay]` line when the code is
shared. This note will be replaced with a definite answer once it is measured on
a vanilla server.

## Privacy — worth thirty seconds

**The code is the credential.** Anyone holding it can watch every player in the
session move, live, for as long as the session lasts. Treat it like a share link,
not like a room name. Don't post it anywhere public unless you mean to.

What the mod sends: your display name, your position, your heading, your biome,
and (if `ShareHealth` is on) your health. What it does not send: your Steam ID or
any other account identifier. The identifier the map sees is a random,
per-install value that cannot be traced back to your account.

`ShareMyPosition = false` keeps you in the session and still shows you everyone
else — you just do not appear on the map yourself. It is not all-or-nothing.

The relay keeps nothing on disk. The only thing the mod stores is the code and
reclaim token for worlds where it created the session, in
`BepInEx/config/ValheimRelay.session.json`. Delete that file and you get a fresh
code next time.

If you are on a `ws://` relay rather than `wss://`, your position is travelling
unencrypted. The default is `wss://` for that reason.

## Configuration

Everything is defaulted; a fresh install needs no edits.

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Master switch |
| `RelayUrl` | see config | Relay address |
| `MapUrl` | empty | Used to build a copyable link instead of a bare code |
| `AnnounceInChat` | `true` | Print the code in chat when a session starts (local only) |
| `ShareMyPosition` | `true` | Broadcast your position |
| `ShareHealth` | `true` | Include health |
| `AcceptMapMarkers` | `true` | Let the map place pins on your minimap |
| `PositionInterval` | `1.0` | Seconds between updates, minimum 0.5 |
| `ToggleKey` | `F9` | Shows the panel |

## Troubleshooting

**The code changed and my browser stopped working.** The player who created the
session left, and the session was rebuilt under a new code. The mod tells you in
chat when this happens; paste the new code into the map.

**"This session is full."** Sixteen players is the limit. The panel has a retry
button for when someone leaves.

**Nothing happens after a game update.** Check `BepInEx/LogOutput.log` for a line
from ValheimRelay. The mod is built to stay dormant and say so rather than break
your game, so a game update disables it rather than crashing you.
