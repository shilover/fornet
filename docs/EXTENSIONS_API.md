# 4net 扩展包接口（API 2）

> 本页是给写扩展的人看的。Lua 侧（init.lua）+ 联机程序插件（`lobby_<名>.dll`，C ABI）。
> 基底不审核、不收录任何扩展包；扩展包由其作者负责。

## 目录

```
<模组目录>\ext\<名字>\      名字：[A-Za-z0-9_-]{1,24}，和 manifest 的 name 一致
  manifest.txt             k=v 一行一个：name / version / title / author / needsBase（最低基底版本）/ api（最低 API 号，现在 2）/ lobby（插件 DLL 文件名，默认 lobby_<名>.dll）/ consent（插件发包要的同意键，逗号分开；`@<键>` = 基底自己的键）
  init.lua                 返回 function(API) … end；可再返回 { version = "…" }
  on.txt                   存在 = 启用；没有 = 停用（2026-11-13 起扩展包默认停用）。玩家在插件页点"启用"写它、"停用"删它，都是重启游戏后生效。
                           旧版的 off.txt 不再看；发布包里不要带 on.txt
<模组目录>\ext\ext.txt     可选：一行一个名字，固定加载顺序 / 没有 popen 的环境用；没有它就 dir 列目录
```

示例：`samples/hello/`。

## 沙箱

`init.lua` 用 `load(src, …, "t", env)` 跑，`env` 里只有：`assert error ipairs next pairs pcall select tonumber tostring type unpack xpcall`、
`string / table / math / utf8` 的**副本**、`os = { time, clock, date }`、`API`。
没有 `require / dofile / io / os.remove / rawget / setmetatable / getmetatable / _G（指向自己）`，也拿不到基底的任何内部表。
扩展包**不能碰游戏对象**。需要游戏数据的功能请提需求给基底。

## API

| 调用 | 作用 | 约束 |
|---|---|---|
| `API.name / version / baseVersion / apiVersion` | 自己的名字 / 版本、基底版本、API 号 | 只读 |
| `API.log.out(fmt, …)` | 写 `mp_probe.log`，前缀 `[ext:<名>]` | |
| `API.T(zh, en)` | 按玩家语言取文案 | |
| `API.consent.register{ id, title, titleEn, zh, en, ver }` | 登记一个同意键（实际键名 `ext_<名>_<id>`），文案进隐私说明页 | **一切对外发数据的行为都要有对应的键**；`zh` / `en` 是字符串或字符串数组；文案不写作者 / 不写死地址 |
| `API.consent.has(id)` / `API.consent.ask(id, fn)` | 查 / 请基底弹确认（同意过直接执行 fn） | 扩展不能自己画窗 |
| `API.consent.registerBase(key, { ver, zh, en, title, titleEn })` | 给基底**固定**的键登记文案：只认 `bridge_match` / `bridge_friends`（提供桥接 DLL 的扩展包用，例如桥接平台） | 基底自己不写任何具体服务的名字；文案进隐私说明、排在 server 后面 |
| `API.panel.addRows(pageId, fn(items, T, lb))` | 往基底页加行：`"main"`（联机首页底部）、`"settings"`（系统设置） | 只能加不能改；`items[#items+1] = { label=, act=, col= }` |
| `API.panel.addPage{ id, title, titleEn, build(items, T, lb) }` | 自己的页，入口在"已加载的扩展"页里 | 返回页 id `ext:<名>[:id]` |
| `API.room.addSetting{ key, zh, en, default, consent }` | 房间设置（X1 只登记，X2+ 进房间设置页 / 状态包） | 带 `consent` 的没同意不能开 |
| `API.status.addField(key, fn)` | 状态包加 `x.<名>.<key>=<fn>`；fn 返回 nil 不发 | 值不能有换行；对方没装就忽略 |
| `API.lobby.cmd(kv)` | 发给自己的联机程序插件 | 先查本包**全部**登记的同意键，一个没同意就拒；没登记任何键也拒；X1 只记日志 |
| `API.lobby.onReply(fn(kv))` | 收联机程序回话里 `x.<名>.*` 的键值 | |
| `API.net.transport` | 这局的传输路：`"udp"` / `"bridge"`（经扩展包的桥接 DLL，例如桥接平台） | 只读；第三方可以据此选在桥接房里不发自己的字段 |
| `API.native.load("<名>.dll")` | 把**本包目录里**的一个原生 DLL 载进游戏进程（`package.loadlib(path, "*")`，只 LoadLibrary） | 文件名只能是 `[A-Za-z0-9_-]+.dll`、只认本包目录；装一个扩展包 = 同意运行它的代码，不再弹窗。桥接 DLL 就是这样进来的 |

## 协议

- 对方没装同名扩展：`x.<名>.*` 字段被忽略，不报错。
- 基底 / 扩展各自有版本，握手各自比（X2+ 加 `ext=` 列表）。
- 扩展包发出的任何数据都要经玩家同意；基底在 `lobby.cmd` 处强制，`status.addField` 由扩展自己在 `fn` 里按 `consent.has` 判断（示例见 hello）。

## 联机程序插件（`lobby_<名>.dll`，API 2）

基底的联机程序（FornetLobby.exe / fornet_lobby.dll，`Ext.cs`）启动时扫 `<模组目录>\ext\<名>\`，manifest 里有 `lobby=`（或目录里有 `lobby_<名>.dll`）就 `LoadLibrary`，没有 `on.txt`（没启用）的跳过。
插件是普通的原生 DLL（C / C++ / NativeAOT 都行，示例 `samples/hello/lobby_hello.c`；第一方的排位插件 `fornet/lobby/ranked/` 是 NativeAOT），导出四个 cdecl 函数：

```
int  fnx_init(const FnxCtx* ctx)   // 0 = 成功；别的值 = 不装（写日志）
void fnx_cmd(const char* kv)        // 基底 / Lua 发来的命令：UTF-8 文本，一行一个 k=v
void fnx_tick(void)                 // 主循环每轮一次（约 10 Hz）
void fnx_shutdown(void)
```

`FnxCtx`（只追加、不改序）：
```
int32 version;   // 现在 2
int32 id;        // 回调时带回去
void        (*log)(int id, const char* utf8);                                  // 写 net\lobby 日志，前缀 [ext:<名>]
int         (*consent)(int id, const char* key);                               // 玩家同意过没：ext_<名>_<key>；"@<键>" 查基底自己的键
int         (*send)(int id, const char* addr, const uint8_t* buf, int len);     // UDP 发到 "host:port"（基底解析）；本包 manifest 登记的同意键一个没同意、或"仅本地模式"→ 返回 -2，不发
int         (*recv)(int id, char* addrOut, int cap, uint8_t* buf, int bufCap);  // 收自己 socket 上的包（非阻塞；0 = 没有）；addrOut = "ip:port"
void        (*reply)(int id, const char* k, const char* v);                    // 回话：面板 / Lua 在 lobby.txt 看到 x.<名>.<k>=<v>（≤600 字）；k 以 _ 开头 = 只给基底自己，不进 lobby.txt
const char* (*name)(int id);                                                   // 自己的包名
int         (*resolve)(int id, const char* addr, char* out, int cap);          // API 2："域名或IP[:端口]" → "ip:port"（返回长度；0 认不出；<0 出错）。插件自己不做 DNS
```

规矩：
- 插件只能经 `send` 发包。`send` 查同意和"仅本地模式"，插件拿不到基底的 socket / 会话 / 配置；没在 manifest 登记任何同意键的包一个字节都发不出去。
- Lua 侧用 `API.lobby.cmd{ k = v }` 发命令（经基底，同样先查同意），`API.lobby.onReply(fn)` 收 `x.<名>.*`。基底自己也会向第一方插件发命令（排位：`poll= / stat= / req=`，见 `fornet/lobby/ranked/RankedPlugin.cs` 文件头）。
- 插件崩了会把联机程序一起带走（同一进程）；`fnx_init` 里先写日志，出事能从 `net\lobby_errors.log` 看到是哪个包。
- 版本：插件在 `fnx_init` 里检查 `ctx->version >= 自己要的号`；基底升 API 只追加字段，老插件照跑。

## 桥接传输（提供外部联机服务的扩展包，API 2）

基底的联机程序带一个通用的"桥接传输"（`Bridge.cs`）：它只认游戏进程里一根命名管道 `\\.\pipe\fornet_bridge_<游戏进程号>`（前缀是 `WireTag.Pipe`，游戏可以改），协议见游戏那边桥接 DLL 源码的文件头
（帧 = 1 字节类型 + 4 字节长度 + 内容；H → h 握手、L / U 监听、C / A / K 连接、S 发、F / N 联系人、P / R 在线状态、事件 s / d / p）。
一个扩展包要提供某个平台的联机，就：① 写一个实现这根管道的 DLL，握手 `h` 里报 `svc=<显示名>`（基底文案"对局走 <显示名>"、"<显示名> 好友"用它）；
② `init.lua` 里 `API.native.load("<那个>.dll")`；③ `API.consent.registerBase("bridge_match", …)` / `("bridge_friends", …)` 登记这个平台的同意文案。
第一方的例子是 `ext\桥接平台\`。基底源码里不出现任何具体服务的实现或名字。
