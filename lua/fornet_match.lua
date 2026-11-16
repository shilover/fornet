-- fornet_match.lua —— fornet（4net）对局框架（F4a）。
-- 从游戏模组的 main.lua 原样搬出的、不认识游戏的对局概念：
-- · 回合状态机（主机说了算：cd → fight → rend → cd / end → idle；客机跟随 mt=）、mt= / arena= 的编解码、回合 / 对局结果的可靠事件
-- · 规则指纹 RFP（"两边这一场实际用上的规则"拼串算指纹、对不上写出哪项）
-- · 回合时限 RT 的框架半边（时限 / 剩余 / 受伤累计 / 到时判负 / 规则行折行）；挂无敌、清状态那半是游戏的，留适配层
-- · 观战的"每个替身一份状态"换入换出机制（PEER.run 等）；名单（哪些表 / 哪些字段）由适配层给
-- · 联机会话角色 sessionRole、看门狗式 pcall（同一个错误只记一次）
-- 游戏那边的事（生成 / 归位 / 锁血 / 倒地动作 / 属性 / HUD 行）全部经 deps 表回调，deps 表由适配层建一次、不每帧造闭包。
-- 用法：FM = require("fornet_match")({ out=, MATCH=, T=, relSend=, hash=, num=, rkRules=, peerIsAi=, peerVersion=, cfgHash=, ruleNames= })
return function(ctx)
local out = ctx.out or function() end
local MATCH = ctx.MATCH
local T = ctx.T or function(zh, en) return en end
local relSend = ctx.relSend or function() end
local F = { ctx = ctx }

-- 原 M4 段的 CD_SEC / REND_SEC / END_SEC / LOST_SEC / FIGHT_SHOW（倒计时 / 回合结束停留 / 对局结束停留 / 掉线判定 / "开战！"显示秒数）
F.TIMES = { CD = 3.0, REND = 3.0, END = 6.0, LOST = 5.0, FIGHT_SHOW = 1.0 }
F.COL = ctx.COL or {}        -- 屏幕文字颜色：适配层装完 HUD 底座后填（RFP.rows 用 RED）

-- 看门狗式 pcall：出错只记一次（同一个错误不再记），错误存在 holder[key]
function F.guard(holder, key, label, fn, ...)
    local ok, err = pcall(fn, ...)
    if not ok and err ~= holder[key] then holder[key] = err; out("!! %s出错：%s", label, tostring(err)) end
    return ok
end

-- v0.13.1：联机会话（联机程序写 net/session.txt：role=host|client|spec，connected=0|1，peer=…）。没有这个文件（幽灵测试）时按主机处理
function F.sessionRole()
    if MATCH.session and MATCH.session.matchhost == "1" then return "host" end
    return (MATCH.session and MATCH.session.role) or "host"
end

-- ---- mt= / arena= 编解码（状态包字段，v0.10.0 起的格式：mt=<状态>:<回合>:<主机分>:<客机分>:<本回合赢家 h|c|d|->:<局号>）
function F.mtEncode()
    return string.format("%s:%d:%d:%d:%s:%d", MATCH.state, MATCH.round, MATCH.me, MATCH.op, MATCH.win, MATCH.id)
end
function F.mtParse(rm)
    local s, rd, hs, cs, w, id = (rm or ""):match("^(%a+):(%d+):(%d+):(%d+):(%S):(%d+)$")
    if not s then return nil end
    return s, tonumber(rd), tonumber(hs), tonumber(cs), w, tonumber(id)
end
function F.arenaEncode(a) return a and string.format("%.1f:%.1f:%.1f:%.1f", a.x, a.y, a.z, a.yaw) or "" end
function F.arenaParse(s)
    local ax, ay, az, ayaw = (s or ""):match("^([%-%d%.]+):([%-%d%.]+):([%-%d%.]+):([%-%d%.]+)$")
    if not ax then return nil end
    return tonumber(ax), tonumber(ay), tonumber(az), tonumber(ayaw)
end

-- ---- 状态机（原 matchStep 的主机 / 客机分支，一字不差）
-- deps（适配层建一次）：enter(p1, s, tn, why) / peerDead 对方这回合倒了没 / timeUp(tn) 时间到返回本回合赢家 h|c|d 或 nil（顺带发 rtime）
--   / matchOver(won) 打完（观战换人等，可 nil）
function F.hostStep(p1, tn, lost, deps)
    local TM = F.TIMES
    if lost > TM.LOST then
        deps.enter(p1, "idle", tn, "对方掉线")
    elseif MATCH.state == "cd" and tn - MATCH.t0 >= TM.CD then
        deps.enter(p1, "fight", tn)
    elseif MATCH.state == "fight" then
        local mine, theirs = MATCH.dead == MATCH.round, deps.peerDead()
        local tw = not (mine or theirs) and deps.timeUp(tn) or nil       -- 时间到：受伤多的判负
        if mine or theirs or tw then
            MATCH.win = tw or ((mine and theirs) and "d" or (mine and "c" or "h"))
            if MATCH.win == "h" then MATCH.me = MATCH.me + 1 elseif MATCH.win == "c" then MATCH.op = MATCH.op + 1 end
            deps.enter(p1, "rend", tn)
        end
    elseif MATCH.state == "rend" and tn - MATCH.t0 >= TM.REND then
        local need = MATCH.toWin or 2
        if MATCH.me >= need or MATCH.op >= need then
            deps.enter(p1, "end", tn)
        else
            MATCH.round = MATCH.round + 1
            deps.enter(p1, "cd", tn)
        end
    elseif MATCH.state == "end" and tn - MATCH.t0 >= TM.END then
        if deps.matchOver then deps.matchOver(MATCH.me >= (MATCH.toWin or 2)) end   -- 打完换人
        deps.enter(p1, "idle", tn, "打完了")
    end
end
-- deps：enter / lostWhy 掉线时的提示（收到过结算用结算）/ follow(p1, tn) 跟随主机的 mt（没有要跟随的对局 = false）
function F.clientStep(p1, tn, lost, deps)
    if lost > F.TIMES.LOST then deps.enter(p1, "idle", tn, deps.lostWhy())
    elseif not deps.follow(p1, tn) then deps.enter(p1, "idle", tn, "主机结束了对局") end
end

-- 客机：跟随主机的 mt（原 followHost 的框架部分）。返回 false 表示没有要跟随的对局。
-- deps：becomeClient(p1, id) 刚跟进来：记下这局的规则 / 日志 / 文字；arena(x, y, z, yaw)；sp(rasp) 开战点；
-- ranked(s, rd, hs, w, id, tn) 排位复核（可 nil）；endWhy 被主机结束时显示什么；enter(p1, s, tn, why)
function F.followHost(p1, rm, rasp, rarena, tn, deps)
    local s, rd, hs, cs, w, id = F.mtParse(rm)
    if not s then return false end
    if MATCH.role ~= "client" then
        if s ~= "cd" and s ~= "fight" then return false end
        -- 刚弃权的那一局：主机还没收到弃权、仍在发 fight，不要再跟进去（离线测试抓到：会立刻重新加入，弃权标记也被清掉）
        if id == MATCH.wire.quit then return false end
        MATCH.role, MATCH.state, MATCH.id = "client", "idle", id
        MATCH.wire.quit = 0
        deps.becomeClient(p1, id)
    end
    local ax, ay, az, ayaw = F.arenaParse(rarena)
    if ax then deps.arena(ax, ay, az, ayaw) end
    deps.sp(rasp)                                   -- 主机发来的开战点（旧版主机没有 = 不强制归位）
    if deps.onMt then deps.onMt(s, rd, hs, w, id, tn) end     -- 每个 mt= 都调一次（排位客机复核等经事件挂）
    F.fire("mt", s, rd, hs, w, id, tn)
    MATCH.me, MATCH.op, MATCH.win = cs, hs, w
    if s == "idle" or id ~= MATCH.id then deps.enter(p1, "idle", tn, deps.endWhy()); return true end
    if (s ~= MATCH.state or rd ~= MATCH.round) and MATCH.center then
        MATCH.round = rd
        deps.enter(p1, s, tn)
    end
    return true
end

-- ---- F4b 钩子：对局事件 / 规则来源 / 附加状态字段 / HUD 行（排位等"定死规则 + 记分"的功能经这些挂进来，核心不认识它们）
-- 事件：F.on(evt, fn) / F.fire(evt, ...)。核心自己发的：
-- "enter"(s, why, p1, tn) 对局进了哪个状态（适配层的 enter 开头发）；"mt"(s, rd, hs, w, id, tn) 客机收到主机的每个 mt=；
-- 出错的处理函数只记一次（同一个错误不重复）
F.listeners, F.fireErr = {}, {}
function F.on(evt, fn)
    local l = F.listeners[evt] or {}
    l[#l + 1] = fn
    F.listeners[evt] = l
end
function F.fire(evt, ...)
    local l = F.listeners[evt]
    if not l then return end
    for i = 1, #l do
        local ok, err = pcall(l[i], ...)
        if not ok and not F.fireErr[tostring(err)] then
            F.fireErr[tostring(err)] = true
            out("!! 对局事件 %s 的处理出错：%s", tostring(evt), tostring(err))
        end
    end
end
-- 规则来源：{ name, priority, kind = "locked"（排位那种：生效时房间设置全按它、规则还没到不能开局）| "room"（解说房间规则：只盖值），
-- active 在不在生效, rules 规则表或 nil（还没到）, waiting 还没到, waitRows 等规则时的屏幕提示行, waitLog(kind) 等规则时的日志（kind = "ready" | "start"）,
-- label = { zh, en } 日志里的来源名, fixedTag = { zh, en } 房间设置页的"：xx 固定", fixedMsg = { zh, en } 点了不能改的提示,
-- startLog(asClient) 开局 / 跟随时的一行日志, headerItems(lb, items) 房间设置页顶上的说明行, fpNote = { zh, en } 规则指纹对不上时横幅末尾加的话 }
F.rulesSrc = {}
function F.addRules(p)
    F.rulesSrc[#F.rulesSrc + 1] = p
    table.sort(F.rulesSrc, function(a, b) return (a.priority or 0) > (b.priority or 0) end)
end
function F.fixedBy()        -- 正在生效的最高优先级来源（规则还没到也算生效）
    for _, p in ipairs(F.rulesSrc) do if p.active() then return p end end
    return nil
end
function F.lockedBy()       -- 正在生效的"定死规则"来源（排位），没有 = nil
    local p = F.fixedBy()
    return (p and p.kind == "locked") and p or nil
end
function F.fixedRules()     -- 第一个给出规则表的来源（排位规则还没到时退到解说房间规则 / nil，和以前 rkRules or roomRules 一样）
    for _, p in ipairs(F.rulesSrc) do
        if p.active() then
            local r = p.rules()
            if r then return r end
        end
    end
    return nil
end
function F.rulesWaiting() local p = F.lockedBy(); return p ~= nil and p.waiting ~= nil and p.waiting() or false end
function F.rulesWaitRows() local p = F.lockedBy(); return p and p.waitRows and p.waitRows() or nil end
function F.rulesWaitLog(kind) local p = F.lockedBy(); return p and p.waitLog and p.waitLog(kind) or "规则还没拿到，不能开局" end
-- HUD 行：F.addRows(where, fn(...)) / F.rowsOf(where, ...) → 行的列表（fn 返回一行 { c=, col= } 或一列行或 nil）。where："cd"（倒计时规则区）/ "idle"（回自由活动时的一次性提示）
F.rowSrc = {}
function F.addRows(where, fn)
    local l = F.rowSrc[where] or {}
    l[#l + 1] = fn
    F.rowSrc[where] = l
end
function F.rowsOf(where, ...)
    local o = {}
    for _, fn in ipairs(F.rowSrc[where] or {}) do
        local r = fn(...)
        if r then
            if r.c then o[#o + 1] = r else for _, x in ipairs(r) do o[#o + 1] = x end end
        end
    end
    return o
end
-- 状态包附加字段：F.addWire(fn → "k=v\n" 或 nil)；F.wireExtra 拼在状态包里
F.wireSrc = {}
function F.addWire(fn) F.wireSrc[#F.wireSrc + 1] = fn end
function F.wireExtra()
    local s = ""
    for _, fn in ipairs(F.wireSrc) do s = s .. (fn() or "") end
    return s
end

-- enter 里的框架部分（原文）：进倒计时清这一回合的记录 + 第 1 回合把"准备"用掉；主机发回合 / 对局结果的可靠事件；回自由活动清对局字段
function F.roundReset()
    MATCH.dead, MATCH.win, MATCH.pupFell, MATCH.hold = 0, "-", 0, {}
    -- 2026-11-06 ：准备在开局时就用掉（两边都清），
    -- 以前客机要等看到主机回自由活动才清，主机一回自由活动就看到客机还没清掉的 rdy=1 → 马上又开一局
    if MATCH.round == 1 then MATCH.ready = false end
end
-- v0.22.0：主机的回合 / 对局结果另发一份可靠事件。客机平时跟着状态包里的 mt= 走；
-- 结算画面只有几秒，客机那几秒收不到包（卡顿 / 丢包）时就靠这份补上结果
function F.sendResult(s)
    if MATCH.role == "host" and s == "rend" then
        relSend("rend", string.format("%d %d %s %d %d", MATCH.id, MATCH.round, MATCH.win, MATCH.me, MATCH.op))
    elseif MATCH.role == "host" and s == "end" then
        relSend("mend", string.format("%d %d %d", MATCH.id, MATCH.me, MATCH.op))
    end
end
function F.idleReset()
    MATCH.res = nil
    MATCH.role, MATCH.hold, MATCH.dead = nil, {}, 0
    MATCH.lastTx = nil
    MATCH.ready = false
    MATCH.quitArm = nil
end

-- ============================================================ 规则指纹（
-- → 选了"规则指纹 + 表版本号"）。11-09 服务器排位规则加了 std=1 / acc=1，0.30.2 不认就跳过，和 0.30.3 对打时两边按不同数值打、谁也看不出来。
-- 新字段旧版不认 = 悄悄跳过，这是根子；这里不靠"记得改最低版本"，而是比"两边这一场实际用上的规则"：
-- 每场第 1 回合倒计时（MATCH.* 和数值表都定下以后）把局数 / 伤害 / 可格挡 / 时限 / 各开关 / 开局资源 / 饰品 / 禁用名单 /
-- 这一场用的数值表哈希拼成一串（RFP.text），算个短指纹。状态包发 rfp=<局号>:<指纹>，可靠事件 rfpx 发整串一次（对不上时说清哪项不一样）。
-- 对方指纹不一样 → 横幅写哪几项不一样；对方 RFP.WAIT 秒还没发指纹（旧版本）→ 横幅写"对方版本旧"。普通房间只提示不拦。
-- 排位：联机程序把我的指纹报给服务器（RFP），服务器自己比两边的——指纹在开局那一刻就报了，打输了再改也没用；
-- 对不上 → 这一场不计分（服务器配置 rfp=check：两边都报了才比；rfp=require：没报的（旧版）也不计分；rfp=off：不管）。
-- 以后新加"影响数值的房间规则 / 排位规则"，要把它加进 RFP.text（或放进 MATCH.flags / MATCH.res，那两张表整张都进），不然新旧版本对打又看不出来
-- 游戏那边给的：ctx.ruleNames（键 → { 中文, 英文 }）、ctx.num（数字 → 字符串，和数值表一致）、ctx.hash（串 → 短指纹）、
-- ctx.cfgHash(flags)（这一场数值表影响伤害时返回用上的表哈希，否则 nil）、ctx.rkRules、ctx.peerIsAi、ctx.peerVersion
local RFP = { WAIT = 3.0, F = 1, mid = nil, fp = nil, text = nil, at = 0, peer = nil, peerText = nil, bad = nil, logged = nil,
              NAME = ctx.ruleNames or {} }
F.RFP = RFP
local function rfpVal(v)
    if v == nil then return "-" end
    if v == true then return "1" elseif v == false then return "0" end
    if type(v) == "number" then return ctx.num and ctx.num(v) or tostring(v) end
    return (tostring(v):gsub("[;=%s]", "_"))
end
-- 这一场实际用上的规则 → "k=v;k=v"（键排好序；两边同一套规则 = 同一串）
function RFP.build()
    local lk = F.lockedBy()
    -- D4：可选覆盖（对决 = 1v1 的局数 / 时限，多人 = bo=0 / 多人时限）。游戏没给回调 = 照旧用 MATCH。
    local ov = nil
    if ctx.rfpOverride then
        local okO, v = pcall(ctx.rfpOverride)
        if okO and type(v) == "table" then ov = v end
    end
    if not ov and type(BASEHOOK) == "table" and BASEHOOK.mp and BASEHOOK.mp.rfpOverride then
        local okO, v = pcall(BASEHOOK.mp.rfpOverride)
        if okO and type(v) == "table" then ov = v end
    end
    local bo = (ov and ov.bo ~= nil) and ov.bo or MATCH.toWin
    local tl = (ov and ov.tl ~= nil) and ov.tl or (MATCH.tl or 0)
    local kv = { f = RFP.F, rk = (lk and lk.rules()) and 1 or 0, bo = bo, dm = MATCH.dmPct, ab = MATCH.ab, tl = tl }     -- rk：定死规则（排位）在生效
    for k, v in pairs(MATCH.flags or {}) do kv[k] = v end
    for k, v in pairs(MATCH.res or {}) do if kv[k] == nil then kv[k] = v end end
    kv.cfg = ctx.cfgHash(MATCH.flags) or "-"      -- 武器平衡 / 标准数值都关着时数值表不影响伤害
    local ks = {}
    for k in pairs(kv) do ks[#ks + 1] = k end
    table.sort(ks)
    local o = {}
    for _, k in ipairs(ks) do o[#o + 1] = k .. "=" .. rfpVal(kv[k]) end
    return table.concat(o, ";")
end
function RFP.parse(s)
    local t = {}
    for k, v in (s or ""):gmatch("([%w_]+)=([^;]*)") do t[k] = v end
    return t
end
-- 两串里不一样的键（按 NAME 写成人话，最多 n 个）
function RFP.diff(a, b, n, lang)
    local ta, tb, ks, seen = RFP.parse(a), RFP.parse(b), {}, {}
    for k in pairs(ta) do seen[k] = true end
    for k in pairs(tb) do seen[k] = true end
    for k in pairs(seen) do if ta[k] ~= tb[k] then ks[#ks + 1] = k end end
    table.sort(ks)
    local o = {}
    for i, k in ipairs(ks) do
        if i > (n or 99) then o[#o + 1] = "…"; break end
        o[#o + 1] = RFP.NAME[k] and RFP.NAME[k][lang or 1] or k
    end
    return o
end
-- 每场第 1 回合倒计时、CFG.begin 之后调（主机 / 客机都调）
-- 局号 / 在不在对局里：游戏可以给 ctx.matchId / ctx.matchOn（多人 / 对决用它们自己的局号），没给 = 1v1 的 MATCH
local function rfpMid() if ctx.matchId then local m = ctx.matchId(); if m ~= nil then return m end end return MATCH.id end
local function rfpOn() if ctx.matchOn then local o = ctx.matchOn(); if o ~= nil then return o end end return MATCH.state ~= "idle" end
function RFP.begin(tn)
    RFP.text = RFP.build()
    RFP.fp, RFP.mid, RFP.at, RFP.bad, RFP.logged = ctx.hash(RFP.text), rfpMid(), tn, nil, nil
    out("规则指纹（对局 #%d）：%s = %s", RFP.mid, RFP.fp, RFP.text)
    pcall(relSend, "rfpx", string.format("%d %s", RFP.mid, RFP.text))
end
function RFP.reset() RFP.mid, RFP.fp, RFP.text, RFP.bad, RFP.logged, RFP.peer, RFP.peerText = nil, nil, nil, nil, nil, nil, nil end
-- 状态包：rfp=<局号>:<指纹>；对局外 / 还没定 = "-"（联机程序也看它，排位时报给服务器）
function RFP.wire()
    if not (RFP.mid and RFP.mid == rfpMid() and rfpOn()) then return "-" end
    return RFP.mid .. ":" .. RFP.fp
end
-- 收包：记下对方的；然后比
function RFP.onPacket(v, tn)
    local mid, fp = (v or ""):match("^(%d+):(%x+)$")
    if mid then RFP.peer = { mid = tonumber(mid), fp = fp } end
    RFP.step(tn)
end
-- 可靠事件 rfpx：对方这一场的规则整串（对不上时说清哪项）
function RFP.onText(rest)
    local mid, txt = rest:match("^(%d+) (.*)$")
    if mid then RFP.peerText = { mid = tonumber(mid), t = txt } end
end
function RFP.step(tn)
    if not rfpOn() or not RFP.mid or RFP.mid ~= rfpMid() then RFP.bad = nil; return end
    -- 对方是本地 AI / 测试假对手（状态包 ai=1）：包是本机联机程序 / 测试脚本造的，不发 rfp=，规则就是我自己的 → 不比
    -- （11-10 总控实测：本地 AI 开局 3 秒误报"对方版本旧"，日志里的"对方版本 0.31.0"是联机程序自己的版本号）
    if ctx.peerIsAi() then RFP.bad = nil; return end
    local p = RFP.peer
    local bad
    if p and p.mid == RFP.mid then bad = p.fp ~= RFP.fp and "diff" or nil
    elseif tn - RFP.at > RFP.WAIT then bad = "none" end
    RFP.bad = bad
    if bad and RFP.logged ~= bad then
        RFP.logged = bad
        local pt = RFP.peerText and RFP.peerText.mid == RFP.mid and RFP.peerText.t or nil
        if bad == "diff" then
            out("!! 规则指纹对不上（对局 #%d）：我 %s / 对方 %s；不一样的：%s；对方 %s", RFP.mid, RFP.fp, p.fp,
                pt and table.concat(RFP.diff(RFP.text, pt), "、") or "（对方整串还没到）", pt or "-")
        else
            out("!! 对方 %.0f 秒没报规则指纹（对局 #%d，对方版本 %s 没有这项检查），双方用的规则 / 数值可能不一样", RFP.WAIT, RFP.mid, tostring(ctx.peerVersion()))
        end
    end
end
-- 对局横幅（倒计时 / 交手中都显示）
function RFP.rows()
    if not RFP.bad then return nil end
    local c
    if RFP.bad == "diff" then
        local pt = RFP.peerText and RFP.peerText.mid == RFP.mid and RFP.peerText.t or nil
        local zh = pt and table.concat(RFP.diff(RFP.text, pt, 3, 1), "、") or "?"
        local en = pt and table.concat(RFP.diff(RFP.text, pt, 3, 2), ", ") or "?"
        c = string.format(T("双方规则不一致：%s", "RULES DIFFER: %s"), T(zh, en))
    else
        c = T("对方模组版本较旧，没报规则，双方数值可能不一致", "OPPONENT'S MOD IS OLD: RULES MAY DIFFER")
    end
    local lk = F.lockedBy()
    if lk and lk.rules() and lk.fpNote then c = c .. T(lk.fpNote[1], lk.fpNote[2]) end     -- 定死规则的来源（排位）：本场可能不计分
    return { { c = c, col = F.COL.RED, size = 0.8, nolog = true } }
end

-- ============================================================ 回合时限 RT 的框架半边：装到调用方的 RT 表上（无敌 / 清状态那半是游戏的，留适配层）
-- d：fixedRules 排位 / 房间定死的规则表或 nil；resOf(k) 房间设置（主机自己的 / 客机用主机的）；peerTl 主机发的 tl=（nil = 旧版主机）；
-- hpOf(p1)；peerDamage 对方回报缺席时按对方血量算的受伤（nil = 算不出）
function F.rtInstall(RT, d)
    -- 这一局的时限（秒，0 = 不限）。客机：主机没发 tl=（旧版 / 本地 AI 当主机）= 不限，免得屏幕上倒数到 0 什么也不发生
    function RT.limit()
        if d.fixedRules() then return d.resOf("tl") end
        if (MATCH.role == "client" or (MATCH.session and MATCH.session.role) == "client") and d.peerTl() == nil then return 0 end
        return d.resOf("tl") or 0
    end
    -- 交手还剩几秒（不限时 / 不在交手 = nil）
    function RT.left(tn)
        local lim = MATCH.tl or 0
        if lim <= 0 or MATCH.state ~= "fight" then return nil end
        return math.max(0, lim - (tn - MATCH.t0))
    end
    -- 倒计时规则行折行：显示宽度（中文 / 全角算 2，其余算 1）不超过 w，一条规则不拆；单条超长的单独一行
    RT.RULE_W = 40
    function RT.dispW(s)
        local w = 0
        for _, c in utf8.codes(s) do w = w + (c >= 0x2E80 and 2 or 1) end
        return w
    end
    function RT.wrapRules(rules, w)
        local lines, cur, cw = {}, nil, 0
        for _, r in ipairs(rules) do
            local rw = RT.dispW(r)
            if cur and cw + 3 + rw <= w then
                cur, cw = cur .. " · " .. r, cw + 3 + rw
            else
                if cur then lines[#lines + 1] = cur end
                cur, cw = r, rw
            end
        end
        if cur then lines[#lines + 1] = cur end
        return lines
    end
    function RT.begin(p1)
        RT.taken, RT.hp, RT.round, RT.info = 0, d.hpOf(p1), MATCH.round, nil
        if (MATCH.tl or 0) > 0 then out("第 %d 回合限时 %d 秒（到时受到伤害多的一方判负）", MATCH.round, MATCH.tl) end
    end
    -- 交手中每帧：自己血量往下掉的累加
    function RT.track(hp)
        if hp and RT.hp and hp < RT.hp then RT.taken = RT.taken + (RT.hp - hp) end
        RT.hp = hp
    end
    function RT.wire()
        if not MATCH.role or RT.round ~= MATCH.round then return "-" end
        return string.format("%d:%.0f", RT.round, RT.taken)
    end
    function RT.onPacket(t)
        local rd, v = (t.tk or ""):match("^(%d+):([%d%.]+)$")
        RT.rtk = rd and { round = tonumber(rd), v = tonumber(v) } or nil
    end
    -- 主机：时间到了没有。到了返回 本回合赢家 h / c / d（RT.info 记两边的数，可靠事件 rtime 由状态机的 deps.timeUp 发）；没到 nil
    function RT.check(tn)
        local left = RT.left(tn)
        if not left or left > 0 then return nil end
        local mine, theirs, how = RT.taken, nil, "对方回报"
        if RT.rtk and RT.rtk.round == MATCH.round then theirs = RT.rtk.v
        else
            local pd = d.peerDamage()
            if pd then theirs, how = pd, "按对方血量算" end
        end
        theirs = theirs or 0
        local w = math.abs(mine - theirs) < 0.5 and "d" or (mine > theirs and "c" or "h")
        RT.info = { round = MATCH.round, h = mine, c = theirs }
        out("== 第 %d 回合时间到（%d 秒）：受伤 我（主机）%.0f / 对方 %.0f（%s）→ %s ==", MATCH.round, MATCH.tl, mine, theirs, how,
            w == "d" and "一样多，平局" or (w == "h" and "对方受伤多，我赢" or "我受伤多，对方赢"))
        return w
    end
    return RT
end

-- ============================================================ 观战：每个替身一份状态的换入换出机制（装到调用方的 PEER 表上）
-- PEER 表上由适配层给的名单：GLOB（整张换的全局模块名）、ORIG（它们原来的表）、STK / STK_FRAME（ST 上要换的字段）、NETK_FRAME、LOK（LO 上要换的字段）。
-- m：对战那一份的 NET / AIR / LO / ST（主代码块的 local，函数里是上值，换不了名字：逐个换字段，不新建表）
function F.peerInstall(PEER, m)
    function PEER.tables()
        local t = { NET = m.NET, AIR = m.AIR }
        for n, mod in pairs(PEER.ORIG) do t[n] = mod end
        return t
    end
    -- 深拷：模块表 / 全局表的引用原样留着（不把 G、ST 之类整张拷进来）
    function PEER.deep(v, seen, skip)
        if type(v) ~= "table" or skip[v] or getmetatable(v) ~= nil then return v end
        if seen[v] then return seen[v] end
        local t = {}
        seen[v] = t
        for k, x in pairs(v) do t[k] = PEER.deep(x, seen, skip) end
        return t
    end
    function PEER.fields(mod)
        local t = {}
        for k, v in pairs(mod) do if type(v) ~= "function" then t[k] = v end end
        return t
    end
    -- 这个替身那张模块表：没有的键只回原模块的函数（状态不漏读对战那份）
    function PEER.meta(orig)
        return { __index = function(_, k) local v = orig[k]; if type(v) == "function" then return v end end }
    end
    -- local 表 mod 的字段换成 mine 的（原来的进 save）；换回来：mod 现在的进 mine，save 的放回 mod
    function PEER.swapIn(mod, mine, save)
        for k, v in pairs(mod) do if type(v) ~= "function" then save[k] = v; mod[k] = nil end end     -- 遍历中把已有键设 nil 是允许的
        for k, v in pairs(mine) do mod[k] = v end
    end
    function PEER.swapOut(mod, mine, save)
        for k in pairs(mine) do mine[k] = nil end
        for k, v in pairs(mod) do if type(v) ~= "function" then mine[k] = v; mod[k] = nil end end
        for k, v in pairs(save) do mod[k] = v; save[k] = nil end
    end
    -- 观战时房间规矩按主机那一路（A）记下的：现在换进来的就是 A → NET；否则 A 那份。不在观战 = NET（对战时客机读的就是它）
    function PEER.hostNet()
        local h = PEER.host
        if not PEER.on or not h or PEER.cur == h then return m.NET end
        return h.m.NET or m.NET
    end
    function PEER.note(ok)
        local c = PEER.on and PEER.cur
        if c then if ok then c.fired = c.fired + 1 else c.failed = c.failed + 1 end end
    end
    -- 换进去 → fn(...) → 换回来。不能嵌套（观战收包 + 驱动在观战的逻辑里一次做完、每帧那次在观战每帧里，不会叠）。
    -- light = 每帧那次：NET / ST 只换 PEER.NETK_FRAME / STK_FRAME（全局模块照样整张换），LO / AIR 不换
    function PEER.run(st, fn, light, ...)
        if PEER.on then return false, "PEER.run 嵌套" end
        local sv = st.save
        local stk = light and PEER.STK_FRAME or PEER.STK
        if light then
            for _, k in ipairs(PEER.NETK_FRAME) do sv.NET[k] = m.NET[k]; m.NET[k] = st.m.NET[k] end
        else
            PEER.swapIn(m.NET, st.m.NET, sv.NET)
            PEER.swapIn(m.AIR, st.m.AIR, sv.AIR)
            for _, k in ipairs(PEER.LOK) do sv.lo[k] = m.LO[k]; m.LO[k] = st.lo[k] end
        end
        for _, n in ipairs(PEER.GLOB) do _G[n] = st.m[n] end
        for _, k in ipairs(stk) do sv.st[k] = m.ST[k]; m.ST[k] = st.st[k] end
        PEER.on, PEER.cur = true, st
        local ok, err = pcall(fn, ...)
        PEER.on, PEER.cur = false, nil
        if light then
            for _, k in ipairs(PEER.NETK_FRAME) do st.m.NET[k] = m.NET[k]; m.NET[k] = sv.NET[k]; sv.NET[k] = nil end
        else
            PEER.swapOut(m.NET, st.m.NET, sv.NET)
            PEER.swapOut(m.AIR, st.m.AIR, sv.AIR)
            for _, k in ipairs(PEER.LOK) do st.lo[k] = m.LO[k]; m.LO[k] = sv.lo[k]; sv.lo[k] = nil end
        end
        for _, n in ipairs(PEER.GLOB) do _G[n] = PEER.ORIG[n] end
        for _, k in ipairs(stk) do st.st[k] = m.ST[k]; m.ST[k] = sv.st[k]; sv.st[k] = nil end
        if not ok then
            st.errN = (st.errN or 0) + 1
            if st.errN <= 5 or st.errN % 100 == 0 then out("!! 观战 %s 走对战替身那一套出错（第 %d 次）：%s", st.tag, st.errN, tostring(err)) end
        end
        return ok, err
    end
    -- 读这个替身那份状态里的一个字段（NET.rlk、ST.pupAtkAt …）
    function PEER.get(st, mod, key)
        if mod == "ST" then return st.st[key] end
        if mod == "LO" then return st.lo[key] end
        return st.m[mod] and st.m[mod][key]
    end
    return PEER
end

return F
end
