// Ext.cs —— X2 扩展包的联机程序插件（方案 a；接口文档 docs/EXTENSIONS_API.md）
// 每个扩展包可带一个 C ABI 的 ext\<名>\lobby_<名>.dll，导出：
// int fnx_init(const FnxCtx* ctx)      // 返回 0 = 成功
// void fnx_cmd(const char* kvUtf8)       // 游戏面板 / Lua 经 API.lobby.cmd 发来的 k=v\n 文本
// void fnx_tick(void)                    // 联机程序主循环每轮一次（~10 Hz）
// void fnx_shutdown(void)
// FnxCtx（只追加字段、不改序；version 现在 1）：
// int32 version; int32 id; 然后 6 个函数指针，都是 cdecl：
// void log(int id, const char* utf8)
// int consent(int id, const char* consentId)                       // 玩家同意过这个包的 <consentId> 没（ext_<名>_<id>）
// int send(int id, const char* addr, const uint8_t* buf, int len)  // UDP 发到 "host:port"；本包登记的同意键一个没同意就拒（返回 <0）
// int recv(int id, char* addrOut, int addrCap, uint8_t* buf, int cap)   // 收自己 socket 上的包（非阻塞；0 = 没有）
// void reply(int id, const char* k, const char* v)                  // 给面板 / Lua：lobby.txt 里 x.<名>.<k>=<v>
// const char* name(int id)                                          // 自己的包名
// int resolve(int id, const char* addr, char* out, int cap)        // API 2："域名或IP[:端口]" → "ip:port"（返回长度，0 = 认不出）
// 隔离：插件只能经 send 发包，而 send 查同意、查"仅本地模式"；插件拿不到基底的 socket / 会话 / 配置。
// 插件崩了会把联机程序一起带走（同一进程），所以 init 前先写日志，出事能从 net\lobby_errors.log 看到是哪个包。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ForNet
{
    static class Ext
    {
        public const int ApiVersion = 2;     // 2（F4c-3）：加 resolve；manifest consent 里 @键 = 基底的同意键；_ 开头的回话私有
        public static volatile bool LocalOnly;      // 安全模式（仅本地）：send 一律拒

        [StructLayout(LayoutKind.Sequential)]
        struct Ctx
        {
            public int version, id;
            public IntPtr log, consent, send, recv, reply, name;
            public IntPtr resolve;      // API 2：int resolve(int id, const char* addr, char* out, int cap) → "ip:port"（返回长度；0 认不出；<0 出错）
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn(ref Ctx c);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CmdFn(IntPtr kv);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void LogCb(int id, IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ConsentCb(int id, IntPtr key);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SendCb(int id, IntPtr addr, IntPtr buf, int len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RecvCb(int id, IntPtr addrOut, int addrCap, IntPtr buf, int cap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ReplyCb(int id, IntPtr k, IntPtr v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NameCb(int id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ResolveCb(int id, IntPtr addr, IntPtr outp, int cap);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr h, string name);

        class Plugin
        {
            public string Name, Dir, Dll, State = "none", Why = "";
            public string[] Consents = new string[0];          // manifest consent=a,b → ext_<名>_a …
            public IntPtr Handle;
            public InitFn Init; public CmdFn Cmd; public VoidFn Tick, Shutdown;
            public Ctx C; public IntPtr NamePtr;
            public Dictionary<string, string> Replies = new Dictionary<string, string>();
            public UdpClient Sock;
            public int Sent, Denied, Recv;
        }
        static readonly List<Plugin> list = new List<Plugin>();
        // 回调委托要一直活着（GC 不能收），放静态字段
        static readonly LogCb cbLog = OnLog; static readonly ConsentCb cbConsent = OnConsent; static readonly SendCb cbSend = OnSend;
        static readonly RecvCb cbRecv = OnRecv; static readonly ReplyCb cbReply = OnReply; static readonly NameCb cbName = OnName; static readonly ResolveCb cbResolve = OnResolve;
        static readonly Regex NameRe = new Regex(@"^[A-Za-z0-9_\-]{1,24}$"), KeyRe = new Regex(@"^[A-Za-z0-9_\-]{1,24}$");
        public static string LastError = "";

        static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            int n = 0; while (Marshal.ReadByte(p, n) != 0 && n < 65536) n++;
            var b = new byte[n]; Marshal.Copy(p, b, 0, n);
            return Encoding.UTF8.GetString(b);
        }
        static Plugin Of(int id) { return id >= 0 && id < list.Count ? list[id] : null; }
        static string Tag(Plugin p) { return "[ext:" + p.Name + "] "; }
        // manifest consent= / 插件问的键：平时是本包的 ext_<名>_<键>；"@<键>" 指基底自己的同意键（排位插件用 @ranked = 面板"排位"那一项，不另弹一次）
        static string ConsentKey(Plugin p, string c) { return c.StartsWith("@") ? c.Substring(1) : "ext_" + p.Name + "_" + c; }

        // ---------------------------------------------------------------- 回调（插件 → 基底）
        static void OnLog(int id, IntPtr s) { var p = Of(id); if (p != null) NetLog.W(Tag(p) + Utf8(s)); }
        static int OnConsent(int id, IntPtr key)
        {
            var p = Of(id); if (p == null) return 0;
            var k = Utf8(key); if (!KeyRe.IsMatch(k.TrimStart('@'))) return 0;
            return Consent.Has(ConsentKey(p, k)) ? 1 : 0;
        }
        static bool AllConsented(Plugin p, out string why)
        {
            why = "";
            if (LocalOnly) { why = "localonly"; return false; }
            if (p.Consents.Length == 0) { why = "noconsent"; return false; }      // 没登记任何同意键 = 不许发
            foreach (var c in p.Consents) if (!Consent.Has(ConsentKey(p, c))) { why = "consent:" + c; return false; }
            return true;
        }
        static int OnSend(int id, IntPtr addr, IntPtr buf, int len)
        {
            var p = Of(id); if (p == null || len < 0 || len > 1400) return -1;
            string why;
            if (!AllConsented(p, out why)) { p.Denied++; if (p.Denied <= 3) NetLog.W(Tag(p) + "send 被拒：" + why); return -2; }
            try
            {
                var a = Utf8(addr); var i = a.LastIndexOf(':'); int port;
                if (i <= 0 || !int.TryParse(a.Substring(i + 1), out port)) return -3;
                var host = a.Substring(0, i);
                IPAddress ip;
                if (!IPAddress.TryParse(host, out ip))
                {
                    var hs = Dns.GetHostAddresses(host);
                    ip = null; foreach (var h in hs) if (h.AddressFamily == AddressFamily.InterNetwork) { ip = h; break; }
                    if (ip == null) return -3;
                }
                if (p.Sock == null) p.Sock = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                var b = new byte[len]; Marshal.Copy(buf, b, 0, len);
                p.Sock.Send(b, len, new IPEndPoint(ip, port));
                p.Sent++;
                return len;
            }
            catch (Exception ex) { NetLog.W(Tag(p) + "send 出错：" + ex.Message); return -4; }
        }
        static int OnRecv(int id, IntPtr addrOut, int addrCap, IntPtr buf, int cap)
        {
            var p = Of(id); if (p == null || p.Sock == null) return 0;
            try
            {
                if (p.Sock.Available <= 0) return 0;
                var ep = new IPEndPoint(IPAddress.Any, 0);
                var b = p.Sock.Receive(ref ep);
                var n = Math.Min(b.Length, cap);
                if (n > 0) Marshal.Copy(b, 0, buf, n);
                if (addrOut != IntPtr.Zero && addrCap > 1)
                {
                    var ab = Encoding.ASCII.GetBytes(ep.Address + ":" + ep.Port);
                    var m = Math.Min(ab.Length, addrCap - 1);
                    Marshal.Copy(ab, 0, addrOut, m); Marshal.WriteByte(addrOut, m, 0);
                }
                p.Recv++;
                return n;
            }
            catch (Exception) { return 0; }
        }
        static void OnReply(int id, IntPtr k, IntPtr v)
        {
            var p = Of(id); if (p == null) return;
            var key = Utf8(k); if (!KeyRe.IsMatch(key)) return;
            var val = Utf8(v); if (val.Length > 600) val = val.Substring(0, 600);     // 排位的 rkrules / rknote 一行能到两三百字
            lock (p.Replies) p.Replies[key] = val;
        }
        static IntPtr OnName(int id) { var p = Of(id); return p == null ? IntPtr.Zero : p.NamePtr; }
        // API 2："域名或IP[:端口]" → "ip:port"（插件自己不做 DNS，和基底的 LobbyServer.Resolve 同一套规则、同一个默认端口）
        static int OnResolve(int id, IntPtr addr, IntPtr outp, int cap)
        {
            var p = Of(id); if (p == null || outp == IntPtr.Zero || cap < 2) return -1;
            try
            {
                var ep = LobbyServer.Resolve4(Utf8(addr)); if (ep == null) return 0;     // 插件只认 ip:port，照旧只给 IPv4
                var b = Encoding.ASCII.GetBytes(ep.Address + ":" + ep.Port);
                var m = Math.Min(b.Length, cap - 1); Marshal.Copy(b, 0, outp, m); Marshal.WriteByte(outp, m, 0);
                return m;
            }
            catch (Exception) { return -1; }
        }

        // ---------------------------------------------------------------- 加载
        static Dictionary<string, string> Manifest(string path)
        {
            var d = new Dictionary<string, string>();
            try { foreach (var l in File.ReadAllLines(path, Encoding.UTF8)) { var i = l.IndexOf('='); if (i > 0) d[l.Substring(0, i).Trim()] = l.Substring(i + 1).Trim(); } }
            catch (Exception) { }
            return d;
        }
        // netDir = <mod>\net；扩展包在 <mod>\ext\<名>\，玩法 mod 在 <mod>\mods\<名>\（：mod 也能带联机程序插件，
        // 比如排位 mods\ranked\lobby_ranked.dll；同一套 C ABI、同一个同意检查）。只装 manifest 里有 lobby= 的（或目录里有 lobby_<名>.dll 的）。
        // 两处同名时先到先得（ext 在前；Lua 侧 fornet_mods.lua 不让 mod 和插件同名）。mod 的依赖是否满足只有 Lua 侧知道，这里只看 on.txt
        public static void Load(string netDir)
        {
            string root;
            try { root = Path.GetDirectoryName(Path.GetFullPath(netDir)); } catch (Exception) { return; }
            var dirs = new List<string>();
            foreach (var sub in new[] { "ext", "mods" })
            {
                var top = Path.Combine(root, sub);
                if (!Directory.Exists(top)) continue;
                string[] ds;
                try { ds = Directory.GetDirectories(top); } catch (Exception) { continue; }
                Array.Sort(ds, StringComparer.OrdinalIgnoreCase);
                dirs.AddRange(ds);
            }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (!NameRe.IsMatch(name)) continue;
                if (list.Exists(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) { NetLog.W("[ext:" + name + "] ext\\ 和 mods\\ 同名，只装先到的那个"); continue; }
                if (!File.Exists(Path.Combine(d, "on.txt"))) continue;          // 11-13：默认停用，有 on.txt 才装（面板插件页"启用"写它，重启生效）
                var mf = Manifest(Path.Combine(d, "manifest.txt"));
                // 11-13（的 C# 侧）：manifest 要的接口比这个联机程序新就不装（Lua 侧 fornet_ext.lua 同样判；两边的号由 test_ext 卡成一样）
                string av; int an;
                if (mf.TryGetValue("api", out av) && int.TryParse(av.Trim(), out an) && an > ApiVersion)
                {
                    NetLog.W("[ext:" + name + "] 需要插件接口 API " + an + "（这个联机程序是 " + ApiVersion + "），不装");
                    continue;
                }
                string dll; mf.TryGetValue("lobby", out dll);
                if (string.IsNullOrEmpty(dll)) dll = "lobby_" + name + ".dll";
                if (dll.IndexOfAny(new[] { '/', '\\' }) >= 0) continue;
                var path = Path.Combine(d, dll);
                if (!File.Exists(path)) continue;                                  // 只有 Lua 的包：联机程序不管
                var p = new Plugin { Name = name, Dir = d, Dll = dll };
                string cs; if (mf.TryGetValue("consent", out cs)) p.Consents = Array.FindAll(cs.Split(','), x => KeyRe.IsMatch(x.Trim().TrimStart('@')));
                for (int i = 0; i < p.Consents.Length; i++) p.Consents[i] = p.Consents[i].Trim();
                list.Add(p);
                var id = list.Count - 1;
                NetLog.W(Tag(p) + "载入 " + dll + "（同意键 " + string.Join(",", p.Consents) + "）…");
                try
                {
                    p.Handle = LoadLibraryW(path);
                    if (p.Handle == IntPtr.Zero) { p.State = "bad"; p.Why = "LoadLibrary " + Marshal.GetLastWin32Error(); NetLog.W(Tag(p) + p.Why); continue; }
                    // 导出名前缀：游戏自己的（WireTag.Abi）优先，再认 4net 的中性名 fnx_（照 4net 文档写的插件在改过名字的游戏里也能用）
                    string abi = WireTag.Abi;
                    if (GetProcAddress(p.Handle, abi + "init") == IntPtr.Zero && GetProcAddress(p.Handle, "fnx_init") != IntPtr.Zero) abi = "fnx_";
                    var fi = GetProcAddress(p.Handle, abi + "init"); var fc = GetProcAddress(p.Handle, abi + "cmd");
                    var ft = GetProcAddress(p.Handle, abi + "tick"); var fs = GetProcAddress(p.Handle, abi + "shutdown");
                    if (fi == IntPtr.Zero || fc == IntPtr.Zero) { p.State = "bad"; p.Why = "缺 " + abi + "init / " + abi + "cmd"; NetLog.W(Tag(p) + p.Why); continue; }
                    p.Init = Marshal.GetDelegateForFunctionPointer<InitFn>(fi);
                    p.Cmd = Marshal.GetDelegateForFunctionPointer<CmdFn>(fc);
                    if (ft != IntPtr.Zero) p.Tick = Marshal.GetDelegateForFunctionPointer<VoidFn>(ft);
                    if (fs != IntPtr.Zero) p.Shutdown = Marshal.GetDelegateForFunctionPointer<VoidFn>(fs);
                    var nb = Encoding.UTF8.GetBytes(name + "\0");
                    p.NamePtr = Marshal.AllocHGlobal(nb.Length); Marshal.Copy(nb, 0, p.NamePtr, nb.Length);
                    p.C = new Ctx
                    {
                        version = ApiVersion, id = id,
                        log = Marshal.GetFunctionPointerForDelegate<LogCb>(cbLog), consent = Marshal.GetFunctionPointerForDelegate<ConsentCb>(cbConsent),
                        send = Marshal.GetFunctionPointerForDelegate<SendCb>(cbSend), recv = Marshal.GetFunctionPointerForDelegate<RecvCb>(cbRecv),
                        reply = Marshal.GetFunctionPointerForDelegate<ReplyCb>(cbReply), name = Marshal.GetFunctionPointerForDelegate<NameCb>(cbName),
                        resolve = Marshal.GetFunctionPointerForDelegate<ResolveCb>(cbResolve),
                    };
                    var rc = p.Init(ref p.C);
                    if (rc != 0) { p.State = "bad"; p.Why = abi + "init 返回 " + rc; NetLog.W(Tag(p) + p.Why); continue; }
                    p.State = "on";
                    NetLog.W(Tag(p) + "就绪");
                }
                catch (Exception ex) { p.State = "bad"; p.Why = ex.Message; NetLog.W(Tag(p) + "载入出错：" + ex.Message); }
            }
        }

        // ---------------------------------------------------------------- 基底 → 插件
        public static bool Cmd(string plugin, Dictionary<string, string> d)
        {
            foreach (var p in list)
            {
                if (p.Name != plugin || p.State != "on") continue;
                var sb = new StringBuilder();
                foreach (var kv in d) if (kv.Key != "id" && kv.Key != "cmd" && kv.Key != "plugin" && KeyRe.IsMatch(kv.Key)) sb.Append(kv.Key).Append('=').Append((kv.Value ?? "").Replace("\r", " ").Replace("\n", " ")).Append('\n');
                var b = Encoding.UTF8.GetBytes(sb.ToString() + "\0");
                var mem = Marshal.AllocHGlobal(b.Length);
                try { Marshal.Copy(b, 0, mem, b.Length); p.Cmd(mem); }
                catch (Exception ex) { NetLog.W(Tag(p) + WireTag.Abi + "cmd 出错：" + ex.Message); }
                finally { Marshal.FreeHGlobal(mem); }
                return true;
            }
            return false;
        }
        public static void Tick()
        {
            foreach (var p in list) if (p.State == "on" && p.Tick != null) { try { p.Tick(); } catch (Exception ex) { NetLog.W(Tag(p) + WireTag.Abi + "tick 出错：" + ex.Message); } }
        }
        public static void ShutdownAll()
        {
            foreach (var p in list) if (p.State == "on" && p.Shutdown != null) { try { p.Shutdown(); } catch (Exception) { } }
        }
        // 基底自己用：某个包装上了没；读它的回话（含 _ 开头的私有键）
        public static bool Has(string name) { foreach (var p in list) if (p.Name == name && p.State == "on") return true; return false; }
        public static bool Reply(string plugin, string key, out string v)
        {
            v = "";
            foreach (var p in list) if (p.Name == plugin) { lock (p.Replies) return p.Replies.TryGetValue(key, out v); }
            return false;
        }
        // lobby.txt：extp=<就绪的包名,…>（Lua 据此把 lobby.cmd 打开）、x.<名>.<k>=<v>
        public static void Status(Action<string, string> kv)
        {
            if (list.Count == 0) return;
            var names = new List<string>();
            foreach (var p in list)
            {
                if (p.State == "on") names.Add(p.Name); else kv("x." + p.Name + ".err", p.Why);
                lock (p.Replies) foreach (var r in p.Replies) if (!r.Key.StartsWith("_")) kv("x." + p.Name + "." + r.Key, r.Value);     // _ 开头的是给基底自己的（Ext.Reply），不进 lobby.txt
            }
            kv("extp", string.Join(",", names.ToArray()));
        }
    }
}
