# 4net 使用说明

[English](USAGE.en.md)

写给"想用 4net 给某款 UE4SS 游戏做联机"的人。宿主版本 `0.4.0`。

> 现在仓库里**没有现成的示例游戏**（适配层 + 基础玩法 mod）：唯一在用的那款游戏的适配层不在这个仓库。
> 下面写的是接口和约定；第一次接新游戏时，建议先只做"两边都看到对方的替身在动"，再往上加对局。

## 1. 需要什么

| 用途 | 需要 |
|---|---|
| 运行 | Windows 版游戏 + [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS)（Lua 模组） |
| 编联机程序（exe，测试用） | Windows 自带的 .NET Framework 4 `csc.exe`（不用另装） |
| 编联机程序（游戏里用的 `fornet_lobby.dll`） | .NET 8 SDK + MSVC 生成工具（`link.exe`、Windows SDK） |
| 大厅服务器 | Node.js 18 以上，不用 `npm install` |
| 跑测试 | Python 3 + [lupa](https://pypi.org/project/lupa/)（没有 lupa 就跳过两套 Lua 测试）、Node |
| 编示例插件 | LLVM（`clang`） |

## 2. 编译

```bat
lobby\build.bat base              rem → lobby\FornetLobby-base.exe（带窗口的 exe，测试 / 调试用）
lobby\aot\build_aot.bat           rem → lobby\aot\out\release\fornet_lobby.dll（装进游戏的就是它）
python -X utf8 -I tests\run_all.py
```

`build_aot.bat` 用 `FORNET_DOTNET` 指定 `dotnet.exe`，不设就用 PATH 里的 `dotnet`。

**游戏名、进程名、版本号、协议名字、开局前备份存档**：0.4.0 起写在适配层的 `manifest.txt` 里（第 4 节），联机程序启动时读，**直接用编好的 `fornet_lobby.dll` 就行，不用自己编**。

**游戏自己的 C# 代码**（本地 AI 对手这类）才要自己编、不放进基底：把 `.cs` 的绝对路径用分号连起来设进环境变量 `FORNET_GAME_CS`，
`build.bat` 和 AOT 工程都会把它们编进同一个程序集。在里面实现 partial 方法：

- `GameInfo.Configure`：`ModName / TitleZh / TitleEn / GameZh / GameEn / ProcessName / AppVersion / BackupSave`（编译时给的写法，0.4.0 前只有这种；和 manifest 都给时编进去的优先）
- `LocalAiSupport.Register(ref ILocalAiImpl)`：本地 AI（不实现就没有本地 AI）
- `WireTag.Configure`：协议名字（见下）

接口定义在 `lobby/GameInfo.cs`、`lobby/LocalAiApi.cs`、`lobby/WireTag.cs`。

`build.bat` 的输出名用环境变量 `FORNET_EXE` 改（不带 `.exe`，默认 `FornetLobby`）。

### 协议名字

线上的包里有几个名字：大厅服务器控制包的标记（`4N|REG|…`）、对端控制包的前缀（`4NHELLO`、`4NEV`、转发头 `4NR|` …）、
房间标记哈希的盐、测试用环境变量的前缀（`FORNET_`）、联机程序插件导出名的前缀（`fnx_`）、桥接管道名的前缀（`fornet_bridge_`）。
默认都是 4net 的中性名，在 `lobby/WireTag.cs`。**新游戏用默认值就行。**0.4.0 起也可以写在适配层 manifest 的 `wire_*` 键里（第 4 节），不用编。

已经有玩家在用另一套名字的游戏（比如先有了自己的联机模组、后来才换到 4net），在 `WireTag.Configure` 里把这几样设回原来的，
线上格式就一个字节都不变，已装的旧版本和已经在跑的服务器照旧能连：

```csharp
static partial class WireTag
{
    static partial void Configure { S = "XYZS"; P = "XYZ"; Salt = "xyzmp"; EnvPrefix = "XYZ_"; Abi = "xyzx_"; Pipe = "xyz_bridge_"; }
}
```

大厅服务器要用同一套（第 8 节）；Lua 这边只有退路 exe 的文件名一样东西，基础玩法 mod 构造 `fornet_ui` 时给 `ctx.LOBBY_EXE`。
联机程序插件按游戏的前缀找导出函数，找不到再找 `fnx_`，所以照本文档写的插件在改过名字的游戏里也能用。

## 3. 装进游戏

```
<游戏>\...\ue4ss\Mods\<你的模组>\
  enabled.txt                 UE4SS 靠它加载这个模组（空文件）
  Scripts\main.lua            ← lua\fornet_host.lua（UE4SS 只认这个文件名）
  Scripts\fornet_core.lua     ← lua\ 里的 core / log / net / ext / mods
  ...
  fornet_lobby.dll            ← lobby\aot\out\release\fornet_lobby.dll
  games\<游戏名>\             适配层（第 4 节），只装一个；有多个时 games\games.txt 第一行写用哪个
  mods\<名>\                  玩法 mod（第 5 节），目录里有 on.txt 才加载；mods\mods.txt 可以固定扫描顺序
  ext\<名>\                   插件（第 6 节），目录里有 on.txt 才加载
  net\  logs\  rec\           运行时自己建
```

`fornet_ui / hud / match` 不放 `Scripts\`：它们是基础玩法 mod 的一部分，放进 `mods\<基础包>\`，由它 `M.require` 构造。

宿主出错只往 UE4SS 控制台打一行 `[fornet] …`，不会抛出、不影响游戏。

## 4. 适配层（`games\<名>\`）

一个安装只有一个。它是这款游戏的 SDK：生成 / 收起替身、放招、读写血量和属性、关 / 开输入、传送、受击钩子、临时改动的记账和还原。
**不写任何玩法。**全权限（普通 Lua 环境）。

`manifest.txt`（`key=value`，一行一个）：

| 键 | 必填 | 说明 |
|---|---|---|
| `name` | 是 | 和目录名一致 |
| `entry` | 否 | 入口，默认 `main.lua`，只能是本目录下的文件 |
| `needsFornet` | 否 | 宿主版本下限，比如 `0.3.0` |
| `title` | 否 | 日志里显示的名字 |

**联机程序读的键（0.4.0）**：同一个 `manifest.txt` 里还可以写下面这些，联机程序（`fornet_lobby.dll`）启动时读，选哪个适配层和宿主一样（`games\games.txt` 第一行，没有就按目录名排第一个）。都可选，不写就是默认值：

| 键 | 默认 | 说明 |
|---|---|---|
| `mod_name` | `fornet` | 本地名字前缀：单实例锁、和游戏之间的管道、UPnP 映射描述、本地文件头。一般写模组目录名 |
| `process` | 不写 = 只认本进程 | 游戏进程名（`.exe` 可写可不写）：退路 exe 看游戏还开着没有、找桥接管道 |
| `game_zh` / `game_en` | `这个游戏` / `this game` | 提示文字里的游戏名 |
| `title_zh` / `title_en` | `4net 联机` / `4net online` | 联机窗口标题（只有退路 exe 有窗口） |
| `app_version` | `0.0.0` | 模组版本号，握手时发给对方，两边不一样就提示；和玩法 mod 的版本保持一致 |
| `ranked_zh` / `ranked_long_zh` / `ranked_mode_zh` | `排位` / `排位` / `排位（经大厅服务器）` | 提示文字里排位方式的叫法 |
| `wire_server` / `wire_peer` / `wire_salt` / `wire_env` / `wire_abi` / `wire_pipe` | `4N` / `4N` / `fornet` / `FORNET_` / `fnx_` / `fornet_bridge_` | 协议名字（见第 2 节"协议名字"）。新游戏不写 |
| `save_dir` | 不写 = 不备份 | 存档目录：相对模组目录或绝对路径，可以带 `%环境变量%`。存档直接在里面，或者每个账号一个子目录 |
| `save_file` | 不写 = 整个目录 | 存档文件名；写了才会"存档和上一个备份一样就不再备份" |
| `save_backup_dir` | `fornet-save-backups` | 备份放在 `save_dir` 下哪个目录 |

备份规则：每局开始前把每个账号目录打成 `<账号>-backup-<时间>-mp.zip`；只清自己打的 `-mp.zip`（最近 20 个全留，14 天内每天留最后一个，更早的删），
别的工具打的备份不碰。

核对写对没有：`FornetLobby-base.exe --print-game 输出.txt`（exe 放在模组目录里跑）把读到的值写成 `键=值`；加 `--backup-now` 顺便备份一次、结果也写进去。

宿主执行入口前设好全局 `FORNET`：

| 字段 | 含义 |
|---|---|
| `VERSION` | 宿主版本 |
| `ROOT` | 模组目录（`net\` `logs\` 和联机程序 DLL 都在这） |
| `GAME` / `GAME_DIR` | 适配层名字 / 目录 |
| `log(fmt, ...)` / `verCmp(a, b)` | 控制台日志 / 版本比较 |
| `adopt(name, mk)` / `mods` | 网络层模块的构造函数包一层，实例登记进 `mods`（同一个模块只构造一次） |
| `loop.paced(sec, tick, alive, idle)` / `loop.frame(fn)` / `loop.async(ms, fn)` | 主循环调度 |
| `provide(name, tbl)` / `use(name)` / `on(ev, fn)` / `emit(ev, ...)` / `ask(ev, ...)` | 和玩法 mod 同一套服务 / 事件（第 5 节） |

适配层一般只做一件事：把这款游戏的 SDK 作为服务 `game` 提供出去，玩法 mod 用 `M.use("game")` 拿：

```lua
-- games\mygame\main.lua
local sdk = require("mygame_sdk")          -- 本目录的模块：宿主把适配层目录排在 package.path 最前
FORNET.provide("game", { name = "mygame", new = sdk.new, dir = FORNET.GAME_DIR })
FORNET.log("适配层 mygame：服务 game 已提供")
```

装载顺序：宿主 → 适配层入口 → 按依赖排好的玩法 mod。全部装完后宿主检查接口表 `FORNET.adapter`
（`name / version / tick / makePacket / applyPacket / spawnPuppet / removePuppet / matchDeps / onMapChange`），缺项只记一行日志；
这张表通常由基础玩法 mod 设，因为每拍发什么包、怎么驱动替身是玩法决定的。
每个网络层模块要哪些钩子（ctx）见 [FORNET_ADAPTER.md](FORNET_ADAPTER.md)；那份写于宿主 0.2.0，"适件一个装下全部玩法"的结构已经被这里的四层取代，但模块和 ctx 的部分仍然有效。

## 5. 玩法 mod（`mods\<名>\`）

可以有很多个。通常先有一个**基础玩法 mod**：构造 `fornet_ui / hud / match`，画联机面板（连接方式、房间列表、同意确认页），做第一个玩法；
其他玩法依赖它、往它的面板上加页、订阅它的对局事件。也可以做和它平级、只依赖 4net + 适配层的独立玩法。

`manifest.txt`：

```
name=spec                 必填，和目录名一致
version=1.0.0             必填
title=观战 / titleEn=Spectate
game=<适配层名>           必填；对不上不加载；真不碰游戏的写 game=*
needsFornet=0.3.0         宿主版本下限
needs=base>=1.0.0         依赖（逗号分隔，可带 >= 版本）；缺了 / 版本低 / 没启用 → 不加载，面板上写原因
wants=base                软依赖：装了就排在它后面，没装也能加载
entry=main.lua
order=50                  同一层里的先后（只为稳定顺序，不能代替 needs）
net=1                     要在对局里和对方交换数据（握手交换名单，对方没装就不发）
lobby=lobby_xxx.dll       可选：联机程序插件（和插件同一套 C 接口）
consent=@键               可选：同意键
```

入口拿到句柄 `local M = ...`：

| 组 | 函数 |
|---|---|
| 身份 | `M.name / version / dir / manifest / log(fmt, ...)` |
| 服务 | `M.provide(name, tbl)` / `M.use(name)`：同名服务只能有一个提供者 |
| 事件 | `M.on(ev, fn)` / `M.emit(ev, ...)` / `M.ask(ev, ...)`：进程内、同步，每个处理函数 pcall 包着；事件名带提供者前缀 |
| 网络 | `M.net.send(kind, data)` / `M.net.on(kind, fn)`：可靠事件，线上自动加 `<mod>.` 前缀 |
|  | `M.net.field(key, fn)` / `M.net.onField(key, fn)`：状态包扩展段 `x.<mod>.<key>=` |
|  | `M.net.peerHas(mod)`：对方装没装这个 mod（握手还没到时是 nil） |
| 对局占用 | `M.activity.acquire(kind)` / `release(kind)` / `owner(kind)`：同一时间只能一个玩法驱动替身 / 关输入 |
| 键位 | `M.keys.bind(key, fn, label, mods)`：同一个键第二个来抢的被拒 |
| 模块 | `M.require(name)`：载本 mod 目录里的模块，不进全局 `package.path` |

mod 是全权限的，但**对外发数据仍只能经 4net**（可靠事件 / 状态包 / 联机程序插件），同意闸门不因此例外。

## 6. 插件（`ext\<名>\`）

只扩展网络能力（比如一种新的传输、一个平台的好友列表），碰不到游戏对象，跑在沙箱里。
API 见 [EXTENSIONS_API.md](EXTENSIONS_API.md)，完整例子是 `samples\hello\`（`samples\build_hello.bat` 编它的 C 插件）。
插件的同意项只在它自己的插件页里问；启用插件 = 同意它的全部条款，停用 = 一起撤回，都要重启游戏生效。

## 7. 同意闸门

每一种会把数据发到别处的功能都有一个同意键。没同意时 4net 直接不发——不管请求来自哪个 mod 或插件。
4net 只记"哪些同意在等"，确认页由基础玩法 mod 画；没装基础玩法 mod = 谁也同意不了 = 什么都发不出去。

## 8. 大厅服务器

```bash
node server/fornet-lobby-server.mjs                     # 默认 UDP 27020，IPv4 + IPv6 双栈
node server/fornet-lobby-server.mjs --port 27020 --host 0.0.0.0 --quiet
node server/fornet-lobby-server.mjs --relay             # 打洞不通时替双方转发（默认关，几乎是服务器全部的带宽）
node server/fornet-lobby-server.mjs --upstream other.example.org:27020   # 和别的服务器互通房间列表
```

不存盘，重启后房间靠主机的心跳自己补回来。完整参数和协议见 [server/README.zh.md](../server/README.zh.md) 和服务端文件开头的注释。
客户端**不预设任何服务器地址**：玩家自己填。
改过协议名字的游戏：启动时给 `--wire-server` / `--wire-peer`（再加 `--wire-alias 4N` 就也认中性名的客户端），
或者在自己的发布脚本里把服务端文件中标着 `@wire-defaults` 的那一行换成自己的默认值。

## 9. 测试

`python -X utf8 -I tests\run_all.py [lupa 所在目录]` 先编基底 exe，再跑：宿主、插件框架、协议名、联机程序命令、同意闸门、历史记录、
联机程序插件、服务器大厅（会起本机的服务器和两个联机程序，用几个固定端口；别和正在跑的游戏同时跑）。

## 10. 许可证

GPL-3.0；`server/` 是 AGPL-3.0。适配层和玩法 mod 在游戏里和 4net 一起加载、互相调用，按 GPL 的一般理解应当以兼容的许可证发布。
