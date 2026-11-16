# 4net 游戏适件接口（F6，2026-11-13；宿主 0.2.0）

> **注意**：适配层只做游戏适配（游戏 SDK），玩法搬进 `mods\<名>\`（基础联机包 + 往上堆的 mod），宿主 0.3.0 加了 mod 机制（`fornet_mods.lua`）。

> 给"想用 4net 给另一款 UE4SS 游戏做联机"的人看。宿主的实现在 `lua/fornet_host.lua`。这份文档不点名任何具体游戏；一个具体适件和这里各项的对照由那个适件自己的仓库维护。
> 宿主负责：找到适件、交出目录、执行适件、登记网络核模块实例、跑主循环的调度、检查适件接口表。适件负责：给每个网络核模块它要的游戏钩子（ctx）、每拍做什么、接口表里那几个函数。

## 1. 三层，各管什么

| 层 | 在哪 | 权限 | 一个安装里几个 |
|---|---|---|---|
| 宿主 + 网络核 | `<模组>\Scripts\main.lua`（宿主）+ `Scripts\fornet_*.lua` + 联机程序 `fornet_lobby.dll` | 不碰游戏对象 | 1 |
| 游戏适件 | `<模组>\games\<名字>\` | **全权限**（要生成替身、读血量、放招） | 1（`games\games.txt` 选） |
| 扩展包 | `<模组>\ext\<名字>\` | 沙箱 + 受限 API（`docs/EXTENSIONS_API.md`），碰不到游戏对象 | 多个 |

适件是"这款游戏怎么联机"，扩展包是"在已有的联机上加功能"。作者只为宿主和自己写的适件负责；扩展包各自负责。

## 2. 目录

```
<UE4SS>\Mods\<模组名>\
  Scripts\main.lua          宿主（UE4SS 只认这个文件）
  Scripts\fornet_*.lua      网络核 / 面板 / HUD / 对局框架 / 扩展包框架
  fornet_lobby.dll          联机程序（NativeAOT；游戏适件的 C# 可以编进来，见第 5 节）
  games\games.txt           可选：第一行非注释的名字 = 用哪个适件；没有就按目录名排序取第一个
  games\<名字>\manifest.txt
  games\<名字>\main.lua     适件入口（manifest 的 entry 可以换名字）
  ext\<名字>\…              扩展包
  net\  rec\  logs\         运行时数据（模组目录下，不在适件目录里）
```

## 3. 宿主做什么 / 交给适件什么

1. 从自己的路径（`debug.getinfo`）或 UE4SS 放进 `package.path` 的 `Scripts\` 推出模组目录。
2. 列 `games\`（先读 `games.txt`，没有再 `dir /b /ad`），选一个，读 `manifest.txt`：

   | 键 | 必填 | 说明 |
   |---|---|---|
   | `name` | 是 | 和目录名一致，不一致就跳过 |
   | `entry` | 否 | 入口文件，默认 `main.lua`；只能是本目录下的文件名 |
   | `needsFornet` | 否 | 宿主版本下限，宿主比它旧就跳过 |
   | `title` | 否 | 日志里显示 |

3. `package.path` 前面加 `games\<名字>\?.lua;Scripts\?.lua;`——适件自己的模块优先，`require("fornet_core")` 等照常能找到。
4. 设全局 `FORNET`，然后 `loadfile` + `pcall` 执行入口；任何一步不对只往 UE4SS 控制台打一行 `[fornet] …`，不抛出、不影响游戏。

`FORNET` 表：

| 字段 | 含义 |
|---|---|
| `VERSION` | 宿主版本（现在 `0.2.0`） |
| `ROOT` | 模组目录（`net\` `ext\` `logs\` 和联机程序 DLL 都在这） |
| `GAME` / `GAME_DIR` | 适件名字 / 适件目录 |
| `SCRIPTS` | `Scripts\`（fornet 模块） |
| `manifest` | 读到的 manifest 表 |
| `log(fmt, ...)` | 往控制台打 `[fornet]` 行 |
| `verCmp(a, b)` | 版本比较（-1 / 0 / 1） |
| `adopt(name, mk)` | 包一层网络核模块的构造函数：构造一次、实例登记到 `mods[name]`（0.2.0） |
| `mods` | 已登记的网络核模块实例：`mods.fornet_match` 等（0.2.0） |
| `loop.paced(sec, tick, alive, idle)` | 主循环调度：每帧检查、按时钟累加到点跑 `tick`、落后超过 0.1 秒不补；`alive` 假时循环自己停；`idle` 每帧先调。退路：固定间隔 → 异步链。返回 `"frame"` / `"delay"` / `"async"` / nil（0.2.0） |
| `loop.frame(fn)` / `loop.async(ms, fn)` | 每帧（游戏线程）/ 异步定时；没有这个能力返回 false（0.2.0） |
| `adapter` | **适件设**：接口表（第 4 节），宿主执行完入口后检查、记一行 |
| `IFACE` | 接口表要的键名列表 |

适件应该：模组目录用 `FORNET.ROOT`；自己的数据文件用 `FORNET.GAME_DIR`。单独跑（没有宿主，比如测试）时自己补一个 `FORNET = { ROOT = …, GAME_DIR = …, standalone = true }`。

## 4. 接口表和网络核模块

### 4.1 接口表 `FORNET.adapter`（适件在入口里设）

| 键 | 是什么 |
|---|---|
| `name` / `version` | 适件名字 / 版本 |
| `tick` | 每拍做什么（宿主 `loop.paced` 按拍调） |
| `makePacket(p1, tn)` | 生成并发出我的状态包 |
| `applyPacket(p1, tn)` | 收对方的包、驱动替身 |
| `spawnPuppet` / `removePuppet` | 生成 / 收起替身 |
| `matchDeps` | 返回 `{ step =, follow = }`：对局框架回调游戏的钩子表（倒下、关输入、归位、无敌、清状态…），给 `fornet_match.hostStep / clientStep / followHost` |
| `onMapChange(...)` | 切图前：冻结循环、藏替身、丢旧引用 |

宿主缺哪项会在控制台写"接口表缺 …"，但照样跑——接口表是给扩展、工具和以后的第二个适件认的约定，不是开关。

### 4.2 网络核模块：适件给钩子，宿主登记

每个模块是 `require("fornet_xxx")` 返回的构造函数，`mk(ctx)` 装出实例。**ctx 里是这款游戏的钩子**（字体路径、怎么判断对象有效、怎么拿玩家控制器…），只有适件知道，所以模块由适件构造；
有宿主时经 `FORNET.adopt` 包一层，实例登记进 `FORNET.mods`，同一个模块只构造一次：

```lua
local ok, mk = pcall(require, "fornet_match")
if ok and FORNET and FORNET.adopt then mk = FORNET.adopt("fornet_match", mk) end
FMATCH = mk({ out = out, MATCH = MATCH, ... })
```

| 模块 | ctx 要什么 | 适件挂进去的点 |
|---|---|---|
| `fornet_log` | ROOT / LOG / TAG / keyPrints | `out(fmt, ...)` 写日志 |
| `fornet_core` | out / NET_DIR / F_LOCAL / valid / short / pathOf / zhFont | 文件 / 命名管道 / 中英文 `T` / 本地偏好 `PREF` |
| `fornet_net` | out / NET_DIR / fileWrite / fileReadKV | 可靠事件 `REL.on[<类型>] = fn`；延迟测量 `LAT` |
| `fornet_match` | 见文件头 | `stepDeps / followDeps`（= 接口表 matchDeps）、`on / fire`、`addRules`、`addRows`、`addWire`、`guard` |
| `fornet_hud` | out / valid / getP1PC / gstat / LANG / … | 屏幕文字底座 |
| `fornet_ui` | out / now / ROOT / NET_DIR / T / fileWrite / fileReadKV / PIPE / consent / … | `page` / `pagePrefixReg` / `addMode` / `statusPre` / `pollHooks` / `defaultItems` / `pingMs` |
| `fornet_ext` | out / ROOT / T / CONSENT / PREF / COL / readKV / writeFile / netuiCmd / transport | 扫 `ext\`、"已加载的扩展"页 |

### 4.3 适件直接用到的网络核字段

各模块返回的表（`NETUI.*`、`FMATCH.*`、`LAT.*` …）上的字段就是网络核给适件的 API。建议适件仓库从自己的代码生成一份"用到了哪些字段"的清单（分"读 / 调用"和"往里写的扩展点"），
并用测试卡住清单和代码一致：网络核改了清单里的名字 = 接口变更，适件用到新字段时先更新清单。

### 4.4 主循环

调度在宿主（`FORNET.loop.paced`，和老代码一字不差地搬过去）；每拍做什么、空帧里补发上一拍的包、看门狗诊断（卡在管道里没有）还是适件的。
看门狗重启主循环时给新的代号，旧循环靠 `alive` 返回假自己退出。没有宿主时（老布局 / 测试）适件走自己原来那份调度。

## 5. 联机程序里的游戏代码（C#）

联机程序基底不认识游戏。适件要往里加东西（本地 AI、游戏名、备份存档）就写 C#，编进同一个程序集：

- 环境变量 `FORNET_GAME_CS` = 分号分隔的 `.cs` 绝对路径；`lobby\build.bat` 和 `lobby\aot\` 工程都读它。
- **0.4.0 起游戏名 / 进程名 / 版本 / 协议名字 / 存档备份不用编**：写在适配 `manifest.txt`（键见 [USAGE.md 第 4 节](USAGE.md)），联机程序启动时读（`GameInfo.Load`），编好的 DLL 直接能用。下面编译的写法还在、两边都给时编进去的优先。
- 在自己的 .cs 里实现 partial 方法：`GameInfo.Configure`（`ModName / TitleZh / TitleEn / GameZh / GameEn / ProcessName / BackupSave`）、`LocalAiSupport.Register(ref ILocalAiImpl)`（本地 AI）。没实现就是基底默认值、没有本地 AI。

## 6. 版本

宿主 `0.2.0`（0.1.0 = F6 第一步：找适件 / 交目录；0.2.0 = 第二步：adopt / mods / loop / 接口表检查）。manifest 的 `needsFornet` 按它比；适件只用到第一步的东西就写 0.1.0，用到第二步的（adopt / loop）要么写 0.2.0，要么先判断 `FORNET.adopt` / `FORNET.loop` 在不在。
