# fornet 仓库自带测试（11-13 测试分家）：路径以本目录的上一级为根。原件：私有库 tests/test_consent_gate.py（去掉了本地 AI 一段）
# 问题 A：联机程序层的同意兜底（Program.ConsentGate）。真的 FornetLobby.exe，不加 --consent-all。
# python -I test_consent_gate.py <FornetLobby.exe> <工作目录>
# ① mode=server、rules.txt 只有 consent_play：cmd host → 不开会话（lobby.txt 没 route）、err 含"要先同意"、net_conn.log 有"同意兜底：拒绝 host（缺 consent_server）"
# ② cmd join target=room:XXXX → 同样被拒（缺 consent_server）
# ③ cmd spectate → 同样被拒
# ④ rules.txt 补 consent_server=1 + consent_server_addr=<填的地址> → cmd host 放行（route=wait，开始向服务器登记；服务器不存在也算"放行"）
# ⑤ mode=ai：没任何同意也放行（本地 AI 不发数据）
import os, shutil, subprocess, sys, time

exe, root = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
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
def conn_log():
    try: return open(os.path.join(net, "net_conn.log"), encoding="utf-8", errors="replace").read()
    except OSError: return ""

shutil.rmtree(root, ignore_errors=True)
net = os.path.join(root, "net"); os.makedirs(net)
SRV = "127.0.0.1:27199"      # 没人听的地址：只看放不放行，不看登记结果
write(os.path.join(net, "netcfg.txt"), "mode=server\nname=GateTest\nport=27152\nserver=%s\ndiag=1\n" % SRV)
write(os.path.join(net, "rules.txt"), "consent_play=1\n")
lob = os.path.join(net, "lobby.txt")
p = subprocess.Popen([exe, "--bg", "--no-backup", "--dir", net, "--seconds", "90"])
try:
    d, t = wait(lob, lambda d: d.get("mode") == "server", 15)
    check("启动：lobby.txt mode=server", d.get("mode") == "server", str(d.get("mode")))

    def cmd(i, body):
        write(os.path.join(net, "cmd.txt"), "id=g%d\n%s" % (i, body))
        time.sleep(2.5)
        return kv(lob), conn_log()

    d, lg = cmd(1, "cmd=host\n")
    check("① host 被拒：没开会话", not d.get("route") and d.get("connected", "0") == "0", "route=%s" % d.get("route"))
    check("① host 被拒：提示要先同意", "要先同意" in (d.get("err") or "") or "consent" in (d.get("err") or "").lower(), (d.get("err") or "")[:80])
    check("① host 被拒：net_conn.log 记了缺的键", "同意兜底：拒绝 host（缺 consent_server）" in lg, lg[-160:].replace("\n", " | "))

    d, lg = cmd(2, "cmd=join\ntarget=room:ABCDEF\n")
    check("② join 被拒", "同意兜底：拒绝 join（缺 consent_server）" in lg and not d.get("route"), "route=%s" % d.get("route"))

    d, lg = cmd(3, "cmd=spectate\ntarget=room:ABCDEF\n")
    check("③ spectate 被拒", "同意兜底：拒绝 spectate（缺 consent_server）" in lg and not d.get("route"), "route=%s" % d.get("route"))

    write(os.path.join(net, "rules.txt"), "consent_play=1\nconsent_server=1\nconsent_server_addr=%s\n" % SRV)
    time.sleep(2.5)     # Consent 缓存 2 秒
    before = conn_log().count("同意兜底")
    d, lg = cmd(4, "cmd=host\n")
    d, t = wait(lob, lambda d: d.get("route") == "wait", 6)
    check("④ 同意后 host 放行（route=wait）", d.get("route") == "wait", "route=%s %.1fs" % (d.get("route"), t))
    check("④ 放行时不再记拒绝", conn_log().count("同意兜底") == before)
finally:
    try: p.kill()
    except Exception: pass
    time.sleep(0.5)

# ⑤ 本地 AI 那段属于游戏适配（基底没有本地 AI），留在私有库的 tests/test_consent_gate.py

print("共 %d 项失败" % len(fails))
sys.exit(1 if fails else 0)
