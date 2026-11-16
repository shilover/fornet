-- fornet_hud.lua —— fornet（4net）UE4SS 网络核：屏幕文字底座（docs/EXTENSIONS.md §10.4 F3b-6；docs/FORNET_SPLIT.md）。
-- 从游戏模组的 main.lua 原样搬出：颜色表、UMG 屏幕文字（UserWidget + CanvasPanel + TextBlock，贴在屏幕上）、退路 3D TextRender 文字、hudLog。
-- 只用 UE4SS 的 StaticFindObject / StaticConstructObject / FName / FText 和调用方给的玩家控制器 / GameStatics，不碰游戏对象。
-- 用法：local HD = require("fornet_hud")({ out=, valid=, try=, rawProp=, getP1PC=, gstat=, LANG=, HUD_SIZE=, MATCH=, setText=, ST= })
--   MATCH = 3D 文字的锚点表（MATCH.text / MATCH.hud / MATCH.shown）；setText = 3D 主文字；ST.mapLoads = 换过几次地图（换图后重建控件）
return function(ctx)
local out, valid, try, rawProp, getP1PC, gstat = ctx.out, ctx.valid, ctx.try, ctx.rawProp, ctx.getP1PC, ctx.gstat
local LANG, HUD_SIZE, MATCH, setText, ST = ctx.LANG, ctx.HUD_SIZE, ctx.MATCH, ctx.setText, ctx.ST
local H = {}
-- 一个 TextRender 只能有一种颜色和字号，所以每行准备三个文字物体：标签（右对齐）、值（左对齐）、居中整句。
-- 都挂在主文字物体上（SnapToTarget，跟着它一起贴在镜头前），主文字本身留空串当锚点。
-- 行的写法：{ c = 整句, col = 颜色, size = 字号倍数 } 或 { l = 标签, v = 值, col = 值的颜色 }；最多 HUD_ROWS 行
local COL_LABEL = { R = 255, G = 215, B = 90, A = 255 }    -- 金黄：普通说明
local COL_WHITE = { R = 240, G = 240, B = 240, A = 255 }
local COL_GRAY = { R = 170, G = 170, B = 170, A = 255 }
local COL_GREEN = { R = 90, G = 230, B = 110, A = 255 }
local COL_RED = { R = 255, G = 70, B = 70, A = 255 }
local COL_ORANGE = { R = 255, G = 140, B = 40, A = 255 }

local function hudPiece(align)
    local parent = MATCH.text
    local cls = StaticFindObject("/Script/Engine.TextRenderActor")
    local world = valid(parent) and try("hud world", function() return parent:GetWorld() end)
    local a = (valid(cls) and valid(world)) and try("spawn hud text", function()
        return world:SpawnActor(cls, { X = 0.0, Y = 0.0, Z = 0.0 }, { Pitch = 0.0, Yaw = 0.0, Roll = 0.0 })
    end) or nil
    local comp = valid(a) and rawProp(a, "TextRender") or nil
    if not valid(comp) then return nil end
    pcall(function() comp:SetHorizontalAlignment(align) end)   -- 0=左 1=中 2=右
    pcall(function() comp:SetVerticalAlignment(1) end)
    pcall(function() a:K2_AttachToActor(parent, FName("None"), 2, 2, 2, false) end)
    pcall(function() a:SetActorHiddenInGame(true) end)
    return { a = a, c = comp, orig = rawProp(comp, "Font") }
end

-- §309（多人玩法实测 11-14）：颜色表是 { R, G, B, A }（0–255）。对决 / 多人的 HUD 行以前写成小写 r / g / b，
-- 每帧在 pieceSet 的 format 里报错（对决模式下 tick 一直刷 "!! tick -> fornet_hud.lua:44"）；这里统一兜底，坏的按白色
local function normCol(c)
    if type(c) ~= "table" then return COL_WHITE end
    if c.R and c.G and c.B then return c end
    if c.r and c.g and c.b then return { R = c.r, G = c.g, B = c.b, A = c.a or 255 } end
    return COL_WHITE
end

local function pieceSet(p, s, col, size, dz)
    if not p or not valid(p.c) then return end
    col = normCol(col)
    if s == nil then
        if p.key ~= nil then p.key = nil; pcall(function() p.a:SetActorHiddenInGame(true) end) end
        return
    end
    -- 字体跟着语言：中文用 ChineseBlackFont，英文换回原来的默认字体
    local font = (LANG.zh and LANG.font) or p.orig
    local key = string.format("%s|%d,%d,%d|%.1f|%.1f|%s", s, col.R, col.G, col.B, size, dz, tostring(font == LANG.font))
    if p.key == key then return end
    p.key = key
    if font and p.font ~= font then
        local okS, errS = pcall(function() p.c:SetFont(font) end)
        if okS then p.font = font
        elseif not LANG.fontErr then LANG.fontErr = true; out("!! 文字 SetFont 失败：%s", tostring(errS)) end
    end
    local okF, ft = pcall(function() return FText(s) end)
    if not okF then return end
    pcall(function() p.c:SetWorldSize(HUD_SIZE * size) end)
    pcall(function() p.c:SetTextRenderColor(col) end)
    pcall(function() p.c:K2_SetText(ft) end)
    pcall(function() p.a:K2_SetActorRelativeLocation({ X = 0.0, Y = 0.0, Z = dz }, false, {}, true) end)
    pcall(function() p.a:SetActorHiddenInGame(false) end)
end

-- net-ui：联机面板最多 9 行，所以 3 → 9；文字物体按需要的行数逐步生成（平时还是只有 3 行 × 3 个）
local HUD_ROWS = 17         -- 房间设置拆成单独一页后设置页 11 项、房间设置页 7 项，回到 17；§105.3 / §106.5 曾是 16 项 / 19；v0.18.6：面板加了"输入地址 / 复制地址 / 设置"三行，9 → 12；v0.27.6 设置页 12 项（伤害倍率、全部可格挡），12 → 15；v0.27.21 设置页加感谢名单（13 项），15 → 16；§102 加"直连不通时用服务器中转"（14 项），16 → 17

local function setHud(rows)
    if rows and valid(MATCH.text) and #(MATCH.hud or {}) < math.min(#rows, HUD_ROWS) then
        MATCH.hud = MATCH.hud or {}
        local from = #MATCH.hud + 1
        for i = from, math.min(#rows, HUD_ROWS) do MATCH.hud[i] = { lab = hudPiece(2), val = hudPiece(0), mid = hudPiece(1) } end
        out("  HUD：生成第 %d–%d 行文字物体（每行 3 个）", from, #MATCH.hud)
    end
    -- 行高按字号算；从上往下排，整体居中在锚点
    local n, total, gap = 0, 0.0, HUD_SIZE * 0.25
    local h = {}
    for i = 1, HUD_ROWS do
        h[i] = (rows and rows[i]) and HUD_SIZE * (rows[i].size or 1.0) or 0
        if h[i] > 0 then n = n + 1; total = total + h[i] end
    end
    total = total + gap * math.max(0, n - 1)
    local dz, top = {}, total / 2
    for i = 1, HUD_ROWS do
        dz[i] = 0.0
        if h[i] > 0 then dz[i] = top - h[i] / 2; top = top - h[i] - gap end
    end
    for i, pr in ipairs(MATCH.hud or {}) do
        local r = rows and rows[i]
        local size, col = r and (r.size or 1.0) or 1.0, r and (r.col or COL_WHITE) or COL_WHITE
        pieceSet(pr.mid, r and r.c, col, size, dz[i])
        pieceSet(pr.lab, r and r.l, COL_LABEL, size, dz[i])
        pieceSet(pr.val, r and r.v and (" " .. r.v), col, size, dz[i])
    end
end

-- v0.18.5 屏幕文字改用 UMG（用户 2026-11-04："文字渲染不应该虚化"）：TextRender 是摆在镜头前 2 米的 3D 物体，
-- 会吃景深 / 动态模糊 / TAA，还会被镜头前的草挡住。UMG 是贴在屏幕上的 2D 界面，画在后处理之后，和游戏自己的 UI 一样清楚。
-- 做法（18:15 实机原型验证）：用引擎的空 UserWidget 类 Create，它的 WidgetTree 里放一个 CanvasPanel 当根，
-- 每行按需加 TextBlock（标签 / 值 / 居中整句三个），AddToViewport。默认字体 Roboto 缺的中文字形 Slate 会自动回退，中文能显示。
-- 任何一步失败就退回旧的 TextRender（HUD.failed），不影响对局
local HUD = { w = nil, cv = nil, rows = {}, side = {}, failed = false }
local SIDE_X, SIDE_Y, SIDE_PAD = 1.0, 0.16, 28     -- §310 右侧栏：屏幕右边、从高度 16% 往下排，离右边 28 像素
local HUD_PX = 34          -- 字号倍数 1.0 对应的像素
local HUD_Y = 0.30         -- 整块文字的中心在屏幕高度的这个比例处
local HUD_Z = 450          -- 视口层级（游戏自己的 HUD 在下面）

local function umgColor(c)
    c = normCol(c)
    return { SpecifiedColor = { R = c.R / 255, G = c.G / 255, B = c.B / 255, A = (c.A or 255) / 255 }, ColorUseRule = 0 }
end

local function umgRoot()
    -- v0.27.12 换过地图（HUD.gen 对不上）就不碰旧控件，连 IsInViewport 也不调（§93）
    if HUD.gen == ST.mapLoads and valid(HUD.w) and valid(HUD.cv) then
        local okV, inVp = pcall(function() return HUD.w:IsInViewport() end)
        if okV and inVp then return HUD.cv end
    end
    -- 换地图后旧的界面会被引擎回收：重建
    HUD.w, HUD.cv, HUD.rows, HUD.side = nil, nil, {}, {}
    local okP, pc = pcall(getP1PC)
    if not (okP and valid(pc)) then return nil end
    local ok, err = pcall(function()
        local w = StaticFindObject("/Script/UMG.Default__WidgetBlueprintLibrary"):Create(pc, StaticFindObject("/Script/UMG.UserWidget"), pc)
        local wt = w.WidgetTree
        local cv = StaticConstructObject(StaticFindObject("/Script/UMG.CanvasPanel"), wt)
        wt.RootWidget = cv
        w:AddToViewport(HUD_Z)
        w:SetVisibility(3)         -- HitTestInvisible：只显示，不挡鼠标
        HUD.w, HUD.cv, HUD.gen = w, cv, ST.mapLoads
    end)
    if not ok or not valid(HUD.cv) then
        HUD.failed = true
        out("!! 屏幕文字（UMG）建不起来：%s，改用旧的 3D 文字", tostring(err))
        return nil
    end
    out("屏幕文字（UMG）已建好")
    LANG.umg = true            -- UMG 显示中文不用 ChineseBlackFont（Slate 自动回退字体）
    return HUD.cv
end

-- 一个 TextBlock；ax = 水平对齐点（0 左端 / 0.5 中间 / 1 右端 落在屏幕中线上）
local function umgPiece(cv, ax, anX, anY)
    local tb = StaticConstructObject(StaticFindObject("/Script/UMG.TextBlock"), HUD.w.WidgetTree)
    local sl = cv:AddChildToCanvas(tb)
    anX, anY = anX or 0.5, anY or HUD_Y
    sl:SetAnchors({ Minimum = { X = anX, Y = anY }, Maximum = { X = anX, Y = anY } })
    sl:SetAlignment({ X = ax, Y = 0.5 })
    sl:SetAutoSize(true)
    tb:SetVisibility(1)
    return { tb = tb, sl = sl }
end

local function umgSet(p, s, col, size, y, x)
    if s == nil then
        if p.key ~= nil then p.key = nil; p.tb:SetVisibility(1) end      -- Collapsed
        return
    end
    x = x or 0.0
    local key = string.format("%s|%d,%d,%d|%.2f|%.1f|%.1f", s, col.R, col.G, col.B, size, y, x)
    if p.key == key then return end
    p.key = key
    p.tb:SetText(FText(s))
    p.tb:SetColorAndOpacity(umgColor(col))
    local f = p.tb.Font
    f.Size = math.floor(HUD_PX * size + 0.5)
    f.OutlineSettings.OutlineSize = 2
    f.OutlineSettings.OutlineColor = { R = 0, G = 0, B = 0, A = 0.85 }
    p.tb:SetFont(f)
    p.sl:SetPosition({ X = x, Y = y })
    p.tb:SetVisibility(3)
end

-- 成功返回 true；失败（或已判定 UMG 不可用）返回 false，由调用方走旧的 TextRender
local function umgHud(rows)
    if HUD.failed then return false end
    if rows == nil and not valid(HUD.w) then return true end
    local cv = umgRoot()
    if not cv then return false end
    -- v0.18.6：游戏暂停（Esc / 角色菜单打开）时整层藏起来。18:27 实测：这层字会盖在游戏菜单上面，把层级降到 -100 也一样
    -- （游戏菜单在自己的层上），所以只能藏
    local okP, paused = pcall(function() return gstat():IsGamePaused(getP1PC()) end)
    local hide = okP and paused == true
    if hide ~= HUD.hidden then
        HUD.hidden = hide
        if hide and MATCH.uiPaused then MATCH.uiPaused() end         -- §260 准备 / 对局中界面还是开出来了：取消暂停
        pcall(function() HUD.w:SetVisibility(hide and 1 or 3) end)
    end
    local ok, err = pcall(function()
        -- §310（11-14 实测）：常驻信息（计分板、剩余时间、对决排队……）标 side 的放屏幕右侧一栏，中间只留一次性提示
        local side = {}
        if rows then
            local mid = {}
            for _, r in ipairs(rows) do
                if r.side then side[#side + 1] = r else mid[#mid + 1] = r end
            end
            rows = mid
        end
        local ns = math.min(#side, HUD_ROWS)
        for i = #HUD.side + 1, ns do HUD.side[i] = umgPiece(cv, 1.0, SIDE_X, SIDE_Y) end
        local sy = 0.0
        for i, pc in ipairs(HUD.side) do
            local r = i <= ns and side[i] or nil
            local size = r and (r.size or 1.0) or 1.0
            local hh = HUD_PX * size * 1.3
            local txt = r and (r.c or ((r.l or "") .. " " .. (r.v or ""))) or nil
            umgSet(pc, txt, r and (r.col or COL_WHITE) or COL_WHITE, size, sy + hh / 2, -SIDE_PAD)
            if r then sy = sy + hh + HUD_PX * 0.15 end
        end
        local n = rows and math.min(#rows, HUD_ROWS) or 0
        HUD.rowY = {}
        for i = #HUD.rows + 1, n do
            HUD.rows[i] = { lab = umgPiece(cv, 1.0), val = umgPiece(cv, 0.0), mid = umgPiece(cv, 0.5) }
        end
        -- 行高按字号算；从上往下排，整体以 HUD_Y 为中心
        local h, total, gap = {}, 0.0, HUD_PX * 0.25
        for i = 1, n do h[i] = HUD_PX * (rows[i].size or 1.0) * 1.3; total = total + h[i] end
        total = total + gap * math.max(0, n - 1)
        local top = -total / 2
        for i, pr in ipairs(HUD.rows) do
            local r = i <= n and rows[i] or nil
            local y, size, col = 0.0, r and (r.size or 1.0) or 1.0, r and (r.col or COL_WHITE) or COL_WHITE
            if r then y = top + h[i] / 2; top = top + h[i] + gap; HUD.rowY[i] = y end
            umgSet(pr.mid, r and r.c, col, size, y)
            umgSet(pr.lab, r and r.l, COL_LABEL, size, y)
            umgSet(pr.val, r and r.v and (" " .. r.v), col, size, y)
        end
    end)
    if not ok then
        HUD.failed = true
        out("!! 屏幕文字（UMG）出错：%s，改用旧的 3D 文字", tostring(err))
        pcall(function() HUD.w:RemoveFromParent() end)
        return false
    end
    return true
end

-- 统一入口：rows=nil 时隐藏全部；有 rows 时主文字留空串当锚点（不是 nil：placeText 要它"显示中"才会摆位）
local function showHud(rows)
    if umgHud(rows) then
        if MATCH.shown ~= nil then setText(nil); setHud(nil) end     -- 旧的 3D 文字收起来
        return
    end
    setText(rows and "" or nil)
    setHud(rows)
end

local function hudLog(rows)
    if not rows then return nil end
    local parts = {}
    for _, r in ipairs(rows) do
        if not r.nolog then parts[#parts + 1] = r.c or ((r.l or "") .. " " .. (r.v or "")) end     -- §173 倒计时钟每秒变，不进日志
    end
    if #parts == 0 then return nil end
    return table.concat(parts, " / ")
end


-- 导出（main.lua 取别名；名字和搬之前一样）
H.COL_LABEL = COL_LABEL
H.COL_WHITE = COL_WHITE
H.COL_GRAY = COL_GRAY
H.COL_GREEN = COL_GREEN
H.COL_RED = COL_RED
H.COL_ORANGE = COL_ORANGE
H.HUD = HUD
H.HUD_PX = HUD_PX
H.HUD_ROWS = HUD_ROWS
H.HUD_Y = HUD_Y
H.hudLog = hudLog
H.setHud = setHud
H.showHud = showHud
H.umgColor = umgColor
H.umgRoot = umgRoot
H.umgSet = umgSet
return H
end
