# fornet 仓库自带测试（11-13 测试分家）：路径以本目录的上一级为根（私有库里是 fornet/，fornet 仓库里是仓库根）。原件：私有库 tests/test_lobby_cmd.py
# 联机窗口的命令通道（net-ui）：两个后台 exe 实例，靠 cmd.txt 让 A 建房、B 加入，查 lobby.txt / session.txt / state_remote.txt。
# 不开游戏；两个实例各用自己的 --dir，互不影响。
# python -I test_lobby_cmd.py <FornetLobby.exe> <工作目录> [模式 direct|lan] [端口]
# 默认 direct = 服务器大厅里的"输入 IP 直连"（v0.20.3 起原来的广域网），端口 27115；大厅地址填一个不通的（127.0.0.1:9），
# 顺带测"服务器连不上也能直连"，也不会登记到公共大厅。lan 会占 27015 并广播几秒。
import os, sys, time, subprocess, threading
os.environ.setdefault("FORNET_LOOPBACK", "1")   # 联机程序只绑 127.0.0.1，测试时防火墙不弹（子进程继承）

sys.stdout.reconfigure(encoding="utf-8")

exe, base = os.path.abspath(sys.argv[1]), sys.argv[2]
arg = sys.argv[3] if len(sys.argv) > 3 else "direct"
mode = "server" if arg in ("direct", "wan") else arg
port = int(sys.argv[4]) if len(sys.argv) > 4 else 27115
A, B = os.path.join(base, "A"), os.path.join(base, "B")
fails = []


def write(path, text):
    with open(path + ".tmp", "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    for _ in range(20):
        try:
            os.replace(path + ".tmp", path)
            return
        except PermissionError:
            time.sleep(0.005)


def kv(path):
    # exe 用"写临时文件再替换"，正好撞上替换的那一下会读不到，重试几次
    for _ in range(20):
        try:
            with open(path, encoding="utf-8") as f:
                d = dict(l.split("=", 1) for l in f.read().splitlines() if "=" in l)
            if d:
                return d
        except OSError:
            pass
        time.sleep(0.01)
    return {}


def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("  " + detail if detail else ""))
    if not ok:
        fails.append(name)


for d in (A, B):
    os.makedirs(d, exist_ok=True)
    for f in os.listdir(d):
        os.remove(os.path.join(d, f))
    write(os.path.join(d, "netcfg.txt"), "mode=lan\nport=%d\nname=%s\n%s" % (port, "甲方" if d == A else "乙方",
          "server=127.0.0.1:9\n" if mode == "server" else ""))   # 故意不是目标模式：测 cmd=mode
    write(os.path.join(d, "lang.txt"), "lang=zh\n")
    write(os.path.join(d, "cmd.txt"), "id=stale\ncmd=host\n")            # 启动前留下的旧命令：不能执行

stop = False


def fake_games():   # 同 fake_two_games.py：两边各写自己的 state_local
    seq = 0
    while not stop:
        seq += 1
        for side, d in (("A", A), ("B", B)):
            write(os.path.join(d, "state_local.txt"), "v=2\nseq=%d\nmap=Map_Xigu\nx=%d.0\nside=%s\n" % (seq, seq, side))
        time.sleep(1 / 30)


th = threading.Thread(target=fake_games, daemon=True)
th.start()
procs = [subprocess.Popen([exe, "--bg", "--consent-all", "--no-backup", "--dir", d, "--seconds", "25"]) for d in (A, B)]
try:
    t0 = time.time()   # .NET 冷启动要 2–3 秒
    while time.time() - t0 < 8:
        la, lb = kv(os.path.join(A, "lobby.txt")), kv(os.path.join(B, "lobby.txt"))
        if la.get("alive") and lb.get("alive"):
            break
        time.sleep(0.2)
    check("两个实例都在写 lobby.txt", float(la.get("alive", 0)) > time.time() - 3 and float(lb.get("alive", 0)) > time.time() - 3)
    check("旧命令没执行", la.get("role") == "none", "role=" + la.get("role", "?"))
    check("后台模式不显示窗口", la.get("shown") == "0")
    if mode != "server":
        check("netcfg 没写 server= 时大厅服务器为空（不默认连作者的服务器）", la.get("srv") == "", "srv=" + la.get("srv", "?"))
    write(os.path.join(A, "cmd.txt"), "id=1\ncmd=mode\nmode=%s\n" % mode)
    write(os.path.join(B, "cmd.txt"), "id=1\ncmd=mode\nmode=%s\n" % mode)
    t0 = time.time()
    while time.time() - t0 < 3 and kv(os.path.join(A, "lobby.txt")).get("ack") != "1":
        time.sleep(0.1)
    la = kv(os.path.join(A, "lobby.txt"))
    check("cmd=mode 生效", la.get("mode") == mode and la.get("ack") == "1", "mode=%s ack=%s" % (la.get("mode"), la.get("ack")))
    write(os.path.join(A, "cmd.txt"), "id=2\ncmd=host\n")
    time.sleep(0.8)
    target = "127.0.0.1" + ("" if mode != "server" else ":%d" % port)
    if mode == "lan":
        # 等 B 的房间列表里出现 A（局域网广播）
        t0 = time.time()
        while time.time() - t0 < 4:
            lb = kv(os.path.join(B, "lobby.txt"))
            rooms = [lb.get("t%d" % i, "") for i in range(1, int(lb.get("n", 0)) + 1)]
            if any(r.startswith("room|") for r in rooms):
                break
            time.sleep(0.3)
        check("局域网房间出现在 B 的可加入列表", any(r.startswith("room|") for r in rooms), str(rooms))
    write(os.path.join(B, "cmd.txt"), "id=2\ncmd=join\ntarget=%s\n" % target)
    time.sleep(3.0)
    la, lb = kv(os.path.join(A, "lobby.txt")), kv(os.path.join(B, "lobby.txt"))
    sa, sb = kv(os.path.join(A, "session.txt")), kv(os.path.join(B, "session.txt"))
    ra, rb = kv(os.path.join(A, "state_remote.txt")), kv(os.path.join(B, "state_remote.txt"))
    check("A 是主机且已连接", la.get("role") == "host" and la.get("connected") == "1", str({k: la.get(k) for k in ("role", "connected", "peer", "port")}))
    check("B 是客机且已连接", lb.get("role") == "client" and lb.get("connected") == "1", str({k: lb.get(k) for k in ("role", "connected", "peer")}))
    check("session.txt 带 mode", sa.get("mode") == mode and sb.get("role") == "client", "A=%s B=%s" % (sa, sb))
    check("A 收到 B 的状态", ra.get("side") == "B", "side=" + ra.get("side", "?"))
    check("B 收到 A 的状态", rb.get("side") == "A", "side=" + rb.get("side", "?"))
    check("ping 有值", float(lb.get("ping", -1)) >= 0, "ping=" + lb.get("ping", "?"))
    # v0.18.7：状态包带昵称（nm=），连上后对方的 peername 是昵称（以前直连方式为空）
    check("A 看到对方昵称 乙方", la.get("peername") == "乙方", "peername=" + la.get("peername", "?"))
    check("B 看到对方昵称 甲方", lb.get("peername") == "甲方", "peername=" + lb.get("peername", "?"))
    check("转给游戏的状态里也带 nm", ra.get("nm") == "乙方", "nm=" + ra.get("nm", "?"))
    recent = kv(os.path.join(B, "netcfg.txt")).get("recent_" + mode, "")
    check("加入过的地址记进最近列表", recent != "", recent)
    # 回环下局域网发现收得到 A 的房间，地址也是 127.0.0.1，列表按目标去重后"最近"那条并进房间那条：认目标在列表里
    check("lobby.txt 列出最近", any(lb.get("t%d" % i, "").startswith("recent|") or (mode == "lan" and lb.get("t%d" % i, "").endswith("|" + target))
                                for i in range(1, int(lb.get("n", 0)) + 1)),
          str({k: v for k, v in lb.items() if k == "n" or k[:1] == "t"}))
    # 等联机程序回 ack 再查（以前固定等 0.6 / 0.8 秒，机器忙时偶发赶不上）
    def wait_ack(d, ack, sec=3.0):
        t0 = time.time()
        while time.time() - t0 < sec:
            x = kv(os.path.join(d, "lobby.txt"))
            if x.get("ack") == ack:
                return x
            time.sleep(0.1)
        return kv(os.path.join(d, "lobby.txt"))

    write(os.path.join(B, "cmd.txt"), "id=3\ncmd=mode\nmode=ai\n")     # 起桥接平台两种方式默认不列（会被当成 lan），换一个一直都有的
    lb = wait_ack(B, "3")
    check("联机中不能换方式", lb.get("mode") == mode and "断开" in lb.get("err", ""), "mode=%s err=%s" % (lb.get("mode"), lb.get("err")))
    write(os.path.join(B, "cmd.txt"), "id=4\ncmd=stop\n")
    lb = wait_ack(B, "4")
    check("cmd=stop 后 B 未联机", lb.get("role") == "none" and kv(os.path.join(B, "session.txt")).get("role") == "none",
          "lobby role=%s ack=%s msg=%s" % (lb.get("role"), lb.get("ack"), lb.get("msg")))
    time.sleep(2.6)
    la = kv(os.path.join(A, "lobby.txt"))
    check("A 发现对方断开", la.get("connected") == "0", "connected=" + la.get("connected", "?"))
finally:
    stop = True
    for p in procs:
        p.kill()
print("共 %d 项失败" % len(fails))
sys.exit(1 if fails else 0)
