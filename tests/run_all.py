# fornet 仓库自带测试：先编不带任何游戏适配的基底联机程序，再逐个跑（11-13 测试分家，HANDOVER §274）。
#   python -X utf8 -I tests/run_all.py [lupa 所在目录]
# 要：Windows 自带的 csc（.NET Framework 4）、node（服务端）、lupa（Lua 测试；没有就跳过那两项）。
# test_ext_lobby 要先编示例插件（samples/build_hello.bat，要 C 编译器），没有 DLL 就跳过。
import glob, os, shutil, subprocess, sys, tempfile
HERE = os.path.dirname(os.path.abspath(__file__)); FR = os.path.dirname(HERE)
env = dict(os.environ); env.pop("FORNET_GAME_CS", None)
r = subprocess.run(["cmd", "/c", os.path.join(FR, "lobby", "build.bat"), "base"], capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
exes = sorted(glob.glob(os.path.join(FR, "lobby", "*-base.exe")))
if r.returncode != 0 or not exes:
    print(r.stdout[-800:] + r.stderr[-800:]); sys.exit("基底联机程序编译失败")
exe = exes[0]
# 示例插件（C，要 LLVM）：没编过、又找得到 clang 就先编，test_ext_lobby 才测得到插件 C 接口
hello = os.path.join(FR, "samples", "hello")
if not glob.glob(os.path.join(hello, "*.dll")):
    llvm = os.environ.get("LLVM_DIR") or r"C:\Program Files\LLVM\bin"
    if os.path.exists(os.path.join(llvm, "clang.exe")):
        rb = subprocess.run(["cmd", "/c", os.path.join(FR, "samples", "build_hello.bat")], capture_output=True, text=True, encoding="utf-8", errors="replace")
        print("示例插件：" + ("编好了" if rb.returncode == 0 else "没编过：" + (rb.stdout + rb.stderr)[-300:]))
    else:
        print("没有 LLVM（clang.exe）：示例插件不编，test_ext_lobby 跳过")
lupa = sys.argv[1] if len(sys.argv) > 1 else None
if lupa is None:
    try:
        import lupa as _l; lupa = os.path.dirname(os.path.dirname(_l.__file__))
    except Exception:
        lupa = None
results = []
def run(name, args):
    p = os.path.join(HERE, name + ".py")
    rr = subprocess.run([sys.executable, "-X", "utf8", "-I", p] + args, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600)
    out = rr.stdout + rr.stderr
    n_pass = sum(1 for l in out.splitlines() if l.startswith("PASS"))
    n_fail = sum(1 for l in out.splitlines() if l.startswith("FAIL"))
    st = "跳过" if rr.returncode == 2 else ("过" if rr.returncode == 0 else "失败")
    results.append((name, st, n_pass, n_fail))
    print("%-18s %s  PASS %d  FAIL %d" % (name, st, n_pass, n_fail), flush=True)
    if st == "失败": print("\n".join(out.splitlines()[-15:]))
work = tempfile.mkdtemp(prefix="fornet_tests_")
if lupa:
    run("test_host", [lupa]); run("test_ext", [lupa])
else:
    print("没有 lupa：跳过 test_host / test_ext")
run("test_wire_4n", [])
for t in ("test_lobby_cmd", "test_consent_gate", "test_history", "test_ext_lobby", "test_server_lobby"):
    d = os.path.join(work, t); os.makedirs(d)
    run(t, [exe, d])
shutil.rmtree(work, ignore_errors=True)
bad = [r for r in results if r[1] == "失败"]
print("共 %d 套：过 %d / 跳过 %d / 失败 %d" % (len(results), sum(1 for r in results if r[1] == "过"), sum(1 for r in results if r[1] == "跳过"), len(bad)))
sys.exit(1 if bad else 0)
