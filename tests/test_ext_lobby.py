# fornet 仓库自带测试（11-13 测试分家）：路径以本目录的上一级为根（私有库里是 fornet/，fornet 仓库里是仓库根）。原件：私有库 tests/test_ext_lobby.py
# X2 扩展包的联机程序插件（lobby/Ext.cs）：真的 FornetLobby.exe + 示例 C 插件 samples/hello/lobby_hello.dll（先跑 build_hello.bat）。
# python -I test_ext_lobby.py <FornetLobby.exe> <工作目录>
# ① 启动后 lobby.txt 有 extp=hello、x.hello.ver=0.1.0（fnx_init 里 reply）
# ② cmd ext plugin=hello ping=1 → x.hello.pong=1
# ③ 没同意 ext_hello_share：send 被拒 → x.hello.sent=denied；本地监听的 UDP 口收不到
# ④ rules.txt 写 consent_ext_hello_share=1 → send 成功 → 本地口收到 data；回一包 → tick 的 recv 收到 → x.hello.got=
# ⑤ 停用（删 on.txt；11-13 起扩展包默认停用，有 on.txt 才装）重启 → extp 空、没有 x.hello.*
import os, shutil, socket, subprocess, sys, time

exe, root = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
HERE = os.path.dirname(os.path.abspath(__file__)); REPO = os.path.dirname(HERE)
fails = []
def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("  " + detail if detail else ""), flush=True)
    if not ok: fails.append(name)
def kv(path):
    try:
        d = {}
        for l in open(path, encoding="utf-8", errors="replace").read().split("\n"):
            i = l.find("=")
            if i > 0: d[l[:i]] = l[i + 1:]
        return d
    except OSError: return {}
def wait(path, pred, sec):
    t0 = time.time(); d = {}
    while time.time() - t0 < sec:
        d = kv(path)
        if d and pred(d): return d, time.time() - t0
        time.sleep(0.1)
    return d, time.time() - t0
def write(path, s):
    tmp = path + ".tmp"; open(tmp, "w", encoding="utf-8").write(s); os.replace(tmp, path)

dll = os.path.join(REPO, "samples", "hello", "lobby_hello.dll")
if not os.path.exists(dll): print("先跑 samples/build_hello.bat"); sys.exit(2)
shutil.rmtree(root, ignore_errors=True)
net = os.path.join(root, "net"); os.makedirs(net)
extd = os.path.join(root, "ext", "hello"); os.makedirs(extd)
for f in ("manifest.txt", "init.lua", "lobby_hello.dll"): shutil.copy(os.path.join(REPO, "samples", "hello", f), extd)
open(os.path.join(extd, "on.txt"), "w").write("on\n")     # 11-13：默认停用，玩家点过启用才有 on.txt
write(os.path.join(net, "netcfg.txt"), "mode=lan\nname=ExtTest\nport=27171\n")
lob = os.path.join(net, "lobby.txt")

def start():
    return subprocess.Popen([exe, "--bg", "--no-backup", "--dir", net, "--seconds", "60"])
p = start()
try:
    d, t = wait(lob, lambda d: d.get("extp") == "hello", 15)
    check("① extp=hello（插件就绪）", d.get("extp") == "hello", str({k: v for k, v in d.items() if k.startswith("x.") or k == "extp"}))
    check("① x.hello.ver=0.1.0（init 里 reply）", d.get("x.hello.ver") == "0.1.0")

    write(os.path.join(net, "cmd.txt"), "id=e1\ncmd=ext\nplugin=hello\nping=7\n")
    d, t = wait(lob, lambda d: d.get("x.hello.pong") == "7", 6)
    check("② ping → pong", d.get("x.hello.pong") == "7", "%.1fs" % t)

    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.bind(("127.0.0.1", 0)); s.settimeout(2.0)
    port = s.getsockname()[1]
    write(os.path.join(net, "cmd.txt"), "id=e2\ncmd=ext\nplugin=hello\nto=127.0.0.1:%d\ndata=hello-from-plugin\n" % port)
    d, t = wait(lob, lambda d: d.get("x.hello.sent") in ("denied", "ok"), 6)
    check("③ 没同意 → send 被拒", d.get("x.hello.sent") == "denied", d.get("x.hello.sent"))
    got = None
    try: got = s.recvfrom(2000)
    except socket.timeout: pass
    check("③ 本地口没收到包", got is None, str(got))

    write(os.path.join(net, "rules.txt"), "consent_play=1\nconsent_ext_hello_share=1\n")
    time.sleep(2.5)     # Consent 缓存 2 秒
    write(os.path.join(net, "cmd.txt"), "id=e3\ncmd=ext\nplugin=hello\nto=127.0.0.1:%d\ndata=hello-from-plugin\n" % port)
    d, t = wait(lob, lambda d: d.get("x.hello.sent") == "ok", 6)
    check("④ 同意后 send 成功", d.get("x.hello.sent") == "ok", d.get("x.hello.sent"))
    got, frm = None, None
    try: got, frm = s.recvfrom(2000)
    except socket.timeout: pass
    check("④ 本地口收到 data", got == b"hello-from-plugin", str(got))
    if frm:
        s.sendto(b"back-to-plugin", frm)
        d, t = wait(lob, lambda d: d.get("x.hello.got") == "back-to-plugin", 6)
        check("④ 插件 recv 收到回包", d.get("x.hello.got") == "back-to-plugin", str(d.get("x.hello.got")))
    else:
        check("④ 插件 recv 收到回包", False, "没拿到来源地址")
    write(os.path.join(net, "cmd.txt"), "id=e4\ncmd=ext\nplugin=hello\nask=share\n")
    d, t = wait(lob, lambda d: d.get("x.hello.has") == "1", 6)
    check("④ consent 回调 = 1", d.get("x.hello.has") == "1")
    write(os.path.join(net, "cmd.txt"), "id=e5\ncmd=ext\nplugin=nothere\nping=1\n")
    time.sleep(1.0)
    check("② 不存在的插件不崩", p.poll() is None)
finally:
    write(os.path.join(net, "cmd.txt"), "id=q\ncmd=quit\n")
    try: p.wait(8)
    except subprocess.TimeoutExpired: p.kill()

os.remove(os.path.join(extd, "on.txt"))
try: os.remove(lob)
except OSError: pass
p = start()
try:
    d, t = wait(lob, lambda d: d.get("mode") == "lan", 15)
    check("⑤ 没有 on.txt → 不载入（extp 空、无 x.hello.*）", d.get("extp", "") == "" and not any(k.startswith("x.hello.") for k in d), str([k for k in d if k.startswith("x.")]))
finally:
    write(os.path.join(net, "cmd.txt"), "id=q2\ncmd=quit\n")
    try: p.wait(8)
    except subprocess.TimeoutExpired: p.kill()
print("共 %d 项失败" % len(fails))
sys.exit(1 if fails else 0)
