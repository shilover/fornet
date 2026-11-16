# 4net 自带测试：路径以本目录的上一级（仓库根）为根。
# F1：服务端双认控制包前缀 `4N|`（fornet）和 `4N|`（0.3x 客户端），回包用对方来时的前缀。
# python -I test_wire_4n.py （起本地服务端 27161，不碰别的端口）
import os, socket, subprocess, sys, time
HERE = os.path.dirname(os.path.abspath(__file__)); REPO = os.path.dirname(HERE)
PORT = 27161
fails = []
def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("  " + detail if detail else ""), flush=True)
    if not ok: fails.append(name)
srv = subprocess.Popen(["node", os.path.join(REPO, "server", "fornet-lobby-server.mjs"), "--port", str(PORT), "--host", "127.0.0.1", "--quiet"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
try:
    def ask(msg):
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(2.0)
        s.sendto(msg.encode("utf-8"), ("127.0.0.1", PORT))
        try: return s.recvfrom(4000)[0].decode("utf-8", "replace")
        except (socket.timeout, ConnectionResetError): return None     # 端口还没人听时 Windows 回 ICMP → ConnectionReset
        finally: s.close()
    for _ in range(40):          # 等服务端起来（机器忙时 node 要几秒）
        if ask("4N|CFGQ|-") is not None: break
        time.sleep(0.25)
    GAP = 5.2     # 服务端 CFGQ 按来源 IP 限速 5 秒一次（0.34 评审），每问一次隔一下
    time.sleep(GAP)
    r = ask("4N|CFGQ|-")
    check("4N| 请求 → 4N| 回包（旧客户端照旧）", r is not None and r.startswith("4N|CFG|"), str(r))
    time.sleep(GAP)
    r = ask("4N|CFGQ|-")
    check("4N| 请求 → 4N| 回包", r is not None and r.startswith("4N|CFG|"), str(r))
    # 同一个来源先 4N 再 4N：回包跟着最后一次
    OLD, NEW = "4N|", "4N|"     # fornet 仓库导出改名后两个都是 4N|：没有"旧前缀"可切，这一项跳过
    if OLD != NEW:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(2.0)
        time.sleep(GAP)
        s.sendto(NEW.encode() + b"CFGQ|-", ("127.0.0.1", PORT)); a = s.recvfrom(4000)[0][:len(NEW)]
        time.sleep(GAP)
        s.sendto(OLD.encode() + b"CFGQ|-", ("127.0.0.1", PORT)); b = s.recvfrom(4000)[0][:len(OLD)]
        s.close()
        check("同一来源切回 4N| 后回包也切回", a == NEW.encode() and b == OLD.encode(), "%s %s" % (a, b))
    time.sleep(GAP)
    time.sleep(GAP)
    r = ask("XX|CFGQ|-")
    check("别的前缀照旧丢", r is None, str(r))
finally:
    srv.kill()
print("共 %d 项失败" % len(fails))
sys.exit(1 if fails else 0)
