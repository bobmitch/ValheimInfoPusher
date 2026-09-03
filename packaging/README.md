# ValheimRelay

A live web map of your Valheim world. Load a world, a code appears in-game,
paste it into the map, and everyone shows up moving in real time.

## How it works

- Install the mod, load a world, and a code appears in chat and in the panel.
- **Other modded players in that world join automatically.** Nobody types
  anything — the code travels over the game's own network.
- Press **Shift+F8**. The map link is already on your clipboard — paste it into a
  browser. Or point your phone at the QR code in the panel, which opens the same
  link without typing anything. Or read the code aloud; it is deliberately made
  of characters that do not sound alike over voice chat.
- A crash, a restart, or an alt-tab resumes the same session and the same code.

Press **Shift+F8** for the panel: the code, connection state, player count, a copy
button, and a QR code of the map link. Opening it copies the link for you, so
the button is there for when you have copied something else since. The key is
rebindable in the config, and the Shift requirement can be turned off there too.
F8 with Shift was picked because neither half is a stock Valheim bind.

The QR is generated inside the game — the code never goes to a QR service, for
the same reason it rides in the link's `#fragment`: anyone holding it can watch
everyone in the session move. Worth remembering before leaving the panel open on
stream, since a camera reads the square rather faster than a person reads the
code.

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

The copy button gives you `https://bobmitch.com/valheim?seed=YOURSEED#YOURCODE`.
The part after the `#` is never sent to the web server, so your code stays out of
its logs — but it is still in the link, so anyone you send the link to can watch
your session.

Your world's seed is in the link too, before the `#`, which does mean the map's
server sees it. That is on purpose: it is what lets the map draw your world's
terrain before it has connected to anything. A seed names a world, not a player —
it is the same string anyone can read on your world-select screen, it grants no
access to your session, and it is not tied to your account.

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

Traffic to the default relay is encrypted (`wss://`). If you point `RelayUrl` at
a `ws://` address, your position travels in the clear — fine for a relay on your
own machine, not fine over the internet.

## Configuration

Everything is defaulted; a fresh install needs no edits.

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Master switch |
| `RelayUrl` | `wss://valheimrelay.bobmitch.com/ws` | Relay address. Leave it alone unless you run your own |
| `MapUrl` | `https://bobmitch.com/valheim` | The map the copy button links to. Clear it to copy the bare code |
| `AnnounceInChat` | `true` | Print the code in chat when a session starts (local only) |
| `ShareMyPosition` | `true` | Broadcast your position |
| `ShareHealth` | `true` | Include health |
| `AcceptMapMarkers` | `true` | Let the map place pins on your minimap |
| `PositionInterval` | `1.0` | Seconds between updates, minimum 0.5 |
| `ToggleKey` | `F8` | Shows the panel, held with Shift |
| `ToggleRequiresShift` | `true` | Require Shift with `ToggleKey`. Off means a bare keypress |

## Troubleshooting

**The code changed and my browser stopped working.** The player who created the
session left, and the session was rebuilt under a new code. The mod tells you in
chat when this happens; paste the new code into the map.

**"This session is full."** Sixteen players is the limit. The panel has a retry
button for when someone leaves.

**Nothing happens after a game update.** Check `BepInEx/LogOutput.log` for a line
from ValheimRelay. The mod is built to stay dormant and say so rather than break
your game, so a game update disables it rather than crashing you.
