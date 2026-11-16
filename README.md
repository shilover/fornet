# 4net（fornet）

[English](README.en.md)

给 UE4SS Lua 模组用的联机底座，不绑定任何游戏。"4net" 读作 "for net"，和 .NET 没有关系。

它从一个已经在玩家手里跑的游戏联机模组里拆出来：网络、房间、可靠通道、同意闸门、mod 加载这些和具体游戏无关的部分放在这里；
"这款游戏怎么生成对手、怎么放招、怎么扣血"由每个游戏自己的**适配层**来写，玩法（1v1、观战、多人……）写成**玩法 mod**。

> **状态：预览。** 目前只有一款游戏在用它，接口还会变。代码注释大多是中文开发记录。

## 怎么运作

全部跑在**游戏进程里**，没有额外的后台程序：

```
 游戏进程
 ┌──────────────────────────────────────────────────────────────┐
 │ UE4SS Lua                                                    │
 │   Scripts\main.lua = 4net 宿主                               │
 │     ├─ games\<游戏>\   适配层：调游戏对象（生成替身、放招……）    │
 │     ├─ mods\<名>\      玩法 mod：每拍打包 / 应用状态包、对局规则 │
 │     └─ ext\<名>\       插件（沙箱）：面板项、状态包扩展段         │
 │              │ 命名管道（连不上退回 net\ 下的文件）              │
 │   fornet_lobby.dll   联机程序（C# NativeAOT，一个后台线程）     │
 │     局域网发现 / 大厅服务器 / 打洞 / 全互连 / 可靠事件 / 签名      │
 │     └─ lobby_<名>.dll  插件的原生部分（C 接口，比如另一种传输）   │
 └──────────────│───────────────────────────────────────────────┘
                │ UDP（或经插件的桥接传输）
     对方玩家的游戏进程（同样一套）      大厅服务器（Node，可选，自己架）
```

1. **启动**：UE4SS 执行 `Scripts\main.lua`（4net 宿主 `fornet_host.lua`）。宿主找到适配层 `games\<名>\`（`games\games.txt` 第一行，或唯一的那个），
   设好全局 `FORNET`（模组目录、版本、服务 / 事件、主循环调度），执行适配层入口；再按依赖顺序装 `mods\` 下有 `on.txt` 的玩法 mod、`ext\` 下有 `on.txt` 的插件。
2. **联机程序**：基础玩法 mod 构造 `fornet_ui` 时用 `package.loadlib` 载入 `fornet_lobby.dll`、调一次 `fornet_lobby_start`，它在游戏进程里起一个后台线程。
   Lua 和它之间是一根命名管道：Lua 发命令（开房、加入、断开……），收状态（房间列表、连没连上、延迟）。
3. **每一拍**（玩法 mod 用 `FORNET.loop.paced` 挂的循环，大约 30 次 / 秒）：玩法 mod 让适配层读出自己角色的状态，拼成一个 `k=v` 文本状态包，
   经管道交给联机程序；联机程序发给对方（直连 UDP；打不通时经大厅服务器中转，或经插件的桥接传输）；对方收到后经管道交给它那边的玩法 mod，
   由适配层驱动"替身"（代表你的那个角色）照着动。
4. **可靠事件**：出招结果、聊天、对局开始 / 结束这类不能丢的，走可靠事件通道（`fornet_net` 的 REL）：按序号确认、重发、去重、按序交付；
   设了联机密码时每个包都带签名。
5. **同意闸门**：每一种会把数据发到别处的功能都有一个同意键，没同意 4net 直接不发——不管请求来自哪个 mod 或插件；联机程序那头也核对同一份同意记录。

## 四层：放在哪、写什么

```
玩法 mod   mods\<名>\    多个、可插拔：基础玩法 + 往上加的玩法（观战、多人……）
适配层     games\<名>\   一个：这款游戏的 SDK（生成替身、放招、读写血量、关输入、传送）
插件       ext\<名>\     多个、沙箱：只扩展网络能力（比如另一种传输），碰不到游戏
4net       Scripts\ + fornet_lobby.dll   一个：只有网络，没有界面
```

依赖只能往下：4net 不知道有哪些玩法，适配层不知道有哪些 mod。**对外发数据只能经 4net。**

| 层 | 目录 | 你要写的 | 详细 |
|---|---|---|---|
| 适配层 | `<模组目录>\games\<游戏名>\` | `manifest.txt`（`name=`、`entry=`、`needsFornet=`）+ 入口 Lua：把这款游戏的 SDK 当服务 `game` 提供出去。全权限，**不写玩法** | [使用说明第 4 节](docs/USAGE.md#4-适配层games名)、[适配层钩子](docs/FORNET_ADAPTER.md) |
| 玩法 mod | `<模组目录>\mods\<名>\` | `manifest.txt`（`name=`、`version=`、`game=`、`needs=`……）+ `on.txt`（启用）+ 入口 Lua，拿到句柄 `M`：服务 / 事件 / 网络频道 / 对局占用 / 键位。第一个通常是**基础玩法 mod**：构造 `fornet_ui / hud / match`、画联机面板、设 `FORNET.adapter` 接口表、跑每拍的打包 / 应用 | [使用说明第 5 节](docs/USAGE.md#5-玩法-modmods名) |
| 插件 | `<模组目录>\ext\<名>\` | `manifest.txt`（`name=`、`version=`、`api=2`、`lobby=`、`consent=`）+ `on.txt` + `init.lua`（沙箱，只有 `API` 表）+ 可选的 `lobby_<名>.dll`（C 接口 `fnx_init / fnx_cmd / fnx_tick / fnx_shutdown`） | [使用说明第 6 节](docs/USAGE.md#6-插件ext名)、[插件 API](docs/EXTENSIONS_API.md)、完整例子 [`samples/hello/`](samples/hello/) |
| 4net | `<模组目录>\Scripts\` + `fornet_lobby.dll` | 不用写：拷 `lua\` 和编出来的 DLL | [使用说明第 2、3 节](docs/USAGE.md#2-编译) |

适配层（`games\mygame\main.lua`）：

```lua
local sdk = require("mygame_sdk")          -- 本目录的模块：宿主把适配层目录放在 package.path 最前
FORNET.provide("game", { name = "mygame", spawnPuppet = sdk.spawn, castMove = sdk.cast, readSelf = sdk.read })
FORNET.log("适配层 mygame 已提供服务 game")
```

玩法 mod（`mods\duel\main.lua`；`manifest.txt` 里 `needs=` 写它依赖的基础玩法 mod）：

```lua
local M = ...                                  -- 宿主给的句柄
local game = M.use("game")                     -- 适配层的 SDK
M.net.on("hit", function(move) game.castMove(move) end)     -- 可靠事件（字符串），线上自动加 "duel." 前缀
M.keys.bind("F1", function()                   -- 键位：同一个键第二个来抢的被拒
    if M.activity.acquire("match") then M.net.send("start", tostring(os.time())) end     -- 同一时间只能一个玩法驱动替身
end, "开始对局")
```

插件（`ext\hello\init.lua`，跑在沙箱里，碰不到游戏）：

```lua
return function(API)
    API.consent.register{ id = "share", title = "分享问候", titleEn = "Share a greeting",
                          zh = "开启后会把一句问候放进状态包发给对手。", en = "Puts a greeting into the status packet." }
    API.status.addField("g", function() return API.consent.has("share") and "hi" or nil end)   -- 状态包多一段 x.hello.g=
    API.lobby.onReply(function(kv) if kv.g then API.log.out("对方问候：%s", kv.g) end end)
    return { version = "0.1.0" }
end
```

`fornet_ui / hud / match`（联机面板、屏幕文字、回合制对局框架）不放 `Scripts\`：它们是给基础玩法 mod 用的"套件"，放进 `mods\<基础包>\`，由它构造。

## 目录

| 部分 | 目录 | 是什么 |
|---|---|---|
| 宿主 + 网络层 | `lua/` | `fornet_host.lua`（部署成模组的 `Scripts\main.lua`）+ `fornet_core / log / net / ext / mods`：命名管道、可靠事件、插件沙箱、玩法 mod 的加载 / 依赖 / 服务 / 事件 / 频道 / 对局占用 / 键位 |
| 基础玩法套件 | `lua/` | `fornet_ui / hud / match` |
| 联机程序 | `lobby/` | C#：局域网发现、服务器大厅、UDP 打洞、全互连多人、可靠事件、桥接传输。编成 NativeAOT 的 `fornet_lobby.dll` |
| 大厅服务器 | `server/` | Node.js，一个 UDP 端口：房间列表、打洞介绍、可选中转。只用 Node 自带模块，可以自己架 |
| 示例插件 | `samples/hello/` | 最小的插件：Lua + 一个 C 写的联机程序插件 |
| 测试 | `tests/` | `python -X utf8 -I tests/run_all.py`：编基底、跑 8 套离线测试 |
| 文档 | `docs/` | [使用说明](docs/USAGE.md)（编译、安装、三层怎么写、服务器）、[适配层钩子](docs/FORNET_ADAPTER.md)、[插件 API](docs/EXTENSIONS_API.md) |

## 协议名字

默认的线上名字是 4net 的中性名：发给大厅服务器的控制包 `4N|…`，玩家之间 `4NHELLO` / `4NEV` / …，联机程序插件的 C 接口前缀 `fnx_`，
测试用环境变量前缀 `FORNET_`。已经有玩家在用另一套名字的游戏，可以在自己的 C# 里把这些设回原来的（`lobby/WireTag.cs`），
服务端用 `--wire-server / --wire-peer` 或改文件里标着 `@wire-defaults` 的那一行——线上格式就一个字节都不变。见[使用说明](docs/USAGE.md#协议名字)。

## 许可证

GPL-3.0（见 `LICENSE`）；`server/` 是 AGPL-3.0（见 `server/LICENSE`）——改了服务器拿去给别人用，要把改过的源码给用户。

非商业、开源，和任何游戏开发商、发行平台都没有关系。架大厅服务器的人是服务运营者，自己负责当地合规。
权利人如有异议，作者会下架或修改。
