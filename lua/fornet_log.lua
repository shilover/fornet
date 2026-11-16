-- fornet_log.lua —— fornet（4net）UE4SS 网络核：日志（F4a）。
-- 从游戏模组的 main.lua 的"基础设施"段原样搬出： 日志攒着写 LOGBUF + out。不碰任何游戏对象。
-- 单独一个模块：它是最先要用的东西（fornet_core 等模块都拿 ctx.out），所以不能放进 fornet_core。
-- 用法：local L = require("fornet_log")({ ROOT=, LOG=, TAG=, keyPrints= }) → L.LOGBUF / L.out
-- ROOT = 模组目录；LOG = 日志文件名（实际写 ROOT/logs/<日期>/<LOG>）；TAG = 进 UE4SS 控制台那几行的前缀；
-- keyPrints = 除 "!!" / "==" 开头 / "管道" 之外还要进控制台的关键字（适配层给，比如版本行）
return function(ctx)
local ROOT, LOG, TAG = ctx.ROOT or ".", ctx.LOG or "fornet.log", ctx.TAG or "[fornet] "
local KEY = { "管道" }
for _, k in ipairs(ctx.keyPrints or {}) do KEY[#KEY + 1] = k end
local L = {}

-- 日志攒着写：以前每行都 打开 / 追加 / 关闭 一次 mp_probe.log（对局里每秒几行到几十行）。攒到 MAX 行或 SEC 秒写一次
-- （驱动每次也看一眼，没新日志时不会一直攒着）；带 "!!" 的（出错）马上写。游戏崩了最多丢最后 SEC 秒的普通日志。
-- 每行日志以前还 print 一份，UE4SS 再一行一行写进 ue4ss/UE4SS.log（对局里每秒十几行，剩下最频繁的写盘）。
-- 现在只有出错（"!!"）和少数关键行（"==" 开头、管道、版本）进 UE4SS 控制台 / UE4SS.log；全部日志照旧在 mp_probe.log。
-- 要看全部：PRINT_ALL = true
local LOGBUF = { lines = {}, at = 0, MAX = 50, SEC = 2.0, PRINT_ALL = false }
L.LOGBUF = LOGBUF
function LOGBUF.wantPrint(s)
    if LOGBUF.PRINT_ALL then return true end
    if s:find("!!", 1, true) ~= nil or s:sub(1, 2) == "==" then return true end
    for _, k in ipairs(KEY) do if s:find(k, 1, true) ~= nil then return true end end
    return false
end
function LOGBUF.flush()
    LOGBUF.at = os.clock()
    if #LOGBUF.lines == 0 then return end
    local text = table.concat(LOGBUF.lines)
    LOGBUF.lines = {}
    local path, day = LOGBUF.path()
    local ok, f = pcall(io.open, path, "a")
    if not (ok and f) and LOGBUF.mkDay ~= day then        -- 当天的文件夹还没有：建一次（一天最多一次 mkdir）
        LOGBUF.mkDay = day
        pcall(os.execute, string.format('mkdir "%s" 2>nul', (ROOT .. "/logs/" .. day):gsub("/", "\\")))
        ok, f = pcall(io.open, path, "a")
    end
    if not (ok and f) then ok, f = pcall(io.open, ROOT .. "/" .. LOG, "a") end   -- 建不了文件夹：退回模组目录下（以前的位置）
    if ok and f then
        f:write(text)
        f:close()
    end
end
-- 日志按天分文件夹：logs/2026-11-10/mp_probe.log（双开第二份 mp_probe_2.log）。
-- 按写入那一刻的日期取，跨零点自动换到新文件夹；行首仍只有 时:分:秒
function LOGBUF.path()
    local ok, day = pcall(os.date, "%Y-%m-%d")
    day = ok and day or "unknown"
    return ROOT .. "/logs/" .. day .. "/" .. LOG, day
end

function L.out(fmt, ...)
    local s = fmt
    if select("#", ...) > 0 then
        local ok, r = pcall(string.format, fmt, ...)
        s = ok and r or tostring(fmt)
    end
    if LOGBUF.wantPrint(tostring(s)) then print(TAG .. tostring(s) .. "\n") end
    local ok2, ts = pcall(os.date, "%H:%M:%S")
    local Ls = LOGBUF.lines
    Ls[#Ls + 1] = (ok2 and ts or "??:??:??") .. " " .. tostring(s) .. "\n"
    if #Ls >= LOGBUF.MAX or os.clock() - LOGBUF.at >= LOGBUF.SEC or tostring(s):find("!!", 1, true) then LOGBUF.flush() end
end

return L
end
