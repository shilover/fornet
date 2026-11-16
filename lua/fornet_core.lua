-- fornet_core.lua —— fornet（4net）UE4SS 网络核的第一块（docs/EXTENSIONS.md §10.4 F3；docs/FORNET_SPLIT.md）。
-- 从游戏模组的 main.lua 原样搬出来的游戏无关部分：文件读写（经管道优先）、§83 / §84 命名管道、中英文切换、本地偏好 PREF。
-- 不碰任何游戏对象（zhFont 只用 UE4SS 的 StaticFindObject / LoadAsset 找字体，字体路径由调用方给）。
-- 用法：local FN = require("fornet_core")({ out=, NET_DIR=, F_LOCAL=, valid=, short=, pathOf=, zhFont= })
--   FN.fileWrite / FN.fileReadKV / FN.PIPE / FN.LANG / FN.langPoll / FN.T / FN.PREF
return function(ctx)
    local out, NET_DIR, F_LOCAL = ctx.out, ctx.NET_DIR, ctx.F_LOCAL
    local valid, short, pathOf = ctx.valid or function(o) return o ~= nil end, ctx.short or tostring, ctx.pathOf or tostring
    local C = {}
    local fileReadKV     -- 下面定义；PIPE.tryConnect 要用

    -- ============================================================ §83 命名管道：游戏 ↔ 联机程序的状态包走内存
    -- 以前每秒各写 30 次 state_local.txt / state_remote.txt：读刚被联机程序换掉的文件要等杀毒扫描（平均 3.6–6.5 ms、
    -- 最长 20+ ms，全压在游戏线程上），还磨硬盘。联机程序（GamePipe.cs，协议见它的文件头）写 net\pipe.txt（name=…），
    -- 这里连 \\.\pipe\<name>.up（写）/ .down（读）。连不上 / 读写出错 → 关掉、退回文件交接，PIPE.RETRY 秒后再试。
    -- Lua 的读是阻塞的：联机程序收到 "R" / "B" 立刻回话（专门的线程，不碰网络）；它退出了管道就断，读到 nil 不会卡住。
    -- §84 以前的小文件也走管道：联机程序给的（DOWN）每次回话时带上变了的；游戏给的（UP）用 "U <文件名>" 发。
    --   断开后联机程序把内存里最新的几份写回硬盘，这边照旧读写文件
    local PIPE = { on = false, retryAt = 0, RETRY = 5.0, name = nil, blob = {}, rev = {}, blobAt = 0, BLOB_SEC = 0.2, ups = {},
                   DOWN = { ["session.txt"] = true, ["lobby.txt"] = true, ["rel_in.txt"] = true, ["lang.txt"] = true },
                   UP = { ["cmd.txt"] = true, ["rel_out.txt"] = true },
                   RESEND = { ["rel_out.txt"] = true } }     -- 重新连上时补发（可靠事件的状态；命令不补，免得执行两次）
    C.PIPE = PIPE
    -- net\ 下的文件名；别的目录的返回 nil
    function PIPE.netName(path)
        local n = path:match("[^/\\]+$")
        if n and path == NET_DIR .. "/" .. n then return n end
        return nil
    end
    function PIPE.kv(data)
        local t = {}
        for k, v in data:gmatch("([%w_]+)=([^\r\n]*)") do t[k] = v end     -- 0.34：键名允许下划线（consent_* 等 PREF 键）
        if next(t) == nil then return nil end
        return t
    end
    function PIPE.close(why)
        if PIPE.up then pcall(function() PIPE.up:close() end) end
        if PIPE.down then pcall(function() PIPE.down:close() end) end
        PIPE.up, PIPE.down = nil, nil
        if PIPE.on then out("管道断开（%s），退回文件交接", tostring(why)); PIPE.lostAt = os.time() end   -- §99 NETUI.watchPipe 看着
        PIPE.on = false
        PIPE.retryAt = os.clock() + PIPE.RETRY
    end
    function PIPE.tryConnect()
        if PIPE.on then return true end
        if os.clock() < PIPE.retryAt then return false end
        PIPE.retryAt = os.clock() + PIPE.RETRY
        local kv = fileReadKV(NET_DIR .. "/pipe.txt")
        local name = kv and kv.name
        if not (name and name:match("^[%w_]+$")) then return false end
        local base = [[\\.\pipe\]] .. name
        local okU, up = pcall(io.open, base .. ".up", "wb")
        if not (okU and up) then return false end
        local okD, down = pcall(io.open, base .. ".down", "rb")
        if not (okD and down) then pcall(function() up:close() end); return false end
        PIPE.up, PIPE.down, PIPE.on, PIPE.name, PIPE.blob, PIPE.blobAt = up, down, true, name, {}, 0
        out("连上联机程序的管道 %s：状态包、联机状态、命令都走内存，不再写文件", name)
        for n, text in pairs(PIPE.ups) do
            if PIPE.RESEND[n] then PIPE.putUp(n, text) end
        end
        return true
    end
    -- 游戏给联机程序的文件（cmd.txt / rel_out.txt）经管道发；没连返回 false（调用方写文件）
    function PIPE.putUp(name, text)
        if not PIPE.tryConnect() then return false end
        local ok, err = pcall(function()
            assert(PIPE.up:write("U ", name, " ", #text, "\n", text))
            assert(PIPE.up:flush())
        end)
        if not ok then PIPE.close("写失败：" .. tostring(err)); return false end
        PIPE.ups[name] = text
        return true
    end
    -- 问一次联机程序（"R" = 状态包 + 变了的文件，"B" = 只要变了的文件），返回状态包列表（kv 表，旧到新）；出错返回 nil
    function PIPE.ask(what)
        local list = {}
        PIPE.waiting = what .. " 等回话"         -- §88 看门狗（另一个线程）据此报"卡在管道"
        local ok, err = pcall(function()
            assert(PIPE.up:write(what, "\n"))
            assert(PIPE.up:flush())
            local h = PIPE.down:read("l")
            local k, m = (h or ""):match("^N (%d+) (%d+)$")
            k, m = tonumber(k), tonumber(m)
            assert(k and m, "回话头不对：" .. tostring(h))
            for _ = 1, k do
                local n = tonumber(PIPE.down:read("l"))
                assert(n, "长度行不对")
                local body = n > 0 and PIPE.down:read(n) or ""
                assert(body and #body == n, "包不完整")
                list[#list + 1] = PIPE.kv(body)
            end
            for _ = 1, m do
                local name, n = (PIPE.down:read("l") or ""):match("^([%w_%.]+) (%d+)$")
                n = tonumber(n)
                assert(name and n, "文件行不对")
                local body = n > 0 and PIPE.down:read(n) or ""
                assert(body and #body == n, "文件不完整")
                if PIPE.blob[name] ~= body then PIPE.rev[name] = (PIPE.rev[name] or 0) + 1 end     -- 换房主等：调用方看版本号变了再解析（不每帧解析）
                PIPE.blob[name] = body
            end
        end)
        PIPE.waiting = nil
        if not ok then PIPE.close("读失败：" .. tostring(err)); return nil end
        PIPE.blobAt = os.clock()
        return list
    end
    -- 读 DOWN 文件前：上次取过超过 BLOB_SEC 秒就问一次（对局里每次驱动的 R 已经顺带取了）
    function PIPE.poll()
        if PIPE.on and os.clock() - PIPE.blobAt >= PIPE.BLOB_SEC then PIPE.ask("B") end
    end
    -- 发本机状态包；没连管道返回 false（调用方写文件）
    function PIPE.send(text)
        if not PIPE.tryConnect() then return false end
        PIPE.waiting = "S 写"
        local ok, err = pcall(function()
            assert(PIPE.up:write("S ", #text, "\n", text))
            assert(PIPE.up:flush())
        end)
        PIPE.waiting = nil
        if not ok then PIPE.close("写失败：" .. tostring(err)); return false end
        return true
    end
    -- 取这段时间收到的远端状态包（kv 表，旧到新，可能是空表）；没连管道返回 nil（调用方读文件）
    function PIPE.recv()
        if not PIPE.tryConnect() then return nil end
        return PIPE.ask("R")
    end

    -- ============================================================ 文件
    local function fileWrite(path, text)
        -- §83 / §84 连着联机程序的管道：本机状态包、cmd.txt、rel_out.txt 走内存，不写文件
        if path == F_LOCAL then
            if PIPE.send(text) then return true end
        else
            local n = PIPE.netName(path)
            if n and PIPE.UP[n] and PIPE.putUp(n, text) then return true end
        end
        local tmp = path .. ".tmp"
        local f = io.open(tmp, "w")
        if not f then return false end
        f:write(text)
        f:close()
        for _ = 1, 3 do
            os.remove(path)
            if os.rename(tmp, path) ~= nil then return true end
        end
        local g = io.open(path, "w")
        if g then
            g:write(text)
            g:close()
            os.remove(tmp)
            return true
        end
        return false
    end
    fileReadKV = function(path)
        -- §84 联机程序给的 session / lobby / rel_in / lang：连着管道就用它经管道给的（内存里最新一份），不读文件
        local n = PIPE.netName(path)
        if n and PIPE.DOWN[n] and PIPE.tryConnect() then
            PIPE.poll()
            if PIPE.on then
                local t = PIPE.blob[n]
                return t and PIPE.kv(t) or nil
            end
        end
        local f = io.open(path, "r")
        if not f then return nil end
        local data = f:read("*a") or ""
        f:close()
        local t = {}
        for k, v in data:gmatch("([%w_]+)=([^\r\n]*)") do t[k] = v end     -- 0.34：键名允许下划线（consent_* 等 PREF 键）
        if next(t) == nil then return nil end
        return t
    end
    C.fileWrite, C.fileReadKV = fileWrite, fileReadKV

    -- ============================================================ v0.15.0 中英文切换
    -- 联机窗口右上角选语言 → 写 net/lang.txt（lang=zh|en），这里每秒读一次。
    -- 引擎默认字体没有中文字形；中文字体路径由调用方给（ctx.zhFont，一般是游戏自带的中文字体）。加载不到就保持英文
    local LANG = { zh = false, checkAt = 0.0, font = nil, fontTried = false }
    local LANG_FILE = NET_DIR .. "/lang.txt"
    local ZH_FONT = ctx.zhFont
    C.LANG = LANG
    local function zhFont()
        if LANG.fontTried then return LANG.font end
        LANG.fontTried = true
        if not ZH_FONT then return nil end
        local f = StaticFindObject(ZH_FONT)
        if not valid(f) and type(LoadAsset) == "function" then
            out("  [步骤] LoadAsset(%s)", ZH_FONT)
            local ok, r = pcall(function() return LoadAsset(ZH_FONT) end)
            out("  LoadAsset -> %s %s", ok and "成功" or "失败", tostring(r))
            f = StaticFindObject(ZH_FONT)
            if not valid(f) and ok and valid(r) then f = r end
        end
        LANG.font = valid(f) and f or nil
        out("中文字体：%s", LANG.font and short(pathOf(LANG.font)) or "加载不到，游戏内提示保持英文")
        return LANG.font
    end
    C.zhFont = zhFont
    function C.langPoll(tn)
        if tn < LANG.checkAt then return end
        LANG.checkAt = tn + 1.0
        local kv = fileReadKV(LANG_FILE)
        local zh = kv ~= nil and kv.lang == "zh"
        if zh ~= LANG.zh then
            LANG.zh = zh
            if zh then zhFont() end
            out("游戏内提示语言 -> %s", (zh and LANG.font) and "中文" or (zh and "中文（字体加载不到，仍显示英文）" or "英文"))
        end
    end
    -- 只有字体可用时才真的出中文（3D 文字要中文字体；UMG 不用）
    function C.T(zh, en)
        return (LANG.zh and (LANG.font or LANG.umg)) and zh or en     -- v0.18.6：UMG 显示中文不靠 LANG.font
    end

    -- ============================================================ 0.34 本地偏好 PREF：存 net/rules.txt 的任意键（只动自己的键，别的行原样留着）
    local PREF = { FILE = NET_DIR .. "/rules.txt" }
    C.PREF = PREF
    function PREF.get(k) local t = fileReadKV(PREF.FILE); return t and t[k] or nil end
    function PREF.set(k, v)
        local lines, done, f = {}, false, io.open(PREF.FILE, "r")
        if f then
            for line in (f:read("*a") or ""):gmatch("[^\r\n]+") do
                if line:match("^" .. k .. "=") then
                    if not done and v ~= nil then lines[#lines + 1] = k .. "=" .. tostring(v); done = true end
                else lines[#lines + 1] = line end
            end
            f:close()
        end
        if not done and v ~= nil then lines[#lines + 1] = k .. "=" .. tostring(v) end
        fileWrite(PREF.FILE, table.concat(lines, "\n") .. "\n")
    end

    -- ============================================================ 耗时统计 PROF（F3c-2，原 main.lua 段）
    -- 模组在游戏线程上每帧花多少：PROF.wrap(名字, 函数) 把要量的函数换成"计时 + 原函数"；每帧 PROF.frame() 数帧、记最长一帧、
    -- 单帧尖峰写分项；联机时每 REPORT_SEC 秒写一行 [耗时]。os.clock 在 Windows 上是 QueryPerformanceCounter 的墙钟。
    -- 适配层给：PROF.setTop(分项名列表)、PROF.online()（联机中）、PROF.frozen()（切图冻结中）、PROF.tag（日志前缀）；要包哪些函数也由适配层决定
local PROF = { on = true, acc = {}, at = nil, frames = 0, worst = 0, last = nil, REPORT_SEC = 10, IDLE_SEC = 60,
         TOP = {}, isTop = {}, fcur = 0, fmax = 0, fn = 0, fsum = 0,
         fk = {}, SPIKE_MS = 25, SPIKE_LOGS = 30, spikeLogs = 0 }
function PROF.setTop(list)     -- 哪几个分项加起来算"模组这一帧一共花了多少"（单帧尖峰）；由适配层给
    PROF.TOP, PROF.isTop = list, {}
    for _, k in ipairs(list) do PROF.isTop[k] = true end
end
function PROF.fin(k, t, ...)
    local d = (os.clock() - t) * 1000
    PROF.acc[k] = (PROF.acc[k] or 0) + d
    PROF.fk[k] = (PROF.fk[k] or 0) + d                       -- §82.5 这一帧各分项（尖峰时写出来）
    if PROF.isTop[k] then PROF.fcur = PROF.fcur + d end     -- §82.4 这一帧模组一共花了多少（单帧尖峰）
    return ...
end
function PROF.wrap(k, fn)
    if type(fn) ~= "function" then return fn end
    return function(...)
        if not PROF.on then return fn(...) end
        local t = os.clock()
        return PROF.fin(k, t, fn(...))
    end
end
PROF.online = function() return false end
PROF.frozen = function() return false end
PROF.tag = ctx.tag or "[fornet] "
C.PROF = PROF

function PROF.frame()
    if PROF.frozen() then PROF.at = nil; return end      -- 切图冻结中：重新开始数（frozen 由适配层给）
    local c = os.clock()
    if not PROF.at then PROF.at, PROF.last, PROF.frames, PROF.worst, PROF.acc = c, c, 0, 0, {}; return end
    PROF.frames = PROF.frames + 1
    local f = PROF.fcur
    PROF.fcur = 0
    -- §82.5 单帧尖峰：模组这一帧花了 SPIKE_MS 以上，写出这一帧的分项（每次加载最多 SPIKE_LOGS 条）
    if f >= PROF.SPIKE_MS and PROF.spikeLogs < PROF.SPIKE_LOGS then
        PROF.spikeLogs = PROF.spikeLogs + 1
        local its = {}
        for k, v in pairs(PROF.fk) do if v >= 1 then its[#its + 1] = { k, v } end end
        table.sort(its, function(x, y) return x[2] > y[2] end)
        local ps = {}
        for _, it in ipairs(its) do ps[#ps + 1] = string.format("%s %.0f", it[1], it[2]) end
        out("[耗时] 单帧尖峰 %.0f ms：%s", f, table.concat(ps, "，"))
    end
    for k in pairs(PROF.fk) do PROF.fk[k] = nil end
    if f > PROF.fmax then PROF.fmax = f end
    if f > 0.5 then PROF.fn, PROF.fsum = PROF.fn + 1, PROF.fsum + f end
    local gap = (c - PROF.last) * 1000
    PROF.last = c
    if gap > PROF.worst then PROF.worst = gap end
    local el = c - PROF.at
    if el < PROF.REPORT_SEC then return end
    local online = PROF.online()     -- 联机中（远端数据 1 秒内新鲜）由适配层给
    if not online and el < PROF.IDLE_SEC then return end
    local a, total, top = PROF.acc, 0, {}
    for _, k in ipairs(PROF.TOP) do total = total + (a[k] or 0); top[k] = true end
    local items = {}
    for k, v in pairs(a) do
        if v / el >= 0.05 then items[#items + 1] = { k, v } end
    end
    table.sort(items, function(x, y) return x[2] > y[2] end)
    local parts = {}
    for _, it in ipairs(items) do parts[#parts + 1] = string.format("%s %.1f", it[1], it[2] / el) end
    local fps, worst = PROF.frames / el, PROF.worst
    local fAvg, fMax = PROF.fn > 0 and PROF.fsum / PROF.fn or 0, PROF.fmax
    PROF.acc, PROF.at, PROF.frames, PROF.worst, PROF.fmax, PROF.fn, PROF.fsum = {}, c, 0, 0, 0, 0, 0
    out("[耗时] %s 帧率 %.1f（最长一帧 %.0f ms）；模组占游戏线程 %.1f ms/秒，单帧平均 %.1f ms、最多 %.1f ms；分项 ms/秒（有嵌套）：%s",
        online and "联机中" or "未联机", fps, worst, total / el, fAvg, fMax, table.concat(parts, "，"))
end

-- 每帧数帧（单独一个循环，不碰每帧输入循环）
function PROF.start()
    if PROF.started or type(LoopInGameThreadAfterFrames) ~= "function" then return end
    PROF.started = true
    LoopInGameThreadAfterFrames(1, function()
        if PROF.on then
            local okP, errP = pcall(PROF.frame)
            if not okP and errP ~= PROF.err then PROF.err = errP; print(PROF.tag .. "耗时统计出错：" .. tostring(errP) .. "\n") end
        end
    end)
end

    return C
end
