# 4net (fornet)

[中文](README.md)

A game-agnostic online-play foundation for UE4SS Lua mods. "4net" reads "for net"; it is not a .NET library.

It was extracted from a game multiplayer mod that is already in players' hands. Everything that does not depend on a particular game lives
here: networking, rooms, a reliable channel, a consent gate, mod loading. How *that* game spawns an opponent, plays a move or deals damage
is written by each game's own **adapter**.

> **Status: preview.** One game uses it so far, and the interfaces will still change. Code comments are mostly Chinese development notes;
> section numbers in them (§xxx) refer to the original project's private handover log, which is not published.

## What is in here

| Part | Folder | What it is |
|---|---|---|
| Host + network layer | `lua/` | `fornet_host.lua` (deployed as the mod's `Scripts\main.lua`) + `fornet_core / log / net / ext / mods`: files / named pipe, reliable events, extension sandbox, play-mod loading / dependencies / services / events / channels / activity lock / key binding |
| UI / match base | `lua/` | `fornet_ui / hud / match`: the online panel, on-screen text, a round-based match framework. Not part of the network layer: your "base play mod" constructs them (see the usage guide) |
| Lobby helper | `lobby/` | C#: LAN discovery, lobby-server rooms, UDP hole punching, full-mesh multiplayer, reliable events, bridge transports. Built as NativeAOT `fornet_lobby.dll`, running inside the game process |
| Lobby server | `server/` | Node.js on one UDP port: room list, hole-punch introductions, optional relay. Built-in modules only; self-hostable |
| Sample extension | `samples/hello/` | A minimal extension: Lua + a lobby plugin written in C |
| Tests | `tests/` | `python -X utf8 -I tests/run_all.py`: builds the base and runs 8 offline suites |

## Four layers

```
play mods   mods\<name>\    many, pluggable: a base online pack + modes stacked on it (spectate, multiplayer, ...)
adapter     games\<name>\   one: this game's SDK (spawn puppets, play moves, read/write health, block input, teleport)
extensions  ext\<name>\     many, sandboxed: only extend networking (e.g. another transport); cannot touch the game
4net        Scripts\ + fornet_lobby.dll   one: networking only, no UI
```

Dependencies only point down. 4net does not know which modes exist; the adapter does not know which mods exist.
**Data only leaves through 4net, and nothing is sent without the player's consent.**

## Start here

- **Usage guide**: [docs/USAGE.en.md](docs/USAGE.en.md) (build, install, write an adapter / play mod / extension, host a server)
- Hooks an adapter gives the network modules: [docs/FORNET_ADAPTER.md](docs/FORNET_ADAPTER.md) (Chinese)
- Extension API: [docs/EXTENSIONS_API.md](docs/EXTENSIONS_API.md) (Chinese)
- Lobby server: [server/README.md](server/README.md) (Chinese)

## Wire protocol

Control packets use the `4N` family (`4N|…` to the lobby server; `4NHELLO` / `4NEV` / … between players); the lobby plugin C ABI is `fnx_*`.
It deliberately does not interoperate with the game mod it came from, whose installed players keep their own protocol names.

## License

GPL-3.0 (see `LICENSE`); `server/` is AGPL-3.0 (see `server/LICENSE`): if you run a modified server for others, offer them its source.

Non-commercial and open source. Not affiliated with any game developer or platform. Whoever runs a lobby server is the service operator
and responsible for local compliance. If a rights holder objects, the author will take it down or change it.
