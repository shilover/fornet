-- fornet_net.lua —— fornet（4net）UE4SS 网络核：网络封装（docs/EXTENSIONS.md §10.4 F3c；docs/FORNET_SPLIT.md）。
-- F3c-1：v0.22.0 可靠事件通道 REL（和联机程序经 rel_out.txt / rel_in.txt 交换、按序号确认、去重按序交付）原样搬出。
-- 事件的处理函数由调用方往 REL.on[<类型>] 里挂（游戏适配 / 对局框架自己的事件类型）；这里只有通道。
-- 用法：local NR = require("fornet_net")({ out=, NET_DIR=, fileWrite=, fileReadKV= })   →  NR.REL / NR.LAT（F3c-3）
return function(ctx)
local out = ctx.out or function() end
local NET_DIR = ctx.NET_DIR or "."
local fileWrite = ctx.fileWrite or function() return false end     -- 测试桩只切 LAT 段时没有文件读写：REL 加载时的读写走空实现
local fileReadKV = ctx.fileReadKV or function() return nil end
local N = {}

-- ---- v0.27.0 延迟：A 测量 / B 外推（F3c-3，原 main.lua 段的游戏无关部分；C 追帧 / 前摇学习留在适配层，往同一张表上补）
-- A 测量：状态包加 ts=<我的毫秒>、tse=<对方最近一包的 ts>:<我收到它之后又过了多少毫秒>，NTP 式四个时间戳算往返和时钟差。
--   在 Lua 层算，两边的驱动节拍和文件邮箱都算在里面；不依赖两台电脑的系统时钟。旧版对方不发 ts = 不知道包龄
-- B 外推：pos + vel × min(包龄, EXTRA_MAX)，只动 XY
local LAT = { N = 32, MAX_RTT = 3000, JUMP = 2000,          -- 往返样本数、超过这个的样本不要、时钟差跳了这么多算对方重启
        EXTRA_MAX = 0.25, EXTRA_ATK = 0.8, EXTRA_HIT = 0.5, EXTRA_TURN = 0.2,     -- 外推上限；出招 / 受击 / 急转后多久内不外推
        samples = {}, off = nil, rtt = nil, rttMin = nil, rttMax = nil, peerTs = nil, peerAt = nil,
        ages = {},
        st = { extra = 0, skip = 0, skipMs = 0, noSkip = 0, noWhy = {} } }
function LAT.ms(tn) return math.floor(tn * 1000 + 0.5) end

-- 新连接 / 对方重启：对方的时钟换了，样本全作废
function LAT.reset()
    LAT.samples, LAT.off, LAT.rtt, LAT.rttMin, LAT.rttMax, LAT.peerTs, LAT.peerAt, LAT.ages = {}, nil, nil, nil, nil, nil, nil, {}
end

-- 发送端：ts / tse / evt（evt=<招式号:我出这招时的 ts>,...，和 ev= 一一对应；旧版对方不认这几个字段，原样忽略）
function LAT.wire(tn, events)
    local s = string.format("ts=%d\n", LAT.ms(tn))
    if LAT.peerTs then s = s .. string.format("tse=%d:%d\n", LAT.peerTs, math.max(0, LAT.ms(tn) - LAT.peerAt)) end
    local evt = {}
    for _, e in ipairs(events or {}) do if e.ts then evt[#evt + 1] = e.id .. ":" .. e.ts end end
    return s .. "evt=" .. table.concat(evt, ",") .. "\n"
end

-- 接收端：每个新包调一次。返回这一包的包龄（秒，不知道 = nil）
function LAT.onPacket(t, tn)
    local ts = tonumber(t.ts)
    if not ts then LAT.peerTs = nil; return nil end
    local t4 = LAT.ms(tn)
    LAT.peerTs, LAT.peerAt = ts, t4
    local t1, hold = (t.tse or ""):match("^(%-?%d+):(%d+)$")
    t1, hold = tonumber(t1), tonumber(hold)
    if t1 then
        local rtt = t4 - t1 - hold
        if rtt >= 0 and rtt < LAT.MAX_RTT then
            local off = ((ts - hold - t1) + (ts - t4)) / 2       -- 对方时钟 − 我的时钟
            if LAT.off and math.abs(off - LAT.off) > LAT.JUMP then
                out("延迟：对方时钟跳了 %+.0f ms（对方重启游戏？），重新测", off - LAT.off)
                LAT.samples = {}
            end
            local sm = LAT.samples
            sm[#sm + 1] = { rtt = rtt, off = off }
            if #sm > LAT.N then table.remove(sm, 1) end
            -- 时钟差取往返最小的那个样本（排队只会让往返变大，最小的那个最接近对称）；往返显示中位数
            local best, sorted = sm[1], {}
            for _, x in ipairs(sm) do
                if x.rtt < best.rtt then best = x end
                sorted[#sorted + 1] = x.rtt
            end
            table.sort(sorted)
            LAT.off, LAT.rtt = best.off, sorted[math.floor((#sorted + 1) / 2)]
            LAT.rttMin, LAT.rttMax = sorted[1], sorted[#sorted]
        end
    end
    local age = LAT.age(ts, tn)
    if age then
        LAT.ages[#LAT.ages + 1] = age
        if #LAT.ages > 300 then table.remove(LAT.ages, 1) end
    end
    return age
end

-- 对方时钟的 tsMs 那一刻，离我现在多少秒（还没测出时钟差 = nil）
function LAT.age(tsMs, tn)
    if not (LAT.off and tsMs) then return nil end
    return math.max(0, LAT.ms(tn) - (tsMs - LAT.off)) / 1000
end

function LAT.rttMs() return LAT.rtt end

-- B 外推（纯函数）：pos + vel × min(age, EXTRA_MAX)，只动 XY（drivePuppet 只驱动 XY，Z 交给重力 / 起跳）
function LAT.extrapolate(pos, vel, age)
    if not (pos and vel and age and age > 0) then return pos end
    local a = math.min(age, LAT.EXTRA_MAX)
    return { X = pos.X + (vel.X or 0) * a, Y = pos.Y + (vel.Y or 0) * a, Z = pos.Z }
end

-- ---- v0.22.0 可靠事件通道（原 main.lua 段；说明见那里的标题行）
-- 只发一次、丢了就没了的事件（弃权、回合结果、对局结果）另走联机程序的可靠通道：按序号确认、超时重发、对方去重并按顺序交付。
-- Lua 这边只交换文件：REL.send 把事件写进 net/rel_out.txt（e<本地号>=<类型> <内容>），联机程序收下后在 rel_in.txt 回 took=<本地号>，
-- 这里删掉；对方的事件在 rel_in.txt 的 d<交付号>=，处理完在 rel_out.txt 回 got=<交付号>。两个文件都以 end=1 结尾，读到半截的不算。
-- 联机程序是旧版（不认 rel_out.txt）时事件只是发不出去，状态包里的旧字段（quit= / mt=）照样兜底
-- （顶层 local 已经接近 Lua 的 200 个上限，文件名 / 常量 / 函数都放进 REL 表里）
local REL = { fOut = NET_DIR .. "/rel_out.txt", fIn = NET_DIR .. "/rel_in.txt", maxOut = 64,
              boot = string.format("%x%04x", os.time(), math.random(0, 0xffff)), lid = 0, out = {}, gb = "", got = 0,
              on = {}, ended = {}, n = 0 }

function REL.write()
    local t = { "b=" .. REL.boot, "gb=" .. REL.gb, "got=" .. REL.got }
    for _, e in ipairs(REL.out) do t[#t + 1] = string.format("e%d=%s", e.id, e.data) end
    t[#t + 1] = "end=1"
    fileWrite(REL.fOut, table.concat(t, "\n") .. "\n")
end

function REL.readIn()
    local t = fileReadKV(REL.fIn)
    if not (t and t["end"]) then return nil end
    local ids = {}
    for k in pairs(t) do
        local n = tonumber(k:match("^d(%d+)$") or "")
        if n then ids[#ids + 1] = n end
    end
    table.sort(ids)
    return t, ids
end

do  -- 加载时 rel_in.txt 里已有的是上一次（上一局游戏）的：记下启动号和最大交付号，不处理
    local t, ids = REL.readIn()
    if t then REL.gb, REL.got = t.b or "", ids[#ids] or 0 end
    REL.write()
end

-- kind 不带空格；data 一行文字
function REL.send(kind, data)
    REL.lid = REL.lid + 1
    local line = (kind .. " " .. tostring(data or "")):gsub("[\r\n]", " ")
    REL.out[#REL.out + 1] = { id = REL.lid, data = line }
    while #REL.out > REL.maxOut do table.remove(REL.out, 1) end   -- 联机程序没在收（没开 / 旧版）：只留最近的
    REL.write()
    out("可靠事件 → #%d %s", REL.lid, line)
end

-- 每帧：收对方的事件（按交付号顺序，各处理一次），删掉联机程序已经收下的
function REL.poll()
    local t, ids = REL.readIn()
    if not t then return end
    local changed = false
    local newLink = false
    if (t.b or "") ~= REL.gb then REL.gb, REL.got, changed, newLink = t.b or "", 0, true, true end   -- 联机程序开了新的一次联机：交付号从 1 重数
    if newLink and REL.onLink then      -- L1（docs/FORNET_LAYERS.md 第 5 节）：玩法 mod 重新握手
        local ok, err = pcall(REL.onLink)
        if not ok then out("!! 新联机回调出错：%s", tostring(err)) end
    end
    for _, n in ipairs(ids) do
        if n > REL.got then
            REL.got, changed = n, true
            local line = t["d" .. n] or ""
            local kind, rest = line:match("^(%S+)%s*(.*)$")
            local fn = kind and REL.on[kind]
            REL.n = REL.n + 1
            out("可靠事件 ← #%d %s%s", n, line, fn and "" or "（不认识，忽略）")
            if fn then
                local ok, err = pcall(fn, rest)
                if not ok then out("!! 可靠事件 %s 处理出错：%s", tostring(kind), tostring(err)) end
            end
        end
    end
    if t.ob == REL.boot then
        local took = tonumber(t.took) or 0
        while #REL.out > 0 and REL.out[1].id <= took do table.remove(REL.out, 1); changed = true end
    end
    if changed then REL.write() end
end

-- 导出（main.lua 取别名；名字和搬之前一样）
N.REL, N.LAT = REL, LAT
return N
end
