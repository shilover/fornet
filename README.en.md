# 4net (fornet)

[中文](README.md)

A game-agnostic online-play foundation for UE4SS Lua mods. "4net" reads "for net"; it is not a .NET library.

It was extracted from a game multiplayer mod that is already in players' hands. Everything that does not depend on a particular game lives
here: networking, rooms, a reliable channel, a consent gate, mod loading. How *that* game spawns an opponent, plays a move or deals damage
is written by each game's own **adapter**; game modes (1v1, spectating, multiplayer, …) are written as **play mods**.

> **Status: preview.** One game uses it so far, and the interfaces will still change. Code comments are mostly Chinese development notes.

## How it works

Everything runs **inside the game process**; there is no separate background program:

```
 game process
 ┌──────────────────────────────────────────────────────────────┐
 │ UE4SS Lua                                                    │
 │   Scripts\main.lua = the 4net host                           │
 │     ├─ games\<game>\  adapter: touches game objects            │
 │     ├─ mods\<name>\   play mods: pack / apply state, match rules │
 │     └─ ext\<name>\    extensions (sandboxed): panel rows, fields  │
 │              │ named pipe (falls back to files under net\)     │
 │   fornet_lobby.dll   lobby helper (C# NativeAOT, one thread)   │
 │     LAN discovery / lobby server / hole punching / full mesh /  │
 │     reliable events / packet signing                            │
 │     └─ lobby_<name>.dll  native part of an extension (C ABI)    │
 └──────────────│───────────────────────────────────────────────┘
                │ UDP (or an extension's bridge transport)
   the other players' game processes (same stack)    lobby server (Node, optional, self-hosted)
```

1. **Startup**: UE4SS runs `Scripts\main.lua` (the 4net host, `fornet_host.lua`). The host finds the adapter `games\<name>\` (first line of
   `games\games.txt`, or the only one), sets up the global `FORNET` (mod folder, version, services / events, main-loop scheduling) and runs the
   adapter's entry; then it loads the play mods under `mods\` and the extensions under `ext\` that have an `on.txt`, in dependency order.
2. **Lobby helper**: when the base play mod builds `fornet_ui`, it loads `fornet_lobby.dll` with `package.loadlib` and calls
   `fornet_lobby_start` once; that starts a background thread inside the game process. Lua talks to it over a named pipe: Lua sends commands
   (host, join, leave, …) and reads status (room list, connected or not, latency).
3. **Every tick** (a loop the play mod hooks with `FORNET.loop.paced`, about 30 times a second): the play mod asks the adapter for its own
   character's state, packs it into a `k=v` text packet and hands it to the lobby helper over the pipe; the helper sends it to the peer (direct
   UDP; if hole punching fails, through the lobby server's relay or an extension's bridge transport); on the other side the packet goes over the
   pipe to that player's play mod, which has the adapter drive the "puppet" (the character standing in for you) accordingly.
4. **Reliable events**: things that must not be lost (move results, chat, match start / end) go through the reliable event channel
   (`fornet_net`'s REL): numbered, acknowledged, resent, de-duplicated, delivered in order; with an online password every packet is signed.
5. **Consent gate**: every feature that sends data anywhere has a consent key; without consent 4net simply does not send, whichever mod or
   extension asks. The lobby helper checks the same consent record.

## The four layers: where they go, what you write

```
play mods   mods\<name>\    many, pluggable: a base online pack + modes stacked on it (spectate, multiplayer, ...)
adapter     games\<name>\   one: this game's SDK (spawn puppets, play moves, read/write health, block input, teleport)
extensions  ext\<name>\     many, sandboxed: only extend networking (e.g. another transport); cannot touch the game
4net        Scripts\ + fornet_lobby.dll   one: networking only, no UI
```

Dependencies only point down: 4net does not know which modes exist; the adapter does not know which mods exist.
**Data only leaves through 4net.**

| Layer | Folder | What you write | Details |
|---|---|---|---|
| Adapter | `<mod folder>\games\<game>\` | `manifest.txt` (`name=`, `entry=`, `needsFornet=`) + an entry Lua that offers the game's SDK as the service `game`. Full access, **no game modes in here** | [Usage guide, section 4](docs/USAGE.en.md#4-the-adapter-gamesname), [adapter hooks](docs/FORNET_ADAPTER.md) (Chinese) |
| Play mod | `<mod folder>\mods\<name>\` | `manifest.txt` (`name=`, `version=`, `game=`, `needs=`, …) + `on.txt` (enabled) + an entry Lua that receives a handle `M`: services / events / network channel / activity lock / key binding. The first one is usually the **base play mod**: it builds `fornet_ui / hud / match`, draws the online panel, sets the `FORNET.adapter` interface table and runs the per-tick pack / apply | [Usage guide, section 5](docs/USAGE.en.md#5-play-mods-modsname) |
| Extension | `<mod folder>\ext\<name>\` | `manifest.txt` (`name=`, `version=`, `api=2`, `lobby=`, `consent=`) + `on.txt` + `init.lua` (sandboxed, only the `API` table) + an optional `lobby_<name>.dll` (C ABI `fnx_init / fnx_cmd / fnx_tick / fnx_shutdown`) | [Usage guide, section 6](docs/USAGE.en.md#6-extensions-extname), [extension API](docs/EXTENSIONS_API.md) (Chinese), full example [`samples/hello/`](samples/hello/) |
| 4net | `<mod folder>\Scripts\` + `fornet_lobby.dll` | Nothing: copy `lua\` and the built DLL | [Usage guide, sections 2–3](docs/USAGE.en.md#2-build) |

Adapter (`games\mygame\main.lua`):

```lua
local sdk = require("mygame_sdk")          -- a module in this folder: the host puts the adapter folder first on package.path
FORNET.provide("game", { name = "mygame", spawnPuppet = sdk.spawn, castMove = sdk.cast, readSelf = sdk.read })
FORNET.log("adapter mygame: service game provided")
```

Play mod (`mods\duel\main.lua`; its `manifest.txt` lists the base play mod under `needs=`):

```lua
local M = ...                                  -- the handle the host passes in
local game = M.use("game")                     -- the adapter's SDK
M.net.on("hit", function(move) game.castMove(move) end)     -- reliable event (a string); on the wire it gets the "duel." prefix
M.keys.bind("F1", function()                   -- key binding: a second mod grabbing the same key is refused
    if M.activity.acquire("match") then M.net.send("start", tostring(os.time())) end     -- only one mode drives the puppet at a time
end, "Start match")
```

Extension (`ext\hello\init.lua`, sandboxed, cannot touch the game):

```lua
return function(API)
    API.consent.register{ id = "share", title = "分享问候", titleEn = "Share a greeting",
                          zh = "开启后会把一句问候放进状态包发给对手。", en = "Puts a greeting into the status packet." }
    API.status.addField("g", function() return API.consent.has("share") and "hi" or nil end)   -- adds x.hello.g= to the state packet
    API.lobby.onReply(function(kv) if kv.g then API.log.out("peer says: %s", kv.g) end end)
    return { version = "0.1.0" }
end
```

`fornet_ui / hud / match` (online panel, on-screen text, round-based match framework) do not go into `Scripts\`: they are a kit for the base
play mod, live in `mods\<base pack>\` and are built by it.

## What is in here

| Part | Folder | What it is |
|---|---|---|
| Host + network layer | `lua/` | `fornet_host.lua` (deployed as the mod's `Scripts\main.lua`) + `fornet_core / log / net / ext / mods`: named pipe, reliable events, extension sandbox, play-mod loading / dependencies / services / events / channels / activity lock / key binding |
| Base play kit | `lua/` | `fornet_ui / hud / match` |
| Lobby helper | `lobby/` | C#: LAN discovery, lobby-server rooms, UDP hole punching, full-mesh multiplayer, reliable events, bridge transports. Built as NativeAOT `fornet_lobby.dll` |
| Lobby server | `server/` | Node.js on one UDP port: room list, hole-punch introductions, optional relay. Built-in modules only; self-hostable |
| Sample extension | `samples/hello/` | A minimal extension: Lua + a lobby plugin written in C |
| Tests | `tests/` | `python -X utf8 -I tests/run_all.py`: builds the base and runs 8 offline suites |
| Docs | `docs/` | [Usage guide](docs/USAGE.en.md) (build, install, how to write each layer, server), [adapter hooks](docs/FORNET_ADAPTER.md), [extension API](docs/EXTENSIONS_API.md) (both Chinese) |

## Wire names

By default the names on the wire are 4net's neutral ones: `4N|…` control packets to the lobby server, `4NHELLO` / `4NEV` / … between players,
the lobby plugin C ABI prefix `fnx_`, test environment variables prefixed `FORNET_`. A game whose players already use another set can set them
back in its own C# (`lobby/WireTag.cs`) and start the server with `--wire-server / --wire-peer` (or replace the line marked `@wire-defaults`),
and the wire format stays byte-for-byte the same. See the [usage guide](docs/USAGE.en.md#wire-names).

## License

GPL-3.0 (see `LICENSE`); `server/` is AGPL-3.0 (see `server/LICENSE`): if you run a modified server for others, offer them its source.

Non-commercial and open source. Not affiliated with any game developer or platform. Whoever runs a lobby server is the service operator
and responsible for local compliance. If a rights holder objects, the author will take it down or change it.
