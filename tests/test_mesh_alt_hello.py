# -*- coding: utf-8 -*-
# 多人房房主：同一个人从"另一族地址"（已记成 AltHandle）又发来加入握手时，不能拿它顶掉当前那条（Handle）。
# 以前 MpMesh.AddPlayer 会把 Handle 换成另一族，原来那条（对方实际发状态包的那条）就谁也不认了：
# 对方的状态包全被当成陌生地址丢掉，房主看不到这个人的替身，对方却看得到房主。
# 做法：反射载入联机程序 exe（不开网络），直接调 MpMesh.AddPlayer / PidByHandle。
#   python -X utf8 -I test_mesh_alt_hello.py <联机程序.exe>
import os
import subprocess
import sys
import tempfile

sys.stdout.reconfigure(encoding="utf-8")
exe = os.path.abspath(sys.argv[1])
csc = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), r"Microsoft.NET\Framework64\v4.0.30319\csc.exe")

SRC = r'''
using System;
using System.Net;
using System.Reflection;
static class T
{
    static BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static Assembly A;
    static object Mesh()
    {
        var m = Activator.CreateInstance(A.GetType("ForNet.MpMesh"), true);
        Set(m, "IsHost", true);
        Set(m, "MyPid", 1);
        return m;
    }
    static void Set(object o, string n, object v)
    {
        var f = o.GetType().GetField(n, F); if (f != null) { f.SetValue(o, v); return; }
        o.GetType().GetProperty(n, F).SetValue(o, v, null);
    }
    static object Get(object o, string n)
    {
        var f = o.GetType().GetField(n, F); if (f != null) return f.GetValue(o);
        return o.GetType().GetProperty(n, F).GetValue(o, null);
    }
    static void Add(object m, int pid, IPEndPoint h)
    {
        m.GetType().GetMethod("AddPlayer", F).Invoke(m, new object[] { pid, h, h.ToString(), 0UL, "P" + pid, "", false, "" });
    }
    static int Pid(object m, IPEndPoint h) { return (int)m.GetType().GetMethod("PidByHandle", F).Invoke(m, new object[] { h }); }
    static object Peer(object m, int pid) { return m.GetType().GetMethod("Get", F, null, new[] { typeof(int) }, null).Invoke(m, new object[] { pid }); }
    static int fails;
    static void Check(bool ok, string name, string detail)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
        if (!ok) fails++;
    }
    static int Main(string[] a)
    {
        A = Assembly.LoadFrom(a[0]);
        var v6 = new IPEndPoint(IPAddress.Parse("2001:db8::2"), 50643);
        var v4 = new IPEndPoint(IPAddress.Parse("198.51.100.2"), 16733);
        var v6b = new IPEndPoint(IPAddress.Parse("2001:db8::2"), 50999);

        // ① 编号 2 先从 IPv6 进来，IPv4 记成备用地址，之后 IPv4 又发来加入握手
        var m = Mesh();
        Add(m, 2, v6);
        Set(Peer(m, 2), "AltHandle", v4);
        Add(m, 2, v4);
        var p = Peer(m, 2);
        Check(v6.Equals(Get(p, "Handle")), "另一族地址再握手：当前那条（IPv6）不被顶掉", "Handle=" + Get(p, "Handle"));
        Check(v4.Equals(Get(p, "AltHandle")), "另一族地址仍是备用地址", "AltHandle=" + Get(p, "AltHandle"));
        Check(Pid(m, v6) == 2, "IPv6 来的状态包仍认成编号 2", "PidByHandle=" + Pid(m, v6));
        Check(Pid(m, v4) == 2, "IPv4 来的包也认成编号 2", "PidByHandle=" + Pid(m, v4));

        // ② 同一族换了端口（NAT 重新映射）：照旧换过去
        var m2 = Mesh();
        Add(m2, 2, v6);
        Add(m2, 2, v6b);
        Check(v6b.Equals(Get(Peer(m2, 2), "Handle")), "同一族换端口照旧更新 Handle", "Handle=" + Get(Peer(m2, 2), "Handle"));

        // ③ 当前那条自己再握手：不变
        var m3 = Mesh();
        Add(m3, 2, v6);
        Set(Peer(m3, 2), "AltHandle", v4);
        Add(m3, 2, v6);
        Check(v6.Equals(Get(Peer(m3, 2), "Handle")) && v4.Equals(Get(Peer(m3, 2), "AltHandle")), "当前那条重复握手不动两条地址", "");
        Console.WriteLine("共 " + fails + " 项失败");
        return fails == 0 ? 0 : 1;
    }
}
'''

d = tempfile.mkdtemp(prefix="meshalt_")
cs, out = os.path.join(d, "t.cs"), os.path.join(d, "t.exe")
with open(cs, "w", encoding="utf-8") as f:
    f.write(SRC)
r = subprocess.run([csc, "/nologo", "/platform:x64", "/codepage:65001", "/out:" + out, cs], capture_output=True, text=True, encoding="utf-8", errors="replace")
if r.returncode != 0:
    print("FAIL 编译测试程序\n" + r.stdout + r.stderr)
    sys.exit(1)
r = subprocess.run([out, exe], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
print(r.stdout + r.stderr, end="")
sys.exit(r.returncode)
