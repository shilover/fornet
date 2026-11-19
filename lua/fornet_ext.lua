-- fornet_ext.lua：扩展包（X1；F5 前叫 game_ext.lua）。基底启动时扫 <模组目录>\ext\<名>\，每个包 = manifest.txt + init.lua（+ 可选 lobby_<名>.dll，X2）。
-- init.lua 跑在只有 API + 标准库副本的沙箱里（拿不到 require / io / os.* / _G / NETUI / LO / PIPE），所有对外发包只能经 API.lobby.cmd，
-- 基底在那里查该包登记的同意键；没登记 / 没同意一律拒。面板"系统设置 → 已加载的扩展…"列出每个包、同意状态、停用开关。
-- 不改任何现有行为：没有 ext\ 目录或目录为空时，这个模块只多一页"无扩展"。
-- X1 范围：扫描 / 沙箱 / consent.register / panel / room.addSetting（只登记）/ status.addField / lobby.cmd（X2 前只记日志）/ log。
return function(ctx)
    local out, ROOT, T, CONSENT, PREF, COL = ctx.out, ctx.ROOT, ctx.T, ctx.CONSENT, ctx.PREF, ctx.COL
    local fileReadKV, fileWrite, netuiCmd = ctx.readKV, ctx.writeFile, ctx.netuiCmd
    local flash = type(ctx.flash) == "function" and ctx.flash or function() end     -- 11-13：面板状态行的 3 秒提示（没给就不提示，日志照记）
    for _, k in ipairs({ "out", "ROOT", "T", "CONSENT", "readKV", "writeFile", "netuiCmd" }) do
        if ctx[k] == nil then error("fornet_ext：ctx 缺 " .. k, 2) end        -- 11-12 冒烟：T 为 nil 时页面每帧报错，改成一开始就拒绝加载
    end
    if type(COL) ~= "table" then error("fornet_ext：ctx 缺 COL", 2) end
    local MODDIR = tostring(ROOT):match("([^/\\]+)[/\\]*$") or "mod"     -- F5：提示里写模组自己的目录名，不写死游戏的
    if type(CONSENT) == "table" then CONSENT.extRoute = true; CONSENT.EXT_OWN = CONSENT.EXT_OWN or {} end     -- 11-13：扩展包的同意项去 ext:<包名> 插件页问（fornet_ui CONSENT.ask）
    local EXT = { list = {}, byName = {}, DIR = ROOT .. "/ext", BASE_VER = ctx.VERSION or "0", API_VER = 2 }
    -- L3：同意项的主人可能是玩法 mod（排位，mods\ranked）而不是插件；只有真是插件的才去 ext:<包名> 页问，mod 的走基底确认页
    if type(CONSENT) == "table" then CONSENT.extHas = function(n) return EXT.byName[n] ~= nil end end
    if type(CONSENT) == "table" then CONSENT.extSvc = function() for _, p in ipairs(EXT.list or {}) do if p.state == "on" and p.svc then return p.svc end end end end     -- 11-15 fornet_ui NETUI.friendsName 兜底 -- ：F4c-2 / F4c-3 起是 API 2（consent.registerBase / native.load），以前漏改成 1 = ext/桥接平台、ext/ranked 一直 bad

    -- ---------------------------------------------------------------- 工具
    local function exists(path) local f = io.open(path, "rb"); if f then f:close(); return true end; return false end
    local function vnum(v)      -- "0.36.1" → 可比较的数
        local a, b, c = tostring(v or "0"):match("^(%d+)%.?(%d*)%.?(%d*)")
        return (tonumber(a) or 0) * 1000000 + (tonumber(b) or 0) * 1000 + (tonumber(c) or 0)
    end
    local function safeName(n) return type(n) == "string" and n:match("^[%w_%-]+$") and #n <= 24 and n or nil end
    local function shallow(t) local r = {}; for k, v in pairs(t) do r[k] = v end; return r end

    -- 列子目录：优先 ext\ext.txt（一行一个名字，给没有 popen 的环境 / 想固定顺序的人）；没有就 dir /b /ad（UE4SS 的 Lua 有 io.popen）
    function EXT.listDirs()
        local names, seen = {}, {}
        local function add(n) n = safeName((n or ""):gsub("^%s+", ""):gsub("%s+$", "")); if n and not seen[n] then seen[n] = true; names[#names + 1] = n end end
        local f = io.open(EXT.DIR .. "/ext.txt", "r")
        if f then for line in f:lines() do if not line:match("^%s*#") then add(line) end end; f:close(); return names, "ext.txt" end
        local ok, p = pcall(io.popen, 'dir /b /ad "' .. EXT.DIR:gsub("/", "\\") .. '" 2>nul')
        if ok and p then for line in p:lines() do add(line) end; p:close(); return names, "dir" end
        return names, "none"
    end

    -- ---------------------------------------------------------------- 沙箱
    local function sandboxEnv(api)
        local env = {
            assert = assert, error = error, ipairs = ipairs, next = next, pairs = pairs, pcall = pcall, select = select,
            tonumber = tonumber, tostring = tostring, type = type, unpack = table.unpack, xpcall = xpcall,
            string = shallow(string), table = shallow(table), math = shallow(math), utf8 = utf8 and shallow(utf8) or nil,
            os = { time = os.time, clock = os.clock, date = os.date },
            API = api,
        }
        env._G = env
        return env
    end

    -- ---------------------------------------------------------------- 给扩展包的 API（每个包一份，闭包里带着自己的名字）
    local function makeApi(p)
        local api = {}
        local function tag() return "[ext:" .. p.name .. "]" end
        api.name, api.version, api.baseVersion, api.apiVersion = p.name, p.version, EXT.BASE_VER, EXT.API_VER
        api.log = { out = function(fmt, ...) out(tag() .. " " .. tostring(fmt), ...) end }
        api.T = T

        -- 同意键：登记进 CONSENT（文案由扩展包给；只在本包的插件页显示 / 同意 / 撤回，不进基底隐私说明；记录由基底管）
        api.consent = {}
        function api.consent.register(d)
            local id = safeName(d and d.id)
            if not id then error("consent.register：id 要是 [A-Za-z0-9_-]{1,24}", 2) end
            local key = "ext_" .. p.name .. "_" .. id
            local zh, en = d.zh, d.en
            if type(zh) == "string" then zh = { zh } end
            if type(en) == "string" then en = { en } end
            if type(zh) ~= "table" or #zh == 0 then error("consent.register：要有 zh 文案", 2) end
            en = (type(en) == "table" and #en > 0) and en or zh
            CONSENT.TEXT[key] = { ver = tonumber(d.ver) or 1, zh = zh, en = en, ext = p.name }
            CONSENT.PRIVACY_TITLE[key] = { "扩展 " .. p.name .. "：" .. (d.title or id), "Ext " .. p.name .. ": " .. (d.titleEn or d.title or id) }
            p.consents[#p.consents + 1] = key     -- 11-13：不进基底的隐私说明（PRIVACY_ORDER），文案和同意只在本包的插件页
            return key
        end
        function api.consent.has(id) return CONSENT.has("ext_" .. p.name .. "_" .. tostring(id)) end
        -- 请基底弹确认（同意过直接做）。只能请求，不能自己画窗
        function api.consent.ask(id, fn) return CONSENT.ask("ext_" .. p.name .. "_" .. tostring(id), fn) end
        -- F4c-2：提供桥接 DLL 的扩展包给基底固定的两个键（bridge_match / bridge_friends）登记文案（基底自己不写任何具体服务的名字）
        function api.consent.registerBase(key, d)
            if type(CONSENT.registerBase) ~= "function" then error("这个基底不支持 consent.registerBase", 2) end
            CONSENT.registerBase(key, d or {}, p.name)
            p.consents[#p.consents + 1] = key
            return key
        end

        -- 面板：只能加，不能改基底的页。pageId："main"（联机首页底部）/ "settings"（系统设置）/ 自己的页 "ext:<名>"
        api.panel = {}
        function api.panel.addRows(pageId, fn)
            if type(fn) ~= "function" then error("panel.addRows：要给函数", 2) end
            p.rows[pageId] = p.rows[pageId] or {}
            p.rows[pageId][#p.rows[pageId] + 1] = fn
        end
        function api.panel.addPage(d)
            if type(d) ~= "table" or type(d.build) ~= "function" then error("panel.addPage：要 {id, title, build}", 2) end
            local id = "ext:" .. p.name .. (d.id and (":" .. tostring(d.id)) or "")
            p.pages[id] = { title = d.title or p.name, titleEn = d.titleEn, build = d.build }
            return id
        end

        -- 房间设置：X1 只登记（存在 EXT.roomSettings，X2+ 接进房间设置页 / 状态包）；带 consent 的没同意不能开
        api.room = {}
        function api.room.addSetting(d)
            local key = safeName(d and d.key)
            if not key then error("room.addSetting：key 要是 [A-Za-z0-9_-]{1,24}", 2) end
            p.roomSettings[#p.roomSettings + 1] = { key = key, zh = d.zh or key, en = d.en or d.zh or key, default = d.default or 0, consent = d.consent }
        end

        -- 状态包扩展段：x.<包>.<key>=<值>，基底原样转发不解释；对方没装就忽略。值里不能有换行
        api.status = {}
        function api.status.addField(key, fn)
            key = safeName(key)
            if not key or type(fn) ~= "function" then error("status.addField：key [A-Za-z0-9_-]{1,24} + 函数", 2) end
            p.fields[#p.fields + 1] = { key = key, fn = fn }
        end

        -- 联机程序：发命令 / 收回话。发之前查这个包登记的全部同意键（一个没同意就拒）。X1 没有 C# 插件，命令只记日志
        api.lobby = {}
        function api.lobby.cmd(kv)
            for _, k in ipairs(p.consents) do
                if not CONSENT.has(k) then out("%s 未同意 %s，拒发 lobby.cmd", tag(), k); return false, "consent" end
            end
            if #p.consents == 0 then out("%s 没登记任何同意键，拒发 lobby.cmd", tag()); return false, "noconsent" end
            local lines = {}
            for k, v in pairs(kv or {}) do
                k = safeName(k); if k then lines[#lines + 1] = k .. "=" .. tostring(v):gsub("[\r\n]", " ") end
            end
            table.sort(lines)
            if not EXT.lobbyReady then out("%s lobby.cmd 暂不可用（联机程序插件接口 X2）：%s", tag(), table.concat(lines, " ")); return false, "noplugin" end
            netuiCmd("ext", "plugin=" .. p.name .. "\n" .. table.concat(lines, "\n") .. "\n")
            return true
        end
        function api.lobby.onReply(fn) if type(fn) == "function" then p.onReply[#p.onReply + 1] = fn end end

        -- 只读：这局的传输路（"udp" 直连 / "bridge" 经扩展包的桥接 DLL）。第三方 mod 可以据此选在桥接房里不发自己的字段
        api.net = { transport = function() local ok, v = pcall(ctx.transport); return ok and v or "udp" end }
        -- F4c-2：把本包目录里的一个原生 DLL 载进游戏进程（package.loadlib 的 "*" 只 LoadLibrary，不用 Lua 的 C 接口）。
        -- 只认本包目录下的文件、名字只能是 字母数字_-.dll；装一个扩展包 = 同意运行它的代码，这里不再弹窗。桥接 DLL（例如桥接平台的）就是这样进来的
        api.native = {}
        function api.native.load(dllName)
            if type(dllName) ~= "string" or not dllName:match("^[%w_%-]+%.dll$") then error("native.load：文件名要是 <字母数字_->.dll", 2) end
            local path = (p.dir .. "/" .. dllName):gsub("/", "\\")
            local f = io.open(path, "rb")
            if not f then out("%s native.load：没有 %s", tag(), path); return false, "missing" end
            f:close()
            if type(package) ~= "table" or type(package.loadlib) ~= "function" then out("!! %s native.load：这个 Lua 没有 package.loadlib", tag()); return false, "noloadlib" end
            local ok, err = package.loadlib(path, "*")
            out("%s native.load %s -> %s %s", tag(), dllName, tostring(ok), tostring(err or ""))
            return ok ~= nil, err
        end
        return api
    end

    -- ---------------------------------------------------------------- 加载一个包
    -- 11-13 定：扩展包默认停用。目录里有 on.txt 才加载（面板插件页"启用"写它、"停用"删它，都是下次启动生效）。
    -- 以前的 off.txt 不再看（没有 on.txt 一律停用）；启用时顺手删掉留下的 off.txt
    function EXT.onFile(name) return EXT.DIR .. "/" .. name .. "/on.txt" end
    function EXT.offFile(name) return EXT.DIR .. "/" .. name .. "/off.txt" end
    function EXT.wanted(name) return exists(EXT.onFile(name)) end
    function EXT.disabled(name) return not EXT.wanted(name) end
    -- 开关只改文件，不动这次已经加载的状态（p.state）；p.want = 下次启动是否加载。两者不一样 = 等重启
    function EXT.setWanted(p, on)
        if on then fileWrite(EXT.onFile(p.name), "on\n"); os.remove(EXT.offFile(p.name)) else os.remove(EXT.onFile(p.name)) end
        p.want = EXT.wanted(p.name)
        return p.want
    end
    function EXT.restartPending(p) return p.want ~= nil and p.want ~= (p.state ~= "off") end
    local function stateLabel(p)
        local st = p.state == "on" and T("启用", "ON") or p.state == "off" and T("已停用", "OFF") or T("加载失败", "FAILED")
        if EXT.restartPending(p) then st = st .. (p.want and T("（重启游戏后启用）", " (ENABLED AFTER RESTART)") or T("（重启游戏后停用）", " (DISABLED AFTER RESTART)")) end
        return st
    end
    local function loadOne(name)
        local dir = EXT.DIR .. "/" .. name
        local p = { name = name, dir = dir, version = "?", title = name, author = "", consents = {}, rows = {}, pages = {}, roomSettings = {}, fields = {}, onReply = {}, state = "loading", want = EXT.wanted(name) }
        EXT.list[#EXT.list + 1] = p; EXT.byName[name] = p
        local mf = fileReadKV(dir .. "/manifest.txt")
        if not mf or not next(mf) then p.state, p.why = "bad", T("没有 manifest.txt", "no manifest.txt"); return p end
        p.version, p.title, p.author, p.lobby = mf.version or "?", mf.title or name, mf.author or "", mf.lobby
        if mf.name and mf.name ~= name then p.state, p.why = "bad", T("manifest 的 name 和目录名不一致", "manifest name != folder"); return p end
        if mf.needsBase and vnum(mf.needsBase) > vnum(EXT.BASE_VER) then
            p.state, p.why = "bad", T("需要基底 ≥ " .. mf.needsBase, "needs base >= " .. mf.needsBase); return p
        end
        if mf.api and tonumber(mf.api) and tonumber(mf.api) > EXT.API_VER then p.state, p.why = "bad", T("需要 API " .. mf.api, "needs API " .. mf.api); return p end
        p.adapter, p.client = mf.adapter, mf.client
        p.svc = mf.svc     -- 11-15 桥接服务的显示名（manifest svc=，如桥接平台）：联机程序还没和桥接 DLL 握手时，好友标签先用它
        if EXT.disabled(name) then p.state = "off"; return p end
        for item in tostring(mf.consent or ""):gmatch("[^,%s]+") do     -- consent=@ranked：基底键 ranked 的文案 / 同意由本包管（插件页里问）
            local k = item:match("^@([%w_]+)$")
            if k and type(CONSENT) == "table" and CONSENT.EXT_OWN then CONSENT.EXT_OWN[k] = name end
        end
        local f = io.open(dir .. "/init.lua", "rb")
        if not f then p.state, p.why = "bad", T("没有 init.lua", "no init.lua"); return p end
        local src = f:read("a"); f:close()
        local api = makeApi(p)
        local chunk, err = load(src, "=ext/" .. name .. "/init.lua", "t", sandboxEnv(api))
        if not chunk then p.state, p.why = "bad", tostring(err); return p end
        local ok, ret = pcall(chunk)
        if ok and type(ret) == "function" then ok, ret = pcall(ret, api) end
        if not ok then p.state, p.why = "bad", tostring(ret); return p end
        if type(ret) == "table" then p.version = ret.version or p.version; p.lobby = ret.needsLobbyPlugin or p.lobby end
        p.state = "on"
        return p
    end

    function EXT.scan()
        EXT.list, EXT.byName = {}, {}
        local names, how = EXT.listDirs()
        for _, n in ipairs(names) do
            local okL, e = pcall(loadOne, n)
            local p = EXT.byName[n]
            if not okL and p then p.state, p.why = "bad", tostring(e) end
        end
        local on, off, bad = 0, 0, 0
        for _, p in ipairs(EXT.list) do if p.state == "on" then on = on + 1 elseif p.state == "off" then off = off + 1 else bad = bad + 1 end end
        if #EXT.list > 0 or how ~= "none" then
            out("扩展包：%d 个（启用 %d / 停用 %d / 失败 %d，列目录用 %s）", #EXT.list, on, off, bad, how)
            for _, p in ipairs(EXT.list) do
                out("  ext/%s v%s：%s%s%s", p.name, tostring(p.version), p.state, p.why and ("（" .. p.why .. "）") or "",
                    #p.consents > 0 and ("，同意键 " .. table.concat(p.consents, ",")) or "")
            end
        end
        return EXT.list
    end

    -- ---------------------------------------------------------------- 基底每帧 / 建面板时调的
    -- 状态包扩展段（接在基底字段后面）
    function EXT.wire()
        local sb = {}
        for _, p in ipairs(EXT.list) do
            if p.state == "on" then
                for _, fd in ipairs(p.fields) do
                    local ok, v = pcall(fd.fn)
                    if ok and v ~= nil then sb[#sb + 1] = "x." .. p.name .. "." .. fd.key .. "=" .. tostring(v):gsub("[\r\n]", " ") .. "\n" end
                end
            end
        end
        return table.concat(sb)
    end
    -- 联机程序回话（lobby.txt 的 kv）里 x.<包>.<k> 分发给各包
    function EXT.poll(lb)
        if not lb then return end
        EXT.lobbyReady = (lb.extp or "") ~= ""      -- X2：联机程序报了就绪的插件名单
        EXT.lobbyPlugins = lb.extp or ""
        for _, p in ipairs(EXT.list) do
            if p.state == "on" and #p.onReply > 0 then
                local mine, any = {}, false
                local pre = "x." .. p.name .. "."
                for k, v in pairs(lb) do if k:sub(1, #pre) == pre then mine[k:sub(#pre + 1)] = v; any = true end end
                if any then for _, fn in ipairs(p.onReply) do pcall(fn, mine) end end
            end
        end
    end
    -- 各包往基底页（main / settings）加的行
    function EXT.rows(pageId, items, lb)
        for _, p in ipairs(EXT.list) do
            if p.state == "on" and p.rows[pageId] then
                for _, fn in ipairs(p.rows[pageId]) do
                    local ok, e = pcall(fn, items, T, lb)
                    if not ok then out("[ext:%s] panel rows(%s) 出错：%s", p.name, pageId, tostring(e)) end
                end
            end
        end
    end
    -- 扩展包自己的页（NETUI.page == "ext:<名>[:id]"）
    function EXT.pageOf(pageId)
        local n = pageId:match("^ext:([%w_%-]+)")
        local p = n and EXT.byName[n]
        return p and p.pages[pageId], p
    end
    function EXT.pageTitle(pageId)
        local pg = EXT.pageOf(pageId)
        if pageId == "ext" then return T("联机 · 已加载的扩展", "ONLINE · EXTENSIONS") end
        local n = pageId:match("^ext:([%w_%-]+)$")
        if n and EXT.byName[n] then local p = EXT.byName[n]; return T("扩展 · ", "EXTENSION · ") .. tostring(p.title or n) end     -- 11-13 插件页
        return pg and T(pg.title, pg.titleEn or pg.title) or nil
    end
    -- 11-13 拆干净：每个包自动有一个插件页 ext:<包名>——包的说明、它的全部同意项（完整文案 / 同意 / 撤回）、待同意的操作、自己的页、停用开关。
    -- 基底的面板和隐私说明里不出现扩展包的任何文案（桥接平台 / 排位等）；基底要用到扩展包的同意项时跳到这里（CONSENT.ask）
    local function wrap(items, line, indent, col)
        if type(CONSENT) == "table" and type(CONSENT.wrap) == "function" then CONSENT.wrap(items, line, indent, col)
        else items[#items + 1] = { label = (indent or "") .. line, col = col } end
    end
    function EXT.ownedKeys(name)     -- 归这个包的同意键：先按登记顺序（p.consents），再补经 addMode 等登记、带 ext = 包名的
        local p, out2, seen = EXT.byName[name], {}, {}
        if p then for _, k in ipairs(p.consents) do if not seen[k] then seen[k] = true; out2[#out2 + 1] = k end end end
        local more = {}
        for k, t in pairs(CONSENT.TEXT or {}) do if type(t) == "table" and t.ext == name and not seen[k] then more[#more + 1] = k end end
        table.sort(more)
        for _, k in ipairs(more) do out2[#out2 + 1] = k end
        return out2
    end
    function EXT.clientOf(name, adapter)     -- 游戏适件用：这个包给 <adapter> 的全权限代码路径（包启用、manifest adapter= 对得上才给）
        local p = EXT.byName[name]
        if p and p.state == "on" and p.client and p.adapter == adapter and p.client:match("^[%w_%-%.]+%.lua$") then return p.dir .. "/" .. p.client end
        return nil
    end
    local function consentTitle(k)
        local tt = CONSENT.PRIVACY_TITLE and CONSENT.PRIVACY_TITLE[k]
        return tt and T(tt[1], tt[2]) or k
    end
    local function consentText(items, k, indent)
        local t = CONSENT.TEXT[k]
        if not t then return end
        if CONSENT.PER_SERVER and CONSENT.PER_SERVER[k] and type(CONSENT.serverAddr) == "function" then
            local addr = CONSENT.serverAddr()
            items[#items + 1] = { label = (indent or "") .. string.format(T("将连接的大厅服务器：%s（你在系统设置里填的）", "LOBBY SERVER: %s (SET BY YOU IN SYSTEM SETTINGS)"), addr ~= "" and addr or T("未填", "NOT SET")), col = COL.orange }
        end
        for _, line in ipairs(T("zh", "en") == "zh" and t.zh or t.en) do wrap(items, line, indent) end
    end
    function EXT.pluginItems(p, items, page)
        if p.notice then      -- 11-13：点了启用 / 停用 → 页顶弹一块提示，点"知道了"才收（状态行的 3 秒提示太容易错过）
            -- 11-15：5 行缩成 2 行（插件页行数一多，底下的启用 / 停用按钮会被挤出面板）
            items[#items + 1] = { label = (p.notice == "on" and T("已启用，", "ENABLED; ") or T("已停用，", "DISABLED; ")) .. T("重启游戏后才生效（这次游戏里保持原样）", "APPLIES AFTER A GAME RESTART"), col = COL.orange }
            items[#items + 1] = { label = T("知道了", "OK"), act = function() p.notice = nil end }
        end
        local st = stateLabel(p)
        items[#items + 1] = { label = string.format("■ %s  v%s  %s%s", p.title or p.name, tostring(p.version), st, p.author ~= "" and ("  " .. T("作者：", "by ") .. p.author) or ""),
                              col = EXT.restartPending(p) and COL.orange or p.state == "on" and COL.green or p.state == "off" and COL.label or COL.orange }
        if EXT.restartPending(p) then items[#items + 1] = { label = "  " .. T("需要重启游戏才生效", "RESTART THE GAME TO APPLY"), col = COL.orange } end
        if p.state == "off" and not p.want then items[#items + 1] = { label = "  " .. T("扩展包默认停用；启用后重启游戏才会加载", "EXTENSIONS ARE OFF BY DEFAULT; ENABLE, THEN RESTART THE GAME") } end
        if p.why then items[#items + 1] = { label = "  " .. p.why, col = COL.orange } end
        local pd = CONSENT.pending
        local pendKey = pd and CONSENT.TEXT[pd.key] and CONSENT.TEXT[pd.key].ext == p.name and pd.key or nil
        if pendKey then
            items[#items + 1] = { label = T("要继续刚才的操作，需要先同意这个扩展的这一项：", "TO CONTINUE, AGREE TO THIS EXTENSION'S ITEM:"), col = COL.orange }
            items[#items + 1] = { label = "■ " .. consentTitle(pendKey), col = COL.label }
            consentText(items, pendKey, "  ")
            items[#items + 1] = { label = T("（同意后不再问；文案更新会再问一次）", "(ASKED ONCE; ASKED AGAIN IF THIS TEXT CHANGES)") }
            items[#items + 1] = { label = T("同意并继续", "AGREE AND CONTINUE"), act = function() CONSENT.agreePending() end }
            items[#items + 1] = { label = T("取消", "CANCEL"), act = function() CONSENT.cancelPending() end }
        end
        -- 11-15 用户："这种多个平级的确认会导致界面放不下，要么多一层 title 各自显示"、"我同意协议和启用应该是一个意思"：
        -- 这里只列条款标题（点开 ctext 页看全文）；启用 = 同意全部条款，停用 = 一起撤回（没有单项撤回了）
        local keys = {}
        for _, k in ipairs(EXT.ownedKeys(p.name)) do if k ~= pendKey then keys[#keys + 1] = k end end
        local allKeys = EXT.ownedKeys(p.name)
        if #keys > 0 then
            items[#items + 1] = { label = T("启用这个插件 = 同意下面这些条款（点开看全文）：", "ENABLING THIS PLUGIN = AGREEING TO THESE TERMS (OPEN TO READ):"), col = COL.label }
            CONSENT.titleRows(items, keys)
        end
        if p.state == "on" and p.lobby then items[#items + 1] = { label = T("联机程序插件：", "LOBBY PLUGIN: ") .. p.lobby, col = COL.label } end
        for id, pg in pairs(p.pages) do
            if p.state == "on" and id ~= "ext:" .. p.name then items[#items + 1] = { label = T(pg.title, pg.titleEn or pg.title) .. "…", act = page(id) } end
        end
        if p.state ~= "bad" or p.want then      -- 加载失败的包：已启用的能停用；manifest 有问题、没启用的不给启用
            local want = p.want
            local missing = false
            for _, k in ipairs(allKeys) do if not CONSENT.has(k) then missing = true end end
            if want and missing and not pendKey then     -- 启用着但条款没同意（旧版本留下的 / 以前单项撤回过）
                items[#items + 1] = { label = T("同意上面的条款", "AGREE TO THE TERMS"),
                                      act = function() CONSENT.grantKeys(allKeys); out("扩展包 %s：同意 %s", p.name, table.concat(allKeys, ",")) end }
            end
            local has = #allKeys > 0
            items[#items + 1] = { label = want and (has and T("停用（同时撤回同意，重启生效）", "DISABLE (ALSO WITHDRAWS TERMS; RESTART)") or T("停用（重启生效）", "DISABLE (RESTART)"))
                                          or (has and T("同意并启用（重启生效）", "AGREE AND ENABLE (RESTART)") or T("启用（重启生效）", "ENABLE (RESTART)")),
                                  act = function()
                                      if want then CONSENT.revokeKeys(allKeys) else CONSENT.grantKeys(allKeys) end
                                      if has then out("扩展包 %s：%s %s", p.name, want and "撤回" or "同意", table.concat(allKeys, ",")) end
                                      local now = EXT.setWanted(p, not want)
                                      p.notice = now and "on" or "off"
                                      local zh = now and "已启用" or "已停用"
                                      flash(T(zh .. " " .. tostring(p.title or p.name) .. "，重启游戏生效", (now and "ENABLED " or "DISABLED ") .. tostring(p.name) .. ", RESTART THE GAME TO APPLY"), COL.orange)
                                      out("扩展包 %s：%s（重启游戏生效）", p.name, now and "启用" or "停用")
                                  end }
        end
    end
    function EXT.pageItems(lb, items, page)
        local plugin = lb and lb._page and lb._page:match("^ext:([%w_%-]+)$")
        if plugin and EXT.byName[plugin] then      -- 插件页（自动）+ 包自己 addPage 不带 id 的那页接在后面
            local p = EXT.byName[plugin]
            EXT.pluginItems(p, items, page)
            local pg = p.pages[lb._page]
            if pg and p.state == "on" then
                local ok, e = pcall(pg.build, items, T, lb)
                if not ok then items[#items + 1] = { label = T("页出错：", "PAGE ERROR: ") .. tostring(e), col = COL.orange } end
            end
            items[#items + 1] = { label = T("返回", "BACK"), act = function() if CONSENT.pending then CONSENT.cancelPending() else page("ext")() end end }
            return items
        end
        if lb and lb._page and lb._page ~= "ext" then      -- 扩展包自己的页
            local pg, p = EXT.pageOf(lb._page)
            if pg then
                local ok, e = pcall(pg.build, items, T, lb)
                if not ok then items[#items + 1] = { label = T("页出错：", "PAGE ERROR: ") .. tostring(e), col = COL.orange } end
            end
            items[#items + 1] = { label = T("返回", "BACK"), act = page("ext") }
            return items
        end
        -- "已加载的扩展"总页
        if #EXT.list == 0 then
            items[#items + 1] = { label = T("没有扩展包（把扩展放到 " .. MODDIR .. "\\ext\\<名字>\\ 下，重启游戏）", "NO EXTENSIONS (put them in " .. MODDIR .. "\\ext\\<name>\\, restart)"), col = COL.label }
        else
            items[#items + 1] = { label = T("扩展包默认停用。在插件页里启用 / 停用，都要重启游戏才生效。", "EXTENSIONS ARE OFF BY DEFAULT. ENABLING OR DISABLING ONE TAKES EFFECT AFTER A GAME RESTART."), col = COL.orange }
        end
        for _, p in ipairs(EXT.list) do
            local st = stateLabel(p)
            items[#items + 1] = { label = string.format("■ %s  v%s  %s%s", p.title or p.name, tostring(p.version), st, p.author ~= "" and ("  " .. T("作者：", "by ") .. p.author) or ""),
                                  col = EXT.restartPending(p) and COL.orange or p.state == "on" and COL.green or p.state == "off" and COL.label or COL.orange }
            if p.why then items[#items + 1] = { label = "    " .. p.why, col = COL.orange } end
            local keys, agreed = EXT.ownedKeys(p.name), 0
            for _, k in ipairs(keys) do if CONSENT.has(k) then agreed = agreed + 1 end end
            items[#items + 1] = { label = "    " .. T("打开插件页（说明 / 条款 / 启用停用）…", "OPEN (INFO / TERMS / ON-OFF)...")
                                          .. (#keys > 0 and string.format(T("  同意项 %d（已同意 %d）", "  %d consent items (%d agreed)"), #keys, agreed) or ""),
                                  act = page("ext:" .. p.name) }
        end
        -- 11-13 用户没看懂原来那句（"由各自作者负责；对外发包都要先经你同意（见隐私说明）"），且基底隐私说明里已没有扩展包的内容
        items[#items + 1] = { label = T("扩展包不是这个模组自带的功能，由它的作者负责。", "Extensions are not part of this mod; their authors are responsible for them."), col = COL.label }
        items[#items + 1] = { label = T("在插件页启用 = 同意它列出的条款（点开能看全文），重启游戏生效；停用会一起撤回。", "Enabling a plugin on its page = agreeing to the terms it lists (open them to read); restart to apply. Disabling withdraws them."), col = COL.label }
        items[#items + 1] = { label = T("返回", "BACK"), act = page("settings") }
        return items
    end

    return EXT
end
