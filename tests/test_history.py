# 4net 自带测试：路径以本目录的上一级（仓库根）为根。
# 0.34 "最近一起玩过的人"：两个联机程序经服务器大厅直连 50 秒 → 两边 net\history.txt 各一条
# （bh = 对方安装号哈希、昵称、src=server，没有平台账号 ID / IP），lobby.txt hn=1 / h1 对得上；对方再开房时 h1 带可加入的目标；
# cmd=clearhist 清掉；hide=1 时登记不带主机标记（服务器列表第 10 列空）→ 对方的历史里对不上
# python -X utf8 -I tests/test_history.py <FornetLobby.exe> <工作目录> [服务器端口，默认 27170]
import hashlib, os, re, socket, subprocess, sys, threading, time

sys.stdout.reconfigure(encoding="utf-8")
os.environ.setdefault("FORNET_LOOPBACK", "1")

exe, base = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
sport = int(sys.argv[3]) if len(sys.argv) > 3 else 27170
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
server_js = os.path.join(root, "server", "fornet-lobby-server.mjs")
A, B = os.path.join(base, "A"), os.path.join(base, "B")
fails = []
stop = False


def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("  " + str(detail) if detail else ""), flush=True)
    if not ok:
        fails.append(name)


def write(path, text):
    with open(path + ".tmp", "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    for _ in range(20):
        try:
            os.replace(path + ".tmp", path)
            return
        except PermissionError:
            time.sleep(0.01)


def read(path):
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except OSError:
        return ""


def kv(path):
    return dict(l.split("=", 1) for l in read(path).splitlines() if "=" in l)


def wait(path, cond, sec):
    t0 = time.time()
    while time.time() - t0 < sec:
        d = kv(path)
        if cond(d):
            return d
        time.sleep(0.2)
    return kv(path)


def fresh(d, name, hide=0, keep=()):
    os.makedirs(d, exist_ok=True)
    for f in os.listdir(d):
        if f in keep:
            continue
        try:
            os.remove(os.path.join(d, f))
        except OSError:
            pass
    write(os.path.join(d, "netcfg.txt"), "mode=server\nname=%s\nserver=127.0.0.1:%d\nport=%d\nhide=%d\n" % (name, sport, 27171 if d == A else 27172, hide))
    write(os.path.join(d, "lang.txt"), "lang=zh\n")


def fake_games():
    # 假游戏：每秒 30 次写 state_local.txt（格式同 test_server_lobby；mt=fight 让"开打了"成立）
    n = 0
    while not stop:
        n += 1
        for side, d in (("A", A), ("B", B)):
            if os.path.isdir(d):
                try:
                    write(os.path.join(d, "state_local.txt"), "v=2\nseq=%d\nmap=Map_Xigu\nx=%d.0\nside=%s\nmt=fight:1:0:0:-:1\n" % (n, n, side))
                except OSError:
                    pass
        time.sleep(1 / 30)


def start(d):
    return subprocess.Popen([exe, "--bg", "--consent-all", "--no-backup", "--dir", d, "--seconds", "120"])


def h12(s):
    return hashlib.sha256(s.encode("utf-8")).hexdigest()[:12]


def udp(msg):
    sk = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sk.settimeout(1.5)
    sk.sendto(msg.encode("utf-8"), ("127.0.0.1", sport))
    try:
        return sk.recvfrom(65536)[0].decode("utf-8", "replace")
    except OSError:
        return ""
    finally:
        sk.close()


os.makedirs(base, exist_ok=True)
srv = subprocess.Popen(["node", server_js, "--port", str(sport), "--host", "127.0.0.1"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
time.sleep(0.8)
threading.Thread(target=fake_games, daemon=True).start()
procs = []
try:
    fresh(A, "甲方"); fresh(B, "乙方")
    procs = [start(A), start(B)]
    wait(os.path.join(A, "lobby.txt"), lambda d: d.get("alive"), 8)
    wait(os.path.join(B, "lobby.txt"), lambda d: d.get("alive"), 8)
    uidA, uidB = read(os.path.join(A, "uid.txt")).strip(), read(os.path.join(B, "uid.txt")).strip()
    write(os.path.join(A, "cmd.txt"), "id=a1\ncmd=host\n")
    la = wait(os.path.join(A, "lobby.txt"), lambda d: d.get("room", "") != "", 8)
    room = la.get("room", "")
    check("甲建房", len(room) == 6, "room=" + room)
    write(os.path.join(B, "cmd.txt"), "id=b1\ncmd=join\ntarget=room:%s\n" % room)
    lb = wait(os.path.join(B, "lobby.txt"), lambda d: d.get("connected") == "1", 20)
    check("乙连上（直连）", lb.get("connected") == "1" and lb.get("route") == "direct", "connected=%s route=%s" % (lb.get("connected"), lb.get("route")))
    # 开打着（假游戏 mt=fight），几秒内就该记进 history.txt（不用等 45 秒）
    hb = ""
    t0 = time.time()
    while ("uid" not in hb and "bh=" not in hb) and time.time() - t0 < 20:
        hb = read(os.path.join(B, "history.txt")); time.sleep(0.5)
    ha = read(os.path.join(A, "history.txt"))
    check("乙的 history.txt 有甲：bh = 甲安装号的哈希、昵称、src=server", ("bh=" + h12("fornet-room|" + uidA)) in hb and "nm=甲方" in hb and "src=server" in hb, hb)
    check("甲的 history.txt 有乙", ("bh=" + h12("fornet-room|" + uidB)) in ha and "nm=乙方" in ha, ha)
    check("history.txt 里没有 IP、没有平台账号 ID 明文", "127.0.0.1" not in hb and "7656" not in hb and "ip=" not in hb and "sid=" not in hb, hb)
    lb = kv(os.path.join(B, "lobby.txt"))
    check("乙的 lobby.txt hn=1、h1 = hist|server|<时间>|甲方|<目标>（连着时甲的房已满 → 目标 -）", lb.get("hn") == "1" and re.match(r"^hist\|server\|\d+\|甲方\|", lb.get("h1", "")) is not None, lb.get("h1"))
    write(os.path.join(B, "cmd.txt"), "id=b2\ncmd=stop\n")
    la = wait(os.path.join(A, "lobby.txt"), lambda d: d.get("connected") == "0", 10)
    # 甲还开着房（没满）→ 乙的 h1 目标 = room:<甲的房>
    lb = wait(os.path.join(B, "lobby.txt"), lambda d: d.get("h1", "").endswith("|room:" + room), 10)
    check("甲重新开放后：乙的 h1 带可加入的目标 room:<甲的房>", lb.get("h1", "").endswith("|room:" + room), lb.get("h1"))
    write(os.path.join(B, "cmd.txt"), "id=b3\ncmd=clearhist\n")
    lb = wait(os.path.join(B, "lobby.txt"), lambda d: d.get("hn") == "0", 6)
    check("cmd=clearhist → hn=0、history.txt 没了", lb.get("hn") == "0" and not os.path.exists(os.path.join(B, "history.txt")), lb.get("hn"))
    # 隐身：甲 hide=1 重开 → 服务器列表第 10 列（主机标记）空
    for p in procs:
        p.kill()
    time.sleep(0.5)
    fresh(A, "甲方", hide=1, keep=("uid.txt",))
    procs = [start(A)]
    wait(os.path.join(A, "lobby.txt"), lambda d: d.get("alive"), 8)
    write(os.path.join(A, "cmd.txt"), "id=a2\ncmd=host\n")
    la = wait(os.path.join(A, "lobby.txt"), lambda d: d.get("room", "") != "", 8)
    time.sleep(2.5)
    lst = udp("4N|LIST")
    row = [l.split("\t") for l in lst.split("\n")[1:] if l.startswith(la.get("room", "?") + "\t")]
    check("隐身 hide=1：服务器列表第 10 列（主机标记）空", row and len(row[0]) >= 10 and row[0][9].strip() == "", str(row))
    check("隐身：lobby.txt hide=1", kv(os.path.join(A, "lobby.txt")).get("hide") == "1")
finally:
    stop = True
    for p in procs:
        p.kill()
    srv.kill()

print("共 %d 项失败" % len(fails) + ("：" + "；".join(fails) if fails else ""))
sys.exit(1 if fails else 0)
