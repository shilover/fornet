-- fornet_mods.lua：4net 的玩法 mod 机制（docs/FORNET_LAYERS.md 第 3–6 节，L1；宿主 0.3.0）。
-- 宿主在执行游戏适配层之前构造它（适配层也能 provide / use 服务），执行完适配层后调 loadAll() 装 mods\<名>\。
-- 这里只有机制，不认识任何游戏、不画任何界面：
--   · 扫 mods\、读 manifest、on.txt 才加载、game= / needsFornet / needs= 检查、按依赖拓扑序执行入口
--   · 服务注册（provide / use）、进程内事件总线（on / emit）
--   · 网络频道：可靠事件按 mod 名加前缀、状态包 x.<mod>.<key>= 字段、握手 4n.mods 交换双方装了哪些 mod
--   · 对局占用锁（activity）、键位仲裁（keys）
-- 每个回调都 pcall 包着，同一个错误只记一次；没有 mods\ 目录时什么都不做，不改任何现有行为。
return function(H)
local S = {
    list = {}, byName = {}, services = {}, svcOwner = {}, events = {}, acts = {}, keys = {},
    netOn = {}, fields = {}, onFields = {}, fieldLast = {}, peer = nil, errs = {}, warned = {},
}
local out = function(fmt, ...) H.log(fmt, ...) end
local enqueue = nil                 -- 适配层给的"放进游戏线程队列"（按键回调里不直接执行）

-- 适配层装好后调：日志改进它的日志文件、按键走它的游戏线程队列
function S.bindAdapter(t)
    if type(t) ~= "table" then return end
    if type(t.out) == "function" then out = t.out end
    if type(t.enqueue) == "function" then enqueue = t.enqueue end
end
function S.out(fmt, ...) out(fmt, ...) end

local function guard(label, fn, ...)
    local ok, err = pcall(fn, ...)
    if not ok and S.errs[label] ~= err then S.errs[label] = err; out("!! %s出错：%s", label, tostring(err)) end
    return ok, err
end

local function exists(path) local f = io.open(path, "rb"); if f then f:close(); return true end; return false end
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
local function okName(n) return type(n) == "string" and n:match("^[%w_%-]+$") and #n <= 24 and n or nil end

-- ── 服务 / 事件 ──────────────────────────────────────────────
function S.provide(owner, name, tbl)
    if not okName(name) then out("!! %s 提供服务：名字不合法 %s", tostring(owner), tostring(name)); return false end
    local cur = S.svcOwner[name]
    if cur and cur ~= owner then out("!! %s 想提供服务 %s，已经由 %s 提供，拒绝", tostring(owner), name, cur); return false end
    S.services[name], S.svcOwner[name] = tbl, owner
    out("服务 %s ← %s", name, tostring(owner))
    S.emit("4n.service", name, owner)
    return true
end
function S.use(name) return S.services[name], S.svcOwner[name] end

function S.on(owner, ev, fn)
    if type(fn) ~= "function" then return false end
    local l = S.events[ev]
    if not l then l = {}; S.events[ev] = l end
    l[#l + 1] = { owner = owner, fn = fn }
    return true
end
function S.emit(ev, ...)
    local l = S.events[ev]
    if not l then return 0 end
    local n = 0
    for _, h in ipairs(l) do     -- 每拍都可能发（base.tick），出错时才拼标签
        local ok, err = pcall(h.fn, ...)
        if ok then n = n + 1
        else
            local label = string.format("事件 %s → %s ", ev, tostring(h.owner))
            if S.errs[label] ~= err then S.errs[label] = err; out("!! %s出错：%s", label, tostring(err)) end
        end
    end
    return n
end
-- 问一圈：按登记顺序调，第一个返回真值的算"接下了"，返回 (值, 谁)；都没接返回 nil（比如按键给谁用）
function S.ask(ev, ...)
    local l = S.events[ev]
    if not l then return nil end
    for _, h in ipairs(l) do
        local ok, v = pcall(h.fn, ...)
        if not ok then
            local label = string.format("事件 %s → %s ", ev, tostring(h.owner))
            if S.errs[label] ~= v then S.errs[label] = v; out("!! %s出错：%s", label, tostring(v)) end
        elseif v then return v, h.owner end
    end
    return nil
end
function S.has(ev) local l = S.events[ev]; return l ~= nil and #l > 0 end

-- ── 对局占用 ────────────────────────────────────────────────
-- 独占类活动（"match" = 驱动替身 / 关输入 / 锁血的那种对局）：同一时间只能一个 mod 占着
function S.acquire(owner, kind)
    local cur = S.acts[kind]
    if cur and cur ~= owner then return false, cur end
    if not cur then
        S.acts[kind] = owner
        out("占用 %s ← %s", kind, tostring(owner))
        S.emit("4n.activity", kind, owner)
    end
    return true
end
function S.release(owner, kind)
    if S.acts[kind] ~= owner then return false end
    S.acts[kind] = nil
    out("占用 %s 释放（%s）", kind, tostring(owner))
    S.emit("4n.activity", kind, nil)
    return true
end
function S.holder(kind) return S.acts[kind] end

-- ── 键位仲裁 ────────────────────────────────────────────────
local function keyId(key, mods)
    local id = tostring(key)
    if type(mods) == "table" and #mods > 0 then
        local m = {}
        for i, v in ipairs(mods) do m[i] = tostring(v) end
        table.sort(m)
        id = table.concat(m, "+") .. "+" .. id
    end
    return id
end
-- 只登记（适配层自己 RegisterKeyBind 的老代码用它报占用）；同一个 owner 重复登记同一个键算它自己的
function S.claimKey(owner, key, label, mods)
    if key == nil then return false end
    local id = keyId(key, mods)
    local cur = S.keys[id]
    if cur and cur.owner ~= owner then
        out("!! 键位冲突：%s 想用 %s（%s），已经归 %s（%s），拒绝", tostring(owner), id, tostring(label), cur.owner, tostring(cur.label))
        cur.conflicts = cur.conflicts or {}
        cur.conflicts[#cur.conflicts + 1] = { owner = owner, label = label }
        return false, cur.owner
    end
    if not cur then S.keys[id] = { owner = owner, label = label } end
    return true
end
-- 登记 + 绑定：回调进适配层的游戏线程队列（按键回调里直接执行会报 Ref was not function，§v0.12.2）
function S.bindKey(owner, key, fn, label, mods)
    if type(fn) ~= "function" then out("!! %s 绑键 %s：不是函数", tostring(owner), tostring(label)); return false end
    local ok, who = S.claimKey(owner, key, label, mods)
    if not ok then return false, who end
    if type(RegisterKeyBind) ~= "function" then return false, "nokeys" end
    local function fire()
        if enqueue then enqueue(fn, string.format("%s:%s", tostring(owner), tostring(label)))
        elseif type(ExecuteInGameThread) == "function" then ExecuteInGameThread(function() guard("按键 " .. tostring(label) .. " ", fn) end)
        else guard("按键 " .. tostring(label) .. " ", fn) end
    end
    if type(mods) == "table" and #mods > 0 then RegisterKeyBind(key, mods, fire) else RegisterKeyBind(key, fire) end
    return true
end
function S.keyTable()
    local r = {}
    for id, k in pairs(S.keys) do r[#r + 1] = { id = id, owner = k.owner, label = k.label, conflicts = k.conflicts } end
    table.sort(r, function(a, b) return a.id < b.id end)
    return r
end

-- ── 网络频道 ────────────────────────────────────────────────
-- 可靠事件 / 状态包都用适配层装好的 fornet_net（FORNET.mods.fornet_net.REL）；没有时（测试 / 老布局）先记着，装好再挂
local function REL()
    local n = H.mods and H.mods.fornet_net
    return n and n.REL
end
local function installNet()
    local R = REL()
    if not R then return false end
    for kind, h in pairs(S.netOn) do
        if R.on[kind] ~= h.wrap then R.on[kind] = h.wrap end
    end
    if R.on["4n.mods"] ~= S.onHello then R.on["4n.mods"] = S.onHello end
    if R.onLink ~= S.onLink then R.onLink = S.onLink end
    return true
end
S.installNet = installNet

local function netMods()            -- 我这边启用了的、要和对方交换数据的 mod
    local t = {}
    for _, m in ipairs(S.list) do
        if m.state == "on" and m.manifest.net == "1" then t[#t + 1] = m.name .. "@" .. m.version end
    end
    return t
end
function S.hello()
    local R = REL()
    if not R then return false end
    R.send("4n.mods", table.concat(netMods(), ","))
    S.helloSent = true
    return true
end
-- 新的一次联机（fornet_net 的 REL.poll 发现交付批次换了）：对方是谁不知道了，重新握手
function S.onLink()
    S.peer, S.helloSent, S.fieldLast = nil, false, {}
    S.emit("4n.peerMods", nil)
    S.hello()
end
function S.onHello(rest)
    local peer = {}
    for item in tostring(rest or ""):gmatch("[^,%s]+") do
        local n, v = item:match("^([%w_%-]+)@(.+)$")
        if n then peer[n] = v elseif okName(item) then peer[item] = "?" end
    end
    S.peer = peer
    local names = {}
    for n, v in pairs(peer) do names[#names + 1] = n .. "@" .. v end
    table.sort(names)
    out("对方的 mod：%s", #names > 0 and table.concat(names, ", ") or "（无）")
    if not S.helloSent then S.hello() end
    S.emit("4n.peerMods", peer)
end
-- 对方装没装 / 启没启用这个 mod；握手还没到时返回 nil（不知道）
function S.peerHas(name)
    if not S.peer then return nil end
    return S.peer[name] ~= nil
end

function S.send(owner, kind, data)
    if not (okName(owner) and type(kind) == "string" and kind:match("^[%w_%-]+$")) then return false end
    if S.peer and not S.peer[owner] then
        if not S.warned[owner] then S.warned[owner] = true; out("[mod:%s] 对方没装这个 mod，不发它的事件", owner) end
        return false
    end
    local R = REL()
    if not R then return false end
    R.send(owner .. "." .. kind, data)
    return true
end
function S.netOnKind(owner, kind, fn)
    if not (okName(owner) and type(kind) == "string" and kind:match("^[%w_%-]+$") and type(fn) == "function") then return false end
    local full = owner .. "." .. kind
    S.netOn[full] = { owner = owner, wrap = function(rest) guard("可靠事件 " .. full .. " ", fn, rest) end }
    installNet()
    return true
end

-- 状态包字段 x.<mod>.<key>=
function S.field(owner, key, fn)
    if not (okName(owner) and okName(key) and type(fn) == "function") then return false end
    S.fields[#S.fields + 1] = { owner = owner, key = key, fn = fn }
    return true
end
function S.onField(owner, key, fn)
    if not (okName(owner) and okName(key) and type(fn) == "function") then return false end
    S.onFields[#S.onFields + 1] = { owner = owner, key = key, full = "x." .. owner .. "." .. key, fn = fn }
    return true
end
-- 适配层拼状态包时调：返回要追加的几行
function S.wire()
    if #S.fields == 0 then return "" end
    local t = {}
    for _, f in ipairs(S.fields) do
        if S.peer == nil or S.peer[f.owner] then
            local ok, v = pcall(f.fn)
            if ok and v ~= nil then
                v = tostring(v)
                if not v:find("[\r\n]") then t[#t + 1] = "x." .. f.owner .. "." .. f.key .. "=" .. v .. "\n" end
            end
        end
    end
    return table.concat(t)
end
-- 适配层收到对方状态包时调（kv 表）：值变了才通知（不是每包都调）
function S.onPacket(kv)
    if #S.onFields == 0 or type(kv) ~= "table" then return end
    for _, f in ipairs(S.onFields) do
        local v = kv[f.full]
        if v ~= S.fieldLast[f.full] then
            S.fieldLast[f.full] = v
            guard("状态字段 " .. f.full .. " ", f.fn, v)
        end
    end
end

-- ── 加载 ────────────────────────────────────────────────────
local function listDirs(dir)
    local names, seen = {}, {}
    local function add(n)
        n = okName((n or ""):gsub("^%s+", ""):gsub("%s+$", ""))
        if n and not seen[n] then seen[n] = true; names[#names + 1] = n end
    end
    local f = io.open(dir .. "/mods.txt", "r")
    if f then
        for line in f:lines() do if not line:match("^%s*#") then add(line) end end
        f:close()
        return names
    end
    local okP, p = pcall(io.popen, 'dir /b /ad "' .. dir:gsub("/", "\\") .. '" 2>nul')
    if okP and p then
        for line in p:lines() do add(line) end
        p:close()
    end
    table.sort(names)
    return names
end

local function parseNeeds(s)
    local r = {}
    for item in tostring(s or ""):gmatch("[^,]+") do
        item = item:gsub("^%s+", ""):gsub("%s+$", "")
        local n, v = item:match("^([%w_%-]+)%s*>=%s*([%d%.]+)$")
        if not n then n = item:match("^([%w_%-]+)$") end
        if n then r[#r + 1] = { name = n, min = v } end
    end
    return r
end

-- 每个 mod 拿到的句柄（入口文件 local M = ...）
local function makeHandle(m)
    local name = m.name
    local M = { name = name, version = m.version, dir = m.dir, manifest = m.manifest, fornet = H }
    function M.log(fmt, ...)
        local ok, s = pcall(string.format, tostring(fmt), ...)
        out("[mod:%s] %s", name, ok and s or tostring(fmt))
    end
    function M.provide(svc, tbl) return S.provide(name, svc, tbl) end
    function M.use(svc) return (S.use(svc)) end
    function M.on(ev, fn) return S.on(name, ev, fn) end
    function M.emit(ev, ...) return S.emit(ev, ...) end
    function M.ask(ev, ...) return S.ask(ev, ...) end
    M.net = {
        send = function(kind, data) return S.send(name, kind, data) end,
        on = function(kind, fn) return S.netOnKind(name, kind, fn) end,
        field = function(key, fn) return S.field(name, key, fn) end,
        onField = function(key, fn) return S.onField(name, key, fn) end,
        peerHas = function(other) return S.peerHas(other) end,
    }
    M.activity = {
        acquire = function(kind) return S.acquire(name, kind) end,
        release = function(kind) return S.release(name, kind) end,
        owner = function(kind) return S.holder(kind) end,
    }
    M.keys = { bind = function(key, fn, label, mods) return S.bindKey(name, key, fn, label, mods) end }
    -- 本 mod 目录里的模块（不进全局 package.path，免得两个 mod 的同名模块互相顶掉）
    local cache = {}
    function M.require(mod)
        if cache[mod] ~= nil then return cache[mod] end
        if not tostring(mod):match("^[%w_%-%.]+$") then error("M.require：模块名不合法 " .. tostring(mod), 2) end
        local path = m.dir .. "/" .. mod:gsub("%.", "/") .. ".lua"
        local chunk, err = loadfile(path)
        if not chunk then error("M.require " .. mod .. "：" .. tostring(err), 2) end
        local v = chunk(M)
        if v == nil then v = true end
        cache[mod] = v
        return v
    end
    return M
end

-- 读全部 manifest、定状态、排顺序（不执行）。状态：on / off（没 on.txt）/ bad（manifest 不对）/ blocked（依赖不满足）
function S.scan()
    S.DIR = H.ROOT .. "/mods"
    S.list, S.byName = {}, {}
    for _, name in ipairs(listDirs(S.DIR)) do
        local dir = S.DIR .. "/" .. name
        local mf = readKV(dir .. "/manifest.txt")
        local m = { name = name, dir = dir, manifest = mf or {}, version = (mf and mf.version) or "?", order = tonumber(mf and mf.order) or 50 }
        local entry = mf and (mf.entry ~= nil and mf.entry ~= "" and mf.entry or "main.lua")
        if not mf then m.state, m.why = "bad", "没有 manifest.txt"
        elseif mf.name ~= name then m.state, m.why = "bad", "manifest 的 name=" .. tostring(mf.name) .. " 和目录名不一致"
        elseif not (mf.version and mf.version:match("^%d+[%.%d]*$")) then m.state, m.why = "bad", "manifest 没有 version="
        elseif not entry:match("^[%w_%-%.]+%.lua$") then m.state, m.why = "bad", "entry=" .. entry .. " 不合法"
        elseif not mf.game or mf.game == "" then m.state, m.why = "bad", "manifest 没有 game="
        elseif mf.game ~= "*" and mf.game ~= H.GAME then m.state, m.why = "bad", "适用于游戏 " .. mf.game .. "（现在是 " .. tostring(H.GAME) .. "）"
        elseif mf.needsFornet and H.verCmp(H.VERSION, mf.needsFornet) < 0 then m.state, m.why = "bad", "需要 4net " .. mf.needsFornet .. " 以上（现在 " .. H.VERSION .. "）"
        elseif exists(H.ROOT .. "/ext/" .. name .. "/manifest.txt") then m.state, m.why = "bad", "和插件 ext\\" .. name .. " 同名（网络命名空间会撞）"
        elseif not exists(dir .. "/on.txt") then m.state, m.why = "off", "没启用（目录里没有 on.txt）"
        else m.state = "on" end
        m.entry, m.needs, m.wants = entry, parseNeeds(mf and mf.needs), parseNeeds(mf and mf.wants)
        S.list[#S.list + 1], S.byName[name] = m, m
    end
    -- 依赖：缺 / 没启用 / 版本低 / 成环 → blocked（会传递：依赖 blocked 的也 blocked）
    local visiting = {}
    local function resolve(m)
        if m.state ~= "on" or m.resolved then return m.state == "on" end
        if visiting[m.name] then m.state, m.why = "blocked", "依赖成环"; return false end
        visiting[m.name] = true
        for _, d in ipairs(m.needs) do
            local dm = S.byName[d.name]
            if not dm then m.state, m.why = "blocked", "缺依赖 " .. d.name
            elseif d.min and H.verCmp(dm.version, d.min) < 0 then m.state, m.why = "blocked", string.format("依赖 %s 要 %s 以上（装的是 %s）", d.name, d.min, dm.version)
            elseif not resolve(dm) then
                if m.state == "on" then m.state, m.why = "blocked", "依赖 " .. d.name .. " 没加载（" .. tostring(dm.why) .. "）" end
            end
            if m.state ~= "on" then break end
        end
        visiting[m.name] = nil
        m.resolved = true
        return m.state == "on"
    end
    for _, m in ipairs(S.list) do resolve(m) end
    -- 加载前置：needs 全部 + wants 里装了、能加载的（软依赖：排在它后面，没装 / 没启用不影响自己）
    for _, m in ipairs(S.list) do
        m.after = {}
        if m.state == "on" then
            for _, d in ipairs(m.needs) do m.after[#m.after + 1] = d.name end
            for _, d in ipairs(m.wants) do
                local dm = S.byName[d.name]
                if dm and dm.state == "on" and dm.name ~= m.name then m.after[#m.after + 1] = d.name end
            end
        end
    end
    -- 拓扑序（Kahn），同一层按 order、再按名字；wants 互相指（成环）的剩下的按 order 补在最后
    local indeg, ready, order, done = {}, {}, {}, {}
    for _, m in ipairs(S.list) do
        if m.state == "on" then
            indeg[m.name] = #m.after
            if #m.after == 0 then ready[#ready + 1] = m end
        end
    end
    local function cmp(a, b) if a.order ~= b.order then return a.order < b.order end; return a.name < b.name end
    while #ready > 0 do
        table.sort(ready, cmp)
        local m = table.remove(ready, 1)
        order[#order + 1], done[m.name] = m, true
        for _, o in ipairs(S.list) do
            if o.state == "on" then
                for _, a in ipairs(o.after) do
                    if a == m.name then
                        indeg[o.name] = indeg[o.name] - 1
                        if indeg[o.name] == 0 then ready[#ready + 1] = o end
                    end
                end
            end
        end
    end
    local rest = {}
    for _, m in ipairs(S.list) do if m.state == "on" and not done[m.name] then rest[#rest + 1] = m end end
    table.sort(rest, cmp)
    for _, m in ipairs(rest) do order[#order + 1] = m; out("mod %s 的 wants 成环，按 order 排在最后", m.name) end
    S.order = order
    return S.list
end

function S.loadAll()
    if not H.ROOT then return 0 end
    S.scan()
    if #S.list == 0 then return 0 end
    local n = 0
    for _, m in ipairs(S.order) do
        -- 依赖在前面加载失败的，这里跟着不加载
        local depOk = true
        for _, d in ipairs(m.needs) do
            local dm = S.byName[d.name]
            if not (dm and dm.state == "on") then depOk = false; m.state, m.why = "blocked", "依赖 " .. d.name .. " 加载失败" end
        end
        if depOk then
            local chunk, err = loadfile(m.dir .. "/" .. m.entry)
            if not chunk then
                m.state, m.why = "error", "载入失败：" .. tostring(err)
            else
                m.handle = makeHandle(m)
                local ok, e = pcall(chunk, m.handle)
                if ok then m.state = "on"; n = n + 1 else m.state, m.why = "error", "运行出错：" .. tostring(e) end
            end
        end
    end
    for _, m in ipairs(S.list) do
        if m.state == "on" then out("mod %s v%s 已加载", m.name, m.version)
        else out("%smod %s v%s 没加载：%s", m.state == "off" and "" or "!! ", m.name, m.version, tostring(m.why)) end
    end
    installNet()
    S.emit("4n.modsLoaded", n)
    return n
end

-- 给"已装的玩法"页 / 状态用（界面在基础联机包）
function S.status()
    local r = {}
    for _, m in ipairs(S.list) do
        r[#r + 1] = { name = m.name, version = m.version, title = m.manifest.title, titleEn = m.manifest.titleEn, state = m.state, why = m.why }
    end
    return r
end
function S.onFile(name) return S.DIR and (S.DIR .. "/" .. name .. "/on.txt") or nil end

return S
end
