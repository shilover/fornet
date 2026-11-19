// 联机窗口（替代 launcher.py：不依赖 Python，用 Windows 自带的 .NET Framework 4 编译成单个 exe）。
// 编译：build.bat（用系统自带的 csc.exe，C# 5 语法：不能用 $"" 插值、?. 这类新语法）。
//
// 游戏里的 Lua 只读写 <exe 所在目录>\net\ 下的文本文件：
// state_local.txt 游戏写，本程序发给对方（起游戏连得上命名管道时走管道，见 GamePipe.cs；这两个文件是兜底）
// state_remote.txt 本程序收到对方的包后写，游戏读（同上）
// pipe.txt 本程序写：管道名（name=），游戏据此连管道
// session.txt 本程序写：role=host|client|none、connected、peer；游戏据此决定自己是主机还是客机
// lang.txt 本程序写：lang=zh|en（窗口右上角切换），游戏据此切换屏幕提示的语言
// lobby.txt 本程序写（每 0.5 秒）：给游戏内联机面板看的状态和可加入目标（net-ui，docs/NETWORK_UI.md）
// cmd.txt 游戏内联机面板写：host / join / stop / mode / show，本程序按 id 执行一次
// 房间发现：主机每秒往 UDP 27016 广播 "4NROOM|名字|端口|地图|是否已满|能观战"，加入方监听同一端口列出来。
// 第 6 段 起有（"1" = 这个主机认观战连接）；旧版主机只有 5 段，满房照旧不能点
// 连接方式（net-ui）：局域网（含 Tailscale / ZeroTier，见 NetExtras.cs）、
// 服务器大厅（自建服务端登记房间 + 打洞，服务器开了中转时不通走中转，含输入 IP 直连，ServerLobby.cs）、本地 AI 对战（接口 LocalAiApi.cs，AI 本身由游戏适配编进来）。
// 开发版另有平台好友（Bridge.cs）；平台大厅 v0.34 起移除；发布版不带（build.bat release）。
//
// 命令行（自动化测试用）：FornetLobby.exe --host | --join IP[:端口] [--seconds N] [--dir 目录] [--log 文件] [--no-backup]
// --bg 后台运行（不显示窗口；游戏启动时 / 游戏内联机面板用它启动；游戏退出 3 秒后自己退出，给了 --seconds 时不自动退）
// --steam-probe 只登录桥接平台、列出在玩这个游戏的好友，然后退出
// --sim-no-direct 服务器大厅：丢掉所有不是服务器发来的包（模拟打洞不通，测"直连 3 次失败改走中转"）
// --secret 密码 / --port 端口 命令行模式的联机密码（窗口模式用 netcfg.txt 的 secret=）/ 主机端口（给了就不广播）
// --sim-loss 0.3 / --sim-reorder / --sim-skew 秒 v0.22.0 测试：发出的包随机丢 / 30% 延后 20–200ms（乱序）/ 签名时间戳偏移
// 可靠事件：游戏写 net\rel_out.txt、读 net\rel_in.txt（Reliable.cs 文件头）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;   // AOT 版（fornet_lobby.dll）里是 Headless.cs 的无界面替身

namespace ForNet
{
    // 只给离线测试用：环境变量 FORNET_LOOPBACK=1 或命令行 --loopback 时所有 UDP 都只绑 127.0.0.1，
    // 防火墙就不弹"已经阻止此应用的部分功能"；局域网广播也改成发给 127.0.0.1。游戏里的 dll / 玩家开的 exe 不设，照旧绑所有网卡。
    static class NetBind
    {
        public static bool Loopback = WireTag.Env("LOOPBACK") == "1" || WireTag.Env("LOOPBACK") == "6";
        public static IPAddress Addr { get { return Loopback ? IPAddress.Loopback : IPAddress.Any; } }

        // 单播只送到同一地址上的一个 socket，所以回环下每个联机程序的局域网发现各占一个地址 127.0.0.1~8（端口都是 27016），发房间时挨个发
        public static List<IPAddress> DiscoveryAddrs()
        {
            var list = new List<IPAddress>();
            for (int k = 1; k <= 8; k++) list.Add(new IPAddress(new byte[] { 127, 0, 0, (byte)k }));
            return list;
        }

        public static UdpClient DiscoverySocket()
        {
            foreach (var a in DiscoveryAddrs())
                try { return new UdpClient(new IPEndPoint(a, Session.BroadcastPort)); } catch (SocketException) { }
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }

        // IPv6 直连。V6 = false（netcfg.txt 写 ipv6=0、命令行 --no-ipv6、环境变量 FORNET_NO_IPV6=1）时和以前一字不差：只开 IPv4。
        public static volatile bool V6 = WireTag.Env("NO_IPV6") != "1";
        // 回环测试下的 IPv6：FORNET_LOOPBACK=6 或 --loopback6 时主机绑 ::1（只收 IPv6，测 "[::1]:端口" 加入用）
        public static bool Loopback6 = WireTag.Env("LOOPBACK") == "6";

        // 对局用的 UDP（Link / ServerLink）：一个双栈 socket 绑 [::]:端口，同一个端口同时收 IPv4 和 IPv6，
        // 发给 IPv4 地址系统自己处理。收到的 IPv4 包地址是 ::ffff:a.b.c.d，收包处一律经 Norm 还原，后面的比较照旧。
        // 系统没装 IPv6 / 开不了双栈时退回只开 IPv4；端口被占照旧抛 SocketException（面板报"开不了端口"）。
        // peer = 客机要连的地址（主机传 null）
        public static UdpClient SessionUdp(int port, IPEndPoint peer)
        {
            bool peerV6 = peer != null && peer.AddressFamily == AddressFamily.InterNetworkV6;
            if (Loopback)
            {
                if (peerV6 || (peer == null && Loopback6)) return new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, port));
                return new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            }
            if (!V6 && !peerV6) return new UdpClient(new IPEndPoint(IPAddress.Any, port));
            UdpClient u = null;
            try
            {
                u = new UdpClient(AddressFamily.InterNetworkV6);
                u.Client.DualMode = true;
            }
            catch (Exception)
            {
                // 这台机器开不了 IPv6 socket：还是 IPv4（客机要连 IPv6 就只能报错了）
                if (u != null) try { u.Close(); } catch (Exception) { }
                if (peerV6) throw;
                return new UdpClient(new IPEndPoint(IPAddress.Any, port));
            }
            try { u.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port)); }
            catch (Exception) { try { u.Close(); } catch (Exception) { } throw; }
            return u;
        }

        // 和大厅服务器说话的小 socket（房间列表 / 数值表 / 观战 / 桥接房登记）：本机端口随便，IPv4 / IPv6 都能发（双栈）。
        // 回环测试下按要连的服务器的地址族绑 127.0.0.1 或 ::1；IPv6 关了（V6 = false）且服务器是 IPv4 时和以前一样只开 IPv4
        public static UdpClient QueryUdp(IPEndPoint server)
        {
            bool v6 = server != null && server.AddressFamily == AddressFamily.InterNetworkV6;
            if (Loopback) return new UdpClient(new IPEndPoint(v6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0));
            if (!V6 && !v6) return new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            UdpClient u = null;
            try
            {
                u = new UdpClient(AddressFamily.InterNetworkV6);
                u.Client.DualMode = true;
                u.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                return u;
            }
            catch (Exception)
            {
                if (u != null) try { u.Close(); } catch (Exception) { }
                if (v6) throw;
                return new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            }
        }

        // 这个 socket 能不能往 ep 发（IPv4 socket 只能发 IPv4；IPv6 socket 开了双栈两种都能发；回环的只能发同一族）
        public static bool CanSend(UdpClient u, IPEndPoint ep)
        {
            if (u == null || ep == null) return false;
            try
            {
                var s = u.Client;
                if (s.AddressFamily == AddressFamily.InterNetwork) return ep.AddressFamily == AddressFamily.InterNetwork;
                if (ep.AddressFamily == AddressFamily.InterNetworkV6) return true;
                if (!s.DualMode) return false;
                var lp = s.LocalEndPoint as IPEndPoint;
                return lp == null || !IPAddress.IPv6Loopback.Equals(lp.Address);
            }
            catch (Exception) { return false; }
        }

        // :ffff:1.2.3.4 → 1.2.3.4（双栈 socket 收到的 IPv4 包）；别的原样
        public static IPAddress Norm(IPAddress a) { return a != null && a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a; }
        public static IPEndPoint Norm(IPEndPoint ep)
        {
            return ep != null && ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;
        }
    }

    // 后台线程的入口一律经过这里：没接住的异常只记日志，不往外抛。
    // exe 里出事只是联机程序退出；联机程序编成 DLL 在游戏进程里跑时（fornet_lobby.dll），没接住的异常会直接结束游戏。
    static class Safe
    {
#if AOT
        public static Action<string, Exception> OnError = (w, ex) => { try { Console.Error.WriteLine("[" + w + "] " + ex); } catch (Exception) { } };
#else
        public static Action<string, Exception> OnError = delegate { };
#endif
        public static void Run(string where, Action a)
        {
            try { a(); }
            catch (ThreadAbortException) { }
            catch (Exception ex) { try { OnError(where, ex); } catch (Exception) { } }
        }
        public static Thread Thread(string name, Action a) { return new Thread(() => Run(name, a)) { IsBackground = true, Name = name }; }
        public static void Pool(string where, Action a) { ThreadPool.QueueUserWorkItem(delegate { Run(where, a); }); }
    }

    static class Kv
    {
        public static readonly Encoding Utf8 = new UTF8Encoding(false);   // 不能带 BOM，否则 Lua 读到的第一个键是乱的

        // 状态包里的昵称（nm=，Session 发包时加上）；没有就 null
        public static string NickOf(string packet)
        {
            string v;
            return Parse(packet).TryGetValue("nm", out v) && v.Trim().Length > 0 ? v.Trim() : null;
        }

        public static Dictionary<string, string> Parse(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            return d;
        }

        public static string ReadText(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Utf8))
                    return sr.ReadToEnd();
            }
            catch (Exception) { return null; }
        }

        public static Dictionary<string, string> Read(string path)
        {
            var t = ReadText(path);
            if (t == null) return null;
            var d = Parse(t);
            return d.Count > 0 ? d : null;
        }

        public static void WriteAtomic(string path, string text)
        {
            var tmp = path + ".tmp";
            try { File.WriteAllText(tmp, text, Utf8); }
            catch (Exception) { return; }
            // 游戏侧同一时刻可能正开着这个文件，Windows 会拒绝替换，重试几次
            for (int k = 0; k < 6; k++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (Exception) { Thread.Sleep(1); }
            }
            try { File.WriteAllText(path, text, Utf8); } catch (Exception) { }
            try { File.Delete(tmp); } catch (Exception) { }
        }

        public static string F(double v, string fmt) { return v.ToString(fmt, CultureInfo.InvariantCulture); }
    }

    // 收到的状态包排队写 state_remote.txt，Session.Run 每轮（~4ms）写一个，
    // 一批里一起到的几个包不再只留最新一个。积压超过 Max 个就丢最旧的（状态包是"最新覆盖"，不为补帧攒延迟）
    class RemoteOut
    {
        const int Max = 3;
        public volatile string Target;   // 等人的房间转正时从 net\wait\ 换到 net\（真人离开再换回去）
        readonly Queue<string> q = new Queue<string>();
        volatile GamePipe pipe;     // 游戏连着管道就交给管道，不写文件； 跟着 Retarget 换（只有 net\ 有管道）

        public RemoteOut(string path) { Retarget(path); }

        public void Retarget(string path) { Target = path; pipe = GamePipe.For(Path.GetDirectoryName(path)); }

        public void Push(string s)
        {
            if (pipe != null && pipe.PushRemote(s)) return;
            q.Enqueue(s);
            while (q.Count > Max) q.Dequeue();
        }

        public void Flush()
        {
            if (q.Count > 0) Kv.WriteAtomic(Target, q.Dequeue());
        }
    }

    // Windows 默认计时精度 15.6ms：Thread.Sleep(4) 实际睡 15.6ms。整个进程开 1ms 精度，退出时还原
    static class WinTimer
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
        static bool on;
        public static void Begin() { try { on = timeBeginPeriod(1) == 0; } catch (Exception) { } }
        public static void End() { if (on) { on = false; try { timeEndPeriod(1); } catch (Exception) { } } }
    }

    // 一个 UDP 端点：收包只要比上一个新的序号，排队写进 state_remote.txt（RemoteOut）
    class Link : ITransport, ISpecSink, IQueueHost, IRelayHost, IPeerTableHost, IMpLink
    {
        public readonly UdpClient Udp;
        readonly PeerTable peers = new PeerTable();
        public PeerTable Peers { get { return peers; } }
        public IPEndPoint Peer { get { return peers.GetPeer<IPEndPoint>(); } set { peers.SetPeer(value, PeerRoute.Direct); } }
        bool lockFirst;
        bool v6AliasLogged;     // "主机回包换了同网段 IPv6 地址"只记一次
        readonly RemoteOut outq;
        public long Tx, Rx, Stale, Gap;
        public double DelaySum, DelayMax;
        public long DelayN;
        public Action<string> Log = delegate { };
        public bool Diag;      // 写 net\net_conn.log（局域网 / 输入 IP 直连；AI 对战的回环不写）
        // 观战（第 2 步）。主机：握手带 spec=1 的地址记成观战者（不占对战位、不锁定），Session 把两路状态包转给它们；
        // OnPeerState = 收到对手的一个状态包（Session 加 src=c 转发）。观战者：SpecMode = 收主机转来的两路包（src=h / src=c），
        // 两路各有自己的序号，按来源分别去重
        public const int MaxSpec = 4;
        public const double SpecLost = 6.0;
        public Action<string> OnPeerState { get; set; }
        public Action<string> OnRole { get; set; }   // 观战者 / 对手：主机发来 4NROLE（轮到你上场 / 你下场观战）
        public Action<string, bool> OnRoleEx { get; set; }
        public Action<PeerRow, string> OnDeliverPeerEvent;
        public volatile int QPos, QLen;       // 观战者：主机发来的 4NQ（排第几、一共几个）
        int ISpecSink.QPos { get { return QPos; } }
        int ISpecSink.QLen { get { return QLen; } }
        List<object> IQueueHost.QueueOrder() { return peers.QueueOrderObj(); }
        public bool CanPromote { get { return peers.CanPromote; } }
        bool IQueueHost.CanPromote { get { return CanPromote; } }
        object IQueueHost.PromoteHead() { return PromoteHead(); }
        object IQueueHost.DemotePeer() { return DemotePeer(); }
        void IQueueHost.SendTo(object who, byte[] b) { SendTo(who as IPEndPoint, b); }
        bool IQueueHost.HasPeer { get { return peers.HasPeer; } }
        bool specMode;
        public bool SpecMode { get { return specMode; } set { specMode = value; peers.ClearSrcSeq(); } }
        public long SpecTx, SrcH, SrcC, SpecRefused;

        internal static bool IsSpecHello(string s)
        {
            return s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal) && s.Contains("\nspec=1");
        }

        // ---- 多人混战（②a）。主机：握手带 mp=1 的地址记成玩家，编号 2..MpCfg.Max（主机自己是 1），
        // 收到的状态包加 src=<编号> 交给自己的游戏、并经 OnMpState 让 Session 转给其他玩家；每 2 秒给每位玩家发 4NMP（你的编号 / 人数）。
        // 客机：MpMode = 收主机发来的多路包（src=<编号>，按来源分别去重），4NMP 里拿到自己的编号
        public Action<int, string> OnMpState { get; set; }
        public volatile bool MpMode;             // 客机：按多人加入
        public volatile bool InMatch;            // 主机：正在多人对局中（倒计时 / 交手），拒绝新人加入
        public volatile string DeniedMsg;
        internal static string ParseDeniedMsg(string s)
        {
            if (s != null && s.IndexOf("inmatch", StringComparison.Ordinal) >= 0) return L.T("对局进行中，这局打完再进", "Match in progress, join after it ends");
            if (s != null && s.IndexOf("|full", StringComparison.Ordinal) >= 0) return L.T("房间已满", "Room is full");
            return L.T("房间暂不可加入", "Room cannot be joined right now");
        }
        public volatile int MyPid;               // 客机：主机给的编号（0 = 还没收到）
        public volatile int MpSeen;              // 客机：主机说房间里现在几个人
        public volatile string MpRoster = "";    // 客机：主机发来的名单（编号:昵称,…，不含主机自己）
        public long MpRx, MpTx, MpRefused;

        // ---- M3：多人混战全互连（lobby/MeshMp.cs）。房主介绍每一对玩家；状态包直连 / 经第三人中转，
        // 游戏收到的仍然是带 src=<编号> 的包。旧版联机程序没有这一层，照旧走 MpTx 星形转发。
        MpMesh mesh;
        public MpMesh Mesh { get { return mesh; } }
        public bool MpJoinMode { get { return MpMode; } }
        int IMpLink.MyPid { get { return mesh != null && mesh.MyPid > 0 ? mesh.MyPid : MyPid; } }
        int IMpLink.MpSeen { get { return mesh != null && mesh.MpSeen > 0 ? mesh.MpSeen : MpSeen; } }
        string IMpLink.MpRoster { get { return mesh != null && !string.IsNullOrEmpty(mesh.MpRoster) ? mesh.MpRoster : MpRoster; } }
        public string LocalAddrForHost(int hostPid)
        {
            if (mesh != null && hostPid == mesh.MyPid) return LocalAddrFor(null);
            if (mesh != null)
            {
                var p = mesh.Get(hostPid);
                if (p != null)
                {
                    if (p.Handle != null) return p.Handle.ToString();
                    return !string.IsNullOrEmpty(p.Addr) ? p.Addr : p.LanAddr;
                }
            }
            return PeerStr;
        }
        public string MpStatsLines()
        {
            return mesh != null ? "\nmpmesh=" + mesh.StatsLine() + "\nmptx=" + mesh.MpTx + "\nmprx=" + mesh.MpRx + "\nmpdual=" + mesh.MpDual
                + "\nmpfwd=" + mesh.MpFwd + "\nmpdup=" + mesh.MpDup + "\nmpdz=" + mesh.DzStat + "\nmpprobe=" + mesh.ProbeStats() : "";     // mpdz=<在省流 1/0>:<实发字节>/<原样字节>:<拼回包数>:<拼不回丢掉>

        }
        int meshGot;

        // ---- M4：局域网 / 输入 IP 的 1v1 解说模式直连（M2 的桥接平台那条路的 UDP 版）。
        // 房主给两位选手发 4N 介绍（对方地址 + 一次性口令），两人同时互发 4NHELLO 打洞；
        // 状态包照旧发给房主（观战 / 兜底），同时给对面直连发一份，seq 相同，接收端按 seq 只收一份。
        IPEndPoint specMeshPeer;
        string specMeshToken = "";
        bool specMeshMatchHost;
        volatile bool specMeshUp;
        DateTime specMeshHelloAt = DateTime.MinValue;
        DateTime specMeshRxAt = DateTime.MinValue;
        SpecDelta specMeshDz;
        string specMeshDzKey = "";
        public long SpecMeshRx, SpecMeshTx;
        public bool SpecMeshPresent { get { return specMeshPeer != null && specMeshToken.Length > 0; } }
        public bool SpecMeshUp { get { return specMeshUp; } }
        public void MeshIntro(IPEndPoint ep, string token, bool matchhost)
        {
            if (ep == null) return;
            bool changed = specMeshPeer == null || !specMeshPeer.Equals(ep) || specMeshToken != (token ?? "");
            specMeshPeer = ep;
            specMeshToken = token ?? "";
            specMeshMatchHost = matchhost;
            specMeshDzKey = "m" + (matchhost ? "1" : "2") + "x" + LocalPort;
            if (changed)
            {
                specMeshUp = false;
                specMeshHelloAt = DateTime.MinValue;
                specMeshRxAt = DateTime.MinValue;
                specMeshDz = new SpecDelta();
                Log(L.T("解说直连：介绍对端 ", "Direct: introduced peer ") + ep + (matchhost ? L.T("（我是对局主机）", " (I am the match host)") : ""));
            }
        }
        public void MeshTick(DateTime now)
        {
            if (specMeshPeer == null || specMeshToken.Length == 0) return;
            if ((now - specMeshHelloAt).TotalSeconds >= (specMeshUp ? 1.0 : 0.5))
            {
                bool first = specMeshHelloAt == DateTime.MinValue;
                specMeshHelloAt = now;
                try
                {
                    var b = wire.Seal(WireTag.P + "MESHHELLO\ntoken=" + specMeshToken + "\nmh=" + (specMeshMatchHost ? "1" : "0") + "\n");
                    Udp.Send(b, b.Length, specMeshPeer);
                }
                catch (Exception) { }
                if (first) Log(L.T("解说直连：向 ", "Direct: sending hello to ") + specMeshPeer + L.T(" 发握手", ""));
            }
            if (specMeshUp && (now - specMeshRxAt).TotalSeconds > 2.5)
            {
                specMeshUp = false;
                Log(L.T("解说直连：2.5 秒没收到对端，先走房主中转（后台继续试直连）", "Direct: no packets for 2.5 s, using host relay (still retrying)"));
            }
        }
        // 直连收到的包：握手 / 状态包。状态包和房主中转来的共用 seq 去重，谁先到用谁。
        public bool MeshPacket(IPEndPoint from, string s)
        {
            if (specMeshPeer == null || !from.Equals(specMeshPeer)) return false;
            if (s.StartsWith(WireTag.P + "MESHHELLO", StringComparison.Ordinal))
            {
                string tok = "";
                string v;
                if (Kv.Parse(s).TryGetValue("token", out v)) tok = v;
                if (specMeshToken.Length > 0 && tok == specMeshToken)
                {
                    bool was = specMeshUp;
                    specMeshUp = true;
                    specMeshRxAt = DateTime.UtcNow;
                    if (!was) Log(L.T("解说直连：打通 ", "Direct link up ") + from);
                }
                return true;
            }
            if (s.StartsWith(WireTag.P, StringComparison.Ordinal)) return true;      // 别的控制包不走这条边
            if (s.IndexOf("seq=", StringComparison.Ordinal) < 0) return true;
            string sv;
            int seq, gap;
            var d = Kv.Parse(s);
            if (!d.TryGetValue("seq", out sv) || !int.TryParse(sv, out seq)) return true;
            specMeshUp = true;
            specMeshRxAt = DateTime.UtcNow;
            if (!peers.TakePeerSeq(seq, out gap)) return true;     // 房主中转的那份已经先到了
            Gap += gap;
            SpecMeshRx++;
            wire.DzSeenText(s);
            if (!NoOut) outq.Push(s);
            return true;
        }
        // 给直连对面发一份本机状态（独立省流编码；seq 原样保留，和经房主的那份可以互相去重）
        public void MeshSendState(string body)
        {
            if (!specMeshUp || specMeshPeer == null || specMeshToken.Length == 0 || body == null) return;
            string full = body + "dzv=1\ndzk=" + specMeshDzKey + "\n";
            if (specMeshDz == null) specMeshDz = new SpecDelta();
            var enc = specMeshDz.Encode(full, 1, DateTime.UtcNow);
            if (enc.IndexOf("dz=1\n", StringComparison.Ordinal) >= 0) enc += "dzk=" + specMeshDzKey + "\n";
            try
            {
                var b = wire.Seal(enc);
                Udp.Send(b, b.Length, specMeshPeer);
                SpecMeshTx++;
            }
            catch (Exception) { }
        }
        // 房主：把两位选手互相介绍给对方（addr = 房主看到的对方地址；token = 这一对的一次性口令）
        public void SendMeshIntro(IPEndPoint to, IPEndPoint peer, string token, bool matchhost)
        {
            if (to == null || peer == null) return;
            try
            {
                var b = wire.Seal(WireTag.P + "MESH\npeer=" + peer + "\nmatchhost=" + (matchhost ? "1" : "0") + "\ntoken=" + token + "\n");
                Udp.Send(b, b.Length, to);
            }
            catch (Exception) { }
        }
        public void EnableMpMesh(bool host)
        {
            if (mesh != null) return;
            mesh = new MpMesh();
            mesh.IsHost = host;
            mesh.MyPid = host ? 1 : 0;
            mesh.MpMax = MpCfg.Max;
            mesh.Seal = b => wire.Seal(b);
            mesh.Verify = b => wire.Verify(b, 0, b.Length, null);
            mesh.SendRaw = delegate(object h, byte[] b)
            {
                var ep = h as IPEndPoint;
                if (ep != null) { try { Udp.Send(b, b.Length, ep); } catch (Exception) { } }
            };
            mesh.Deliver = delegate(string tagged) { outq.Push(tagged); };
            mesh.EnsureHandle = delegate(int pid, string addr, ulong sid)
            {
                if (string.IsNullOrEmpty(addr)) return null;
                return Addr.ParseEp(addr);     // "ip:端口" 和 "[v6]:端口" 都认
            };
            mesh.Dedupe = delegate(string src, int seq)
            {
                int g;
                return peers.TakeSrcSeq(src, seq, out g);
            };
            mesh.Log = m => Log(m);
            mesh.Enabled = true;
            mesh.OnRosterUpdated = delegate(int pid, int seen, string roster)
            {
                MyPid = pid;
                MpSeen = seen;
                MpRoster = roster;
            };
            if (!host) { MpMode = true; mesh.AddIntro(1, PeerStr, 0UL, "", Peer); mesh.HostAddr = PeerStr; }
        }

        // 房主告诉玩家"到我这台机器走哪个地址"：用临时 socket 问路由，不真发包。回环测试固定 127.0.0.1
        string LocalAddrFor(IPEndPoint peer)
        {
            int port = LocalPort;
            bool v6 = peer != null && peer.AddressFamily == AddressFamily.InterNetworkV6;     // 按对方的地址族问路由
            if (NetBind.Loopback) return new IPEndPoint(v6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, port).ToString();
            if (peer == null)
            {
                var a = NetBind.Addr;
                if (a.Equals(IPAddress.Any)) a = Addr.PrimaryLocal() ?? IPAddress.Loopback;
                else if (a.Equals(IPAddress.IPv6Any)) a = IPAddress.IPv6Loopback;
                return new IPEndPoint(a, port).ToString();
            }
            try
            {
                using (var s = new Socket(peer.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(peer);
                    var ep = (IPEndPoint)s.LocalEndPoint;
                    return new IPEndPoint(ep.Address, port).ToString();
                }
            }
            catch (Exception) { return new IPEndPoint(IPAddress.Loopback, port).ToString(); }
        }

        internal static bool IsMpHello(string s)
        {
            return s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal) && s.Contains("\nmp=1");
        }

        // 多人玩家握手里的 watch=1（观战加入）：房间满了也不为它挤掉 AI（AI 是它要看的对局的一部分）
        internal static bool HelloWatch(string s)
        {
            if (s == null || !s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal)) return false;
            string v;
            return Kv.Parse(s).TryGetValue("watch", out v) && v == "1";
        }

        // 多人玩家握手里的 hideip=1（开了"隐藏我的 IP"）
        internal static bool HelloHide(string s)
        {
            if (s == null || !s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal)) return false;
            string v;
            return Kv.Parse(s).TryGetValue("hideip", out v) && v == "1";
        }

        // M4：多人玩家握手里的局域网端点（lan=ip:端口）。没带 / 不是握手 = 空
        internal static string HelloNick(string s)
        {
            if (s == null || !s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal)) return "";
            string v;
            return Kv.Parse(s).TryGetValue("nm", out v) ? v : "";
        }
        // 多人玩家握手里自己报的公网 IPv6 端点
        internal static string HelloV6(string s)
        {
            if (s == null || !s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal)) return "";
            string v;
            return Kv.Parse(s).TryGetValue("v6", out v) ? v : "";
        }
        internal static string HelloLan(string s)
        {
            if (s == null || !s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal)) return "";
            string v;
            return Kv.Parse(s).TryGetValue("lan", out v) ? v : "";
        }

        // 主机：现在的玩家（编号, 昵称），顺手清掉 MpCfg.Lost 秒没动静的
        public List<KeyValuePair<int, string>> MpPlayers()
        {
            if (mesh != null)
            {
                return mesh.PruneAndGetPlayers(MpCfg.Lost);
            }
            var gone = new List<PeerRow>();
            var list = peers.MpPlayers(MpCfg.Lost, gone);
            for (int i = 0; i < gone.Count; i++)
            {
                Log(L.T("多人：玩家离开 ", "Multi: player left ") + gone[i].Pid + " " + gone[i].Handle);
                if (mesh != null) mesh.Remove(gone[i].Pid);
            }
            return list;
        }
        public int MpCount { get { return MpPlayers().Count; } }

        // 主机：发给每位玩家（except = 不发给这个编号，转发时不发回给本人）
        public void SendMp(byte[] b, int except)
        {
            var to = peers.MpHandlesExcept<IPEndPoint>(except);
            foreach (var ep in to) { MpTx++; try { Udp.Send(b, b.Length, ep); } catch (Exception) { } }
        }

        // 主机：每位玩家一条 4NMP（编号 / 人数 / 名单），Session 每 2 秒调
        public void SendMpInfo()
        {
            if (mesh != null)
            {
                mesh.BroadcastMpInfo();
                return;
            }
            var players = MpPlayers();
            var sb = new StringBuilder();
            foreach (var p in players) sb.Append(p.Key).Append(':').Append(p.Value.Replace(",", " ").Replace(":", " ")).Append(',');
            var roster = sb.ToString().TrimEnd(',');
            var to = peers.MpSnapshot<IPEndPoint>();
            foreach (var kv in to)
            {
                var b = wire.Seal(WireTag.P + "MP\npid=" + kv.Value + "\nn=" + (players.Count + 1) + "\nmax=" + MpCfg.Max + "\nl=" + roster + "\n");
                try { Udp.Send(b, b.Length, kv.Key); } catch (Exception) { }
            }
        }

        // 主机：这个包是多人玩家的（握手 / 状态包 / 别的控制包）就处理掉，返回 true
        bool NoteMp(IPEndPoint from, string s)
        {
            if (MpCfg.Max < 2) return false;
            bool hello = IsMpHello(s);
            PeerRow p;
            if (hello && mesh != null && mesh.DuelRefuse(from)) { MpRefused++; return true; }     // 对决模式观众已满：回 4NMPREFUSE 带原因
            // 对局中拒收新人：只在对局中、而且是加入握手时才查"是不是已经在房里"（每个包都查一遍是白干活）。
            // 对决模式（新版 1v1）不拦：观众打着也能进
            if (!MpCfg.MidJoin && InMatch && !MpCfg.DuelOn && (hello || (s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal) && !IsSpecHello(s)))
                && !((mesh != null && mesh.HasHandle(from)) || peers.NoteMp(from, false, MpCfg.Max, MpCfg.Limit, MpCfg.Lost, out p) == 1))
            {
                MpRefused++;
                var rej = wire.Seal(WireTag.P + "BUSY|inmatch\n");
                try { Udp.Send(rej, rej.Length, from); } catch (Exception) { }
                return true;
            }
            int existingPid = mesh != null ? mesh.PidByHandle(from) : 0;
            if (existingPid > 0)
            {
                if (hello) mesh.AddPlayer(existingPid, from, from.ToString(), 0UL, HelloNick(s), HelloLan(s), HelloHide(s), HelloV6(s));
                int g;
                if (mesh.HandlePacket(from, s, out g))
                {
                    if (g > 0) { Rx++; meshGot += g; MpRx++; }
                    return true;
                }
                return true;
            }
            if (hello && mesh != null && mesh.IsHost && !HelloWatch(s)) mesh.TryYieldAiForHuman(MpCfg.Max);     // 观战加入不挤 AI
            var extraUsed = mesh != null ? mesh.Pids() : null;
            if (extraUsed != null && mesh.MyPid > 0) extraUsed.Add(mesh.MyPid);     // 不再写死占住 1 号：换房主后走掉的 1 号可以补给新人（mesh.Pids 里还在的成员照样占着）
            int r = peers.NoteMp(from, hello, MpCfg.Max, MpCfg.Cap, MpCfg.Lost, out p, extraUsed, existingPid);
            if (r == 0)
            {
                // 多人房间不收 1v1 对手：没编号的人（满员时来的）发来的状态包 / 普通握手吞掉，别被锁成对手；观战握手照旧交给观战
                if (mesh != null && !IsSpecHello(s)) mesh.TellOldClient(from, s);     // 旧版 1v1 握手：回版本号，让它显示"版本不一致，建议更新"
                return !IsSpecHello(s) && (s.Contains("seq=") || s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal));
            }
            if (r < 0) { MpRefused++; if (hello && mesh != null) mesh.DuelRefuseFull(from); return true; }     // 对决模式满了明说；普通多人房满了不回绝：握手留着，有人走了就补上（M3，test_mp_lobby）
            if (r == 2)
            {
                Log(L.T("多人：玩家加入，编号 ", "Multi: player joined, id ") + p.Pid + " " + from);
                if (Diag) NetLog.W("多人：玩家加入 编号 " + p.Pid + " " + NetLog.Ip(from));
                if (mesh != null)
                {
                    mesh.HostAddr = LocalAddrFor(from);
                    mesh.HostAddrOf = h => { var hep = h as IPEndPoint; return hep != null ? LocalAddrFor(hep) : null; };     // 按玩家的地址族
                    mesh.AddPlayer(p.Pid, from, from.ToString(), p.BridgeId, p.Nick, HelloLan(s), HelloHide(s), HelloV6(s));
                    mesh.MyV6Addr = MpMesh.V6Ep(LocalPort);     // 房主介绍自己时带上
                    mesh.SendIntros(DateTime.UtcNow, true);
                }
                SendMpInfo();
            }
            else if (mesh != null) mesh.AddPlayer(p.Pid, from, from.ToString(), p.BridgeId, p.Nick, HelloLan(s), HelloHide(s), HelloV6(s));
            if (mesh != null)
            {
                int g;
                if (mesh.HandlePacket(from, s, out g))
                {
                    if (g > 0) { Rx++; meshGot += g; MpRx++; }
                    return true;
                }
            }
            if (s.StartsWith(WireTag.P, StringComparison.Ordinal) || s.Contains("hello=1")) return true;    // 握手 / 可靠事件（多人先不走可靠通道）
            if (mesh != null) return true;
            var d = Kv.Parse(s);
            string q;
            int seq;
            if (!d.TryGetValue("seq", out q) || !int.TryParse(q, out seq)) return true;
            string nm;
            d.TryGetValue("nm", out nm);
            if (!peers.TakeMpState(p, seq, nm)) { Stale++; return true; }
            MpRx++;
            var sb = new StringBuilder(s.Length + 8);
            foreach (var raw in s.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("src=")) continue;
                sb.Append(line).Append('\n');
            }
            sb.Append("src=").Append(p.Pid).Append('\n');
            var tagged = sb.ToString();
            outq.Push(tagged);                 // 主机自己的游戏
            var cb = OnMpState;
            if (cb != null) cb(p.Pid, tagged);   // 转给其他玩家
            return true;
        }

        public int SpecCount
        {
            get
            {
                var gone = new List<PeerRow>();
                int n = peers.PruneSpecs(SpecLost, gone);
                for (int i = 0; i < gone.Count; i++) Log(L.T("观战者离开：", "Spectator left: ") + gone[i].Handle);
                return n;
            }
        }

        // 观战者的握手 / 保活：记下（满了不收）。返回 true = 这个包是观战者的，不再往下处理
        // 观战者会不会拼省流包（握手带 dzv=1）
        public bool SpecDzAll { get { return peers.SpecDzAll; } }

        bool NoteSpec(IPEndPoint from, string s)
        {
            int r = peers.NoteSpec(from, s, SpecCfg.Max, 0UL);
            if (r == 0) return false;
            if (r < 0) { SpecRefused++; return true; }
            if (r == 2)
            {
                Log(L.T("观战者加入：", "Spectator joined: ") + from);
                if (Diag) NetLog.W("观战者加入：" + NetLog.Ip(from));
            }
            return true;
        }

        // 对手位让给队首：返回顶上来的地址（没人排队 = null）。刚下场的那个跳过（自己下去观战的不会马上又被叫上来）
        DateTime promotedAt = DateTime.MinValue;

        // ---- 主机下场：第二位选手
        public IPEndPoint Peer2 { get { return peers.GetSecond<IPEndPoint>(); } set { peers.SetSecond(value, PeerRoute.Direct, 0UL); } }
        public Action<string> OnSecondState { get; set; }
        public bool NoOut { get; set; }
        public bool HasSecond { get { return peers.HasSecond; } }
        public double SinceFromSecond { get { return peers.SinceFromSecond; } }
        public void SendSecond(byte[] b) { var p = Peer2; if (p != null) { Tx++; try { Udp.Send(b, b.Length, p); } catch (Exception) { } } }
        public object PromoteSecond()
        {
            var r = peers.PromoteSecond(SpecLost);
            if (r == null) return null;
            var head = (IPEndPoint)r.Handle;
            Log(L.T("第二位选手上场：", "Second player: ") + head);
            if (Diag) NetLog.W("主机下场：第二位选手 " + NetLog.Ip(head));
            return head;
        }
        public object DemoteSecond()
        {
            var r = peers.DemoteSecond();
            if (r == null) return null;
            var old = (IPEndPoint)r.Handle;
            Log(L.T("第二位选手下场：", "Second player steps down: ") + old);
            return old;
        }
        public void SecondToPeer()
        {
            PeerRow prev;
            var r = peers.SecondToPeer(out prev);
            if (r == null) return;
            Log(L.T("第二位选手改当对手：", "Second player is now the opponent: ") + r.Handle);
        }
        public const double PromoteGrace = 6.0;
        public IPEndPoint PromoteHead()
        {
            PeerRow prev;
            var r = peers.PromoteHead(SpecLost, out prev);
            if (r == null) return null;
            var head = (IPEndPoint)r.Handle;
            promotedAt = DateTime.UtcNow;
            Log(L.T("队首上场：", "Next in queue plays: ") + head);
            if (Diag) NetLog.W("排队：队首上场 " + NetLog.Ip(head));
            return head;
        }

        // 打完换人：现在的对手下场，排到队尾当观战者。返回下场的地址
        public IPEndPoint DemotePeer()
        {
            var r = peers.DemotePeer();
            if (r == null) return null;
            var old = (IPEndPoint)r.Handle;
            Log(L.T("对手下场观战：", "Opponent moves to spectators: ") + old);
            if (Diag) NetLog.W("排队：对手下场观战 " + NetLog.Ip(old));
            return old;
        }

        public double SinceFromPeer { get { return peers.SinceFromPeer; } }
        public bool SpecAlive(IPEndPoint ep) { return peers.SpecAlive(ep); }

        // 观战者在队里的位置（1 起），给每个人发 4NQ 用
        public List<IPEndPoint> QueueOrder()
        {
            return peers.QueueOrder<IPEndPoint>();
        }

        public void SendTo(IPEndPoint ep, byte[] b)
        {
            if (ep == null) return;
            try { Udp.Send(b, b.Length, ep); } catch (Exception) { }
        }

        public void SendSpecs(byte[] b)
        {
            var to = peers.SpecHandles<IPEndPoint>();
            foreach (var ep in to)
            {
                SpecTx++;
                try { Udp.Send(b, b.Length, ep); } catch (Exception) { }
            }
        }

        public Link(int port, IPEndPoint peer, string fRemote)
        {
            outq = new RemoteOut(fRemote);
            Peer = peer;
            lockFirst = peer == null;
            Udp = NetBind.SessionUdp(port, peer);     // 双栈：同一个端口收 IPv4 + IPv6
            // 对方端口还没开时 Windows 会把 ICMP 端口不可达报成下一次 Receive 的异常，关掉这个行为
            try { Udp.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch (Exception) { }
        }

        public int LocalPort { get { return ((IPEndPoint)Udp.Client.LocalEndPoint).Port; } }
        public string LocalEndpoint { get { var p = Peer; return p == null ? "" : LocalAddrFor(p); } }
        // M4：多人握手报的局域网端点。回环（测试）不报：跨机连不过去，还会把 NAT 测试里的对称 NAT 绕过去
        public string LocalLanEndpoint
        {
            get
            {
                var s = LocalEndpoint;
                var ep = Addr.ParseEp(s);     // IPv6 是 "[v6]:端口"
                if (ep == null || IPAddress.IsLoopback(ep.Address) || ep.Address.IsIPv6LinkLocal
                    || ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any)) return "";
                return s;
            }
        }
        public string PeerStr { get { var p = Peer; return p == null ? "" : p.ToString(); } }
        public string PeerName { get { return ""; } }
        public string PeerNick { get { return peers.PeerNick; } private set { peers.PeerNick = value; } }
        long ITransport.Tx { get { return Tx; } }
        long ITransport.Gap { get { return Gap; } }
        public double PingMs { get { return DelayN > 0 ? DelaySum / DelayN * 2000 : -1; } }   // 单程估计 ×2（两边时钟不同步时不准）
        Action<string> ITransport.Log { get { return Log; } set { Log = value ?? delegate { }; } }
        Wire wire = new Wire(null);
        public Wire Wire { get { return wire; } set { wire = value ?? new Wire(null); } }
        public RemoteOut Out { get { return outq; } }
        public volatile bool RehelloNow;
        bool movedOnce;
        DateTime lastReferLog = DateTime.MinValue;
        bool TryMemberRefer(IPEndPoint from, string s)
        {
            if (lockFirst || !MpMode || mesh == null || mesh.IsHost || !mesh.Enabled) return false;
            if (!IsMpHello(s)) return false;
            if (mesh.HasHandle(from)) return false;
            if (Blacklist.DeniesBody(s))
            {
                wire.Deny(b => { try { Udp.Send(b, b.Length, from); } catch (Exception) { } });
                return true;
            }
            if (!mesh.HostConfirmed) return true;
            if (!MpCfg.MidJoin && mesh.MpInMatch && !mesh.DuelOn)     // 对局中也收
            {
                MpRefused++;
                var rej = wire.Seal(WireTag.P + "BUSY|inmatch\n");
                try { Udp.Send(rej, rej.Length, from); } catch (Exception) { }
                return true;
            }
            if (mesh.DuelOn && mesh.MpSeen >= 2 + mesh.DuelCap)
            {
                MpRefused++;
                var rej = wire.Seal(WireTag.P + "MPREFUSE\nwhy=specfull\ncap=" + mesh.DuelCap + "\n");
                try { Udp.Send(rej, rej.Length, from); } catch (Exception) { }
                return true;
            }
            // 满了也转给房主：房主那边握手留着、有人走了就补上（不在成员这里回绝）
            if (mesh.IsHostHidden()) return true;
            string hostEp = LocalAddrForHost(mesh.HostPid);
            if (string.IsNullOrEmpty(hostEp) || Addr.ParseEp(hostEp) == null) return true;
            var mv = wire.Seal(WireTag.P + "MOVED|" + hostEp + "\n");
            try { Udp.Send(mv, mv.Length, from); } catch (Exception) { }
            var now = DateTime.UtcNow;
            if ((now - lastReferLog).TotalSeconds > 5.0)
            {
                lastReferLog = now;
                Log(L.T("多人：转告陌生人到房主 ", "Multi: referred stranger to host ") + hostEp);
            }
            return true;
        }
        public void PromoteToHost()
        {
            lockFirst = true;
            Peer = null;
            if (mesh != null)
            {
                mesh.HostAddr = LocalAddrFor(null);
                mesh.HostAddrOf = h => { var hep = h as IPEndPoint; return hep != null ? LocalAddrFor(hep) : null; };
            }
        }
        public void DemoteToClient(IPEndPoint newHost)
        {
            lockFirst = false;
            Peer = newHost;
        }
        public void Close()
        {
            if (mesh != null && mesh.MyPid > 0)     // 成员也告别（只告诉房主）
            {
                mesh.BroadcastBye();
                try { Thread.Sleep(30); } catch (Exception) { }
            }
            if (mesh != null) mesh.CloseBridge();
            try { Udp.Close(); } catch (Exception) { }
        }

        public void Send(byte[] b)
        {
            var p = Peer;
            if (p == null) return;
            Tx++;
            try { Udp.Send(b, b.Length, p); } catch (Exception) { }
        }

        // 诊断里只记包的种类，不记内容
        internal static string Head(string s)
        {
            if (s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal)) return "握手";
            if (s.StartsWith(WireTag.P + "EV", StringComparison.Ordinal) || s.StartsWith(WireTag.P + "ACK", StringComparison.Ordinal)) return "可靠事件";
            return s.Contains("seq=") ? "状态包" : "其他";
        }

        public static double UnixNow()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        // 返回这次收到的有效状态包数
        public int Pump()
        {
            string newest = null;
            int got = 0;
            // 一批里的每个包都排队，Session.Run 每轮写一个（RemoteOut）
            while (true)
            {
                int avail;
                try { avail = Udp.Available; } catch (Exception) { break; }
                if (avail <= 0) break;
                IPEndPoint from = null;
                byte[] data;
                try { data = Udp.Receive(ref from); } catch (Exception) { break; }
                from = NetBind.Norm(from);     // 双栈 socket 收到的 IPv4 包是 ::ffff:a.b.c.d，还原成 IPv4 再比较
                // v0.22.0：先验签再锁定对端，设了密码时伪造的包抢不走主机
                var to = from;
                var s = wire.Verify(data, 0, data.Length, b => { try { Udp.Send(b, b.Length, to); } catch (Exception) { } });
                if (s == null) continue;
                // 客机连的是主机的 IPv6 地址：主机回包时系统可能挑同一网段的另一个地址当源地址（Windows 的临时地址），
                // 同网段（前 64 位）+ 同端口的包当成主机发的（发还是发给填的那个地址，主机哪个地址都收）
                if (!lockFirst)
                {
                    var pp = Peer;
                    if (pp != null && from.Port == pp.Port && !from.Address.Equals(pp.Address) && Addr.SameV6Prefix(from.Address, pp.Address))
                    {
                        if (!v6AliasLogged) { v6AliasLogged = true; if (Diag) NetLog.W("主机的回包从同网段另一个 IPv6 地址来（" + NetLog.Ip(from) + "），当成主机"); }
                        from = pp;
                    }
                }
                // M4：解说房两位选手之间的直连（只在客机侧；状态包和房主中转的那份按 seq 去重）
                if (!lockFirst && specMeshPeer != null && MeshPacket(from, s)) continue;
                if (!lockFirst && s.StartsWith(WireTag.P + "BUSY|", StringComparison.Ordinal))
                {
                    if (DeniedMsg == null)
                    {
                        DeniedMsg = ParseDeniedMsg(s);
                        Log(DeniedMsg);
                        if (Diag) NetLog.W("被房主拒绝：" + DeniedMsg);
                    }
                    continue;
                }
                if (!lockFirst && s.StartsWith(WireTag.P + "MOVED|", StringComparison.Ordinal))
                {
                    if (MpMode && mesh != null && !mesh.HostConfirmed && !movedOnce && from.Equals(Peer))
                    {
                        var target = s.Substring((WireTag.P + "MOVED|").Length).Trim();
                        int nl = target.IndexOfAny(new[] { '\r', '\n', '|' });
                        if (nl >= 0) target = target.Substring(0, nl).Trim();
                        var newEp = Addr.ParseEp(target);
                        if (newEp != null && !newEp.Equals(Peer))
                        {
                            movedOnce = true;
                            Peer = newEp;
                            mesh.RetargetJoinHost(newEp);
                            RehelloNow = true;
                            Log(L.T("多人：已转向房主 ", "Multi: redirected to host ") + newEp);
                            if (Diag) NetLog.W("多人：被成员指路到房主 " + NetLog.Ip(newEp));
                        }
                    }
                    continue;
                }
                // M3 多人网状：介绍信 / 握手 / 直连状态 / 经第三人中转 / 延迟探测。认识的玩家在这里就处理掉，
                // 不进入下面的 1v1 锁定、观战、星形转发。
                if (mesh != null)
                {
                    int mg;
                    if (mesh.HandlePacket(from, s, out mg))
                    {
                        if (mg > 0) { Rx++; got += mg; }
                        continue;
                    }
                }
                if (!lockFirst && TryMemberRefer(from, s)) continue;
                // 多人混战：玩家的握手 / 状态包（不锁定、不占 1v1 的对手位）
                if (lockFirst && NoteMp(from, s)) continue;
                // 第二位选手的包：状态包交给 Session 转发；握手 / 可靠事件不进主机的可靠通道（它只认第一位）
                var p2 = Peer2;
                if (lockFirst && p2 != null && from.Equals(p2))
                {
                    peers.LastFromSecond = DateTime.UtcNow;
                    var s2Row = peers.SecondRow;
                    if (s2Row != null && (s.StartsWith(WireTag.P + "EV\n", StringComparison.Ordinal) || s.StartsWith(WireTag.P + "ACK\n", StringComparison.Ordinal)))
                    {
                        if (s2Row.Rel == null)
                        {
                            var to2 = from;
                            s2Row.Rel = new RelChannel(b => { try { var bytes = Wire.Seal(b); Udp.Send(bytes, bytes.Length, to2); } catch (Exception) { } });
                            if (OnDeliverPeerEvent != null) s2Row.Rel.OnDeliver = ev => OnDeliverPeerEvent(s2Row, ev);
                        }
                        if (s.StartsWith(WireTag.P + "EV\n", StringComparison.Ordinal)) { s2Row.Rel.OnEvents(s); continue; }
                        if (s.StartsWith(WireTag.P + "ACK\n", StringComparison.Ordinal)) { s2Row.Rel.OnAck(s); continue; }
                    }
                    if (s.StartsWith(WireTag.P, StringComparison.Ordinal) || s.Contains("hello=1")) continue;
                    var d2 = Kv.Parse(s);
                    string q2;
                    int sq2, gap2;
                    if (!d2.TryGetValue("seq", out q2) || !int.TryParse(q2, out sq2)) continue;
                    if (!peers.TakeSecondSeq(sq2, out gap2)) continue;
                    var cb2 = OnSecondState;
                    if (cb2 != null) cb2(s);
                    continue;
                }
                // 主机：观战者的握手 / 保活不锁定、不占对战位（对手本人发来的不算）
                var curPeer = Peer;
                if (lockFirst && !from.Equals(curPeer))
                {
                    var spRow = peers.FindByHandle(from);
                    if (spRow != null && (s.StartsWith(WireTag.P + "EV\n", StringComparison.Ordinal) || s.StartsWith(WireTag.P + "ACK\n", StringComparison.Ordinal)))
                    {
                        if (spRow.Rel == null)
                        {
                            var toSp = from;
                            spRow.Rel = new RelChannel(b => { try { var bytes = Wire.Seal(b); Udp.Send(bytes, bytes.Length, toSp); } catch (Exception) { } });
                            if (OnDeliverPeerEvent != null) spRow.Rel.OnDeliver = ev => OnDeliverPeerEvent(spRow, ev);
                        }
                        if (s.StartsWith(WireTag.P + "EV\n", StringComparison.Ordinal)) { spRow.Rel.OnEvents(s); continue; }
                        if (s.StartsWith(WireTag.P + "ACK\n", StringComparison.Ordinal)) { spRow.Rel.OnAck(s); continue; }
                    }
                }
                if (lockFirst && !from.Equals(curPeer) && NoteSpec(from, s)) continue;
                if (lockFirst && curPeer != null && !from.Equals(curPeer) && HostSpecCfg.On && !peers.SpecAlive(from))
                {
                    peers.NoteSpec(from, WireTag.P + "HELLO\nspec=1\n", SpecCfg.Max, 0UL);
                    continue;
                }
                // 对手自己说要下去观战（发来观战握手）：下场排到队尾
                // 刚叫上场的人在收到 4NROLE 之前还会发几个观战握手：上场后 PromoteGrace 秒内的不算
                if (lockFirst && curPeer != null && from.Equals(curPeer) && IsSpecHello(s))
                {
                    if ((DateTime.UtcNow - promotedAt).TotalSeconds > PromoteGrace) { DemotePeer(); NoteSpec(from, s); }
                    continue;
                }
                // 主机：锁定第一个来的对端；对端 3 秒没动静（比如对方重开了联机窗口，端口变了）就允许换人
                if (lockFirst && (curPeer == null || (!from.Equals(curPeer) && peers.SinceFromPeer > 3)))
                {
                    if (curPeer == null || !from.Equals(curPeer))
                    {
                        Log(L.T("对端锁定为 ", "Peer locked: ") + from);
                        if (Diag) NetLog.W((curPeer == null ? "对端锁定为 " : "原对端 3 秒没动静，换成 ") + NetLog.Ip(from) + "（第一个包 " + Head(s) + "）");
                    }
                    peers.SetPeer(from, PeerRoute.Direct);
                    peers.ResetPeerSeq();
                    curPeer = from;
                }
                if (curPeer != null && !from.Equals(curPeer)) continue;
                // M4：房主介绍另一位选手（局域网 / 输入 IP 的解说房直连）。双方互发 4NHELLO 打洞。
                if (!lockFirst && s.StartsWith(WireTag.P + "MESH\n", StringComparison.Ordinal))
                {
                    var qm = Kv.Parse(s);
                    string pv, tv, mhv;
                    if (qm.TryGetValue("peer", out pv))
                    {
                        var ep = Addr.ParseDirect(pv, Session.GamePort);     // IPv6 房间里介绍来的是 "[v6]:端口"
                        if (ep != null)
                            MeshIntro(ep, qm.TryGetValue("token", out tv) ? tv : "",
                                      qm.TryGetValue("matchhost", out mhv) && mhv == "1");
                    }
                    continue;
                }
                // 主机发来的换角色 / 排队位置（只认主机：观战者 / 客机的 Peer 就是主机）
                if (!lockFirst && s.StartsWith(WireTag.P + "ROLE", StringComparison.Ordinal))
                {
                    var q = Kv.Parse(s);
                    string rv, mh;
                    bool isMh = q.TryGetValue("matchhost", out mh) && mh == "1";
                    if (q.TryGetValue("role", out rv))
                    {
                        var cbx = OnRoleEx; if (cbx != null) cbx(rv, isMh);
                        var cb = OnRole; if (cb != null) cb(rv);
                    }
                    continue;
                }
                // 多人混战：主机给的编号 / 人数
                if (!lockFirst && s.StartsWith(WireTag.P + "MP", StringComparison.Ordinal))
                {
                    var q = Kv.Parse(s);
                    string a, b2;
                    int pid, n;
                    if (q.TryGetValue("pid", out a) && int.TryParse(a, out pid) && pid != MyPid)
                    {
                        MyPid = pid;
                        if (mesh != null) mesh.MyPid = pid;
                        Log(L.T("多人：主机给的编号 ", "Multi: my id ") + pid);
                    }
                    if (q.TryGetValue("n", out b2) && int.TryParse(b2, out n)) MpSeen = n;
                    string l;
                    MpRoster = q.TryGetValue("l", out l) ? l : "";
                    if (mesh != null) mesh.PruneRoster(MpRoster);
                    continue;
                }
                if (!lockFirst && s.StartsWith(WireTag.P + "Q", StringComparison.Ordinal))
                {
                    var q = Kv.Parse(s);
                    string a, b2;
                    int pos, len;
                    if (q.TryGetValue("pos", out a) && int.TryParse(a, out pos)) QPos = pos;
                    if (q.TryGetValue("n", out b2) && int.TryParse(b2, out len)) QLen = len;
                    continue;
                }
                peers.LastFromPeer = DateTime.UtcNow;
                if (wire.Control(s)) continue;     // 可靠事件 / 确认 / 握手（握手包只用来让主机知道往哪回）
                if (s.Contains("hello=1")) continue;
                var d = Kv.Parse(s);
                string seqS;
                int seq;
                if (!d.TryGetValue("seq", out seqS) || !int.TryParse(seqS, out seq)) continue;
                string src;
                int gap;
                if ((SpecMode || MpMode) && d.TryGetValue("src", out src))
                {
                    // 观战者：主机 / 对手两路包各自的序号；多人混战的客机：每个编号一路
                    if (!peers.TakeSrcSeq(src, seq, out gap)) { Stale++; continue; }
                    Gap += gap;
                    if (src == "h") SrcH++; else SrcC++;
                }
                else
                {
                    if (!peers.TakePeerSeq(seq, out gap)) { Stale++; continue; }   // 乱序到达的旧包；回退很多 = 对方重启了游戏
                    Gap += gap;
                }
                Rx++;
                got++;
                string st;
                double t;
                if (d.TryGetValue("st", out st) && double.TryParse(st, NumberStyles.Float, CultureInfo.InvariantCulture, out t))
                {
                    double delay = UnixNow() - t;
                    DelaySum += delay; DelayN++;
                    if (delay > DelayMax) DelayMax = delay;
                }
                newest = s;
                if (!SpecMode) wire.DzSeenText(s);      // 认定是对手的状态包了：算对方会不会拼省流包
                if (!NoOut) outq.Push(s);
                var fw = OnPeerState;
                if (fw != null && !SpecMode) fw(s);
            }
            outq.Flush();
            if (newest != null && !SpecMode) PeerNick = Kv.NickOf(newest) ?? PeerNick;
            return got;
        }
    }

    // 一次联机：host 监听固定端口、等第一个对端；client 用随机端口连主机。后台线程里跑
    class Session
    {
        public const int GamePort = 27015, DiscoveryPort = 27016, PollMs = 4;
        public static int BroadcastPort = DiscoveryPort;
        public static int ClientBindPort;     // 测试用（--client-port）：客机固定本地端口；平时 0 = 系统随便给
        public readonly string Name, Mode;
        volatile string role;
        public string Role { get { return role; } }     // 观战者 ↔ 客机会在会话里换（轮到上场 / 下场观战）
        public readonly ITransport T;
        volatile string fLocal, fSession;
        volatile string retargetTo;     // 等人的房间转正：Run 线程里换目录
        readonly IPEndPoint enteredPeer;
        readonly bool allowBroadcast, doBackup;
        volatile bool broadcast, bcastReset;
        volatile bool stop;
        Thread thread, sendThread;
        readonly object sendLock = new object();   // 发包线程和主循环都会发（SimNet / 传输不保证线程安全）
        DateTime lastRx = DateTime.MinValue;
        DateTime lastSess = DateTime.MinValue;
        public long RxSeen, Loops, SendLoops;   // Run / SendLoop 各转了几轮（命令行日志里算"轮询 X 次/秒"）
        public volatile string LocalMap = "?";
        public volatile string RemoteMap = "?";
        // 排位：自己的排位 pid（发包时加 rk=，对方据此上报"和谁打的"）；null = 不是排位房间。
        // LocalMt / LocalQuit = 本机游戏状态包里的 mt= / quit=（主机看自己的 mt= 判开局 / 打完，客机看自己的 quit= 判弃权）
        public volatile string RankPid, LocalMt, LocalQuit;
        public volatile string MpTag = "m=elim;tm=0;st=idle";
        public string MpDeniedMsg
        {
            get
            {
                var lk = T as Link;
                if (lk != null && !string.IsNullOrEmpty(lk.DeniedMsg)) return lk.DeniedMsg;
                var sl = T as ServerLink;
                if (sl != null && !string.IsNullOrEmpty(sl.DeniedMsg)) return sl.DeniedMsg;
                var bl = T as BridgeLink;
                if (bl != null && !string.IsNullOrEmpty(bl.DeniedMsg)) return bl.DeniedMsg;
                var ml = T as IMpLink;
                if (ml != null && ml.Mesh != null && !string.IsNullOrEmpty(ml.Mesh.RefusedMsg)) return ml.Mesh.RefusedMsg;
                return null;
            }
        }
        internal static bool IsInMatchTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            foreach (var part in tag.Split(';'))
            {
                var p = part.Trim();
                if (p == "st=cd" || p == "st=fight") return true;
            }
            return false;
        }
        internal static string UpdateTagState(string tag, string st)
        {
            var sb = new StringBuilder();
            bool hasSt = false;
            if (!string.IsNullOrEmpty(tag))
            {
                foreach (var raw in tag.Split(';'))
                {
                    var p = raw.Trim();
                    if (p.Length == 0) continue;
                    if (sb.Length > 0) sb.Append(';');
                    if (p.StartsWith("st=", StringComparison.Ordinal)) { sb.Append("st=").Append(st); hasSt = true; }
                    else sb.Append(p);
                }
            }
            if (!hasSt) { if (sb.Length > 0) sb.Append(';'); sb.Append("st=").Append(st); }
            return sb.ToString();
        }
        internal void SyncMpTag(string tag)
        {
            MpTag = tag;
            var lk = T as Link;
            if (lk != null) lk.InMatch = IsInMatchTag(tag);
            var bl = T as BridgeLink;
            if (bl != null) bl.InMatch = IsInMatchTag(tag);
            var ml = T as IMpLink;
            if (Role == "host" && ml != null && ml.Mesh != null) ml.Mesh.SetHostInMatch(IsInMatchTag(tag));
            if ((Mode == "bridge" || bl != null) && Role == "host" && MpCfg.Max >= 2)
            {
                int cur = (bl != null ? bl.MpCount + 1 : 1);
                Bridge.SetHosting(true, MpMesh.Hosting(bl != null ? bl.Mesh : null, cur, MpCfg.Max, tag));
            }
        }
        static int serialSeq;
        public readonly int Serial = System.Threading.Interlocked.Increment(ref serialSeq);     // F4c-3：会话序号，排位插件据此认"同一个联机"（以前是 ReferenceEquals）
        public volatile string LocalRfp;       // 规则指纹：本机状态包的 rfp=<局号>:<指纹>（"-" = 没有），排位时报给服务器
        public volatile string LocalRkd;       // 排位客机复核：本机状态包的 rkd=<局号>:<对不上的次数>（"-" = 没有）
        public IMatchStats Stats;     // 排位房间：本机每个状态包的武器 / 影之武装 / 放招记进去（F4c-1：接口）
        public Action<string> Log = delegate { };
        readonly HashSet<string> backedUp = new HashSet<string>();
        public readonly Wire Wire;            // v0.22.0 包签名（Reliable.cs）
        public readonly RelChannel Rel;       // v0.22.0 可靠事件通道
        readonly SimNet sim = new SimNet();
        // 连接诊断（net\net_conn.log）：服务器大厅 / 输入 IP 直连 / 局域网写；桥接平台有自己的 steam_conn.log，AI 对战不写
        readonly bool diag;
        DateTime startedAt, connAt = DateTime.MinValue, lostAt = DateTime.MinValue;
        bool wasConn, helloLogged;
        string lastHint = "", lastPeerApp;

        // UDP：peer = null 当主机（监听 port），否则连 peer；t 不为空时用它（桥接平台）。mode = 桥接平台 | lan | server（server 只在"输入 IP 直连"的客机走这里）
        public Session(string role, IPEndPoint peer, string name, string dir, bool doBackup)
            : this(role, "lan", null, peer, GamePort, name, dir, doBackup, null) { }

        // secret：联机密码，空 = 不签名
        public Session(string role, string mode, ITransport t, IPEndPoint peer, int port, string name, string dir, bool doBackup, string secret)
        {
            this.role = role;
            Mode = mode;
            Name = string.IsNullOrEmpty(name) ? Environment.MachineName : name;
            enteredPeer = peer;
            fLocal = Path.Combine(dir, "state_local.txt");
            fSession = Path.Combine(dir, "session.txt");
            T = t ?? new Link(role == "host" ? port : ClientBindPort, peer, Path.Combine(dir, "state_remote.txt"));
            var lk0 = T as Link;
            if (lk0 != null)
            {
                lk0.OnDeliverPeerEvent = HandlePeerEvent;
                lk0.OnRoleEx = (r, mh) => { wantRole = r; wantMatchHost = mh; };
            }
            var st0 = T as BridgeLink;
            if (st0 != null)
            {
                st0.OnDeliverPeerEvent = HandlePeerEvent;
                st0.OnRoleEx = (r, mh) => { wantRole = r; wantMatchHost = mh; };
            }
            // 观战：主机把对手的包加 src=c 转给观战者；观战者（role = spec）只收不发状态包
            var ss0 = T as ISpecSink;
            if (ss0 != null && role == "host") ss0.OnPeerState = body => { if (RelayOn) RelayForward(body, true); else ForwardSpec(body, "c"); };
            var rh0 = T as IRelayHost;
            if (rh0 != null && role == "host") rh0.OnSecondState = body => { secondRx++; if (RelayOn) RelayForward(body, false); };
            if (ss0 != null && role == "spec") ss0.SpecMode = true;
            if (ss0 != null && role != "host") ss0.OnRole = r => wantRole = r;
            // 局域网 / 虚拟局域网才广播房间（广域网的朋友收不到广播；桥接平台用富状态）
            // 测试给了 --lan-port（不是 27015）时默认不广播，免得开着的游戏的房间列表里冒出测试房间；若同时改了 --disc-port 则照常广播到该端口
            allowBroadcast = mode == "lan" && T is Link && (port == GamePort || BroadcastPort != DiscoveryPort);
            broadcast = role == "host" && allowBroadcast;
            this.doBackup = doBackup;
            Wire = new Wire(secret) { SimSkewSec = Program.SimSkew };
            Rel = new RelChannel(dir, SendText);
            var slw0 = T as ServerLink;
            if (slw0 != null)
            {
                // 服务器自己开的房：服务器发角色（含 matchhost）、聊天和捡道具结果
                slw0.OnRoleEx = (r, mh) => { wantRole = r; wantMatchHost = mh; };
                slw0.OnServerEvent = ev => Rel.DeliverLocal(ev);
            }
            Wire.Rel = Rel;
            Wire.OnRules = r =>
            {
                string s = r ?? "";
                if (s.StartsWith(WireTag.P + "RULES\n", StringComparison.Ordinal)) s = s.Substring((WireTag.P + "RULES\n").Length);
                RoomRulesStr = s.Replace("\r", "").Replace("\n", "").Trim();
            };
            Rel.OnDeliver = data => HandlePeerEvent(Peers.PeerRow, data);
            Rel.InterceptEnqueue = data =>
            {
                var mlk = T as IMpLink;
                if (mlk == null || mlk.Mesh == null || !mlk.Mesh.Enabled) return false;
                var sl = T as ServerLink;
                if (sl != null && sl.ServerRoom) return false;     // 服务器房聊天走 SCHAT
                if (Role == "host" && mlk.Mesh.IsHost)
                {
                    if (data.StartsWith("chat "))
                    {
                        int selfPid = mlk.Mesh.MyPid > 0 ? mlk.Mesh.MyPid : 1;
                        BroadcastChat(HostSpecCfg.On ? "[解说] " + Name : Name, data.Substring(5), 0, selfPid);
                        return true;
                    }
                    if (data.StartsWith("cfg ") || data.StartsWith("rfpx "))
                    {
                        mlk.Mesh.HostRelAll(data);
                        return true;
                    }
                    return false;
                }
                if (Role == "client" && !mlk.Mesh.IsHost)
                {
                    return mlk.Mesh.SendToHostRel(data);
                }
                return false;
            };
            Rel.OnLocalEnqueue = data =>
            {
                if (Role == "host")
                {
                    if (data.StartsWith("chat "))
                    {
                        var mlkSelf = T as IMpLink;
                        int selfPid = (mlkSelf != null && mlkSelf.Mesh != null && mlkSelf.Mesh.MyPid > 0) ? mlkSelf.Mesh.MyPid : 1;
                        BroadcastChat(HostSpecCfg.On ? "[解说] " + Name : Name, data.Substring(5), 0, selfPid);
                    }
                    else if (data.StartsWith("cfg "))
                    {
                        if (Peers.SecondRow != null)
                        {
                            if (Peers.SecondRow.Rel == null) EnsurePeerRel(Peers.SecondRow);
                            if (Peers.SecondRow.Rel != null) Peers.SecondRow.Rel.Send(data);
                        }
                    }
                    // 全互连多人房：房主的数值表块（cfg）和这一场的规则整串（rfpx）发给全房（以前只到 1v1 的第二位，多人里谁都收不到）
                    if (data.StartsWith("cfg ") || data.StartsWith("rfpx "))
                    {
                        var mlkAll = T as IMpLink;
                        if (mlkAll != null && mlkAll.Mesh != null && mlkAll.Mesh.IsHost) mlkAll.Mesh.HostRelAll(data);
                    }
                }
            };
            T.Wire = Wire;
            if (role == "host") Wire.Gate = Blacklist.DeniesBody;
            diag = mode != "ai" && !Modes.IsBridge(mode);
            var lk = T as Link;
            if (lk != null) lk.Diag = diag;
            // 多人混战（②a）：主机把每位玩家的包转给其他玩家；客机按多人加入（局域网 / 输入 IP / 桥接平台）
            if (lk != null && role == "host") lk.OnMpState = (pid, tagged) => { lock (sendLock) lk.SendMp(Wire.Seal(tagged), pid); };
            var mlink = T as IMpLink;
            MpJoin = mlink != null && role == "client" && MpCfg.Join;
            if (mlink != null && role == "host" && MpCfg.Max >= 2) mlink.EnableMpMesh(true);      // M3：房主发介绍信、玩家两两直连
            if (MpJoin) mlink.EnableMpMesh(false);
            if ((mode == "bridge" || T is BridgeLink) && role == "host" && MpCfg.Max >= 2)
            {
                Bridge.SetHosting(true, "1/" + MpCfg.Max + ";" + MpTag);
            }
            if (mlink != null && mlink.Mesh != null)
            {
                HookMpAi(mlink.Mesh);
                mlink.Mesh.ChatIn = (pid, nick, text) => BroadcastChat(string.IsNullOrEmpty(nick) ? "玩家" + pid : nick, text, pid);
                mlink.Mesh.DeliverRel = ev => Rel.DeliverLocal(ev);
                mlink.Mesh.OnHostChanged += delegate(int newHost, int newEpoch)
                {
                    if (mlink.Mesh.MyPid == newHost)
                    {
                        PromoteToHost();
                    }
                    else
                    {
                        DemoteToClient(newHost);
                    }
                };
            }
        }

        void HookMpAi(MpMesh mesh)
        {
            mesh.OnHumanStateForAi = (pid, body) => FeedMpAi(pid, body);
            mesh.OnPeerLeftForAi = pid => OnMpPeerLeft(pid);
            mesh.OnYieldAiForHuman = pid => OnMpAiYielded(pid);
        }

        void PromoteToHost()
        {
            StopAllMpAi();
            role = "host";
            wantRole = "host";
            MpJoin = false;
            Wire.Gate = Blacklist.DeniesBody;
            var lk = T as Link;
            if (lk != null)
            {
                if (MpCfg.Max < 2) MpCfg.Max = (lk.Mesh != null && lk.Mesh.MpMax >= 2) ? lk.Mesh.MpMax : 4;
                lk.PromoteToHost();
                lk.OnMpState = (pid, tagged) => { lock (sendLock) lk.SendMp(Wire.Seal(tagged), pid); };
            }
            var sl = T as ServerLink;
            if (sl != null)
            {
                if (MpCfg.Max < 2) MpCfg.Max = (sl.Mesh != null && sl.Mesh.MpMax >= 2) ? sl.Mesh.MpMax : 4;
                sl.PromoteToMpHost();
                sl.OnMpState = (pid, tagged) => { lock (sendLock) sl.SendMp(Wire.Seal(tagged), pid); };
            }
            var bl = T as BridgeLink;
            if (bl != null)
            {
                if (MpCfg.Max < 2) MpCfg.Max = (bl.Mesh != null && bl.Mesh.MpMax >= 2) ? bl.Mesh.MpMax : 4;
                bl.PromoteToMpHost();
            }
            if (allowBroadcast && MpCfg.Max >= 2) { bcastReset = true; broadcast = true; }
            SyncMpTag(UpdateTagState(MpTag, "idle"));
            lastSess = DateTime.MinValue;
        }

        void DemoteToClient(int newHost)
        {
            StopAllMpAi();
            role = "client";
            wantRole = "client";
            MpCfg.Join = true;
            Wire.Gate = null;
            broadcast = false;
            var lk = T as Link;
            if (lk != null)
            {
                var mh = T as IMpLink;
                IPEndPoint newEp = null;
                if (mh != null && mh.Mesh != null)
                {
                    var p = mh.Mesh.Get(newHost);
                    if (p != null && p.Handle is IPEndPoint) newEp = (IPEndPoint)p.Handle;
                }
                lk.DemoteToClient(newEp);
                lk.OnMpState = null;
            }
            var sl = T as ServerLink;
            if (sl != null)
            {
                sl.DemoteToMpClient(newHost);
                sl.OnMpState = null;
            }
            var bl = T as BridgeLink;
            if (bl != null)
            {
                bl.DemoteToMpClient(newHost);
            }
            lastSess = DateTime.MinValue;
        }

        // ---- 多人混战
        public bool MpJoin;        // 多人混战客机（--mp-join / 服务器自己开的多人房）
        DateTime lastMpInfo = DateTime.MinValue;
        public void TouchSess() { lastSess = DateTime.MinValue; }

        // ---- 多人本地 AI（在主机进程内跑多个 AI 玩家；不走网络 socket，由宿主直接喂包、代发）
        readonly object mpAiLock = new object();
        readonly Dictionary<int, IMpAiBot> mpBots = new Dictionary<int, IMpAiBot>();
        int mpAiLevel = 1;
        int wantMpAiCount;
        IAiLoad mpAiLoad;
        public int MpAiActiveCount { get { lock (mpAiLock) return mpBots.Count; } }

        void SyncMpAiMetaLocked(MpMesh mesh)
        {
            if (mesh == null) return;
            var aiIds = new List<int>(mpBots.Keys);
            aiIds.Sort();
            int hp = mesh.MyPid > 0 ? mesh.MyPid : 1;
            foreach (var bot in mpBots.Values)
            {
                try { bot.SetMeta(hp, aiIds); } catch (Exception) { }
            }
        }

        public void FeedMpAi(int pid, string body)
        {
            if (pid <= 0 || string.IsNullOrEmpty(body)) return;
            IMpAiBot[] bots;
            lock (mpAiLock)
            {
                if (mpBots.Count == 0) return;
                bots = new IMpAiBot[mpBots.Count];
                mpBots.Values.CopyTo(bots, 0);
            }
            for (int i = 0; i < bots.Length; i++)
            {
                if (bots[i].Pid == pid) continue;
                try { bots[i].FeedPacket(pid, body); } catch (Exception) { }
            }
        }

        public void RemoveMpAiPeer(int pid)
        {
            IMpAiBot[] bots;
            lock (mpAiLock)
            {
                if (mpBots.Count == 0) return;
                bots = new IMpAiBot[mpBots.Count];
                mpBots.Values.CopyTo(bots, 0);
            }
            for (int i = 0; i < bots.Length; i++)
            {
                try { bots[i].RemovePlayer(pid); } catch (Exception) { }
            }
        }

        void OnMpAiYielded(int pid)
        {
            IMpAiBot removed = null;
            var mh = T as IMpLink;
            lock (mpAiLock)
            {
                if (mpBots.TryGetValue(pid, out removed)) mpBots.Remove(pid);
                if (mh != null) SyncMpAiMetaLocked(mh.Mesh);
            }
            if (removed != null)
            {
                try { removed.Stop(); } catch (Exception) { }
                RemoveMpAiPeer(pid);
            }
            TouchSess();
        }

        public void UpdateMpAiConfig(int level, IAiLoad load)
        {
            IMpAiBot[] bots;
            lock (mpAiLock)
            {
                mpAiLevel = level;
                mpAiLoad = load != null ? load.Clone() : null;
                if (mpBots.Count == 0) return;
                bots = new IMpAiBot[mpBots.Count];
                mpBots.Values.CopyTo(bots, 0);
            }
            for (int i = 0; i < bots.Length; i++)
            {
                try
                {
                    bots[i].Level = level;
                    if (load != null) bots[i].Load = load.Clone();
                }
                catch (Exception) { }
            }
        }

        public void TrimMpAiTo(int keepCount, Action<string> log)
        {
            var mh = T as IMpLink;
            MpMesh mesh = mh != null ? mh.Mesh : null;
            var stopped = new List<IMpAiBot>();
            lock (mpAiLock)
            {
                int target = Math.Max(0, keepCount);
                while (mpBots.Count > target)
                {
                    int maxPid = 0;
                    foreach (var k in mpBots.Keys) if (k > maxPid) maxPid = k;
                    if (maxPid <= 0) break;
                    IMpAiBot b = mpBots[maxPid];
                    mpBots.Remove(maxPid);
                    if (mesh != null) mesh.RemoveAiPlayer(maxPid);
                    stopped.Add(b);
                }
                if (stopped.Count > 0 && mesh != null) SyncMpAiMetaLocked(mesh);
            }
            for (int i = 0; i < stopped.Count; i++)
            {
                try { stopped[i].Stop(); } catch (Exception) { }
                RemoveMpAiPeer(stopped[i].Pid);
                if (log != null) log(L.T("多人：移除 AI #", "Multi: removed AI #") + stopped[i].Pid);
            }
            if (stopped.Count > 0 && mh != null)
            {
                try { mh.SendMpInfo(); } catch (Exception) { }
                TouchSess();
            }
        }

        public void StopAllMpAi()
        {
            lock (mpAiLock) wantMpAiCount = 0;
            TrimMpAiTo(0, null);
        }

        public void OnMpPeerLeft(int pid)
        {
            RemoveMpAiPeer(pid);
            RefillMpAi();
        }

        public void RefillMpAi()
        {
            int target;
            int level;
            IAiLoad load;
            lock (mpAiLock)
            {
                target = wantMpAiCount;
                level = mpAiLevel;
                load = mpAiLoad != null ? mpAiLoad.Clone() : null;
            }
            if (target <= 0 || Role != "host" || MpCfg.Max < 2 || !LocalAiSupport.Has) return;
            var mh = T as IMpLink;
            if (mh == null || mh.Mesh == null || !mh.Mesh.IsHost || mh.Mesh.DuelOn) return;
            if (MpAiActiveCount >= target) return;
            ReconcileMpAi(this, target, level, load, Log, true);
        }

        public static bool ApplyMpAi(Session s, int wantCount, int level, IAiLoad load, Action<string> log)
        {
            var mh = s != null ? s.T as IMpLink : null;
            if (s == null || s.Role != "host" || mh == null || mh.Mesh == null || !mh.Mesh.IsHost || MpCfg.Max < 2)
            {
                if (log != null) log(L.T("只有多人房的房主能增减 AI", "Only the multiplayer room host can change AI count"));
                return false;
            }
            if (wantCount > 0 && !LocalAiSupport.Has)
            {
                if (log != null) log(L.T("这个版本没有本地 AI（由游戏适配提供）", "This build has no local AI (provided by the game adapter)"));
                return false;
            }
            if (wantCount > 0 && mh.Mesh.DuelOn)
            {
                if (log != null) log(L.T("对决模式不支持本地 AI", "Duel mode does not support local AI"));
                return false;
            }
            int clampedTarget = Math.Max(0, Math.Min(wantCount, MpCfg.Limit - 1));
            lock (s.mpAiLock) s.wantMpAiCount = clampedTarget;
            return ReconcileMpAi(s, wantCount, level, load, log, false);
        }

        static bool ReconcileMpAi(Session s, int wantCount, int level, IAiLoad load, Action<string> log, bool refillOnly)
        {
            var mh = s != null ? s.T as IMpLink : null;
            if (s == null || s.Role != "host" || mh == null || mh.Mesh == null || !mh.Mesh.IsHost || MpCfg.Max < 2) return false;
            MpMesh mesh = mh.Mesh;
            int humanPeers = mesh.HumanPeerCount();
            int maxAi = Math.Max(0, Math.Min(MpCfg.Max - 1 - humanPeers, MpCfg.Max - 1));
            int want = Math.Max(0, Math.Min(wantCount, maxAi));
            if (!refillOnly) s.UpdateMpAiConfig(level, load);
            var added = new List<IMpAiBot>();
            var removed = new List<IMpAiBot>();
            lock (s.mpAiLock)
            {
                if (refillOnly && s.mpBots.Count >= want) return false;
                while (!refillOnly && s.mpBots.Count > want)
                {
                    int maxPid = 0;
                    foreach (var k in s.mpBots.Keys) if (k > maxPid) maxPid = k;
                    if (maxPid <= 0) break;
                    removed.Add(s.mpBots[maxPid]);
                    s.mpBots.Remove(maxPid);
                    mesh.RemoveAiPlayer(maxPid);
                }
                while (s.mpBots.Count < want)
                {
                    var used = mesh.Pids();
                    if (mesh.MyPid > 0) used.Add(mesh.MyPid);
                    int nextPid = 0;
                    for (int cand = 2; cand <= MpCfg.Max; cand++)
                    {
                        if (!used.Contains(cand)) { nextPid = cand; break; }
                    }
                    if (nextPid <= 0)
                    {
                        for (int cand = 2; cand <= MpCfg.Limit; cand++)
                        {
                            if (!used.Contains(cand)) { nextPid = cand; break; }
                        }
                    }
                    if (nextPid <= 0) break;
                    string nick = "[AI] AI" + nextPid;
                    var botLoad = load != null ? load.Clone() : (s.mpAiLoad != null ? s.mpAiLoad.Clone() : LocalAiSupport.NewLoad());
                    var bot = LocalAiSupport.CreateMp(nextPid, nick, level, botLoad);
                    if (bot == null) break;
                    // 也喂给别的 AI（以前 AI 只看得见真人，找不到别队的 AI 当目标）；OnState 在 AI 线程锁外调，不会互锁
                    bot.OnState = delegate(int aiPid, string body) { mesh.BroadcastAiState(aiPid, body); s.FeedMpAi(aiPid, body); };
                    bot.Log = delegate(string msg) { if (s.Log != null) s.Log(msg); };
                    s.mpBots[nextPid] = bot;
                    mesh.AddAiPlayer(nextPid, nick);
                    added.Add(bot);
                }
                if (added.Count > 0 || removed.Count > 0) s.SyncMpAiMetaLocked(mesh);
            }
            for (int i = 0; i < removed.Count; i++)
            {
                try { removed[i].Stop(); } catch (Exception) { }
                s.RemoveMpAiPeer(removed[i].Pid);
            }
            for (int i = 0; i < added.Count; i++)
            {
                try { added[i].Start(); } catch (Exception) { }
            }
            if (refillOnly && added.Count == 0 && removed.Count == 0) return false;
            try { mh.SendMpInfo(); } catch (Exception) { }
            s.TouchSess();
            if (refillOnly)
            {
                NetLog.W("多人房间真人离开，自动补回 AI → " + s.MpAiActiveCount + "（原设 " + wantCount + "，人类=" + (1 + humanPeers) + "，上限=" + MpCfg.Max + "）");
                if (log != null) log(string.Format(L.T("人数空出，已补回 AI：{0}", "Slot opened, refilled AIs: {0}"), s.MpAiActiveCount));
            }
            else
            {
                NetLog.W("多人房间 AI 数量 → " + s.MpAiActiveCount + "（要 " + wantCount + "，人类=" + (1 + humanPeers) + "，上限=" + MpCfg.Max + "）");
                if (want != wantCount && log != null)
                    log(string.Format(L.T("房间 AI 已调整为 {0}（空位限制）", "Room AIs set to {0} (slot limit)"), want));
                else if (log != null)
                    log(string.Format(L.T("房间 AI：{0}", "Room AIs: {0}"), s.MpAiActiveCount));
            }
            return true;
        }
        // 房主在房间里改人数上限（Lua 面板"房间人数"，命令 mpsize n=）。 可以低于现在的人数：已经进来的人不赶，
        // 但走了的人要等人数降到上限以下才能再进（Mesh.NoteMp 按人头数挡）；
        // 不高于 MpCfg.Limit。11-15 用户（"新版 1v1 和其他模式的人数上限要同步，不然 1v1 这边 18 人、切换模式不就炸了"）：对决模式也走这里，
        // 和其他玩法同一个上限、观众上限 = 人数 − 2（DuelCmdOn）；Lua 切玩法后把同一个"房间人数"再发一遍。改完：编号分配（MpCfg.Max / 网状 MpMax）、
        // 服务器大厅登记（ServerLink.MpMax，心跳带出去）、桥接富状态（"人数/上限;标签"）一起换，下一次名单广播（2 秒内）带给成员
        public static bool ApplyMpSize(Session s, int n, Action<string> log)
        {
            var mh = s != null ? s.T as IMpLink : null;
            if (mh == null || mh.Mesh == null || !mh.Mesh.IsHost)
            {
                if (log != null) log(L.T("只有多人房的房主能改房间人数", "Only the multiplayer room host can change the room size"));
                return false;
            }
            bool duel = mh.Mesh.DuelOn;
            int want = Math.Max(2, Math.Min(n, MpCfg.Limit));     // 不再抬到现在的人数；11-15 对决同一个上限
            if (duel) mh.Mesh.DuelCmdOn(true, want - 2);     // 11-15 对决：2 位选手 + 观众
            // 调小上限时按真人优先先裁掉最高编号 AI（仍不足再由 按人头数拦新人）
            int humanTotal = 1 + mh.Mesh.HumanPeerCount();
            int maxAiKeep = Math.Max(0, want - humanTotal);
            lock (s.mpAiLock) { if (s.wantMpAiCount > want - 1) s.wantMpAiCount = Math.Max(0, want - 1); }
            if (s.MpAiActiveCount > maxAiKeep) s.TrimMpAiTo(maxAiKeep, log);
            int cur = mh.MpPlayers().Count + 1;
            MpCfg.Max = want;
            mh.Mesh.MpMax = want;
            var sl = s.T as ServerLink;
            if (sl != null) sl.MpMax = want;
            var blk = s.T as BridgeLink;
            if (blk != null) { s.lastBridgeHosting = MpMesh.Hosting(blk.Mesh, blk.MpCount + 1, want, s.MpTag); Bridge.SetHosting(true, s.lastBridgeHosting); }
            s.TouchSess();
            NetLog.W("多人房间人数上限 → " + want + "（要 " + n + "，现在 " + cur + " 人）");
            if (want < cur && log != null) log(string.Format(L.T("房间里现在 {0} 人、上限 {1}：在的人不受影响，走了的人要等人数降到 {1} 以下才能再进",
                                                                  "{0} players in the room, cap {1}: nobody is removed, but anyone who leaves can only rejoin once the room drops below {1}"), cur, want));
            else if (want != n && log != null) log(string.Format(L.T("上限改成 {0}", "Cap set to {0}"), want));
            return true;
        }
        string lastBridgeHosting;
        public static bool ApplyDuelCmd(Session s, string cmd, Dictionary<string, string> d, Action<string> log)
        {
            if (cmd == "duellat")
            {
                if (!MpMesh.TestInject)
                {
                    if (log != null) log("duellat ignored (enable --duel-test or " + WireTag.EnvPrefix + "DUEL_TEST=1)");
                    return false;
                }
                string spec;
                d.TryGetValue("spec", out spec);
                MpMesh.SetInject(spec ?? "");
                if (log != null) log("[duel] test latency inject: " + (string.IsNullOrEmpty(spec) ? "cleared" : spec));
                return true;
            }
            var mh = s != null ? s.T as IMpLink : null;
            if (mh == null || mh.Mesh == null)
            {
                if (log != null) log(L.T("对决命令只在多人房间里有效", "Duel commands only work in a multiplayer room"));
                return false;
            }
            string v;
            if (cmd == "duel")
            {
                bool on = d.TryGetValue("on", out v) ? v != "0" : true;
                int cap = -1;
                if (d.TryGetValue("cap", out v)) int.TryParse(v, out cap);
                bool ok = mh.Mesh.DuelCmdOn(on, cap);
                if (ok && on)
                {
                    bool hadAi = s.MpAiActiveCount > 0;
                    s.StopAllMpAi();
                    if (hadAi && log != null) log(L.T("对决模式不支持本地 AI，已移除房间内 AI", "Duel mode does not support local AI; removed room AIs"));
                }
                var sl = s.T as ServerLink;
                if (ok && sl != null && sl.MpMax < mh.Mesh.MpMax) sl.MpMax = mh.Mesh.MpMax;
                if (ok && s.T is BridgeLink && MpCfg.Max < mh.Mesh.MpMax)
                {
                    MpCfg.Max = mh.Mesh.MpMax;
                    var blk = s.T as BridgeLink;
                    int cur = blk != null ? blk.MpCount + 1 : 1;
                    Bridge.SetHosting(true, MpMesh.Hosting(blk != null ? blk.Mesh : null, cur, MpCfg.Max, s.MpTag));
                }
                s.TouchSess();
                return ok;
            }
            if (cmd == "duelset")
            {
                int fa = 0, fb = 0;
                if (d.TryGetValue("fa", out v)) int.TryParse(v, out fa);
                if (d.TryGetValue("fb", out v)) int.TryParse(v, out fb);
                bool ok = mh.Mesh.DuelCmdSet(fa, fb);
                s.TouchSess();
                return ok;
            }
            if (cmd == "duelphase")
            {
                bool live = d.TryGetValue("live", out v) && v == "1";
                bool ok = mh.Mesh.DuelCmdPhase(live);
                s.TouchSess();
                return ok;
            }
            return false;
        }
        void MpTick(DateTime now)
        {
            var mh = T as IMpLink;
            if (mh == null || Role != "host" || MpCfg.Max < 2 || (now - lastMpInfo).TotalSeconds < 2.0) return;
            lastMpInfo = now;
            lock (sendLock) mh.SendMpInfo();
            // 桥接好友页的"人数/上限"以前只在对局状态（mptag）变时才更新，有人进出不变 → 好友看到的人数一直是开房时的 1。
            // 名单广播（2 秒一次）时顺带核对，变了才重发富状态
            var blk = T as BridgeLink;
            if (blk != null)
            {
                string h = MpMesh.Hosting(blk.Mesh, blk.MpCount + 1, MpCfg.Max, MpTag);
                if (h != lastBridgeHosting) { lastBridgeHosting = h; Bridge.SetHosting(true, h); }
            }
        }
        // session.txt 里给游戏的：mp=1 是多人会话，mpid= 我的编号（主机 1，客机等主机给，0 = 还没给），
        // mpn= 房间人数（连主机），mpmax= 上限，mpl= 名单（编号:昵称,…，不含主机）
        internal static bool IsLanOrLoopback(IPAddress a)
        {
            if (a == null) return false;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                if (b[0] == 10) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 100 && b[1] >= 64 && b[1] < 128) return true;
                if (b[0] == 169 && b[1] == 254) return true;
                return false;
            }
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (a.IsIPv6LinkLocal) return true;
                var b = a.GetAddressBytes();
                return (b[0] & 0xfe) == 0xfc;
            }
            return false;
        }
        public string VisibleMpHostAddr()
        {
            if (!(T is Link)) return "";
            var mh = T as IMpLink;
            if (mh == null || mh.Mesh == null) return "";
            if (!(MpJoin || (Role == "host" && MpCfg.Max >= 2))) return "";
            string raw = mh.LocalAddrForHost(mh.Mesh.HostPid);
            if (string.IsNullOrEmpty(raw)) return "";
            var ep = Addr.ParseEp(raw);
            if (ep == null) return "";
            bool typed = enteredPeer != null && ep.Equals(enteredPeer);
            if (Role != "host" && mh.Mesh.IsHostHidden() && !typed) return "";
            if (IsLanOrLoopback(ep.Address) || typed) return raw;
            return "";
        }
        string MpSessionLines()
        {
            var mh = T as IMpLink;
            if (mh == null || mh.Mesh == null) return "";
            var mesh = mh.MpStatsLines();
            int hostPid = mh.Mesh.HostPid;
            string hostAddr = VisibleMpHostAddr();
            string hostExtra = "\nmphost=" + hostPid + (!string.IsNullOrEmpty(hostAddr) ? "\nmphostaddr=" + hostAddr : "");
            string refExtra = mh.Mesh.RefusedWhy != null ? ("\nrefused=" + mh.Mesh.RefusedWhy + "\nrefusedmsg=" + mh.Mesh.RefusedMsg) : "";
            string aiCsv = mh.Mesh.AiPidsCsv();
            string aiExtra = "\nmpai=" + aiCsv;
            if (Role == "host" && MpCfg.Max >= 2)
            {
                var sb = new StringBuilder();
                var players = mh.MpPlayers();
                foreach (var p in players) sb.Append(p.Key).Append(':').Append(p.Value.Replace(",", " ").Replace(":", " ")).Append(',');
                return "\nmp=1\nmpid=" + mh.Mesh.MyPid + "\nmpn=" + (players.Count + 1) + "\nmpmax=" + MpCfg.Max + "\nmpl=" + sb.ToString().TrimEnd(',') + aiExtra + hostExtra + mesh + mh.Mesh.DuelLines() + refExtra;
            }
            if (MpJoin) return "\nmp=1\nmpid=" + mh.MyPid + "\nmpn=" + mh.MpSeen + "\nmpmax=0\nmpl=" + mh.MpRoster + aiExtra + hostExtra + mesh + mh.Mesh.DuelLines() + refExtra + (MpCfg.JoinWatch ? "\nmpwatch=1" : "");
            return "";
        }

        string PeerDiag
        {
            get
            {
                var sl = T as ServerLink;
                if (sl != null) return sl.PeerDiag;
                var lk = T as Link;
                return lk != null ? NetLog.Ip(lk.Peer) : "";
            }
        }

        // Session 发出的包都走这里：签名 → （测试时的坏网络）→ 传输
        void SendText(string body) { lock (sendLock) sim.Send(Wire.Seal(DzOut(body)), T.Send); }

        // 对战状态包省流（Transport.cs DzCfg / DzIn 的说明）：带上 dzv / dzk（主机再带 dzh），对方会拼、主机开着才编码
        SpecDelta dzOut;
        Func<string, bool, SpecDelta> dzOutHook;
        Action dzResetHook;
        public volatile bool DzActive;      // 给面板：这次连接实际在省流
        public volatile string DzWhy = "";  // lobby.txt dzwhy=：on / peer-old / host-off / host-old（游戏脚本变了就记日志）
        string dzWhy, dzPeer;
        string DzOut(string body)
        {
            if (body == null || body.StartsWith(WireTag.P, StringComparison.Ordinal) || body.IndexOf("seq=", StringComparison.Ordinal) < 0) return body;
            bool host = Role == "host";
            var ps = T.PeerStr ?? "";
            if (ps != dzPeer) { if (dzPeer != null) Wire.DzForget(); dzPeer = ps; }      // 换了对手：等它的完整包再说
            var full = body + "dzv=1\ndzk=" + Wire.DzKey + "\n" + (host ? "dzh=" + (DzCfg.On ? "1" : "0") + "\n" : "");
            bool on = Wire.PeerDz && (host ? DzCfg.On : Wire.PeerDzHost == 1);
            // 日志：开 / 关变了记一行（为什么没开：对方旧版 / 主机关了 / 主机旧版）
            var why = on ? "on" : !Wire.PeerDz ? (host ? "peer-old" : "host-old") : host ? "host-off" : Wire.PeerDzHost == 0 ? "host-off" : "host-old";
            // 不进面板状态行（会挤掉"XXX 加入了你的房间"这类提示，test_ai_room_lan 抓到的）：报给 lobby.txt dzwhy=，游戏脚本记进 mp_probe.log；
            // 局域网 / 输入 IP 另记 net_conn.log
            if (why != dzWhy)
            {
                dzWhy = why;
                DzWhy = why;
                NetLog.W("联机省流：" + why);
            }
            DzActive = on;
            if (Wire.DzReset) { Wire.DzReset = false; dzOut = null; if (dzOutHook != null) dzOutHook("", false); }      // 对方联机程序重启：从全量重来
            if (!on) { dzOut = null; if (dzOutHook != null) dzOutHook("", false); return full; }
            if (dzOutHook != null) dzOut = dzOutHook("", true);
            if (dzOut == null) dzOut = new SpecDelta();
            var enc = dzOut.Encode(full, 1, DzCfg.Now());
            return enc.IndexOf("dz=1\n", StringComparison.Ordinal) >= 0 ? enc + "dzk=" + Wire.DzKey + "\n" : enc;
        }

        // 主机下场时转给选手的包：收的那位会拼（它自己的包带 dzv=1）、主机开着才省流。key = 这一路的发送方编号
        readonly Dictionary<string, SpecDelta> relayDz = new Dictionary<string, SpecDelta>();
        volatile bool relayDzFirst, relayDzSecond;
        // 换人（主机下场 / 回来、补第二位、第二位顶上、打完换人）：新来的那位可能是旧版、也没有拼包的底 → 先发完整包，等他的包到了再算
        void RelayDzReset() { lock (sendLock) { relayDz.Clear(); relayDzFirst = relayDzSecond = false; if (dzResetHook != null) dzResetHook(); } }
        string RelayDz(string full, string key, bool peerDz)
        {
            var body = full + "dzv=1\ndzk=" + key + "\ndzh=" + (DzCfg.On ? "1" : "0") + "\n";     // 主机会拼（dzv）、主机的设置（dzh）：选手给主机发的包照样省流
            SpecDelta e;
            bool ok = DzCfg.On && peerDz;
            if (dzOutHook != null) e = dzOutHook(key, ok);
            else if (!ok) { relayDz.Remove(key); return body; }
            else if (!relayDz.TryGetValue(key, out e)) relayDz[key] = e = new SpecDelta();
            if (e == null) return body;
            var enc = e.Encode(body, 1, DzCfg.Now());
            return enc.IndexOf("dz=1\n", StringComparison.Ordinal) >= 0 ? enc + "dzk=" + key + "\n" : enc;
        }

        // 主机：一个状态包加上来源（h = 主机自己，c = 对手）签名后发给每个观战者；没有观战者不做
        // 主机和本地 AI 对打、同时开着等人的房间：AI 会话的两路包（主机 = h、AI = c）交给等人房间的观战者
        public volatile Session SpecRelay;
        // 这个会话是"AI 对战时等人的房间"：房间列表里多一行"观战 AI 对战"（局域网广播第 7 段 / 服务器 REG 第 14 段）
        public volatile bool AiWaiting;
        public void ForwardSpecFrom(string body, string src) { ForwardSpec(body, src); }

        readonly PeerTable fallbackPeers = new PeerTable();
        PeerTable Peers { get { var h = T as IPeerTableHost; return h != null ? h.Peers : fallbackPeers; } }

        // 每一路（h = 主机 / c = 对手）一个省流编码器（SpecDelta，存进 PeerTable）
        void ForwardSpec(string body, string src)
        {
            var relay = SpecRelay;
            if (relay != null && relay != this) { relay.ForwardSpecFrom(body, src); return; }
            var ss = T as ISpecSink;
            if (ss == null || ss.SpecCount == 0) return;
            lock (sendLock)
            {
                // 主机关了联机省流，或者有观战者是旧版（握手 / WATCH 没带 dz）：都发完整包（去掉旧的 src= 再加）；切回来时编码器从全量重来
                if (!DzCfg.On || !ss.SpecDzAll)
                {
                    Peers.ResetSpecStreamDz(src);
                    var sb = new StringBuilder();
                    foreach (var raw in body.Split('\n'))
                    {
                        var line = raw.TrimEnd('\r');
                        if (line.Length == 0 || line.StartsWith("src=")) continue;
                        sb.Append(line).Append('\n');
                    }
                    ss.SendSpecs(Wire.Seal(sb.Append("src=").Append(src).Append('\n').ToString()));
                    return;
                }
                ss.SendSpecs(Wire.Seal(Peers.EncodeSpecStreamDz(src, body, ss.SpecCount, DzCfg.Now()) + "src=" + src + "\n"));
            }
        }
        int QPos { get { var ss = T as ISpecSink; return ss != null && Role == "spec" && !SpecCfg.NoQueue ? ss.QPos : 0; } }
        int QLen
        {
            get
            {
                var q = T as IQueueHost;
                if (Role == "host") return q != null ? q.QueueOrder().Count : 0;
                var ss = T as ISpecSink;
                return ss != null ? ss.QLen : 0;
            }
        }
        public int Spectators
        {
            get
            {
                var ss = T as ISpecSink;
                return ss != null ? ss.SpecCount : 0;
            }
        }

        // 排队。主机：对手掉线 FillAfter 秒、有人排队 → 队首上场；游戏打完一场按房间规矩发 rotate → 对手下场排队尾、队首上场。
        // 换角色的 4NROLE 每 0.5 秒重发，直到对方照新角色发包（上场的发状态包 / 下场的发 spec=1 握手），最多 RoleRetry 秒
        public const double FillAfter = 4.0, RoleRetry = 6.0;
        volatile string wantRole;                   // 观战者 / 客机：主机让换成的角色（Run 线程里换）
        volatile bool wantMatchHost;
        public volatile bool MatchHost;
        public volatile string RoomRulesStr;
        DateTime lastHostSpecKeepalive;
        object roleTo, roleTo2;                     // 主机：正在通知上场的 / 下场的（IP 端点或 桥接连接句柄）
        DateTime roleAt, roleSentAt, lastQ;
        public volatile string QueueMsg = "";       // 给面板：最近一次换人
        long rxAtPromote;

        // ---- 主机下场：两位选手经主机互相转发，主机自己观战（游戏收到 src=h 第一位 / src=c 第二位）。
        // 转发给对面选手时序号改成主机自己的（两边看到的序号一直往上走），房间规矩字段换成主机的（两位都是客机，按"主机"的规矩打）。
        // 可靠事件（聊天 / 回合结果）不转：对局流程靠状态包里的 mt= / dead= / quit= 就能走
        public volatile bool RelayOn;
        volatile string relayReq;           // "down" / "up"：面板的"我也下场" / "我回来打"，Run 线程里做
        long secondRx, relaySeq = 100000;
        // ---- M4：解说房两位选手互相直连（局域网 / 输入 IP）。房主每 2 秒发一次介绍，丢包也能补上。
        string specIntroToken = "";
        DateTime specIntroAt = DateTime.MinValue;
        IPEndPoint specIntroP1, specIntroP2;
        void MeshIntroTick(DateTime now)
        {
            var lk = T as Link;
            if (lk == null || !HostSpecCfg.On || Role != "host" || !RelayOn) return;
            var p1 = lk.Peer;
            var p2 = lk.Peer2;
            if (p1 == null || p2 == null) return;
            if (specIntroP1 == null || specIntroP2 == null || !specIntroP1.Equals(p1) || !specIntroP2.Equals(p2) || specIntroToken.Length == 0)
            {
                specIntroP1 = p1;
                specIntroP2 = p2;
                specIntroToken = Guid.NewGuid().ToString("N").Substring(0, 10);
                specIntroAt = DateTime.MinValue;
            }
            if ((now - specIntroAt).TotalSeconds < 2.0) return;
            specIntroAt = now;
            lk.SendMeshIntro(p1, p2, specIntroToken, true);      // 第一位选手是对局主机
            lk.SendMeshIntro(p2, p1, specIntroToken, false);
        }
        readonly Dictionary<string, string> hostRules = new Dictionary<string, string>();
        static readonly string[] RuleKeys = { "bar", "bo", "dm", "ab", "ctr", "bal", "medok", "npos", "std", "cs", "gcd", "dgc", "sycd", "stg", "swk", "cfgh", "ult0", "sp0", "resm", "ultx", "acc", "accn", "accban", "wpnban", "edgeban", "tl", "dif", "rdyto" };

        public string FormatRoomRules()
        {
            var sb = new StringBuilder();
            lock (hostRules)
            {
                for (int i = 0; i < RuleKeys.Length; i++)
                {
                    var k = RuleKeys[i];
                    string v;
                    if (!hostRules.TryGetValue(k, out v))
                    {
                        if (k == "bar") v = "2";
                        else if (k == "bo") v = "3";
                        else if (k == "dm") v = "100";
                        else if (k == "ctr" || k == "medok" || k == "npos" || k == "cs" || k == "gcd" || k == "dgc" || k == "sycd" || k == "stg" || k == "dif") v = "1";
                        else if (k == "swk") { if (!hostRules.TryGetValue("bal", out v)) v = "0"; }     // 11-16 主机旧版没发 swk=：按它的武器平衡（旧版跟着 bal 走）
                        else if (k == "sp0" || k == "accn") v = "3";
                        else if (k == "ultx") v = "100";
                        else if (k == "tl") v = "180";
                        else if (k == "rdyto") v = "10";
                        else if (k == "accban") v = "AnimMovement,BreakRegen,MagicWeapon";
                        else if (k == "wpnban" || k == "edgeban") v = "-";
                        else v = "0";
                    }
                    if (i > 0) sb.Append(';');
                    sb.Append(k).Append('=').Append(v);
                }
            }
            return sb.ToString();
        }

        void SendRoomRulesAndRoles(object p2)
        {
            var rh = T as IRelayHost;
            var rulesPkt = Wire.Seal(WireTag.P + "RULES\n" + FormatRoomRules() + "\n");
            lock (sendLock)
            {
                T.Send(Wire.Seal(WireTag.P + "ROLE\nrole=client\nmatchhost=1\n"));
                T.Send(rulesPkt);
                if (rh != null && p2 != null)
                {
                    rh.SendSecond(Wire.Seal(WireTag.P + "ROLE\nrole=client\nmatchhost=0\n"));
                    rh.SendSecond(rulesPkt);
                }
            }
        }

        void HandlePeerEvent(PeerRow sender, string data)
        {
            if (string.IsNullOrEmpty(data) || Role != "host") return;
            if (data.StartsWith("chat "))
            {
                string senderNick = (sender != null && !string.IsNullOrEmpty(sender.Nick)) ? sender.Nick : "选手";
                string text = data.Substring(5);
                BroadcastChat(senderNick, text);
                return;
            }
            if (data.StartsWith("cfgreq"))
            {
                if (Rel != null && Rel.HasLocalIn) Rel.DeliverLocal(data);
                return;
            }
            if (RelayOn && sender != null)
            {
                if (sender == Peers.PeerRow && Peers.SecondRow != null && Peers.SecondRow.Rel != null)
                {
                    Peers.SecondRow.Rel.Send(data);
                }
                else if (sender == Peers.SecondRow && Peers.PeerRow != null && Peers.PeerRow.Rel != null)
                {
                    Peers.PeerRow.Rel.Send(data);
                }
            }
        }

        void EnsurePeerRel(PeerRow row)
        {
            if (row == null || row.Handle == null || row.Rel != null) return;
            var lk = T as Link;
            if (lk != null && row.Handle is IPEndPoint)
            {
                var ep = (IPEndPoint)row.Handle;
                row.Rel = new RelChannel(b => { try { var bytes = Wire.Seal(b); lk.Udp.Send(bytes, bytes.Length, ep); } catch (Exception) { } });
                row.Rel.OnDeliver = ev => HandlePeerEvent(row, ev);
                return;
            }
            var sb = T as BridgeLink;
            if (sb != null && row.Handle is uint)
            {
                var h = (uint)row.Handle;
                row.Rel = new RelChannel(b => Bridge.SendTo(h, Wire.Seal(b), 1));
                row.Rel.OnDeliver = ev => HandlePeerEvent(row, ev);
                return;
            }
        }

        public void BroadcastChat(string nick, string text, int exceptPid = 0, int fromPid = 0)
        {
            // 对决控制消息（新版 1v1 借房内聊天的可靠通道）：房主在这里盖上发送人的真编号（@duel@<编号>:），
            // 游戏按编号认房主 / 本人；别人自己写的 @duel@ 一律丢（冒充）
            if (text != null && text.StartsWith("@duel@", StringComparison.Ordinal)) return;
            if (text != null && text.StartsWith("@duel:", StringComparison.Ordinal))
            {
                int who = fromPid > 0 ? fromPid : exceptPid;
                if (who <= 0) return;
                text = "@duel@" + who + ":" + text.Substring(6);
            }
            string msg = "chatn " + nick + "|" + text;
            if (Peers.PeerRow != null && Peers.PeerRow.Rel != null)
                Peers.PeerRow.Rel.Send(msg);
            else if (Rel != null)
                Rel.Send(msg);
            if (Peers.SecondRow != null)
            {
                if (Peers.SecondRow.Rel == null) EnsurePeerRel(Peers.SecondRow);
                if (Peers.SecondRow.Rel != null) Peers.SecondRow.Rel.Send(msg);
            }
            var specs = Peers.SpecRows;
            if (specs != null)
            {
                for (int i = 0; i < specs.Count; i++)
                {
                    var sp = specs[i];
                    if (sp != null)
                    {
                        if (sp.Rel == null) EnsurePeerRel(sp);
                        if (sp.Rel != null) sp.Rel.Send(msg);
                    }
                }
            }
            if (Rel != null && Rel.HasLocalIn)
                Rel.DeliverLocal(msg);
            // M3 多人房间：房主把聊天转给每位玩家（每对一条可靠通道，直连 + 中转双发，接收端按事件去重）
            var mlk = T as IMpLink;
            if (mlk != null && mlk.Mesh != null && Role == "host")
                mlk.Mesh.HostRelAll(msg, exceptPid);
        }

        public bool HostDown() { if (Role != "host" || !(T is IRelayHost)) return false; relayReq = "down"; return true; }
        public bool HostUp() { if (Role != "host" || !RelayOn) return false; relayReq = "up"; return true; }

        void RelayForward(string body, bool fromFirst)
        {
            var rh = T as IRelayHost;
            if (rh == null) return;
            var side = fromFirst ? "h" : "c";
            ForwardSpec(body, side);
            // 普通"主机下场"：转发给对面时序号换成主机自己的（relaySeq 从 100000 往上）。
            // 换人后新选手的 seq 从头数，照原样转过去对面会看到序号倒退（联机程序把 100 以内倒退当旧包丢掉，
            // 游戏脚本把大幅倒退当对方重启）。只有解说模式（两位选手之间还有一条直连，两份包同一个 seq 要去重）
            // 和全互连（M3）才保留原 seq；M4 之前无条件保留是个回归， 修。
            var lkSeq = T as Link;
            bool keepSeq = HostSpecCfg.On || (lkSeq != null && lkSeq.SpecMeshUp);
            var strip = new StringBuilder();
            foreach (var raw in body.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("src=")) continue;
                if (!keepSeq && line.StartsWith("seq=")) continue;      // 下面统一追加主机自己的序号
                strip.Append(line).Append('\n');
            }
            var plain = strip.ToString();
            T.Out.Push(plain + "src=" + side + "\n");        // 主机自己的游戏：观战
            var sb = new StringBuilder();
            foreach (var raw in plain.Split('\n'))
            {
                if (raw.Length == 0) continue;
                int eq = raw.IndexOf('=');
                var k = eq > 0 ? raw.Substring(0, eq) : raw;
                if (k == "dzv" || k == "dzk" || k == "dzh") continue;     // 转发的不带原发送方的省流标记（转出去的是完整包）
                string hv;
                lock (hostRules) if (hostRules.TryGetValue(k, out hv)) { sb.Append(k).Append('=').Append(hv).Append('\n'); continue; }
                sb.Append(raw).Append('\n');
            }
            // 解说模式 / 直连在用时保留原 seq（见上）；普通主机下场换成主机自己的序号流
            if (!keepSeq) sb.Append("seq=").Append(System.Threading.Interlocked.Increment(ref relaySeq)).Append('\n');
            // （移植 0.33 分支）普通"主机下场"两位都是客机，以前谁都不开局：给第一位带 rly=h，它代主机开局 / 判回合；第二位 rly=c。
            // 解说模式不带：那边 4NROLE matchhost=1 已经让第一位当对局主机，且两位之间有直连、直连的包没有 rly
            if (!HostSpecCfg.On) sb.Append("rly=").Append(fromFirst ? "c" : "h").Append('\n');
            // 这位选手会不会拼省流包（看他自己发来的包），转给对面那位时按对面那位的
            bool dzv = body.IndexOf("\ndzv=1", StringComparison.Ordinal) >= 0 || body.StartsWith("dzv=1", StringComparison.Ordinal);
            Peers.SetFighterDz(fromFirst, dzv);
            if (fromFirst) relayDzFirst = dzv; else relayDzSecond = dzv;
            lock (sendLock)
            {
                var b = Wire.Seal(RelayDz(sb.ToString(), Wire.DzKey + (fromFirst ? "a" : "b"), Peers.GetFighterDz(!fromFirst)));
                if (fromFirst) rh.SendSecond(b); else T.Send(b);
            }
        }

        void RelayTick(DateTime now)
        {
            var rh = T as IRelayHost;
            var q = T as IQueueHost;
            if (rh == null || q == null) return;
            if (HostSpecCfg.On && Role == "host")
            {
                MeshIntroTick(now);
                rh.NoOut = true;
                if (!RelayOn)
                {
                    if (!q.HasPeer)
                    {
                        QueueMsg = L.T("等待选手加入", "Waiting for players to join");
                    }
                    else if (q.CanPromote)
                    {
                        var p2 = rh.PromoteSecond();
                        if (p2 != null)
                        {
                            RelayOn = true;
                            RelayDzReset();
                            roleTo = p2; roleTo2 = null; roleAt = now; roleSentAt = DateTime.MinValue; rxAtPromote = RxSeen + secondRx;
                            QueueMsg = L.T("两位选手就位，比赛开始", "Two players ready, match starts");
                            SendRoomRulesAndRoles(p2);
                        }
                    }
                    else
                    {
                        QueueMsg = L.T("等第二位选手", "Waiting for second player");
                        if ((now - lastHostSpecKeepalive).TotalSeconds >= 0.5)
                        {
                            lastHostSpecKeepalive = now;
                            lock (sendLock)
                            {
                                T.Send(Wire.Seal("v=2\nseq=" + System.Threading.Interlocked.Increment(ref relaySeq) + "\nwait=1\n"));
                            }
                        }
                    }
                    return;
                }
                if (roleTo == null && (!rh.HasSecond || rh.SinceFromSecond > FillAfter))
                {
                    var p2 = q.CanPromote ? rh.PromoteSecond() : null;
                    RelayDzReset();
                    if (p2 != null)
                    {
                        roleTo = p2; roleAt = now; roleSentAt = DateTime.MinValue; rxAtPromote = RxSeen + secondRx;
                        QueueMsg = L.T("第二位走了，队首补上", "Second player left; next in queue");
                        SendRoomRulesAndRoles(p2);
                    }
                    else
                    {
                        RelayOn = false;
                        QueueMsg = L.T("第二位走了，等第二位选手", "Second player left; waiting for second player");
                    }
                    return;
                }
                if (roleTo == null && (!q.HasPeer || q.SinceFromPeer > FillAfter))
                {
                    if (rh.HasSecond)
                    {
                        rh.SecondToPeer();
                        RelayDzReset();
                        var p2 = q.CanPromote ? rh.PromoteSecond() : null;
                        if (p2 != null)
                        {
                            roleTo = p2; roleAt = now; roleSentAt = DateTime.MinValue; rxAtPromote = RxSeen + secondRx;
                            QueueMsg = L.T("第一位走了，第二位接任，队首补上", "First player left; second takes over, next in queue");
                            SendRoomRulesAndRoles(p2);
                        }
                        else
                        {
                            RelayOn = false;
                            QueueMsg = L.T("第一位走了，等第二位选手", "First player left; waiting for second player");
                        }
                    }
                    return;
                }
                return;
            }
            var req = relayReq;
            relayReq = null;
            if (req == "down" && !RelayOn)
            {
                if (!q.HasPeer || !q.CanPromote) { QueueMsg = L.T("要有对手、也要有人排队才能下场", "Need an opponent and someone waiting"); return; }
                var p2 = rh.PromoteSecond();
                if (p2 == null) return;
                rh.NoOut = true;
                RelayOn = true;
                RelayDzReset();
                roleTo = p2; roleTo2 = null; roleAt = now; roleSentAt = DateTime.MinValue; rxAtPromote = RxSeen + secondRx;
                QueueMsg = L.T("主机下场，两位对打", "Host stepped down; two players fight");
                if (diag) NetLog.W("主机下场：两位选手经主机转发");
                return;
            }
            if (req == "up" && RelayOn)
            {
                var old = rh.DemoteSecond();
                rh.NoOut = false;
                RelayOn = false;
                RelayDzReset();
                roleTo = null; roleTo2 = old; roleAt = now; roleSentAt = DateTime.MinValue;
                QueueMsg = L.T("主机回来打", "Host is back");
                if (diag) NetLog.W("主机回来打：第二位选手下场");
                return;
            }
            if (!RelayOn) return;
            // 第二位走了：有人排队就补，没人就结束转发（主机回来跟第一位打）
            if (roleTo == null && (!rh.HasSecond || rh.SinceFromSecond > FillAfter))
            {
                var p2 = q.CanPromote ? rh.PromoteSecond() : null;
                RelayDzReset();
                if (p2 != null) { roleTo = p2; roleAt = now; roleSentAt = DateTime.MinValue; rxAtPromote = RxSeen + secondRx; QueueMsg = L.T("第二位走了，队首补上", "Second player left; next in queue"); }
                else { rh.NoOut = false; RelayOn = false; QueueMsg = L.T("第二位走了，主机回来打", "Second player left; host is back"); }
                return;
            }
            // 第一位走了、没人补：第二位改当第一位，主机回来跟他打
            if (roleTo == null && (!q.HasPeer || q.SinceFromPeer > FillAfter) && !q.CanPromote && rh.HasSecond)
            {
                rh.SecondToPeer();
                RelayDzReset();
                rh.NoOut = false;
                RelayOn = false;
                QueueMsg = L.T("第一位走了，主机回来跟第二位打", "First player left; host plays the second");
            }
        }

        // 打完换人：只记下请求，由 Run 线程的 QueueTick 做（和"对手走了补位"在同一个线程，不会两边同时换）
        volatile string rotateWhy, rotateWho;
        public bool Rotate(string why, string who)
        {
            rotateWho = who;
            return Rotate(why);
        }
        public bool Rotate(string why)
        {
            var lk = T as IQueueHost;
            if (lk == null || Role != "host") return false;
            if (!lk.CanPromote) { QueueMsg = L.T("没人排队，不换", "Nobody is waiting"); return false; }
            rotateWhy = why ?? "";
            return true;
        }

        void DoRotate(IQueueHost lk, string why)
        {
            if (!lk.CanPromote) { QueueMsg = L.T("没人排队，不换", "Nobody is waiting"); return; }
            var who = rotateWho;
            rotateWho = null;
            var rh = T as IRelayHost;
            object old, head;
            if (HostSpecCfg.On && Role == "host" && RelayOn && rh != null)
            {
                if (who == "c")
                {
                    old = rh.DemoteSecond();
                    head = rh.PromoteSecond();
                }
                else
                {
                    old = lk.DemotePeer();
                    rh.SecondToPeer();
                    head = rh.PromoteSecond();
                }
                RelayDzReset();
                roleTo2 = old;
                roleTo = head;
                roleAt = DateTime.UtcNow;
                roleSentAt = DateTime.MinValue;
                rxAtPromote = RxSeen + secondRx;
                QueueMsg = L.T("打完换人（", "Rotated (") + why + "）";
                if (diag) NetLog.W("排队：打完换人（" + why + "）");
                SendRoomRulesAndRoles(head);
                return;
            }
            if (RelayOn && rh != null && who == "c") { old = rh.DemoteSecond(); head = rh.PromoteSecond(); }     // 第二位输了
            else { old = lk.DemotePeer(); head = lk.PromoteHead(); }
            RelayDzReset();
            roleTo2 = old;
            roleTo = head;
            roleAt = DateTime.UtcNow;
            roleSentAt = DateTime.MinValue;
            rxAtPromote = RxSeen + secondRx;
            QueueMsg = L.T("打完换人（", "Rotated (") + why + "）";
            if (diag) NetLog.W("排队：打完换人（" + why + "）");
        }

        // 对手自己下去观战（面板"我下去观战"）：本机换成观战者、发观战握手，主机见到就让下一位上场
        public bool StepDown()
        {
            if (Role != "client" || !(T is ISpecSink)) return false;
            SpecCfg.NoQueue = true;     // 自己下去 = 观战开关切成"只观战"，想再打自己切回"排队"（房主换人 / 补位不碰这个开关）
            wantRole = "spec";
            return true;
        }

        void QueueTick(DateTime now)
        {
            var lk = T as IQueueHost;
            var ss = T as ISpecSink;
            if (Role == "host")
            {
                if (lk == null) return;
                var rw = rotateWhy;
                if (rw != null) { rotateWhy = null; DoRotate(lk, rw); }
                RelayTick(now);
                // M4：解说房直连的握手重发 / 掉线判定（房主侧不会收到介绍，no-op）
                var lkMeshTick = T as Link;
                if (lkMeshTick != null) lkMeshTick.MeshTick(now);
                // 对手走了（连接断了，或 FillAfter 秒没包）→ 队首上场。从来没有过对手（RxSeen = 0）不补：空房间先来的观战者只是来看
                // 例外：AI 开房等真人（AiWaiting）时主机在和 AI 打，排队的人就是来打的 → 马上叫队首上场，连进来 CheckWait 停 AI 转正
                //（以前 AI 房间里排队永远轮不到：打完换人的命令发给 AI 那条连接，那边没人排队）
                if ((RxSeen > 0 || AiWaiting) && roleTo == null && (!lk.HasPeer || lk.SinceFromPeer > FillAfter) && lk.CanPromote)
                {
                    var head = lk.PromoteHead();
                    if (head != null)
                    {
                        roleTo = head; roleTo2 = null;
                        roleAt = now; roleSentAt = DateTime.MinValue; rxAtPromote = RxSeen;
                        QueueMsg = L.T("对手走了，队首上场", "Opponent left, next in queue plays");
                    }
                }
                if (roleTo != null || roleTo2 != null)
                {
                    bool upDone = roleTo == null || RxSeen + secondRx > rxAtPromote;   // 新对手开始发状态包了
                    bool downDone = roleTo2 == null || (now - roleAt).TotalSeconds > 3.0;   // 下场通知连发 3 秒（对方收到后不再变）
                    if ((upDone && downDone) || (now - roleAt).TotalSeconds > RoleRetry) { roleTo = null; roleTo2 = null; }
                    else if ((now - roleSentAt).TotalSeconds > 0.5)
                    {
                        roleSentAt = now;
                        lock (sendLock)
                        {
                            if (roleTo != null && !upDone) lk.SendTo(roleTo, Wire.Seal(WireTag.P + "ROLE\nrole=client\n"));
                            if (roleTo2 != null) lk.SendTo(roleTo2, Wire.Seal(WireTag.P + "ROLE\nrole=spec\n"));
                        }
                    }
                }
                // 每 2 秒告诉每个观战者排第几
                if ((now - lastQ).TotalSeconds > 2.0)
                {
                    lastQ = now;
                    var q = lk.QueueOrder();
                    lock (sendLock)
                        for (int i = 0; i < q.Count; i++) lk.SendTo(q[i], Wire.Seal(WireTag.P + "Q\npos=" + (i + 1) + "\nn=" + q.Count + "\n"));
                }
                return;
            }
            var w = wantRole;
            if (w != null)
            {
                wantRole = null;
                // 只观战（不排队）的人：主机 / 服务器叫上场一律不理（刚切成只观战、上场通知已经在路上的情况），主机过几秒会换下一个
                if (w == "client" && Role == "spec" && SpecCfg.NoQueue)
                {
                    Log(L.T("只观战：不上场", "Watch only: not going up"));
                    if (diag) NetLog.W("排队：只观战，主机叫上场不理");
                    w = null;
                }
                if (w != null && (w != Role || wantMatchHost != MatchHost) && (w == "client" || w == "spec"))
                {
                    Log(L.T("主机让你换成：", "Host switched you to: ") + w + (wantMatchHost ? " (对局主机)" : ""));
                    if (diag) NetLog.W("排队：主机让本机换成 " + w + (wantMatchHost ? " (对局主机)" : ""));
                    role = w;
                    MatchHost = wantMatchHost;
                    if (ss != null) ss.SpecMode = w == "spec";
                    QueueMsg = w == "client" ? L.T("轮到你上场了", "Your turn to play") : L.T("你下场观战了", "You are spectating now");
                }
            }
            var lkMesh = T as Link;
            if (lkMesh != null) lkMesh.MeshTick(now);
        }

        public bool Connected { get { return (DateTime.UtcNow - lastRx).TotalSeconds < 2.0; } }
        // 对方在：收到状态包，或 2 秒内握过手。等人的房间读不到游戏的包、一个状态包都不发，
        // 要先收到主机的包才发自己状态的客户端（tests\fake_remote.py）只会一直握手——只认状态包就两边干等
        public bool PeerSeen { get { return Connected || Wire.SinceHello < 2.0; } }

        // AI 对战时等人的房间（文件在 net\wait\）有真人连进来 → 换到 net\ 当正式联机。连接本身不断：
        // 房间号 / 端口 / 桥接连接都不变，对方不用重连。Run 线程下一轮（≤4ms）生效
        public void Retarget(string dir) { retargetTo = dir; }
        public string PeerStr { get { return T.PeerStr; } }

        public Session Start()
        {
            T.Log = Log;
            Rel.Log = Log;
            dzOutHook = (key, enable) => Peers.EnsureFighterDzOut(key.EndsWith("a", StringComparison.Ordinal), enable);
            dzResetHook = () => Peers.ResetRelayDz();
            startedAt = DateTime.UtcNow;
            if (diag)
            {
                var sl = T as ServerLink;
                var lk = T as Link;
                NetLog.W("会话开始：角色 " + Role + "，方式 " + Mode + "，联机程序 " + NetLog.Build + " v" + Proto.App + "，本地端口 " + T.LocalPort
                         + "，本机 " + NetLog.Ip(Addr.PrimaryLocal()) + "，联机密码 " + (Wire.Signed ? "有" : "无")
                         + (sl != null ? "，大厅服务器 " + sl.ServerDiag + "，允许中转 " + (sl.AllowRelay ? "是" : "否")
                                         + (Role == "client" ? "，加入房间 " + sl.RoomId : "") + (sl.RankPid != null ? "，排位" : "") : "")
                         + (lk != null && Role == "client" ? "，目标 " + NetLog.Ip(lk.Peer) : "")
                         + (NetBind.Loopback ? "，只绑回环（测试）" : ""));
            }
            thread = Safe.Thread("session-recv", Run);
            thread.Start();
            sendThread = Safe.Thread("session-send", SendLoop);     // 观战者也开（轮到上场就要发），观战时不发
            sendThread.Start();
            return this;
        }

        public void Stop()
        {
            StopAllMpAi();
            var mh = T as IMpLink;
            if (mh != null && mh.Mesh != null && mh.Mesh.MyPid > 0)     // 成员也告别（只告诉房主）
            {
                mh.Mesh.BroadcastBye();
                try { Thread.Sleep(30); } catch (Exception) { }
            }
            stop = true;
            if (thread != null) thread.Join(2000);
            if (sendThread != null) sendThread.Join(2000);
            if (mh != null && mh.Mesh != null) mh.Mesh.CloseBridge();
            try { T.Close(); } catch (Exception) { }
            if (diag) NetLog.W("会话结束（" + Role + "，" + Mode + "）：共收状态包 " + RxSeen + "，发包 " + T.Tx + "，"
                               + (lastRx == DateTime.MinValue ? "一个状态包都没收到" : "最后收包在 " + Kv.F((DateTime.UtcNow - lastRx).TotalSeconds, "0.0") + " 秒前")
                               + "，被拒 " + Wire.Rejected);
        }

        void Run()
        {
            DateTime lastBcast = DateTime.MinValue, lastHello = DateTime.MinValue;
            UdpClient bsock = null;
            while (!stop)
            {
                var now = DateTime.UtcNow;
                var rt = retargetTo;
                if (rt != null)
                {
                    retargetTo = null;
                    fLocal = Path.Combine(rt, "state_local.txt");
                    fSession = Path.Combine(rt, "session.txt");
                    T.Out.Retarget(Path.Combine(rt, "state_remote.txt"));     // 管道也跟着换：net\ 有、net\wait\ 没有
                    Rel.Retarget(rt);
                    lastSess = DateTime.MinValue;     // session.txt 马上写一份（role / peer / mode 都换了）
                }
                Rel.PollLua();
                // 观战者一直每秒发一次握手（带 spec=1）当保活：主机 6 秒没收到就当它走了
                if (Role == "spec" && (now - lastHello).TotalSeconds > 1.0)
                {
                    lastHello = now;
                    SendText(WireTag.P + "HELLO\npv=" + Proto.Version + "\nav=" + Proto.App + "\nnm=" + Name.Replace("\r", " ").Replace("\n", " ") + "\nspec=1\n"
                             + (SpecCfg.NoQueue ? "noq=1\n" : "") + (SpecCfg.Up ? "up=1\n" : "") + "dzv=1\n" + Ident.Lines());     // 我会拼省流观战包
                }
                // 客机没连上之前每 0.5 秒发一次握手：主机就算游戏还没读档，也能先知道往哪回
                // 多人混战的客机：一直每秒握手一次（带 mp=1）当保活——游戏还没读完档、不发状态包时主机也不会把编号收回去
                bool mpJoin = MpJoin;
                var lkRe = T as Link;
                if (lkRe != null && lkRe.RehelloNow) { lkRe.RehelloNow = false; lastHello = DateTime.MinValue; }
                if (Role == "client" && (!Connected || mpJoin) && (now - lastHello).TotalSeconds > (Connected ? 1.0 : 0.5))
                {
                    lastHello = now;
                    // 带上昵称：AI 对战时等人的房间可能只靠握手就转正（还没收到状态包里的 nm=）
                    var lkHello = T as Link;
                    SendText(WireTag.P + "HELLO\npv=" + Proto.Version + "\nav=" + Proto.App + "\nnm=" + Name.Replace("\r", " ").Replace("\n", " ") + "\n"
                             + (mpJoin ? "mp=1\n" + (lkHello != null && lkHello.LocalLanEndpoint.Length > 0 ? "lan=" + lkHello.LocalLanEndpoint + "\n" : "") + (MpMesh.HideIpPref ? "hideip=1\n" : "") + (MpCfg.JoinWatch ? "watch=1\n" : "") + MpMesh.V6HelloLine(T.LocalPort) : "")     // v6=
                             + Ident.Lines());     // uid= / sh=
                }
                int got = T.Pump();
                if (got > 0)
                {
                    lastRx = now;
                    RxSeen += got;
                }
                var kl = T as ServerLink;          // 刚按拉黑放掉了对手：马上算断开（收包线程里做，不和上面抢 lastRx）
                if (kl != null && kl.Kicked) { kl.Kicked = false; lastRx = DateTime.MinValue; }
                if (kl != null && kl.ServerRoomMp) MpJoin = true;      // 服务器自己开的多人房：知道是多人就按多人会话写 session.txt
                QueueTick(now);
                MpTick(now);
                Rel.Tick(now);
                var mlk = T as IMpLink;
                if (mlk != null && mlk.Mesh != null)
                {
                    if (mlk.Mesh.Verify == null) mlk.Mesh.Verify = b => Wire.Verify(b, 0, b.Length, null);
                    int bgot = mlk.Mesh.PumpBridge();
                    if (bgot > 0)
                    {
                        lastRx = now;
                        RxSeen += bgot;
                        if (T.Out != null) T.Out.Flush();
                    }
                    mlk.Mesh.Tick(now);
                }
                if (Peers.SecondRow != null && Peers.SecondRow.Rel != null)
                    Peers.SecondRow.Rel.Tick(now);
                var specTickList = Peers.SpecRows;
                if (specTickList != null)
                {
                    for (int i = 0; i < specTickList.Count; i++)
                    {
                        var sp = specTickList[i];
                        if (sp != null && sp.Rel != null) sp.Rel.Tick(now);
                    }
                }
                lock (sendLock) sim.Flush(T.Send);
                if (broadcast && bsock == null)
                {
                    try { bsock = new UdpClient(new IPEndPoint(NetBind.Addr, 0)); bsock.EnableBroadcast = true; } catch (Exception) { bsock = null; }
                    if (bsock != null) { lastBcast = DateTime.MinValue; bcastReset = false; }
                }
                else if (!broadcast && bsock != null)
                {
                    try { bsock.Close(); } catch (Exception) { }
                    bsock = null;
                }
                if (bcastReset) { lastBcast = DateTime.MinValue; bcastReset = false; }
                if (bsock != null && (now - lastBcast).TotalSeconds > 1.0)
                {
                    lastBcast = now;
                    bool isMp = MpCfg.Max >= 2;
                    var lkB = T as Link;
                    int mpCur = isMp ? MpMesh.Listed(lkB != null ? lkB.Mesh : null, (lkB != null ? lkB.MpPlayers().Count : 0) + 1) : 0;     // 对局外不算 AI
                    bool full = isMp ? (mpCur >= MpCfg.Max) : Connected;
                    bool watchOk = !isMp && SpecCfg.Max > 0;
                    string mpSeg = isMp ? ("|" + mpCur + "/" + MpCfg.Max + ";" + ((MpTag ?? "m=elim;tm=0;st=idle") + MpMesh.ListedTag(lkB != null ? lkB.Mesh : null)).Replace("|", " ").Replace("\r", "").Replace("\n", "")) : "";
                    var msg = WireTag.P + "ROOM|" + Name.Replace("|", " ") + "|" + T.LocalPort + "|" + LocalMap + "|" + (full ? "1" : "0") + (watchOk ? "|1" : "|0") + (AiWaiting ? "|1" : "|0") + "|" + Ident.MyRoomHash + mpSeg;
                    var b = Kv.Utf8.GetBytes(msg);
                    // Windows 发 255.255.255.255 只走一张网卡（实测走了 VMware 虚拟网卡，WLAN 上的朋友收不到），
                    // 所以再往每张网卡自己的子网广播地址各发一份
                    foreach (var dst in BroadcastTargets())
                        try { bsock.Send(b, b.Length, new IPEndPoint(dst, BroadcastPort)); } catch (Exception) { }
                    // Tailscale 不转发广播（ZeroTier 转发，上面的子网广播就够了）：单播给每个在线节点。没装 Tailscale = 空表
                    if (Mode == "lan" && !NetBind.Loopback)
                        foreach (var pr in Tailscale.Peers())
                        {
                            IPAddress ip;
                            if (IPAddress.TryParse(pr[0], out ip))
                                try { bsock.Send(b, b.Length, new IPEndPoint(ip, BroadcastPort)); } catch (Exception) { }
                        }
                }
                if ((now - lastSess).TotalSeconds > 0.5)
                {
                    lastSess = now;
                    if ((Mode == "bridge" || T is BridgeLink) && Role == "host" && MpCfg.Max >= 2)
                    {
                        var blk = T as BridgeLink;
                        int curH = blk != null ? blk.MpCount + 1 : 1;
                        Bridge.SetHosting(true, MpMesh.Hosting(blk != null ? blk.Mesh : null, curH, MpCfg.Max, MpTag));
                    }
                    if (diag) DiagTick(now);
                    // v0.22.0：签名 / 拒收 / 可靠事件的统计（窗口状态、测试读这里；Lua 只用前几个键）
                    GamePipe.Out(fSession, "role=" + Role + "\nconnected=" + (Connected ? 1 : 0) + "\npeer=" + PeerStr + "\nspecs=" + Spectators + "\nrelay=" + (RelayOn ? 1 : 0) + "\nqpos=" + QPos + "\nqlen=" + QLen + "\nqmsg=" + QueueMsg
                                             + "\nmode=" + Mode + "\nt=" + Kv.F(Link.UnixNow(), "0.0")
                                             + "\nsig=" + (Wire.Signed ? 1 : 0) + "\nrej=" + Wire.Rejected + "\nrejbad=" + Wire.RejBad
                                             + "\nrejtime=" + Wire.RejTime + "\nrejunsigned=" + Wire.RejUnsigned + "\nrejsigned=" + Wire.RejSigned
                                             + "\nhint=" + Wire.Hint() + "\npeerapp=" + (Wire.PeerApp ?? "") + "\npeerproto=" + Wire.PeerProto
                                             + "\nrelq=" + Rel.Pending + "\nreltaken=" + Rel.Taken + "\nrelsent=" + Rel.Sent + "\nrelretx=" + Rel.Retx
                                             + "\nrelacked=" + Rel.Acked + "\nrelexpired=" + Rel.Expired + "\nreldelivered=" + Rel.Delivered
                                             + "\nreldup=" + Rel.Dup + "\nrelooo=" + Rel.OutOfOrder + "\nrelskipped=" + Rel.Skipped
                                             + "\nsimdrop=" + sim.Dropped + "\nsimdelay=" + sim.Delayed
                                             + "\nhostspec=" + (HostSpecCfg.On ? 1 : 0)
                                             + "\nmatchhost=" + (MatchHost ? 1 : 0)
                                             + (!string.IsNullOrEmpty(RoomRulesStr) ? "\nroomrules=" + RoomRulesStr : (Role == "host" ? "\nroomrules=" + FormatRoomRules() : ""))
                                             + MeshSessionLines()
                                             + MpSessionLines() + "\n");
                }
                // 15ms → 4ms（配合 WinTimer 的 1ms 精度），一起到的包靠这个间隔各写一次 state_remote.txt
                Thread.Sleep(PollMs);
                Loops++;
            }
            if (bsock != null) bsock.Close();
        }

        string MeshSessionLines()
        {
            var p = Peers.PeerRow;
            if (p == null) return "";
            var sb = new StringBuilder();
            if (p.DirectPing >= 0) sb.Append("\ndirect_ping=").Append(Kv.F(p.DirectPing, "0.0"));
            if (p.DirectLoss > 0) sb.Append("\ndirect_loss=").Append(Kv.F(p.DirectLoss, "0.00"));
            if (p.DirectMaxLoss > 0) sb.Append("\ndirect_maxloss=").Append(p.DirectMaxLoss);
            if (p.ViaPing >= 0) sb.Append("\nvia_ping=").Append(Kv.F(p.ViaPing, "0.0"));
            if (p.ViaLoss > 0) sb.Append("\nvia_loss=").Append(Kv.F(p.ViaLoss, "0.00"));
            if (p.ViaMaxLoss > 0) sb.Append("\nvia_maxloss=").Append(p.ViaMaxLoss);
            var lk = T as Link;      // M4：解说房两位选手直连的状态（smup=1 直连在用；smrx=直连收到的状态包数）
            if (lk != null && lk.SpecMeshPresent)
            {
                sb.Append("\nsmup=").Append(lk.SpecMeshUp ? 1 : 0);
                sb.Append("\nsmrx=").Append(lk.SpecMeshRx);
                sb.Append("\nsmtx=").Append(lk.SpecMeshTx);
            }
            return sb.ToString();
        }

        // 每 0.5 秒看一次连接状态，有变化才写一行
        void DiagTick(DateTime now)
        {
            if (Wire.SinceHello < 1.0 && !helloLogged)
            {
                helloLogged = true;
                var pa = Wire.PeerApp;
                NetLog.W("收到对方握手（HELLO）：协议 " + Wire.PeerProto + "，对方 v" + (pa ?? "?") + (pa != null && pa != Proto.App ? "（本机 v" + Proto.App + "，不一样）" : "")
                         + "，来自 " + PeerDiag);
            }
            var app = Wire.PeerApp;
            if (app != null && app != lastPeerApp)
            {
                if (lastPeerApp != null) NetLog.W("对方版本变成 v" + app);
                lastPeerApp = app;
            }
            bool c = Connected;
            if (c && !wasConn)
            {
                NetLog.W("连上：收到对方状态包，对方 " + PeerDiag + "，对方 v" + (app ?? "?") + " 协议 " + Wire.PeerProto
                         + (lostAt == DateTime.MinValue ? "（会话开始后 " + Kv.F((now - startedAt).TotalSeconds, "0.0") + " 秒）"
                                                        : "（中断 " + Kv.F((now - lostAt).TotalSeconds, "0.0") + " 秒后恢复）"));
                connAt = now;
            }
            else if (!c && wasConn)
            {
                lostAt = now;
                helloLogged = false;
                NetLog.W("断开：2 秒没收到对方状态包（连着 " + Kv.F((now - connAt).TotalSeconds, "0.0") + " 秒，共收 " + RxSeen + " 个）");
            }
            wasConn = c;
            var h = Wire.Hint();
            if (h != lastHint)
            {
                if (h.Length > 0) NetLog.W("提示：" + h + "（被拒 " + Wire.Rejected + "）");
                lastHint = h;
            }
        }

        // 读 state_local.txt 发出去单独一个线程，每 4ms 一次。模组两次写有时只隔 ~17ms，
        // 原来 Sleep(15)（实际 15.6 / 31ms）会漏读；刚被替换的文件第一次打开要等杀毒扫描（实测常见 ~20ms），
        // 和收包、写 state_remote.txt 放在同一个循环里会互相拖慢
        void SendLoop()
        {
            string lastSeq = null, lastText = null, lastMpmSt = null;
            var pipeFor = fLocal;
            var pipe = GamePipe.For(Path.GetDirectoryName(fLocal));
            while (!stop)
            {
                // 等人的房间转正 / 退回等人换了目录：管道跟着换（只有 net\ 有）
                var fl = fLocal;
                if (!ReferenceEquals(fl, pipeFor)) { pipeFor = fl; pipe = GamePipe.For(Path.GetDirectoryName(fl)); }
                bool viaPipe = pipe != null && pipe.Connected;
                if (Role == "spec") { Thread.Sleep(PollMs * 5); SendLoops++; continue; }     // 观战者不发自己的状态包
                var text = viaPipe ? pipe.LatestLocal : Kv.ReadText(fl);
                if (text != null && text != lastText)   // 每 4ms 读一次，没变就不解析
                {
                    lastText = text;
                    var loc = Kv.Parse(text);
                    string v, seq;
                    if (loc.TryGetValue("v", out v) && v == "2" && loc.TryGetValue("seq", out seq) && seq != lastSeq)
                    {
                        lastSeq = seq;
                        string m;
                        if (loc.TryGetValue("map", out m)) LocalMap = m;
                        if (loc.TryGetValue("mt", out m)) LocalMt = m;
                        if (loc.TryGetValue("quit", out m)) LocalQuit = m;
                        if (loc.TryGetValue("rkd", out m)) LocalRkd = m;
                        if (loc.TryGetValue("rfp", out m)) LocalRfp = m;
                        if (Role == "host" && MpCfg.Max >= 2 && loc.TryGetValue("mpm", out m))
                        {
                            int colon = m.IndexOf(':');
                            string mpmSt = (colon >= 0 ? m.Substring(0, colon) : m).Trim();
                            if (mpmSt.Length > 0 && mpmSt != lastMpmSt)
                            {
                                lastMpmSt = mpmSt;
                                SyncMpTag(UpdateTagState(MpTag, mpmSt));
                                var slMp = T as ServerLink;
                                if (slMp != null) slMp.SetMpState(mpmSt);
                            }
                        }
                        var st = Stats;
                        if (st != null)
                        {
                            string wc, wcs, ev;
                            loc.TryGetValue("wc", out wc); loc.TryGetValue("wcs", out wcs); loc.TryGetValue("ev", out ev);
                            st.Local(wc, wcs, ev);
                        }
                        MaybeBackup(loc);
                        var sb = new StringBuilder();
                        foreach (var raw in text.Split('\n'))
                        {
                            var line = raw.TrimEnd('\r');
                            if (line.Length == 0 || line.StartsWith("st=") || line.StartsWith("nm=")
                                || line.StartsWith("pv=") || line.StartsWith("av=") || line.StartsWith("rk=")
                                || line.StartsWith("uid=") || line.StartsWith("sh=")) continue;
                            sb.Append(line).Append('\n');
                        }
                        // v0.22.0：协议版本 / 程序版本，对方据此提示"版本不一致"（旧版没有 pv=）
                        sb.Append("pv=").Append(Proto.Version).Append("\nav=").Append(Proto.App).Append('\n');
                        sb.Append("st=").Append(Kv.F(Link.UnixNow(), "0.0000")).Append('\n');
                        // v0.18.7：带上自己的昵称
                        sb.Append("nm=").Append(Name.Replace("\r", " ").Replace("\n", " ")).Append('\n');
                        var rk = RankPid;
                        if (rk != null) sb.Append("rk=").Append(rk).Append('\n');
                        sb.Append(Ident.Lines());     // 拉黑用的标识
                        var body = sb.ToString();
                        var slTap = T as ServerLink;
                        if (slTap != null) slTap.StateTap(body);      // 服务器房：低频上报（观战 / 校验 / 计分 / 捡道具）
                        lock (hostRules)
                            foreach (var k in RuleKeys) { string rv; if (loc.TryGetValue(k, out rv)) hostRules[k] = rv; }
                        if (!RelayOn && (!HostSpecCfg.On || Role != "host"))
                        {
                            var mh0 = T as IMpLink;
                            if (mh0 != null && mh0.Mesh != null)
                            {
                                // M3：多人全互连。每个对端一条边（直连 / 经第三人），游戏收到的仍是 src=<我的编号>
                                mh0.Mesh.BroadcastState(body);
                                if (Role == "host" && MpAiActiveCount > 0) FeedMpAi(mh0.Mesh.MyPid > 0 ? mh0.Mesh.MyPid : 1, body);
                            }
                            else
                            {
                                SendText(body);
                                // 多人混战：主机自己的包加 src=1 发给每位玩家（旧版星形，M3 之前的保底）
                                var mlk0 = T as Link;
                                if (mlk0 != null && Role == "host" && MpCfg.Max >= 2)
                                    lock (sendLock) mlk0.SendMp(Wire.Seal(body + "src=1\n"), 1);
                            }
                            // M4：解说房两位选手之间直连（没打通时这里 no-op，照旧走房主中转）
                            var lkMeshState = T as Link;
                            if (lkMeshState != null) lkMeshState.MeshSendState(body);
                            ForwardSpec(body, "h");     // 也给观战者一份
                        }
                    }
                }
                // 管道：游戏一发来就醒（不用等 4ms 轮询）
                if (viaPipe) pipe.LocalSignal.WaitOne(PollMs); else Thread.Sleep(PollMs);
                SendLoops++;
            }
        }

        List<IPAddress> bcastCache;
        DateTime bcastAt = DateTime.MinValue;
        List<IPAddress> BroadcastTargets()
        {
            if (bcastCache != null && (DateTime.UtcNow - bcastAt).TotalSeconds < 10) return bcastCache;
            if (NetBind.Loopback) return bcastCache = NetBind.DiscoveryAddrs();   // 回环下收不到广播，单播给本机每个发现地址
            var list = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        var a = ua.Address.GetAddressBytes();
                        var m = ua.IPv4Mask.GetAddressBytes();
                        if (m.Length != 4 || (m[0] == 0 && m[1] == 0 && m[2] == 0 && m[3] == 0)) continue;
                        var bc = new byte[4];
                        for (int k = 0; k < 4; k++) bc[k] = (byte)(a[k] | ~m[k]);
                        var ip = new IPAddress(bc);
                        if (!list.Contains(ip)) list.Add(ip);
                    }
                }
            }
            catch (Exception) { }
            bcastCache = list;
            bcastAt = DateTime.UtcNow;
            return list;
        }

        // 每局开始备份一次存档（对局期间游戏可能自动存档）。mt=<状态>:<回合>:<主机分>:<客机分>:<赢家>:<局号>
        void MaybeBackup(Dictionary<string, string> loc)
        {
            string mt;
            if (!doBackup || GameInfo.BackupSave == null || !loc.TryGetValue("mt", out mt)) return;
            var p = mt.Split(':');
            if (p.Length != 6 || p[0] != "cd" || backedUp.Contains(p[5])) return;
            backedUp.Add(p[5]);
            var id = p[5];
            Safe.Pool("backup", delegate { Log(L.T("第 " + id + " 局开始：", "Match " + id + " started: ") + GameInfo.BackupSave()); });
        }
    }

    // F5：开局前备份存档搬到游戏适配（GameInfo.BackupSave，由适件给）

    // 界面语言（中文 / English）。选择存在 net\lang.txt（lang=zh|en），游戏里的 Lua 也读这个文件来切换屏幕提示
    static class L
    {
        public static volatile bool En;
        public static string T(string zh, string en) { return En ? en : zh; }

        static string FilePath(string dir) { return Path.Combine(dir, "lang.txt"); }

        public static void Load(string dir)
        {
            var d = Kv.Read(FilePath(dir));
            string v;
            if (d != null && d.TryGetValue("lang", out v)) En = v == "en";
            else En = !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);   // 第一次：跟系统语言
        }

        public static void Save(string dir)
        {
            var text = "lang=" + (En ? "en" : "zh") + "\n";
            Kv.WriteAtomic(FilePath(dir), text);         // 设置，下次启动读；只在切换时写
            var p = GamePipe.For(dir);                   // 游戏经管道拿（不再每秒读一次文件）
            if (p != null) p.SetDown("lang.txt", text);
        }
    }

    // 监听房间发现广播（默认 27016）
    class Discovery
    {
        class Room { public string Name, Map, Bh, Mp; public bool Full, Watch, Ai; public DateTime Seen; }
        readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
        readonly UdpClient sock;
        public readonly bool Ok;
        volatile bool stop;

        public Discovery()
        {
            try
            {
                if (NetBind.Loopback) sock = NetBind.DiscoverySocket();
                else
                {
                    sock = new UdpClient();
                    sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    sock.Client.Bind(new IPEndPoint(IPAddress.Any, Session.BroadcastPort));
                }
                Ok = true;
                Safe.Thread("discovery", Run).Start();
            }
            catch (Exception) { Ok = false; }
        }

        void Run()
        {
            while (!stop)
            {
                IPEndPoint from = null;
                byte[] data;
                try { data = sock.Receive(ref from); } catch (Exception) { if (stop) break; Thread.Sleep(200); continue; }
                var p = Kv.Utf8.GetString(data).Split('|');
                int port;
                if (p.Length >= 5 && p[0] == WireTag.P + "ROOM" && int.TryParse(p[2], out port))
                    lock (rooms) rooms[from.Address + ":" + port] = new Room { Name = p[1], Map = p[3], Full = p[4] == "1", Watch = p.Length >= 6 && p[5] == "1", Ai = p.Length >= 7 && p[6] == "1", Bh = p.Length >= 8 ? p[7] : "", Mp = p.Length >= 9 ? p[8] : "", Seen = DateTime.UtcNow };
            }
        }

        public List<string[]> Current()   // {地址:端口, 名字, 地图, 已满, 能观战, 和 AI 对打, 主机标记, 多人字段}
        {
            var list = new List<string[]>();
            lock (rooms)
                foreach (var kv in rooms)
                    if ((DateTime.UtcNow - kv.Value.Seen).TotalSeconds < 3.5)
                        list.Add(new[] { kv.Key, kv.Value.Name, kv.Value.Map, kv.Value.Full ? "1" : "0", kv.Value.Watch ? "1" : "0", kv.Value.Ai ? "1" : "0", kv.Value.Bh ?? "", kv.Value.Mp ?? "" });
            list.Sort((a, b) => string.CompareOrdinal(a[0], b[0]));
            return list;
        }

        public void Close() { stop = true; try { sock.Close(); } catch (Exception) { } }
    }

    class MainForm : Form
    {
        readonly string dir;
        readonly bool doBackup, bg;
        readonly Cfg cfg;
        Session session;
        Session aiSession;    // 本地 AI：AI 那一头的 Session（net\ai\ ，127.0.0.1 连 session）
        ILocalAi ai;
        // AI 对战时同时开着的真房间（文件在 net\wait\，真人连进来后转正成 session）；waitMode = 它的连接方式
        Session waitSes;
        string waitMode = "";
        // 排位的等人房间：账号还没登记好 / 服务器没开排位时先不开，等就绪了 Tick 里自动再开（见 StartWait / RankWaitReady）
        bool waitRankPending;
        DateTime waitRankAt = DateTime.MinValue;
        readonly CheckBox aiRoomBox = new CheckBox();
        readonly ComboBox aiRoomModeBox = new ComboBox();
        readonly Discovery disc = new Discovery();
        readonly LobbyList lobbyList = new LobbyList();
        readonly CfgFetch cfgFetch = new CfgFetch();     // 数值表从大厅服务器拉（0.34 起只在面板点了"获取"才拉）
        DateTime cfgAutoAt = DateTime.MinValue;          // 排位方式下自动拉数值表（同意过 ranked；每 5 分钟 CFGQ 一次，同一张服务器只回"最新"）
        IRankedClient rank;     // 排位（排位）：密钥、上报、排行榜（Ranked.cs）；F4c-1 基底只认接口，没编排位时是 NoRanked（RankedApi.cs）
        IRankedClient Rank
        {
            get
            {
                if (rank == null) { rank = RankedSupport.Make(dir); rank.Log = m => { lastLog = DateTime.Now.ToString("HH:mm:ss ") + m; }; }
                return rank;
            }
        }

        // 面板 / 窗口里列出的连接方式：大厅服务器没开排位时不列"排位"；
        // 还在问的时候（-1）只有正选着它才列，免得一开窗口标签闪一下
        string[] ShownModes
        {
            get
            {
                int sup = Rank.Support;
                // 排位（武林盟主）列不列只看两件事：玩家装没装这个玩法（Modes.All 里有没有 ranked，没装 / 没启用就不在）
                // 和当前大厅服务器开没开这个玩法（Rank.Support）。桥接扩展包装没装只影响 bridge 方式，跟排位无关。
                // 已经选着 ranked 的照样列（刚开游戏、服务器支持还没问到时别把玩家的选择改掉存盘），建房 / 加入时再提示
                // 安全模式（本地）：只剩局域网 / 本地 AI（下线标志 X3 删了）
                bool ai = LocalAiSupport.Has;     // 11-13 拆干净：本地 AI 由游戏适配编进来；没有就不列这种方式（面板跟着 modes= 藏标签）
                if (cfg.Safe) return Array.FindAll(Modes.All, x => x == "lan" || (x == "ai" && ai));
                var all = Array.FindAll(Modes.All, x => x != "bridge" && (x != "ai" || ai));     // 0.34 评审：面板没有桥接平台标签了（好友在"最近"页），lobby.txt modes= 也不列，免得 End 键切进去
                // 0.34：排位默认直连，不要求桥接平台（没装桥接扩展包也照样能进排位）
                if (sup == 1 || (sup == -1 && Mode == "ranked")) return all;
                return Array.FindAll(all, x => x != "ranked");
            }
        }
        int shownSupport = -2;     // 上次按哪个 Support / 桥接平台有没有填的下拉框（变了重填）
        int ShownKey { get { return Rank.Support * 2 + (cfg.Safe ? 16 : 0); } }
        // "同时开房等真人"能选的方式：联机程序有的（Modes.Rooms，已去掉 ai / 没装的排位）里，只列面板现在列出来的
        // （排位要这台大厅服务器开了 --ranked 才列）。正选着的那个即使现在列不出来也留着（排位还在探测），开房时再提示
        string[] AiRoomModes
        {
            get
            {
                var shown = ShownModes;
                var list = Array.FindAll(Modes.Rooms, m => Array.IndexOf(shown, m) >= 0 || m == cfg.AiRoomMode);
                return list.Length > 0 ? list : Modes.Rooms;
            }
        }
        readonly TextBox nameBox = new TextBox(), ipBox = new TextBox(), portBox = new TextBox(), srvBox = new TextBox(), secretBox = new TextBox();
        readonly ListBox roomList = new ListBox();
        readonly Label status = new Label(), logLabel = new Label(), roleBig = new Label(), roleSub = new Label();
        readonly Label lblName = new Label(), lblIp = new Label(), lblJoin = new Label(), lblRooms = new Label(), lblHelp = new Label();
        readonly Label lblMode = new Label(), lblPort = new Label(), lblSecret = new Label();
        readonly TextBox ipsBox = new TextBox();
        readonly ComboBox langBox = new ComboBox(), modeBox = new ComboBox(), aiBox = new ComboBox();
        readonly CheckBox upnpBox = new CheckBox();
        Button hostBtn, joinBtn, stopBtn;
        readonly HashSet<string> myIps = new HashSet<string>();
        readonly List<string[]> cands = new List<string[]>();
        // 房间列表里没满的房间：target → 主机认不认观战。"只观战"开着时点这些房间 = 观战，不认的不加入
        readonly Dictionary<string, bool> roomWatch = new Dictionary<string, bool>();     // 可加入目标 {类型, 显示名, target}
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer(), cmdTimer = new System.Windows.Forms.Timer();
        string lastLog = "", lastErr = "", lastCmd = null, lastPipeCmd = null, ack = "";
        volatile bool cmdFileDirty = true;     // cmd.txt 被改过（FileSystemWatcher）；没有管道时照旧每次读
        DateTime lastErrAt = DateTime.MinValue, noGameSince = DateTime.MinValue;
        bool loadingMode;

        public MainForm(string dir, bool doBackup, string autoJoin, bool bg)
        {
            this.dir = dir;
            this.doBackup = doBackup;
            this.bg = bg;
            cfg = new Cfg(dir);      // 0.34 富状态 / 好友列表开关在 Cfg 构造里交给桥接 DLL
            L.Save(dir);      // 第一次按系统语言选好后也写下来，游戏端读到的和窗口一致
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(600, 640);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            int y = 14;
            lblName.SetBounds(14, y + 3, 130, 20); Add(lblName);
            nameBox.SetBounds(150, y, 200, 24); nameBox.Text = cfg.Name; Controls.Add(nameBox);
            nameBox.Leave += delegate { cfg.Name = nameBox.Text.Trim(); cfg.Save(); };
            // v0.22.0 联机密码（可选）：两边填同一个，每个包带 HMAC-SHA256 签名；空 = 不签名（同旧版）
            lblSecret.SetBounds(356, y + 3, 40, 20); Add(lblSecret);
            secretBox.SetBounds(398, y, 82, 24); secretBox.UseSystemPasswordChar = true; secretBox.Text = cfg.Secret; Controls.Add(secretBox);
            secretBox.Leave += delegate { cfg.Secret = secretBox.Text.Trim(); cfg.Save(); };
            // 右上角：语言切换（立即生效并写 net\lang.txt，游戏里的屏幕提示跟着切）
            langBox.DropDownStyle = ComboBoxStyle.DropDownList;
            langBox.Items.AddRange(new object[] { "中文", "English" });
            langBox.SetBounds(486, y, 100, 24);
            langBox.SelectedIndex = L.En ? 1 : 0;
            langBox.SelectedIndexChanged += delegate { L.En = langBox.SelectedIndex == 1; L.Save(dir); ApplyLang(); };
            Add(langBox);
            y += 34;
            // 连接方式（net-ui）：平台好友 / 局域网 / 广域网 / 虚拟局域网；游戏内联机面板改的也是这个
            lblMode.SetBounds(14, y + 3, 130, 20); Add(lblMode);
            modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            modeBox.SetBounds(150, y, 250, 24);
            modeBox.SelectedIndexChanged += delegate { if (!loadingMode) SetMode(ShownModes[Math.Max(0, modeBox.SelectedIndex)]); };
            Add(modeBox);
            lblPort.SetBounds(410, y + 3, 40, 20); Add(lblPort);
            portBox.SetBounds(450, y, 56, 24); portBox.Text = cfg.Port.ToString(); Controls.Add(portBox);
            portBox.Leave += delegate
            {
                int p;
                if (int.TryParse(portBox.Text.Trim(), out p) && p > 0 && p < 65536) { cfg.Port = p; cfg.Save(); }
                portBox.Text = cfg.Port.ToString();
            };
            upnpBox.SetBounds(514, y + 1, 80, 24); upnpBox.Checked = cfg.Upnp; Add(upnpBox);
            upnpBox.CheckedChanged += delegate { cfg.Upnp = upnpBox.Checked; cfg.Save(); };
            // 服务器大厅：端口 / UPnP 的位置换成大厅服务器地址（Tick 里按方式切换显示）
            srvBox.SetBounds(450, y, 136, 24); srvBox.Text = cfg.Server; srvBox.Visible = false; Controls.Add(srvBox);
            // 本地 AI：端口 / UPnP 的位置换成难度
            aiBox.DropDownStyle = ComboBoxStyle.DropDownList;
            aiBox.SetBounds(450, y, 136, 24); aiBox.Visible = false; Add(aiBox);
            aiBox.SelectedIndexChanged += delegate
            {
                if (loadingMode || aiBox.SelectedIndex < 0) return;
                cfg.AiLevel = aiBox.SelectedIndex; cfg.Save();
                if (ai != null) ai.Level = cfg.AiLevel;
            };
            srvBox.Leave += delegate { cfg.Server = srvBox.Text.Trim(); cfg.Save(); };
            y += 34;
            lblIp.SetBounds(14, y, 130, 20); Add(lblIp);
            ipsBox.SetBounds(150, y, 430, 66);
            ipsBox.ReadOnly = true; ipsBox.Multiline = true; ipsBox.ScrollBars = ScrollBars.Vertical; ipsBox.WordWrap = false;
            ipsBox.BorderStyle = BorderStyle.None; ipsBox.TabStop = false;
            Add(ipsBox);
            y += 74;
            hostBtn = new Button { Left = 14, Top = y, Width = 180, Height = 30 };
            hostBtn.Click += delegate { StartSession("host", null); };
            Add(hostBtn);
            // 本地 AI：同时开房等真人（开关）+ 开房方式；AI 对战中也能开关、换方式
            aiRoomBox.SetBounds(206, y + 4, 150, 24); aiRoomBox.Checked = cfg.AiRoom; Add(aiRoomBox);
            aiRoomBox.CheckedChanged += delegate { if (!loadingMode) SetAiRoom(aiRoomBox.Checked); };
            aiRoomModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            aiRoomModeBox.SetBounds(360, y + 3, 226, 24); Add(aiRoomModeBox);
            aiRoomModeBox.SelectedIndexChanged += delegate
            {
                var ms = AiRoomModes;
                if (!loadingMode && aiRoomModeBox.SelectedIndex >= 0 && aiRoomModeBox.SelectedIndex < ms.Length)
                    SetAiRoomMode(ms[aiRoomModeBox.SelectedIndex]);
            };
            y += 44;
            lblJoin.SetBounds(14, y + 3, 130, 20); Add(lblJoin);
            ipBox.SetBounds(150, y, 200, 24); Controls.Add(ipBox);
            joinBtn = new Button { Left = 360, Top = y - 2, Width = 90, Height = 28 };
            joinBtn.Click += delegate { Join(ipBox.Text); };
            Add(joinBtn);
            y += 38;
            lblRooms.SetBounds(14, y, 570, 20); Add(lblRooms);
            y += 22;
            roomList.SetBounds(14, y, 570, 110);
            roomList.DoubleClick += delegate
            {
                int i = roomList.SelectedIndex;
                if (i >= 0 && i < cands.Count && cands[i][0] == "nohost") Warn(L.T("对方还没开房", "They are not hosting yet"));
                else if (i >= 0 && i < cands.Count && (cands[i][0] == "watch" || cands[i][0] == "watchf")) Spectate(cands[i][2]);
                else if (i >= 0 && i < cands.Count) { ipBox.Text = cands[i][2]; Join(cands[i][2]); }
            };
            Controls.Add(roomList);
            y += 120;
            status.SetBounds(14, y, 370, 120);
            Add(status);
            // 右侧大字：当前身份
            roleBig.SetBounds(390, y, 196, 54);
            roleBig.Font = new Font("Microsoft YaHei UI", 26f, FontStyle.Bold);
            roleBig.TextAlign = ContentAlignment.MiddleCenter;
            Add(roleBig);
            roleSub.SetBounds(390, y + 56, 196, 44);
            roleSub.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            roleSub.TextAlign = ContentAlignment.TopCenter;
            Add(roleSub);
            y += 124;
            stopBtn = new Button { Left = 14, Top = y, Width = 90, Height = 28 };
            stopBtn.Click += delegate { StopSession(); };
            Add(stopBtn);
            logLabel.SetBounds(116, y + 5, 470, 22);
            logLabel.ForeColor = Color.DimGray;
            logLabel.AutoEllipsis = true;
            Add(logLabel);
            y += 38;
            lblHelp.SetBounds(14, y, 570, 54); lblHelp.ForeColor = Color.DimGray; Add(lblHelp);
            ApplyLang();

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) myIps.Add(ua.Address.ToString());
            }
            catch (Exception) { }
            WriteNone();
            // 启动时 cmd.txt 里留着的旧命令不执行（上次游戏留下的）
            var old = Kv.Read(CmdPath);
            string oid;
            if (old != null && old.TryGetValue("id", out oid)) lastCmd = oid;
            if (Modes.IsBridge(cfg.Mode) || (cfg.BridgeFriends && !cfg.Hide && !cfg.Safe)) Bridge.Init();     // 0.34 评审：开了"显示 " + Bridge.Svc + " 好友"也要 Init，不然"最近"页没好友
            timer.Interval = 500;
            timer.Tick += delegate { Tick(); };
            timer.Start();
            var gpw = GamePipe.For(dir);
            if (gpw != null) gpw.CmdFileChanged = () => { cmdFileDirty = true; };
            cmdTimer.Interval = 100;
            cmdTimer.Tick += delegate { CheckWait(); CheckBack(); PollCmd(); };
            cmdTimer.Start();
            FormClosing += (s, e) =>
            {
                // 后台模式（游戏带起的）被游戏内面板"打开电脑上的联机窗口"显示出来后，点 × 只藏起窗口。
                // 2026-11-05 19:56 内测：点 × 整个联机程序退出，游戏那边管道断开、4 分多钟没有联机程序，直到手动重启
                if (bg && !quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
                quitting = true;          // 关闭过程中 Tick 再进 AutoExit 时不要再 Quit
                timer.Stop(); cmdTimer.Stop(); StopSession(); disc.Close(); lobbyList.Close(); cfgFetch.Close(); Bridge.Shutdown();
                WriteLobby(false);
            };
            Shown += delegate { ActiveControl = hostBtn; };
            if (autoJoin != null) Shown += delegate { ipBox.Text = autoJoin; Join(autoJoin); };   // 测试用：--gui-join IP
            Tick();
        }

        void Add(Control c) { Controls.Add(c); }
        string CmdPath { get { return Path.Combine(dir, "cmd.txt"); } }
        string Mode { get { return cfg.Mode; } }

        // 固定文字按当前语言重设；会变的文字（状态、身份、房间列表）由 Tick 每次按语言生成
        void ApplyLang()
        {
            Text = GameInfo.Title;     // F5：游戏名由适配给
            lblName.Text = L.T("昵称", "Nickname");
            lblSecret.Text = L.T("密码", "Pass");
            lblMode.Text = L.T("连接方式", "Connect via");
            lblPort.Text = L.T("端口", "Port");
            upnpBox.Text = "UPnP";
            loadingMode = true;
            modeBox.Items.Clear();
            var shown = ShownModes;
            shownSupport = ShownKey;
            foreach (var m in shown) modeBox.Items.Add(Modes.Name(m));
            modeBox.SelectedIndex = Array.IndexOf(shown, Mode);
            aiBox.Items.Clear();
            for (int i = 0; i < LocalAiSupport.LevelCount; i++) aiBox.Items.Add(L.T("难度：", "Level: ") + LocalAiSupport.LevelName(i));
            if (cfg.AiLevel < aiBox.Items.Count) aiBox.SelectedIndex = cfg.AiLevel;     // F5：没编进本地 AI 时列表是空的
            aiRoomBox.Text = L.T("同时开房等真人", "Also host a room");
            aiRoomBox.Checked = cfg.AiRoom;
            aiRoomModeBox.Items.Clear();
            var arm = AiRoomModes;
            foreach (var m in arm) aiRoomModeBox.Items.Add(L.T("开房：", "Room: ") + Modes.Name(m));
            aiRoomModeBox.SelectedIndex = Array.IndexOf(arm, cfg.AiRoomMode);
            loadingMode = false;
            lblIp.Text = L.T("你的地址（发给朋友）", "Your address (send it)");
            hostBtn.Text = Mode == "ai" ? L.T("开始 AI 对战", "Start AI battle") : L.T("创建房间（当主机）", "Create room (host)");
            joinBtn.Text = L.T("加入", "Join");
            lblRooms.Text = L.T("可加入（双击加入）", "Joinable (double-click to join)");
            stopBtn.Text = L.T("断开", "Disconnect");
            lblHelp.Text = L.T("游戏里按 Home 打开联机面板（建房 / 加入 / 换连接方式），不用切出来。连上后对手自动出现；两边都按 F1 准备，开始对战。\n主机可以先在场地中心按 F2 存场地。第一次运行 Windows 防火墙会询问，请允许。",
                               "In game, press Home for the online panel (host / join / connection) without leaving the game. The opponent appears once connected; both press F1 to fight.\nHost: press F2 at the arena center to save it. Allow the firewall prompt on first run.");
            if (IsHandleCreated) Tick();
        }

        void WriteNone()
        {
            GamePipe.Out(Path.Combine(dir, "session.txt"), "role=none\nconnected=0\npeer=\nt=" + Kv.F(Link.UnixNow(), "0.0") + "\n");
        }

        // 出错：窗口开着就弹框，同时记下来给游戏内面板显示
        // 问题 A：面板命令通道（cmd.txt）的同意兜底。Lua 的 CONSENT.gate 是第一道，这里是程序层第二道：
        // 没同意过就不开房 / 不加入 / 不观战（测试工具、改过的脚本、旧配置都绕不过）。键和 Lua CONSENT.MODE_KEY 一样；
        // 本地 AI 不发数据不问（AI 等人房间在 StartWait 另有一道）；--consent-all 时全放行。
        bool ConsentGate(string cmd, Dictionary<string, string> d, bool mp = false)     // mp：多人房另要 consent_mp（11-14，房里所有人都能看到你的 IP）
        {
            if (Consent.All) return true;
            string v, m = Mode, mk;
            if (m == "ai") return true;
            mk = Modes.IsBridge(m) ? "bridge_match" : m;
            var keys = new List<string> { "play", mk };
            string target = d.TryGetValue("target", out v) ? v : "";
            if ((cmd == "join" || cmd == "spectate") && target.StartsWith("bridge:")) keys[1] = "bridge_match";
            if (cmd == "join" && d.TryGetValue("bridge", out v) && v == "1") keys.Add("bridge_match");
            if (cmd == "host" && d.TryGetValue("bridge", out v) && v == "1") keys[1] = "bridge_match";     // 开房（好友可见）走桥接，不用当前方式那一项
            if (cmd == "host" && cfg.BridgeRoom) keys.Add("bridge_match");
            bool isMp = mp || (cmd == "join" && d.TryGetValue("mp", out v) && v == "1");
            if (isMp) keys.Add("mp");
            else if ((cmd == "host" || cmd == "join" || cmd == "spectate") && Consent.ModOn("spec")) keys.Add("spec");     // 11-14：观战 mod 的项（观众收到对局数据）
            foreach (var k in keys)
                if (!Consent.Has(k))
                {
                    Warn(L.T("这一步要先同意（面板会问）：", "This needs your consent first (the panel asks): ") + k);
                    try { NetLog.W("同意兜底：拒绝 " + cmd + "（缺 consent_" + k + "）"); } catch (Exception) { }
                    return false;
                }
            return true;
        }

        // 从房间列表 / 好友页点进多人房走的是 join 命令，ConsentGate 那时还不知道是多人房；认出是多人房后再补查 consent_mp
        bool MpJoinConsent()
        {
            if (Consent.All || Consent.Has("mp")) return true;
            Warn(L.T("这一步要先同意（面板会问）：", "This needs your consent first (the panel asks): ") + "mp");
            try { NetLog.W("同意兜底：拒绝 join 多人房（缺 consent_mp）"); } catch (Exception) { }
            return false;
        }

        void DuelCmd(string cmd, Dictionary<string, string> d)
        {
            Session.ApplyDuelCmd(session, cmd, d, m => Warn(m));
        }

        void Warn(string msg)
        {
            lastErr = msg;
            lastErrAt = DateTime.UtcNow;
            lastLog = DateTime.Now.ToString("HH:mm:ss ") + msg;
            if (Visible) MessageBox.Show(this, msg, L.T("联机", "LAN Battle"));
        }

        void SetMode(string m)
        {
            m = Modes.Valid(m);
            if (m == "ranked" && Rank.Support == 0) { Warn(L.T("这台大厅服务器没开" + GameInfo.RankedLongZh, "This lobby server has no ranked mode")); ApplyLang(); return; }
            // 0.34：排位默认直连，不再要求桥接平台（作废；"对局走 " + Bridge.Svc是房间设置）     //
            if (session != null && m != Mode) { Warn(L.T("联机中不能换连接方式，先断开", "Disconnect before changing the connection")); ApplyLang(); return; }
            cfg.Mode = m;
            cfg.Save();
            lastErr = "";
            if (Modes.IsBridge(m) && !Bridge.Init()) lastErr = Bridge.Error;
            ApplyLang();
            Tick();
        }

        // remember = false：不记进"最近连接"（testsake_join.py 让游戏连本机假主机时带 remember=0，
        // 不然 127.0.0.1:27115 / 27117 这种测试地址会一直留在用户面板的"加入 最近"里）
        // 0.34 "最近"页里点平台好友（没有平台好友标签页了）：临时切到桥接平台方式，会话结束后 Tick 切回；返回 false = 切不了
        bool AutoBridge(string text)
        {
            if (!text.StartsWith("bridge:") || Mode == "bridge" || Mode == "ai") return true;
            if (session != null) { Warn(L.T("联机中不能换连接方式，先断开", "Disconnect before changing the connection")); return false; }
            var from = Mode;
            SetMode("bridge");
            if (Mode != "bridge") return false;
            autoBridgeFrom = from;
            return true;
        }

        void Join(string text, bool remember = true)
        {
            text = (text ?? "").Trim();
            if (Mode == "ai") { StartSession("host", null); return; }     // 本地 AI 没有"加入"，一律当开始
            if (!AutoBridge(text)) return;
            // "只观战"开着时点房间列表里没满的房间：以观战者身份进去；主机不认观战（或排位）就不加入、提示
            bool canWatch;
            if (SpecCfg.NoQueue && IsMpTarget(text)) nextMpWatch = true;     // 新版房间：照常加入，进去后当"只看不上场"
            else if (SpecCfg.NoQueue && roomWatch.TryGetValue(text, out canWatch))
            {
                if (canWatch && Mode != "ranked") { Spectate(text); return; }
                Warn(L.T("你开着「只观战」，这个房间不能观战，没有加入（想上场打请把「观战时」切回排队）",
                         "Watch-only is on and this room can't be watched, so you didn't join (switch \"When watching\" back to queue to play)"));
                return;
            }
            if (text.Length == 0)
            {
                Warn(Mode == "bridge" ? L.T("先选一个开房的好友，或填主机的桥接 ID", "Pick a friend who is hosting, or enter the host's bridge ID")
                     : Modes.IsServer(Mode) ? L.T("先选一个房间，或填房间号", "Pick a room, or enter the room code")
                     : L.T("先填主机 IP", "Enter the host IP first"));
                return;
            }
            // 服务器大厅里填的是 IP / 域名（带 . 或 :）= 输入 IP 直连（原来的广域网）
            if (Modes.IsServer(Mode) && (text.IndexOf('.') >= 0 || text.IndexOf(':') >= 0) && !text.StartsWith("room:"))
            {
                // 排位要经服务器（服务器靠会话认两边是谁），不能 IP 直连
                if (Mode == "ranked") { Warn(L.T(GameInfo.RankedZh + "只能从房间列表 / 房间号加入，不能 IP 直连", "Ranked rooms are joined from the list or by room code, not by IP")); return; }
                var dep = Addr.ParseDirect(text, cfg.Port);     // IPv4 / IPv6 都认
                if (dep == null) { Warn(L.T("认不出这个地址：", "Can't resolve this address: ") + text); return; }
                if (StartSession("client", dep, 0) && remember) cfg.Remember(Mode, Addr.Fmt(dep));
                return;
            }
            if (Modes.IsServer(Mode))
            {
                // 房间号（6 位）或列表里的 room:<房间号>[@<所在服务器 IP:端口>]（别的服务器互通过来的房间，要去那台加入）；
                // 房间是临时的，不记进最近列表
                var room = text.StartsWith("room:") ? text.Substring(5) : text;
                int at = room.IndexOf('@');
                var code = at >= 0 ? room.Substring(0, at) : room;
                if (code.Length < 4 || code.Length > 16 || code.IndexOfAny(new[] { ' ', '|', ':', '.' }) >= 0
                    || (at >= 0 && LobbyServer.Resolve(room.Substring(at + 1)) == null)) { Warn(L.T("这不是房间号：", "Not a room code: ") + text); return; }
                if (roomMp.ContainsKey(text) || roomMp.ContainsKey(code.Trim().ToUpperInvariant()) || roomMp.ContainsKey("room:" + code.Trim().ToUpperInvariant()))
                { if (!MpJoinConsent()) return; nextMpJoin = true; }
                StartSession("client", null, 0, room);
                return;
            }
            if (Mode == "bridge")
            {
                ulong id = Addr.BridgeId(text);
                if (id == 0) { Warn(L.T("这不是有效的桥接 ID：", "Not a valid bridge ID: ") + text); return; }
                if (roomMp.ContainsKey(text) || roomMp.ContainsKey("bridge:" + id) || roomMp.ContainsKey(id.ToString()))
                { if (!MpJoinConsent()) return; nextMpJoin = true; }
                StartSession("client", null, id);     // 0.34 评审：不记 recent_steam=（明文平台账号 ID落盘）
                return;
            }
            if (roomMp.ContainsKey(text)) { if (!MpJoinConsent()) return; nextMpJoin = true; }
            var ep = Addr.ParseDirect(text, Session.GamePort);
            if (ep == null) { Warn(L.T("认不出这个地址：", "Can't resolve this address: ") + text); return; }
            if (StartSession("client", ep, 0) && remember) cfg.Remember(Mode, ep.Port == Session.GamePort ? ep.Address.ToString() : ep.ToString());
        }

        // 对手自己下去观战：局域网 / IP / 桥接平台在会话里换角色；服务器大厅的客机要换成观战连接（WatchLink），重开会话
        void ToSpec()
        {
            var s = session;
            if (s == null || s.Role != "client") return;
            if (s.StepDown()) return;
            var sl = s.T as ServerLink;
            if (sl != null && sl.RoomId.Length > 0) { var room = sl.RoomId; nextDown = true; StartSession("spec", null, 0, room); }
        }

        // 观战：局域网 / 服务器大厅里输入 IP，连主机当观战者（主机要是局域网开的房，服务器大厅的主机还不支持）
        void Spectate(string text)
        {
            text = (text ?? "").Trim();
            if (Mode == "ranked" || Mode == "ai") { Warn(L.T("这种连接方式不能观战", "Spectating is not available in this mode")); return; }
            // 新版房间（多人 / 对决，全互连）：观战 = 加入房间 + 只看不上场；状态包由选手 / 转发机直接发来，不经大厅服务器转发（旧的 WatchLink 只留给旧版 1v1）
            if (IsMpTarget(text)) { nextMpWatch = true; Join(text.StartsWith("watch:") ? "room:" + text.Substring(6) : text); return; }
            if (!AutoBridge(text)) return;
            if (Modes.IsBridge(Mode))
            {
                // 平台好友（桥接平台:<平台账号 ID>）：连主机当观战者
                ulong owner = Addr.BridgeId(text);
                if (owner == 0) { Warn(L.T("找不到主机：", "Can't find the host: ") + text); return; }
                StartSession("spec", null, owner);
                return;
            }
            // 服务器大厅房间列表里的 watch:<房间号>[@服务器]，或直接填房间号
            if (Modes.IsServer(Mode) && (text.StartsWith("watch:") || text.StartsWith("room:") || (text.IndexOf('.') < 0 && text.IndexOf(':') < 0)))
            {
                var room = text.StartsWith("watch:") ? text.Substring(6) : text.StartsWith("room:") ? text.Substring(5) : text;
                if (room.Length < 4) { Warn(L.T("这不是房间号：", "Not a room code: ") + text); return; }
                StartSession("spec", null, 0, room);
                return;
            }
            var ep = Addr.ParseDirect(text, Modes.IsServer(Mode) ? cfg.Port : Session.GamePort);
            if (ep == null) { Warn(L.T("认不出这个地址：", "Can't resolve this address: ") + text); return; }
            StartSession("spec", ep, 0);
        }

        bool StartSession(string role, IPEndPoint peer) { return StartSession(role, peer, 0, null); }
        bool StartSession(string role, IPEndPoint peer, ulong bridgeHost) { return StartSession(role, peer, bridgeHost, null); }

        string nextToken;       // 下一次服务器大厅客机会话 JOIN 带的上场口令
        bool nextDown;          // 下一次服务器大厅观战会话是"刚下场的"

        int nextMpMax;          // 多人混战：下一次开的会话是多人房间（人数）/ 按多人加入；开完就清，之后普通开房 / 加入还是 1v1
        bool nextMpJoin;
        bool nextMpWatch;     // 下一次加入是"观战"（Spectate / 只观战点新版房间）

        // 房间列表 / 好友页里这一项是新版房间（多人 / 对决）：列表、房间号、watch: / room: 几种写法都认
        bool IsMpTarget(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (roomMp.ContainsKey(text)) return true;
            var t = text.StartsWith("watch:") ? text.Substring(6) : text.StartsWith("room:") ? text.Substring(5) : null;
            if (t == null) return false;
            int at = t.IndexOf('@');
            var code = (at >= 0 ? t.Substring(0, at) : t).Trim().ToUpperInvariant();
            return roomMp.ContainsKey(code) || roomMp.ContainsKey("room:" + code);
        }

        bool StartSession(string role, IPEndPoint peer, ulong bridgeHost, string room)
        {
            // 临时切到桥接方式（AutoBridge：好友页开房 / 加入好友）后马上开会话：StopSession 里的 Tick 看到"没会话 + 临时切过"会立刻切回去，
            // 新会话就按原来的方式开了（以前从"最近"页加入好友也是这样）。这次先收起切回标记，开完再放回
            var keepAuto = autoBridgeFrom;
            autoBridgeFrom = null;
            StopSession();
            autoBridgeFrom = keepAuto;
            MpCfg.Max = role == "host" ? nextMpMax : 0;
            MpCfg.Join = role == "client" && nextMpJoin;
            MpCfg.JoinWatch = MpCfg.Join && nextMpWatch;
            nextMpMax = 0;
            nextMpJoin = false;
            nextMpWatch = false;
            cfg.Name = nameBox.Text.Trim();
            cfg.Secret = secretBox.Text.Trim();
            cfg.Save();
            lastErr = "";
            try
            {
                ITransport t = null;
                ServerLink sl = null;
                IRoomLink rl = null;      // 经大厅服务器开房 / 加入的（服务器大厅 sl，或排位 BridgeRoomLink）
                // 0.34（最终方案 3）：服务器大厅 / 排位的房间默认 UDP 直连（不中转）；房主开了"对局走 " + Bridge.Svc的房经桥接平台连（BridgeRoomLink），
                // 客机按房间列表第 11 列判断对方的房是不是桥接平台房（roomBridge）。观战不走桥接平台房
                // 三档：主机按自己的设置（一直桥接 = BridgeRoomLink；桥接扩展包没装 / 没启用时退回直连，不然整个开不了房）；
                // 客机一律按主机的（房间列表第 11 列：1 = 一直桥接、2 = 直连失败时走桥接），自己的设置不管
                bool bridgeUsable = Modes.ShowBridge && Bridge.Available;
                // 一直桥接的房走 BridgeRoomLink，它只有 1v1（没有 IMpLink）：多人房间以前在这里悄悄变成旧版 1v1 房（不报 mp=1、列表标 [旧版]、面板没有玩法设置）。
                // 多人房间按"直连失败时走桥接"开（那条退路），并明说
                bool mpHostNoBridge = role == "host" && MpCfg.Max >= 2 && Modes.IsServer(Mode) && cfg.BridgeRoom;
                bool bridgeRoom = Modes.IsServer(Mode) && role != "spec" && !Program.RankUdp
                              && (role == "host" ? cfg.BridgeRoom && bridgeUsable && !mpHostNoBridge : IsBridgeRoom(room));
                if (role == "host" && Modes.IsServer(Mode) && cfg.BridgeRoom && !bridgeUsable)
                    NetLog.W("房间设置是\"一直桥接\"，但桥接扩展包没装 / 没启用：这间房只走直连");
                if (mpHostNoBridge)
                {
                    NetLog.W("多人房间不支持\"一直桥接\"：这间房按\"直连失败时走桥接\"开");
                    Warn(L.T("多人房间不支持\"一直桥接\"，这间房改成直连失败时走桥接",
                                         "Multiplayer rooms can't use \"always bridge\"; this room uses the bridge only if direct fails"));     // 只是提示，接着开房
                }
                // 安全模式：只许局域网 / 本地 AI
                if (cfg.Safe && Mode != "lan" && Mode != "ai") { Warn(SafeMsg()); return false; }
                // 0.34 评审：从"最近"页 / 手输房间号进桥接平台房时 Lua 不知道它是桥接平台房、没问过 steam_match——这里兜底：自己没开"对局走 " + Bridge.Svc就不进
                if (bridgeRoom && role == "client" && (!bridgeUsable || !(Consent.All || Consent.Has("bridge_match"))))     // 看能不能、同意没，不看自己的设置
                {
                    Warn(!bridgeUsable ? L.T("这间房主机设成一直桥接：要装上桥接扩展包并在它的插件页启用（重启游戏生效）", "The host set this room to always use the bridge: install and enable the bridge extension (restart the game)")
                                       : L.T("这间房主机设成一直桥接：先同意桥接（面板会问）", "The host set this room to always use the bridge: agree to the bridge first (the panel asks)"));
                    return false;
                }
                if (bridgeRoom)
                {
                    // 大厅服务器只管登记 / 介绍 /（排位）记分，不打洞、不中转；对局数据全走 BridgeLink
                    int at = room != null ? room.IndexOf('@') : -1;
                    if (at >= 0) room = room.Substring(0, at);       // 排位房间不推给别的服务器，一般不会有 @
                    var srv = LobbyServer.Resolve(cfg.Server);
                    if (srv == null)
                    {
                        Warn(cfg.Server.Length == 0 ? L.T("先在设置里填大厅服务器地址", "Set the lobby server first")
                                                    : L.T("认不出大厅服务器地址：", "Can't resolve the lobby server: ") + cfg.Server);
                        return false;
                    }
                    if (!Bridge.Init()) { Warn(L.T("对局走桥接：", "Play over bridge: ") + Bridge.Error); return false; }
                    var rk = new BridgeRoomLink(srv, role == "host" ? null : room, cfg.Name, Path.Combine(dir, "state_remote.txt"));
                    rk.ServerName = cfg.Server;
                    rk.Ranked = Mode == "ranked";
                    rk.RankPid = Mode == "ranked" ? Rank.Pid : "";
                    t = rk;
                    rl = rk;
                }
                else if (Modes.IsBridge(Mode))
                {
                    if (!Bridge.Init()) { Warn(Bridge.Error); return false; }
                    t = new BridgeLink(role == "host" ? 0 : bridgeHost, Path.Combine(dir, "state_remote.txt"));
                }
                else if (role == "spec" && room != null && Modes.IsServer(Mode))
                {
                    // 服务器大厅的房间：观战者只和大厅服务器说话，主机的两路包由服务器转来
                    int at = room.IndexOf('@');
                    var srv = LobbyServer.Resolve(at >= 0 ? room.Substring(at + 1) : cfg.Server);
                    if (at >= 0) room = room.Substring(0, at);
                    if (srv == null) { Warn(L.T("认不出大厅服务器地址：", "Can't resolve the lobby server: ") + cfg.Server); return false; }
                    t = new WatchLink(srv, room, Path.Combine(dir, "state_remote.txt")) { Down = nextDown };
                    nextDown = false;
                }
                else if (Modes.IsServer(Mode) && !((role == "client" || role == "spec") && peer != null))     // 客机输入 IP 直连 = 普通 UDP（下面 t = null）
                {
                    // 客机加入别的服务器的房间（room = 房间号@那台服务器）：去那台 JOIN、打洞、中转
                    int at = room != null ? room.IndexOf('@') : -1;
                    var srv = LobbyServer.Resolve(at >= 0 ? room.Substring(at + 1) : cfg.Server);
                    if (at >= 0) room = room.Substring(0, at);
                    if (srv == null)
                    {
                        Warn(cfg.Server.Length == 0 ? L.T("先在设置里填大厅服务器地址（可自己架设）", "Set or host a lobby server first")
                                                    : L.T("认不出大厅服务器地址：", "Can't resolve the lobby server: ") + cfg.Server);
                        return false;
                    }
                    // 客机端口随机：打洞靠服务器告诉对方地址。主机监听设置里的端口（默认 27015）：朋友可以"输入 IP 直连"，
                    // 开了端口转发 / UPnP 时服务器连不上也能玩（原来的广域网）
                    t = sl = new ServerLink(srv, role == "host" ? null : room, role == "host" ? cfg.Port : 0, cfg.Name,
                                            Path.Combine(dir, "state_remote.txt"), Program.SimNoDirect);
                    sl.AllowRelay = false;      // 0.34 对局不经服务器中转（cfg.Relay 只留着兼容旧配置，不再用）
                    sl.BridgeUpOn = cfg.BridgeUp;
                    sl.BridgeUpMs = cfg.BridgeUpMs;
                    if (MpCfg.Max >= 2) { sl.MpTag = currentMpTag; }
                    sl.ServerName = at >= 0 ? "" : cfg.Server;     // 互通过来的房间去的是别的服务器，只显示 IP
                    if (role == "client") { sl.JoinToken = nextToken; nextToken = null; }
                    if (Mode == "ranked") sl.RankPid = Rank.Pid;     // 0.34 排位默认直连就走这里（作废）
                    // 直连失败时走桥接：主机按自己的设置（1 档）；客机按房间（列表第 11 列 = 2）。都要桥在、同意过 bridge_match 才把桥接 ID 交给服务器
                    if (Modes.IsServer(Mode) && !(Mode == "ranked" && Program.RankUdp) && role != "spec" && peer == null)     // 服务器大厅和排位都算（同"一直桥接"的判断）
                    {
                        bool agreed = Consent.All || Consent.Has("bridge_match");
                        if (role == "client") { sl.FallbackRoom = IsFallbackRoom(room); sl.FallbackHostPid = FallbackHostPidOf(room); }
                        bool want = role == "host" ? (cfg.BridgeMode == 1 || mpHostNoBridge) : sl.FallbackRoom;
                        if (want && bridgeUsable && agreed && Bridge.Init() && Bridge.MyId != 0) sl.FallbackMyId = Bridge.MyId;
                        if (want) NetLog.W("" + (role == "host" ? "主机" : "客机") + "：直连失败时走桥接 " + (sl.FallbackMyId != 0 ? "准备好了" : "用不了（" + (!bridgeUsable ? "没有桥接扩展包" : !agreed ? "没同意桥接" : "桥没就绪") + "），只走直连"));
                    }
                    rl = sl;
                }
                if (Mode == "ai") return StartAi();
                int port = Modes.IsServer(Mode) ? cfg.Port : Program.LanPort;
                var ses = new Session(role, Mode, t, peer, port, cfg.Name, dir, doBackup, cfg.Secret);
                if (role == "host" && MpCfg.Max >= 2)
                {
                    currentMpTag = Session.UpdateTagState(currentMpTag, "idle");
                    ses.SyncMpTag(currentMpTag);
                    if (sl != null) sl.MpTag = currentMpTag;
                }
                if (rl != null && Mode == "ranked")
                {
                    ses.RankPid = Rank.Pid;
                    ses.Stats = Rank.Stats;
                    var rs = Rank.Stats;
                    rl.PeerPacket = d => { string r; if (d.TryGetValue("res", out r)) rs.Peer(r); };     // 对方报我的招打没打中
                }
                if (rl != null) rl.MapOf = () => ses.LocalMap;
                session = ses;
                session.Log = m => { lastLog = DateTime.Now.ToString("HH:mm:ss ") + m; };
                session.Start();
                if (Mode == "server" && role == "host" && cfg.Upnp) Upnp.MapAsync(port);     // 排位走桥接平台，用不着开端口
            }
            catch (SocketException ex)
            {
                session = null;
                WriteNone();
                if (!Modes.IsBridge(Mode) && Mode != "ai") NetLog.W("开会话失败（" + role + "，" + Mode + "）：开不了端口 " + ex.Message);
                Warn(L.T("开不了端口：", "Can't open the port: ") + ex.Message
                     + L.T("（是不是还开着另一个游戏 / 联机程序 / 幽灵测试？）", " (Is another game, online helper or the ghost test still running?)"));
                return false;
            }
            catch (Exception ex)
            {
                session = null;
                WriteNone();
                Warn(ex.Message);
                return false;
            }
            Tick();
            return true;
        }

        // 本地 AI 对战：游戏这边当主机（随机端口，不广播、不签名），AI 当客机从 127.0.0.1 连进来，包走真的 Session / 可靠通道；
        // LocalAi 在 net\ai\ 里读你的包、写它的包。在 StartSession 的 try 里调（开不了端口照常报错）
        // backFrom： 真人离开后重开 AI 时，离开的人的昵称（AI 的包带上，模组提示"XXX 已离开房间，AI 对战已重新开始"）
        bool StartAi(string backFrom = null)
        {
            if (!LocalAiSupport.Has) throw new Exception(L.T("这个版本没有本地 AI（由游戏适配提供）", "This build has no local AI (it comes with the game adapter)"));
            var aiDir = Path.Combine(dir, "ai");
            Directory.CreateDirectory(aiDir);
            GamePipe.StartVirtual(aiDir);       // AI 和它的 Session 在同一个进程里：内存交接，不写 net\ai\ 的文件（要在建 Session 之前）
            foreach (var f in new[] { "state_local.txt", "state_remote.txt", "session.txt", "rel_in.txt", "rel_out.txt" })
                try { File.Delete(Path.Combine(aiDir, f)); } catch (Exception) { }
            var host = new Session("host", "ai", null, null, 0, cfg.Name, dir, doBackup, null);
            session = host;
            // "对端锁定为 127.0.0.1:…" 对 AI 没意义，还会盖掉"XXX 已离开房间，AI 对战已重新开始"
            var locked = L.T("对端锁定为 ", "Peer locked: ");
            session.Log = m => { if (!m.StartsWith(locked)) lastLog = DateTime.Now.ToString("HH:mm:ss ") + m; };
            session.Start();
            try
            {
                aiSession = new Session("client", "ai", null, new IPEndPoint(IPAddress.Loopback, host.T.LocalPort), 0,
                                        L.T("AI 对手", "AI opponent"), aiDir, false, null).Start();
            }
            catch (Exception) { StopSession(); throw; }     // 主机那头已经开了，别留着
            ai = LocalAiSupport.Impl.Create(aiDir, cfg.AiLevel, cfg.AiLoad, backFrom);     // F5：AI 本身在游戏适配里
            // 重开时 AI 开局那几行（读招式表、"AI：武器 / 影之武装 …"）同样会盖掉"XXX 已离开房间"：
            // 在 AI 说别的话（喝药、被打倒…）之前，这几种不进 msg
            bool quiet = backFrom != null;
            var setup = LocalAiSupport.SetupLogPrefixes();
            ai.Log = m =>
            {
                if (quiet && Array.Exists(setup, p => m.StartsWith(p))) return;
                quiet = false;
                lastLog = DateTime.Now.ToString("HH:mm:ss ") + m;
            };
            ai.Start();
            lastLog = DateTime.Now.ToString("HH:mm:ss ") + (backFrom != null ? backFrom + L.T(" 已离开房间，AI 对战已重新开始", " left the room; AI battle restarted")
                      : L.T("AI 对手已就位：两边都按 F1 准备后开打（AI 一直是准备好的）", "AI opponent ready: press F1 to fight (the AI is always ready)"));
            if (cfg.AiRoom && waitSes == null) StartWait();     // 真人离开时房间已经退回等人（CheckBack），不重开
            else if (waitSes != null) session.SpecRelay = waitSes;     // 退回等人的房间：AI 对打的包照样转给那边的观战者（以前只在 StartWait 里接）
            Tick();
            return true;
        }

        bool AiRunning { get { return session != null && session.Mode == "ai"; } }

        // 主机连接（等人的房间用）：和 StartSession 里当主机的那一支一样，只是文件放在 d。
        // 房间设置"对局走桥接"三档对等人的房间也生效（客机一律按主机的）：
        //   2 = 一直桥接（大厅只登记 / 介绍，对局数据全走桥接；桥接扩展包没装 / 没同意时退回直连）
        //   1 = 直连优先，直连不通再走桥接（默认；没装桥接扩展包就只有直连）
        //   0 = 只直连
        // mode = ranked 时和 StartSession 的排位主机一样：在服务器大厅登记成排位房间（REG 第 9/10 段 = 1 / pid），
        // 等人房间转正后这间房就是排位对局，照常上报计分
        Session NewHost(string mode, string d)
        {
            ITransport t = null;
            ServerLink sl = null;
            IRoomLink rl = null;
            bool bridgeUsable = Modes.ShowBridge && Bridge.Available, bridgeAgreed = Consent.All || Consent.Has("bridge_match");
            if (Modes.IsBridge(mode))
            {
                if (!Bridge.Init()) throw new InvalidOperationException(Bridge.Error);
                t = new BridgeLink(0, Path.Combine(d, "state_remote.txt"));
            }
            else if (Modes.IsServer(mode))
            {
                var srv = LobbyServer.Resolve(cfg.Server);
                if (srv == null)
                    throw new InvalidOperationException(cfg.Server.Length == 0 ? L.T("先在设置里填大厅服务器地址（可自己架设）", "Set or host a lobby server first")
                                                                            : L.T("认不出大厅服务器地址：", "Can't resolve the lobby server: ") + cfg.Server);
                // 一直桥接：和 StartSession 同一套判断；桥接不可用 / 没同意就退回直连，不耽误开房
                bool always = cfg.BridgeRoom && bridgeUsable && bridgeAgreed && !Program.RankUdp;
                if (cfg.BridgeRoom && !always)
                    NetLog.W("等人的房间设置是\"一直桥接\"，但" + (!bridgeUsable ? "桥接扩展包没装 / 没启用" : !bridgeAgreed ? "没同意桥接" : "命令行强制 UDP") + "：这间房只走直连");
                if (always && Bridge.Init())
                {
                    var br = new BridgeRoomLink(srv, null, cfg.Name, Path.Combine(d, "state_remote.txt"));
                    br.ServerName = cfg.Server;
                    br.Ranked = mode == "ranked";
                    br.RankPid = mode == "ranked" ? Rank.Pid : "";
                    t = br;
                    rl = br;
                }
                else
                {
                    if (always) NetLog.W("等人的房间：桥接用不了（" + Bridge.Error + "），这间房只走直连");
                    sl = new ServerLink(srv, null, cfg.Port, cfg.Name, Path.Combine(d, "state_remote.txt"), Program.SimNoDirect);
                    sl.AllowRelay = false;     // 0.34 最终方案 3：对局不经服务器中转（以前是 cfg.Relay）
                    sl.ServerName = cfg.Server;
                    if (mode == "ranked") sl.RankPid = Rank.Pid;     // REG 第 9 段 = 1：排位房间，等人时也挂在排位房间列表里
                    // 默认 1 档：直连优先，直连不通时走桥接（桥在、同意过才把自己的桥接 ID 交给服务器）
                    if (cfg.BridgeMode == 1 && bridgeUsable && bridgeAgreed && Bridge.Init() && Bridge.MyId != 0)
                    {
                        sl.FallbackMyId = Bridge.MyId;
                        sl.BridgeUpOn = cfg.BridgeUp;
                        sl.BridgeUpMs = cfg.BridgeUpMs;
                        NetLog.W("等人的房间：直连失败时走桥接 准备好了");
                    }
                    t = sl;
                }
            }
            int port = Modes.IsServer(mode) ? cfg.Port : Program.LanPort;
            var ses = new Session("host", mode, t, null, port, cfg.Name, d, doBackup, cfg.Secret);
            if (rl != null) rl.MapOf = () => ses.LocalMap;
            else if (sl != null) sl.MapOf = () => ses.LocalMap;
            if (mode == "ranked")     // 和 StartSession 的排位那一支一样：状态包带 rk=、本机 / 对方包的统计交给排位插件
            {
                ses.RankPid = Rank.Pid;
                ses.Stats = Rank.Stats;
                var rs = Rank.Stats;
                if (rl != null && rs != null) rl.PeerPacket = pkt => { string r; if (pkt.TryGetValue("res", out r)) rs.Peer(r); };
            }
            return ses;
        }

        // AI 对战时同时开一个真房间（cfg.AiRoomMode）。文件在 net\wait\：游戏照常和 AI 打，
        // 有真人连进来（收到它的状态包）→ CheckWait 停掉 AI、把这个房间转正。开不了只提示，AI 对战照常
        void StartWait()
        {
            StopWait();
            if (!AiRunning) return;
            var m = cfg.AiRoomMode;
            if (cfg.Safe) { Warn(SafeMsg()); return; }     // 0.34 评审：等人房间也受安全模式管
            if (!Consent.Has(Modes.IsBridge(m) ? "bridge_match" : m)) { Warn(L.T("开房等真人要先同意（面板会问）", "Hosting a room needs your consent first (the panel asks)")); return; }
            // 排位（武林盟主）的等人房间：这个模式要"确实激活"才开——排位扩展包装上并启用、这台大厅服务器开了 --ranked、
            // 本机排位账号已经在服务器登记过。还没就绪时只把开关记下来（waitRankPending），Tick 里每几秒再看一次，就绪了自动开
            waitRankPending = false;
            if (m == "ranked")
            {
                string why = RankWaitWhy();
                if (why.Length > 0)
                {
                    waitRankPending = true;
                    waitRankAt = DateTime.UtcNow.AddSeconds(RankWaitEvery);
                    Warn(L.T("排位房间还开不了（AI 对战照常）：", "Can't open the ranked room yet (the AI battle goes on): ") + why);
                    return;
                }
            }
            var wdir = Path.Combine(dir, "wait");
            try
            {
                Directory.CreateDirectory(wdir);
                foreach (var f in new[] { "state_local.txt", "state_remote.txt", "session.txt", "rel_in.txt", "rel_out.txt" })
                    try { File.Delete(Path.Combine(wdir, f)); } catch (Exception) { }
                var ses = NewHost(m, wdir);
                ses.Log = x => { lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("[等人的房间] ", "[Room] ") + x; };
                ses.LocalMap = session.LocalMap;
                ses.AiWaiting = true;
                var wsl = ses.T as ServerLink;
                if (wsl != null) wsl.AiRoom = true;
                else { var wbr = ses.T as BridgeRoomLink; if (wbr != null) wbr.AiRoom = true; }     // 一直桥接的等人房间也标"和 AI 对打中"
                ses.Start();
                waitSes = ses;
                var ais = session;
                if (ais != null) ais.SpecRelay = ses;     // AI 对打的两路包给等人房间的观战者
                waitMode = m;
                if (wsl != null && cfg.Upnp) Upnp.MapAsync(cfg.Port);     // 走桥接的房没有 UDP 端口，不用映射
                lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("同时开着房间等真人（", "Also hosting a room (") + Modes.Name(m)
                          + L.T("）：有人进来就结束 AI 对战", "): the AI battle ends when a player joins");
            }
            catch (Exception ex)
            {
                waitSes = null;
                Warn(L.T("开不了等人的房间（AI 对战照常）：", "Can't open the room (the AI battle goes on): ") + ex.Message
                     + (ex is SocketException ? L.T("（端口被占用？）", " (port in use?)") : ""));
            }
        }

        const double RankWaitEvery = 5;     // 秒：排位的等人房间还没就绪时多久再看一次
        // 排位的等人房间开不了的原因（"" = 可以开）。startWait 用；Tick 里按同一个判断决定要不要自动重开
        string RankWaitWhy()
        {
            if (!RankedSupport.Available) return L.T("排位扩展包没装 / 没启用（重启游戏生效）", "the ranked extension is not installed / enabled (restart the game)");
            if (!Consent.Has("ranked")) return L.T("还没同意排位（面板会问）", "you haven't agreed to ranked yet (the panel asks)");
            int sup = Rank.Support;
            if (sup == 0) return L.T("这台大厅服务器没开武林盟主（服务器要带 --ranked）", "this lobby server has no ranked mode (start it with --ranked)");
            if (sup != 1) return L.T("正在问大厅服务器开没开排位，稍等", "still asking the lobby server whether it has ranked mode");
            if (string.IsNullOrEmpty(Rank.Pid) || Rank.Me.Length == 0) return L.T("排位账号还没和服务器登记好，稍等", "your ranked account isn't registered with the server yet");
            return "";
        }
        // 排位的等人房间现在能不能开（Tick 里自动重试用）
        bool RankWaitReady { get { return !cfg.Safe && cfg.AiRoom && cfg.AiRoomMode == "ranked" && AiRunning && waitSes == null && RankWaitWhy().Length == 0; } }

        void StopWait()
        {
            var w = waitSes;
            waitRankPending = false;
            if (w == null) return;
            waitSes = null;
            var ais = session;
            if (ais != null && ais.SpecRelay == w) ais.SpecRelay = null;
            w.Stop();
            if (Modes.IsServer(waitMode)) Upnp.Unmap();
        }

        // 等人的房间有真人来了（起：状态包或握手都算，PeerSeen）：停掉 AI（两头的连接 + LocalAi），房间转正成正式联机，
        // 连接方式换成房间的方式（面板 / 窗口都按真人联机显示）。游戏那边看到对手不再是 AI（ai=1 没了）就结束 AI 那局（main.lua）
        void CheckWait()
        {
            var w = waitSes;
            if (w == null || !w.PeerSeen) return;
            var nick = !string.IsNullOrEmpty(w.T.PeerNick) ? w.T.PeerNick : w.Wire.HelloNick;
            waitSes = null;
            waitRankPending = false;
            w.AiWaiting = false;     // 转正了，不再是 AI 等人的房间（房间列表也不再标"和 AI 对打中"）
            if (ai != null) { ai.Stop(); ai = null; }
            if (aiSession != null) { aiSession.Stop(); aiSession = null; }
            GamePipe.Remove(Path.Combine(dir, "ai"));     // 同 StopSession：AI 的内存交接不要了
            if (session != null) session.Stop();
            w.Retarget(dir);
            w.Log = x => { lastLog = DateTime.Now.ToString("HH:mm:ss ") + x; };
            session = w;
            fromAi = true;
            realNick = nick;
            lostSince = DateTime.MinValue;
            cfg.Mode = waitMode;
            cfg.Save();
            var who = string.IsNullOrEmpty(nick) ? L.T("有玩家", "A player") : nick + " ";
            lastLog = DateTime.Now.ToString("HH:mm:ss ") + who + L.T("加入了你的房间，AI 对战已关闭（", "joined your room; AI battle closed (") + Modes.Name(waitMode) + L.T("）", ")");
            ApplyLang();
            Tick();
        }

        // 从 AI 对战转来的真人联机：对方断了 BackAfter 秒 →
        // 连接方式换回 ai、重开 AI 对战。开关还开着时这个房间退回等人（文件换回 net\wait\，房间号 / 端口不变，
        // 下一个人进来照样转正）；开关关了就关掉房间。自己点"断开"不算（StopSession 清 fromAi）
        bool fromAi;
        string realNick;
        DateTime lostSince = DateTime.MinValue;
        const double BackAfter = 5.0;     // 同模组判"对方掉线"的 LOST_SEC

        void CheckBack()
        {
            var s = session;
            if (!fromAi || s == null || s.Mode == "ai") return;
            if (s.Connected)
            {
                lostSince = DateTime.MinValue;
                if (!string.IsNullOrEmpty(s.T.PeerNick)) realNick = s.T.PeerNick;
                return;
            }
            if (lostSince == DateTime.MinValue) { lostSince = DateTime.UtcNow; return; }
            if ((DateTime.UtcNow - lostSince).TotalSeconds < BackAfter) return;
            fromAi = false;
            lostSince = DateTime.MinValue;
            var nick = string.IsNullOrEmpty(realNick) ? L.T("对方", "The player") : realNick;
            var mode = s.Mode;
            session = null;
            if (cfg.AiRoom && mode == cfg.AiRoomMode)
            {
                var wdir = Path.Combine(dir, "wait");
                s.Retarget(wdir);
                s.Log = x => { lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("[等人的房间] ", "[Room] ") + x; };
                waitSes = s;
                s.AiWaiting = true;      // 退回等人：排队的人又可以直接叫上场
                waitMode = mode;
            }
            else
            {
                s.Stop();
                if (Modes.IsServer(mode)) Upnp.Unmap();
            }
            cfg.Mode = "ai";
            cfg.Save();
            try { StartAi(nick); }
            catch (Exception ex) { StopSession(); Warn(L.T("重开 AI 对战失败：", "Couldn't restart the AI battle: ") + ex.Message); return; }
            ApplyLang();
            Tick();
        }

        void SetAiRoom(bool on)
        {
            cfg.AiRoom = on;
            cfg.Save();
            if (on && AiRunning && waitSes == null) StartWait();
            else if (!on) StopWait();
            if (aiRoomBox.Checked != on) { loadingMode = true; aiRoomBox.Checked = on; loadingMode = false; }
            Tick();
        }

        void SetAiRoomMode(string m)
        {
            if (Modes.Valid(m) == "ai") { Warn(L.T("开房方式不能是本地 AI", "The room can't use Local AI")); return; }
            m = Modes.ValidRoom(m);
            if (Modes.IsBridge(m) && !Bridge.Init()) lastErr = Bridge.Error;
            bool changed = m != cfg.AiRoomMode;
            cfg.AiRoomMode = m;
            cfg.Save();
            // 换方式：开着的房间重开；还没开成（比如排位在等账号登记）也按新方式再试一次
            if (changed && cfg.AiRoom && AiRunning) StartWait();
            var arm = AiRoomModes;
            int i = Array.IndexOf(arm, m);
            if (aiRoomModeBox.SelectedIndex != i) { loadingMode = true; aiRoomModeBox.SelectedIndex = Math.Max(0, i); loadingMode = false; }
            Tick();
        }

        void StopSession()
        {
            fromAi = false;
            StopWait();
            if (ai != null) { ai.Stop(); ai = null; }
            if (aiSession != null) { aiSession.Stop(); aiSession = null; }
            GamePipe.Remove(Path.Combine(dir, "ai"));
            if (session != null) { session.Stop(); session = null; }
            Upnp.Unmap();
            WriteNone();
            if (IsHandleCreated && !IsDisposed) Tick();
        }

        // 游戏内联机面板的命令：id 变了才执行一次。 来源两个，各记各的 id：
        // 游戏经管道发的（内存，每 0.1 秒看一眼不碰硬盘）；cmd.txt（外部工具 / 第二次打开程序写的，FileSystemWatcher 说变了才读；
        // 本程序没开管道时照旧每次读）
        void PollCmd()
        {
            var gp = GamePipe.For(dir);
            if (gp != null && gp.Connected)
            {
                foreach (var t in gp.TakeCmds()) RunCmd(Kv.Parse(t), true);     // 按顺序全部执行（以前只看最新一条，同一帧的前一条会丢）
            }
            if (gp == null || cmdFileDirty)
            {
                cmdFileDirty = false;
                var d = Kv.Read(CmdPath);
                if (d != null) RunCmd(d, false);
            }
        }

        void RunCmd(Dictionary<string, string> d, bool fromPipe)
        {
            string id, cmd, v;
            if (!d.TryGetValue("id", out id) || id == (fromPipe ? lastPipeCmd : lastCmd)) return;
            if (fromPipe) lastPipeCmd = id; else lastCmd = id;
            ack = id;
            if (!d.TryGetValue("cmd", out cmd)) return;
            lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("游戏内面板：", "In-game panel: ") + cmd;
            switch (cmd)
            {
                case "mode": if (d.TryGetValue("mode", out v)) SetMode(v); break;
                case "host":
                    if (!ConsentGate(cmd, d)) break;
                    if (d.TryGetValue("bridge", out v) && v == "1" && !AutoBridge("bridge:host")) break;     // 桥接好友页"开房（好友可见）"
                    if (session == null || session.Role != "host") StartSession("host", null);
                    break;     // A
                case "join":
                    if (!ConsentGate(cmd, d)) break;     // A
                    if (d.TryGetValue("mp", out v) && v == "1") nextMpJoin = true;
                    Join(d.TryGetValue("target", out v) ? v : "", !(d.TryGetValue("remember", out v) && v == "0")); break;   // remember=0 不记最近
                case "spectate": if (!ConsentGate(cmd, d)) break; Spectate(d.TryGetValue("target", out v) ? v : ""); break;     // A     // 观战（先只有局域网 / 输入 IP）
                case "mphost":     // 多人混战 ②a：开多人房间（n = 连自己几个人，面板给 3–12）；局域网或服务器大厅
                    {
                        if (!ConsentGate("host", d, true)) break;     // A
                        int n;
                        if (!d.TryGetValue("n", out v) || !int.TryParse(v, out n)) n = MpCfg.Limit;
                        if (d.TryGetValue("bridge", out v) && v == "1" && !AutoBridge("bridge:host")) break;
                        if (Mode != "lan" && Mode != "server" && Mode != "bridge") { Warn(L.T("多人房间仅支持局域网、服务器大厅或桥接", "Multiplayer rooms only support LAN, Server Lobby or Bridge")); break; }
                        nextMpMax = Math.Max(2, Math.Min(n, MpCfg.Limit));
                        StartSession("host", null);
                    }
                    break;
                case "mpjoin":     // 多人混战 ②a：按多人加入（target = 主机 IP[:端口] 或 房间号）
                    {
                        if (!ConsentGate("join", d, true)) break;     // A
                        string target = d.TryGetValue("target", out v) ? v : "";
                        if (!AutoBridge(target)) break;
                        if (Mode != "lan" && Mode != "server" && Mode != "bridge") { Warn(L.T("多人房间仅支持局域网、服务器大厅或桥接", "Multiplayer rooms only support LAN, Server Lobby or Bridge")); break; }
                        nextMpJoin = true;
                        if (Mode == "server")
                        {
                            Join(target, !(d.TryGetValue("remember", out v) && v == "0"));
                        }
                        else if (Mode == "bridge")
                        {
                            ulong bid = Addr.BridgeId(target);
                            if (bid == 0) { Warn(L.T("这不是有效的桥接 ID：", "Not a valid bridge ID: ") + target); break; }
                            StartSession("client", null, bid);
                        }
                        else
                        {
                            var mep = Addr.ParseDirect(target, Session.GamePort);
                            if (mep == null) { Warn(L.T("认不出这个地址：", "Can't resolve this address: ") + target); break; }
                            StartSession("client", mep);
                        }
                    }
                    break;
                case "duel":       // 新版 1v1（对决）：房主开 / 关对决模式（on=0/1）； 没带 cap 时观众上限 = 房间设置"观战 / 排队人数上限"（maxspec）
                case "duelset":    // 房主：这一局谁上场（fa= fb=，0 = 没人上场）
                case "duelphase":  // 房主：开打 / 打完（live=0/1；转发机只在 live=0 时按分数换）
                case "duellat":    // 只在 --duel-test 时生效：按对注入固定延迟（spec=a-b:ms[:丢包率],…；空 = 清掉）
                    if (cmd == "duel" && !d.ContainsKey("cap")) d["cap"] = Math.Max(0, MpCfg.Max - 2).ToString();     // 11-15 没带 cap：观众 = 房间人数 − 2（时跟"观战 / 排队人数上限"，现在那项只管旧版 1v1）
                    DuelCmd(cmd, d);
                    break;
                case "mptag":
                    {
                        string m, tm, st;
                        d.TryGetValue("m", out m);
                        d.TryGetValue("tm", out tm);
                        d.TryGetValue("st", out st);
                        currentMpTag = "m=" + (m ?? "elim") + ";tm=" + (tm ?? "0") + ";st=" + (st ?? "idle");
                        if (session != null)
                        {
                            session.SyncMpTag(currentMpTag);
                            if (session.T is ServerLink) ((ServerLink)session.T).UpdateMpTag(m, tm, st);
                        }
                    }
                    break;
                case "rotate":     // 游戏打完一场按规矩换人； 主机下场时 who = h（第一位）/ c（第二位）谁输了
                    if (session != null) { string who; d.TryGetValue("who", out who); session.Rotate(d.TryGetValue("why", out v) ? v : "", who); }
                    break;
                case "hostdown": if (session != null) session.HostDown(); break;     // 主机也下场
                case "hostspec":
                    if (d.TryGetValue("on", out v))
                    {
                        HostSpecCfg.On = v != "0";
                        cfg.HostSpec = HostSpecCfg.On;
                        cfg.Save();
                        lastLog = DateTime.Now.ToString("HH:mm:ss ") + (HostSpecCfg.On ? L.T("解说模式（主机只观战）已开启", "Host spectate-only enabled") : L.T("解说模式已关闭", "Host spectate-only disabled"));
                    }
                    break;
                case "specq":      // 观战者自己：排队上场（on=1）/ 只观战（on=0）。马上生效（下一个观战握手就带上）
                    if (d.TryGetValue("on", out v)) { SpecCfg.NoQueue = v == "0"; if (v != "0") SpecCfg.WantUp(); cfg.Save(); }     // 切回排队：顺带让主机 / 服务器清掉"刚下场、先别叫"
                    break;
                case "maxspec":    // 房间设置：观战 / 排队人数上限（马上生效：已经在看的超了也不赶，新来的按新上限）
                    int mx;
                    // 新版 1v1（对决）也用这一项当观众上限，所以放到 16；旧 1v1 是主机一个人转给所有观众，仍按最多 8 个收（SpecCfg.Max）
                    if (d.TryGetValue("n", out v) && int.TryParse(v, out mx) && mx >= 0 && mx <= 16)
                    {
                        cfg.MaxSpec = mx; SpecCfg.Max = Math.Min(mx, SpecCfg.OldMax); cfg.Save();
                        // 11-15 只管旧版 1v1 的观战 / 排队；新版 1v1 的观众 = 房间人数 − 2（mpsize），不再跟这一项
                    }
                    break;
                case "dz":         // 房间设置：联机省流（主机的；马上生效，下一个包起）
                    if (d.TryGetValue("on", out v)) { DzCfg.On = v != "0"; cfg.Save(); }
                    break;
                case "hostup": if (session != null) session.HostUp(); break;
                case "tospec": ToSpec(); SpecCfg.NoQueue = true; cfg.Save(); break;     // 自己下去 → 只观战（服务器大厅那条路也一样）     // 对手自己下去观战
                case "stop": StopSession(); break;
                // v0.18.6：窗口里能改的设置，游戏内面板也都能改
                case "name":
                    if (d.TryGetValue("name", out v) && v.Trim().Length > 0)
                    {
                        v = v.Trim();
                        if (v.Length > 24) v = v.Substring(0, 24);
                        cfg.Name = v; nameBox.Text = v; cfg.Save();
                    }
                    break;
                case "port":
                    int p;
                    if (d.TryGetValue("port", out v) && int.TryParse(v.Trim(), out p) && p > 0 && p < 65536) { cfg.Port = p; portBox.Text = p.ToString(); cfg.Save(); }
                    else Warn(L.T("端口要是 1–65535 的数字", "The port must be a number from 1 to 65535"));
                    break;
                case "secret":   // v0.22.0 联机密码；空 = 清掉（不签名）。联机中不能改：签名是每次联机开始时定的
                    if (d.TryGetValue("secret", out v))
                    {
                        if (session != null) { Warn(L.T("联机中不能改联机密码，先断开", "Disconnect before changing the connection password")); break; }
                        cfg.Secret = v.Trim(); secretBox.Text = cfg.Secret; cfg.Save();
                        lastLog = DateTime.Now.ToString("HH:mm:ss ") + (cfg.Secret.Length > 0 ? L.T("联机密码已设置", "Connection password set") : L.T("联机密码已清除", "Connection password cleared"));
                    }
                    break;
                case "ailevel":   // 本地 AI 难度 0–2；对战中也能换
                    {
                        int lv;
                        if (d.TryGetValue("level", out v) && int.TryParse(v.Trim(), out lv) && lv >= 0 && lv < LocalAiSupport.LevelCount)
                        {
                            cfg.AiLevel = lv; cfg.Save();
                            aiBox.SelectedIndex = lv;
                            if (ai != null) ai.Level = lv;
                            if (session != null) session.UpdateMpAiConfig(cfg.AiLevel, cfg.AiLoad);
                            lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("AI 难度：", "AI difficulty: ") + LocalAiSupport.LevelName(lv);
                        }
                    }
                    break;
                case "aiload":   // AI 装备：w=<族>|<路径> e=<影之武装路径或空> o=<皮肤行名或空> med=<0–10> acc=<- 或 GA,GA>；给了哪个改哪个，对战中也能换
                    {
                        var nl = cfg.AiLoad.Clone();
                        bool ok = true;
                        foreach (var k in nl.CmdKeys) if (d.TryGetValue(k, out v)) ok &= nl.Set(k, v);     // F5：键名归游戏适配
                        if (!ok) { Warn(L.T("AI 装备的值不合法，没改", "Invalid AI loadout value; nothing changed")); break; }
                        cfg.AiLoad = nl; cfg.Save();
                        if (ai != null) ai.Load = nl.Clone();
                        if (session != null) session.UpdateMpAiConfig(cfg.AiLevel, cfg.AiLoad);
                        lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("AI 装备已更新", "AI loadout updated");
                    }
                    break;
                case "airoom":       // AI 对战时同时开房等真人：on=0|1
                    if (d.TryGetValue("on", out v)) SetAiRoom(v == "1");
                    break;
                case "airoommode":   // 等人的房间的连接方式（不能是 ai）
                    if (d.TryGetValue("mode", out v)) SetAiRoomMode(v);
                    break;
                case "rankudp":   // 测试用：排位照旧走 UDP（假对手 fake_join --ranked 开 / 关）
                    if (d.TryGetValue("on", out v)) { Program.RankUdp = v == "1"; lastLog = DateTime.Now.ToString("HH:mm:ss ") + "rankudp=" + v; }
                    break;
                case "relay":   // 的"允许中转"0.34 起没有了（最终方案 3）：命令留着不报错，但只会关
                    cfg.Relay = false; cfg.Save();
                    break;
                case "upnp":
                    if (d.TryGetValue("on", out v)) { cfg.Upnp = v == "1"; upnpBox.Checked = cfg.Upnp; cfg.Save(); }
                    break;
                case "server":   // 大厅服务器地址（服务器大厅方式用）；空 = 清掉
                    if (d.TryGetValue("server", out v))
                    {
                        v = v.Trim();
                        if (v.Length > 0 && LobbyServer.Resolve(v) == null) { Warn(L.T("认不出这个服务器地址：", "Can't resolve this server address: ") + v); break; }
                        cfg.Server = v; srvBox.Text = v; cfg.Save();
                    }
                    break;
                case "lang":
                    if (d.TryGetValue("lang", out v)) { L.En = v == "en"; L.Save(dir); langBox.SelectedIndex = L.En ? 1 : 0; ApplyLang(); }
                    break;
                case "copy":
                    {
                        // AI 对战时开着等人的房间：复制那个房间的地址 / 房间号
                        var a = waitSes != null ? CopyOf(waitMode, waitSes) : CopyAddr();
                        if (d.TryGetValue("v6", out v) && v == "1") a = MyV6(session);     // 面板"复制我的 IPv6 地址"
                        if (d.TryGetValue("v4", out v) && v == "1") { string np; a = MyV4(session, out np); }     // 11-15 面板"复制我的 IPv4 地址"
                        if (a.Length == 0 || a.StartsWith(":")) { Warn(L.T("现在没有可复制的地址（", "No address to copy yet (") + MyAddr() + L.T("）", ")")); break; }
                        try { Clipboard.SetText(a); lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("已复制：", "Copied: ") + a; }
                        catch (Exception ex) { Warn(L.T("复制失败：", "Copy failed: ") + ex.Message); }
                    }
                    break;
                case "ranktop":   // 排行榜第几页
                    {
                        int pg;
                        Rank.RequestTop(d.TryGetValue("page", out v) && int.TryParse(v.Trim(), out pg) ? pg : 1);
                    }
                    break;
                case "rankstat":   // 统计页：kind=w 武器 / e 影之武装 / m 招式，page=N
                    {
                        int pg;
                        string kind;
                        d.TryGetValue("kind", out kind);
                        Rank.RequestStats(kind ?? "w", d.TryGetValue("page", out v) && int.TryParse(v.Trim(), out pg) ? pg : 1);
                    }
                    break;
                case "rankexport":   // 把排位密钥复制到剪贴板（换电脑用）
                    try { Clipboard.SetText(Rank.ExportKey()); lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("排位密钥已复制到剪贴板，别发给别人", "Ranked key copied; keep it private"); }
                    catch (Exception ex) { Warn(L.T("复制失败：", "Copy failed: ") + ex.Message); }
                    break;
                case "rankimport":   // 从剪贴板导入排位密钥
                    {
                        string clip = "", why;
                        try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(); } catch (Exception) { }
                        if (Rank.ImportKey(clip, out why)) lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("排位密钥已导入", "Ranked key imported");
                        else Warn(why);
                    }
                    break;
                case "bridgetest":   // 桥接连接自检（单机），结果在 net\steam_conn.log
                    if (session != null || waitSes != null) Warn(L.T("先断开联机再做桥接自检", "Disconnect before the bridge self-test"));
                    else
                    {
                        string tv;
                        ulong target = 0;
                        if (d.TryGetValue("target", out tv)) target = Addr.BridgeId(tv);
                        if (Bridge.SelfTest(target)) lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("桥接自检开始，约 12 秒，结果写进 net\\steam_conn.log", "Bridge self-test started (~12 s), see net\\steam_conn.log");
                        else Warn(Bridge.Error);
                    }
                    break;
                case "block":      // 拉黑最近第 who 个对手（1 = 当前 / 最近）
                    {
                        var o = OppAt(d);
                        if (o == null) { lastLog = L.T("没有可拉黑的对手（还没收到对方的包）", "No opponent to block yet"); break; }
                        if (!Blacklist.Add(o)) { lastLog = L.T("对方没有可用的标识，拉黑不了", "Nothing identifies this opponent; cannot block"); break; }
                        lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("已拉黑：", "Blocked: ") + (o.Nick.Length > 0 ? o.Nick : o.Uid);
                        NetLog.W("拉黑：" + (o.Uid.Length > 0 ? "uid " : "") + (o.Sh.Length > 0 ? "sh " : ""));
                        // 拉黑的是自己正连着的主机：断开（自己是主机时收包那一层会把他拒掉，不用断）
                        var cs = session;
                        if (cs != null && cs.Role != "host" && recentOpp.Count > 0 && recentOpp[0] == o) StopSession();
                        // 自己是服务器大厅的主机、拉黑的是正连着的对手：马上放掉这条连接（房间立刻报空，被拉黑的人再加入时直接被拒）
                        if (cs != null && cs.Role == "host" && recentOpp.Count > 0 && recentOpp[0] == o)
                        {
                            var sl = cs.T as ServerLink;
                            if (sl != null) sl.KickReq = true;
                        }
                    }
                    break;
                case "unblock":    // 解除黑名单第 i 个（1 起）
                    {
                        int bi;
                        if (d.TryGetValue("i", out v) && int.TryParse(v, out bi) && Blacklist.Remove(bi - 1)) lastLog = L.T("已从黑名单移除", "Removed from the blacklist");
                    }
                    break;
                case "safemode":       // 0.34 安全模式 L0（本地硬开关）：开 = 断开、只留局域网 / 本地 AI、桥接平台全关
                    cfg.Safe = d.TryGetValue("on", out v) && v == "1"; cfg.Save();
                    if (cfg.Safe) { if (session != null && Mode != "lan" && Mode != "ai") StopSession(); if (waitSes != null && waitMode != "lan") StopWait(); if (Mode != "lan" && Mode != "ai") SetMode("lan"); }
                    EnforceLevel();
                    break;
                case "bridgeroom":      // 0.34 最终方案 3：房间设置"对局走 " + Bridge.Svc（Lua 那边已二次确认 steam_match）
                    {
                        int bm;     // mode=0/1/2；旧面板发 on=1/0 → 一直桥接 / 关闭
                        if (d.TryGetValue("mode", out v) && int.TryParse(v, out bm) && bm >= 0 && bm <= 2) cfg.BridgeMode = bm;
                        else if (d.TryGetValue("on", out v)) cfg.BridgeMode = v == "1" ? 2 : 0;
                        cfg.SyncMeshPrefs();
                        cfg.Save();
                    }
                    break;
                case "bridgefriends":   // 0.34 最终方案 2：在"最近"页显示平台好友 / 向好友公开"正在开房"（Lua 那边已二次确认 steam_friends）
                    cfg.BridgeFriends = d.TryGetValue("on", out v) && v == "1"; cfg.Save(); ApplyPresence();
                    if (cfg.BridgeFriends && !cfg.Safe && !Bridge.Init()) lastErr = Bridge.Error;     // 0.34 评审：不在桥接平台方式下桥从没 Init 过，好友列表会一直空
                    break;
                case "mpsize":         // 房主在房间里改人数上限（不在多人房 / 不是房主 = 只提示）
                    {
                        int n;
                        if (d.TryGetValue("n", out v) && int.TryParse(v, out n)) Session.ApplyMpSize(session, n, m => Warn(m));
                    }
                    break;
                case "mpai":           // 多人本地 AI 数量 n=<0..MpMax-1>
                    {
                        int n;
                        if (d.TryGetValue("n", out v) && int.TryParse(v, out n))
                            Session.ApplyMpAi(session, n, cfg.AiLevel, cfg.AiLoad, m => Warn(m));
                    }
                    break;
                case "hideip":         // 多人房隐藏我的 IP（下次开 / 进多人房生效）
                    cfg.HideIp = d.TryGetValue("on", out v) && v == "1"; MpMesh.HideIpPref = cfg.HideIp; cfg.Save();
                    break;
                case "hide":           // 0.34 隐身：登记 / 广播不带主机标记，不写富状态
                    cfg.Hide = d.TryGetValue("on", out v) && v == "1"; Ident.Hidden = cfg.Hide; cfg.Save(); ApplyPresence();
                    break;
                case "clearhist":      // 0.34 清除本地联机记录：history.txt + netcfg 里的最近地址
                    History.Clear(); cfg.ClearRecent();
                    lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("已清除本地联机记录", "Local play history cleared");
                    break;
                case "ext":       // X2 扩展包 → 自己的联机程序插件（Lua API.lobby.cmd 已查过同意；这里 send 时再查一次）
                    if (d.TryGetValue("plugin", out v) && !Ext.Cmd(v, d)) lastLog = L.T("扩展插件没载入：", "Extension plugin not loaded: ") + v;
                    break;
                case "cfgfetch":  // 0.34 最终方案 4：玩家在面板点"获取服务器数值表"（Lua 那边已二次确认）
                    cfgFetch.Request();
                    break;
                case "refresh":   // 游戏内面板打开房间列表页：马上重新问一次房间
                    if (Modes.IsServer(Mode)) lobbyList.Refresh();
                    break;
                case "show":
                    Show();
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    Activate();
                    break;
            }
            Tick();
        }

        // 0.34 富状态 / 好友列表只在玩家开了"显示 " + Bridge.Svc + " 好友"且没隐身时才允许（桥命令 R），下线标志 L1 / L2 时一律关
        void ApplyPresence()
        {
            Bridge.AllowPresence(cfg.BridgeFriends && !cfg.Hide && !cfg.Safe);
        }

        // 安全模式（本地一键，只留局域网 / 本地 AI）开关变了就执行：开 = 关等人房间、断开非局域网 / AI 的会话、方式退回 lan；关 = 恢复富状态。
        // 服务器下发的下线标志（L1 / L2）X3 删了
        static string SafeMsg() { return L.T("安全模式：只留局域网 / 本地 AI", "Safe mode: LAN / local AI only"); }
        int enforced = -1;
        void EnforceLevel()
        {
            int lv = cfg.Safe ? 2 : 0;
            if (lv == enforced) return;
            enforced = lv;
            ApplyPresence();
            var s = session;
            if (lv >= 2)
            {
                if (waitSes != null && waitMode != "lan") { NetLog.W("安全模式：关掉等人的房间"); StopWait(); }
                if (s != null && s.Mode != "lan" && s.Mode != "ai") { NetLog.W("安全模式：断开当前会话"); StopSession(); }
                if (Mode != "lan" && Mode != "ai") { cfg.Mode = "lan"; cfg.Save(); ApplyLang(); }
                lastLog = DateTime.Now.ToString("HH:mm:ss ") + SafeMsg();
            }
        }

        // 0.34 "最近"页的行：历史对手（对上现在的房间 / 好友就给加入目标），再加开了设置时正在开房的平台好友。
        // 只用当前方式已经在拿的列表（服务器大厅房间 / 局域网广播），不为这一页额外去问服务器
        List<string> RecentRows()
        {
            var rows = new List<string>();
            var byBh = new Dictionary<string, string>();
            var bySh = new Dictionary<string, string>();
            if (Modes.IsServer(Mode) && Consent.Has("server"))
                foreach (var r in lobbyList.Poll(cfg.Server))
                    if (r.Length > 9 && r[9].Length > 0 && r[3] != "1")
                        byBh[r[9]] = "room:" + r[0] + (r.Length > 4 && r[4].Trim().Length > 0 ? "@" + r[4].Trim() : "");
            if (Mode == "lan")
                foreach (var r in disc.Current())
                    if (r[6].Length > 0 && r[3] != "1")
                        byBh[r[6]] = r[0].EndsWith(":" + Session.GamePort) ? r[0].Substring(0, r[0].LastIndexOf(':')) : r[0];
            List<string[]> friends = FriendsNow();
            if (friends != null)
                foreach (var f in friends) if (f[2] == "1") bySh[Ident.BridgeHash(f[0])] = "bridge:" + f[0];
            var seen = new HashSet<string>();
            foreach (var e in History.Snapshot())
            {
                if (Blacklist.HidesRoom(e.Bh) || Blacklist.DeniesSh(e.Sh)) continue;     // 0.34 评审：拉黑的人不进"最近"
                string target = null;
                if (!(e.Bh.Length > 0 && byBh.TryGetValue(e.Bh, out target)) && !(e.Sh.Length > 0 && bySh.TryGetValue(e.Sh, out target))) target = "-";
                if (target != "-") seen.Add(target);
                rows.Add("hist|" + e.Src + "|" + e.At + "|" + Clean(e.Nick) + "|" + target);
                if (rows.Count >= 40) break;
            }
            // 桥接好友挪到单独一页（lobby.txt fn / f<i>，FriendRows）："最近"只留联机记录
            return rows;
        }

        // 0.34 评审：好友列表 15 秒读一次（说的 10–30 秒）；没开"显示桥接好友"、隐身、桥没就绪 = null（不读）
        List<string[]> FriendsNow()
        {
            if (!(cfg.BridgeFriends && !cfg.Hide && Bridge.Ready)) return null;
            if ((DateTime.UtcNow - friendsAt).TotalSeconds >= 15) { friendsAt = DateTime.UtcNow; friendsCache = Bridge.FriendsInGame(); }
            return friendsCache;
        }

        // 桥接好友页：fn / f<i> = <1 在开房 | 0 在玩没开房>|<昵称>|<能加入的目标 bridge:<ID>，没开房 = ->；开房的排前面，拉黑的不列
        List<string> FriendRows()
        {
            var rows = new List<string>();
            var friends = FriendsNow();
            if (friends == null) return rows;
            foreach (var hosting in new[] { true, false })
                foreach (var f in friends)
                {
                    if ((f[2] == "1") != hosting || Blacklist.DeniesBridge(Addr.BridgeId(f[0]))) continue;
                    bool isMpRoom = false;
                    string tgStr = "";
                    if (hosting && f.Length > 3 && !string.IsNullOrEmpty(f[3]))
                    {
                        tgStr = ParseMpCandTag(f[3], out isMpRoom);
                        if (isMpRoom)
                        {
                            roomMp["bridge:" + f[0]] = true;
                            roomMp[f[0]] = true;
                        }
                    }
                    rows.Add((hosting ? "1|" : "0|") + Clean(f[1]) + "|" + (hosting ? "bridge:" + f[0] : "-") + (hosting && isMpRoom ? "|" + tgStr : ""));
                    if (rows.Count >= 40) return rows;
                }
            return rows;
        }

        // 可加入目标：开房的平台好友 / 局域网房间 → 剪贴板里的地址 → 最近连过的 → Tailscale 在线节点
        static string ParseMpCandTag(string raw, out bool isMp)
        {
            isMp = false;
            if (string.IsNullOrEmpty(raw)) return "k=1v1";
            var parts = raw.Trim().Split(';');
            string cur = "1", max = "4", k = "mp", m = "elim", tm = "0", st = "idle", ai = "";
            if (parts.Length > 0 && parts[0].IndexOf('/') >= 0)
            {
                var slash = parts[0].Split('/');
                if (slash.Length >= 2) { cur = slash[0]; max = slash[1]; }
            }
            for (int pi = 1; pi < parts.Length; pi++)
            {
                var p = parts[pi];
                if (p.StartsWith("k=")) k = p.Substring(2);
                else if (p.StartsWith("m=")) m = p.Substring(2);
                else if (p.StartsWith("tm=")) tm = p.Substring(3);
                else if (p.StartsWith("st=")) st = p.Substring(3);
                else if (p.StartsWith("ai=")) ai = p.Substring(3);     // 房主报的 AI 个数（对局外不算人数），面板显示"+ AI N"
            }
            int maxInt;
            isMp = int.TryParse(max, out maxInt) && maxInt >= 2;
            return string.Format("k={0};n={1};max={2};m={3};tm={4};st={5}", k, cur, max, m, tm, st) + (ai.Length > 0 ? ";ai=" + ai : "");
        }

        void BuildCands()
        {
            cands.Clear();
            roomWatch.Clear();
            roomBridge.Clear();
            roomMp.Clear();
            candTags.Clear();
            var s = session;
            var seen = new HashSet<string>();
            Action<string, string, string> add = (k, label, target) => { if (seen.Add(target)) cands.Add(new[] { k, label, target }); };
            if (Mode == "bridge")
            {
                foreach (var f in Bridge.FriendsInGame())
                {
                    // 没开房的好友单独标 nohost：面板不算"可加入"、排在后面，点了直接提示（以前也标 friend，连过去注定超时）
                    if (Blacklist.DeniesBridge(Addr.BridgeId(f[0]))) continue;     // 拉黑的人
                    bool isMpRoom = false;
                    string tgStr = "";
                    if (f.Length > 3 && !string.IsNullOrEmpty(f[3]))
                    {
                        tgStr = ParseMpCandTag(f[3], out isMpRoom);
                    }
                    string target = "bridge:" + f[0];
                    if (isMpRoom)
                    {
                        roomMp[target] = true;
                        roomMp[f[0]] = true;
                    }
                    if (!string.IsNullOrEmpty(tgStr)) candTags[target] = tgStr;
                    add(f[2] == "1" ? (isMpRoom ? "room" : "friend") : "nohost", f[1] + (f[2] == "1" ? (isMpRoom ? "" : L.T("（在开房）", " (hosting)")) : L.T("（在玩，没开房）", " (playing, not hosting)")), target);
                    // 开房的好友再列一行"观战"（好友状态里看不出满没满：满了就看，没满也能先看着排队）
                    if (f[2] == "1") add("watchf", f[1], target);
                    if (f[2] == "1") roomWatch[target] = true;     // 好友状态看不出认不认观战：照试，主机不认会拒
                }
            }
            else if (Modes.IsServer(Mode))
            {
                var mine = s != null ? s.T as IRoomLink : null;
                if (Consent.Has("server"))     // 0.34 评审：没同意过"服务器大厅"就不向服务器问房间列表（旧 netcfg mode=server 启动即发 LIST 的问题）
                foreach (var r in lobbyList.Poll(cfg.Server))
                {
                    if (mine != null && mine.RoomId == r[0] && (r.Length < 5 || r[4].Trim().Length == 0)) continue;     // 不列自己开的房间
                    // 第 6 列 = 排位房间（旧服务器没有这列 = 不是）：服务器大厅只列普通房间，排位只列排位房间；第 7 列 = 称号
                    bool rk = r.Length > 5 && r[5] == "1";
                    if (rk != (Mode == "ranked")) continue;
                    if (r.Length > 9 && Blacklist.HidesRoom(r[9])) continue;     // 第 10 列 = 主机标记：拉黑的人开的房不列
                    var title = rk && r.Length > 6 && r[6].Trim().Length > 0 ? "【" + r[6].Trim() + "】" : "";
                    // 第 5 列 = 所在服务器（空 = 就是我们连的这台；否则是互通过来的别的服务器）
                    var home = r.Length > 4 ? r[4].Trim() : "";
                    // 第 8 列 = 能观战（服务器开了中转、主机认观战）：满了的房间标 watch，点了以观战者身份经服务器看
                    bool watch = r[3] == "1" && r.Length > 7 && r[7] == "1";
                    bool sr = r.Length > 10 && r[10].Trim() == "1";     // 0.34 第 11 列 = 对局走桥接平台的房
                    roomBridge[r[0].Trim().ToUpperInvariant()] = sr;
                    var target = (watch ? "watch:" : "room:") + r[0] + (home.Length > 0 ? "@" + home : "");
                    bool isMpRoom;
                    string tgStr = ParseMpCandTag(r.Length > 11 ? r[11] : "", out isMpRoom);
                    if (isMpRoom)
                    {
                        roomMp[r[0].Trim().ToUpperInvariant()] = true;
                        roomMp["room:" + r[0].Trim().ToUpperInvariant()] = true;
                        roomMp[target] = true;
                    }
                    candTags[target] = tgStr;
                    roomFallback[r[0].Trim().ToUpperInvariant()] = r.Length > 10 && r[10].Trim() == "2";     // = 直连失败时走桥接
                    roomFbPid[r[0].Trim().ToUpperInvariant()] = ParseHostPidTag(r.Length > 11 ? r[11] : "");     // 多人房房主编号（hb=，桥接监听口跟着它）
                    // 新版房间满了 = 观众位也满了（观众算在房间人数里），不再标"观战"（以前点了经服务器转发看）
                    add(r[3] != "1" ? (sr ? "sroom" : "room") : watch && !isMpRoom ? "watch" : "full", (home.Length > 0 ? L.T("[互通] ", "[LINKED] ") : "") + title + r[1] + L.T("  地图 ", "  map ") + r[2]
                                + (r[3] == "1" ? L.T("  已满", "  full") : "") + L.T("  房间号 ", "  code ") + r[0],
                        target);
                    // 没满的房间记下能不能观战（"只观战"开着时点它 = 观战）；和 AI 对打时不再单列"观战"行
                    if (r[3] != "1") roomWatch["room:" + r[0] + (home.Length > 0 ? "@" + home : "")] = r.Length > 7 && r[7] == "1";
                }
            }
            else if (Mode == "lan")
            {
                foreach (var r in disc.Current())
                {
                    // 不列自己开的房间（同一台机器的每张网卡都会广播一份）
                    if (s != null && s.Role == "host" && r[0].EndsWith(":" + s.T.LocalPort) && (myIps.Contains(r[0].Substring(0, r[0].LastIndexOf(':'))) || r[0].StartsWith("127.0.0.")))
                        continue;
                    var addr = r[0].EndsWith(":" + Session.GamePort) ? r[0].Substring(0, r[0].LastIndexOf(':')) : r[0];
                    if (Blacklist.HidesRoom(r[6])) continue;
                    bool isMpRoom;
                    string tgStr = ParseMpCandTag(r.Length > 7 ? r[7] : "", out isMpRoom);
                    if (isMpRoom) roomMp[addr] = true;
                    candTags[addr] = tgStr;
                    // 满了、主机又认观战的房间标 watch（面板上能点"观战"），旧版主机的满房照旧 full（灰）
                    add(r[3] != "1" ? "room" : r[4] == "1" && !isMpRoom ? "watch" : "full",     // 同服务器大厅
                        r[1] + "  " + addr + L.T("  地图 ", "  map ") + r[2] + (r[3] == "1" ? L.T("  已满", "  full") : ""), addr);
                    if (r[3] != "1") roomWatch[addr] = r[4] == "1";
                }
            }
            if (Mode == "ai") return;     // 本地 AI 没有可加入的目标
            string clip = "";
            try { if (Clipboard.ContainsText()) clip = Clipboard.GetText().Trim(); } catch (Exception) { }
            if (Modes.IsServer(Mode))
            {
                if (LooksLikeRoom(clip)) add("clip", L.T("剪贴板：房间号 ", "Clipboard: room ") + clip, "room:" + clip.ToUpperInvariant());
                else if (Addr.LooksLikeIp(clip) && Mode == "server") add("clip", L.T("剪贴板：IP 直连 ", "Clipboard: direct IP ") + clip, clip);
            }
            else if (Modes.IsBridge(Mode) ? Addr.BridgeId(clip) != 0 : Addr.LooksLikeIp(clip))
                add("clip", L.T("剪贴板：", "Clipboard: ") + clip, Modes.IsBridge(Mode) ? "bridge:" + Addr.BridgeId(clip) : clip);
            foreach (var r in cfg.RecentOf(Mode))
                add("recent", L.T("最近：", "Recent: ") + (r.StartsWith("bridge:") ? Bridge.FriendName(Addr.BridgeId(r)) : r), r);
            if (Mode == "lan")
                foreach (var p in Tailscale.Peers()) add("peer", "Tailscale：" + p[1] + "  " + p[0], p[0]);
        }

        // 服务器大厅的房间号：6 位，字母表见服务端 newId
        static bool LooksLikeRoom(string s)
        {
            if (s == null || s.Length != 6) return false;
            foreach (var c in s.ToUpperInvariant()) if ("ABCDEFGHJKMNPQRSTUVWXYZ23456789".IndexOf(c) < 0) return false;
            return true;
        }

        ServerLink MyServerLink { get { var s = session; return s != null ? s.T as ServerLink : null; } }

        // 你的地址（发给朋友）：按连接方式挑
        string MyAddr() { return AddrOf(Mode, session); }

        // m = 连接方式，s = 那种方式的主机连接（等人的房间也用这个）
        string AddrOf(string m, Session s)
        {
            switch (m)
            {
                case "bridge": return Bridge.Ready ? Bridge.MyName + "  " + Bridge.Name + " ID " + Bridge.MyId : L.T("（桥接没连上）", "(bridge not connected)");
                case "server":
                case "ranked":
                    {
                        var sl = s != null ? s.T as IRoomLink : null;
                        if (cfg.Server.Length == 0) return L.T("（没填大厅服务器：设置里填，可自己架设）", "(no lobby server: set or host one)");
                        // 登记上了只显示房间号（朋友从列表 / 房间号加入，用不着 IP）；还没登记上才给 IP 直连的地址兜底
                        if (sl != null && s.Role == "host")
                            return sl.RoomId.Length > 0 ? L.T("房间号 ", "Room ") + sl.RoomId
                                   : L.T("正在登记到大厅 ", "Listing on ") + cfg.Server + L.T("  IP 直连 ", "  direct IP ") + PublicAddr();
                        return L.T("大厅 ", "Lobby ") + cfg.Server;
                    }
                case "ai": return L.T("本机 AI 对手（不联网）", "Local AI opponent (offline)");
                default:
                    {
                        // 局域网：真实网卡地址；装了 Tailscale / ZeroTier 再带上虚拟网卡地址
                        var lan = BestIp(0);
                        var vl = BestIp(1);
                        if (vl == null) return lan ?? L.T("（没找到局域网网卡）", "(no LAN adapter)");
                        return (lan != null ? lan + "  " : "") + L.T("虚拟 ", "VLAN ") + vl;
                    }
            }
        }

        // 复制给朋友的那一段：桥接平台只要平台账号 ID，其余是 IP（广域网带端口）
        string CopyAddr() { return CopyOf(Mode, session); }

        string CopyOf(string m, Session s)
        {
            switch (m)
            {
                case "bridge": return Bridge.Ready ? Bridge.MyId.ToString() : "";
                // 房间号；还没登记上（大厅服务器连不上）时复制 公网IP:端口，朋友用"输入 IP 直连"
                case "server": { var sl = s != null ? s.T as ServerLink : null; return sl == null ? "" : sl.RoomId.Length > 0 ? sl.RoomId : PublicAddr(); }
                case "ranked": { var sl = s != null ? s.T as IRoomLink : null; return sl == null ? "" : sl.RoomId; }     // 排位不能 IP 直连
                case "ai": return "";
                // 局域网：有虚拟网卡（Tailscale / ZeroTier）时复制它——要"发给朋友"的多半不在同一个路由器下，
                // 同一个路由器下的朋友在房间列表里直接能看到房间，用不着地址
                default: return BestIp(1) ?? BestIp(0) ?? "";
            }
        }

        // IP 直连用的地址：UPnP 拿到的公网 IP（没有就本机 IP）+ 设置里的端口。
        // UPnP 没拿到公网 IPv4、本机有公网 IPv6 时给 IPv6（"[v6]:端口"，多半比内网 IPv4 有用）
        string PublicAddr()
        {
            if (Upnp.PublicIp.Length == 0 && NetBind.V6)
            {
                var v6 = CachedV6();
                if (v6 != null) return Addr.Fmt(new IPEndPoint(v6, cfg.Port));
            }
            return (Upnp.PublicIp.Length > 0 ? Upnp.PublicIp : (BestIp(0) ?? L.T("公网IP", "public-IP"))) + ":" + cfg.Port;
        }

        // 本机的公网 IPv6（发往公网时的源地址，主机回包也从它出去）。状态每秒写好几次，30 秒问一次路由
        static IPAddress v6Cache;
        static DateTime v6At = DateTime.MinValue;
        static IPAddress CachedV6()
        {
            if ((DateTime.UtcNow - v6At).TotalSeconds > 30) { v6Cache = Addr.OutboundV6(); v6At = DateTime.UtcNow; }
            return v6Cache;
        }

        // "复制我的 IPv6 地址"：开着房就用房间真正监听的端口，没开房按这种方式会监听的端口；没有公网 IPv6 / 关了 IPv6 = ""
        string MyV6(Session s)
        {
            if (!NetBind.V6 || Mode == "ai" || Mode == "ranked" || Modes.IsBridge(Mode)) return "";
            if (s != null && s.Role != "host") return "";
            var v6 = CachedV6();
            if (v6 == null) return "";
            int port = s != null ? s.T.LocalPort : Modes.IsServer(Mode) ? cfg.Port : Session.GamePort;
            // 房间走桥接时（BridgeRoomLink / Bridge）没有 UDP 端口（LocalPort = 0），本机根本没监听，
            // 给出去的 "[v6]:0" 别人直连只会"认不出这个地址"——不给，面板就不显示复制按钮
            if (port <= 0) return "";
            return Addr.Fmt(new IPEndPoint(v6, port));
        }

        // 11-15 用户："联机界面加一项复制 ipv4 地址（没开 upnp 就提示需要成功开启 upnp 才能获取公开 ip 地址）"。
        // 本机只知道内网 IPv4，公网的只有 UPnP 成功时路由器报的那个（不去问外部服务）。只给服务器大厅的房主（IP 直连只有服务器大厅有）。
        // notPublic = "1"：路由器报的外网地址是内网 / 运营商 NAT 段（多层路由），朋友照这个连不进来——照样给，面板提醒
        string MyV4(Session s, out string notPublic)
        {
            notPublic = "";
            if (Mode != "server" || s == null || s.Role != "host") return "";
            IPAddress ip;
            if (Upnp.PublicIp.Length == 0 || Upnp.MappedPort == 0 || !IPAddress.TryParse(Upnp.PublicIp, out ip) || ip.AddressFamily != AddressFamily.InterNetwork) return "";
            var b = ip.GetAddressBytes();
            if (b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] < 128) || (b[0] == 169 && b[1] == 254) || b[0] == 0) notPublic = "1";
            return Upnp.PublicIp + ":" + Upnp.MappedPort;
        }

        static string BestIp(int score)
        {
            foreach (var r in IpRows()) if (r.Key == score) return r.Value[0];
            return null;
        }

        string AddrHint()
        {
            switch (Mode)
            {
                case "bridge":
                    return L.T("对方在你的好友列表里、也开着游戏和这个模组，选“桥接好友”即可看到你开的房。不用 IP、不用端口转发。",
                               "Your friend sees your room in their list (both need the game with this mod running). No IP or port forwarding needed.")
                           + (Bridge.Ready ? "" : "\n" + Bridge.Error);
                case "server":
                    return L.T("房间登记在大厅服务器上（右上角填地址）。加入时先试直连（打洞），" + ServerLink.Tries + " 次不通自动改走服务器中转。把房间号发给朋友也行。",
                               "Rooms are listed on the lobby server (address at top right). Joining tries a direct link first (hole punching); after " + ServerLink.Tries + " failures it relays through the server.")
                           + L.T("\n服务器连不上时：主机把路由器的 UDP " + cfg.Port + " 转发到本机（或勾 UPnP），朋友在\"加入\"里填 公网IP:端口 直连。",
                                 "\nIf the server is down: the host forwards UDP " + cfg.Port + " (or ticks UPnP); the friend enters public-IP:port to join directly.")
                           + (Upnp.Status.Length > 0 ? "\n" + Upnp.Status : "")
                           + (lobbyList.Error.Length > 0 ? "\n" + lobbyList.Error : "");
                case "ranked":
                    return L.T(GameInfo.RankedZh + (GameInfo.RankedZh == "排位" ? "" : "（排位）") + "：房间登记在大厅服务器上，和服务器大厅一样建房 / 加入，但只列排位房间；对局默认直连，按房间设置里的\"对局走桥接\"三档走（没装桥接扩展包时一律直连）。打完两边各报结果，对上才记分（Elo，每天同一对人记 3 场）；规则由服务器定（游戏内面板的\"房间设置\"里能看到）。",
                               "Ranked: rooms on the lobby server, host / join like the lobby server but only ranked rooms are listed. Matches go direct by default and follow the \"play over bridge\" setting (direct only without the bridge extension). Both sides report the result; it counts only if they agree (Elo, 3 rated matches per pair per day). The server sets the rules (see Room rules in the in-game panel).")
                           + (Rank.Me.Length > 0 ? "\n" + L.T("我的分数 ", "My rating ") + Rank.Me.Split('|')[0] : "")
                           + (Rank.Error.Length > 0 ? "\n" + Rank.Error : "")
                           + (lobbyList.Error.Length > 0 ? "\n" + lobbyList.Error : "");
                case "ai":
                    return L.T("和电脑里的 AI 打（照抄你的攻击属性和最大血量，武器 / 影之武装等在游戏内面板的\"AI 装备\"里选），不联网、不用别人。点\"开始 AI 对战\"后 AI 走到你身边，按 F1 开打。",
                               "Fight a computer opponent (copies your attack stats and max health; pick its gear in AI loadout in the in-game panel). Offline. Click \"Start AI battle\", then press F1 to fight.")
                           + L.T("\n勾\"同时开房等真人\"：打 AI 的同时用右边选的方式开一个房间，有人进来就结束 AI 对战、改成和他联机。",
                                 "\nTick \"Also host a room\" to host a room (method on the right) while fighting the AI; when someone joins, the AI battle ends and you play them.")
                           + (waitSes != null ? "\n" + L.T("等人的房间：", "Room: ") + AddrOf(waitMode, waitSes) : "");
                default:
                    return L.T("同一个路由器下，或两边装了同一个 Tailscale / ZeroTier 网络，房间会自动出现在列表里；也可以把地址发给朋友手动加入。",
                               "On the same router, or both on the same Tailscale / ZeroTier network, rooms show up in the list; or send your address.")
                           + "\n" + LocalIps();
            }
        }

        // 服务器大厅的排队要换连接：观战（WatchLink）轮到上场 → 带口令重新加入；客机被主机换下 → 改成观战连接
        void QueueSwitch()
        {
            var s = session;
            if (s == null) return;
            var wl = s.T as WatchLink;
            if (wl != null && wl.TurnToken != null && SpecCfg.NoQueue) wl.TurnToken = null;     // 只观战：服务器叫上场不理
            if (wl != null && wl.TurnToken != null && wl.SpecMode)
            {
                nextToken = wl.TurnToken;
                var room = wl.RoomId;
                NetLog.W("排队：轮到上场，用口令加入房间 " + room);
                StartSession("client", null, 0, room);
                return;
            }
            var sl = s.T as ServerLink;
            if (sl != null && s.Role == "spec" && sl.RoomId.Length > 0)
            {
                nextDown = true;
                var room = sl.RoomId;
                NetLog.W("排队：被换下场，改成观战房间 " + room);
                StartSession("spec", null, 0, room);
            }
        }

        // 当前 / 最近的对手（最多 5 个，新的在前），面板拉黑 / 举报用。AI 不算；观战时记的是主机
        readonly Dictionary<string, bool> roomBridge = new Dictionary<string, bool>();   // 0.34 房间号 → 房主开了"对局走 " + Bridge.Svc（房间列表第 11 列）
        readonly Dictionary<string, bool> roomMp = new Dictionary<string, bool>();
        readonly Dictionary<string, string> candTags = new Dictionary<string, string>();
        string currentMpTag = "m=elim;tm=0;st=idle";
        readonly Dictionary<string, bool> roomFallback = new Dictionary<string, bool>();     // 房间号 → 主机设了"直连失败时走桥接"（列表第 11 列 = 2）
        readonly Dictionary<string, int> roomFbPid = new Dictionary<string, int>();     // 房间号 → 房主编号（列表标签 hb=，没有 = 1）
        static int ParseHostPidTag(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 1;
            foreach (var part in raw.Split(';'))
            {
                int v;
                if (part.StartsWith("hb=", StringComparison.Ordinal) && int.TryParse(part.Substring(3), out v) && v > 0) return v;
            }
            return 1;
        }
        int FallbackHostPidOf(string room)
        {
            if (room == null) return 1;
            int at = room.IndexOf('@');
            var code = (at >= 0 ? room.Substring(0, at) : room).Trim().ToUpperInvariant();
            if (code.StartsWith("ROOM:")) code = code.Substring(5);
            int v;
            return roomFbPid.TryGetValue(code, out v) && v > 0 ? v : 1;
        }
        bool IsFallbackRoom(string room)
        {
            if (room == null) return false;
            int at = room.IndexOf('@');
            var code = (at >= 0 ? room.Substring(0, at) : room).Trim().ToUpperInvariant();
            if (code.StartsWith("ROOM:")) code = code.Substring(5);
            bool v;
            return roomFallback.TryGetValue(code, out v) && v;
        }
        bool IsBridgeRoom(string room)
        {
            if (room == null) return false;
            int at = room.IndexOf('@');
            var code = (at >= 0 ? room.Substring(0, at) : room).Trim().ToUpperInvariant();
            if (code.StartsWith("ROOM:")) code = code.Substring(5);
            bool v;
            return roomBridge.TryGetValue(code, out v) && v;
        }
        readonly List<Opp> recentOpp = new List<Opp>();
        string autoBridgeFrom;                 // 0.34 从"最近"页连平台好友时临时切到桥接平台方式，断开后切回这个
        DateTime friendsAt = DateTime.MinValue;
        List<string[]> friendsCache = new List<string[]>();
        Opp OppAt(Dictionary<string, string> d)
        {
            string v;
            int i;
            if (!d.TryGetValue("who", out v) || !int.TryParse(v, out i)) i = 1;
            return i >= 1 && i <= recentOpp.Count ? recentOpp[i - 1] : null;
        }

        void NoteOpp(Session s)
        {
            if (s == null || s.Mode == "ai" || !s.PeerSeen) return;
            var t = s.T;
            var o = new Opp { Uid = s.Wire.PeerUid ?? "", Sh = s.Wire.PeerSh ?? "", Mode = s.Mode, Ver = s.Wire.PeerApp ?? "", Mt = s.LocalMt ?? "" };
            o.Nick = !string.IsNullOrEmpty(t.PeerNick) ? t.PeerNick : !string.IsNullOrEmpty(s.Wire.HelloNick) ? s.Wire.HelloNick : t.PeerName ?? "";
            ulong sid = t is BridgeLink ? ((BridgeLink)t).PeerBridgeId : t is BridgeRoomLink ? ((BridgeRoomLink)t).PeerBridgeId : 0;
            if (sid != 0 && o.Sh.Length == 0) o.Sh = Ident.BridgeHash(sid);     // 桥接平台验过的平台账号 ID，只留哈希（0.34）
            var rl = t as IRoomLink;
            if (rl != null) { o.Room = rl.RoomId ?? ""; o.Rk = rl.PeerRk ?? ""; }
            if (!o.HasId) return;
            var now = DateTime.UtcNow;
            var old = recentOpp.Find(x => x.Same(o));
            if (old == null) { o.First = now; old = o; }
            else
            {
                recentOpp.Remove(old);
                old.Merge(o);
            }
            old.Last = now;
            recentOpp.Insert(0, old);
            if (recentOpp.Count > OPP_MAX) recentOpp.RemoveAt(OPP_MAX);
        }

        // 房间里的每位真人也进"最近对手"（以前只记 1v1 的那一个对手，房里遇到的人没法拉黑 / 举报）。已在列表里只更新时间，不挪顺序
        const int OPP_MAX = 12;
        DateTime membersNotedAt = DateTime.MinValue;
        void NoteMembers(Session s)
        {
            if (s == null || s.Mode == "ai") return;
            var ml = s.T as IMpLink;
            if (ml == null || ml.Mesh == null) return;
            var now = DateTime.UtcNow;
            if ((now - membersNotedAt).TotalSeconds < 2.0) return;
            membersNotedAt = now;
            foreach (var m in ml.Mesh.MemberIds())
            {
                var o = new Opp { Uid = m[1], Nick = m[2], Mode = s.Mode };
                var old = recentOpp.Find(x => x.Same(o));
                if (old != null) { old.Last = now; if (o.Nick.Length > 0) old.Nick = o.Nick; continue; }
                o.First = now; o.Last = now;
                recentOpp.Insert(0, o);
                if (recentOpp.Count > OPP_MAX) recentOpp.RemoveAt(OPP_MAX);
            }
        }

        void Tick()
        {
            var s = session;
            QueueSwitch();
            s = session;
            NoteOpp(s);
            NoteMembers(s);
            // 0.34 最近一起玩过：连着的真人连了 45 秒以上 / 已开打才记（本地 history.txt，不上传）
            if (s != null && s.Mode != "ai" && s.Role != "spec" && s.Connected && !s.Wire.PeerHidden && recentOpp.Count > 0 && recentOpp[0].HasId
                && ((DateTime.UtcNow - recentOpp[0].First).TotalSeconds >= 45 || (s.LocalMt ?? "").StartsWith("fight")))
                History.Note(recentOpp[0], s.Mode);
            // 0.34 从"最近"页连平台好友时临时切到了桥接平台方式：断开后切回原来的方式
            if (autoBridgeFrom != null && s == null) { var back = autoBridgeFrom; autoBridgeFrom = null; SetMode(back); }
            // 主机说我们在它的黑名单里：断开，别一直重连
            if (s != null && s.Role != "host" && s.Wire.Denied)
            {
                NetLog.W("被房主拒绝（在对方的黑名单里），断开");
                StopSession();
                Warn(L.T("被房主拒绝（你在对方的黑名单里）", "Refused by the host (you are on their blacklist)"));
                return;
            }
            var mRef = s != null && s.Role != "host" ? s.T as IMpLink : null;
            if (mRef != null && mRef.Mesh != null && mRef.Mesh.RefusedWhy != null)
            {
                string rmsg = !string.IsNullOrEmpty(mRef.Mesh.RefusedMsg) ? mRef.Mesh.RefusedMsg : L.T("被房主拒绝：", "Refused by the host: ") + mRef.Mesh.RefusedWhy;
                NetLog.W("对决被拒绝，断开：" + rmsg);
                StopSession();
                Warn(rmsg);
                return;
            }
            var mpDenied = s != null && s.Role != "host" ? s.MpDeniedMsg : null;
            if (!string.IsNullOrEmpty(mpDenied))
            {
                NetLog.W("被房主/服务器拒绝（" + mpDenied + "），断开");
                StopSession();
                Warn(mpDenied);
                return;
            }
            var fLocal = Path.Combine(dir, "state_local.txt");
            var gp = GamePipe.For(dir);      // 走管道时 state_local.txt 不再更新
            bool gameOn = (gp != null && gp.Connected) || File.Exists(fLocal) && (DateTime.Now - File.GetLastWriteTime(fLocal)).TotalSeconds < 3;
            var w = waitSes;
            // 排位：在排位方式下（或正连着排位房间 / AI 对练同时开着排位等人的房间）往来；不在时也每 30 秒问一下服务器开没开排位（决定列不列这个标签）
            Consent.ServerAddr = cfg.Server ?? "";     // 0.34：server / ranked 的同意绑定地址
            // w 是 AI 等人房间：排位房要先把账号在服务器登记好（RHELLO / RREG 才会走），所以打开开关、选了排位就按"在用排位"问
            bool rankWant = Mode == "ranked" || (s != null && s.Mode == "ranked") || (w != null && w.Mode == "ranked")
                         || (cfg.AiRoom && cfg.AiRoomMode == "ranked");
            if (Consent.Has("play"))     // 0.34 评审：没同意过任何一项就不向服务器发包（RTOP 探测也不发）；RHELLO 带昵称 / pid，要同意过 ranked
                Rank.Poll(cfg.Server, cfg.Name, rankWant && Consent.Has("ranked") && !cfg.Safe, s);
            // 排位的规则本来就由服务器定、在"排位"同意里告知；数值表在排位方式下自动拉，不用再点"获取"。普通方式照旧手动
            bool rankedAuto = rankWant && !cfg.Safe && Consent.Has("play") && Consent.Has("ranked") && !string.IsNullOrWhiteSpace(cfg.Server);
            if (rankedAuto && (DateTime.UtcNow - cfgAutoAt).TotalSeconds >= 300) { cfgAutoAt = DateTime.UtcNow; if (!cfgFetch.Want) cfgFetch.Request(); }
            else if (!rankedAuto) cfgAutoAt = DateTime.MinValue;
            // 排位的等人房间刚才没开成（排位还在探测 / 账号还没登记好）：每 5 秒再看一次，就绪了自动开
            if (waitRankPending && DateTime.UtcNow >= waitRankAt && RankWaitReady)
            {
                waitRankAt = DateTime.UtcNow.AddSeconds(RankWaitEvery);
                StartWait();
            }
            cfgFetch.Poll(cfg.Server, dir);     // 数值表：面板点了 cfgfetch 才向服务器要（0.34）
            Ext.LocalOnly = cfg.Safe; Ext.Tick();     // X2 插件每轮一次；安全模式下插件的 send 一律拒
            EnforceLevel();
            if (ShownKey != shownSupport && !modeBox.DroppedDown)
            {
                if (Mode == "ranked" && Rank.Support == 0 && s == null)
                {
                    lastLog = DateTime.Now.ToString("HH:mm:ss ") + L.T("这台大厅服务器没开" + GameInfo.RankedLongZh + "，换回服务器大厅", "This lobby server has no ranked mode; back to the lobby server");
                    cfg.Mode = "server"; cfg.Save();
                }
                ApplyLang();     // 重填连接方式下拉框（里面会再调 Tick，shownSupport 已经更新，不会再进来）
                if (!IsHandleCreated) Tick();
                return;
            }
            if (Modes.IsBridge(Mode) || (w != null && Modes.IsBridge(waitMode)) || (cfg.BridgeFriends && Bridge.Ready)) Bridge.Pump();
            if (w != null && s != null)
            {
                w.LocalMap = s.LocalMap;     // 等人的房间不读游戏的包，地图跟 AI 对战那边（房间列表里显示）
            }
            BuildCands();
            ipsBox.Text = MyAddr() + Environment.NewLine + AddrHint().Replace("\n", Environment.NewLine);
            lblJoin.Text = Modes.IsBridge(Mode) ? L.T("加入：桥接 ID", "Join: bridge ID")
                         : Modes.IsServer(Mode) ? L.T("加入：房间号", "Join: room code") : L.T("加入：主机 IP[:端口]", "Join: host IP[:port]");
            bool srvMode = Modes.IsServer(Mode);
            bool aiMode = Mode == "ai";
            srvBox.Visible = srvMode;
            aiBox.Visible = aiMode;
            portBox.Visible = upnpBox.Visible = !srvMode && !aiMode;
            aiRoomBox.Visible = aiRoomModeBox.Visible = aiMode;
            lblPort.Text = srvMode ? L.T("大厅", "Lobby") : aiMode ? "AI" : L.T("端口", "Port");
            if (s != null)
            {
                var t = s.T;
                var gpr = GamePipe.For(dir);
                var remote = gpr != null && gpr.Connected ? (gpr.LastRemote != null ? Kv.Parse(gpr.LastRemote) : null)
                                                         : Kv.Read(Path.Combine(dir, "state_remote.txt"));
                string rmap;
                if (remote != null && remote.TryGetValue("map", out rmap)) s.RemoteMap = rmap;
                var role = s.Role == "host" ? L.T("主机", "Host") : L.T("客机", "Client");
                var nick = !string.IsNullOrEmpty(t.PeerNick) ? t.PeerNick : t.PeerName;
                var who = nick.Length > 0 ? nick + " " : "";
                var conn = s.Connected ? L.T("已连接 ", "Connected ") + who + t.PeerStr
                                       : (s.Role == "host" ? L.T("等待对方…", "Waiting for opponent…") : L.T("连接中… ", "Connecting… ") + who + t.PeerStr);
                var warn = "";
                if (s.Connected && s.RemoteMap != "?" && s.RemoteMap != s.LocalMap)
                    warn = L.T("\n⚠ 两边不在同一张地图：你在 " + s.LocalMap + "，对方在 " + s.RemoteMap + "。请先去同一张地图",
                               "\n⚠ Different maps: you are on " + s.LocalMap + ", opponent on " + s.RemoteMap + ". Go to the same map first");
                var sl = t as IRoomLink;
                if (sl != null) conn += "  [" + RouteName(sl.Route) + "]" + (sl.Error.Length > 0 ? "\n" + sl.Error : "");
                // v0.22.0：签名 / 被拒数 / 可靠事件统计；版本不一致、密码对不上时的提示
                var hint = s.Wire.Hint();
                if (hint.Length > 0) warn += "\n⚠ " + hint;
                status.Text = role + " · " + Modes.Name(s.Mode) + (t.LocalPort > 0 ? L.T("，端口 ", ", port ") + t.LocalPort : "") + "\n" + conn
                              + L.T("\n收包 ", "\nRecv ") + s.RxSeen + L.T("  丢包 ", "  Lost ") + t.Gap
                              + "  ping " + (t.PingMs >= 0 ? Kv.F(t.PingMs, "0") + "ms" : "-")
                              + (s.Wire.Signed ? L.T("  签名开 ", "  Signed ") : "  ") + s.Wire.RejText() + "\n" + s.Rel.StatText() + "\n"
                              + (gameOn ? L.T("游戏运行中，地图 ", "Game running, map ") + s.LocalMap : L.T("游戏未运行 / 未读档", "Game not running / no save loaded")) + warn
                              + (w != null ? L.T("\n同时开着房间等真人（", "\nAlso hosting a room (") + Modes.Name(waitMode) + L.T("）", ")") : "");
            }
            else
            {
                status.Text = L.T("未连接。当主机点“创建房间”；加入别人就填地址点“加入”，或双击下面的一行。\n",
                                  "Not connected. To host, click \"Create room\". To join, enter the address and click \"Join\", or double-click a row below.\n")
                              + (gameOn ? L.T("游戏运行中", "Game running")
                                        : L.T("游戏未运行 / 未读档（先开游戏也行，后开也行）", "Game not running / no save loaded (start it before or after, either works)"));
            }
            logLabel.Text = lastLog;

            // 主机 / 客机互斥：联机中只能"断开"，其余入口灰掉；没联机时"断开"灰掉
            bool busy = s != null;
            hostBtn.Enabled = joinBtn.Enabled = ipBox.Enabled = nameBox.Enabled = roomList.Enabled = modeBox.Enabled = secretBox.Enabled = !busy;
            portBox.Enabled = upnpBox.Enabled = !busy && Modes.IsServer(Mode);
            srvBox.Enabled = !busy;
            stopBtn.Enabled = busy;
            if (s == null)
            {
                roleBig.Text = L.T("未联机", "OFFLINE"); roleBig.ForeColor = Color.Gray;
                roleSub.Text = "";
            }
            else
            {
                bool host = s.Role == "host";
                roleBig.Text = host ? L.T("主  机", "HOST") : L.T("客  机", "CLIENT");
                roleBig.ForeColor = host ? Color.FromArgb(30, 110, 220) : Color.FromArgb(140, 60, 200);
                roleSub.Text = s.Connected ? L.T("● 已连接", "● Connected")
                                           : (host ? L.T("○ 等待对方加入…", "○ Waiting for a player…") : L.T("○ 正在连接主机…", "○ Connecting to host…"));
                roleSub.ForeColor = s.Connected ? Color.FromArgb(20, 150, 60) : Color.FromArgb(200, 120, 0);
            }

            roomList.BeginUpdate();
            roomList.Items.Clear();
            foreach (var c in cands) roomList.Items.Add(c[1]);
            if (cands.Count == 0)
            {
                if (Mode == "lan" && !disc.Ok)
                    roomList.Items.Add(L.T("（房间发现端口 27016 被占用，只能手动输入 IP）", "(Room discovery port 27016 is in use; enter the IP manually)"));
                else if (Mode == "bridge")
                    roomList.Items.Add(Bridge.Ready ? L.T("（没有好友在玩" + GameInfo.GameZh + "并开着房）", "(No friend is playing and hosting)") : "(" + Bridge.Error + ")");
                else if (Modes.IsServer(Mode) && lobbyList.Error.Length > 0)
                    roomList.Items.Add("(" + lobbyList.Error + ")");
                else
                    roomList.Items.Add(s != null && s.Role == "host"
                        ? L.T("（你正在当主机；这里只列别人开的房间）", "(You are hosting; only other people's rooms are listed here)")
                        : L.T("（没发现房间。手动输入地址，或者复制到剪贴板）", "(No rooms found. Enter the address, or copy it to the clipboard)"));
            }
            roomList.EndUpdate();
            WriteLobby(true);
            AutoExit(gameOn);
        }

        static string RouteName(string r)
        {
            if (r == "relay") return L.T("服务器中转", "server relay");
            if (r == "bridge") return L.T("桥接", "bridge");                                               // 排位
            if (r == "bridgewait") return L.T("正在经桥接连接", "connecting via bridge");
            if (r == "join") return L.T("向大厅服务器报到", "joining via the lobby server");
            if (r == "direct") return L.T("直连", "direct");
            if (r == "wait") return L.T("等人加入", "waiting");
            if (r == "punch") return L.T("正在打洞", "punching");
            if (r.StartsWith("punch")) return L.T("直连第 " + r.Substring(5) + "/" + ServerLink.Tries + " 次", "direct try " + r.Substring(5) + "/" + ServerLink.Tries);
            return r;
        }

        static string Clean(string v) { return (v ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "/"); }

        // net\lobby.txt：给游戏内联机面板（main.lua 的 NETUI）看
        void WriteLobby(bool alive)
        {
            var s = session;
            var sb = new StringBuilder();
            Action<string, string> kv = (k, v) => sb.Append(k).Append('=').Append(Clean(v)).Append('\n');
            kv("alive", alive ? Kv.F(Link.UnixNow(), "0.0") : "0");
            kv("ver", "1");
            kv("lang", L.En ? "en" : "zh");
            kv("mode", Mode);
            kv("role", s == null ? "none" : s.Role);
            Ext.Status(kv);     // X2 extp= / x.<名>.<k>=
            kv("connected", s != null && s.Connected ? "1" : "0");
            kv("peer", s == null ? "" : s.T.PeerStr);
            bool sMp = s != null && ((s.Role == "host" && MpCfg.Max >= 2) || s.MpJoin);
            kv("mp", sMp ? "1" : "0");
            var mhW = sMp ? (s.T as IMpLink) : null;
            kv("mpn", mhW != null ? (s.Role == "host" ? (mhW.MpPlayers().Count + 1).ToString() : mhW.MpSeen.ToString()) : "0");
            kv("mphostaddr", sMp ? s.VisibleMpHostAddr() : "");
            // 对方昵称优先；还没收到（或对方是旧版）时桥接平台用桥接平台名字，直连为空（面板退回显示地址）
            var pn = s == null ? "" : (!string.IsNullOrEmpty(s.T.PeerNick) ? s.T.PeerNick : s.T.PeerName);
            var psl = s != null && s.Mode == "ranked" ? s.T as IRoomLink : null;
            var ptitle = psl != null ? Rank.TitleOf(psl.PeerRk) : "";       // 对方是排位：昵称前加称号（称号只由服务器给）
            kv("peername", ptitle.Length > 0 && pn.Length > 0 ? "【" + ptitle + "】" + pn : pn);
            kv("ping", s != null && s.T.PingMs >= 0 ? Kv.F(s.T.PingMs, "0") : "-1");
            kv("port", s == null ? "0" : s.T.LocalPort.ToString());
            kv("myaddr", MyAddr());
            kv("myv6", MyV6(s));     // 有公网 IPv6 时给面板"复制我的 IPv6 地址"（空 = 没有 / 关了）
            { string v4np; kv("myv4", MyV4(s, out v4np)); kv("myv4np", v4np); }     // 11-15 UPnP 拿到的公网 IPv4:端口（空 = 没开 / 没成功）；myv4np=1 路由器报的不是公网地址
            kv("name", cfg.Name);
            kv("cfgport", cfg.Port.ToString());
            kv("upnpon", cfg.Upnp ? "1" : "0");
            kv("relayon", cfg.Relay ? "1" : "0");
            kv("maxspec", cfg.MaxSpec.ToString());     // 房间设置：观战人数上限
            kv("specnoq", SpecCfg.NoQueue ? "1" : "0");     // 只观战、不排队
            kv("hostspec", HostSpecCfg.On ? "1" : "0");
            kv("roomrules", s != null && !string.IsNullOrEmpty(s.RoomRulesStr) ? s.RoomRulesStr : (s != null && s.Role == "host" ? s.FormatRoomRules() : ""));
            // 联机省流：dz = 我当主机时的设置；dzhost = 主机的设置（客机看到的，-1 = 主机没说 / 旧版）；dzact = 这次连接实际在省流
            kv("dz", DzCfg.On ? "1" : "0");
            kv("dzhost", s == null ? "-1" : (s.Role == "host" ? (DzCfg.On ? "1" : "0") : s.T.Wire == null ? "-1" : s.T.Wire.PeerDzHost.ToString()));
            kv("dzact", s != null && s.DzActive ? "1" : "0");
            kv("dzwhy", s == null ? "" : s.DzWhy);
            kv("hasai", LocalAiSupport.Has ? "1" : "0");
            kv("mpai", s == null ? "0" : s.MpAiActiveCount.ToString());
            kv("ailevel", cfg.AiLevel.ToString());
            cfg.AiLoad.LobbyKv(kv);                 // AI 装备（面板按这些值标出当前选的是哪个）；键名归游戏适配（F5）
            // AI 对战时同时开房等真人：开关、开房方式、房间开着没有（aiwait=1）、房间地址（服务器大厅 = 房间号）
            var w = waitSes;
            kv("airoom", cfg.AiRoom ? "1" : "0");
            kv("airoommode", cfg.AiRoomMode);
            kv("aiwait", w != null ? "1" : "0");
            kv("aiwaitaddr", w != null ? AddrOf(waitMode, w) : "");
            // v0.22.0：联机密码只告诉游戏"设没设"，不写密码本身；rej = 被拒的包数；hint = 版本 / 密码对不上的提示
            kv("secret", cfg.Secret.Length > 0 ? "1" : "0");
            kv("rej", s != null ? s.Wire.Rejected.ToString() : "0");
            kv("hint", s != null ? s.Wire.Hint(true) : "");     // 短句（面板一行放得下）；窗口里是长句
            kv("app", Proto.App);
            kv("modes", string.Join(",", ShownModes));      // 面板只显示联机程序有的方式（发布版没有桥接平台两种）
            // 服务器大厅：srv = 设置里的地址；route = 线路（direct 直连 / relay 中转 / punch1..3 第几次直连 / wait 主机等人）；
            // room = 自己开的房间号；srverr = 服务器那边的问题（没回应 / 没这个房间），面板没联机时也要能看到
            var sl = s != null ? s.T as IRoomLink : null;
            kv("srv", cfg.Server);
            kv("route", sl != null ? sl.Route : "");
            kv("room", sl != null ? sl.RoomId : "");
            kv("srverr", !Modes.IsServer(Mode) ? "" : sl != null ? sl.Error : lobbyList.Error);
            kv("bridge", Bridge.Ready ? "ok" : (Mode == "bridge" ? Bridge.Error : "off"));
            kv("bridgesvc", Bridge.Name);     // F4c-2 桥接服务的显示名（桥接 DLL 握手报的；面板文案用）
            // 桥在不在（游戏里的桥接 DLL 开了管道 / 已连上）。0.34 起 modes= 不列 bridge，面板以前拿 hasMode("bridge") 判断
            // "对局走桥接平台 / 显示平台好友 / 自检"三行显不显示 = 永远不显示；改看这一项
            kv("bridgeav", Modes.ShowBridge && Bridge.Available ? "1" : "0");
            // 0.34 "最近"页：开关 + 行（hn / h<i> = <hist|friend>|<来源>|<时间 unix>|<昵称>|<现在能加入的目标，没有 = ->）
            kv("bridgefriends", cfg.BridgeFriends ? "1" : "0");
            kv("hide", cfg.Hide ? "1" : "0");
            kv("hideip", cfg.HideIp ? "1" : "0");
            kv("bridgeroom", cfg.BridgeMode.ToString());     // 0 关闭 / 1 直连失败时走桥接 / 2 一直桥接     // 0.34 房间设置"对局走 " + Bridge.Svc
            // 安全模式：olevel 2 = 全关（只留局域网 / 本地 AI）；omsg 原因；safe 开关。（服务器下线标志 L1 X3 删了，olevel 不再有 1）
            kv("olevel", cfg.Safe ? "2" : "0");
            kv("omsg", cfg.Safe ? SafeMsg() : "");
            kv("safe", cfg.Safe ? "1" : "0");
            var hist = RecentRows();
            kv("hn", hist.Count.ToString());
            for (int i = 0; i < hist.Count; i++) sb.Append('h').Append(i + 1).Append('=').Append(hist[i]).Append('\n');
            var frows = FriendRows();     // 桥接好友页
            kv("fn", frows.Count.ToString());
            for (int i = 0; i < frows.Count; i++) sb.Append('f').Append(i + 1).Append('=').Append(frows[i]).Append('\n');
            // 0.34 数值表（手动获取）：cfgst = 上次获取的结果；cfgh / cfgat = 本地缓存 srvcfg.txt 的哈希 / 时间
            kv("cfgst", cfgFetch.Status);
            kv("cfgh", cfgFetch.Have);
            kv("cfgat", cfgFetch.HaveAt.ToString());
            kv("upnp", Upnp.Status);
            kv("err", (DateTime.UtcNow - lastErrAt).TotalSeconds < 10 ? lastErr : "");
            kv("msg", lastLog);
            kv("ack", ack ?? "");
            // 房间列表还在取第一份：面板显示"正在获取房间…"（局域网 / 平台好友是现成的，不用等）
            kv("listing", Modes.IsServer(Mode) ? (lobbyList.Listing ? "1" : "0") : "0");
            kv("shown", Visible ? "1" : "0");
#if AOT
            kv("win", "0");     // 游戏进程里的 DLL 没有窗口：面板不显示"打开电脑上的联机窗口"
#else
            kv("win", "1");
#endif
            Rank.WriteLobby(sb, Mode == "ranked");     // rk / rkme / rkrules / rktop / rkt1..
            // 最近的对手 opN = <已拉黑 0/1>|<连接方式>|<几秒前>|<昵称>；黑名单 blN = <连接方式>|<拉黑时间 unix>|<昵称或标识>；rpt = 举报结果
            kv("opn", recentOpp.Count.ToString());
            for (int i = 0; i < recentOpp.Count; i++)
            {
                var o = recentOpp[i];
                sb.Append("op").Append(i + 1).Append('=').Append(Blacklist.Has(o) ? "1" : "0").Append('|').Append(o.Mode).Append('|')
                  .Append((long)(DateTime.UtcNow - o.Last).TotalSeconds).Append('|').Append(Clean(o.Nick.Length > 0 ? o.Nick : o.Uid)).Append('\n');
            }
            var bl = Blacklist.Snapshot();
            kv("bln", bl.Count.ToString());
            for (int i = 0; i < bl.Count && i < 40; i++)
                sb.Append("bl").Append(i + 1).Append('=').Append(Clean(bl[i].Mode)).Append('|').Append(bl[i].At).Append('|')
                  .Append(Clean(bl[i].Nick.Length > 0 ? bl[i].Nick : bl[i].Uid.Length > 0 ? bl[i].Uid : bl[i].Sh)).Append('\n');
            // v0.19.1：8 → 40（跟大厅服务器一次给的上限一样），游戏内面板的"房间列表"页自己分页
            int n = Math.Min(cands.Count, 40);
            kv("n", n.ToString());
            // 目标行自己拼：kv 会把 | 换掉
            for (int i = 0; i < n; i++)
            {
                sb.Append('t').Append(i + 1).Append('=').Append(cands[i][0]).Append('|').Append(Clean(cands[i][1])).Append('|').Append(Clean(cands[i][2])).Append('\n');
                string tg;
                if (!candTags.TryGetValue(cands[i][2], out tg) || string.IsNullOrEmpty(tg)) tg = "k=1v1";
                sb.Append("tg").Append(i + 1).Append('=').Append(tg).Append('\n');
            }
            GamePipe.Out(Path.Combine(dir, "lobby.txt"), sb.ToString());      // 连着管道只在内存里（开着文件镜像才写盘）
        }

        // 后台模式（--bg，游戏启动的）：游戏进程没了 3 秒 → 自己退出。
        // v0.18.6：30 秒 → 3 秒，窗口打开过也退。它算游戏的子进程，在跑时桥接平台认为游戏还开着、拒绝再启动
        // （18:47 实测：退游戏后马上重开，桥接平台报"游戏已在运行"）；v0.18.5 起每次开游戏都会带起它，所以必须跟着游戏一起走
        public bool NoAutoExit;     // 测试用：给了 --seconds 就到时再退，不看游戏进程
        void AutoExit(bool gameOn)
        {
            if (!bg || NoAutoExit) return;
            bool running = gameOn;
            if (!running) running = GameInfo.GameRunning();     // F5：游戏进程名由适配给
            if (running) { noGameSince = DateTime.MinValue; return; }
            if (noGameSince == DateTime.MinValue) noGameSince = DateTime.UtcNow;
            else if ((DateTime.UtcNow - noGameSince).TotalSeconds > 3) Quit();
        }

        // 退出。2026-11-04 17:28 实测：从没显示过的窗口调 Close 不触发 FormClosing / FormClosed，后台进程一直不退
        // （游戏关掉后它还挂在游戏的进程树里，桥接平台认为游戏还在运行、拒绝再启动）。显示过的走正常关闭；
        // 没显示过的自己做 FormClosing 里的收尾，再直接结束消息循环
        bool quitting;
        public void Quit()
        {
            Ext.ShutdownAll();     // X2
            if (quitting) return;
            // v0.18.7：先立标志再 Close。19:05 实测窗口打开过时关不掉：Close → FormClosing → StopSession → Tick
            // → AutoExit → Quit 又 Close 一次（窗口正在关）→ 抛异常、这次关闭被取消，进程一直挂着，桥接平台不让重开游戏
            quitting = true;
            if (Visible) { Close(); return; }
            timer.Stop(); cmdTimer.Stop(); StopSession(); disc.Close(); lobbyList.Close(); cfgFetch.Close(); Bridge.Shutdown();
            WriteLobby(false);
            Application.ExitThread();
        }

        // 只读网卡列表，不做主机名解析（Python 版用 getaddrinfo(主机名)，在有些网络环境下要卡好几秒）。
        // 排序：有默认网关的真实网卡 → Tailscale / ZeroTier → 其他 → VMware / Hyper-V 这类虚拟网卡（发给朋友没用）
        static List<KeyValuePair<int, string[]>> IpRows()
        {
            var rows = new List<KeyValuePair<int, string[]>>();
            var ov6 = NetBind.V6 ? CachedV6() : null;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    bool gw = false;
                    foreach (var g in props.GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) gw = true;
                    var tag = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    foreach (var ua in props.UnicastAddresses)
                    {
                        // 公网 IPv6 只列系统往外发包用的那一个（一张网卡常有固定 + 临时好几个，主机回包用的是这个）；
                        // 排在虚拟局域网后面，BestIp(0/1) 不会挑到它。链路本地 / 内网 IPv6 跨网用不上，不列
                        if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            if (ov6 != null && ua.Address.Equals(ov6))
                                rows.Add(new KeyValuePair<int, string[]>(3, new[] { "[" + ua.Address + "]", ni.Name }));
                            continue;
                        }
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var a = ua.Address.ToString();
                        if (a.StartsWith("169.254.")) continue;
                        int score = 5;
                        if (tag.Contains("vmware") || tag.Contains("virtualbox") || tag.Contains("vethernet") || tag.Contains("hyper-v")) score = 9;
                        else if (tag.Contains("tailscale") || tag.Contains("zerotier") || Addr.IsVirtualLan(a)) score = 1;
                        else if (gw) score = 0;
                        rows.Add(new KeyValuePair<int, string[]>(score, new[] { a, ni.Name }));
                    }
                }
            }
            catch (Exception) { }
            rows.Sort((x, z) => x.Key.CompareTo(z.Key));
            return rows;
        }

        static string LocalIps()
        {
            var parts = new List<string>();
            foreach (var r in IpRows())
            {
                var note = r.Key == 0 ? L.T("（局域网）", " (LAN)")
                         : r.Key == 1 ? L.T("（虚拟局域网）", " (virtual LAN)")
                         : r.Key == 3 ? L.T("（IPv6，朋友可用 [地址]:端口 直连）", " (IPv6: friends can join with [address]:port)")
                         : r.Key == 9 ? L.T("（虚拟机网卡，一般用不上）", " (VM adapter, usually not useful)") : "";
                parts.Add(r.Value[0] + "  " + r.Value[1] + note);
            }
            return parts.Count > 0 ? string.Join(Environment.NewLine, parts) : "?";
        }
    }


    static class Program
    {
        public static string BaseDir = AppDomain.CurrentDomain.BaseDirectory;   // AOT 版（游戏进程里的 DLL）由 AotEntry 改成 DLL 所在目录
        // 单实例锁 / 管道名后缀：exe 版空（一个 net 目录一个联机程序）；DLL 版是 "_p<游戏 pid>"——沙盒里双开时
        // 两份游戏看到的 net 目录路径一样（文件各在各的沙盒里），锁和管道却是全机共用的，不加 pid 第二份就被当成"已经开着"
        public static string InstanceSalt = "";
        public static bool SimNoDirect;     // --sim-no-direct（测试用，见文件头）
        // 测试用：排位照旧走 UDP（ServerLink， 那条路）。离线测试 / 假对手在一台电脑上没法走桥接平台（同一个桥接平台账号连不了自己），
        // 排位上报 / 记分 / 统计靠它测。--rank-udp 或 cmd=rankudp on=1 打开；不存盘、面板里没有
        public static volatile bool RankUdp;
        public static double SimLoss, SimSkew;   // --sim-loss 0..1 / --sim-skew 秒（测试用，见文件头）
        public static bool SimReorder;           // --sim-reorder（测试用）
        public static bool CliSpec;
        public static string CliHostServer, CliJoinRoom, CliName;
        public static double CliSpecUpAt = -1;     // --spec-up-at（测试用）：观战者第 N 秒把观战开关切回"排队"
        public static double CliRotateAt = -1, CliStepDownAt = -1, CliHostDownAt = -1, CliHostUpAt = -1;              // --spectate（测试用）：当观战者连 --spectate 给的主机
        public static bool CliBridge;             // --steam（测试用）：--host 时经桥接 DLL 开房
        public static int LanPort = Session.GamePort;   // --lan-port（测试用）：AI 对战时等人的房间用局域网方式时监听的端口，避开开着的游戏占的 27015

        [STAThread]
        static int Main(string[] args)
        {
            WinTimer.Begin();
            try { return Main2(args); }
            finally { WinTimer.End(); }
        }

        // 0.4.0 --print-game：读到的游戏配置（GameInfo / WireTag）写成 key=value，给适配作者和测试核对
        static int PrintGame(string path, bool backupNow)
        {
            var sb = new StringBuilder();
            Action<string, string> kv = (k, v) => sb.Append(k).Append('=').Append(v ?? "").Append('\n');
            kv("source", GameInfo.Source);
            kv("mod_name", GameInfo.ModName); kv("process", GameInfo.ProcessName);
            kv("game_zh", GameInfo.GameZh); kv("game_en", GameInfo.GameEn);
            kv("title_zh", GameInfo.TitleZh); kv("title_en", GameInfo.TitleEn);
            kv("app_version", GameInfo.AppVersion);
            kv("ranked_zh", GameInfo.RankedZh); kv("ranked_long_zh", GameInfo.RankedLongZh); kv("ranked_mode_zh", GameInfo.RankedModeZh);
            kv("wire_server", WireTag.S); kv("wire_peer", WireTag.P); kv("wire_salt", WireTag.Salt);
            kv("wire_env", WireTag.EnvPrefix); kv("wire_abi", WireTag.Abi); kv("wire_pipe", WireTag.Pipe);
            kv("backup", GameInfo.BackupSave != null ? "on" : "off");
            if (backupNow && GameInfo.BackupSave != null) kv("backup_result", GameInfo.BackupSave());
            try { File.WriteAllText(path, sb.ToString(), Kv.Utf8); return 0; } catch (Exception) { return 1; }
        }

        internal static int Main2(string[] args)
        {
            string dir = Path.Combine(BaseDir, "net"), log = null, join = null, guiJoin = null, secret = null;
            int port = 0;
            bool host = false, backup = true, bg = false, probe = false, selfTest = false;
            ulong selfTarget = 0;
            double seconds = 0;
            string printGame = null; bool backupNow = false;
            GameInfo.Load(BaseDir);     // 0.4.0：游戏的名字 / 协议名 / 版本 / 存档备份先从适配 manifest 读（用到任何名字之前）
            if (WireTag.Env("DUEL_TEST") == "1") MpMesh.TestInject = true;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--host": host = true; break;
                    case "--join": join = args[++i]; break;
                    case "--spectate": join = args[++i]; CliSpec = true; break;     // 测试：当观战者连主机
                    case "--mp": MpCfg.Max = Math.Min(int.Parse(args[++i]), MpCfg.Limit); break;     // 多人混战测试：主机的房间收几个人（连自己）
                    // 多人混战测试：按多人加入。两种写法都认：`--mp-join <地址>`（局域网测试）和
                    // `--mp-join --join 桥接平台:<平台账号 ID>`（的桥接平台多人房；不带地址时等 --join 给）
                    case "--mp-join": MpCfg.Join = true; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) join = args[++i]; break;
                    case "--mp-watch": MpCfg.Join = true; MpCfg.JoinWatch = true; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) join = args[++i]; break;     // 测试：按"观战"加入多人房（握手带 watch=1）
                    case "--duel-test": MpMesh.TestInject = true; break;                                  // 对决测试：允许 duellat 命令 / --duel-lat 注入延迟
                case "--duel-lat": MpMesh.TestInject = true; MpMesh.SetInject(args[++i]); break;      // 对决测试：按对注入固定 RTT（a-b:ms[:丢包率],…，只改测速摘要）
                case "--mp-block": MpMesh.SetBlock(args[++i]); break;                            // M3 测试：丢掉 a-b 这一对的直连包（a-b:开始秒:结束秒[:丢包率]）
                    case "--host-server": CliHostServer = args[++i]; host = true; break;   // 测试：经大厅服务器开房
                    case "--join-room": CliJoinRoom = args[++i]; join = "room"; break;     // 测试：经大厅服务器加入
                    case "--name": CliName = args[++i]; break;                              // 测试工具（tests/fake_net_dummy.py）：命令行会话用的昵称，不给 = 电脑名
                    case "--rotate-at": CliRotateAt = double.Parse(args[++i], CultureInfo.InvariantCulture); break;   // 测试：主机第 N 秒打完换人
                    case "--spec-up-at": CliSpecUpAt = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--step-down-at": CliStepDownAt = double.Parse(args[++i], CultureInfo.InvariantCulture); break;   // 测试：客机第 N 秒下去观战
                    case "--host-down-at": CliHostDownAt = double.Parse(args[++i], CultureInfo.InvariantCulture); break;   // 测试：主机第 N 秒下场
                    case "--host-up-at": CliHostUpAt = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--host-spec": HostSpecCfg.On = true; break;
                    case "--max-spec": SpecCfg.Max = int.Parse(args[++i]); break;     // 测试：观战人数上限
                    case "--no-queue": SpecCfg.NoQueue = true; break;     // 测试：只观战、不排队
                    case "--seconds": seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--dir": dir = args[++i]; break;
                    case "--print-game": printGame = args[++i]; break;     // 0.4.0：把读到的游戏配置写进这个文件后退出（适配作者核对 manifest 用）
                    case "--backup-now": backupNow = true; break;           // 0.4.0：和 --print-game 一起：按配置备份一次存档，结果写进同一个文件
                    case "--log": log = args[++i]; break;
                    case "--no-backup": backup = false; break;
                    case "--gui-join": guiJoin = args[++i]; break;
                    case "--bg": bg = true; break;
                    case "--bridge-probe": probe = true; break;
                    case "--bridge-selftest": selfTest = true; if (i + 1 < args.Length && Addr.BridgeId(args[i + 1]) != 0) selfTarget = Addr.BridgeId(args[++i]); break;
                    case "--bridge": CliBridge = true; break;
                    case "--sim-no-direct": SimNoDirect = true; break;
                    case "--rank-udp": RankUdp = true; break;
                    case "--consent-all": Consent.All = true; break;     // 0.34 测试：当 rules.txt 里全同意过（LIST / RTOP / RHELLO / 等人房间不被 Consent 挡）     // 0.34 测试：忽略下线标志
                    case "--loopback": NetBind.Loopback = true; break;
                    case "--loopback6": NetBind.Loopback = true; NetBind.Loopback6 = true; break;     // 测试：主机绑 ::1
                    case "--no-ipv6": NetBind.V6 = false; break;     // 只开 IPv4（和以前一样）
                    case "--sim-loss": SimLoss = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--sim-reorder": SimReorder = true; break;
                    case "--lan-port": LanPort = int.Parse(args[++i]); break;
                    case "--disc-port": Session.BroadcastPort = int.Parse(args[++i]); break;
                    case "--sim-skew": SimSkew = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--secret": secret = args[++i]; break;
                    case "--port": port = int.Parse(args[++i]); break;
                    case "--client-port": Session.ClientBindPort = int.Parse(args[++i]); break;
                }
            }
            if (printGame != null) return PrintGame(printGame, backupNow);
            Directory.CreateDirectory(dir);
            Bridge.LogDir = dir;     // 桥接连接诊断 net\steam_conn.log
            L.Load(dir);
            Ident.Load(dir);         // net\uid.txt
            Blacklist.Load(dir);     // net\blacklist.txt（只在启动时读这一次）
            History.Load(dir);       // 0.34 net\history.txt 最近一起玩过的人
            Consent.Init(dir);       // 0.34 评审：联机程序也看 Lua 的同意记录
            Ext.Load(dir);           // X2 扩展包的联机程序插件 ext/<名>/lobby_<名>.dll
            if (selfTest)
            {
                var w2 = new StreamWriter(log ?? Path.Combine(dir, "bridge_selftest_cli.log"), false, Kv.Utf8) { AutoFlush = true };
                int r2 = Bridge.SelfTestCli(selfTarget, m => w2.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + m));
                w2.Close();
                return r2;
            }
            if (probe)
            {
                var w = new StreamWriter(log ?? Path.Combine(dir, "bridge_probe.log"), false, Kv.Utf8) { AutoFlush = true };
                Action<string> wl = m => w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + m);
                int r = Bridge.Probe(wl);
                w.Close();
                return r;
            }
            if (host || join != null) return RunCli(host, join, seconds, dir, log, backup, secret, port);
            // 单实例（按 net 目录区分，测试时两个 --dir 可以同时开）：已经开着就让它显示窗口，自己退出
            bool created;
            var mutex = new Mutex(true, "Local\\" + GameInfo.ModName + "_Lobby_" + Fnv(Path.GetFullPath(dir).ToLowerInvariant()) + InstanceSalt, out created);
            if (!created)
            {
                if (!bg) Kv.WriteAtomic(Path.Combine(dir, "cmd.txt"), "id=show-" + DateTime.UtcNow.Ticks + "\ncmd=show\n");
                return 0;
            }
            NetLog.Start(dir, true);    // 非桥接平台方式的连接诊断 net\net_conn.log（上一份改名 net_conn.prev.log）
            NetLog.W("联机程序启动：" + NetLog.Build + " v" + Proto.App + (bg ? "（后台）" : "（窗口）"));
            var pipe = GamePipe.Start(dir, Fnv(Path.GetFullPath(dir).ToLowerInvariant()) + InstanceSalt);   // （DLL 版带 pid）
            pipe.SetDown("lang.txt", "lang=" + (L.En ? "en" : "zh") + "\n");                  // 游戏经管道拿语言（只在切换时落盘）
            // 诊断：管道的连上 / 断开 / 出错记进 net\pipe_log.txt（只在这些事发生时写，平时不写）
            var pipeLog = Path.Combine(dir, "pipe_log.txt");
            pipe.Log = m => { try { File.AppendAllText(pipeLog, DateTime.Now.ToString("MM-dd HH:mm:ss.fff ") + m + "\r\n", Kv.Utf8); } catch (Exception) { } };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm(dir, backup, guiJoin, bg);
            var hwnd = form.Handle;   // 后台模式不显示窗口，也要先建好句柄：否则 Close 不触发 FormClosing / FormClosed
            var ctx = new ApplicationContext();
            form.FormClosed += delegate { ctx.ExitThread(); };
            form.NoAutoExit = seconds > 0;
            if (seconds > 0)
            {
                var t = new System.Windows.Forms.Timer { Interval = (int)(seconds * 1000) };
                t.Tick += delegate { t.Stop(); form.Quit(); };
                t.Start();
            }
            if (!bg) form.Show();
            Application.Run(ctx);
            pipe.Stop();
            GC.KeepAlive(mutex);
            return 0;
        }

        internal static string Fnv(string s)
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }

        internal static int RunCli(bool host, string join, double seconds, string dir, string logPath, bool backup, string secret, int port)
        {
            new Cfg(dir);     // 0.34 命令行方式也读一次 netcfg：steamfriends / hide 决定桥接 DLL 的富状态 / 好友列表开关
            logPath = logPath ?? Path.Combine(dir, "lobby_cli.log");
            var w = new StreamWriter(logPath, false, Kv.Utf8) { AutoFlush = true };
            Action<string> log = m => { lock (w) w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + m); };
            Safe.OnError = (where, ex) => log("[err] " + where + "：" + ex);      // 命令行测试里线程异常不要静默
            IPEndPoint peer = null;
            ITransport t = null;
            // 测试：--steam（主机）/ --join 桥接平台:<平台账号 ID>（客机）走桥接 DLL（tests	est_steam_bridge.py 用假桥）
            if (CliBridge || (join != null && join.StartsWith("bridge:")))
            {
                if (!Bridge.Init()) { log(Bridge.Name + "：" + Bridge.Error); w.Close(); return 2; }
                log("[lobby] " + Bridge.Name + " 桥 " + Bridge.PipeName + "：" + Bridge.MyName + " " + Bridge.MyId);
                try { t = new BridgeLink(host ? 0 : Addr.BridgeId(join), Path.Combine(dir, "state_remote.txt")); }
                catch (InvalidOperationException ex) { log(Bridge.Name + "：" + ex.Message); w.Close(); return 2; }
            }
            else if (CliSpec && join.StartsWith("watch:"))
            {
                // 测试：--spectate watch:<房间号>@<服务器 IP:端口>
                var rest = join.Substring(6);
                int at = rest.IndexOf('@');
                var srvW = LobbyServer.Resolve(rest.Substring(at + 1));     // [v6]:端口 / 域名也认
                if (srvW == null) { log("认不出大厅服务器地址：" + rest.Substring(at + 1)); w.Close(); return 2; }
                t = new WatchLink(srvW, rest.Substring(0, at), Path.Combine(dir, "state_remote.txt"));
            }
            else if (CliHostServer != null && host)
            {
                // 测试：--host-server <服务器 IP:端口> 经服务器大厅开房（ServerLink），房间号写进日志
                var srvH = LobbyServer.Resolve(CliHostServer);     // [v6]:端口 / localhost / 域名也认（A + AAAA）
                if (srvH == null) { log("认不出大厅服务器地址：" + CliHostServer); w.Close(); return 2; }
                var sl = new ServerLink(srvH, null, port, "CLI", Path.Combine(dir, "state_remote.txt"), false);
                if (MpCfg.Max >= 2) sl.AllowRelay = false; else sl.AllowRelay = true;
                t = sl;
            }
            else if (CliJoinRoom != null)
            {
                // 测试：--join-room <房间号>@<服务器 IP:端口> 经服务器大厅加入
                int at = CliJoinRoom.IndexOf('@');
                var srvJ = LobbyServer.Resolve(CliJoinRoom.Substring(at + 1));
                if (srvJ == null) { log("认不出大厅服务器地址：" + CliJoinRoom.Substring(at + 1)); w.Close(); return 2; }
                var sl = new ServerLink(srvJ, CliJoinRoom.Substring(0, at), 0, "CLI", Path.Combine(dir, "state_remote.txt"), Program.SimNoDirect);
                if (MpCfg.Join) sl.AllowRelay = false; else sl.AllowRelay = true;
                t = sl;
            }
            else if (join != null)
            {
                // "1.2.3.4[:端口]"、"[v6]:端口"、裸 IPv6 都认
                peer = Addr.ParseDirect(join, Session.GamePort);
                if (peer == null) { log("认不出这个地址：" + join); w.Close(); return 2; }
            }
            NetLog.Start(dir, false);    // 测试：两个进程可能共用一个目录，不改名
            var pipe = GamePipe.Start(dir, Fnv(Path.GetFullPath(dir).ToLowerInvariant()));   // （测试里的假游戏也能连）
            pipe.Log = log;
            pipe.SetDown("lang.txt", "lang=" + (L.En ? "en" : "zh") + "\n");
            Session s;
            // 给了 --port 时按非局域网方式（不往 27016 广播房间），不占默认端口，测试可以并行跑
            try { s = new Session(host ? "host" : CliSpec ? "spec" : "client", t is ServerLink || t is WatchLink ? "server" : t != null ? "bridge" : port > 0 ? "server" : "lan", t, peer, port > 0 ? port : Session.GamePort, CliName ?? "", dir, backup, secret); }
            catch (SocketException ex) { log("开不了端口：" + ex.Message); return 2; }
            s.Log = log;
            s.Start();
            log("[lobby] " + s.Role + " 端口 " + s.T.LocalPort + " 目录 " + dir);
            var end = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
            var rotateAt = CliRotateAt >= 0 ? DateTime.UtcNow.AddSeconds(CliRotateAt) : DateTime.MaxValue;
            var stepAt = CliStepDownAt >= 0 ? DateTime.UtcNow.AddSeconds(CliStepDownAt) : DateTime.MaxValue;
            var upAt = CliSpecUpAt >= 0 ? DateTime.UtcNow.AddSeconds(CliSpecUpAt) : DateTime.MaxValue;
            var hdAt = CliHostDownAt >= 0 ? DateTime.UtcNow.AddSeconds(CliHostDownAt) : DateTime.MaxValue;
            var huAt = CliHostUpAt >= 0 ? DateTime.UtcNow.AddSeconds(CliHostUpAt) : DateTime.MaxValue;
            var last = DateTime.MinValue;
            long lastLoops = 0, lastSendLoops = 0;
            string refWhy = null, refMsg = null, cliDenied = null;
            while (DateTime.UtcNow < end)
            {
                Thread.Sleep(200);
                var mlkRef = s.T as IMpLink;
                if (s.Role != "host" && mlkRef != null && mlkRef.Mesh != null && mlkRef.Mesh.RefusedWhy != null)
                {
                    refWhy = mlkRef.Mesh.RefusedWhy;
                    refMsg = mlkRef.Mesh.RefusedMsg;
                    log("[lobby] 被房主拒绝：" + (string.IsNullOrEmpty(refMsg) ? refWhy : refMsg));
                    break;
                }
                var cmdPath = Path.Combine(dir, "cmd.txt");
                if (File.Exists(cmdPath))
                {
                    try
                    {
                        var cmdd = Kv.Read(cmdPath);
                        string c;
                        if (cmdd != null && cmdd.TryGetValue("cmd", out c))
                        {
                            if (c == "mptag")
                            {
                                string m, tm, st;
                                cmdd.TryGetValue("m", out m);
                                cmdd.TryGetValue("tm", out tm);
                                cmdd.TryGetValue("st", out st);
                                s.SyncMpTag("m=" + (m ?? "elim") + ";tm=" + (tm ?? "0") + ";st=" + (st ?? "idle"));
                                var sl = s.T as ServerLink;
                                if (sl != null) sl.UpdateMpTag(m, tm, st);
                            }
                            else if (c == "duel" || c == "duelset" || c == "duelphase" || c == "duellat")
                            {
                                Session.ApplyDuelCmd(s, c, cmdd, log);
                            }
                            else if (c == "mpsize")     // 命令行 / 测试用（游戏面板走 MainForm.RunCmd 那份）
                            {
                                string nv; int n;
                                if (cmdd.TryGetValue("n", out nv) && int.TryParse(nv, out n)) Session.ApplyMpSize(s, n, log);
                            }
                            else if (c == "mpai")       // 多人本地 AI（命令行 / 测试用）
                            {
                                string nv; int n;
                                if (cmdd.TryGetValue("n", out nv) && int.TryParse(nv, out n))
                                    Session.ApplyMpAi(s, n, 1, null, log);
                            }
                            else if (c == "ailevel")    // 命令行 / 测试用：运行中调 AI 难度
                            {
                                string lvStr; int lv;
                                if (cmdd.TryGetValue("level", out lvStr) && int.TryParse(lvStr, out lv) && lv >= 0 && lv < LocalAiSupport.LevelCount)
                                    s.UpdateMpAiConfig(lv, null);
                            }
                            else if (c == "maxspec")     // 观战 / 排队人数上限（命令行 / 测试用；游戏面板走 MainForm.RunCmd 那份）：对决开着时观众上限跟着改
                            {
                                string nv; int mx;
                                if (cmdd.TryGetValue("n", out nv) && int.TryParse(nv, out mx) && mx >= 0 && mx <= 16)
                                {
                                    SpecCfg.Max = Math.Min(mx, SpecCfg.OldMax);
                                    log("[lobby] 观战 / 排队人数上限 = " + mx);
                                }
                            }
                            else if (c == "stop")
                            {
                                try { File.Delete(cmdPath); } catch (Exception) { }
                                break;
                            }
                        }
                        File.Delete(cmdPath);
                    }
                    catch (Exception) { }
                }
                cliDenied = s.Role != "host" ? s.MpDeniedMsg : null;
                if (!string.IsNullOrEmpty(cliDenied))
                {
                    log("[lobby] 拒绝加入：" + cliDenied);
                    break;
                }
                if (DateTime.UtcNow >= rotateAt) { rotateAt = DateTime.MaxValue; log("[lobby] 打完换人：" + s.Rotate("测试")); }
                if (DateTime.UtcNow >= stepAt) { stepAt = DateTime.MaxValue; log("[lobby] 下去观战：" + s.StepDown()); }
                if (DateTime.UtcNow >= upAt) { upAt = DateTime.MaxValue; SpecCfg.NoQueue = false; SpecCfg.WantUp(); log("[lobby] 切回排队"); }
                if (DateTime.UtcNow >= hdAt) { hdAt = DateTime.MaxValue; log("[lobby] 主机下场：" + s.HostDown()); }
                if (DateTime.UtcNow >= huAt) { huAt = DateTime.MaxValue; log("[lobby] 主机回来打：" + s.HostUp()); }
                // 测试：服务器大厅排队要换连接（同 MainForm.QueueSwitch）
                var wl = s.T as WatchLink;
                var sl3 = s.T as ServerLink;
                ITransport nt = null;
                string nrole = null;
                if (wl != null && wl.TurnToken != null && SpecCfg.NoQueue) wl.TurnToken = null;
                if (wl != null && wl.TurnToken != null && wl.SpecMode)
                {
                    nt = new ServerLink(wl.Server, wl.RoomId, 0, "CLI", Path.Combine(dir, "state_remote.txt"), false) { AllowRelay = true, JoinToken = wl.TurnToken };
                    nrole = "client";
                }
                else if (sl3 != null && s.Role == "spec" && sl3.RoomId.Length > 0)
                {
                    nt = new WatchLink(sl3.Server, sl3.RoomId, Path.Combine(dir, "state_remote.txt")) { Down = true };
                    nrole = "spec";
                }
                if (nt != null)
                {
                    s.Stop();
                    s = new Session(nrole, "server", nt, null, Session.GamePort, "", dir, backup, secret);
                    s.Log = log;
                    s.Start();
                    log("[lobby] 排队换连接 → " + nrole);
                }
                if ((DateTime.UtcNow - last).TotalSeconds >= 2)
                {
                    double secs = last == DateTime.MinValue ? 0 : (DateTime.UtcNow - last).TotalSeconds;
                    long loops = s.Loops, sendLoops = s.SendLoops;
                    last = DateTime.UtcNow;
                    log("[lobby] " + (s.Connected ? "已连接 " + s.PeerStr : "未连接") + " 收包 " + s.RxSeen + " 发包 " + s.T.Tx
                        + " 地图 " + s.LocalMap + " 缺口 " + s.T.Gap + " " + s.Wire.RejText() + " " + s.Rel.StatText()
                        + (s.Wire.Hint().Length > 0 ? " 提示：" + s.Wire.Hint() : "")
                        + (s.T is Link && s.Role == "host" ? " 观战者 " + s.Spectators + " 转发 " + ((Link)s.T).SpecTx : "")
                        + (s.T is Link && s.Role == "spec" ? " 收到 主机 " + ((Link)s.T).SrcH + " 对手 " + ((Link)s.T).SrcC + " 排第 " + ((Link)s.T).QPos : "")
                        + (s.T is WatchLink ? " 收到 主机 " + ((WatchLink)s.T).SrcH + " 对手 " + ((WatchLink)s.T).SrcC + " 观战人数 " + ((WatchLink)s.T).Watchers + " " + ((WatchLink)s.T).Error : "")
                        + (s.T is ServerLink ? " 房间 " + ((ServerLink)s.T).RoomId + " 观战者 " + ((ServerLink)s.T).Watchers + " 上传 " + ((ServerLink)s.T).WatchTx : "")
                        + (s.T is Link && s.Role == "host" && MpCfg.Max >= 2 ? " 多人 " + ((Link)s.T).MpCount + " 位 收 " + ((Link)s.T).MpRx + " 转 " + ((Link)s.T).MpTx : "")
                        + (s.T is ServerLink && s.Role == "host" && MpCfg.Max >= 2 ? " 多人 " + ((ServerLink)s.T).MpCount + " 位" : "")
                        + (s.T is IMpLink && s.MpJoin ? " 多人 我是 " + ((IMpLink)s.T).MyPid + " 房间 " + ((IMpLink)s.T).MpSeen + " 人" : "")
                        + " 角色 " + s.Role + " 观战者 " + s.Spectators
                        + (secs > 0 ? " 轮询 收 " + Kv.F((loops - lastLoops) / secs, "0") + " / 发 " + Kv.F((sendLoops - lastSendLoops) / secs, "0") + " 次/秒" : ""));
                    lastLoops = loops;
                    lastSendLoops = sendLoops;
                }
            }
            s.Stop();
            pipe.Stop();
            log("[lobby] 管道：连上 " + pipe.Connects + " 次，收本机包 " + pipe.LocalIn + "，交远端包 " + pipe.RemoteOut + "，取包 " + pipe.Requests + " 次");
            Kv.WriteAtomic(Path.Combine(dir, "session.txt"), "role=none\nconnected=0\npeer=\nt=0\n"
                + (refWhy != null ? "refused=" + refWhy + "\nrefusedmsg=" + (refMsg ?? "") + "\n" : "")
                + (!string.IsNullOrEmpty(cliDenied) ? "denied=" + cliDenied + "\n" : ""));
            log("[lobby] 结束");
            w.Close();
            return 0;
        }
    }
}
