-- fornet_host.lua：4net 宿主入口（F6， 第 3 节）。部署成 <模组目录>\Scripts\main.lua——UE4SS 只认这个文件。
-- 它不认识任何游戏：找到 games\<名字>\ 下的"游戏适件"（manifest.txt），查版本，把 Scripts\ 和适件目录放进 package.path，
-- 交出全局 FORNET（模组目录 / 适件目录 / 版本 / 日志），然后执行适件的入口。适件是全权限的（要碰游戏对象），一个安装只装一个；
-- 受限的第三方扩展包另走 ext\（fornet_ext.lua），和这里无关。
--
-- 适件 manifest.txt（key=value，一行一个）：
-- name=<名字> 必填，和目录名一致
-- entry=<文件> 入口脚本，默认 main.lua
-- needsFornet=<x.y.z> 需要的宿主版本下限（可选）
-- title=<显示名> 日志里用（可选）
-- 选哪个适件：games\games.txt 第一行非注释的名字；没有就按目录名排序取第一个（有多个时记一行日志）。
-- 列目录优先 games\games.txt（没有 io.popen 的环境 / 想固定顺序），没有再用 dir /b /ad。
local HOST_VERSION = "0.3.0"     -- 0.2.0：F6 第二步（FORNET.mods / adopt / loop / 接口表检查）；0.3.0：玩法 mod（mods\，fornet_mods.lua， L1）

local function log(fmt, ...)
    local ok, s = pcall(string.format, fmt, ...)
    print("[fornet] " .. (ok and s or tostring(fmt)) .. "\n")
end

-- 模组目录 = 本文件所在的 Scripts\ 的上一级。先看本文件路径，再看 UE4SS 放进 package.path 的 Scripts 目录
local function modRoot()
    local cands = {}
    local okI, info = pcall(debug.getinfo, 1, "S")
    local src = okI and info and info.source or ""
    local d = src:gsub("^@", ""):gsub("\\", "/"):match("^(.*)/[Ss]cripts/[^/]+$")
    if d then cands[#cands + 1] = d end
    for entry in tostring(package.path or ""):gmatch("[^;]+") do
        local e = entry:gsub("\\", "/"):match("^(.*)/[Ss]cripts/%?%.lua$")
        if e then cands[#cands + 1] = e end
    end
    for _, c in ipairs(cands) do
        local f = io.open(c .. "/Scripts/main.lua", "r")
        if f then f:close(); return c end
    end
    return cands[1]
end

local function readKV(path)
    local f = io.open(path, "r")
    if not f then return nil end
    local t = {}
    for line in f:lines() do
        local k, v = line:match("^%s*([%w_]+)%s*=%s*(.-)%s*$")
        if k then t[k] = v end
    end
    f:close()
    return t
end

local function verCmp(a, b)
    local function parts(s) local t = {}; for n in tostring(s):gmatch("%d+") do t[#t + 1] = tonumber(n) end; return t end
    local x, y = parts(a), parts(b)
    for i = 1, math.max(#x, #y) do
        local p, q = x[i] or 0, y[i] or 0
        if p ~= q then return p < q and -1 or 1 end
    end
    return 0
end

local function listGames(dir)
    local names, seen = {}, {}
    local function add(n)
        n = (n or ""):gsub("^%s+", ""):gsub("%s+$", "")
        if n:match("^[%w_%-]+$") and not seen[n] then seen[n] = true; names[#names + 1] = n end
    end
    local f = io.open(dir .. "/games.txt", "r")
    if f then
        for line in f:lines() do if not line:match("^%s*#") then add(line) end end
        f:close()
        return names, "games.txt"
    end
    local okP, p = pcall(io.popen, 'dir /b /ad "' .. dir:gsub("/", "\\") .. '" 2>nul')
    if okP and p then
        for line in p:lines() do add(line) end
        p:close()
        table.sort(names)
        return names, "dir"
    end
    return names, "none"
end

local ROOT = modRoot()
if not ROOT then
    log("!! 找不到模组目录（本文件不在 <模组>\\Scripts\\ 下？），什么也不做")
    return
end
local GAMES = ROOT .. "/games"
local names, how = listGames(GAMES)
if #names == 0 then
    log("没有游戏适件（%s\\games\\<名字>\\manifest.txt），只装宿主，什么也不做", ROOT)
    return
end
local pick = names[1]
if #names > 1 then log("有 %d 个游戏适件（%s），用 %s（按 %s；改 games\\games.txt 第一行可换）", #names, table.concat(names, ", "), pick, how) end
local GDIR = GAMES .. "/" .. pick
local mf = readKV(GDIR .. "/manifest.txt")
if not mf then log("!! 游戏适件 %s 没有 manifest.txt，跳过", pick); return end
if mf.name and mf.name ~= pick then log("!! 游戏适件目录 %s 和 manifest 的 name=%s 不一致，跳过", pick, mf.name); return end
if mf.needsFornet and verCmp(HOST_VERSION, mf.needsFornet) < 0 then
    log("!! 游戏适件 %s 需要宿主 %s 以上（现在 %s），跳过", pick, mf.needsFornet, HOST_VERSION); return
end
local entry = mf.entry and mf.entry ~= "" and mf.entry or "main.lua"
if not entry:match("^[%w_%-%.]+%.lua$") then log("!! 游戏适件 %s 的 entry=%s 不合法，跳过", pick, entry); return end

-- 适件目录排在最前：适件自己的模块名（含它带的同名覆盖）优先，fornet_* 在 Scripts\
package.path = GDIR .. "/?.lua;" .. ROOT .. "/Scripts/?.lua;" .. (package.path or "")

FORNET = {
    VERSION = HOST_VERSION,
    ROOT = ROOT,                  -- 模组目录：net\ rec\ logs\ ext\ 和联机程序 DLL 都在这
    GAME = pick,
    GAME_DIR = GDIR,              -- 适件目录：适件自己的脚本 / 数据
    SCRIPTS = ROOT .. "/Scripts", -- fornet_*.lua
    manifest = mf,
    log = log,
    verCmp = verCmp,
    mods = {},                    -- F6 第二步：网络核模块的实例（适件装时经 adopt 登记），扩展 / 工具按名字拿：FORNET.mods.fornet_match
    loop = {},                    -- F6 第二步：调度机制（主循环 / 每帧 / 异步），适件只给"每拍做什么"
}

-- 适件装网络核模块：local ok, mk = pcall(require, "fornet_xxx"); mk = FORNET.adopt("fornet_xxx", mk); mk(ctx)
-- ctx 是"这款游戏给这个模块的钩子"（只有适件知道），所以模块还是适件来构造；宿主管登记，同一个模块只构造一次
function FORNET.adopt(name, mk)
    if type(mk) ~= "function" then return mk end
    return function(ctx)
        if FORNET.mods[name] ~= nil then return FORNET.mods[name] end
        local inst = mk(ctx)
        FORNET.mods[name] = inst
        return inst
    end
end

-- 主循环：每帧检查、按时钟累加到点就跑 tick（平均 min(帧率, 1/sec) 次 / 秒；落后超过 0.1 秒不补）。
-- alive 为假时这个循环自己停（看门狗重启时旧循环靠它退出）；idle 每帧在判断到点之前调（空帧里做杂事，比如发上一拍剩下的包）。
-- 没有每帧回调时退到固定间隔，再没有退到异步链。返回用上的方式 "frame" / "delay" / "async"，都没有返回 nil。
function FORNET.loop.paced(sec, tick, alive, idle)
    if type(LoopInGameThreadAfterFrames) == "function" then
        local due = 0.0
        LoopInGameThreadAfterFrames(1, function()
            if not alive() then return true end
            local c = os.clock()
            if idle then idle() end
            if c < due then return end
            due = (c - due > 0.1) and (c + sec) or (due + sec)
            tick()
        end)
        return "frame"
    end
    local ms = math.floor(sec * 1000 + 0.5)
    if type(LoopInGameThreadWithDelay) == "function" then
        LoopInGameThreadWithDelay(ms, tick)
        return "delay"
    end
    if type(ExecuteWithDelay) ~= "function" then return nil end
    local function chain()
        if not alive() then return end
        tick()
        pcall(function() ExecuteWithDelay(ms, chain) end)
    end
    pcall(function() ExecuteWithDelay(1000, chain) end)
    return "async"
end

-- 每帧（游戏线程）；没有这个能力返回 false
function FORNET.loop.frame(fn)
    if type(LoopInGameThreadAfterFrames) ~= "function" then return false end
    LoopInGameThreadAfterFrames(1, fn)
    return true
end

-- 异步线程定时（看门狗之类）；没有返回 false
function FORNET.loop.async(ms, fn)
    if type(LoopAsync) ~= "function" then return false end
    LoopAsync(ms, fn)
    return true
end

-- 适件接口表（docs/FORNET_ADAPTER.md 第 4 节）：适件在入口里设 FORNET.adapter；这里只检查、记日志
local IFACE = { "name", "version", "tick", "makePacket", "applyPacket", "spawnPuppet", "removePuppet", "matchDeps", "onMapChange" }
FORNET.IFACE = IFACE
local function checkAdapter()
    local a = FORNET.adapter
    if type(a) ~= "table" then log("!! 游戏适件 %s / 基础联机包没有设 FORNET.adapter（接口表），按老办法跑", pick); return end
    local miss = {}
    for _, k in ipairs(IFACE) do if a[k] == nil then miss[#miss + 1] = k end end
    local mods = {}
    for k in pairs(FORNET.mods) do mods[#mods + 1] = k end
    table.sort(mods)
    log("游戏适件 %s v%s 接口表%s；网络核模块 %d 个（%s）", tostring(a.name), tostring(a.version),
        #miss == 0 and "齐" or ("缺 " .. table.concat(miss, " / ")), #mods, table.concat(mods, ", "))
end

-- 0.3.0 玩法 mod 机制：在适配层之前构造，适配层也能 provide / use 服务、发事件
do
    local okM, mk = pcall(dofile, ROOT .. "/Scripts/fornet_mods.lua")
    if okM and type(mk) == "function" then
        local okI, S = pcall(mk, FORNET)
        if okI and type(S) == "table" then
            FORNET.modsys = S
            FORNET.provide = function(name, tbl) return S.provide(pick, name, tbl) end
            FORNET.use = function(name) return (S.use(name)) end
            FORNET.on = function(ev, fn) return S.on(pick, ev, fn) end
            FORNET.emit, FORNET.ask, FORNET.has = S.emit, S.ask, S.has
        else log("!! fornet_mods.lua 初始化失败：%s", tostring(S)) end
    else log("没有 fornet_mods.lua（%s），不装玩法 mod", tostring(mk)) end
end

local chunk, err = loadfile(GDIR .. "/" .. entry)
if not chunk then log("!! 游戏适件 %s 的 %s 载入失败：%s", pick, entry, tostring(err)); return end
log("宿主 %s：装游戏适件 %s%s（%s）", HOST_VERSION, pick, mf.title and ("「" .. mf.title .. "」") or "", entry)
local okR, e = pcall(chunk)
if not okR then log("!! 游戏适件 %s 运行出错：%s", pick, tostring(e)) end
-- 适配层装完再装玩法 mod（它们经适配层操作游戏、经基础联机包装好的 fornet_net 收发）
if FORNET.modsys then
    local okL, n = pcall(FORNET.modsys.loadAll)
    if not okL then log("!! 装玩法 mod 出错：%s", tostring(n)) end
end
-- 接口表（tick / makePacket …）现在由基础联机包设（L2），所以等 mod 都装完再查
pcall(checkAdapter)
