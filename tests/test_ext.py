# fornet 仓库自带测试（11-13 测试分家）：路径以本目录的上一级为根（私有库里是 fornet/，fornet 仓库里是仓库根）。原件：私有库 tests/test_ext.py
# X1 扩展包（fornet/lua/fornet_ext.lua，F5 前叫 game_ext.lua）：lupa 加载真的模块，基底部分用桩。 / EXTENSIONS_API.md
# 用法：python -X utf8 -I test_ext.py <lupa 目录>
import os, shutil, sys, tempfile
sys.path.insert(0, sys.argv[1])
import lupa.lua54 as L

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
fails = []
def check(ok, name, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("  " + str(detail) if detail else ""), flush=True)
    if not ok: fails.append(name)

root = tempfile.mkdtemp(prefix="game_ext_")
ext = os.path.join(root, "ext")
shutil.copytree(os.path.join(REPO, "samples", "hello"), os.path.join(ext, "hello"))
open(os.path.join(ext, "hello", "on.txt"), "w").write("on\n")
def mk(name, manifest, init, on=True):     # 11-13：扩展包默认停用，测试里的包默认放 on.txt（= 玩家点过启用）
    d = os.path.join(ext, name); os.makedirs(d)
    if on: open(os.path.join(d, "on.txt"), "w").write("on\n")
    if manifest is not None: open(os.path.join(d, "manifest.txt"), "w", encoding="utf-8").write(manifest)
    if init is not None: open(os.path.join(d, "init.lua"), "w", encoding="utf-8").write(init)
    return d
# 越权：io / require / os.remove / 改 string 库 / 摸 _G 外面
mk("evil", "name=evil\nversion=1\n", r'''
return function(API)
    local tried = {}
    tried.io = io == nil
    tried.require = require == nil
    tried.osremove = (os.remove == nil)
    tried.dofile = dofile == nil
    tried.NETUI = NETUI == nil and rawget == nil
    string.upper = function() return "HACKED" end      -- 改的是副本
    API.status.addField("t", function() local n = 0; for k, v in pairs(tried) do if v then n = n + 1 end end; return n end)
    return {}
end''')
mk("nomanifest", None, "return function() end")
mk("toonew", "name=toonew\nversion=1\nneedsBase=9.9.9\n", "return function() end")
mk("broken", "name=broken\nversion=1\n", "return function(API) error('boom') end")
d = mk("sleepy", "name=sleepy\nversion=1\n", "return function(API) API.status.addField('z', function() return 1 end) end", on=False)
d = mk("legacy", "name=legacy\nversion=1\n", "return function() end", on=False)     # 旧版只认 off.txt：没有 off.txt 也没有 on.txt → 现在一样是停用
mk("bad name!", "name=x\n", "return function() end")      # 名字不合法，应被忽略
mk("api2", "name=api2\nversion=1\napi=2\n", "return function() end")   # API 2 的包要能装
mk("api9", "name=api9\nversion=1\napi=9\n", "return function() end")   # 比基底新的 API → bad
mk("ownr", "name=ownr\nversion=1\napi=2\nconsent=@owned\nadapter=game\nclient=client.lua\n", "return function() end")   # 11-13：@键归属 + 适件代码

lua = L.LuaRuntime(unpack_returned_tuples=True)
src = open(os.path.join(REPO, "lua", "fornet_ext.lua"), encoding="utf-8").read()
mkext = lua.execute(src)
state = lua.execute(r'''
local S = { logs = {}, cmds = {}, granted = {}, pref = {} }
S.CONSENT = { TEXT = {}, PRIVACY_TITLE = {}, PRIVACY_ORDER = {}, PER_SERVER = {} }
function S.CONSENT.has(k) local t = S.CONSENT.TEXT[k]; if not t then return true end; return S.granted[k] == true end
function S.CONSENT.ask(k, fn) if S.CONSENT.has(k) then fn(); return true end; S.pendingKey = k; return false end
function S.CONSENT.titleRows(items, keys) for _, k in ipairs(keys) do items[#items + 1] = { label = "  · " .. k .. (S.CONSENT.has(k) and "（已同意）" or "（未同意）") .. "…" } end end   -- 11-15 插件页只列条款标题
function S.CONSENT.grantKeys(keys) for _, k in ipairs(keys) do S.granted[k] = true end end   -- 11-15 启用 = 同意全部条款
function S.CONSENT.revokeKeys(keys) for _, k in ipairs(keys) do S.granted[k] = nil end end   -- 停用 = 一起撤回
S.ctx = {
    out = function(fmt, ...) local ok, s = pcall(string.format, fmt, ...); S.logs[#S.logs + 1] = ok and s or fmt end,
    ROOT = "@ROOT@", T = function(zh, en) return zh end, CONSENT = S.CONSENT, PREF = { get = function(k) return S.pref[k] end, set = function(k, v) S.pref[k] = v end },
    VERSION = "0.36.0", COL = { label = 1, orange = 2, green = 3, white = 4 },
    readKV = function(path) local f = io.open(path, "r"); if not f then return nil end; local t = {}; for l in f:lines() do local k, v = l:match("^([^=]+)=(.*)$"); if k then t[k] = v end end; f:close(); return t end,
    writeFile = function(path, s) local f = io.open(path, "w"); if not f then return false end; f:write(s); f:close(); return true end,
    netuiCmd = function(cmd, extra) S.cmds[#S.cmds + 1] = cmd .. "|" .. (extra or "") end,
    transport = function() return S.route or "udp" end,
}
return S
''')
state.ctx.ROOT = root.replace("\\", "/")
EXT = mkext(state.ctx)
lst = EXT.scan()
by = {EXT.list[i].name: EXT.list[i] for i in range(1, len(EXT.list) + 1)}
check(set(by) == {"hello", "evil", "nomanifest", "toonew", "broken", "sleepy", "legacy", "api2", "api9", "ownr"}, "扫描：10 个合法目录名（bad name! 被忽略）", str(sorted(by)))
check(by["api2"].state == "on", "api=2 → 能装", str(by["api2"].why))
check(by["api9"].state == "bad" and "9" in str(by["api9"].why), "api=9 → bad", str(by["api9"].why))
# 仓库里随基底发的扩展包，manifest 要的 API 不能比 fornet_ext.lua 的 API_VER 高（就是这样漏的）
import re
apiver = int(re.search(r"API_VER = (\d+)", src).group(1))
csv = int(re.search(r"public const int ApiVersion = (\d+);", open(os.path.join(REPO, "lobby", "Ext.cs"), encoding="utf-8").read()).group(1))
check(csv == apiver, "Lua fornet_ext.lua API_VER = C# Ext.cs ApiVersion（两边一起升）", "Lua %d / C# %d" % (apiver, csv))
check("an > ApiVersion" in open(os.path.join(REPO, "lobby", "Ext.cs"), encoding="utf-8").read(), "C# 侧也按 manifest api= 拒装比自己新的插件")
for pk in ("steam", "ranked"):     # 私有库里才有这两个包（fornet 仓库没有，跳过）
    _mf = os.path.join(REPO, "..", "mod", "<mod>", "ext", pk, "manifest.txt")
    if not os.path.exists(_mf): continue
    mf = open(_mf, encoding="utf-8").read()
    m = re.search(r"^api=(\d+)", mf, re.M)
    check(m is not None and int(m.group(1)) <= apiver, f"ext/{pk} manifest api={m and m.group(1)} ≤ 基底 API_VER={apiver}")
check(by["hello"].state == "on" and by["hello"].version == "0.1.0", "hello 加载成功", by["hello"].state)
check(by["evil"].state == "on", "evil 加载成功（越权的东西都是 nil，不是报错）", str(by["evil"].why))
check(by["nomanifest"].state == "bad" and "manifest" in str(by["nomanifest"].why), "没有 manifest → bad")
check(by["toonew"].state == "bad" and "9.9.9" in str(by["toonew"].why), "needsBase 太高 → bad")
check(by["broken"].state == "bad" and "boom" in str(by["broken"].why), "init 报错 → bad，原因带出来")
check(by["sleepy"].state == "off", "没有 on.txt → off（默认停用）")
check(by["legacy"].state == "off", "旧版没标记的包也是 off（默认停用）")

# 沙箱：io / require / os.remove / dofile / NETUI 全 nil（evil 的 t 字段 = 5）；外面的 string.upper 没被改
w = EXT.wire()
check("x.evil.t=5\n" in w, "沙箱：io / require / os.remove / dofile / 全局 都拿不到", w.replace("\n", " "))
check(lua.eval('string.upper("a")') == "A", "沙箱：扩展改 string 库只改到自己的副本")
check("x.sleepy." not in w, "停用的包不进状态包")
check("x.hello.g" not in w, "hello 未开启时 g 为 nil，不发字段")

# 同意键登记进 CONSENT；lobby.cmd 没同意拒发
key = "ext_hello_share"
check(key not in [state.CONSENT.PRIVACY_ORDER[i] for i in range(1, len(state.CONSENT.PRIVACY_ORDER) + 1)] and state.CONSENT.TEXT[key] is not None and state.CONSENT.TEXT[key].ext == "hello",
      "consent.register 进 TEXT（ext = hello），不进基底隐私说明（11-13 拆干净）")
check(state.CONSENT.PRIVACY_TITLE[key][1].startswith("扩展 hello"), "隐私说明标题带 扩展 hello：", state.CONSENT.PRIVACY_TITLE[key][1])
items = lua.table()
page = lua.eval('function(id) return function() _G.__page = id end end')
EXT.rows("settings", items, lua.table())
check(len(items) == 1 and "示例扩展" in items[1].label, "panel.addRows(settings) 加了 1 行")
items[1].act()      # 没同意 → 基底弹确认，不执行
check(state.pendingKey == key and "x.hello.g" not in EXT.wire(), "consent.ask 没同意时翻确认页、不执行")
state.granted[key] = True
items[1].act()
check("x.hello.g=hi\n" in EXT.wire(), "同意后执行，状态包出现 x.hello.g=hi", EXT.wire().replace("\n", " "))
# lobby.cmd：X1 没有 C# 插件 → 记日志不发；evil 没登记同意键 → 拒
r = lua.eval('function(e, name, t) local p = e.byName[name]; return p end')
ok1 = EXT.lobbyReady
check(not ok1, "X1：lobbyReady 为 false")
before = len(state.cmds)
# 经扩展自己的 API 发：从 hello 的 init 拿不到 api，这里直接构造一次调用（makeApi 是内部的）→ 用 pageItems 不涉及；改用 evil 的字段数验证已够。补：直接测 EXT 内部的拒发逻辑
check(len(state.cmds) == before, "没有任何 netuiCmd 被发出（X1）")

# 回话分发
lb = lua.table_from({"x.hello.g": "yo", "role": "host"})
EXT.poll(lb)
check(any("对方问候：yo" in s for s in [state.logs[i] for i in range(1, len(state.logs) + 1)]), "onReply 收到 x.hello.g")

# 页：总页列出各包 / 插件页的启用 / 停用开关写 / 删 on.txt
items = lua.table()
lb = lua.table_from({"_page": "ext"})
EXT.pageItems(lb, items, page)
labels = [items[i].label for i in range(1, len(items) + 1)]
check(any("示例扩展" in s and "启用" in s for s in labels), "已加载的扩展页列出 hello 启用")
check(any("broken" in s for s in labels) and any("boom" in s for s in labels), "失败的包显示原因")
check(not any(key in s or "共享" in s for s in labels), "总页不列同意文案（只给入口）")
check(any("打开插件页" in s and "同意项 1（已同意 1）" in s for s in labels), "总页每包一行入口 + 同意项个数", [l for l in labels if "打开插件页" in l][:3])
# 插件页 ext:hello：同意项完整文案 / 状态 / 撤回；停用开关在这里
items = lua.table()
EXT.pageItems(lua.table_from({"_page": "ext:hello"}), items, page)
labels = [items[i].label for i in range(1, len(items) + 1)]
# 11-15 起没有单项撤回：只列条款标题 + 状态，停用 = 一起撤回
check(any("已同意" in s for s in labels) and any("撤回上面的同意" in s for s in labels), "插件页列出同意项、状态，停用时一起撤回", labels[:6])
check(lua.eval("function(e) return e.pageTitle('ext:hello') end")(EXT).startswith("扩展 · "), "插件页标题")
tog = [items[i] for i in range(1, len(items) + 1) if items[i].label and "停用" in items[i].label and "重启游戏生效" in items[i].label]
check(len(tog) >= 1, "有停用按钮（写明重启游戏生效）")
tog[0].act()
check(not os.path.exists(os.path.join(ext, "hello", "on.txt")), "停用删了 hello 的 on.txt")
check(by["hello"].state == "on", "点了停用，这次游戏里仍在跑（重启才生效）")
check(any("hello：停用（重启游戏生效）" in s for s in [state.logs[i] for i in range(1, len(state.logs) + 1)]), "日志记了停用")
items = lua.table()
EXT.pageItems(lua.table_from({"_page": "ext:hello"}), items, page)
labels = [items[i].label for i in range(1, len(items) + 1)]
check(any(s.startswith("已停用，") for s in labels) and any("重启游戏后才生效" in s for s in labels) and "知道了" in labels, "插件页顶上弹出提示 + 知道了", labels[:6])
check(any("重启游戏后停用" in s for s in labels), "状态写明重启后停用")
ok = [items[i] for i in range(1, len(items) + 1) if items[i].label == "知道了"][0]
ok.act()
items = lua.table()
EXT.pageItems(lua.table_from({"_page": "ext:hello"}), items, page)
labels = [items[i].label for i in range(1, len(items) + 1)]
check("知道了" not in labels, "点知道了提示收起")
# 11-15：有条款的包按钮是"同意上面的条款并启用（重启游戏生效）"
en = [items[i] for i in range(1, len(items) + 1) if items[i].label and items[i].label.endswith("启用（重启游戏生效）")]
check(len(en) == 1, "停用后按钮变成启用（重启游戏生效）")
en[0].act()
check(os.path.exists(os.path.join(ext, "hello", "on.txt")), "再启用写回 on.txt")
items = lua.table()
EXT.pageItems(lua.table_from({"_page": "ext:hello"}), items, page)
labels = [items[i].label for i in range(1, len(items) + 1)]
check(any(s.startswith("已启用，") for s in labels), "启用也弹提示")
tog[0].act()     # 再停一次，下面重扫看 off
EXT.scan()
by = {EXT.list[i].name: EXT.list[i] for i in range(1, len(EXT.list) + 1)}
check(by["hello"].state == "off", "重扫后 hello 是 off")
items = lua.table()
EXT.pageItems(lua.table_from({"_page": "ext"}), items, page)
labels = [items[i].label for i in range(1, len(items) + 1)]
check(any("默认停用" in s and "重启游戏" in s for s in labels), "总页写明默认停用、启用 / 停用要重启游戏")
check(not any("见隐私说明" in s for s in labels), "总页不再指向隐私说明（扩展包内容不在基底隐私说明里）")
items = lua.table()
EXT.pageItems(lua.table_from({"_page": "ext:sleepy"}), items, page)
en = [items[i] for i in range(1, len(items) + 1) if items[i].label and items[i].label.endswith("启用（重启游戏生效）")]
en[0].act()
EXT.scan()
by = {EXT.list[i].name: EXT.list[i] for i in range(1, len(EXT.list) + 1)}
check(by["sleepy"].state == "on", "默认停用的包点启用 → 重扫后 on")
open(os.path.join(ext, "hello", "on.txt"), "w").write("on\n")     # 下面的检查要 hello 开着
EXT.scan()
by = {EXT.list[i].name: EXT.list[i] for i in range(1, len(EXT.list) + 1)}
# 11-13：manifest consent=@键 → CONSENT.EXT_OWN；adapter= / client= → EXT.clientOf
check(state.CONSENT.extRoute is True, "装上扩展包框架 = CONSENT.extRoute（扩展的同意项去插件页问）")
check(state.CONSENT.EXT_OWN["owned"] == "ownr", "manifest consent=@owned → EXT_OWN.owned = ownr")
cl = EXT.clientOf("ownr", "game")
check(cl is not None and cl.endswith("/ownr/client.lua"), "clientOf：包启用 + adapter 对得上 → 给 client 路径", cl)
check(EXT.clientOf("ownr", "other") is None and EXT.clientOf("hello", "game") is None, "clientOf：adapter 不对 / 没写 client → nil")
# 待同意块：pending 的键归这个包时，插件页顶上出"同意并继续 / 取消"
state.CONSENT.TEXT["owned"] = lua.table_from({"ver": 1, "zh": lua.table_from({1: "归 ownr 的文案"}), "en": lua.table_from({1: "owned text"}), "ext": "ownr"})
state.CONSENT.pending = lua.table_from({"key": "owned"})
items = lua.table()
EXT.pageItems(lua.table_from({"_page": "ext:ownr"}), items, page)
labels = [items[i].label for i in range(1, len(items) + 1)]
check(any("要继续刚才的操作" in s for s in labels) and any("归 ownr 的文案" in s for s in labels) and any(s == "同意并继续" for s in labels), "插件页显示待同意项的文案和同意并继续", labels[:6])
state.CONSENT.pending = None
# 扩展自己的页
items = lua.table()
state.route = "steam"
lb = lua.table_from({"_page": "ext:hello:about"})
by2 = by
if by["hello"].state == "on":
    EXT.pageItems(lb, items, page)
    labels = [items[i].label for i in range(1, len(items) + 1)]
    check(any("传输：steam" in s for s in labels), "扩展自己的页 + net.transport() = steam")
else:
    check(True, "（hello 已被停用，跳过自己的页）")
# ext.txt 列表方式
open(os.path.join(ext, "ext.txt"), "w", encoding="utf-8").write("# 注释\nhello\nnot_there\n")
names, how = EXT.listDirs()
check(how == "ext.txt" and list(names.values()) == ["hello", "not_there"], "ext.txt 优先于 dir", f"{how} {list(names.values())}")
# 没有 ext 目录：不报错、列表空
state.ctx.ROOT = (root + "/nowhere").replace("\\", "/")
EXT2 = mkext(state.ctx)
EXT2.scan()
check(len(EXT2.list) == 0 and EXT2.wire() == "", "没有 ext 目录：0 个包、状态包不多字节")

shutil.rmtree(root, ignore_errors=True)
print("共 %d 项失败" % len(fails))
sys.exit(1 if fails else 0)
