-- fornet_ui.lua —— fornet（4net）UE4SS 网络核：面板底座第一块（F3b）。
-- 从游戏模组的 main.lua 原样搬出来的 net-ui 核心段：联机程序拉起（DLL / exe）、lobby.txt / cmd.txt 文件协议、连接方式 / 线路名、
-- 面板的节流读取、等回话（BUSY）。不碰任何游戏对象、不认识任何全局：全部经 ctx 给。
-- 用法：local UI = require("fornet_ui")({ out=, now=, ROOT=, NET_DIR=, INST=, T=, fileWrite=, fileReadKV=, PIPE=, consent= })
-- UI.NETUI / UI.netuiRead / UI.netuiCmd / UI.netuiEnsure / UI.netuiLaunch / UI.netuiStarting / UI.netuiMode /
-- UI.netModeName / UI.netRouteName / UI.clipText / UI.lobbyAlive / UI.NETUI_QUICK / UI.NETUI_PER_PAGE / UI.GUI_PER_PAGE
return function(ctx)
local out, now, ROOT, NET_DIR, INST, T = ctx.out, ctx.now, ctx.ROOT, ctx.NET_DIR, ctx.INST, ctx.T
local fileWrite, fileReadKV, PIPE = ctx.fileWrite, ctx.fileReadKV, ctx.PIPE
local consent = ctx.consent or function() return nil end
local LOBBY_EXE = ctx.LOBBY_EXE or "FornetLobby.exe"     -- 退路 exe 的文件名（DLL 载不进来时才用；游戏可以经 ctx 改成自己的名字）
local U = {}
-- 画面就是上面的 HUD（最多 9 行）。内容来自联机窗口每 0.5 秒写的 net/lobby.txt；操作写 net/cmd.txt（id 每次不同，窗口只执行一次）。
-- 联机窗口没开着（lobby.txt 的 alive 超过 3 秒没更新）时自动以后台模式启动它（<退路 exe> --bg）：
-- v0.18.5 起模组一加载就拉起，打开面板时发现它没响应再拉一次。
-- 自动拉起最多连续 NETUI_AUTO_TRIES 次，之后只在面板上按 End 手动重试。
-- 启动计时一律用 os.time：now 在玩家生成前是 os.time、之后换成游戏时间，两种时基相减会算出负数（永远"正在启动"）
local NETUI = { open = false, sel = 1, cmdN = 0, lobby = nil, readAt = 0.0, launchAt = nil, tries = 0, items = {} }
local NETUI_AUTO_TRIES = 3
-- 发出后要等联机程序回话的命令（copy / show / refresh 不等：copy 自己闪提示，另两个面板上看不出变化）
NETUI.BUSY_SEC = 5.0
NETUI.BUSY = {
    host = { "正在创建房间", "CREATING THE ROOM" }, ai = { "正在开始 AI 对战", "STARTING THE AI BATTLE" },
    join = { "正在加入", "JOINING" }, stop = { "正在断开", "DISCONNECTING" }, mode = { "正在切换连接方式", "SWITCHING" },
    spectate = { "正在进入观战", "JOINING AS SPECTATOR" }, rotate = { "正在换人", "ROTATING" }, tospec = { "正在下场", "STEPPING DOWN" },
    hostdown = { "正在下场", "STEPPING DOWN" }, hostup = { "正在回来", "COMING BACK" },
}
-- 11-18 用户："调 AI 难度每次等好久、有时失败"：ailevel / aiload 只是写设置，联机程序立即生效，
-- 不走"正在保存 + 等 ack 5 秒"那一套（等不到就报失败）；去掉这两个的忙状态
for _, c in ipairs({ "name", "port", "upnp", "relay", "server", "secret", "lang", "airoom", "airoommode" }) do
    NETUI.BUSY[c] = { "正在保存", "SAVING" }
end
local LOBBY_FILE, CMD_FILE = NET_DIR .. "/lobby.txt", NET_DIR .. "/cmd.txt"
-- v0.20.2：虚拟局域网并进局域网（局域网顺带找 Tailscale 节点、显示虚拟网卡地址；联机程序把旧的 vlan 当 lan）
-- v0.20.3：广域网并进服务器大厅（"输入 IP 直连…"；联机程序把旧的 wan 当 server）
local NET_MODES = { "server", "lan", "ai" }   -- 顺序同联机程序的 Modes.All（End 按这个顺序换）； 本地 AI 在局域网后面；平台大厅 v0.34 删；平台好友 v0.34 并进"最近"页，不再是独立方式
-- 排位（ranked）排在服务器大厅后面。只有报 modes= 的新版联机程序才有它，所以不进上面那个"旧版联机程序"的兜底列表；
-- 鼠标面板的标签页按这个全集建（联机程序没报的方式藏起，见 guiTick）
-- F4c-2：桥接传输（扩展包装进游戏的外部联机服务，例如桥接平台）的显示名由联机程序从桥接 DLL 握手拿到（lobby.txt bridgesvc=）；文案里写 {SVC}
-- 给玩家看的文字一律写"桥接"，不写服务名（桥接平台等）；
-- 真实服务名（lobby.txt bridgesvc=）只进日志和扩展包自己的插件页
function NETUI.bridgeSvc() return T("桥接", "Bridge") end
function NETUI.svc(s) return (tostring(s):gsub("{SVC}", NETUI.bridgeSvc())) end
-- 11-15 用户："桥接好友……能跟之前一样显示成一个单独分类么，桥接平台插件的就是桥接平台好友"：好友标签 / 好友页按插件报的服务名叫（bridgesvc=桥接平台 → 平台好友），
-- 插件没报名字时叫"桥接好友"。其余文案照 写"桥接"
function NETUI.friendsName()
    local svc = NETUI.lobby and NETUI.lobby.bridgesvc or ""
    local C = consent()
    if (svc == "" or svc == "Bridge") and C and C.extSvc then svc = C.extSvc() or "" end     -- 11-15 实测：服务器大厅模式下桥接按需才初始化，联机程序报的还是默认 Bridge；先用插件 manifest 的 svc=
    if svc == "" or svc == "Bridge" then return T("桥接好友", "BRIDGE FRIENDS") end
    return T(svc .. " 好友", svc:upper() .. " FRIENDS")
end
-- 好友标签能不能点：桥在、没在联机（联机中没有标签栏，和换连接方式一样）
function NETUI.friendsTab(lb) return NETUI.hasBridge() and ((lb or NETUI.lobby or {}).role or "none") == "none" end
NETUI.TABS = { "server", "lan", "friends", "ai" }     -- 11-15 friends = 桥接好友标签（不是连接方式，点了只换到 bfriends 页；装了桥接插件才显示，名字按插件报的服务名，如"平台好友"）；0.34：没有平台好友标签（mode=桥接平台只是内部传输，从"最近"页进）；F4b：排位等注册进来的方式经 U.addMode 插在 after 之后
-- F4b：方式注册表（名字 / 标签 / 底下走不走服务器大厅 / 没房间时的话 / 开房按钮 / 主页行 / 能不能当 AI 等人房间）。基底只有桥接平台 / server / lan / ai；
-- 排位等由 U.addMode 加进来（game_ranked.lua）
local MODE_NAME = { bridge = { "{SVC}好友", "{SVC} FRIENDS" }, server = { "服务器大厅", "LOBBY SERVER" }, ai = { "本地 AI", "LOCAL AI" }, lan = { "局域网", "LAN" } }
local VIA_SERVER = { server = true }
local NOROOMS = { bridge = { "（没有好友在开房）", "(NO FRIEND IS HOSTING)" }, server = { "（大厅里没有房间）", "(NO ROOMS ON THE SERVER)" } }
local GUI_TAB = { friends = { "{SVC}好友", "{SVC}" }, bridge = { "{SVC}好友", "{SVC}" }, lan = { "局域网", "LAN" }, server = { "服务器大厅", "SERVER" }, ai = { "本地 AI", "LOCAL AI" } }
local MODE_HOST_LABEL, MODE_ROWS, MODE_NO_AIROOM, MODE_INROOM = {}, {}, { ai = true }, {}
-- 实际能用的方式以联机程序报的 lobby.txt modes= 为准（发布版不带桥接平台方式）；旧版联机程序没这一项时用 NET_MODES
NETUI.modes = function()
    local s = NETUI.lobby and NETUI.lobby.modes or ""
    if s == "" then return NET_MODES end
    if NETUI.modesKey ~= s then
        local l = {}
        for m in s:gmatch("[^,]+") do l[#l + 1] = m end
        NETUI.modesKey, NETUI.modesList = s, (#l > 0 and l or NET_MODES)
    end
    return NETUI.modesList
end
NETUI.hasMode = function(m)
    for _, x in ipairs(NETUI.modes()) do if x == m then return true end end
    return false
end
-- 装了桥接扩展包、游戏里的桥在（联机程序报 bridgeav=1，或已经连上 bridge=ok）。0.34 起 modes= 不列 bridge，
-- 所以别拿 hasMode("bridge") 判断桥在不在（以前那三行桥接平台设置因此永远不显示）
NETUI.hasBridge = function()
    local lb = NETUI.lobby or {}
    return lb.bridgeav == "1" or lb.bridge == "ok" or NETUI.hasMode("bridge")
end
-- 排位（ranked）底下走的就是服务器大厅那一套：房间列表、建房、房间号、复制、线路都和 server 一样（只是没有 IP 直连）
NETUI.viaServer = function(m) return VIA_SERVER[m] == true end
-- v0.19.1：房间多了放到单独的"房间列表"页。
-- 以前主页最多列 3 个、联机程序最多给 8 个，多出来的根本看不到。现在：主页只放剪贴板 / 最近（快捷加入，最多 NETUI_QUICK 个）
-- 和一行"房间列表（N 个）…"；房间列表页每页 NETUI_PER_PAGE 个，在页首 / 页尾再往上 / 往下自动翻页
local NETUI_QUICK = 2
local NETUI_PER_PAGE = 6
local GUI_PER_PAGE = 8       -- v0.20.1 鼠标面板的房间列表页不受屏幕文字 12 行的限制，每页多放几个

local function netModeName(m)
    local n = MODE_NAME[m] or MODE_NAME.lan
    return NETUI.svc(T(n[1], n[2]))
end

-- 服务器大厅的线路（lobby.txt 的 route）：直连 / 中转 / 第几次直连
local function netRouteName(r)
    if r == "direct" then return T("直连", "DIRECT")
    elseif r == "relay" then return T("服务器中转", "RELAY")
    -- 排位的对局走桥接平台：桥接平台已连上 / 正在经桥接平台连 / 还在向大厅服务器报到
    elseif r == "bridge" then return NETUI.bridgeSvc()
    elseif r == "bridgewait" then return T("正在经" .. NETUI.bridgeSvc() .. "连接", "CONNECTING VIA " .. NETUI.bridgeSvc():upper())
    elseif r == "join" then return T("向大厅服务器报到", "JOINING VIA LOBBY") end
    local k = (r or ""):match("^punch(%d)$")
    if k then return T("直连第 " .. k .. "/3 次", "DIRECT TRY " .. k .. "/3") end
    return nil
end

-- 按字符截断（中文是多字节）：太长的行会出屏
local function clipText(s, n)
    s = tostring(s or "")
    local ok, len = pcall(utf8.len, s)
    if not ok or not len or len <= n then return s end
    return s:sub(1, utf8.offset(s, n + 1) - 1) .. ".."
end

local function lobbyAlive(lb)
    return lb ~= nil and (tonumber(lb.alive) or 0) > os.time() - 3
end

local function netuiRead(tn, force)
    -- v0.24.0：now 在玩家控制器就绪前退回 os.time（约 17 亿秒），之后才是游戏秒数；那时定下的 readAt 会让这里永远不再读文件
    -- （对手血条一直拿不到 peername）。下次读的时间比现在晚 5 秒以上 = 时间基准变了，立即重读
    if not force and tn < NETUI.readAt and NETUI.readAt - tn < 5 then return NETUI.lobby end
    NETUI.readAt = tn + (NETUI.busy and 0.05 or NETUI.open and 0.25 or 1.0)
    if NETUI.busy and PIPE then PIPE.blobAt = 0 end      -- 等命令回话：管道这次不按 BLOB_SEC 节流，马上问
    -- v0.18.6：联机程序每 0.5 秒整个替换一次 lobby.txt，正好碰上时读不到 → 以前面板会闪一下红字"没响应"。
    -- 读失败就沿用上一次的内容；程序真没了的话，上一次的 alive 时间戳 3 秒后自然过期
    local kv = fileReadKV(LOBBY_FILE)
    if kv or not NETUI.lobby then NETUI.lobby = kv end
    if U.onLobby then U.onLobby(NETUI.lobby) end
    -- 联机省流开 / 关和原因变了记一行（联机程序不进面板状态行，报在 lobby.txt dzwhy=）
    local why = kv and kv.dzwhy
    if why and why ~= "" and why ~= NETUI.dzWhy then
        NETUI.dzWhy = why
        out("联机省流：%s", ({ on = "开（只发变了的字段）", ["peer-old"] = "对方联机程序是旧版，发完整包",
                              ["host-off"] = "主机关了，发完整包", ["host-old"] = "主机联机程序是旧版，发完整包" })[why] or why)
    end
    return NETUI.lobby
end

local function netuiCmd(cmd, extra)
    -- 0.34 总原则：开房 / 加入 / AI 房等真人之前，先过一次性同意（CONSENT.gate 没过就翻到确认页，同意后原样重放这条命令）
    local CONSENT = consent()     -- F3b：同意框架由调用方给（main.lua 的全局 CONSENT）
    if CONSENT and CONSENT.gate and not CONSENT.gate(cmd, extra, function() netuiCmd(cmd, extra) end) then return end
    NETUI.cmdN = NETUI.cmdN + 1
    local id = string.format("%d-%d", os.time(), NETUI.cmdN)
    local ok = fileWrite(CMD_FILE, string.format("id=%s\ncmd=%s\n%s", id, cmd, extra or ""))
    out("联机面板 → 联机窗口：%s %s（id=%s，%s）", cmd, ((extra or ""):gsub("\n", " ")), id, ok and "已写" or "写失败")
    NETUI.readAt = 0.0
    -- 等联机程序回话（lobby.txt 的 ack = 这条命令的 id）：状态行显示"正在…"，按钮变灰点不了，5 秒没回话报错
    local w = ok and NETUI.BUSY[cmd]
    if w then
        if cmd == "host" and (NETUI.lobby or {}).mode == "ai" then w = NETUI.BUSY.ai end
        NETUI.busy = { id = id, cmd = cmd, zh = w[1], en = w[2], at = now() }
    end
end

-- 以后台模式启动联机窗口。v0.23.0 起 exe 叫 <退路 exe>（以前是中文名的 exe：os.execute 走 ANSI 代码页，中文名传不过去，
-- 只能让 cmd 展开 旧名字*.exe 通配符；那样新旧两个 exe 都在时会都被拉起来，所以改成英文名、按全名启动）
-- 先试 fornet_lobby.dll：联机程序编成原生 DLL、在游戏进程里跑（不再多一个后台进程）。package.loadlib 拿它导出的
-- fornet_lobby_start 调一次：它只起一个后台线程（已经在跑就什么也不做），不碰 lua_State。没有这个 DLL / 载不进来 /
-- 连着 2 次拉起后还没响应，退回启动 <退路 exe>（两者用同一个 net 目录的单实例锁，不会同时跑两份）
-- §390：联机程序没起来时，面板要写真正的原因。以前一律落到"退路 exe 不存在"（no PBZLobby.exe），
-- 可 0.34 起发布包根本不带那个 exe，最常见的原因（DLL 被 Windows 应用控制策略 / 杀软拦了）被彻底盖住，
-- 玩家和作者都看不出问题。这里把每一步的真实原因记进 NETUI.launchErr / launchWhy。
-- 系统给的是本机 ANSI（中文系统 = GBK）字节，画到屏幕上会乱码：认出来的给人话，认不出的只在日志里留原文。
local function isAppControl(s)
    if s:find("应用程序控制", 1, true) or s:find("Application Control", 1, true) or s:find("0x800711C7", 1, true) then return true end
    return s:find("\xD3\xA6\xD3\xC3\xB3\xCC\xD0\xF2\xBF\xD8\xD6\xC6", 1, true) ~= nil     -- GBK：应用程序控制
end
-- Windows 的 FormatMessage 结尾带 \r\n：以前会把这行日志和面板撑成两行
local function oneLine(s)
    return (tostring(s or ""):gsub("[\r\n]+$", ""):gsub("[\r\n]", " "))
end
local function setLaunchErr(why, raw)
    NETUI.launchWhy = why
    if why == "appcontrol" then NETUI.launchErr = T("被系统的应用控制策略拦住", "BLOCKED BY WINDOWS APP CONTROL")
    elseif why == "missing" then NETUI.launchErr = T("缺少 fornet_lobby.dll", "FORNET_LOBBY.DLL IS MISSING")
    elseif why == "noloadlib" then NETUI.launchErr = T("这个 UE4SS 载入不了 DLL", "THIS UE4SS CANNOT LOAD DLLS")
    elseif why == "silent" then NETUI.launchErr = T("拉起 2 次都没响应", "STARTED TWICE, NO REPLY")
    elseif raw and raw ~= "" and not raw:find("[\128-\255]") then NETUI.launchErr = raw     -- 英文系统给的 ASCII 原文：直接显示
    else NETUI.launchErr = T("载入 DLL 失败（详情看日志）", "DLL LOAD FAILED (SEE THE LOG)") end     -- 非 UTF-8：别画到屏幕上
end

local function netuiLaunchDll()
    if NETUI.dllOff then     -- 第二份也用 DLL（DLL 自己判断用 net2）；也让外面再拉时原因照旧（别退回"没有 PBZLobby.exe"）
        setLaunchErr(NETUI.dllOff, NETUI.dllRaw)
        return false
    end
    local dll = (ROOT .. "/fornet_lobby.dll"):gsub("/", "\\")
    local f = io.open(dll, "rb")
    if not f then
        NETUI.dllOff = "missing"
        setLaunchErr("missing")
        out("联机面板：模组目录里没有 fornet_lobby.dll（%s），改用 %s", dll, LOBBY_EXE)
        return false
    end
    f:close()
    if type(package) ~= "table" or type(package.loadlib) ~= "function" then
        NETUI.dllOff = "noloadlib"
        setLaunchErr("noloadlib")
        out("!! 联机面板：这个 UE4SS 的 Lua 没有 package.loadlib，改用 %s", LOBBY_EXE)
        return false
    end
    NETUI.dllTries = (NETUI.dllTries or 0) + 1
    if NETUI.dllTries > 2 then
        NETUI.dllOff = "silent"
        setLaunchErr("silent")
        out("!! 联机面板：fornet_lobby.dll 拉起 2 次都没响应，改用 %s（原因看 net/lobby_errors.log）", LOBBY_EXE)
        return false
    end
    local fn, err = package.loadlib(dll, "fornet_lobby_start")
    if type(fn) ~= "function" then
        local e = oneLine(err)
        NETUI.dllOff, NETUI.dllRaw = isAppControl(e) and "appcontrol" or "raw", e
        setLaunchErr(NETUI.dllOff, e)
        out("!! 联机面板：载入 %s 失败 %s，改用 %s", dll, e, LOBBY_EXE)
        return false
    end
    local ok, e = pcall(fn)
    out("联机面板：启动联机程序（游戏进程内）%s -> %s %s", dll, ok and "已执行" or "失败", tostring(e or ""))
    if not ok then
        NETUI.dllOff, NETUI.dllRaw = "raw", oneLine(e)
        setLaunchErr("raw", NETUI.dllRaw)
        return false
    end
    return true
end

local function netuiLaunch()
    NETUI.launchAt = os.time()
    NETUI.launchErr, NETUI.launchWhy = nil, nil
    if netuiLaunchDll() then return end
    if type(os) ~= "table" or type(os.execute) ~= "function" then
        NETUI.launchErr = "no os.execute"
        out("!! 联机面板：这个 UE4SS 的 Lua 没有 os.execute，请手动打开 %s", LOBBY_EXE)
        return
    end
    local dir = ROOT:gsub("/", "\\")
    -- 发布包 / deploy 早就不带 <退路 exe> 了；文件不在时 "cmd /c start" 会弹"找不到文件"框，os.execute 一直等 → 游戏线程卡死
    local fe = io.open(dir .. "\\" .. LOBBY_EXE, "rb")
    if not fe then
        -- 退路 exe（PBZLobby.exe）发布包早就不带了：它不在不是失败原因，真正的原因 netuiLaunchDll 已经记下
        if not NETUI.launchErr then setLaunchErr("raw", "no " .. LOBBY_EXE) end
        out("联机面板：没有 %s，不再退回启动它（真正原因：%s；重启游戏会重新载入 fornet_lobby.dll）", LOBBY_EXE, tostring(NETUI.launchErr))
        return
    end
    fe:close()
    local cmd = string.format('cmd /c start "" "%s\\%s" --bg', dir, LOBBY_EXE)
    if INST.slot == 2 then cmd = cmd .. string.format(' --dir "%s"', NET_DIR:gsub("/", "\\")) end
    local ok, r = pcall(os.execute, cmd)
    NETUI.launchErr, NETUI.launchWhy = (not ok) and oneLine(r) or nil, (not ok) and "exe" or nil
    out("联机面板：启动联机窗口 %s -> %s %s", cmd, ok and "已执行" or "失败", tostring(r))
end

-- 刚拉起、还在等它写 lobby.txt（8 秒内）
local function netuiStarting()
    return NETUI.launchAt ~= nil and os.time() - NETUI.launchAt < 8 and not NETUI.launchErr
end

-- 联机程序没响应就自动拉起（模组加载时、打开面板时）。10 秒内不重复拉；连续失败 NETUI_AUTO_TRIES 次后不再自动拉
local function netuiEnsure(why, tn)
    -- 实测（11-14 22:08 / 22:11）：多人房房主的联机程序忙时 lobby.txt 的 alive 落后 1.5–4 秒，这里按"没运行"重启，
    -- 第 3 次退到 <退路 exe> → 游戏卡死。管道连着就是活着（真断了有 NETUI.watchPipe 管），不看 alive
    if (PIPE and PIPE.on) or lobbyAlive(netuiRead(tn, true)) then NETUI.tries = 0; NETUI.dllTries = 0; return end
    if NETUI.launchAt and os.time() - NETUI.launchAt < 10 then return end
    if NETUI.tries >= NETUI_AUTO_TRIES then return end
    NETUI.tries = NETUI.tries + 1
    out("联机面板：联机程序没运行（%s），自动启动第 %d 次", why, NETUI.tries)
    netuiLaunch()
end

-- 管道断开（联机程序退出 / 崩了）4 秒后还没连回来就自动拉起，不等玩家打开面板。
-- 2026-11-05 19:56 内测：联机窗口点 × 退出后 4 分多钟没有联机程序，直到玩家在面板上手动点"重新启动联机程序"。
-- 每次断开算新的一轮（次数清零）；10 秒内不重复拉、连续 NETUI_AUTO_TRIES 次的限制照旧在 netuiEnsure 里
function NETUI.watchPipe(tn)
    if not PIPE.lostAt then return end
    if PIPE.on then PIPE.lostAt = nil; return end
    if os.time() - PIPE.lostAt < 4 then return end
    PIPE.lostAt, NETUI.tries = nil, 0
    netuiEnsure("管道断开", tn)
end

-- 当前显示的连接方式：刚在面板上换过、窗口还没跟上（2 秒内）时以面板的选择为准，连按左右才不会从旧值算
local function netuiMode(lb, tn)
    local w = NETUI.want
    if w and lb.mode ~= w.mode and tn - w.at < 2.0 then return w.mode end
    NETUI.want = nil
    return lb.mode or "lan"
end



-- ---- 文本按半角宽度折行 / 截断（原在 UPnP 提示旁；CONSENT.wrap 也用）
-- 宽度 w 以内能放下的字节数，以及其中最后一个能折行的位置（优先 ， ；之后，其次空格之后；太靠前的不算）
local function upnpFit(s, w)
    local x, last, punct, space = 0, 0, nil, nil
    for p, c in utf8.codes(s) do
        x = x + (c >= 0x1100 and 2 or 1)
        if x > w then return last, punct or space end
        last = p + #utf8.char(c) - 1
        if x > w // 3 then
            if c == 0xFF0C or c == 0x2C or c == 0xFF1B then punct = last elseif c == 0x20 then space = last end
        end
    end
    return #s, nil
end
local function upnpClip(s, w)
    local n = upnpFit(s, w)
    return n >= #s and s or s:sub(1, upnpFit(s, w - 2)) .. ".."
end

-- ============================================================ F3b-2：游戏内输入框 + 鼠标面板 GUI（从 main.lua 原样搬出）
-- 两段互相引用（输入框用 GUI 表 / guiRoot，GUI 用 editBox / inputMode / WBL），一起搬。UMG 控件只经 UE4SS 的 StaticFindObject /
-- 控件库建，不碰游戏对象；屏幕文字 HUD / 颜色 / 游戏适配 G / 切图 MAPGO / 硬直 STG / 好友 FRIEND 等经 ctx 给，CONSENT 经 consent 调用时取
local G, HUD, HUD_Y, umgColor, umgRoot = ctx.G, ctx.HUD, ctx.HUD_Y, ctx.umgColor, ctx.umgRoot
local MAPGO, STG, FRIEND, ST, full, status = ctx.MAPGO, ctx.STG, ctx.FRIEND, ctx.ST, ctx.full, ctx.status
local COL_GRAY, COL_LABEL, COL_ORANGE, COL_RED, COL_WHITE = ctx.COL_GRAY, ctx.COL_LABEL, ctx.COL_ORANGE, ctx.COL_RED, ctx.COL_WHITE
-- 11-13 冒烟：valid / getP1PC 以前是裸全局（main.lua 的 local，游戏里是 nil），鼠标面板建不起来又没日志。经 ctx 给；缺了用保守默认
local valid = ctx.valid or function(o) return o ~= nil end
local getP1PC = ctx.getP1PC or function() return nil end

-- 11-18 用户：面板里"结束 XX / 退出 XX"这类按钮的文字改成红色，一眼看出会中断当前玩法。
-- 只认能点的按钮（act，忙碌时 act 被摘掉、留 off）；说明行 / 菜单入口（"…"）不动；item.col 写了的按写的。
local END_BTN = { "^结束", "^退出", "^断开", "^离开", "^解散", "^中止", "^取消.*对局",
                  "^END ", "^DISCONNECT", "^LEAVE ", "^QUIT", "^EXIT" }
local function itemCol(it)
    if it.col or not (it.act or it.off) then return it.col end
    local s = it.label or ""
    for i = 1, #END_BTN do if s:find(END_BTN[i]) then return COL_RED end end
    return nil
end

-- ---- v0.18.6 游戏内输入框（原 main.lua 段；说明见那里的标题行）
-- UMG 的 EditableTextBox 放在屏幕文字的界面里；输入时 SetInputMode_UIOnlyEx，键盘只给输入框（游戏收不到，角色不动、Esc 不开菜单）。
-- 18:28 实机原型：打字 / Ctrl+V 正常；按回车后输入框丢焦点、而且读出来的字已经被清空 → 打字时每帧把内容记在 e.text，
-- 回车（UE4SS 键位，见文件末尾）用记下的内容提交；Esc 取消。切出窗口回来焦点会丢：丢焦点 0.3 秒内没等到回车就把焦点拿回来
local WBL = "/Script/UMG.Default__WidgetBlueprintLibrary"

-- v0.29.3 输入框字色。SetForegroundColor 只改没焦点时的字色，
-- 打字时输入框有焦点，用的是 WidgetStyle.FocusedForegroundColor（引擎默认浅灰）→ 三种状态和 TextStyle 都写成深色。
-- 要在输入框加进界面（建出 Slate 控件）之前写样式；之后再 SetForegroundColor 一次兜底
local EB_DARK = { R = 0.05, G = 0.05, B = 0.05, A = 1.0 }
local function ebStyle(eb)
    local ok, err = pcall(function()
        local st = eb.WidgetStyle
        for _, k in ipairs({ "ForegroundColor", "FocusedForegroundColor", "ReadOnlyForegroundColor" }) do
            st[k] = { SpecifiedColor = EB_DARK, ColorUseRule = 0 }
        end
        st.TextStyle.ColorAndOpacity = { SpecifiedColor = EB_DARK, ColorUseRule = 0 }
    end)
    if not ok then out("!! 输入框字色样式改不了：%s", tostring(err)) end
end
local function ebColor(eb)
    pcall(function() eb:SetForegroundColor(EB_DARK) end)
end

-- v0.20.0 鼠标面板。
-- 面板开着时放出鼠标（bShowMouseCursor + SetInputMode_UIOnlyEx，Phantom Pact 的面板也是这么放鼠标的），
-- 每行是真的 UMG Button，连接方式是一排标签页。UE4SS 的 Lua 绑不了按钮的 OnClicked 事件，所以每 33ms 查一次
-- IsHovered / IsPressed：上一次按着、这次松开且鼠标还在上面 = 点了一下。键盘 PgUp/PgDn/End 照样能用。
-- 建不起来（GUI.failed）就退回原来的文字面板。函数在 netuiRows 后面（guiShow 等），这里只放状态
local GUI = { on = false, failed = false }

-- v0.27.12 鼠标面板还是这张地图上建的那个（2026-11-05 19:05 崩溃：坐铃切图后按 Home，把旧地图的面板控件交给
-- SetInputMode_UIOnlyEx，引擎对失效控件 TakeWidget 崩了）。换过地图就不碰 GUI.w，只丢掉引用，guiRoot 重建
GUI.fresh = function()
    return GUI.w ~= nil and GUI.gen == ST.mapLoads and valid(GUI.w)
end
GUI.drop = function()
    GUI.w, GUI.on, GUI.shownVis, GUI.pc = nil, false, nil, nil
    if NETUI.box and NETUI.box.gui then NETUI.box = nil end
end

local function editBox()
    if HUD.failed then return nil end
    if GUI.on and GUI.fresh() and valid(GUI.eb) then     -- 鼠标面板里自带输入框
        local b = NETUI.box
        if not (b and b.eb == GUI.eb) then NETUI.box = { eb = GUI.eb, w = GUI.w, gui = true } end
        return NETUI.box
    end
    local b = NETUI.box
    if b and b.w == HUD.w and HUD.gen == ST.mapLoads and valid(HUD.w) and valid(b.eb) then return b end
    local cv = umgRoot()
    if not cv then return nil end
    local ok, r = pcall(function()
        local eb = StaticConstructObject(StaticFindObject("/Script/UMG.EditableTextBox"), HUD.w.WidgetTree)
        ebStyle(eb)
        local sl = cv:AddChildToCanvas(eb)
        sl:SetAnchors({ Minimum = { X = 0.5, Y = HUD_Y }, Maximum = { X = 0.5, Y = HUD_Y } })
        sl:SetAlignment({ X = 0.5, Y = 0.5 })
        sl:SetSize({ X = 460, Y = 44 })
        ebColor(eb)   -- 18:45 实测默认字色太浅
        eb:SetVisibility(1)
        return { eb = eb, sl = sl, w = HUD.w }
    end)
    if not ok then out("!! 输入框建不起来：%s", tostring(r)); return nil end
    NETUI.box = r
    return r
end

local function inputMode(ui)
    local okP, pc = pcall(getP1PC)
    if not (okP and valid(pc)) then return end
    -- 鼠标面板开着：不在输入时也是 UIOnly（游戏收不到键盘鼠标，角色不动），并显示鼠标
    -- v0.27.12 面板是旧地图的：先丢掉、这次按没有面板处理（只给游戏），下一轮 guiShow → guiRoot 重建后再放鼠标
    if GUI.on and not GUI.fresh() then out("鼠标面板：地图换过了，旧面板不用，重建"); GUI.drop() end
    local mouse = (NETUI.open and GUI.on) and true or false
    local ok, err = pcall(function()
        local lib = StaticFindObject(WBL)
        if ui then
            if not (NETUI.box and valid(NETUI.box.eb)) then error("输入框没了") end
            lib:SetInputMode_UIOnlyEx(pc, NETUI.box.eb, 0, false); NETUI.box.eb:SetKeyboardFocus()
        elseif mouse then lib:SetInputMode_UIOnlyEx(pc, GUI.focusable and GUI.w or nil, 0, false)
        elseif STG.locked then lib:SetInputMode_UIOnlyEx(pc, nil, 0, false)     -- 硬直关着输入：关面板也别开回
        else lib:SetInputMode_GameOnly(pc, false) end
        pc.bShowMouseCursor = mouse
    end)
    GUI.lostAt = nil            -- v0.26.4 guiMouseTick：重新开始数"面板丢焦点"
    if not ok then out("!! 切换输入模式失败：%s", tostring(err)) end
end

-- 硬直关输入：面板 / 输入框开着时不另外关；开回时按面板的状态来（先把 locked 放下再调 inputMode）
STG.uiBusy = function() return (NETUI.open and GUI.on) or NETUI.edit ~= nil end
STG.restoreInput = function()
    if STG.uiBusy() then STG.locked = false; return end      -- 面板 / 输入框开着：它关的时候 inputMode 会开回游戏
    local ok, err = G.gameInputBlock(false)
    if not ok then error(err) end
    STG.locked = false
end

local function editEnd(why)
    if not NETUI.edit then return end
    out("联机面板：输入框关闭（%s）", why)
    local kind = NETUI.edit.kind
    NETUI.edit = nil
    if NETUI.box and valid(NETUI.box.eb) then pcall(function() NETUI.box.eb:SetVisibility(1) end) end
    inputMode(false)
end

-- kind = join / name / port。没有 UMG 时只能退回打开电脑上的联机窗口。
-- 不预填当前值（18:45 实测：预填后打字会接在后面，SelectAllTextWhenFocused 不生效），当前值放在灰色提示文字里
local function editStart(kind, prompt, init, hint)
    local b = editBox()
    if not b then out("联机面板：没有游戏内输入框，改为打开联机窗口"); netuiCmd("show"); return end
    local ok, err = pcall(function()
        b.eb:SetText(FText(init or ""))
        b.eb:SetHintText(FText(hint or ""))
        b.eb:SetVisibility(0)             -- Visible：能接收键盘
    end)
    if not ok then out("!! 输入框打不开：%s", tostring(err)); netuiCmd("show"); return end
    pcall(function() b.eb:SetIsPassword(kind == "secret") end)       -- v0.22.0 输密码时显示成圆点（同一个输入框，别的项要关掉）
    NETUI.edit = { kind = kind, prompt = prompt, text = init or "", lostAt = nil }
    inputMode(true)
    out("联机面板：输入框打开（%s）", kind)
end

local function editCommit()
    local e = NETUI.edit
    if not e then return end
    local s = (e.text or ""):gsub("[\r\n|]", " "):gsub("^%s+", ""):gsub("%s+$", "")
    editEnd("回车")
    if s == "" then return end
    out("联机面板：输入「%s」→ %s", e.kind == "secret" and "***" or s, e.kind)   -- 密码不进日志
    if e.kind == "join" then NETUI.sel = 1; netuiCmd("join", "target=" .. s .. "\n")
    elseif e.kind == "mpjoin" then NETUI.sel = 1; netuiCmd("mpjoin", "target=" .. s .. "\n")     -- 多人混战 ②
    elseif e.kind == "name" then netuiCmd("name", "name=" .. s .. "\n")
    elseif e.kind == "port" then netuiCmd("port", "port=" .. s .. "\n")
    elseif e.kind == "server" then netuiCmd("server", "server=" .. s .. "\n")
    elseif e.kind == "secret" then netuiCmd("secret", "secret=" .. (s == "-" and "" or s) .. "\n")
    end
end

-- 每帧（netuiRows）：有焦点时记下内容；丢了焦点等 0.3 秒回车，没等到就拿回焦点
local function editTick()
    local e, b = NETUI.edit, NETUI.box
    if not e then return end
    if not (b and valid(b.eb)) then editEnd("输入框没了"); return end
    local y = (not b.gui) and HUD.rowY and HUD.rowY[3]
    if y and y ~= b.y then b.y = y; pcall(function() b.sl:SetPosition({ X = 0.0, Y = y }) end) end
    local okF, focus = pcall(function() return b.eb:HasKeyboardFocus() end)
    if okF and focus then
        e.lostAt = nil
        local okT, s = pcall(function() return b.eb:GetText():ToString() end)
        if okT and type(s) == "string" then e.text = s end
        return
    end
    local t = now()             -- 墙钟（os.clock 是 CPU 时间，多线程下跑得快）
    if not e.lostAt then e.lostAt = t
    elseif t - e.lostAt > 0.3 then e.lostAt = nil; inputMode(true) end
end

local function netuiFlash(s, col)
    NETUI.flash = { text = s, till = os.time() + 3, col = col }
end

-- 每帧（netuiRows）：联机程序回了这条命令（ack）→ 结束等待；超时 → 红字提示
function NETUI.busyStep(lb, tn)
    local b = NETUI.busy
    if not b then return end
    if tn < b.at then b.at = tn end                  -- now 的时间基准变了（见 netuiRead）
    if lb and lb.ack == b.id then
        NETUI.busy = nil
        out("联机面板：%s 已执行（等了 %d ms）", b.cmd, math.floor((tn - b.at) * 1000 + 0.5))
    elseif tn - b.at > NETUI.BUSY_SEC then
        NETUI.busy = nil
        out("!! 联机面板：%s 等了 %.0f 秒联机程序没回话", b.cmd, NETUI.BUSY_SEC)
        netuiFlash(T("联机程序没回应，再点一次试试", "THE ONLINE HELPER DID NOT ANSWER, TRY AGAIN"), COL_RED)
    end
end

-- 房间列表的显示名：联机程序给的"地图 Map_DengDingShanDao"换成游戏里的大区域名（game_places 里没有的照旧）；
-- 已满的房间把末尾的"已满"（长名字会被截掉看不到）挪到最前面做标记。排序（可加入的在前）在 netuiTargets 里
local function parseTg(s)
    if not s or s == "" then return nil end
    local tg = {}
    for k, v in s:gmatch("([^;=]+)=([^;=]*)") do
        tg[k] = v
    end
    if not tg.k then tg.k = "1v1" end
    return tg
end

local function roomLabel(kind, label, tg)
    label = label:gsub("(" .. T("地图 ", "map ") .. ")(%S+)", function(pre, lv)
        local r = MAPGO.PLACES and MAPGO.PLACES.regions[string.lower(lv)]
        return pre .. (r and T(r[1], r[2]) or lv)
    end)
    -- 11-15 用户："房间的 tag [旧版][淘汰][积分][对决] 要在房间名前面显示清楚"：最前面固定是玩法，再是 [服务器房]、[对局中] / [已满]，
    -- 再是人数（多人房）和分队，最后房间名。没有 tg（老版本联机程序开的）和 k = 1v1 的都是旧版 1v1
    if kind == "nohost" then return label end     -- 在玩但没开房的好友：不是房间，不标玩法
    local full = kind == "full" or kind == "watch"     -- watch = 满了但能观战
    if full then label = label:gsub(T("  已满", "  full"), "", 1) end
    if kind == "sroom" then label = "[" .. NETUI.bridgeSvc() .. T("房] ", "] ") .. label end     -- 0.34 房主开了"对局走桥接平台"
    local isMp = tg and (tg.k == "mp" or tg.k == "srv")
    if isMp and tonumber(tg.n) and tonumber(tg.max) and tonumber(tg.n) >= tonumber(tg.max) then full = true end     -- 多人房人数到上限 = 满（busyNoJoin 也这么判），标签跟着写 [已满]
    local mode
    if not isMp then mode = T("[旧版]", "[CLASSIC]")
    elseif tg.m == "duel" then mode = T("[对决]", "[DUEL]")
    elseif tg.m == "score" then mode = T("[积分]", "[SCORE]")
    else mode = T("[淘汰]", "[ELIM]") end
    local tags = { mode }
    if tg and tg.k == "srv" then tags[#tags + 1] = T("[服务器房]", "[SERVER]") end
    if full then tags[#tags + 1] = T("[已满]", "[FULL]") end     -- 满了排在对局中前面：多人房对局中能进，满了才是进不去的原因
    if tg and (tg.st == "fight" or tg.st == "cd") then tags[#tags + 1] = T("[对局中]", "[IN MATCH]") end
    local count = ""
    if isMp then
        local n, mx = tg.n or "1", tg.max or "4"
        count = n .. "/" .. mx
        -- 对局外 AI 不算人数（真人来了挤掉 AI），房主另报 ai=<个数>
        if tonumber(tg.ai) and tonumber(tg.ai) > 0 then count = count .. " + AI " .. tg.ai end
        if tg.m ~= "duel" and (tg.tm == "1" or tg.tm == "3") then
            count = count .. "·" .. ((tg.tm == "3" and T("三队", "3 TEAMS")) or (tonumber(mx) == 4 and "2v2") or T("两队", "TEAMS"))     -- 三队
        end
        count = count .. "  "
    end
    return table.concat(tags, " ") .. " " .. count .. label
end

-- 能点"加入"的房间：kind = full（已满的房间）、watch（满了、能观战）和 nohost（在玩但没开房的平台好友）列出来但不算"可加入"
-- 对局中（st == "fight" 或 st == "cd"）不可加入
-- 多人房（淘汰 / 积分 / 对决）对局中也能进：进去这局先观战，下局参加。
-- 旧版 1v1 的房对局中照旧不能点
local function busyNoJoin(tg)
    if not tg then return false end
    local mp = tg.k == "mp" or tg.k == "srv" or (tonumber(tg.max) or 0) >= 2
    if mp and tonumber(tg.n) and tonumber(tg.max) and tonumber(tg.n) >= tonumber(tg.max) then return true end     -- 多人房人数到上限 = 满（不靠联机程序标 full）
    if not (tg.st == "fight" or tg.st == "cd") then return false end
    return not mp
end
local function roomOpen(t)
    if t.kind == "full" or t.kind == "nohost" or t.kind == "watch" or t.kind == "watchf" then return false end
    if busyNoJoin(t.tg) then return false end
    return true
end

-- lobby.txt 里的加入目标分两类：剪贴板 / 最近（快捷加入，放主页）和房间（局域网房间、开房的平台好友、服务器 / 平台大厅的房间、
-- Tailscale 节点，放房间列表页）。kind = full 是已满的房间（v0.19.1 起联机程序单独标），列出来但选不中
local function netuiTargets(lb)
    local quick, rooms = {}, {}
    for i = 1, tonumber(lb.n) or 0 do
        local kind, label, target = (lb["t" .. i] or ""):match("^(%w+)|([^|]*)|(.*)$")
        if kind then
            local tg = parseTg(lb["tg" .. i])
            local t = { kind = kind, label = label, target = target, tg = tg }
            if kind == "clip" or kind == "recent" then quick[#quick + 1] = t else
                t.label = roomLabel(kind, label, tg)
                rooms[#rooms + 1] = t
            end
        end
    end
    -- 固定顺序：可加入的在前，再按显示名、target 排。20:32 实测大厅服务器每次给的顺序都不一样，
    -- 列表每 2 秒刷新一次就整个打乱，光标下的房间会突然换成另一个（按 End 可能进错房间）
    table.sort(rooms, function(a, b)
        local oa, ob = roomOpen(a), roomOpen(b)
        if oa ~= ob then return oa end
        if a.label ~= b.label then return a.label < b.label end
        return a.target < b.target
    end)
    return quick, rooms
end

-- inList = 房间列表页：不加"加入"前缀，多留几个字给末尾的房间号
local ROOM_CLIP = 60     -- 11-15 房间列表每行截到这么多字（原来 40；面板宽见 GUI_W_ROOMS）
local function joinItem(t, inList)
    if t.kind == "full" or busyNoJoin(t.tg) then
        return { label = clipText(t.label, ROOM_CLIP), target = t.target }   -- 已满 / 旧版 1v1 对局中：说明行（灰、跳过）； 多人房对局中能点
    end
    -- 满了、主机认观战的局域网房间（联机程序标 watch）/ 开房的平台好友（watchf）：点了以观战者身份连进去
    if t.kind == "watch" or t.kind == "watchf" then
        return { label = clipText(T("观战 ", "WATCH ") .. t.label, ROOM_CLIP), target = t.target,
                 act = function() netuiCmd("spectate", "target=" .. t.target .. "\n") end }
    end
    if t.kind == "nohost" then     -- 没开房的好友：能点，但只提示，不发起注定超时的连接
        return { label = clipText(t.label, ROOM_CLIP), target = t.target,
                 act = function() netuiFlash(T("对方还没开房", "THEY ARE NOT HOSTING YET"), COL_ORANGE) end }
    end
    local isMp = t.tg and (t.tg.k == "mp" or (tonumber(t.tg.max) or 0) >= 2)
    local watchRoom = NETUI.roomWatch and inList     -- 11-18 观战选房页：点房间 = 观战
    if t.kind == "sroom" then     -- 0.34 对局走桥接平台的房：自己没开"对局走桥接平台"就先确认（steam_match），join 带桥接平台=1 让联机程序顺手开
        return { label = inList and clipText(t.label, ROOM_CLIP) or (T("加入 ", "JOIN ") .. clipText(t.label, 30)), target = t.target,
                 act = function()
                     if not NETUI.hasBridge() then     -- 没装桥接扩展包（公开版 / 没有桥接平台扩展）：进不了，提示而不是发注定被拒的 join
                         local svc = NETUI.bridgeSvc()
                         netuiFlash(T("这间房的对局走" .. svc .. "，要装了桥接扩展包并在它的插件页启用才能进", "THIS ROOM PLAYS OVER " .. svc:upper() .. "; NEEDS THE " .. svc:upper() .. " EXTENSION"), COL_ORANGE); return
                     end
                     local cmdName = watchRoom and "spectate" or (isMp and "mpjoin" or "join")
                     -- 主机设成一直桥接的房：客机不看自己的设置，只要同意过桥接（没同意就先问）
                     consent().ask("bridge_match", function() netuiCmd(cmdName, "target=" .. t.target .. "\nbridge=1\n") end)
                 end }
    end
    if isMp then
        return { label = inList and clipText(t.label, ROOM_CLIP) or (T("加入 ", "JOIN ") .. clipText(t.label, 30)), target = t.target,
                 act = function() netuiCmd(watchRoom and "spectate" or "mpjoin", "target=" .. t.target .. "\n") end }
    end
    return { label = inList and clipText(t.label, ROOM_CLIP) or (T("加入 ", "JOIN ") .. clipText(t.label, 30)), target = t.target,
             act = function() netuiCmd(watchRoom and "spectate" or "join", "target=" .. t.target .. "\n") end }
end

local function noRoomsText(mode)
    local t = NOROOMS[mode]
    return t and T(t[1], t[2]) or T("（没发现房间）", "(NO ROOMS FOUND)")
end

-- 还在取第一份房间列表时说"正在获取"，别说"没有房间"：联机程序说还在取（listing=1，最多 10 秒），
-- 或者刚打开面板 / 进房间列表页 / 换连接方式 1.5 秒内（局域网房间每秒广播一次）
function NETUI.noRooms(lb, mode, tn)
    local age = tn - (NETUI.listFrom or -100)
    if age < 0 then NETUI.listFrom, age = tn, 0 end
    if age < 10 and (lb.listing == "1" or age < 1.5) then return T("（正在获取房间…）", "(LOOKING FOR ROOMS...)") end
    return noRoomsText(mode)
end

-- 进房间列表页：马上让联机程序重新问一次房间（服务器大厅 / 平台大厅本来 2–3 秒问一次）
function NETUI.openRooms()
    -- 0.34 冒烟（11-12）：没同意过时联机程序不发 LIST，页面只会一直"没有房间"，玩家不知道为什么——先按方式问一次同意
    do local lb = netuiRead(now()) or {}
       local mk = consent().MODE_KEY[lb.mode or ""]
       if mk then
           if not consent().has("play") then return consent().ask("play", NETUI.openRooms) end
           if not consent().has(mk) then return consent().ask(mk, NETUI.openRooms) end
       end
    end
    NETUI.page, NETUI.sel, NETUI.roomPage, NETUI.listFrom = "rooms", 1, 1, now()
    NETUI.roomsFresh = true      -- 筛选行在最上面：刚打开时光标放到第一个房间（房间列表到了才知道在哪），自己动过光标就不管
    netuiCmd("refresh")
end

-- 房间列表页一共几页（至少 1）
-- 每页几个房间：鼠标面板 GUI_PER_PAGE，文字面板 NETUI_PER_PAGE
local function perPage()
    return (GUI.on and not GUI.failed) and GUI_PER_PAGE or NETUI_PER_PAGE
end

local function roomPages(rooms)
    return math.max(1, math.ceil(#rooms / perPage()))
end

-- 房间筛选（本机偏好 room_filter）：读一次记在内存里，面板每帧画列表不再读盘
function NETUI.roomFilter()
    if NETUI.roomFilterV == nil then NETUI.roomFilterV = (PREF and PREF.get and PREF.get("room_filter")) or "all" end
    return NETUI.roomFilterV
end
function NETUI.setRoomFilter(f)
    NETUI.roomFilterV = f
    if PREF and PREF.set then PREF.set("room_filter", f) end
end

local function filterRooms(rooms)
    local f = NETUI.roomFilter()
    if f == "all" then return rooms end
    local res = {}
    for _, r in ipairs(rooms) do
        if f == "1v1" then
            if not r.tg or r.tg.k == "1v1" or r.tg.m == "duel" then res[#res + 1] = r end
        elseif f == "multi" then
            if r.tg and (r.tg.k == "mp" or r.tg.k == "srv" or (tonumber(r.tg.max) or 0) >= 2) and r.tg.m ~= "duel" then res[#res + 1] = r end
        elseif f == "open" then
            if roomOpen(r) then res[#res + 1] = r end
        else
            res[#res + 1] = r
        end
    end
    return res
end

function NETUI.hasMpRooms(rooms)
    for _, r in ipairs(rooms or {}) do
        if r.tg and (r.tg.k == "mp" or r.tg.k == "srv" or (tonumber(r.tg.max) or 0) >= 2) then return true end
    end
    return false
end

-- ---- v0.20.0 鼠标面板（原 main.lua 段）
local GUI_Z = 460            -- 比屏幕文字（450）高
local GUI_ROWS = 17           -- 各页底下多了"房间设置…"一行，16 → 17；房间设置单独一页后最多 11 项，回到 16； 曾 16 项 / 18；v0.27.6 设置页已有 12 项，留两行余量；v0.27.21 13 项（感谢名单），14 → 15； 14 项（服务器中转），15 → 16
local GUI_W = 640            -- 面板最小宽度（Slate 单位）
-- 11-15 用户："房间列表界面宽度也加宽一些，不然有些人名字长后面很多都看不到了"：房间列表页面板最小宽 1000、每行截到 ROOM_CLIP 字（原来 640 / 40）
local GUI_W_ROOMS = 1000
U.GUI_W, U.GUI_W_ROOMS = GUI_W, GUI_W_ROOMS
local BG_ROW = { R = 0.10, G = 0.10, B = 0.12, A = 0.92 }
local BG_HOVER = { R = 0.24, G = 0.24, B = 0.28, A = 0.95 }
local BG_SEL = { R = 0.42, G = 0.30, B = 0.08, A = 0.95 }      -- 键盘选中 / 当前的连接方式：金
local BG_DOWN = { R = 0.60, G = 0.44, B = 0.12, A = 1.0 }
local BG_NONE = { R = 0.0, G = 0.0, B = 0.0, A = 0.0 }
local BG_OFF = { R = 0.10, G = 0.10, B = 0.12, A = 0.45 }      -- 等回话时的按钮：底色淡一半

local function guiNew(cls)
    return StaticConstructObject(StaticFindObject("/Script/UMG." .. cls), GUI.w.WidgetTree)
end

local function guiFont(tb, size)
    local f = tb.Font
    f.Size = size
    f.OutlineSettings.OutlineSize = 1
    f.OutlineSettings.OutlineColor = { R = 0, G = 0, B = 0, A = 0.8 }
    tb:SetFont(f)
end

local function guiText(size, col)
    local tb = guiNew("TextBlock")
    guiFont(tb, size)
    tb:SetColorAndOpacity(umgColor(col))
    return tb
end

-- 按钮：三种状态都画成白色方块（没贴图的 Image），颜色每帧由 BackgroundColor 按 悬停 / 选中 / 按下 自己染；
-- 改样式失败就用引擎默认样式（照样能点）。halign：1 左 / 2 中
local function guiButton(size, halign)
    local b = guiNew("Button")
    local okS, errS = pcall(function()
        local st = b.WidgetStyle
        for _, k in ipairs({ "Normal", "Hovered", "Pressed", "Disabled" }) do
            st[k].DrawAs = 3
            st[k].TintColor = { SpecifiedColor = { R = 1, G = 1, B = 1, A = 1 }, ColorUseRule = 0 }
        end
        b:SetStyle(st)
    end)
    if not okS and not GUI.styleErr then GUI.styleErr = true; out("!! 鼠标面板：按钮样式改不了（用默认样式）：%s", tostring(errS)) end
    local tb = guiText(size, COL_WHITE)
    local s = b:SetContent(tb)
    pcall(function()
        s:SetPadding({ Left = 14, Top = 5, Right = 14, Bottom = 5 })
        s:SetHorizontalAlignment(halign)
        s:SetVerticalAlignment(2)
    end)
    return { b = b, tb = tb, s = s }
end

-- 下面三个只在值变了时才调引擎
local function guiLabel(p, s, col)
    local key = string.format("%s|%d,%d,%d", s, col.R, col.G, col.B)
    if p.key == key then return end
    p.key = key
    p.tb:SetText(FText(s))
    p.tb:SetColorAndOpacity(umgColor(col))
end

local function guiBg(p, c)
    if p.bg == c then return end
    p.bg = c
    p.b:SetBackgroundColor(c)
end

-- 0 Visible（能点）/ 1 Collapsed / 3 HitTestInvisible（只显示）
local function guiVis(p, v)
    if p.vis == v then return end
    p.vis = v
    if v ~= 0 then p.down, p.hov = false, false end
    (p.b or p):SetVisibility(v)
end

-- 11-14 快速点击：面板每拍才查一次 IsPressed，按下和松开都落在两拍之间的点击（很快的点、触摸板轻点）以前收不到。
-- 另一条路：左键钩子（main.lua 绑 LEFT_MOUSE_BUTTON → U.lmbDown，只在面板开着时记）数按下次数；每拍开头 U.guiTickBegin 看这一拍有没有新的按下，
-- 有就交给这一拍里鼠标悬停的那个按钮（同一拍只给一个）。钩子已经替某个按钮点过时，那一下松开就不再按老办法再点一次
local LMB = { n = 0, seen = 0, pending = false }
function U.lmbDown() if NETUI.open then LMB.n = LMB.n + 1 end end
function U.guiTickBegin() LMB.pending = LMB.n > LMB.seen; LMB.seen = LMB.n end
-- 这次查询算不算点了一下：上一次按着、这次松开且鼠标还在上面；或者这一拍左键钩子记到一次按下、鼠标在它上面
local function guiClicked(p)
    local okP, pressed = pcall(function() return p.b:IsPressed() end)
    local okH, hov = pcall(function() return p.b:IsHovered() end)
    pressed, hov = okP and pressed == true, okH and hov == true
    local click = p.down == true and not pressed and hov
    if click and p.hooked then click = false end          -- 这一按钩子已经点过了
    if not pressed then p.hooked = nil end
    if not click and LMB.pending and hov then click = true; LMB.pending = false; p.hooked = pressed or nil end
    p.entered = hov and not p.hov
    p.down, p.hov = pressed, hov
    return click
end

local function guiBuild()
    local okP, pc = pcall(getP1PC)
    if not (okP and valid(pc)) then
        if not GUI.noPcLogged then GUI.noPcLogged = true; out("鼠标面板：拿不到玩家控制器（%s），这次先用文字面板，下次打开再试", okP and "nil" or tostring(pc)) end
        return false
    end
    local ok, err = pcall(function()
        GUI.w = StaticFindObject(WBL):Create(pc, StaticFindObject("/Script/UMG.UserWidget"), pc)
        local cv = guiNew("CanvasPanel")
        GUI.w.WidgetTree.RootWidget = cv
        cv:SetVisibility(4)                      -- SelfHitTestInvisible：空白处不挡
        local win = guiNew("Border")
        win:SetBrushColor({ R = 0.02, G = 0.02, B = 0.03, A = 0.88 })
        win:SetPadding({ Left = 22, Top = 16, Right = 22, Bottom = 16 })
        local sl = cv:AddChildToCanvas(win)
        sl:SetAnchors({ Minimum = { X = 0.5, Y = 0.45 }, Maximum = { X = 0.5, Y = 0.45 } })
        sl:SetAlignment({ X = 0.5, Y = 0.5 })
        sl:SetAutoSize(true)
        local size = guiNew("SizeBox")
        size:SetMinDesiredWidth(GUI_W)
        GUI.size, GUI.sizeW = size, GUI_W     -- 房间列表页换宽（main.lua 的 guiShow）
        win:SetContent(size)
        local vb = guiNew("VerticalBox")
        size:SetContent(vb)
        local function add(child, top, halign)
            local s = vb:AddChildToVerticalBox(child)
            s:SetPadding({ Left = 0, Top = top or 0, Right = 0, Bottom = 0 })
            s:SetHorizontalAlignment(halign or 0)     -- 0 = 撑满
            return s
        end
        local head = guiNew("HorizontalBox")
        add(head)
        GUI.title = { tb = guiText(26, COL_LABEL) }
        local hs = head:AddChildToHorizontalBox(GUI.title.tb)
        hs:SetSize({ SizeRule = 1, Value = 1.0 })     -- 占满剩下的宽度，关闭按钮靠右
        hs:SetVerticalAlignment(2)
        GUI.close = guiButton(18, 2)
        head:AddChildToHorizontalBox(GUI.close.b)
        GUI.status = { tb = guiText(20, COL_GRAY) }
        add(GUI.status.tb, 8)
        GUI.tabBox = { b = guiNew("HorizontalBox") }
        add(GUI.tabBox.b, 12)
        GUI.tabs = {}
        for i, m in ipairs(NETUI.TABS) do
            local t = guiButton(18, 2)
            t.mode = m
            local ts = GUI.tabBox.b:AddChildToHorizontalBox(t.b)
            ts:SetSize({ SizeRule = 1, Value = 1.0 })
            ts:SetPadding({ Left = i > 1 and 4 or 0, Top = 0, Right = 0, Bottom = 0 })
            GUI.tabs[i] = t
        end
        GUI.rows = {}
        for i = 1, GUI_ROWS do
            GUI.rows[i] = guiButton(22, 1)
            add(GUI.rows[i].b, i == 1 and 12 or 4)
        end
        -- v0.20.1 房间列表页底下的翻页栏：上一页 / 第 p/P 页 / 下一页 …… 返回
        GUI.navBox = { b = guiNew("HorizontalBox") }
        add(GUI.navBox.b, 12)
        GUI.prev, GUI.next, GUI.back = guiButton(20, 2), guiButton(20, 2), guiButton(20, 2)
        GUI.filter = guiButton(20, 2)
        GUI.enterMode = guiButton(20, 2)     -- 11-18 房间列表：进入方式（上场 / 只观战），小按钮放翻页栏
        GUI.pageNo = { tb = guiText(20, COL_WHITE) }
        GUI.navBox.b:AddChildToHorizontalBox(GUI.prev.b)
        local ps = GUI.navBox.b:AddChildToHorizontalBox(GUI.pageNo.tb)
        ps:SetPadding({ Left = 16, Top = 0, Right = 16, Bottom = 0 })
        ps:SetVerticalAlignment(2)
        GUI.navBox.b:AddChildToHorizontalBox(GUI.next.b)
        local gap = guiNew("Spacer")
        GUI.navBox.b:AddChildToHorizontalBox(gap):SetSize({ SizeRule = 1, Value = 1.0 })   -- 把"返回"推到最右
        local es = GUI.navBox.b:AddChildToHorizontalBox(GUI.enterMode.b)
        es:SetPadding({ Left = 0, Top = 0, Right = 12, Bottom = 0 })
        local fs = GUI.navBox.b:AddChildToHorizontalBox(GUI.filter.b)
        fs:SetPadding({ Left = 0, Top = 0, Right = 12, Bottom = 0 })
        GUI.navBox.b:AddChildToHorizontalBox(GUI.back.b)
        GUI.eb = guiNew("EditableTextBox")
        ebStyle(GUI.eb)
        add(GUI.eb, 14)
        ebColor(GUI.eb)
        GUI.eb:SetVisibility(1)
        GUI.editBox = { b = guiNew("HorizontalBox") }
        add(GUI.editBox.b, 10, 3)                    -- 靠右
        GUI.ok, GUI.cancel = guiButton(20, 2), guiButton(20, 2)
        GUI.editBox.b:AddChildToHorizontalBox(GUI.ok.b)
        GUI.editBox.b:AddChildToHorizontalBox(GUI.cancel.b):SetPadding({ Left = 8, Top = 0, Right = 0, Bottom = 0 })
        GUI.hint = { tb = guiText(16, COL_LABEL) }
        add(GUI.hint.tb, 14)
        GUI.w:AddToViewport(GUI_Z)
        GUI.pc, GUI.gen = pc, ST.mapLoads
    end)
    -- v0.26.4 面板能拿键盘焦点：放鼠标时焦点给它，guiMouseTick 看焦点被抢走（切出窗口回来）就重新放鼠标。拿不到焦点也能用，只是少一种检测
    if ok then
        GUI.focusable = pcall(function() GUI.w:SetIsFocusable(true) end)
                        or pcall(function() GUI.w.bIsFocusable = true end)
        if not GUI.focusable then out("鼠标面板：设不了可焦点，切窗口回来只靠鼠标指针 / 暂停检测") end
    end
    if not ok then
        GUI.failed = true
        out("!! 鼠标面板建不起来：%s，改用文字面板", tostring(err))
        pcall(function() GUI.w:RemoveFromParent() end)
        GUI.w = nil
        return false
    end
    out("鼠标面板已建好")
    return true
end

-- 换地图后旧的界面会被引擎回收：重建，并重新放出鼠标（玩家控制器可能也换了）
local function guiRoot()
    if GUI.fresh() then
        local okV, inVp = pcall(function() return GUI.w:IsInViewport() end)
        if okV and inVp then return true end
    end
    GUI.drop()
    if not guiBuild() then return false end
    GUI.on = true
    if NETUI.edit then editEnd("鼠标面板重建") else inputMode(false) end
    return true
end

local function guiHide()
    if GUI.fresh() and GUI.shownVis ~= 1 then
        GUI.shownVis = 1
        pcall(function() GUI.w:SetVisibility(1) end)
    end
end

local function netuiSetOpen(open, why)
    editEnd(why)
    NETUI.open = open
    out("联机面板 %s（%s）", open and "打开" or "关闭", why)
    if open then
        local C = consent()
        if C and C.betaSweep then C.betaSweep() end     -- 11-15 内测包默认同意（玩法 mod / 扩展包的项启动后才登记）
        netuiEnsure("打开面板", now())
        NETUI.sel, NETUI.page, NETUI.listFrom = 1, "main", now()
    else
        guiHide()
    end
    if GUI.on then inputMode(false) end             -- 放出 / 收回鼠标
end



-- ============================================================ F3b-3："最近"页（从 main.lua 原样搬出；lobby.txt 的 hn / h<i> 由联机程序给）
-- ---- 0.34 "最近"页（原 main.lua 段；说明见那里的标题行）
-- lobby.txt：hn / h<i> = <hist|friend>|<来源>|<时间 unix>|<昵称>|<现在能加入的目标，没有 = ->；steamfriends= / hide= 两个开关。
-- 没有独立的平台好友标签页了：点平台好友那行 = join target=桥接平台:<id>（联机程序临时切到桥接平台方式、断开后切回；二次确认 steam_match）。
local RECENT = {}     -- main.lua 里是全局（RECENT = UI.RECENT）
function RECENT.ago(sec)
    local d = os.time() - sec
    if d < 3600 then return T("刚才", "JUST NOW") end
    if d < 86400 then return string.format(T("%d 小时前", "%d H AGO"), math.floor(d / 3600)) end
    return string.format(T("%d 天前", "%d D AGO"), math.floor(d / 86400))
end
function RECENT.rows(lb)
    local rows = {}
    for i = 1, tonumber(lb.hn) or 0 do
        local kind, src, at, nick, target = (lb["h" .. i] or ""):match("^(%w+)|([^|]*)|(%d+)|([^|]*)|(.*)$")
        if kind then rows[#rows + 1] = { kind = kind, src = src, at = tonumber(at) or 0, nick = nick ~= "" and nick or "?", target = target ~= "-" and target ~= "" and target or nil } end
    end
    return rows
end
function RECENT.items(lb, items, page)
    local rows = RECENT.rows(lb)
    if #rows == 0 then items[#items + 1] = { label = T("（还没有一起玩过的人）", "(NOBODY YET)") } end
    for _, r in ipairs(rows) do
        local srcName = r.src == "bridge" and (NETUI.bridgeSvc() .. T("好友", " FRIEND")) or netModeName(r.src)
        local when = r.kind == "friend" and T("在开房", "HOSTING") or RECENT.ago(r.at)
        local tail = r.target and (r.kind == "hist" and T("  [在开房 → 加入]", "  [HOSTING → JOIN]") or T("  [加入]", "  [JOIN]")) or ""
        local target = r.target
        items[#items + 1] = { label = clipText(r.nick, 14) .. "  " .. srcName .. "  " .. when .. tail,
                              act = target and function() netuiCmd("join", "target=" .. target .. "\n") end or nil }
    end
    if NETUI.hasBridge() then     -- 桥接好友挪到单独一页
        items[#items + 1] = { label = T("桥接好友在单独一页：联机面板首页 → 桥接好友…", "BRIDGE FRIENDS HAVE THEIR OWN PAGE: MAIN PAGE -> BRIDGE FRIENDS..."), col = COL_LABEL }
    end
    local hide = lb.hide == "1"
    items[#items + 1] = { label = T("隐身（别人的最近 / 好友列表里看不到我）：", "INVISIBLE (OTHERS WON'T SEE ME): ") .. (hide and T("开", "ON") or T("关", "OFF")),
                          act = function() netuiCmd("hide", "on=" .. (hide and "0" or "1") .. "\n") end }
    items[#items + 1] = { label = T("清除本地联机记录", "CLEAR LOCAL HISTORY"),
                          act = function() netuiCmd("clearhist"); netuiFlash(T("已清除", "CLEARED")) end }
    items[#items + 1] = { label = T("（记录只在你电脑上，30 天自动删；点一行加入对方正在开的房）", "(LOCAL ONLY, AUTO-DELETED AFTER 30 DAYS; CLICK A ROW TO JOIN)") }
    items[#items + 1] = { label = T("返回", "BACK"), act = page("main") }
end



-- ============================================================ 桥接好友页
-- 只在装了桥接扩展包、桥在时出现（NETUI.hasBridge）：扩展包停用 = 首页入口和这一页一起没了。lobby.txt fn / f<i> =
-- <1 在开房 | 0 在玩没开房>|<昵称>|<目标 bridge:<ID> 或 ->（联机程序 FriendRows；没开"显示桥接好友"时是 0 行）。
-- "开房（好友可见）"= cmd host bridge=1：联机程序临时切到桥接方式开房（同从好友行加入），断开后切回；要同意 bridge_match（开房）
local BFRIENDS = {}
function BFRIENDS.rows(lb)
    local rows = {}
    for i = 1, tonumber(lb.fn) or 0 do
        local hosting, nick, target, tgRaw = (lb["f" .. i] or ""):match("^([01])|([^|]*)|([^|]*)|?(.*)$")
        if hosting then
            local tg = parseTg(tgRaw)
            local isMp = tg and (tg.k == "mp" or tg.k == "srv")
            rows[#rows + 1] = {
                hosting = hosting == "1",
                nick = nick ~= "" and nick or "?",
                target = target ~= "-" and target ~= "" and target or nil,
                tg = tg,
                isMp = isMp
            }
        end
    end
    return rows
end
function BFRIENDS.items(lb, items, page)
    local on = lb.bridgefriends == "1"
    if (lb.role or "none") == "none" then     -- 11-15：好友是一个标签，这页顶上也画标签栏（文字面板里是"连接方式"那行，End / 左右换）
        items[#items + 1] = { label = T("连接方式：", "VIA: ") .. "< " .. NETUI.friendsName() .. " >", mode = true, act = function() NETUI.step(1) end }
    end
    local fname = NETUI.friendsName()
    items[#items + 1] = { label = T("显示" .. (fname:match("^%w") and " " or "") .. fname .. "、让好友看到我在开房：", "SHOW " .. fname .. " / LET THEM SEE I'M HOSTING: ") .. (on and T("开", "ON") or T("关", "OFF")),
                          act = function()
                              local function sfGo() netuiCmd("bridgefriends", "on=" .. (on and "0" or "1") .. "\n") end
                              if on then sfGo() else consent().ask("bridge_friends", sfGo) end      -- 开之前确认（读好友列表、公开在开房）
                          end }
    local inSession = (lb.role or "none") ~= "none"
    if not on then
        items[#items + 1] = { label = T("（打开上面这项才会读好友列表，好友也才看得到你开的房）", "(TURN THIS ON TO READ YOUR FRIEND LIST AND LET FRIENDS SEE YOUR ROOM)") }
    elseif lb.hide == "1" then
        items[#items + 1] = { label = T("（你开着隐身：不读好友列表，好友也看不到你；在「最近一起玩过的人」页里关）", "(INVISIBLE IS ON: FRIEND LIST NOT READ, FRIENDS CAN'T SEE YOU; TURN IT OFF ON THE RECENT PAGE)"), col = COL_ORANGE }
    else
        local rows = BFRIENDS.rows(lb)
        if #rows == 0 then items[#items + 1] = { label = T("（现在没有好友在玩这个游戏）", "(NO FRIEND IS PLAYING RIGHT NOW)") } end
        for _, r in ipairs(rows) do
            local target = r.target
            local isMp = r.isMp
            local label
            if r.hosting and isMp then
                label = roomLabel("room", clipText(r.nick, 16), r.tg) .. "  " .. T("[加入]", "[JOIN]")
            else
                label = clipText(r.nick, 16) .. "  " .. (r.hosting and T("在开房  [加入]", "HOSTING  [JOIN]") or T("在玩，没开房", "PLAYING, NOT HOSTING"))
            end
            local canAct = (target and not inSession)
            -- 多人房对局中也能进（进去先观战）；旧版 1v1 不走这里
            items[#items + 1] = {
                label = label,
                act = canAct and function()
                    if isMp then
                        netuiCmd("mpjoin", "target=" .. target .. "\n")
                    else
                        netuiCmd("join", "target=" .. target .. "\n")
                    end
                end or nil
            }
        end
        if not inSession then
            items[#items + 1] = { label = T("开房（好友可见）", "HOST A ROOM (VISIBLE TO FRIENDS)"),
                                  act = function() consent().ask("bridge_match", function() netuiCmd("host", "bridge=1\n") end) end }
            -- 11-19：没装 / 没启用多人模块时没有 mp 那条同意，「开房（最多 N 人）」点了只会卡在同意页 → 不显示
            local mpn = (BASEHOOK and BASEHOOK.mp and BASEHOOK.mp.cfg and BASEHOOK.mp.cfg.size) or 4
            if BASEHOOK and BASEHOOK.mp then
                items[#items + 1] = { label = string.format(T("开房（好友可见，最多 %d 人）", "HOST A ROOM (VISIBLE TO FRIENDS, UP TO %d)"), mpn),
                                      act = function() consent().ask("bridge_match", function() netuiCmd("mphost", "bridge=1\nn=" .. mpn .. "\n") end) end }
            end
        end
    end
    -- 11-15 用户："好友页里没必要再加一个（隐身），主面板上有最近的入口"：隐身开关只在"最近一起玩过的人"页，这里不再重复
    items[#items + 1] = { label = T("（好友开的房经桥接直接连，不经大厅服务器；好友在服务器大厅开的房在服务器大厅里找）", "(FRIEND ROOMS CONNECT OVER THE BRIDGE, NOT THE LOBBY SERVER; ROOMS ON THE LOBBY SERVER ARE LISTED THERE)") }
    items[#items + 1] = { label = T("返回", "BACK"), act = page("main") }
end

-- ============================================================ F3b-4：二次确认 CONSENT 框架 + 文案表 + EXTCTX + 拉黑 / 对手页 OPP（从 main.lua 原样搬出）
-- 文案写的全是联机功能（lan / server / ranked / 桥接平台 / upnp / cfg_fetch），是 fornet 概念；游戏适配 / 扩展包经 CONSENT.TEXT 加自己的键。
-- PREF（本地偏好）和 LANG 来自 fornet_core（调用方给同一份）
local LANG, PREF, COL_GREEN = ctx.LANG, ctx.PREF, ctx.COL_GREEN
local HUD_ROWS = ctx.HUD_ROWS     -- F3b-5：屏幕文字最多几行（netuiRows 截断用）
-- ---- 0.34 二次确认 CONSENT（原 main.lua 段；说明见那里的标题行）
-- 一张表管所有确认：key → { ver, zh = {...}, en = {...} }。同意过存 rules.txt consent_<key>=<ver>；文案改了把 ver +1 就会重新问。
-- PRIVACY.md 由 tools/gen_privacy.py 从这张表生成，弹窗和隐私说明逐字一致——改文案只改这里。
-- 用法：CONSENT.ask(key, fn)：同意过直接 fn，没同意翻到确认页，点"同意并继续"后 fn。
-- netuiCmd 里的 CONSENT.gate(cmd, extra, replay)：开房 / 加入 / AI 房等真人按当前方式查 play + 方式那个键。
local CONSENT = { pending = nil, PAGE = "consent" }
-- X1 给文件末尾的 ST.ext 用：T / netuiCmd / NETUI / 颜色都是这一段的 local，末尾那块看不见（11-12 冒烟：扩展页 T 为 nil）
local EXTCTX = { T = T, netuiCmd = netuiCmd, netuiFlash = netuiFlash, NETUI = NETUI, COL = { label = COL_LABEL, orange = COL_ORANGE, green = COL_GREEN, white = COL_WHITE } }

-- 主机开了 UPnP：显示联机程序给的结果（lobby.txt 的 upnp=，文案是联机程序按语言给的，这里不另拼）。
-- 正在请求橙、已转发绿、失败红；太长折成两行（先在句号处分开，让"请手动转发"单独一行；每行再长就截断），说明行不占光标。
-- 宽度按半角算（中文 2），鼠标面板一行大约放 46 个半角。
-- 这是基底功能，F4b 误搬进 game_ranked.lua、11-13 又跟着进了排位扩展包 → 排位包停用时房间里开面板每帧报 nil，搬回基底
NETUI.UPNP_W = 44
function NETUI.upnpInfo(lb, items)
    local s = lb.upnp or ""
    if lb.role ~= "host" or lb.upnpon ~= "1" or s == "" then return end
    local col = (s:find("失败", 1, true) or s:find("failed", 1, true)) and COL_RED
             or (s:find("正在", 1, true) or s:find("asking", 1, true)) and COL_ORANGE or COL_GREEN
    local w = NETUI.UPNP_W
    -- 折成最多 max 行：在逗号 / 空格处折，都没有就硬切；最后一行还放不下就截断
    local function wrap(t, max)
        while t ~= "" and max > 0 do
            local n, brk = upnpFit(t, w)
            if n >= #t then items[#items + 1] = { label = t, col = col }; return end
            if max == 1 then items[#items + 1] = { label = upnpClip(t, w), col = col }; return end
            local cut = brk or n
            items[#items + 1] = { label = (t:sub(1, cut):gsub("%s+$", "")), col = col }
            t, max = t:sub(cut + 1):gsub("^%s+", ""), max - 1
        end
    end
    -- 有句号就在句号处分（失败时第二句是"请手动转发"，单独一行）：前一句最多两行、后一句一行；没有句号就整句最多两行
    local p = s:find("。", 1, true) or s:find(". ", 1, true)
    if p then
        local cut = s:sub(p, p) == "." and p or p + 2
        wrap(s:sub(1, cut), 2)
        wrap((s:sub(cut + 1):gsub("^%s+", "")), 1)
    else
        wrap(s, 2)
    end
end
CONSENT.TEXT = {
    play = { ver = 1,
        zh = { "和对手联机时，会把你的位置、出招、血量 / 杀气、装备外观、快捷语发给对手，每个包带本机随机生成的安装号（net\\uid.txt，只用来拉黑 / 记最近一起玩过，不是任何平台账号）；", "接受房主的房间规则和数值表（只在这一局生效、不保存）；", "本机记一条\"最近一起玩过\"（只在你电脑上，30 天后自动删）。" },
        en = { "While playing online, your position, moves, health / energy, outfit and quick phrases are sent to the opponent, each packet tagged with a random install id generated on this PC (net\\uid.txt; only for blocking / recent players, not a platform account);", "the host's room rules and stat table apply for this match only (not saved);", "a local 'recently played with' entry is kept on your PC only (auto-deleted after 30 days)." } },
    lan = { ver = 1,
        zh = { "局域网方式会向同一局域网广播你的昵称、房间名和地图。" },
        en = { "LAN mode broadcasts your name, room name and map to the local network." } },
    server = { ver = 2,
        zh = { "连上后，模组会把你的昵称、安装号哈希、房间名和地图登记到这台服务器；它还能看到你的 IP 地址。服务器只负责介绍双方直连，对局数据不经过它；房主开了\"允许观战\"时，观战流经它转发。",
               "服务端程序是开源的，任何人都能架设。这台服务器由谁运营、怎样处理这些数据、是否安全，模组无法验证，也不对此负责。",
               "请只连接你知道来源、信得过的服务器。换了地址会再问一次。点\"同意并继续\"即表示你已了解并自行承担连接这台服务器的风险。" },
        en = { "Once connected, the mod registers your name, install-id hash, room name and map on this server; it can also see your IP address. The server only introduces both sides for a direct connection; match traffic does not pass through it. If the host allows spectators, the spectator stream is relayed by it.",
               "The server software is open source and anyone can run one. The mod cannot verify who runs this server, how it handles this data or whether it is safe, and takes no responsibility for it.",
               "Only connect to servers whose origin you know and trust. You will be asked again if the address changes. By choosing Agree you confirm that you understand and accept the risk of connecting to this server." } },
    -- mp（多人房：房里所有人都能看到你的 IP）、spec（观众收到你的对局数据）归各自的玩法 mod，经 U.addConsent 登记
    -- F4c-2：bridge_match / bridge_friends（对局走外部服务 / 显示外部服务好友）的文案由提供桥接 DLL 的扩展包经 API.consent.registerBase 登记（ext\桥接平台\init.lua），基底不写任何具体服务
    cfg_fetch = { ver = 1,
        zh = { "从大厅服务器下载一份数值表（标准数值基准 + 武器平衡，只有数字，不含代码）；", "保存在本机 net\\srvcfg.txt，以后开局按它算。" },
        en = { "Downloads a stat table (standard stats + weapon balance; numbers only, no code) from the lobby server;", "it is saved locally as net\\srvcfg.txt and used in later matches." } },
    upnp = { ver = 1,
        zh = { "UPnP 会在你的路由器上开一条端口映射（UDP，联机程序的端口），关掉时撤销。" },
        en = { "UPnP opens a port mapping (UDP, the helper's port) on your router; it is removed when you turn it off." } },
}
CONSENT.MODE_KEY = { lan = "lan", server = "server", bridge = "bridge_match" }     -- F4b：注册进来的方式（排位）经 U.addMode 加
-- 隐私说明的固定部分（PRIVACY.md 的头 / 尾也从这里生成，改文字只改这里）。不写任何指向特定人 / 特定服务器的描述（没有"作者""默认那台""所在地"），只写通用事实
CONSENT.PRIVACY_PLACE = { "", "" }     -- 以前放服务器所在地；现在不写，占位保留给 gen_privacy.py
CONSENT.PRIVACY_HEAD = {
    zh = { "这个模组是单人游戏的第三方联机 MOD。你没同意过下面任何一项时，它不会发出任何带你标识的数据（唯一的例外是末尾说的\"联机是否可用\"查询，不带标识）。",
           "下面每一项第一次用到时都会在游戏里问你一次；同意记在你电脑上的 net\\rules.txt，文案改了会再问。",
           "大厅服务器就是你在系统设置里填的那台（服务端程序开源，任何人都能架设）；本模组附带的服务端程序不保存任何外部联机服务的账号标识，日志里的地址只留前两段。" },
    en = { "This is a third-party online mod for a single-player game. Until you agree to one of the items below it sends nothing that identifies you (the only exception is the identifier-free availability query described at the end).",
           "Each item below is asked once in-game the first time it is needed; your answer is stored in net\\rules.txt on your PC and asked again if the text changes.",
           "The lobby server is whichever address is set in system settings (the server software is open source; anyone can run one); the bundled server software stores no external-service account identifiers and logs only the first two octets of addresses." } }
CONSENT.PRIVACY_TAIL = {
    zh = { "本机留存：net\\history.txt（最近一起玩过的人，30 天）、net\\blacklist.txt（黑名单，只存哈希）、net\\netcfg.txt（最近连过的地址）、net\\*.log。",
           "面板\"最近一起玩过的人\"页有\"清除本地联机记录\"；黑名单在\"拉黑 / 黑名单\"页逐条解除；删 net\\ 目录 = 全部清空。",
           "没有游戏内举报；作弊 / 骚扰请联系你所用那台服务器的管理员。",     -- 11-13：排行榜那半挪到排位扩展包的插件页 / 它自己的 PRIVACY.md
           "安全模式（系统设置）一键只留局域网 / 本地 AI，不连任何服务器、不碰任何外部联机服务。",
           "本模组是玩家自制的非商业项目：开源，不收费、不接受付费服务，不含游戏本体的任何资产，不修改游戏付费内容、不绕过任何保护机制。它不是游戏开发商或任何平台方的产品，与它们无关。开发商或权利人提出要求时，作者会配合下架或修改。" },
    en = { "Kept on this PC: net\\history.txt (recently played with, 30 days), net\\blacklist.txt (hashes only), net\\netcfg.txt (recent addresses), net\\*.log.",
           "Clear history from the 'Recently played with' page; unblock from the 'Block / blacklist' page; deleting the net\\ folder clears everything.",
           "There is no in-game reporting; contact the admin of the server you use about cheating / harassment.",
           "Safe mode (system settings) keeps only LAN / local AI, connects to no server and never touches any external online service.",
           "This mod is a non-commercial, fan-made project: open source, free of charge, no paid services, contains no game assets, does not alter paid content or bypass any protection. It is not a product of the game developer or any platform holder and is unaffiliated with them. If the developer or a rights holder asks, the author will take it down or change it." } }
CONSENT.PRIVACY_ORDER = { "play", "lan", "server", "cfg_fetch", "upnp" }     -- F4b：排位的键由 U.addMode 插在 server 后面
CONSENT.PRIVACY_TITLE = { play = { "联机对局", "Playing online" }, lan = { "局域网", "LAN" }, server = { "服务器大厅", "Lobby server" },
                          cfg_fetch = { "获取服务器数值表", "Fetch the server table" }, upnp = { "UPnP", "UPnP" } }
-- 面板"隐私说明"页：头 + 每项标题 + 文案 + 尾（每项是否已同意也标出来）
-- 0.34 冒烟（11-12）：整页 60 多行超过鼠标面板的行数上限（后面的行连"撤回 / 返回"都画不出来），改成分页：第 1 页总说明、每项一页、最后一页本机留存 + 撤回
function CONSENT.privacyPages()
    local Z = LANG.zh
    local pages, cur = {}, nil
    local function newPage(title) cur = { title = title, lines = {} }; pages[#pages + 1] = cur end
    local function add(l, indent, col) CONSENT.wrap(cur.lines, l, indent, col) end
    newPage(T("总说明", "OVERVIEW"))
    for _, l in ipairs(Z and CONSENT.PRIVACY_HEAD.zh or CONSENT.PRIVACY_HEAD.en) do add((l:gsub("{SERVER_PLACE}", T(CONSENT.PRIVACY_PLACE[1], CONSENT.PRIVACY_PLACE[2])))) end
    for _, k in ipairs(CONSENT.PRIVACY_ORDER) do
        local tx, tt = CONSENT.TEXT[k], CONSENT.PRIVACY_TITLE[k]
        if tx and not tx.ext then     -- 11-13：扩展包的同意项只在它的插件页
            newPage(T(tt[1], tt[2]))
            cur.lines[#cur.lines + 1] = { label = "■ " .. T(tt[1], tt[2]) .. (CONSENT.has(k) and T("（已同意）", " (agreed)") or T("（未同意）", " (not yet)")), col = COL_LABEL }
            for _, l in ipairs(Z and tx.zh or tx.en) do add(l, "  ") end
        end
    end
    -- 11-14：装着的玩法 mod 登记的项也列在这里（只读；同意 / 撤回在"已装的玩法"它那一栏）。扩展包（插件）的项只在它的插件页，基底页不写任何平台名
    local owned = {}
    for k, tx in pairs(CONSENT.TEXT) do if tx.ext and tx.mod then owned[#owned + 1] = k end end
    table.sort(owned)
    for _, k in ipairs(owned) do
        local tx, tt = CONSENT.TEXT[k], CONSENT.PRIVACY_TITLE[k] or { k, k }
        newPage(T(tt[1], tt[2] or tt[1]))
        cur.lines[#cur.lines + 1] = { label = "■ " .. T(tt[1], tt[2] or tt[1]) .. string.format(T("（来自 %s）", " (from %s)"), tostring(tx.ext))
                                          .. (CONSENT.has(k) and T("（已同意）", " (agreed)") or T("（未同意）", " (not yet)")), col = COL_LABEL }
        for _, l in ipairs(Z and tx.zh or tx.en) do add(l, "  ") end
    end
    newPage(T("本机留存 / 撤回", "LOCAL DATA / WITHDRAW"))
    -- 0.35 冒烟（11-12）：尾段全放一页超过面板行数（"撤回全部同意"画不出来）→ 最后一段（非商业声明）单独一页，撤回按钮跟在它后面
    local tail = Z and CONSENT.PRIVACY_TAIL.zh or CONSENT.PRIVACY_TAIL.en
    for i, l in ipairs(tail) do
        if i == #tail and i > 1 then newPage(T("声明 / 撤回", "NOTICE / WITHDRAW")) end
        add(l)
    end
    return pages
end
function CONSENT.privacyItems(lb, items, page)
    local pages = CONSENT.privacyPages()
    local pg = math.min(math.max(1, NETUI.privPg or 1), #pages)
    NETUI.privPg, NETUI.privPages = pg, #pages
    items[#items + 1] = { label = string.format(T("第 %d / %d 页 · %s", "PAGE %d / %d · %s"), pg, #pages, pages[pg].title), col = COL_LABEL }
    for _, it in ipairs(pages[pg].lines) do items[#items + 1] = it end
    if pg < #pages then
        items[#items + 1] = { label = T("下一页", "NEXT PAGE"), nav = "next", act = function() NETUI.privPg = pg + 1; NETUI.sel = 1 end }
    end
    if pg > 1 then
        items[#items + 1] = { label = T("上一页", "PREVIOUS PAGE"), nav = "prev", act = function() NETUI.privPg = pg - 1; NETUI.sel = 1 end }
    end
    if pg == #pages then
        items[#items + 1] = { label = T("撤回全部同意（下次用到时会再问）", "WITHDRAW ALL CONSENT (ASKED AGAIN NEXT TIME)"),
                              act = function() CONSENT.revokeAll(); netuiFlash(T("已撤回", "WITHDRAWN")) end }
    end
    items[#items + 1] = { label = T("返回", "BACK"), nav = "back", act = function() NETUI.privPg = 1; page("settings")() end }
end

-- 0.34：服务器大厅 / 排位的同意绑定当时填的那台服务器地址——换了地址就是另一台，要再问一次
CONSENT.PER_SERVER = { server = true }     -- F4b：排位的键由 U.addMode 加
function CONSENT.serverAddr()
    local lb = netuiRead(now()) or {}
    return ((lb.srv or ""):gsub("^%s+", ""):gsub("%s+$", "")):lower()
end
-- F4c-2：提供桥接 DLL 的扩展包登记基底里这两个固定键的文案（fornet_ext 的 API.consent.registerBase 调）。
-- 11-13 拆干净：也认 manifest 里 consent=@<键> 声明归这个包的键（CONSENT.EXT_OWN，fornet_ext 扫包时填）。
-- 扩展包登记的项（TEXT[key].ext = 包名）**不进基底的隐私说明页、不用基底的确认页**：要问时跳到那个包的插件页（ext:<包名>），文案和同意按钮只在那里
CONSENT.BASE_KEYS = { bridge_match = true, bridge_friends = true }
CONSENT.EXT_OWN = {}
function CONSENT.registerBase(key, d, who)
    if not CONSENT.BASE_KEYS[key] and CONSENT.EXT_OWN[key] ~= who then error("registerBase：只能登记 bridge_match / bridge_friends 或 manifest 里 consent=@ 声明的键", 2) end
    local zh, en = d.zh, d.en
    if type(zh) == "string" then zh = { zh } end
    if type(en) == "string" then en = { en } end
    if type(zh) ~= "table" or #zh == 0 then error("registerBase：要有 zh 文案", 2) end
    CONSENT.TEXT[key] = { ver = tonumber(d.ver) or 1, zh = zh, en = (type(en) == "table" and #en > 0) and en or zh, ext = who }
    CONSENT.PRIVACY_TITLE[key] = { d.title or key, d.titleEn or d.title or key }
    if d.perServer then CONSENT.PER_SERVER[key] = true end
end
-- 这个键归哪个扩展包（nil = 基底自己的）
function CONSENT.owner(key) local t = CONSENT.TEXT[key]; return t and t.ext or nil end
function CONSENT.revoke(key) PREF.set("consent_" .. key, nil); PREF.set("consent_" .. key .. "_addr", nil) end
-- 11-15 内测包（打包脚本放 <模组目录>\beta_defaults.txt，仓库 / 公开版没有这个文件）：每一项默认同意。
-- 每项第一次查到时同意一次并记 consent_<键>_auto=<ver>：撤回过的不再自动同意；文案改了（ver +1）再自动同意一次。
-- 按服务器的项（服务器大厅 / 排位）绑定包里预填的那台（server=）；玩家改填别的服务器照常问。
function CONSENT.beta()
    if CONSENT.BETA == nil then
        local kv = fileReadKV and fileReadKV(ROOT .. "/beta_defaults.txt")
        CONSENT.BETA = type(kv) == "table" and { server = ((kv.server or ""):gsub("%s+", "")):lower(), seen = {} } or false
        if CONSENT.BETA then out("内测包：同意项默认同意（beta_defaults.txt，预填服务器 %s）", CONSENT.BETA.server) end
    end
    return CONSENT.BETA
end
-- 启动后 / 打开面板时把已登记的项都过一遍：联机程序也查 rules.txt（Consent.Has，2 秒才重读一次），要在它发房间列表 / 开房之前就写好
function CONSENT.betaSweep() if CONSENT.beta() then for k in pairs(CONSENT.TEXT) do CONSENT.has(k) end end end
function CONSENT.has(key)
    local t = CONSENT.TEXT[key]
    if not t then return true end
    local b = CONSENT.beta()
    if b and b.seen[key] ~= t.ver then     -- seen：本次启动看过的项不再读盘
        b.seen[key] = t.ver
        if tonumber(PREF.get("consent_" .. key .. "_auto") or "") ~= t.ver then
            PREF.set("consent_" .. key .. "_auto", t.ver)
            PREF.set("consent_" .. key, t.ver)
            if CONSENT.PER_SERVER[key] then PREF.set("consent_" .. key .. "_addr", b.server ~= "" and b.server or "*") end
            out("内测包：默认同意 %s", key)
        end
    end
    -- F4c-2：0.34 记的是 consent_steam_match / consent_steam_friends；同一件事换了键名，照认并改写成新键（不重复问）
    if key:sub(1, 7) == "bridge_" and PREF.get("consent_" .. key) == nil and PREF.get("consent_steam_" .. key:sub(8)) ~= nil then PREF.set("consent_" .. key, t.ver) end
    if tonumber(PREF.get("consent_" .. key) or "") ~= t.ver then return false end
    if CONSENT.PER_SERVER[key] then
        local rec = PREF.get("consent_" .. key .. "_addr") or ""
        return rec == "*" or (rec ~= "" and rec == CONSENT.serverAddr())     -- "*" = grantAll（测试 / 安全模式重置）给的任意地址
    end
    return true
end
function CONSENT.grant(key)
    local t = CONSENT.TEXT[key]
    if not t then return end
    PREF.set("consent_" .. key, t.ver)
    if CONSENT.PER_SERVER[key] then PREF.set("consent_" .. key .. "_addr", CONSENT.serverAddr()) end
end
function CONSENT.grantAll() for k in pairs(CONSENT.TEXT) do CONSENT.grant(k); if CONSENT.PER_SERVER[k] then PREF.set("consent_" .. k .. "_addr", "*") end end end     -- 测试 / 安全模式重置用（地址绑定给 *）
function CONSENT.revokeAll()
    for k in pairs(CONSENT.TEXT) do PREF.set("consent_" .. k, nil); PREF.set("consent_" .. k .. "_addr", nil) end
    -- 0.34 评审：撤回同意时把靠这些同意开着的东西也关掉（不然富状态 / UPnP / 桥接平台房照旧）
    local lb = netuiRead(now()) or {}
    if lb.bridgefriends == "1" then netuiCmd("bridgefriends", "on=0\n") end
    -- "对局走桥接"三档不跟着关：没同意就不会用桥接（1 档只走直连，2 档开房时再问）
    netuiCmd("upnp", "on=0\n")
    if (lb.role or "none") ~= "none" then netuiCmd("stop", "") end
end

-- 同意过直接做；没同意翻到确认页（记下回来要做的事和原来的页）
function CONSENT.ask(key, fn)
    if CONSENT.has(key) then fn(); return true end
    local own = CONSENT.owner(key)
    local dest = (own and CONSENT.extRoute and (CONSENT.extHas == nil or CONSENT.extHas(own))) and ("ext:" .. own) or CONSENT.PAGE     -- 11-13：扩展包的项去它的插件页问（fornet_ext 装上后 extRoute = true）；L3：主人是玩法 mod（不在 ext\）的走基底确认页
    CONSENT.pending = { key = key, fn = fn, back = (NETUI.page ~= CONSENT.PAGE and NETUI.page ~= dest) and NETUI.page or "main" }
    NETUI.page, NETUI.sel = dest, 1
    return false
end
-- 插件页用：同意 / 取消待办（和基底确认页的两个按钮一样）
function CONSENT.agreePending()
    local pd = CONSENT.pending
    if not pd then return end
    local t = CONSENT.TEXT[pd.key]
    CONSENT.grant(pd.key)
    CONSENT.pending = nil
    NETUI.page, NETUI.sel = pd.back or "main", 1
    out("二次确认：同意 %s（文案版本 %d）", pd.key, t and t.ver or 0)
    if pd.fn then pd.fn() end
end
function CONSENT.cancelPending()
    local pd = CONSENT.pending
    if not pd then return end
    CONSENT.pending = nil
    NETUI.page, NETUI.sel = pd.back or "main", 1
    out("二次确认：取消 %s", pd.key)
end

-- netuiCmd 的闸门：host / join / airoom on=1 按方式查需要的键；都同意过返回 true
function CONSENT.gate(cmd, extra, replay)
    local keys
    local isMp = cmd == "mphost" or cmd == "mpjoin"     -- 11-14：多人房另加 mp（房里所有人都能看到你的 IP）
    if cmd == "mphost" then cmd = "host" elseif cmd == "mpjoin" then cmd = "join" end     -- 0.35 mesh 多人房：开 / 进房和 1v1 一样要同意
    if cmd == "mode" then     -- 0.34 评审：切到服务器大厅 / 排位 / 局域网就会向服务器 / 局域网发查询，选标签时就问（本地 AI 不问）
        local m = (extra or ""):match("mode=(%w+)")
        if not m or m == "ai" then return true end
        keys = { "play", CONSENT.MODE_KEY[m] or m }
    elseif cmd == "airoommode" then     -- AI 等人房间换方式：按新方式问
        local m = (extra or ""):match("mode=(%w+)")
        if not m then return true end
        keys = { "play", CONSENT.MODE_KEY[m] or m }
    elseif cmd == "host" or cmd == "join" or cmd == "spectate" then
        local lb = netuiRead(now()) or {}
        if cmd == "spectate" then     -- 观战也发握手（uid / 昵称 / 地址），按 join 的规则问
            local mk = CONSENT.MODE_KEY[lb.mode or ""]
            if (extra or ""):find("target=bridge:", 1, true) then mk = "bridge_match" end
            keys = { "play", mk }
        elseif lb.mode == "ai" then     -- 本地 AI 不发数据；但 netcfg 里 airoom=1（AI 对战时同时开房等真人）会去登记房间，按等人房间的方式问
            if lb.airoom ~= "1" then return true end
            keys = { "play", CONSENT.MODE_KEY[lb.airoommode or "lan"] }
            if lb.upnpon == "1" then keys[#keys + 1] = "upnp" end     -- 0.34 冒烟：0.33 留下的 upnp=1 开房会直接改路由器，先问
        else
        local mk = CONSENT.MODE_KEY[lb.mode or ""]
        if cmd == "join" and (extra or ""):find("target=bridge:", 1, true) then mk = "bridge_match" end     -- "最近"页点平台好友：经桥接平台连
        keys = { "play", mk }
        if cmd == "join" and (extra or ""):find("\nbridge=1", 1, true) then keys[#keys + 1] = "bridge_match" end     -- 进桥接平台房
        if cmd == "host" and lb.bridgeroom == "2" then keys[#keys + 1] = "bridge_match" end                        -- 开桥接平台房
        if cmd == "host" and lb.upnpon == "1" then keys[#keys + 1] = "upnp" end                                 -- 0.34 冒烟：upnp=1 开房会改路由器，先问
        end
    elseif cmd == "airoom" and (extra or ""):find("on=1", 1, true) then
        local lb = netuiRead(now()) or {}
        keys = { "play", CONSENT.MODE_KEY[lb.airoommode or "lan"] }
    else return true end
    -- 11-19：没装 / 没启用多人模块时没人登记 mp 这条同意。以前照样把 "mp" 加进 keys，面板翻到一条没有文案的同意页
    -- （只写 "This needs your consent first (the text is missing)"），房开不出来也进不去。这里直接拦下并说清楚。
    if isMp and not CONSENT.TEXT.mp then
        netuiFlash(T("没装多人模块：只能开 / 加入旧版 1v1 房间", "NO MULTIPLAYER MODULE: CLASSIC 1V1 ROOMS ONLY"))
        return false
    end
    if isMp then keys[#keys + 1] = "mp" end
    if not isMp and CONSENT.TEXT.spec and keys[2] ~= nil and (cmd == "host" or cmd == "join" or cmd == "spectate")
       and not ((netuiRead(now()) or {}).mode == "ai") then keys[#keys + 1] = "spec" end     -- 11-14：观战 mod 的项（观众收到你的对局数据）
    for _, k in ipairs(keys) do
        if k and not CONSENT.has(k) then return CONSENT.ask(k, replay) end
    end
    return true
end

-- 0.34 冒烟（11-12）：同意文案一句一行会把面板撑得比屏幕还宽（1080p 全屏都截两头），按 CONSENT.W 格折行（中文算 2 格，逗号 / 空格处折）
CONSENT.W = 56
function CONSENT.wrap(items, line, indent, col)
    local w = CONSENT.W - #(indent or "")
    local first = true
    while line ~= "" do
        local n, brk = upnpFit(line, w)
        local cut = (n >= #line) and #line or (brk or n)
        items[#items + 1] = { label = (first and (indent or "") or (indent or "") .. "  ") .. (line:sub(1, cut):gsub("%s+$", "")), col = col }
        line = (line:sub(cut + 1):gsub("^%s+", ""))
        first = false
    end
end
function CONSENT.items(lb, items, page)
    local pd = CONSENT.pending
    local t = pd and CONSENT.TEXT[pd.key]
    if not t then NETUI.page = "main"; return end
    if CONSENT.PER_SERVER[pd.key] then     -- 客户端自己告知：将连的是哪台（你填的地址），不是服务器说什么就是什么
        local addr = CONSENT.serverAddr()
        items[#items + 1] = { label = string.format(T("将连接的大厅服务器：%s（你在系统设置里填的）", "LOBBY SERVER TO CONNECT: %s (SET BY YOU IN SYSTEM SETTINGS)"), addr ~= "" and addr or T("未填", "NOT SET")), col = COL_ORANGE }
    end
    for _, line in ipairs(LANG.zh and t.zh or t.en) do CONSENT.wrap(items, line) end
    items[#items + 1] = { label = T("（同意后不再问；文案更新会再问一次。详见 PRIVACY.md）", "(ASKED ONCE; ASKED AGAIN IF THIS TEXT CHANGES. SEE PRIVACY.md)") }
    items[#items + 1] = { label = T("同意并继续", "AGREE AND CONTINUE"),
                          act = function()
                              CONSENT.grant(pd.key)
                              CONSENT.pending = nil
                              NETUI.page, NETUI.sel = pd.back or "main", 1
                              out("二次确认：同意 %s（文案版本 %d）", pd.key, t.ver)
                              if pd.fn then pd.fn() end
                          end }
    items[#items + 1] = { label = T("取消", "CANCEL"),
                          act = function() CONSENT.pending = nil; NETUI.page, NETUI.sel = pd.back or "main", 1; out("二次确认：取消 %s", pd.key) end }
end

-- 11-15 用户："这种多个平级的确认会导致界面放不下，要么多一层 title 各自显示"、"我同意协议和启用应该是一个意思"：
-- 插件页 / 玩法页只列条款标题，点进来这一页看全文（只看，不在这里同意 / 撤回）；启用 = 同意它的全部条款，停用 = 撤回（CONSENT.grantKeys / revokeKeys）
function CONSENT.title(k)
    local tt = CONSENT.PRIVACY_TITLE[k]
    return tt and T(tt[1], tt[2] or tt[1]) or k
end
function CONSENT.textItems(items, k, indent)
    local t = CONSENT.TEXT[k]
    if not t then return end
    if CONSENT.PER_SERVER[k] then
        local addr = CONSENT.serverAddr()
        items[#items + 1] = { label = (indent or "") .. string.format(T("将连接的大厅服务器：%s（你在系统设置里填的）", "LOBBY SERVER: %s (SET BY YOU IN SYSTEM SETTINGS)"), addr ~= "" and addr or T("未填", "NOT SET")), col = COL_ORANGE }
    end
    for _, line in ipairs(LANG.zh and t.zh or t.en) do CONSENT.wrap(items, line, indent) end
end
function CONSENT.showText(key, back)
    NETUI.ctext = { key = key, back = back or NETUI.page, sel = NETUI.sel }
    NETUI.page, NETUI.sel = "ctext", 1
end
function CONSENT.grantKeys(keys) for _, k in ipairs(keys) do CONSENT.grant(k) end end
function CONSENT.revokeKeys(keys)
    for _, k in ipairs(keys) do
        CONSENT.revoke(k)
        if k == "bridge_friends" and (netuiRead(now()) or {}).bridgefriends == "1" then netuiCmd("bridgefriends", "on=0\n") end     -- 靠这项开着的好友列表一起关
    end
end
-- 条款标题行（插件页 / 玩法详情页共用）：· 标题（已同意 / 未同意）… 点了看全文
function CONSENT.titleRows(items, keys)
    for _, k in ipairs(keys) do
        local has = CONSENT.has(k)
        items[#items + 1] = { label = "  · " .. CONSENT.title(k) .. (has and T("（已同意）", " (AGREED)") or T("（未同意）", " (NOT AGREED)")) .. "…",
                              act = function() CONSENT.showText(k) end }
    end
end
function CONSENT.textPage(lb, tn, items, page)
    local c = NETUI.ctext
    if not (c and CONSENT.TEXT[c.key]) then NETUI.page = "main"; return end
    items[#items + 1] = { label = "■ " .. CONSENT.title(c.key) .. (CONSENT.has(c.key) and T("（已同意）", " (AGREED)") or T("（未同意）", " (NOT AGREED)")), col = COL_LABEL }
    CONSENT.textItems(items, c.key)
    items[#items + 1] = { label = T("返回", "BACK"), act = function() NETUI.page, NETUI.sel, NETUI.ctext = c.back or "main", c.sel or 1, nil end }
end

-- 拉黑（举报 0.34 整个删了， / ——玩家有问题直接联系作者）。
-- 名单、标识、拒绝连入都在联机程序里（lobby/NetExtras.cs Blacklist，只存安装号 / 平台账号 ID哈希，不存 IP），面板只发命令：
-- block who=<第几个最近对手> / unblock i=<黑名单第几个>。lobby.txt：
-- opn / op<i> = <已拉黑 0/1>|<连接方式>|<几秒前>|<昵称>；bln / bl<i> = <连接方式>|<拉黑时间 unix>|<昵称>
local OPP = { who = 1, pg = 1, PER = 8 }
-- 0.34：联系作者的提示（举报没了）。文案和 PRIVACY.md 同一处来源，别写两份
OPP.CONTACT = { "遇到作弊 / 骚扰请联系你所用服务器的管理员（没有游戏内举报）", "FOR CHEATS / HARASSMENT, CONTACT THE ADMIN OF THE SERVER YOU USE (NO IN-GAME REPORTS)" }

function OPP.opp(lb, i)
    local b, mode, ago, nick = (lb["op" .. i] or ""):match("^([01])|([^|]*)|(%d+)|(.*)$")
    if not b then return nil end
    return { blocked = b == "1", mode = mode, ago = tonumber(ago) or 0, nick = nick ~= "" and nick or "?" }
end

local function blkAgo(sec)
    if sec < 60 then return T("刚才", "JUST NOW") end
    if sec < 3600 then return string.format(T("%d 分钟前", "%d MIN AGO"), math.floor(sec / 60)) end
    return string.format(T("%d 小时前", "%d H AGO"), math.floor(sec / 3600))
end

-- 系统设置页"感谢名单…"下面的入口
function OPP.entry(lb, items, page)
    items[#items + 1] = { label = T("拉黑 / 黑名单…", "BLOCK / BLACKLIST..."), act = page("opp") }
end

-- 最近的对手（最多 5 个）：选一个 → 拉黑
function OPP.oppItems(lb, items, page)
    local n = tonumber(lb.opn) or 0
    for i = 1, n do
        local o = OPP.opp(lb, i)
        if o then
            items[#items + 1] = { label = clipText(o.nick, 16) .. "  " .. netModeName(o.mode) .. "  " .. blkAgo(o.ago) .. (o.blocked and T("  [已拉黑]", "  [BLOCKED]") or ""),
                                  act = function() OPP.who = i; page("oppact")() end }
        end
    end
    if n == 0 then items[#items + 1] = { label = T("（还没有对手）", "(NO OPPONENT YET)") } end
    items[#items + 1] = { label = string.format(T("黑名单（%d）…", "BLACKLIST (%d)..."), tonumber(lb.bln) or 0), act = function() OPP.pg = 1; page("blist")() end }
    items[#items + 1] = { label = T(OPP.CONTACT[1], OPP.CONTACT[2]) }
    items[#items + 1] = { label = T("返回", "BACK"), act = page("settings") }
end

function OPP.actItems(lb, items, page)
    local o = OPP.opp(lb, OPP.who)
    if not o then page("opp")(); return end
    items[#items + 1] = { label = T("对手：", "OPPONENT: ") .. clipText(o.nick, 16) .. "（" .. netModeName(o.mode) .. "，" .. blkAgo(o.ago) .. "）" }
    if o.blocked then
        items[#items + 1] = { label = T("已在黑名单里（上一页的\"黑名单\"里可以解除）", "ALREADY BLOCKED (UNBLOCK IT IN THE BLACKLIST)") }
    else
        items[#items + 1] = { label = T("拉黑（以后不让他进我的房、不列他的房）", "BLOCK (REFUSE HIM, HIDE HIS ROOMS)"),
                              act = function() netuiCmd("block", "who=" .. OPP.who .. "\n"); netuiFlash(T("已拉黑", "BLOCKED")) end }
    end
    items[#items + 1] = { label = T("返回", "BACK"), act = page("opp") }
end

-- 系统设置 → 黑名单：点一行解除
function OPP.listItems(lb, items, page)
    local n = tonumber(lb.bln) or 0
    local shown = math.min(n, 40)
    local pages = math.max(1, math.ceil(shown / OPP.PER))
    OPP.pg = math.min(math.max(OPP.pg, 1), pages)
    if n == 0 then items[#items + 1] = { label = T("（黑名单是空的）", "(THE BLACKLIST IS EMPTY)") } end
    for i = (OPP.pg - 1) * OPP.PER + 1, math.min(OPP.pg * OPP.PER, shown) do
        local mode, at, nick = (lb["bl" .. i] or ""):match("^([^|]*)|(%d+)|(.*)$")
        if mode then
            items[#items + 1] = { label = T("解除：", "UNBLOCK: ") .. clipText(nick, 16) .. "  " .. netModeName(mode) .. "  " .. os.date("%m-%d", tonumber(at) or 0),
                                  act = function() netuiCmd("unblock", "i=" .. i .. "\n"); netuiFlash(T("已解除", "UNBLOCKED")) end }
        end
    end
    if OPP.pg < pages then items[#items + 1] = { label = T("下一页", "NEXT PAGE"), nav = "next", act = function() OPP.pg = OPP.pg + 1; NETUI.sel = 1 end } end
    if OPP.pg > 1 then items[#items + 1] = { label = T("上一页", "PREVIOUS PAGE"), nav = "prev", act = function() OPP.pg = OPP.pg - 1; NETUI.sel = 1 end } end
    items[#items + 1] = { label = T("返回", "BACK"), act = page("opp") }
end



-- ============================================================ F3b-5：面板内容拼装的骨架（原 main.lua netuiStatus / netuiRows / netuiItems / NETUI.step / setMode）
-- 页按名字注册：U.page(name, items, title, head)。items(lb, tn, items, page, mode) 往 items 里加行，返回 false = 交给默认内容；
-- title = { zh, en } 页名；head(lb, tn) → 页名, 标题行, 视图附加字段（翻页栏用）。U.pagePrefixReg 按前缀注册（扩展页 ext / ext:*）。
-- 默认内容（主页 / 房间列表 / 系统设置 / 房间设置）由适配层 U.defaultItems(lb, tn, items, page, mode, settingsBtns) 给。
-- 状态行：U.statusPre 前置钩子列表（返回文字就盖过其他状态）；U.pingMs 往返毫秒。每次拼装前跑 U.pollHooks（扩展包轮询）。
U.pages, U.pagePrefix, U.statusPre, U.pollHooks = {}, {}, {}, {}
-- F4b：连接方式注册。d = { key, zh, en, after（标签插在哪个方式之后，默认末尾）, tab = { zh, en }（鼠标面板标签文字，默认同名字）, viaServer（底下走服务器大厅那一套）,
-- noRooms = { zh, en }（房间列表空时的话）, hostLabel = { zh, en }（开房按钮）, rows = fn(lb, items, page, inRoom)（主页上这个方式自己的行）, noAiRoom（不能当 AI 等人房间）,
-- inRoom = fn(lb)（房间里 lobby 的 mode 不是它时，怎么认出这间房是它的，比如排位的 rk=1）,
-- consent = { key, text = { ver, zh, en }, title = { zh, en }, perServer, after }（同意键：切到这个方式 / 开房 / 加入都要先同意；文案进隐私说明页，排在 after 之后） }
U.GUI_TAB, U.MODE_HOST_LABEL, U.MODE_ROWS, U.MODE_NO_AIROOM, U.MODE_INROOM = GUI_TAB, MODE_HOST_LABEL, MODE_ROWS, MODE_NO_AIROOM, MODE_INROOM
U.svc = NETUI.svc     -- F4c-2：文案里的 {SVC} → 桥接服务显示名
-- 房间里（连着 / 开着房）这间房算不算某个注册方式的：lobby 的 mode 等于它，或它自己的 inRoom(lb) 说是（排位房间 lobby 的 mode 可能是 server，靠 rk=1 认）
function U.modeRowsInRoom(lb, items, page)
    for key, fn in pairs(MODE_ROWS) do
        local chk = MODE_INROOM[key]
        if lb.mode == key or (chk and chk(lb)) then fn(lb, items, page, true) end
    end
end
U.GUI_ROWS = GUI_ROWS     -- 鼠标面板一页几行（测试用）
function U.addMode(d)
    MODE_NAME[d.key] = { d.zh, d.en }
    GUI_TAB[d.key] = d.tab or { d.zh, d.en }
    if d.viaServer then VIA_SERVER[d.key] = true end
    if d.noRooms then NOROOMS[d.key] = d.noRooms end
    if d.hostLabel then MODE_HOST_LABEL[d.key] = d.hostLabel end
    if d.rows then MODE_ROWS[d.key] = d.rows end
    if d.inRoom then MODE_INROOM[d.key] = d.inRoom end
    if d.noAiRoom then MODE_NO_AIROOM[d.key] = true end
    local tabs, at = NETUI.TABS, #NETUI.TABS + 1
    for _, m in ipairs(tabs) do if m == d.key then return end end     -- 重复注册不加第二次
    for i, m in ipairs(tabs) do if m == d.after then at = i + 1 end end
    table.insert(tabs, at, d.key)
    local c = d.consent
    if c then
        CONSENT.MODE_KEY[d.key] = c.key
        if c.text then
            CONSENT.TEXT[c.key] = c.text
            if c.owner then c.text.ext = c.owner end     -- 11-13：归扩展包（排位）——不进基底隐私说明，问的时候去 ext:<owner> 插件页
            if c.owner and c.mod then c.text.mod = true end     -- 11-14：排位现在是玩法 mod：隐私说明页也列
        end
        if c.title then CONSENT.PRIVACY_TITLE[c.key] = c.title end
        if c.perServer then CONSENT.PER_SERVER[c.key] = true end
        if c.owner then return end
        local o, pos = CONSENT.PRIVACY_ORDER, #CONSENT.PRIVACY_ORDER + 1
        for i, k in ipairs(o) do if k == (c.after or "server") then pos = i + 1 end end
        table.insert(o, pos, c.key)
    end
end
-- 11-14：玩法 mod 登记自己的同意项（不是连接方式的那种：多人 mp、观战 spec）。d = { key, title = {zh, en}, text = {ver, zh, en}, perServer }；
-- owner = mod 名（manifest 里也要写 consent=@<key>，"已装的玩法"页按它列）。归 mod 的项不进基底 PRIVACY.md，问的时候走确认页
function U.addConsent(d, owner)
    if type(d) ~= "table" or type(d.key) ~= "string" or type(d.text) ~= "table" then error("addConsent：要 { key, title, text = { ver, zh, en } }", 2) end
    d.text.ext = owner or d.owner or "?"
    d.text.mod = true     -- 玩法 mod 的项（隐私说明页也列）；插件的项没有这个标记
    CONSENT.TEXT[d.key] = d.text
    if d.title then CONSENT.PRIVACY_TITLE[d.key] = d.title end
    if d.perServer then CONSENT.PER_SERVER[d.key] = true end
end
function U.page(name, items, title, head) U.pages[name] = { items = items, title = title, head = head } end
function U.pagePrefixReg(prefix, items, title, head) U.pagePrefix[prefix] = { items = items, title = title, head = head } end
local function pageReg(p)
    local r = U.pages[p]
    if r then return r end
    for pre, rr in pairs(U.pagePrefix) do if p:sub(1, #pre) == pre then return rr end end
    return nil
end
-- 核心自己的页
U.page("consent", function(lb, tn, items, page) CONSENT.items(lb, items, page) end)            -- 0.34 二次确认页
U.page("recent", function(lb, tn, items, page) RECENT.items(lb, items, page) end)              -- 0.34 最近一起玩过的人
U.page("bfriends", function(lb, tn, items, page) BFRIENDS.items(lb, items, page) end)          -- 桥接好友
U.page("privacy", function(lb, tn, items, page) CONSENT.privacyItems(lb, items, page) end)     -- 0.34 隐私说明
U.page("opp", function(lb, tn, items, page) OPP.oppItems(lb, items, page) end)                 -- 拉黑
U.page("oppact", function(lb, tn, items, page) OPP.actItems(lb, items, page) end)
U.page("blist", function(lb, tn, items, page) OPP.listItems(lb, items, page) end)
U.page("ctext", CONSENT.textPage, { "联机 · 条款全文", "ONLINE · TERMS" })     -- 11-15 条款全文（插件页 / 玩法页点标题进来）

local function netuiItems(lb, tn)
    local items = {}
    if not lobbyAlive(lb) then
        if not netuiStarting() then
            items[#items + 1] = { label = T("重新启动联机程序", "RESTART THE ONLINE HELPER"),
                                  act = function()     -- §390：手动重试要真的再试一次 DLL（以前 dllOff 一旦置上，按多少次都只往死路上撞）
                                      NETUI.tries, NETUI.dllTries, NETUI.dllOff = 0, 0, nil
                                      netuiLaunch() end }
            -- 说明行：讲真正该怎么办。发布包没有 <退路 exe>（联机程序是游戏里的 fornet_lobby.dll），
            -- 只有 exe 真在时才叫人手动打开它
            local exe = io.open((ROOT .. "/" .. LOBBY_EXE):gsub("/", "\\"), "rb")
            if exe then exe:close() end
            local md = "Mods\\" .. (tostring(ROOT):match("([^/\\]+)[/\\]*$") or "?")     -- F5：模组目录名不写死
            local why = NETUI.launchWhy
            local hint
            if exe then hint = T("（还不行：手动打开 " .. md .. "\\" .. LOBBY_EXE .. "）", "(STILL NOT: OPEN " .. md .. "\\" .. LOBBY_EXE .. ")")
            elseif why == "appcontrol" then hint = T("（Windows 拦的：关掉“智能应用控制”后重启游戏）", "(BLOCKED BY WINDOWS: TURN OFF SMART APP CONTROL, THEN RESTART)")
            elseif why == "missing" then hint = T("（从压缩包重新解压 fornet_lobby.dll，杀软里放行）", "(RE-EXTRACT fornet_lobby.dll AND ALLOW IT IN YOUR ANTIVIRUS)")
            elseif why == "noloadlib" then hint = T("（换影之刃零专用版 UE4SS）", "(USE THE PHANTOM BLADE ZERO UE4SS BUILD)")
            elseif why == "silent" then hint = T("（原因看 " .. md .. "\\net\\lobby_errors.log；重启游戏再试）", "(SEE " .. md .. "\\net\\lobby_errors.log; RESTART AND RETRY)")
            else hint = T("（重启游戏；详情看 logs\\<日期>\\mp_probe.log）", "(RESTART THE GAME; DETAILS IN logs\\<date>\\mp_probe.log)") end
            items[#items + 1] = { label = hint, col = (why == "appcontrol") and COL_RED or nil }
        end
        return items
    end
    local mode = netuiMode(lb, tn)
    local function page(p) return function() NETUI.page = p; NETUI.sel = 1 end end
    -- 每页底下：通用对局规则…（原"房间设置…"，11-18 用户改名）在 系统设置… 上面
    local function settingsBtns()
        items[#items + 1] = { label = T("通用对局规则…", "GENERAL MATCH RULES..."), act = page("room") }
        items[#items + 1] = { label = T("系统设置…", "SYSTEM SETTINGS..."), act = page("settings") }
    end
    for _, h in ipairs(U.pollHooks) do pcall(h, lb) end     -- 原来只在没被前面的页截走时跑（X1 ST.ext.poll）；现在每次都跑
    local r = pageReg(NETUI.page)
    if r and r.items(lb, tn, items, page, mode) ~= false then return items end
    if U.defaultItems then     -- 默认内容可以返回另一份 items（原 netuiItems 里"联机了就回主页"那条是 return netuiItems(lb, tn)）
        local r2 = U.defaultItems(lb, tn, items, page, mode, settingsBtns)
        if type(r2) == "table" then return r2 end
    end
    return items
end

-- 状态行：文字 + 颜色
local function netuiStatus(lb, tn)
    for _, h in ipairs(U.statusPre) do local t, c = h(lb, tn); if t then return t, c end end     -- F3b-5：前置钩子（Phantom Pact 共存等，适配层注册；返回文字就盖过其他状态）
    if not lobbyAlive(lb) then
        if netuiStarting() then
            return T("正在启动联机程序…", "STARTING THE ONLINE HELPER..."), COL_ORANGE
        end
        if NETUI.launchErr then
            return T("联机程序启动失败：", "HELPER FAILED: ") .. clipText(NETUI.launchErr, 24), COL_RED
        end
        return T("联机程序没响应", "THE ONLINE HELPER IS NOT RESPONDING"), COL_RED
    end
    local b = NETUI.busy
    if b then return T(b.zh, b.en) .. ({ ".", "..", "..." })[math.floor(tn * 3) % 3 + 1], COL_ORANGE end   -- 点会动
    if (lb.err or "") ~= "" then return clipText(lb.err, 34), COL_RED end
    -- 安全模式（olevel 2）：红字一直显示原因（服务器下线标志 L1 X3 删了）
    if (lb.olevel or "0") ~= "0" and (lb.omsg or "") ~= "" and lb.connected ~= "1" then return clipText(lb.omsg, 34), lb.olevel == "2" and COL_RED or COL_ORANGE end
    -- v0.22.0：密码对不上 / 版本不一致的提示（联机程序给的短句）；没连上时红色，连着时橙色
    if (lb.hint or "") ~= "" then return clipText(lb.hint, 34), lb.connected == "1" and COL_ORANGE or COL_RED end
    if NETUI.flash and os.time() < NETUI.flash.till then return NETUI.flash.text, NETUI.flash.col or COL_GREEN end
    if lb.mode == "bridge" and lb.bridge ~= "ok" then return clipText(lb.bridge, 34), COL_RED end
    -- 服务器大厅：服务器没回应 / 没这个房间 / 没填地址
    if NETUI.viaServer(lb.mode) and (lb.srverr or "") ~= "" and lb.connected ~= "1" then return clipText(lb.srverr, 34), COL_RED end
    local role, conn = lb.role or "none", lb.connected == "1"
    if role == "none" then
        if lb.mode == "ai" then return T("未开始  和本机 AI 对战，不联网", "NOT STARTED  OFFLINE, AGAINST THE COMPUTER"), COL_GRAY end
        return T("未联机", "OFFLINE") .. "  " .. clipText(lb.myaddr, 40), COL_GRAY
    end
    local who = (lb.peername or "") ~= "" and lb.peername or (lb.peer or "")
    local route = NETUI.viaServer(lb.mode) and netRouteName(lb.route) or nil
    if conn then
        local ping = (U.pingMs and U.pingMs()) or tonumber(lb.ping) or -1     -- F3b-5：往返毫秒由适配层给（LAT），没有就用联机程序的
        local ha = (lb.mp == "1" and (lb.room or "") ~= "") and (T("  房间号 ", "  ROOM ") .. lb.room)
                   or (lb.mp == "1" and (lb.mphostaddr or "") ~= "") and (T("  房主地址 ", "  HOST ") .. clipText(lb.mphostaddr, 28)) or ""
        return T("已连接 ", "CONNECTED ") .. clipText(who, 18) .. (ping >= 0 and string.format("  %dms", ping) or "")
               .. (route and ("  " .. route) or "") .. ha
               .. (lb.aiwait == "1" and (T("  房间开着：", "  ROOM: ") .. clipText(lb.aiwaitaddr, 22)) or ""), COL_GREEN
    end
    -- 实测：多人房里 connected 是旧 1v1 的"有对手包"，对决没人上场时为 0 → 4 人的房间房主写"等对方加入"、客机写"正在连接"。
    -- 房里有别人就按多人房显示
    local mpn = tonumber(lb.mpn) or 0
    if lb.mp == "1" and mpn >= 2 then
        -- 服务器大厅开的房有房间号（lobby.txt room=）就显示房间号（发给朋友用），没有才显示房主地址
        local ha = ((lb.room or "") ~= "") and (T("  房间号 ", "  ROOM ") .. lb.room)
                   or ((lb.mphostaddr or "") ~= "") and (T("  房主地址 ", "  HOST ") .. clipText(lb.mphostaddr, 28)) or ""
        return string.format(T("房间 %d 人", "ROOM: %d"), mpn) .. ha, COL_GREEN
    end
    if role == "host" then
        local ha = (lb.mp == "1" and (lb.room or "") ~= "") and (T("房间号 ", "ROOM ") .. lb.room)
                   or (lb.mp == "1" and (lb.mphostaddr or "") ~= "") and (T("房主地址 ", "HOST ") .. clipText(lb.mphostaddr, 28)) or clipText(lb.myaddr, 22)
        return T("主机：等对方加入  ", "HOSTING: WAITING  ") .. ha, COL_ORANGE
    end
    if route then return T("正在连接（", "CONNECTING (") .. route .. T("）", ")"), COL_ORANGE end
    return T("正在连接 ", "CONNECTING ") .. clipText(who, 24), COL_ORANGE
end

local function netuiRows(tn)
    local lb = netuiRead(tn)
    if NETUI.edit then
        editTick()
        local e = NETUI.edit
        if e then
            NETUI.view = { edit = true, title = T("联机", "ONLINE"), prompt = e.prompt }
            -- 第 3 行留空给输入框（umgHud 记下每行的位置，输入框摆到这一行）
            local rows = { { c = T("联机   [Home] 关闭", "ONLINE   [HOME] CLOSE"), col = COL_LABEL, size = 0.8 },
                           { c = e.prompt, col = COL_WHITE, size = 0.7 },
                           { c = " ", col = COL_WHITE, size = 1.0 },
                           { c = T("回车 确定   Esc 取消   Ctrl+V 粘贴", "ENTER OK   ESC CANCEL   CTRL+V PASTE"), col = COL_LABEL, size = 0.55 } }
            return rows
        end
    end
    NETUI.busyStep(lb, tn)
    local items = netuiItems(lb, tn)
    -- 等回话时按钮点不了；off = 本来是按钮（鼠标面板照样画按钮底、整个变暗，不像说明行那样只剩字）
    if NETUI.busy then for _, it in ipairs(items) do if it.act then it.act, it.off = nil, true end end end
    -- 房间列表页：刷新后光标跟着同一个房间（按 target 认）；房间没了就留在原位置
    if NETUI.page == "rooms" and NETUI.selTarget then
        for i, it in ipairs(items) do
            if it.target == NETUI.selTarget and it.act then NETUI.sel = i; break end
        end
    end
    if NETUI.page == "rooms" and NETUI.roomsFresh and not NETUI.selTarget then
        for i, it in ipairs(items) do
            if it.room and it.act then NETUI.sel, NETUI.roomsFresh = i, nil; break end
        end
    end
    NETUI.items = items
    if NETUI.sel > #items then NETUI.sel = #items end
    if NETUI.sel < 1 then NETUI.sel = 1 end
    -- 光标落在选不中的行（说明行、已满的房间：比如翻到全是已满房间的那一页）就往下找第一个能选的，没有再往上找
    if items[NETUI.sel] and not items[NETUI.sel].act then
        local found
        for i = NETUI.sel, #items do if items[i].act then found = i; break end end
        if not found then for i = NETUI.sel, 1, -1 do if items[i].act then found = i; break end end end
        NETUI.sel = found or NETUI.sel
    end
    local st, col = netuiStatus(lb, tn)
    local title = T("联机   [Home] 关闭", "ONLINE   [HOME] CLOSE")
    -- F3b-5：注册过的页由注册方给标题 / 标题行 / 视图附加字段（head），没 head 的用 title；核心自己的页在下面的链里
    local reg, headExtra = pageReg(NETUI.page), nil
    local name
    if reg and reg.head then
        local n, t, extra = reg.head(lb, tn)
        name, headExtra = n, extra
        if t then title = t end
    elseif reg and reg.title then name = T(reg.title[1], reg.title[2]) end
    name = name or (NETUI.page == "settings" and T("联机 · 系统设置", "ONLINE · SYSTEM SETTINGS")
              or NETUI.page == "settings:net" and T("系统设置 · 网络与连接", "SETTINGS · NETWORK")     -- 11-15 系统设置分类
              or NETUI.page == "settings:match" and T("系统设置 · 对局", "SETTINGS · MATCH")
              or NETUI.page == "settings:ui" and T("系统设置 · 界面与语言", "SETTINGS · DISPLAY & LANGUAGE")
              or NETUI.page == "room" and T("联机 · 房间设置", "ONLINE · ROOM RULES")
              or (NETUI.page == "opp" or NETUI.page == "oppact") and T("联机 · 拉黑", "ONLINE · BLOCK")
              or NETUI.page == "blist" and T("联机 · 黑名单", "ONLINE · BLACKLIST")
              or NETUI.page == "consent" and T("联机 · 请确认", "ONLINE · PLEASE CONFIRM")
              or NETUI.page == "recent" and T("联机 · 最近", "ONLINE · RECENT")
              or NETUI.page == "bfriends" and T("联机 · ", "ONLINE · ") .. NETUI.friendsName()
              or NETUI.page == "privacy" and T("联机 · 隐私说明", "ONLINE · PRIVACY")
              or T("联机", "ONLINE"))
    if NETUI.page == "rooms" then
        local _, allRooms = netuiTargets(lb)
        local rooms = filterRooms(allRooms)
        title = string.format(T("房间列表  第 %d/%d 页   [Home] 关闭", "ROOMS  PAGE %d/%d   [HOME] CLOSE"), NETUI.roomPage or 1, roomPages(rooms))
        local open = 0
        for _, r in ipairs(rooms) do if roomOpen(r) then open = open + 1 end end
        name = string.format(T("房间列表（%d 个，可加入 %d）", "ROOM LIST (%d, %d OPEN)"), #rooms, open)   -- 页码在鼠标面板底下的翻页栏
    end
    -- 鼠标面板用的同一份内容（guiShow 读）
    NETUI.view = { title = name, status = st, col = col, items = items, mode = lobbyAlive(lb) and netuiMode(lb, tn) or nil,
                   rooms = NETUI.page == "rooms" and lobbyAlive(lb), noq = lb ~= nil and lb.specnoq == "1", roomPage = NETUI.roomPage or 1, busy = NETUI.busy ~= nil }
    if NETUI.view.rooms then NETUI.view.pages = roomPages(filterRooms(select(2, netuiTargets(lb)))) end
    if NETUI.page == "privacy" then     -- 0.34 隐私说明分页：上一页 / 下一页 / 返回 走底下的翻页栏（不然行数上限之外的按钮画不出来）
        NETUI.view.priv, NETUI.view.roomPage, NETUI.view.pages = true, NETUI.privPg or 1, NETUI.privPages or 1
    end
    -- 排行榜页也用底下的翻页栏（rank = true；页码来自 lobby.txt 的 rktop）
    if headExtra then for k, v in pairs(headExtra) do NETUI.view[k] = v end end     -- F3b-5：注册页的翻页栏 / 页码等（排行榜 rank=、规则 rules=、统计 stat=）
    local rows = { { c = title, col = COL_LABEL, size = 0.8 },
                   { c = st, col = col, size = 0.7 } }
    NETUI.selTarget = items[NETUI.sel] and items[NETUI.sel].target or nil
    -- 项目比屏幕行多（房间设置页最多 15 项 + 标题 / 状态 / 说明 = 18 > HUD_ROWS）：只显示选中项附近一段，
    -- 上下各留一行"还有 N 项"，PgUp/PgDn 走到哪段就显示哪段（以前是截掉最后几项，"返回"选得到却看不见）
    local cap, first, last = HUD_ROWS - 3, 1, #items
    if #items > cap then
        local w = cap - 2
        first = math.max(1, math.min((NETUI.sel or 1) - w // 2, #items - w + 1))
        last = first + w - 1
        rows[#rows + 1] = first > 1 and { c = T("（上面还有 " .. (first - 1) .. " 项）", "(" .. (first - 1) .. " MORE ABOVE)"), col = COL_GRAY, size = 0.55 } or nil
    end
    for i = first, last do
        local it = items[i]
        local sel = i == NETUI.sel and it.act
        rows[#rows + 1] = { c = sel and ("> " .. it.label .. " <") or it.label, col = sel and COL_WHITE or itemCol(it) or COL_GRAY, size = 0.7 }
    end
    if last < #items then
        rows[#rows + 1] = { c = T("（下面还有 " .. (#items - last) .. " 项）", "(" .. (#items - last) .. " MORE BELOW)"), col = COL_GRAY, size = 0.55 }
    end
    rows[#rows + 1] = { c = T("PgUp/PgDn 选择   End 确定 / 换方式", "PGUP/PGDN SELECT   END OK / CHANGE VIA"),
                        col = COL_LABEL, size = 0.55 }
    while #rows > HUD_ROWS do table.remove(rows, #rows - 1) end
    return rows
end

-- 换连接方式（d = ±1）
function NETUI.step(d)
    local lb = NETUI.lobby
    if not lobbyAlive(lb) or NETUI.busy then return end
    if (lb.role or "none") ~= "none" then out("联机面板：联机中不能换连接方式，先断开"); return end
    local cur, shown = 1, netuiMode(lb, now())
    local ms = NETUI.modes()
    if NETUI.friendsTab(lb) then     -- 11-15 好友标签排在局域网后面（同 NETUI.TABS）
        local l, at = {}, nil
        for i, m in ipairs(ms) do l[#l + 1] = m; if m == "lan" then at = i + 1 end end
        table.insert(l, at or #l + 1, "friends")
        ms = l
        if NETUI.page == "bfriends" then shown = "friends" end
    end
    for i, m in ipairs(ms) do if m == shown then cur = i end end
    NETUI.pickTab(ms[(cur - 1 + d) % #ms + 1])
end

-- 11-15 点标签 / 换方式：friends 只换到好友页；别的方式回首页，和现在的方式不一样才让联机程序换
function NETUI.pickTab(m)
    local lb = NETUI.lobby
    if m == "friends" then
        if NETUI.friendsTab(lb) then NETUI.page, NETUI.sel = "bfriends", 1 end
        return
    end
    if NETUI.page == "bfriends" then NETUI.page = "main" end
    if lobbyAlive(lb) and m ~= netuiMode(lb, now()) then NETUI.setMode(m) end
end

-- 直接换到某种连接方式（鼠标面板点标签页）
function NETUI.setMode(m)
    local lb = NETUI.lobby
    if not lobbyAlive(lb) or NETUI.busy then return end
    NETUI.listFrom = now()
    if (lb.role or "none") ~= "none" then out("联机面板：联机中不能换连接方式，先断开"); return end
    NETUI.want = { mode = m, at = now() }
    netuiCmd("mode", "mode=" .. m .. "\n")
end

-- ---- v0.20.0 鼠标面板（说明见 GUI 的定义处）

-- 导出（main.lua 取别名；名字和搬之前一样）
U.netuiStatus, U.netuiRows, U.netuiItems = netuiStatus, netuiRows, netuiItems     -- F3b-5
U.netuiItemCol = itemCol     -- 11-18 结束 / 退出类按钮的红字（main.lua 的鼠标面板取）
U.upnpFit, U.upnpClip = upnpFit, upnpClip     -- F3b-4
U.CONSENT, U.EXTCTX, U.OPP = CONSENT, EXTCTX, OPP     -- F3b-4
U.RECENT = RECENT     -- F3b-3
-- F3b-2 输入框 / GUI
U.GUI = GUI
U.editBox = editBox
U.editCommit = editCommit
U.editEnd = editEnd
U.editStart = editStart
U.editTick = editTick
U.inputMode = inputMode
U.joinItem = joinItem
U.netuiFlash = netuiFlash
U.netuiTargets = netuiTargets
U.perPage = perPage
U.roomOpen = roomOpen
U.roomPages = roomPages
U.filterRooms = filterRooms
NETUI.filterRooms = filterRooms
U.BG_DOWN = BG_DOWN
U.BG_HOVER = BG_HOVER
U.BG_NONE = BG_NONE
U.BG_OFF = BG_OFF
U.BG_ROW = BG_ROW
U.BG_SEL = BG_SEL
U.guiBg = guiBg
U.guiClicked = guiClicked
U.guiLabel = guiLabel
U.guiRoot = guiRoot
U.guiVis = guiVis
U.netuiSetOpen = netuiSetOpen
U.NETUI = NETUI
U.NETUI_QUICK = NETUI_QUICK
U.NETUI_PER_PAGE = NETUI_PER_PAGE
U.GUI_PER_PAGE = GUI_PER_PAGE
U.netModeName = netModeName
U.netRouteName = netRouteName
U.clipText = clipText
U.lobbyAlive = lobbyAlive
U.netuiRead = netuiRead
U.netuiCmd = netuiCmd
U.netuiLaunch = netuiLaunch
U.netuiStarting = netuiStarting
U.netuiEnsure = netuiEnsure
U.netuiMode = netuiMode
return U
end
