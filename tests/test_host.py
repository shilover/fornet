# 4net 自带测试：路径以本目录的上一级（仓库根）为根。
# F6 fornet 宿主（fornet/lua/fornet_host.lua → 部署成 <模组>\Scripts\main.lua）：lupa 跑真的宿主，假模组目录里放游戏适件。
# 用法：python -X utf8 -I test_fornet_host.py <lupa 目录>
# ① 找到 games\demo\，执行适件入口，FORNET.ROOT / GAME_DIR / SCRIPTS 对；适件能 require 自己目录的模块和 Scripts\ 的 fornet_*
# ② games.txt 决定用哪个；没有 games.txt 时按目录名取第一个
# ③ 没有 games\ / manifest 缺 / name 对不上 / needsFornet 太高 / entry 不合法 / 入口报错：都只记日志，不抛出
# ④ 真的 mod/<mod>/Scripts/main.lua 开头：有宿主时模组目录用 FORNET.ROOT、不覆盖 FORNET；没宿主时补一个 standalone 的 FORNET
import os, shutil, sys, tempfile
sys.path.insert(0, sys.argv[1])
import lupa.lua54 as L

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
HOST = open(os.path.join(REPO, "lua", "fornet_host.lua"), encoding="utf-8").read()
fails = []
def check(ok, name, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("  " + str(detail) if detail else ""), flush=True)
    if not ok: fails.append(name)

def w(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, "w", encoding="utf-8").write(text)

def mkmod(games, games_txt=None):
    root = tempfile.mkdtemp(prefix="fornet_host_").replace("\\", "/")
    w(root + "/Scripts/main.lua", HOST)
    w(root + "/Scripts/fornet_dummy.lua", "return { tag = 'fornet' }\n")
    for name, (manifest, files) in games.items():
        if manifest is not None: w("%s/games/%s/manifest.txt" % (root, name), manifest)
        for fn, text in files.items(): w("%s/games/%s/%s" % (root, name, fn), text)
    if games_txt is not None: w(root + "/games/games.txt", games_txt)
    return root

ADAPTER = r'''
RAN = (RAN or 0) + 1
GOT = { root = FORNET.ROOT, gdir = FORNET.GAME_DIR, scripts = FORNET.SCRIPTS, game = FORNET.GAME, ver = FORNET.VERSION,
        own = require("adapter_mod").tag, core = require("fornet_dummy").tag }
'''

def run(root, popen=True):
    lua = L.LuaRuntime(unpack_returned_tuples=True)
    lua.execute("LOGS = {}; function print(s) LOGS[#LOGS + 1] = s end")
    lua.globals().ROOTDIR = root
    # 模拟 UE4SS：package.path 里有本模组 Scripts\；debug.getinfo 报的是 Scripts\main.lua
    lua.execute(r'''
package.path = ROOTDIR .. "/Scripts/?.lua;" .. package.path
local real = debug.getinfo
debug.getinfo = function(l, w) local t = real(l, w); if t then t.source = "@" .. ROOTDIR .. "/Scripts/main.lua" end; return t end
''')
    if not popen:
        lua.execute("io.popen = nil")
    lua.execute(HOST)
    logs = [lua.eval("LOGS")[i] for i in range(1, len(lua.eval("LOGS")) + 1)]
    return lua, "\n".join(logs)

ok_files = {"main.lua": ADAPTER, "adapter_mod.lua": "return { tag = 'adapter' }\n"}

# ① 正常
root = mkmod({"demo": ("name=demo\nentry=main.lua\nneedsFornet=0.1.0\ntitle=T\n", ok_files)}, "demo\n")
lua, log = run(root)
g = lua.eval("GOT")
check(lua.eval("RAN") == 1, "① 适件入口执行了一次", log)
check(g and g.root == root and g.gdir == root + "/games/demo" and g.scripts == root + "/Scripts", "① FORNET.ROOT / GAME_DIR / SCRIPTS", g and (g.root, g.gdir))
check(g and g.own == "adapter" and g.core == "fornet", "① 适件能 require 自己目录和 Scripts\\ 的模块")
check(g and g.game == "demo" and g.ver, "① FORNET.GAME / VERSION")
check("装游戏适件 demo" in log, "① 日志写了装的哪个", log)

# ② 选哪个
two = {"aaa": ("name=aaa\n", ok_files), "demo": ("name=demo\n", {"main.lua": "PICKED = 'demo'\n"})}
lua, log = run(mkmod(two, "# 注释\ndemo\n"))
check(lua.eval("PICKED") == "demo", "② games.txt 第一行（跳过注释）决定用哪个")
lua, log = run(mkmod({"zzz": ("name=zzz\n", {"main.lua": "PICKED = 'zzz'\n"}), "bbb": ("name=bbb\n", {"main.lua": "PICKED = 'bbb'\n"})}))
check(lua.eval("PICKED") == "bbb" and "有 2 个游戏适件" in log, "② 没有 games.txt：按目录名排序取第一个、记日志", log)
lua, log = run(mkmod({"demo": ("name=demo\n", {"main.lua": "PICKED = 'demo'\n"})}), popen=False)
check(lua.eval("PICKED") is None and "没有游戏适件" in log, "② 没有 games.txt 也没有 io.popen：不装、记日志", log)

# ③ 各种不对
def bad(games, games_txt, want, name):
    lua, log = run(mkmod(games, games_txt))
    check(lua.eval("RAN") is None and want in log, name, log)
bad({}, None, "没有游戏适件", "③ 没有 games\\ 目录")
bad({"demo": (None, ok_files)}, "demo\n", "没有 manifest", "③ manifest 缺")
bad({"demo": ("name=other\n", ok_files)}, "demo\n", "不一致", "③ name 和目录对不上")
bad({"demo": ("name=demo\nneedsFornet=9.0.0\n", ok_files)}, "demo\n", "需要宿主 9.0.0", "③ needsFornet 太高")
bad({"demo": ("name=demo\nentry=../x.lua\n", ok_files)}, "demo\n", "不合法", "③ entry 带路径")
lua, log = run(mkmod({"demo": ("name=demo\n", {"main.lua": "RAN = 1\nerror('boom')\n"})}, "demo\n"))
check(lua.eval("RAN") == 1 and "运行出错" in log and "boom" in log, "③ 入口报错：记日志、不抛出", log)

# ④ 真 main.lua 开头（到 FORNET 那行）
_main = os.path.join(REPO, "..", "mod", "<mod>", "Scripts", "main.lua")     # 装在游戏 mod 里时才有游戏适配层（单独的 4net 仓库里没有，跳过）
src = open(_main, encoding="utf-8").read() if os.path.exists(_main) else None
head = src[:src.index("\n", src.index("if type(FORNET) ~= \"table\" then FORNET")) + 1] if src else None
hroot = mkmod({}, None)
for with_host in ((True, False) if src else ()):
    lua = L.LuaRuntime(unpack_returned_tuples=True)
    lua.execute('package.preload["UEHelpers"] = function() return {} end')
    lua.globals().ROOTDIR = hroot
    if with_host:
        lua.execute('FORNET = { ROOT = ROOTDIR, GAME_DIR = ROOTDIR .. "/games/demo", SCRIPTS = ROOTDIR .. "/Scripts", VERSION = "0.1.0" }')
    else:
        lua.execute('local real = debug.getinfo; debug.getinfo = function(l, w) local t = real(l, w); if t then t.source = "@" .. ROOTDIR .. "/Scripts/main.lua" end; return t end')
    lua.execute(head + "\nHEAD_ROOT = ROOT")
    f = lua.eval("FORNET")
    if with_host:
        check(lua.eval("HEAD_ROOT") == hroot and f.GAME_DIR == hroot + "/games/demo" and not f.standalone, "④ 有宿主：模组目录 = FORNET.ROOT，FORNET 不被覆盖", lua.eval("HEAD_ROOT"))
    else:
        check(lua.eval("HEAD_ROOT") == hroot and f.standalone and f.GAME_DIR == hroot + "/Scripts", "④ 没宿主：老办法找模组目录，补 standalone FORNET", lua.eval("HEAD_ROOT"))

# ⑤ F6 第二步：adopt / loop / 接口表检查
LOOPCHK = r'''
RAN = 1
CALLS = { built = 0, ticks = 0, idles = 0 }
local mk = function(ctx) CALLS.built = CALLS.built + 1; return { ctx = ctx } end
local a = FORNET.adopt("fornet_x", mk)
I1 = a({ k = 1 }); I2 = a({ k = 2 })
NOTFN = FORNET.adopt("fornet_y", nil)
'''
lua, log = run(mkmod({"demo": ("name=demo\n", {"main.lua": LOOPCHK})}, "demo\n"))
check(lua.eval("CALLS.built") == 1 and lua.eval("I1 == I2") and lua.eval("FORNET.mods.fornet_x == I1") and lua.eval("I1.ctx.k") == 1,
      "⑤ adopt：只构造一次、登记到 FORNET.mods、第二次拿到同一份")
check(lua.eval("NOTFN") is None, "⑤ adopt：不是构造函数原样返回")
check("没有设 FORNET.adapter" in log, "⑤ 适件没设接口表：记一行、照跑", log)
lua, log = run(mkmod({"demo": ("name=demo\n", {"main.lua": 'FORNET.adapter = { name = "demo", version = "1.0.0", tick = function() end }\n'})}, "demo\n"))
check("缺 makePacket / applyPacket / spawnPuppet / removePuppet / matchDeps / onMapChange" in log, "⑤ 接口表缺项写出缺哪些", log)

# loop.paced：每帧回调里按时钟累加；用假时钟一步步走
lua, log = run(mkmod({"demo": ("name=demo\n", {"main.lua": "RAN = 1\n"})}, "demo\n"))
lua.execute(r'''
FRAMES = {}; CLK = 0
os.clock = function() return CLK end
function LoopInGameThreadAfterFrames(n, f) FRAMES[#FRAMES + 1] = f end
T = { ticks = 0, idles = 0, alive = true }
MODE = FORNET.loop.paced(0.1, function() T.ticks = T.ticks + 1 end, function() return T.alive end, function() T.idles = T.idles + 1 end)
function frame(c) CLK = c; return FRAMES[1]() end
''')
check(lua.eval("MODE") == "frame", "⑤ paced：有每帧回调就用它")
lua.execute("frame(0.00); frame(0.05); frame(0.10); frame(0.15); frame(0.21)")
check(lua.eval("T.ticks") == 3 and lua.eval("T.idles") == 5, "⑤ paced：每帧都调 idle、到点才 tick（0 / 0.1 / 0.2 三拍）", (lua.eval("T.ticks"), lua.eval("T.idles")))
lua.execute("frame(1.0)")     # 落后 0.7 秒：不补，只跑一拍，下一拍从现在起算
lua.execute("frame(1.05)"); t1 = lua.eval("T.ticks"); lua.execute("frame(1.10)")
check(t1 == 4 and lua.eval("T.ticks") == 5, "⑤ paced：落后超过 0.1 秒不补拍、从现在重新起算", (t1, lua.eval("T.ticks")))
lua.execute("T.alive = false; STOP = frame(1.5)")
check(lua.eval("STOP") is True and lua.eval("T.ticks") == 5, "⑤ paced：alive() 为假时回调返回 true（循环自己停）、不再 tick")
lua.execute(r'''
LoopInGameThreadAfterFrames = nil
DELAY = nil; function LoopInGameThreadWithDelay(ms, f) DELAY = ms end
M2 = FORNET.loop.paced(1 / 30, function() end, function() return true end)
LoopInGameThreadWithDelay = nil
CHAIN = {}; function ExecuteWithDelay(ms, f) CHAIN[#CHAIN + 1] = ms end
M3 = FORNET.loop.paced(1 / 30, function() end, function() return true end)
ExecuteWithDelay = nil
M4 = FORNET.loop.paced(1 / 30, function() end, function() return true end)
LoopAsync = nil
NOASYNC = FORNET.loop.async(2000, function() end)
NOFRAME = FORNET.loop.frame(function() end)
''')
check(lua.eval("M2") == "delay" and lua.eval("DELAY") == 33, "⑤ paced：没有每帧回调退到 33 ms 固定间隔（和老代码一样）", lua.eval("DELAY"))
check(lua.eval("M3") == "async" and lua.eval("CHAIN[1]") == 1000, "⑤ paced：再没有退到异步链（1 秒后开始）")
check(lua.eval("M4") is None and lua.eval("NOASYNC") is False and lua.eval("NOFRAME") is False, "⑤ 什么定时器都没有：paced 返回 nil、frame / async 返回 false")

print("共 %d 项失败" % len(fails))
sys.exit(1 if fails else 0)
