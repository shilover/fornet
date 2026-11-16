# 4net usage guide

[中文](USAGE.md)

For people who want to add online play to a UE4SS game with 4net. Host version `0.3.0`.

> There is **no ready-made sample game** (adapter + base play mod) in this repository yet: the one game that uses 4net keeps its adapter
> elsewhere. This guide describes the interfaces and conventions. For a new game, first aim for "both players see each other's puppet move",
> then add matches on top.

## 1. Requirements

| For | You need |
|---|---|
| Running | The Windows version of the game + [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) (Lua mods) |
| Building the lobby helper exe (tests) | `csc.exe` from .NET Framework 4, which ships with Windows |
| Building `fornet_lobby.dll` (what the game loads) | .NET 8 SDK + MSVC build tools (`link.exe`, Windows SDK) |
| Lobby server | Node.js 18+, no `npm install` |
| Tests | Python 3 + [lupa](https://pypi.org/project/lupa/) (without lupa the two Lua suites are skipped), Node |
| Sample extension | LLVM (`clang`) |

## 2. Build

```bat
lobby\build.bat base              rem -> lobby\FornetLobby-base.exe (windowed exe, for tests / debugging)
lobby\aot\build_aot.bat           rem -> lobby\aot\out\release\fornet_lobby.dll (this is what goes into the game)
python -X utf8 -I tests\run_all.py
```

`build_aot.bat` takes `dotnet.exe` from `FORNET_DOTNET`, or `dotnet` on PATH.

**Your game's own C#** (a local AI opponent, the game's name, backing up the save before a match, …) stays out of the base: set
`FORNET_GAME_CS` to a `;`-separated list of absolute `.cs` paths and both `build.bat` and the AOT project compile them into the same assembly.
Implement the partial methods there:

- `GameInfo.Configure`: `ModName / TitleZh / TitleEn / GameZh / GameEn / ProcessName / BackupSave`
- `LocalAiSupport.Register(ref ILocalAiImpl)`: local AI (leave it out and there is none)
- `WireTag.Configure`: wire names (below)

The interfaces are in `lobby/GameInfo.cs`, `lobby/LocalAiApi.cs` and `lobby/WireTag.cs`.

`build.bat` names its output from `FORNET_EXE` (without `.exe`, default `FornetLobby`).

### Wire names

A few names travel inside the packets: the lobby server's control tag (`4N|REG|…`), the peer control prefix (`4NHELLO`, `4NEV`, the
forward heads `4NR|` …), the salt of the room-tag hash, the prefix of test environment variables (`FORNET_`), the export prefix of lobby
plugins (`fnx_`) and the bridge pipe prefix (`fornet_bridge_`). They default to 4net's neutral names in `lobby/WireTag.cs`.
**A new game just keeps the defaults.**

A game whose players already use another set of names (say it had its own online mod before moving onto 4net) sets them back in
`WireTag.Configure`; the wire format then stays byte-for-byte the same, so installed old versions and running servers keep working:

```csharp
static partial class WireTag
{
    static partial void Configure { S = "XYZS"; P = "XYZ"; Salt = "xyzmp"; EnvPrefix = "XYZ_"; Abi = "xyzx_"; Pipe = "xyz_bridge_"; }
}
```

The lobby server must use the same set (section 8). On the Lua side the only name is the fallback exe's file name: the base play mod
passes `ctx.LOBBY_EXE` when it builds `fornet_ui`. The lobby looks up a plugin's exports with the game's prefix first and `fnx_` second,
so plugins written against this guide also load in a game that renamed things.

## 3. Install into the game

```
<game>\...\ue4ss\Mods\<YourMod>\
  enabled.txt                 UE4SS loads the mod because of this (empty file)
  Scripts\main.lua            <- lua\fornet_host.lua (UE4SS only looks for this file name)
  Scripts\fornet_core.lua     <- core / log / net / ext / mods from lua\
  ...
  fornet_lobby.dll            <- lobby\aot\out\release\fornet_lobby.dll
  games\<game>\               the adapter (section 4), exactly one; with several, the first line of games\games.txt picks one
  mods\<name>\                play mods (section 5), loaded only if the folder has on.txt; mods\mods.txt can pin the scan order
  ext\<name>\                 extensions (section 6), loaded only if the folder has on.txt
  net\  logs\  rec\           created at run time
```

`fornet_ui / hud / match` do not go into `Scripts\`: they belong to the base play mod. Put them in `mods\<base>\` and construct them with `M.require`.

When something goes wrong the host prints one `[fornet] …` line to the UE4SS console; it never throws and never takes the game down.

## 4. The adapter (`games\<name>\`)

Exactly one per install. It is this game's SDK: spawn / remove puppets, play moves, read and write health and attributes, block / unblock
input, teleport, hit hooks, bookkeeping and rollback of temporary changes. **No game modes in here.** It runs with full permissions (plain Lua).

`manifest.txt` (`key=value`, one per line):

| Key | Required | Meaning |
|---|---|---|
| `name` | yes | same as the folder name |
| `entry` | no | entry file, default `main.lua`, must be in this folder |
| `needsFornet` | no | minimum host version, e.g. `0.3.0` |
| `title` | no | name shown in the log |

Before running the entry the host sets the global `FORNET`:

| Field | Meaning |
|---|---|
| `VERSION` | host version |
| `ROOT` | the mod folder (`net\`, `logs\` and the lobby DLL live here) |
| `GAME` / `GAME_DIR` | adapter name / folder |
| `log(fmt, ...)` / `verCmp(a, b)` | console log / version compare |
| `adopt(name, mk)` / `mods` | wrap a network module constructor; the instance is registered in `mods` (constructed once) |
| `loop.paced(sec, tick, alive, idle)` / `loop.frame(fn)` / `loop.async(ms, fn)` | main loop scheduling |
| `provide(name, tbl)` / `use(name)` / `on(ev, fn)` / `emit(ev, ...)` / `ask(ev, ...)` | the same services / events the play mods use (section 5) |

Usually the adapter does one thing: offer this game's SDK as the service `game`, which play mods get with `M.use("game")`:

```lua
-- games\mygame\main.lua
local sdk = require("mygame_sdk")          -- a module in this folder: the host puts the adapter folder first on package.path
FORNET.provide("game", { name = "mygame", new = sdk.new, dir = FORNET.GAME_DIR })
FORNET.log("adapter mygame: service game provided")
```

Load order: host → adapter entry → play mods in dependency order. After everything is loaded the host checks the interface table
`FORNET.adapter` (`name / version / tick / makePacket / applyPacket / spawnPuppet / removePuppet / matchDeps / onMapChange`) and logs any
missing entries. The base play mod normally sets this table, because what to send each tick and how to drive a puppet are mode decisions.
The hooks (ctx) each network module needs are in [FORNET_ADAPTER.md](FORNET_ADAPTER.md) (Chinese). It was written for host 0.2.0, when one
adapter held every mode; that structure is replaced by the four layers here, but the module / ctx part still applies.

## 5. Play mods (`mods\<name>\`)

As many as you like. Usually there is first a **base play mod**: it constructs `fornet_ui / hud / match`, draws the online panel (connection
modes, room list, consent pages) and provides the first mode. Other modes depend on it, add pages to its panel and subscribe to its match
events. A mode can also sit beside it, depending only on 4net + the adapter.

`manifest.txt`:

```
name=spec                 required, same as the folder name
version=1.0.0             required
title=… / titleEn=Spectate
game=<adapter name>       required; not loaded if it does not match; game=* if it really never touches the game
needsFornet=0.3.0         minimum host version
needs=base>=1.0.0         dependencies (comma-separated, optional >= version); missing / too old / disabled -> not loaded, reason shown in the panel
wants=base                soft dependency: if present, load after it; loads fine without it
entry=main.lua
order=50                  order within one dependency level (only for a stable order; not a substitute for needs)
net=1                     exchanges data with the other player in a match (names are swapped in a handshake; nothing is sent if they lack it)
lobby=lobby_xxx.dll       optional: lobby plugin (same C ABI as extensions)
consent=@key              optional: consent key
```

The entry receives a handle, `local M = ...`:

| Group | Functions |
|---|---|
| Identity | `M.name / version / dir / manifest / log(fmt, ...)` |
| Services | `M.provide(name, tbl)` / `M.use(name)`: one provider per service name |
| Events | `M.on(ev, fn)` / `M.emit(ev, ...)` / `M.ask(ev, ...)`: in-process, synchronous, each handler wrapped in pcall; event names carry the provider's prefix |
| Network | `M.net.send(kind, data)` / `M.net.on(kind, fn)`: reliable events, prefixed `<mod>.` on the wire |
|  | `M.net.field(key, fn)` / `M.net.onField(key, fn)`: state-packet extension field `x.<mod>.<key>=` |
|  | `M.net.peerHas(mod)`: whether the other player has this mod (nil until the handshake arrives) |
| Activity lock | `M.activity.acquire(kind)` / `release(kind)` / `owner(kind)`: only one mode drives puppets / blocks input at a time |
| Keys | `M.keys.bind(key, fn, label, mods)`: a second mod grabbing the same key is refused |
| Modules | `M.require(name)`: loads a module from this mod's folder, outside the global `package.path` |

Mods have full permissions, but **data still only leaves through 4net** (reliable events / state packets / lobby plugins); the consent gate
has no exception for that.

## 6. Extensions (`ext\<name>\`)

Extensions only extend networking (a new transport, a platform's friend list, …), cannot reach game objects and run in a sandbox.
The API is in [EXTENSIONS_API.md](EXTENSIONS_API.md) (Chinese); `samples\hello\` is a complete example (`samples\build_hello.bat` builds its C plugin).
An extension's consent items are asked only on its own page; enabling it = agreeing to all of its terms, disabling it withdraws them;
both take effect after a game restart.

## 7. The consent gate

Every feature that sends data somewhere has a consent key. Without consent 4net simply does not send, whichever mod or extension asked.
4net only records which consents are pending; the base play mod draws the confirmation page. No base play mod = nobody can agree =
nothing can be sent.

## 8. Lobby server

```bash
node server/fornet-lobby-server.mjs                     # UDP 27020 by default, IPv4 + IPv6 dual stack
node server/fornet-lobby-server.mjs --port 27020 --host 0.0.0.0 --quiet
node server/fornet-lobby-server.mjs --relay             # relay when hole punching fails (off by default; almost all of a server's bandwidth)
node server/fornet-lobby-server.mjs --upstream other.example.org:27020   # share room lists with another server
```

Nothing is written to disk; after a restart rooms come back with the hosts' heartbeats. All options and the protocol are in
[server/README.md](../server/README.md) (Chinese) and the comment at the top of the server file.
The client **ships with no server address**: players enter one themselves.
A game with its own wire names starts the server with `--wire-server` / `--wire-peer` (add `--wire-alias 4N` to also accept neutral
clients), or has its release script replace the line marked `@wire-defaults` in the server file with its own defaults.

## 9. Tests

`python -X utf8 -I tests\run_all.py [lupa dir]` builds the base exe, then runs: host, extension framework, protocol names, lobby commands,
consent gate, history, lobby plugins, lobby server (it starts a local server and two lobby helpers on a few fixed ports; do not run it
while the game is running).

## 10. License

GPL-3.0; `server/` is AGPL-3.0. Adapters and play mods are loaded together with 4net inside the game and call into each other, so under the
usual reading of the GPL they should be released under a compatible license.
