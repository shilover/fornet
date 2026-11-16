# fornet 仓库自带测试（11-13 测试分家）：路径以本目录的上一级为根（私有库里是 fornet/，fornet 仓库里是仓库根）。原件：私有库 tests/test_server_lobby.py
# 服务器大厅（mode=server）离线测试：本地起服务端 fornet/server/fornet-lobby-server.mjs + 两个后台联机程序实例（各用自己的 --dir），
# 不开游戏、不碰 27015 / 27016（服务器大厅的游戏端口是系统随机分配的）。
# python -I test_server_lobby.py <FornetLobby.exe> <工作目录> [服务器端口，默认 27120 | 外部服务器 域名:端口]
# 第 3 个参数带域名 / IP（比如 lobby.example.com:27020）时不起本地服务端，直接测那台（两个实例在同一台电脑、同一个 NAT 后面，
# 直连走的是局域网地址那条候选；中转是真的经过那台服务器）；服务端日志那项跳过。
# 三段：
# 1) 直连：A 建房 → B 的列表里出现 → B 加入 → 打洞直连成功、双向收包、线路 = direct
# 2) 0.34（最终方案 3）：对局不再经服务器中转。一边加 --sim-no-direct（丢掉所有不是服务器发来的包 = 只有单向通），主机丢 / 客机丢各一次
# → 3 次直连都失败 → srverr 提示"对局走桥接"那条路、不请求中转、线路从不变成 relay、重新加入接着试直连（回到 punch1）
# 2c) 服务端没开 --relay：客机同样不请求中转，提示一样（服务器开没开中转对客机没区别了）
# （旧的 2d / 2e"客机 / 主机设置里允许中转"随设置项一起删了）
# 2f) 主机进不来也收不到中转（校园网那种）：测试脚本自己当主机登记房间、什么都不回 → 客机直连 3 次不通、
# 中转一个包都没收到 → srverr 提示"校园网 … 让对方建房"，重新加入、接着试直连时提示还在
# 3) 服务器没开：大厅地址填一个没人听的端口 → lobby.txt 的 srverr 报"没回应"
# 4) 服务器互通（只在本地模式跑）：再起两台服务端 B、A，只有 A 配 --upstream B。
# 主机在 B、客机连 A → 客机列表里出现 [互通] 房间、加入去 B、直连；主机在 A、客机连 B（B 靠 A 推送看到）→ 客机丢直连包 → 经 A 中转
# 每段再看两边的连接诊断 net\net_conn.log：有对应的事件行（直连成功 / 请求中转 / 服务器同意 / 中转第一个包 /
# 对方没动静 / norelay / hostnorelay / 服务器没回应），且里面没有完整 IP（脱敏成前两段 + 短哈希）
import os, re, sys, time, socket, subprocess, threading

sys.stdout.reconfigure(encoding="utf-8")

exe, base = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
arg3 = sys.argv[3] if len(sys.argv) > 3 else "27120"
external = ":" in arg3
if not external:
    os.environ.setdefault("FORNET_LOOPBACK", "1")   # 本地服务端时联机程序只绑 127.0.0.1（测外部服务器要走真网卡，不设）
sport = 27120 if external else int(arg3)
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
server_js = os.path.join(root, "server", "fornet-lobby-server.mjs")
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
    print(("PASS " if ok else "FAIL ") + name + ("  " + detail if detail else ""), flush=True)
    if not ok:
        fails.append(name)


def wait(path, pred, sec):
    t0 = time.time()
    d = {}
    while time.time() - t0 < sec:
        d = kv(path)
        if d and pred(d):
            return d, time.time() - t0
        time.sleep(0.1)
    return d, time.time() - t0


IP_RE = re.compile(r"(?<![\d.])\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?![\d.])")


def connlog(d):
    try:
        with open(os.path.join(d, "net_conn.log"), encoding="utf-8") as f:
            return f.read()
    except OSError:
        return ""


def check_log(tag, d, needles, sec=0):
    """：d 的 net_conn.log 里每个 needle 都有（最多等 sec 秒），且没有完整 IPv4"""
    t0 = time.time()
    text = connlog(d)
    while sec > 0 and time.time() - t0 < sec and not all(n in text for n in needles):
        time.sleep(0.2)
        text = connlog(d)
    side = os.path.basename(d)
    for n in needles:
        check("%s：%s 的 net_conn.log 有「%s」" % (tag, side, n), n in text, "" if n in text else text[-600:])
    ips = IP_RE.findall(text)
    check("%s：%s 的 net_conn.log 里没有完整 IP" % (tag, side), text != "" and not ips, str(ips[:3]) if ips else "%d 字节" % len(text))


def targets(d):
    return [d.get("t%d" % i, "") for i in range(1, int(d.get("n", 0) or 0) + 1)]


stop = False


def fake_games():   # 同 fake_two_games.py：两边各写自己的 state_local
    seq = 0
    while not stop:
        seq += 1
        for side, d in (("A", A), ("B", B)):
            if os.path.isdir(d):
                write(os.path.join(d, "state_local.txt"), "v=2\nseq=%d\nmap=Map_Xigu\nx=%d.0\nside=%s\n" % (seq, seq, side))
        time.sleep(1 / 30)


PORTS = {}


def blocked(server):
    """假主机：只每秒发 REG 保住房间，打洞包、中转包一概不回（= 校园网主机：外面进不来、到服务器的高频包被限流）"""
    host, port = server.rsplit(":", 1)
    srv = (host, int(port))
    sk = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sk.bind(("127.0.0.1", 0))
    sk.settimeout(0.2)
    me = "127.0.0.1:%d" % sk.getsockname()[1]
    stop = threading.Event()
    room = []

    def run():
        last = 0
        while not stop.is_set():
            if time.time() - last > 1:
                last = time.time()
                sk.sendto(("4N|REG|campus-test-key-134|校园网主机|?|0|%s|1" % me).encode("utf-8"), srv)
            try:
                f = sk.recvfrom(4096)[0].decode("utf-8", "replace").split("|")
            except OSError:
                continue
            if len(f) > 2 and f[0] == "4N" and f[1] == "REGOK" and not room:
                room.append(f[2])
    th = threading.Thread(target=run, daemon=True)
    th.start()
    fresh(B, "乙方", server, relay=1)
    p = start(B)
    hint = "对局走桥接"     # 0.34
    try:
        t0 = time.time()
        while not room and time.time() - t0 < 5:
            time.sleep(0.1)
        check("2f：假主机登记上房间", bool(room), "")
        if not room:
            return
        lb, _ = wait(os.path.join(B, "lobby.txt"), lambda d: any(is_room(x, room[0]) for x in targets(d)), 8)
        write(os.path.join(B, "cmd.txt"), "id=c1\ncmd=join\ntarget=room:%s\n" % room[0])
        routes = []

        def seen(d):
            r = d.get("route", "")
            if not routes or routes[-1] != r:
                routes.append(r)
            return hint in d.get("srverr", "")
        lb, t = wait(os.path.join(B, "lobby.txt"), seen, 25)
        err = lb.get("srverr", "")
        # 0.34 对局不中转：校园网主机那种情形和普通直连失败一样——提示开"对局走桥接"、从不进中转、重新加入接着试
        check("2f：客机 srverr 提示 对局走桥接（0.34 没有校园网专用提示了）", hint in err and lb.get("connected") != "1",
              "srverr=%s（%.1f 秒）经过 %s" % (err, t, " → ".join(routes)))
        check("2f：从没进过中转", "relay" not in routes, " → ".join(routes))
        lb, t = wait(os.path.join(B, "lobby.txt"), lambda d: seen(d) and d.get("route", "").startswith("punch"), 8)
        check("2f：重新加入、接着试直连，提示还在", lb.get("route", "").startswith("punch") and hint in lb.get("srverr", ""),
              "route=%s srverr=%s（%.1f 秒）" % (lb.get("route"), lb.get("srverr"), t))
        check_log("2f", B, ["0.34 对局不中转", "重新加入"])
    finally:
        stop.set()
        p.kill()
        th.join(2)
        sk.close()
        time.sleep(0.5)


def fresh(d, name, server, relay=1):
    os.makedirs(d, exist_ok=True)
    for f in os.listdir(d):
        try:
            os.remove(os.path.join(d, f))
        except FileNotFoundError:   # 假游戏线程正好在写 / 替换 state_local 的临时文件
            pass
    # v0.20.3 起服务器大厅的主机监听设置里的端口（默认 27015，给 IP 直连用）：测试每个目录各用一个，不占 27015
    PORTS.setdefault(d, 27130 + len(PORTS))
    write(os.path.join(d, "netcfg.txt"), "mode=server\nname=%s\nserver=%s\nport=%d\nrelay=%d\n" % (name, server, PORTS[d], relay))
    write(os.path.join(d, "lang.txt"), "lang=zh\n")


def start(d, extra=()):
    return subprocess.Popen([exe, "--bg", "--consent-all", "--no-backup", "--dir", d, "--seconds", "45"] + list(extra))


def is_room(x, room):
    """可加入目标那一行是不是这个房间（target = room:<房间号> 或 room:<房间号>@<所在服务器>）"""
    t = x.split("|")[-1]
    return t == "room:" + room or t.startswith("room:" + room + "@")


def round_trip(tag, host_dir, join_dir, join_extra, host_extra, expect_route, max_connect, linked=None):
    """host_dir 建房 → join_dir 从列表里加入；linked = 房间在别的服务器（互通）时，它的地址"""
    procs = [start(host_dir, host_extra), start(join_dir, join_extra)]
    try:
        la, _ = wait(os.path.join(host_dir, "lobby.txt"), lambda d: d.get("alive"), 8)
        wait(os.path.join(join_dir, "lobby.txt"), lambda d: d.get("alive"), 8)
        check(tag + "：两个实例都在写 lobby.txt，方式 = server", la.get("mode") == "server", "mode=" + la.get("mode", "?"))
        write(os.path.join(host_dir, "cmd.txt"), "id=h1\ncmd=host\n")
        la, t = wait(os.path.join(host_dir, "lobby.txt"), lambda d: d.get("room", "") != "", 5)
        room = la.get("room", "")
        check(tag + "：主机登记到大厅、拿到房间号", len(room) == 6 and la.get("role") == "host",
              "room=%s myaddr=%s（%.1f 秒）" % (room, la.get("myaddr"), t))
        check(tag + "：主机线路 = wait（等人）", la.get("route") == "wait", "route=" + la.get("route", "?"))
        lb, t = wait(os.path.join(join_dir, "lobby.txt"), lambda d: any(is_room(x, room) for x in targets(d)), 8)
        tg = [x for x in targets(lb) if is_room(x, room)]
        check(tag + "：房间出现在对方的可加入列表", len(tg) == 1 and tg[0].startswith("room|"), "%s（%.1f 秒）" % (tg or targets(lb), t))
        target = tg[0].split("|")[-1] if tg else "room:" + room
        if linked:
            check(tag + "：标 [互通]、target 带所在服务器", tg and tg[0].startswith("room|[互通] ") and target.endswith("@" + linked), target)
        write(os.path.join(join_dir, "cmd.txt"), "id=j1\ncmd=join\ntarget=%s\n" % target)
        routes = []

        def both(d):
            r = d.get("route", "")
            if not routes or routes[-1] != r:
                routes.append(r)
            return d.get("connected") == "1" and r == expect_route

        lb, t = wait(os.path.join(join_dir, "lobby.txt"), both, max_connect)
        check(tag + "：客机已连接，线路 = " + expect_route, lb.get("connected") == "1" and lb.get("route") == expect_route,
              "connected=%s route=%s（%.1f 秒）经过 %s" % (lb.get("connected"), lb.get("route"), t, " → ".join(routes)))
        la, _ = wait(os.path.join(host_dir, "lobby.txt"), lambda d: d.get("connected") == "1" and d.get("route") == expect_route, 4)
        check(tag + "：主机已连接，线路 = " + expect_route, la.get("connected") == "1" and la.get("route") == expect_route,
              "connected=%s route=%s peer=%s" % (la.get("connected"), la.get("route"), la.get("peer")))
        time.sleep(1.5)
        ra, rb = kv(os.path.join(host_dir, "state_remote.txt")), kv(os.path.join(join_dir, "state_remote.txt"))
        hs, js = os.path.basename(join_dir), os.path.basename(host_dir)
        check(tag + "：主机收到客机的状态", ra.get("side") == hs, "side=" + ra.get("side", "?"))
        check(tag + "：客机收到主机的状态", rb.get("side") == js, "side=" + rb.get("side", "?"))
        # 3 秒内主机 / 客机各收到多少个新状态包（假游戏每秒写 30 次，Windows 上 sleep 精度差，实际 20 多）
        s1 = [int(kv(os.path.join(x, "state_remote.txt")).get("seq", 0)) for x in (host_dir, join_dir)]
        time.sleep(3.0)
        s2 = [int(kv(os.path.join(x, "state_remote.txt")).get("seq", 0)) for x in (host_dir, join_dir)]
        rate = [(b - a) / 3.0 for a, b in zip(s1, s2)]
        check(tag + "：双向每秒 18 个以上新包", min(rate) >= 18, "主机收 %.1f/秒，客机收 %.1f/秒" % tuple(rate))
        la, lb = kv(os.path.join(host_dir, "lobby.txt")), kv(os.path.join(join_dir, "lobby.txt"))
        check(tag + "：双方看到对方昵称", la.get("peername") == lb.get("name") and lb.get("peername") == la.get("name"),
              "主机看到 %s，客机看到 %s" % (la.get("peername"), lb.get("peername")))
        check(tag + "：ping 有值", float(lb.get("ping", -1)) >= 0, "ping=" + lb.get("ping", "?"))
        lh = kv(os.path.join(host_dir, "lobby.txt"))
        hl = targets(lh)
        check(tag + "：主机自己的列表里不列自己的房间", not any(is_room(x, room) for x in hl), str(hl))
        write(os.path.join(join_dir, "cmd.txt"), "id=j2\ncmd=stop\n")
        la, t = wait(os.path.join(host_dir, "lobby.txt"), lambda d: d.get("connected") == "0" and d.get("route") == "wait", 12)
        check(tag + "：客机断开后主机重新开放房间", la.get("connected") == "0" and la.get("route") == "wait",
              "connected=%s route=%s（%.1f 秒）" % (la.get("connected"), la.get("route"), t))
        common = ["联机程序启动：", "会话开始：角色 ", "大厅服务器有回应"]
        if expect_route == "relay":
            check_log(tag, join_dir, common + ["请求服务器中转（RELAY）", "服务器同意中转（RELAYOK", "中转收到对方第一个包", "连上：收到对方状态包"])
            check_log(tag, host_dir, common + ["房间已登记", "有人要加入", "中转收到对方第一个包", "秒没动静，房间重新开放（之前经中转"], 2)
        else:
            check_log(tag, join_dir, common + ["发加入请求（JOIN）", "服务器给主机地址", "直连成功", "连上：收到对方状态包"])
            check_log(tag, host_dir, common + ["房间已登记", "有人要加入", "直连成功", "秒没动静，房间重新开放（之前直连"], 2)
        write(os.path.join(host_dir, "cmd.txt"), "id=h2\ncmd=stop\n")
        lb, t = wait(os.path.join(join_dir, "lobby.txt"), lambda d: not any(x.startswith("room|") for x in targets(d)), 14)
        check(tag + "：主机断开后房间从列表里消失", not any(x.startswith("room|") for x in targets(lb)), "%s（%.1f 秒）" % (targets(lb), t))
    finally:
        for p in procs:
            p.kill()
        time.sleep(0.5)


def no_relay(tag, hint, log_needle, host_extra=()):
    """A 建房、B 加入（默认 B 丢直连包；host_extra 给 A 时是主机丢）：直连 3 次不通后 B 的 srverr 出现 hint、不进中转、回到 punch1 接着试"""
    procs = [start(A, list(host_extra)), start(B, [] if host_extra else ["--sim-no-direct"])]
    try:
        time.sleep(1.0)
        write(os.path.join(A, "cmd.txt"), "id=n1\ncmd=host\n")
        la, _ = wait(os.path.join(A, "lobby.txt"), lambda d: d.get("room", "") != "", 6)
        room = la.get("room", "")
        lb, _ = wait(os.path.join(B, "lobby.txt"), lambda d: any(is_room(x, room) for x in targets(d)), 8)
        write(os.path.join(B, "cmd.txt"), "id=n2\ncmd=join\ntarget=room:%s\n" % room)
        routes = []

        def seen(d):
            r = d.get("route", "")
            if not routes or routes[-1] != r:
                routes.append(r)
            return hint in d.get("srverr", "")
        lb, t = wait(os.path.join(B, "lobby.txt"), seen, 15)
        check(tag + "：客机 srverr 提示" + hint, hint in lb.get("srverr", "") and lb.get("connected") != "1",
              "srverr=%s connected=%s（%.1f 秒）经过 %s" % (lb.get("srverr"), lb.get("connected"), t, " → ".join(routes)))
        lb, t = wait(os.path.join(B, "lobby.txt"), lambda d: seen(d) and d.get("route") == "punch1", 8)
        check(tag + "：重新加入、接着试直连（回到 punch1），提示还在", lb.get("route") == "punch1" and hint in lb.get("srverr", ""),
              "route=%s srverr=%s（%.1f 秒）" % (lb.get("route"), lb.get("srverr"), t))
        check(tag + "：线路从没变成 relay", "relay" not in routes, " → ".join(routes))
        la = kv(os.path.join(A, "lobby.txt"))
        check(tag + "：主机也没进中转", la.get("route") != "relay", "route=" + la.get("route", "?"))
        check_log(tag, B, ["发加入请求（JOIN）", "第 1 次直连 2.5 秒没通", log_needle, "重新加入再试"])
        check_log(tag, A, ["有人要加入"])
    finally:
        for x in procs:
            x.kill()
        time.sleep(0.5)


os.makedirs(base, exist_ok=True)
srv_log = open(os.path.join(base, "server.log"), "w", encoding="utf-8")
srv = subprocess.Popen(["node", server_js, "--port", str(sport), "--relay"] if not external else ["cmd", "/c", "exit"], stdout=srv_log, stderr=subprocess.STDOUT)
th = threading.Thread(target=fake_games, daemon=True)
try:
    time.sleep(1.0)
    server = arg3 if external else "127.0.0.1:%d" % sport
    if not external:
        check("服务端启动", srv.poll() is None)
    na, nb = ("测试-甲方", "测试-乙方") if external else ("甲方", "乙方")   # 公共服务器上别人也看得到
    fresh(A, na, server)
    fresh(B, nb, server)
    th.start()
    print("== 1) 直连", flush=True)
    round_trip("直连", A, B, [], [], "direct", 6)
    print("== 2a) 直连不通、对局不中转（0.34）：主机丢掉所有直连包 → 客机提示 对局走桥接、不进中转", flush=True)
    fresh(A, na, server)
    fresh(B, nb, server)
    no_relay("对局不中转(主机收不到)", "对局走桥接", "0.34 对局不中转", host_extra=["--sim-no-direct"])
    print("== 2b) 直连不通、对局不中转（0.34）：客机丢掉所有直连包", flush=True)
    fresh(A, na, server)
    fresh(B, nb, server)
    no_relay("对局不中转(客机收不到)", "对局走桥接", "0.34 对局不中转")
    srv_log.flush()
    log = open(os.path.join(base, "server.log"), encoding="utf-8").read()
    if not external:
        check("服务端日志：没有任何会话改走中转（0.34 客机不请求）", "改走中转" not in log, "")
        check("服务端日志：启动时写 对局中转开 / 观战转发开（--relay 两个都开）", "对局中转开" in log and "观战转发开" in log, "")
        print("== 2c) 服务端没开 --relay：客机同样不请求中转、提示一样", flush=True)
        pn = sport + 4
        nlog = open(os.path.join(base, "server_norelay.log"), "w", encoding="utf-8")
        nsrv = subprocess.Popen(["node", server_js, "--port", str(pn)], stdout=nlog, stderr=subprocess.STDOUT)
        fresh(A, na, "127.0.0.1:%d" % pn)
        fresh(B, nb, "127.0.0.1:%d" % pn)
        try:
            no_relay("没开中转", "对局走桥接", "0.34 对局不中转")
        finally:
            nsrv.kill()
            nlog.close()
            time.sleep(0.5)
        check("没开中转：服务端启动时写 对局中转关 / 观战转发关", "对局中转关" in open(os.path.join(base, "server_norelay.log"), encoding="utf-8").read(), "")
        print("== 2c') 只开观战转发（测试服务器 0.34 的配置 --relay-watch）：启动行写 对局中转关、观战转发开", flush=True)
        pw = sport + 5
        wlog = open(os.path.join(base, "server_watch.log"), "w", encoding="utf-8")
        wsrv = subprocess.Popen(["node", server_js, "--port", str(pw), "--relay-watch"], stdout=wlog, stderr=subprocess.STDOUT)
        time.sleep(1.0)
        wsrv.kill()
        wlog.close()
        time.sleep(0.3)
        wl = open(os.path.join(base, "server_watch.log"), encoding="utf-8").read()
        check("--relay-watch：对局中转关、观战转发开", "对局中转关" in wl and "观战转发开" in wl, wl[-200:])
        print("== 2f) 主机进不来、中转也没包（校园网）：脚本当主机只登记房间、什么都不回", flush=True)
        blocked(server)
    print("== 3) 大厅服务器没开", flush=True)
    fresh(A, "甲方", "127.0.0.1:%d" % (sport + 1))
    p = start(A)
    try:
        la, t = wait(os.path.join(A, "lobby.txt"), lambda d: "没回应" in d.get("srverr", ""), 15)
        check("服务器没开 → srverr 没回应", "没回应" in la.get("srverr", ""), "srverr=%s（%.1f 秒）" % (la.get("srverr"), t))
        check_log("服务器没开", A, ["房间列表：大厅服务器没回应"], 3)
        write(os.path.join(A, "cmd.txt"), "id=s1\ncmd=server\nserver=\n")
        la, _ = wait(os.path.join(A, "lobby.txt"), lambda d: d.get("ack") == "s1", 3)
        time.sleep(0.6)
        la = kv(os.path.join(A, "lobby.txt"))
        check("cmd=server 清空地址 → 提示先填地址", la.get("srv") == "" and "先在设置里填" in la.get("srverr", ""), "srv=%s srverr=%s" % (la.get("srv"), la.get("srverr")))
        write(os.path.join(A, "cmd.txt"), "id=s2\ncmd=server\nserver=%s\n" % server)
        la, _ = wait(os.path.join(A, "lobby.txt"), lambda d: d.get("srv") == server, 3)
        check("cmd=server 设地址 → netcfg 记住", kv(os.path.join(A, "netcfg.txt")).get("server") == server, la.get("srv", "?"))
        write(os.path.join(A, "cmd.txt"), "id=s3\ncmd=server\nserver=不是地址\n")
        la, _ = wait(os.path.join(A, "lobby.txt"), lambda d: d.get("ack") == "s3", 3)
        time.sleep(0.6)
        la = kv(os.path.join(A, "lobby.txt"))
        check("cmd=server 填错地址 → 报错、不改", la.get("srv") == server and la.get("err", "") != "", "srv=%s err=%s" % (la.get("srv"), la.get("err")))
    finally:
        p.kill()
    if not external:
        print("== 4) 服务器互通：B 不配置，A --upstream B", flush=True)
        pb, pa = sport + 2, sport + 3
        logs = [open(os.path.join(base, n), "w", encoding="utf-8") for n in ("serverB.log", "serverA.log")]
        extra_srv = [subprocess.Popen(["node", server_js, "--port", str(pb), "--relay"], stdout=logs[0], stderr=subprocess.STDOUT),
                     subprocess.Popen(["node", server_js, "--port", str(pa), "--relay", "--upstream", "127.0.0.1:%d" % pb], stdout=logs[1], stderr=subprocess.STDOUT)]
        try:
            time.sleep(1.0)
            sb, sa = "127.0.0.1:%d" % pb, "127.0.0.1:%d" % pa
            print("== 4a) 主机在 B（被拉取的那台），客机连 A", flush=True)
            fresh(A, "甲方", sa)
            fresh(B, "乙方", sb)
            round_trip("互通(主机在B)", B, A, [], [], "direct", 6, linked=sb)
            print("== 4b) 主机在 A（推送的那台），客机连 B → 去 A 加入、直连（0.34 不中转）", flush=True)
            fresh(A, "甲方", sa)
            fresh(B, "乙方", sb)
            round_trip("互通(主机在A)", A, B, [], [], "direct", 8, linked=sa)
        finally:
            for x in extra_srv:
                x.kill()
            for f in logs:
                f.close()
        la_log = open(os.path.join(base, "serverA.log"), encoding="utf-8").read()
        check("A 的日志：客机去 A 加入（会话在主机所在的服务器）", "[join]" in la_log and "改走中转" not in la_log, "")
        print("服务端 A 日志：\n" + la_log)
        print("服务端 B 日志：\n" + open(os.path.join(base, "serverB.log"), encoding="utf-8").read())
finally:
    stop = True
    srv.kill()
    srv_log.close()
print("服务端日志：")
print(open(os.path.join(base, "server.log"), encoding="utf-8").read())
print("共 %d 项失败" % len(fails))
sys.exit(1 if fails else 0)
