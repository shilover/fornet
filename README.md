# 4net（fornet）

[English](README.en.md)

给 UE4SS Lua 模组用的联机底座，不绑定任何游戏。"4net" 读作 "for net"，和 .NET 没有关系。

它从一个已经在玩家手里跑的游戏联机模组里拆出来：网络、房间、可靠通道、同意闸门、mod 加载这些和具体游戏无关的部分放在这里；
"这款游戏怎么生成对手、怎么放招、怎么扣血"由每个游戏自己的**适配层**来写。

> **状态：预览。** 目前只有一款游戏在用它，接口还会变。代码注释大多是中文开发记录，里面提到的章节号（§xxx）指原项目的私有交接文档，这里看不到。

## 有什么

| 部分 | 目录 | 是什么 |
|---|---|---|
| 宿主 + 网络层 | `lua/` | `fornet_host.lua`（部署成模组的 `Scripts\main.lua`）+ `fornet_core / log / net / ext / mods`：文件 / 命名管道、可靠事件、插件沙箱、玩法 mod 的加载 / 依赖 / 服务 / 事件 / 频道 / 对局占用 / 键位 |
| 界面 / 对局底座 | `lua/` | `fornet_ui / hud / match`：联机面板、屏幕文字、回合制对局框架。它们不归网络层管，由你的"基础玩法 mod"构造（见使用说明） |
| 联机程序 | `lobby/` | C#：局域网发现、服务器大厅、UDP 打洞、全互连多人、可靠事件、桥接传输。编成 NativeAOT 的 `fornet_lobby.dll`，在游戏进程里跑 |
| 大厅服务器 | `server/` | Node.js，一个 UDP 端口：房间列表、打洞介绍、可选中转。只用 Node 自带模块，可以自己架 |
| 示例插件 | `samples/hello/` | 最小的插件：Lua + 一个 C 写的联机程序插件 |
| 测试 | `tests/` | `python -X utf8 -I tests/run_all.py`：编基底、跑 8 套离线测试 |

## 四层

```
玩法 mod   mods\<名>\    多个、可插拔：基础联机包 + 往上加的玩法（观战、多人……）
适配层     games\<名>\   一个：这款游戏的 SDK（生成替身、放招、读写血量、关输入、传送）
插件       ext\<名>\     多个、沙箱：只扩展网络能力（比如另一种传输），碰不到游戏
4net       Scripts\ + fornet_lobby.dll   一个：只有网络，没有界面
```

依赖只能往下。4net 不知道有哪些玩法，适配层不知道有哪些 mod。**对外发数据只能经 4net，没同意就发不出去。**

## 开始

- **使用说明**：[docs/USAGE.md](docs/USAGE.md)（怎么编、怎么装、怎么写适配层 / 玩法 mod / 插件、怎么架服务器）
- 适配层要给网络层模块的钩子：[docs/FORNET_ADAPTER.md](docs/FORNET_ADAPTER.md)
- 插件 API：[docs/EXTENSIONS_API.md](docs/EXTENSIONS_API.md)
- 大厅服务器：[server/README.md](server/README.md)

## 协议

控制包用 `4N` 一族（发给大厅服务器的是 `4N|…`，玩家之间是 `4NHELLO` / `4NEV` / …），联机程序插件的 C 接口前缀是 `fnx_`。
它故意不和原来那个游戏模组互通：那边的玩家已经装好了，保留自己的协议名字。

## 许可证

GPL-3.0（见 `LICENSE`）；`server/` 是 AGPL-3.0（见 `server/LICENSE`）——改了服务器拿去给别人用，要把改过的源码给用户。

非商业、开源，和任何游戏开发商、发行平台都没有关系。架大厅服务器的人是服务运营者，自己负责当地合规。
权利人如有异议，作者会下架或修改。
