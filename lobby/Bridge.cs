// 外部联机服务的桥接传输（F4c-2）。基底不碰任何平台 SDK、不知道任何 App ID：
// 扩展包把一个"桥接 DLL"载进游戏进程（ext\<名>\init.lua 经 API.native.load），DLL 用游戏自己已经初始化好的平台服务，
// 开命名管道 \\.\pipe\fornet_bridge_<<游戏进程>.exe 的 pid> 给本程序用；本程序只说这根管道的协议（帧格式和命令见扩展包里 DLL 的源码文件头）。
// 分工：DLL 只搬数据（点对点连接、虚拟端口）和读联系人 / 在线状态；握手、可靠事件、房间、密码签名都在本程序（Wire / Session，BridgeLink）。
// 找主机：主机的 DLL 把在线状态 fornet 设成 host；加入方从 DLL 拿"正在玩同一个游戏"的联系人，看这个键。
// 桥连不上（没装扩展包、DLL 被关掉、游戏没开）时 bridge 方式不在面板里出现（Modes / ShownModes 看 Bridge.Available）。
// 显示名（Svc）由 DLL 握手时报（h 的 svc=，例如桥接平台）；没报就叫 Bridge。基底源码里不出现任何具体服务的实现。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace ForNet
{
    static class Bridge
    {
        public static string Name = "Bridge";      // 真实服务名（桥接 DLL 握手报的 svc=，如桥接平台）：只进日志和扩展包插件页；给玩家看的文字一律写"桥接"
        public const int VPort = 27015;         // 我们的 P2P 虚拟端口
        public const int Proto = 1;             // 和桥接 DLL 的协议版本（h 里的 v=）

        // 连接状态（桥接 DLL 报的连接状态码，沿用平台的枚举值）；-1 = 句柄已失效
        public const int StNone = 0, StConnecting = 1, StFindingRoute = 2, StConnected = 3, StClosedByPeer = 4, StProblem = 5;

        public static bool Ready { get; private set; }
        public static string Error = L.T("游戏里的桥接没连上", "Bridge in the game is not connected");
        public static ulong MyId { get; private set; }
        public static string MyName = "";
        public static bool LobbyOk { get; private set; }     // 桥有大厅接口（hello 的 lobby=1）
        public static string PipeName = "";

        static NamedPipeClientStream pipe;
        static Thread reader;
        static readonly object wlock = new object(), gate = new object();
        static readonly Dictionary<char, byte[]> replies = new Dictionary<char, byte[]>();
        static readonly AutoResetEvent replied = new AutoResetEvent(false);
        static readonly Queue<object[]> status = new Queue<object[]>();             // {uint 句柄, int 状态, ulong 对方}
        public static readonly Queue<KeyValuePair<uint, byte[]>> Data = new Queue<KeyValuePair<uint, byte[]>>();
        public static readonly Dictionary<uint, int> Ping = new Dictionary<uint, int>();
        static readonly Dictionary<ulong, string> names = new Dictionary<ulong, string>();
        static List<string[]> friends = new List<string[]>();
        static volatile bool presenceOk;      // 0.34：玩家开了"在最近里显示 " + Bridge.Svc + " 好友"且没隐身时才写富状态 / 读好友（桥命令 R）
        public static Action<uint, int, ulong> OnConnStatus = delegate { };       // 只有当前的 BridgeLink 订阅

        // 连接诊断：和游戏里的桥接 DLL 写同一个 net\steam_conn.log（桥那边带 [桥]，这边带 [联机]），只在事件发生时写一行。
        // 两边可能同时写：都用共享读写打开、追加
        public static string LogDir = "";
        static readonly object logLock = new object();
        static string lastDiag = "";
        public static void Diag(string m)
        {
            if (string.IsNullOrEmpty(LogDir)) return;
            try
            {
                lock (logLock)
                {
                    var b = Kv.Utf8.GetBytes(DateTime.Now.ToString("HH:mm:ss.fff") + " [联机] " + m + "\r\n");
                    using (var f = new FileStream(Path.Combine(LogDir, "bridge_conn.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        f.Write(b, 0, b.Length);
                }
            }
            catch (Exception) { }
        }
        static void DiagOnce(string m) { if (m != lastDiag) { lastDiag = m; Diag(m); } }     // 同一句不连着记（Init 会被反复调）

        // 管道名：环境变量 FORNET_BRIDGE_PIPE（测试用假桥）优先；否则每个游戏进程各一个 fornet_bridge_<pid>（管道名是桥接 DLL 的接口，1.0 前不改）
        static List<string> Candidates()
        {
            var l = new List<string>();
            var env = WireTag.Env("BRIDGE_PIPE");
            if (!string.IsNullOrEmpty(env)) { l.Add(env); return l; }
            foreach (var id in GameInfo.GamePids()) l.Add(WireTag.Pipe + id);     // F5：游戏进程名由适配给
            return l;
        }

        [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern bool WaitNamedPipeW(string name, int ms);

        // 管道在不在：不能用 File.Exists（它会真的打开管道，占掉桥唯一的那个连接位，桥以为联机程序来了又走了）。
        // WaitNamedPipe 不打开：有空位返回 true；被占着（别的联机程序连着）等 1 ms 超时，错误 121
        static bool PipeExists(string name)
        {
            try
            {
                if (WaitNamedPipeW(@"\\.\pipe\" + name, 1)) return true;
                return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 121;
            }
            catch (Exception) { return false; }
        }

        // 面板列不列桥接平台方式：桥连着，或者管道在（还没连）。5 秒内用上次的结果，免得每次画面板都查进程
        static DateTime availAt = DateTime.MinValue;
        static bool avail;
        public static bool Available
        {
            get
            {
                if (Ready || pipe != null) return true;
                if ((DateTime.UtcNow - availAt).TotalSeconds < 5) return avail;
                availAt = DateTime.UtcNow;
                avail = Candidates().Exists(PipeExists);
                return avail;
            }
        }

        public static bool Init()
        {
            lock (gate)
            {
                if (Ready) return true;
                if (pipe == null && !Connect()) { DiagOnce("连不上 " + Bridge.Name + " 桥：" + Error); return false; }
                if (!Ready)
                {
                    // 连上了但游戏的桥接平台还没就绪：再问一次（dll 就绪时也会主动推 r）
                    var h = Request('H', null, 'h', 1500);
                    if (h != null) Hello(h);
                }
                DiagOnce(Ready ? Bridge.Name + " 桥 " + PipeName + " 就绪：" + MyName + " " + MyId + (LobbyOk ? "（有大厅接口）" : "（没有大厅接口）")
                               : Bridge.Name + " 桥 " + PipeName + " 连上了但没就绪：" + Error);
                return Ready;
            }
        }

        static bool Connect()
        {
            foreach (var name in Candidates())
            {
                if (!PipeExists(name)) continue;
                // Asynchronous：读线程一直挂在 Read 上，同步句柄会把别的线程的 Write 也卡住（Windows 同步句柄的 I/O 是排队的）
                var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { c.Connect(500); }
                catch (Exception) { c.Dispose(); continue; }      // 占着（别的联机程序连着）或刚关
                pipe = c;
                PipeName = name;
                reader = Safe.Thread("bridge", ReadLoop);
                reader.Start();
                var h = Request('H', null, 'h', 1500);
                if (h == null) { Drop(L.T("桥接没回话", "Bridge did not answer")); continue; }
                Hello(h);
                return true;
            }
            Error = L.T("游戏里没有桥接（扩展包的桥接 DLL 没载入，或游戏没开）", "No bridge in the game (bridge DLL not loaded, or the game is not running)");
            return false;
        }

        static void Hello(byte[] b)
        {
            var d = Kv.Parse(Encoding.UTF8.GetString(b));
            string v;
            int ver;
            if (d.TryGetValue("svc", out v) && v.Length > 0) Name = v;     // 服务显示名（F4c-2）
            if (!d.TryGetValue("v", out v) || !int.TryParse(v, out ver) || ver != Proto)
            {
                Ready = false;
                Error = L.T("桥接版本不对（游戏里的桥接 DLL 和联机程序不是同一版）", "Bridge version mismatch (bridge DLL vs lobby)");
                return;
            }
            ulong id;
            MyId = d.TryGetValue("id", out v) && ulong.TryParse(v, out id) ? id : 0;
            MyName = d.TryGetValue("name", out v) ? v : "";
            LobbyOk = d.TryGetValue("lobby", out v) && v == "1";     // 桥拿到了大厅接口
            Ready = d.TryGetValue("ready", out v) && v == "1" && MyId != 0;
            Error = Ready ? "" : L.T("游戏里的桥接还没就绪：", "Bridge in the game is not ready: ") + (d.TryGetValue("err", out v) ? v : "");
            if (Ready) Send('R', new[] { (byte)(presenceOk ? 1 : 0) });     // 0.34 告诉桥：能不能写富状态 / 回好友列表
        }

        // 0.34 允许 / 禁止富状态和好友列表（Program 按 cfg.BridgeFriends && !cfg.Hide 调）。禁止时桥立刻清掉富状态
        public static void AllowPresence(bool on)
        {
            presenceOk = on;
            if (Ready) Send('R', new[] { (byte)(on ? 1 : 0) });
        }

        static void Drop(string why)
        {
            var p = pipe;
            if (p != null) DiagOnce(Bridge.Name + " 桥断开：" + why);
            pipe = null;
            Ready = false;
            LobbyOk = false;
            Error = why;
            availAt = DateTime.MinValue;
            try { if (p != null) p.Dispose(); } catch (Exception) { }
        }

        public static void Shutdown()
        {
            lock (gate)
            {
                if (pipe == null) return;
                SetHosting(false);
                Drop(L.T("桥接已断开", "Bridge closed"));      // dll 看到断开会关掉所有连接、清富状态
            }
        }

        static void ReadLoop()
        {
            var p = pipe;
            var hdr = new byte[5];
            try
            {
                while (true)
                {
                    if (!ReadExact(p, hdr, 5)) break;
                    int n = BitConverter.ToInt32(hdr, 1);
                    if (n < 0 || n > 1 << 20) break;
                    var b = new byte[n];
                    if (!ReadExact(p, b, n)) break;
                    Dispatch((char)hdr[0], b);
                }
            }
            catch (Exception) { }
            if (pipe == p) Drop(L.T("桥接断开了（游戏关了？）", "Bridge disconnected (game closed?)"));
        }

        static bool ReadExact(Stream s, byte[] b, int n)
        {
            int off = 0;
            while (off < n)
            {
                int k = s.Read(b, off, n - off);
                if (k <= 0) return false;
                off += k;
            }
            return true;
        }

        static void Dispatch(char t, byte[] b)
        {
            switch (t)
            {
                case 'r': Hello(b); return;
                case 't':     // 自检的结论（详细过程桥自己写进 steam_conn.log）
                    SelfTestResult = Encoding.UTF8.GetString(b);
                    Diag("自检完成：" + SelfTestResult);
                    return;
                case 's':
                    if (b.Length >= 16)
                        lock (status) status.Enqueue(new object[] { BitConverter.ToUInt32(b, 0), BitConverter.ToInt32(b, 4), BitConverter.ToUInt64(b, 8) });
                    return;
                case 'd':
                    if (b.Length >= 4)
                    {
                        var d = new byte[b.Length - 4];
                        Buffer.BlockCopy(b, 4, d, 0, d.Length);
                        lock (Data) { if (Data.Count < 512) Data.Enqueue(new KeyValuePair<uint, byte[]>(BitConverter.ToUInt32(b, 0), d)); }
                    }
                    return;
                case 'p':
                    if (b.Length >= 8) lock (Ping) Ping[BitConverter.ToUInt32(b, 0)] = BitConverter.ToInt32(b, 4);
                    return;
                default:
                    lock (replies) replies[t] = b;
                    replied.Set();
                    return;
            }
        }

        public static bool Send(char t, byte[] payload)
        {
            var p = pipe;
            if (p == null) return false;
            int n = payload == null ? 0 : payload.Length;
            var f = new byte[5 + n];
            f[0] = (byte)t;
            Buffer.BlockCopy(BitConverter.GetBytes(n), 0, f, 1, 4);
            if (n > 0) Buffer.BlockCopy(payload, 0, f, 5, n);
            try { lock (wlock) { p.Write(f, 0, f.Length); p.Flush(); } return true; }
            catch (Exception) { Drop(L.T("桥接写不进去", "Can't write to the bridge")); return false; }
        }

        // 一问一答（同一种回话同时只等一个；命令都在会话线程 / 界面线程上发，量很小）
        static byte[] Request(char t, byte[] payload, char want, int ms)
        {
            lock (replies) replies.Remove(want);
            if (!Send(t, payload)) return null;
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (true)
            {
                lock (replies)
                {
                    byte[] r;
                    if (replies.TryGetValue(want, out r)) { replies.Remove(want); return r; }
                }
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || pipe == null) return null;
                replied.WaitOne(left < TimeSpan.FromMilliseconds(50) ? left : TimeSpan.FromMilliseconds(50));
            }
        }

        static byte[] Cat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        // 处理桥推来的连接状态（会话线程 / 界面线程都会调）
        public static void Pump()
        {
            while (true)
            {
                object[] s;
                lock (status) { if (status.Count == 0) return; s = status.Dequeue(); }
                var who = (ulong)s[2];
                OnConnStatus((uint)s[0], (int)s[1], who);
            }
        }

        public static uint Listen(int vport)
        {
            lastHosting = null;     // 桥开监听时自己会改富状态，下一次 SetHosting 照发
            var r = Request('L', BitConverter.GetBytes(vport), 'l', 2000);
            return r != null && r.Length >= 4 ? BitConverter.ToUInt32(r, 0) : 0;
        }

        public static void Unlisten() { lastHosting = null; Send('U', null); }

        // 单机自检：桥另开监听口 27099、连目标（0 = 自己），看连接回调是游戏线程送来的、桥线程派发的还是只能轮询看出来；
        // 约 12 秒，过程和结论写进 net\steam_conn.log，结论另外回到 SelfTestResult。有联机会话时桥不跑
        public static volatile string SelfTestResult;
        public static bool SelfTest(ulong target)
        {
            if (!Init()) return false;
            SelfTestResult = null;
            Diag("自检：开始（目标 " + (target == 0 ? "自己" : target.ToString()) + "）");
            return Send('T', BitConverter.GetBytes(target));
        }

        public static uint ConnectP2P(ulong id, int vport)
        {
            var r = Request('C', Cat(BitConverter.GetBytes(id), BitConverter.GetBytes(vport)), 'c', 2000);
            return r != null && r.Length >= 4 ? BitConverter.ToUInt32(r, 0) : 0;
        }

        public static bool Accept(uint h)
        {
            var r = Request('A', BitConverter.GetBytes(h), 'a', 2000);
            return r != null && r.Length >= 8 && BitConverter.ToInt32(r, 4) == 1;   // k_EResultOK
        }

        public static void Close(uint h, bool linger)
        {
            Send('K', Cat(BitConverter.GetBytes(h), new[] { (byte)(linger ? 1 : 0) }));
            lock (Ping) Ping.Remove(h);
        }

        public static void SendTo(uint h, byte[] data, int flags)
        {
            Send('S', Cat(BitConverter.GetBytes(h), BitConverter.GetBytes(flags), data));
        }

        // 验收：多人房主每 0.5 秒带人数 / 状态调一次，只有内容变了才真的发给桥（富状态别刷屏）
        static string lastHosting;
        public static void SetHosting(bool on, string tag = null)
        {
            if (!Ready) return;
            string s = !on ? "" : string.IsNullOrEmpty(tag) ? "host" : ("host;" + tag);
            if (s == lastHosting) return;
            lastHosting = s;
            Send('P', Encoding.UTF8.GetBytes(s));
        }

        public static string FriendName(ulong id)
        {
            lock (names) { string n; if (names.TryGetValue(id, out n)) return n; }
            if (!Ready) return id.ToString();
            var r = Request('N', BitConverter.GetBytes(id), 'n', 800);
            if (r == null || r.Length < 8) return id.ToString();
            var name = Encoding.UTF8.GetString(r, 8, r.Length - 8);
            if (name.Length == 0) return id.ToString();
            lock (names) names[id] = name;
            return name;
        }

        // 正在玩同一个游戏的好友：{平台账号 ID, 名字, 是否在开房, 房间标签}。开房的排前面
        public static List<string[]> FriendsInGame()
        {
            if (!Ready || !presenceOk) return new List<string[]>();     // 0.34 没开"显示 " + Bridge.Svc + " 好友"就不读好友列表
            var r = Request('F', null, 'f', 1500);
            if (r == null) return friends;
            var list = new List<string[]>();
            foreach (var line in Encoding.UTF8.GetString(r).Split('\n'))
            {
                var f = line.Split('\t');
                ulong id;
                if (f.Length < 3 || !ulong.TryParse(f[0], out id)) continue;
                string tag = f.Length > 3 ? f[3] : "";
                list.Add(new[] { f[0], f[2], f[1] == "1" ? "1" : "0", tag });
                lock (names) names[id] = f[2];
            }
            list.Sort((a, b) => string.CompareOrdinal(b[2], a[2]));
            friends = list;
            return list;
        }

        // --steam-selftest [平台账号 ID]：命令行跑自检，等结论（最多 20 秒）
        public static int SelfTestCli(ulong target, Action<string> log)
        {
            if (!SelfTest(target)) { log("失败：" + Error); return 1; }
            var t0 = DateTime.UtcNow;
            while (SelfTestResult == null && (DateTime.UtcNow - t0).TotalSeconds < 20) Thread.Sleep(100);
            log(SelfTestResult ?? "20 秒没等到结论");
            Shutdown();
            return SelfTestResult == null ? 2 : 0;
        }

        // --steam-probe：连桥、列好友、断开
        public static int Probe(Action<string> log)
        {
            if (!Init()) { log("失败：" + Error); return 1; }
            log(Bridge.Name + " 桥 " + PipeName + "：" + MyName + " " + Bridge.Name + " ID=" + MyId);
            AllowPresence(true);     // 探测是开发者自己跑的，看一眼好友
            var fs = FriendsInGame();
            log("正在玩同一个游戏的好友 " + fs.Count + " 个");
            foreach (var f in fs) log("  " + f[1] + "  " + f[0] + (f[2] == "1" ? "  （在开房）" : ""));
            Shutdown();
            return 0;
        }
    }

    // 点对点 一条连接（经游戏里的桥）。主机：监听虚拟端口，接受第一个连进来的人（后来的拒掉）；客机：ConnectP2P 到主机
    class BridgeLink : ITransport, ISpecSink, IQueueHost, IRelayHost, IPeerTableHost, IMpLink
    {
        readonly RemoteOut outq;
        bool host;
        public volatile bool InMatch;
        string deniedMsg;
        public string DeniedMsg
        {
            get { return !string.IsNullOrEmpty(deniedMsg) ? deniedMsg : (mesh != null && !string.IsNullOrEmpty(mesh.RefusedMsg) ? mesh.RefusedMsg : null); }
            set { deniedMsg = value; }
        }
        readonly PeerTable peers = new PeerTable();
        public PeerTable Peers { get { return peers; } }
        uint listen;
        uint conn { get { return peers.PeerConn; } }
        ulong peerId { get { return peers.PeerBridgeId; } }
        uint conn2 { get { return peers.SecondConn; } }
        string peerName = "";
        long tx, gap;
        Action<string> log = delegate { };
        public Action<string> Log { get { return log; } set { log = value ?? delegate { }; } }
        Wire wire = new Wire(null);
        public Wire Wire { get { return wire; } set { wire = value ?? new Wire(null); } }
        public RemoteOut Out { get { return outq; } }
        // 排位：主机只接受大厅服务器介绍的那个平台账号 ID（null = 谁先连进来接谁，平台好友 / 大厅照旧）
        public Func<ulong, bool> AllowPeer;
        // 每轮收到的最新一个状态包（BridgeRoomLink 从里面取 rk= / mt= / quit=）
        public Action<string> OnPacket;
        // 直连慢时自动试桥接：每收到一个包（含控制包）/ 每收到一个状态包；NoReconnect = 主机通知停用桥接时客机不自己重连
        public Action<string> OnAny, OnState;
        public bool NoReconnect;
        public void SendCtl(byte[] b) { var c = conn; if (c != 0) Bridge.SendTo(c, b, 1); }
        public bool Linked { get { return conn != 0; } }
        public ulong PeerBridgeId { get { return conn != 0 ? peerId : 0; } }     // 桥接平台验过的对方
        // 黑名单里的人连进来：先接住（才能回 4NDENY 让对方面板显示"被房主拒绝"），3 秒后关掉
        readonly Dictionary<uint, DateTime> denied = new Dictionary<uint, DateTime>();

        // 观战（平台好友 / 平台大厅）。主机：已经有对手时新来的连接先接住（最多 MaxSpec 个），
        // SpecWait 秒内收到带 spec=1 的握手才算观战者，否则关掉（旧版客机照旧当"房间已满"被拒）；观战者 6 秒没保活也关。
        // 观战者（SpecMode）：主机转来的两路包（src=h / src=c）按来源分别去重
        public const int MaxSpec = 4;
        public const double SpecWait = 5.0, SpecLost = 6.0;
        readonly Dictionary<uint, DateTime> pend = new Dictionary<uint, DateTime>();
        readonly Dictionary<uint, ulong> pendWho = new Dictionary<uint, ulong>();
        // （移植 0.33 线）：等着说明来意的连接发来的包验签没过（联机密码对不上 / 时钟差太多）→ 拒绝时说清原因，不再一律写"房间已满"
        readonly Dictionary<uint, string> pendBad = new Dictionary<uint, string>();
        public Action<string> OnPeerState { get; set; }
        public Action<string> OnRole { get; set; }
        public Action<string, bool> OnRoleEx { get; set; }
        public Action<PeerRow, string> OnDeliverPeerEvent { get; set; }
        // ---- M3 多人混战全互连（桥接平台）
        MpMesh mesh;
        public MpMesh Mesh { get { return mesh; } }
        public bool MpJoinMode { get { return mpJoin; } }
        int IMpLink.MyPid { get { return mesh != null && mesh.MyPid > 0 ? mesh.MyPid : MyPid; } }
        int IMpLink.MpSeen { get { return mesh != null && mesh.MpSeen > 0 ? mesh.MpSeen : MpSeen; } }
        string IMpLink.MpRoster { get { return mesh != null && !string.IsNullOrEmpty(mesh.MpRoster) ? mesh.MpRoster : MpRoster; } }
        public string LocalAddrForHost(int hostPid)
        {
            return "";     // 桥接方式不在游戏内面板/状态栏暴露 IP 或 ID
        }
        bool mpJoin;
        public int MyPid;
        public int MpSeen;
        public string MpRoster = "";
        long mpRx, mpRefused;
        uint meshListen;
        readonly Dictionary<uint, ulong> mpWho = new Dictionary<uint, ulong>();
        public void EnableMpMesh(bool hostMode)
        {
            if (mesh != null) return;
            if (!hostMode) mpJoin = true;
            mesh = new MpMesh();
            mesh.IsHost = hostMode;
            mesh.MyPid = hostMode ? 1 : 0;
            mesh.Seal = b => wire.Seal(b);
            mesh.SendRaw = delegate(object h, byte[] b)
            {
                if (h is uint) { try { Bridge.SendTo((uint)h, b, 1); } catch (Exception) { } }
            };
            mesh.Deliver = delegate(string tagged) { outq.Push(tagged); };
            mesh.Dedupe = delegate(string src, int seq)
            {
                int g;
                return peers.TakeSrcSeq(src, seq, out g);
            };
            mesh.Log = m => log(m);
            mesh.Enabled = true;
            mesh.BridgePrimary = true;
            mesh.SelfSid = Bridge.MyId;
            mesh.OnRosterUpdated = delegate(int pid, int seen, string roster)
            {
                MyPid = pid;
                MpSeen = seen;
                MpRoster = roster;
            };
            mesh.OnHostChanged = delegate(int newHost, int newEpoch)
            {
                if (mesh.MyPid == newHost)
                {
                    mesh.IsHost = true;
                }
                else
                {
                    mesh.IsHost = false;
                }
            };
            mesh.EnsureHandle = delegate(int pid, string addr, ulong sid)
            {
                if (pid == mesh.HostPid || (peerId != 0 && sid == peerId)) return conn;
                if (sid == 0UL || MyPid <= 0) return null;
                // 编号小的连编号大的：每人只需要一个监听口（VPort + 自己的编号）；房主用原来的 VPort
                if (MyPid < pid)
                {
                    EnsureMeshListen();
                    return Bridge.ConnectP2P(sid, Bridge.VPort + pid);
                }
                EnsureMeshListen();
                return null;     // 等对方连过来
            };
            if (!hostMode)
            {
                ulong hostId = peerId;
                if (hostId != 0) mesh.AddIntro(1, "", hostId, "", conn);
            }
        }
        void EnsureMeshListen()
        {
            if (host) return;
            if (meshListen != 0 || MyPid <= 0) return;
            meshListen = Bridge.Listen(Bridge.VPort + MyPid);
            Bridge.SetHosting(false);      // 只是为了直连另开的监听，不是房间主机
            Bridge.Diag("多人网状：监听虚拟端口 " + (Bridge.VPort + MyPid) + " → " + meshListen);
        }
        public void PromoteToMpHost()
        {
            host = true;
            if (meshListen != 0) { Bridge.Unlisten(); meshListen = 0; }
            listen = Bridge.Listen(Bridge.VPort);
            if (mesh != null)
            {
                mesh.IsHost = true;
                mesh.HostPid = mesh.MyPid;
                mesh.SendIntros(DateTime.UtcNow, true);
                SendMpInfo();
            }
            log(L.T("多人：晋升为主机", "Multi: promoted to host"));
            Bridge.Diag("多人：晋升为主机，监听端口 " + Bridge.VPort);
        }
        public void DemoteToMpClient(int newHost)
        {
            host = false;
            if (mesh != null)
            {
                var hp = mesh.Get(newHost);
                if (hp != null && hp.Handle is uint)
                {
                    var h = (uint)hp.Handle;
                    peers.SetPeer(h, PeerRoute.Direct, hp.BridgeId, null);
                    peerName = Bridge.FriendName(hp.BridgeId);
                }
            }
            Bridge.Diag("多人：降级为客机，新房主 " + newHost);
        }
        public List<KeyValuePair<int, string>> MpPlayers()
        {
            if (mesh != null) return mesh.PruneAndGetPlayers(MpCfg.Lost);
            var gone = new List<PeerRow>();
            var list = peers.MpPlayers(MpCfg.Lost, gone);
            for (int i = 0; i < gone.Count; i++)
            {
                log(L.T("多人：玩家离开 ", "Multi: player left ") + gone[i].Pid + " " + gone[i].Handle);
                if (mesh != null) mesh.Remove(gone[i].Pid);
            }
            return list;
        }
        public int MpCount { get { return MpPlayers().Count; } }
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
            var to = peers.MpSnapshot<uint>();
            foreach (var kv in to)
            {
                var b = wire.Seal(WireTag.P + "MP\npid=" + kv.Value + "\nn=" + (players.Count + 1) + "\nmax=" + MpCfg.Max + "\nl=" + roster + "\n");
                try { Bridge.SendTo(kv.Key, b, 1); } catch (Exception) { }
            }
        }
        public string MpStatsLines()
        {
            return mesh != null ? "\nmpmesh=" + mesh.StatsLine() + "\nmptx=" + mesh.MpTx + "\nmprx=" + mesh.MpRx + "\nmpdual=" + mesh.MpDual
                + "\nmpfwd=" + mesh.MpFwd + "\nmpdup=" + mesh.MpDup + "\nmpdz=" + mesh.DzStat + "\nmpprobe=" + mesh.ProbeStats()
                + "\nmphost=" + mesh.HostPid : "";
        }
        // 房主：进来一条 桥接连接，按多人玩家收下（MpCfg.Max >= 2 时）
        bool NoteMp(uint h, string s, ulong who)
        {
            if (MpCfg.Max < 2) return false;
            bool hello = (s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal) && s.Contains("\nmp=1"))
                         || (MpCfg.Max >= 2 && s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal) && !Link.IsSpecHello(s));
            PeerRow p;
            if (hello && mesh != null && mesh.DuelRefuse(h))
            {
                mpRefused++;
                return true;
            }
            if (!MpCfg.MidJoin && InMatch && !MpCfg.DuelOn && (hello || (s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal) && !Link.IsSpecHello(s)))
                && !((mesh != null && mesh.HasHandle(h)) || peers.NoteMp(h, false, MpCfg.Max, MpCfg.Limit, MpCfg.Lost, out p) == 1))
            {
                mpRefused++;
                var rej = wire.Seal(WireTag.P + "BUSY|inmatch\n");
                try { Bridge.SendTo(h, rej, 1); } catch (Exception) { }
                log(L.T("多人：对局进行中，拒绝新人", "Multi: match in progress, refused newcomer"));
                Bridge.Diag("主机：对局进行中，拒绝连接 " + h);
                return true;
            }
            int existingPid = mesh != null ? mesh.PidByHandle(h) : 0;
            if (hello && mesh != null && mesh.IsHost && !Link.HelloWatch(s)) mesh.TryYieldAiForHuman(MpCfg.Max);     // 观战加入不挤 AI
            var extraUsed = mesh != null ? mesh.Pids() : null;
            if (extraUsed != null && mesh.MyPid > 0) extraUsed.Add(mesh.MyPid);     // 不再写死占住 1 号：换房主后走掉的 1 号可以补给新人（mesh.Pids 里还在的成员照样占着）
            int r = peers.NoteMp(h, hello, MpCfg.Max, MpCfg.Cap, MpCfg.Lost, out p, extraUsed, existingPid);     // 和局域网一样用 Cap：对决模式人数上限放宽到 18（原来写死 6，桥接对决房 6 人后默默排队）
            if (r == 0)
            {
                if (mesh != null && !Link.IsSpecHello(s)) mesh.TellOldClient(h, s);     // 旧版 1v1 握手：回版本号，让它显示"版本不一致，建议更新"
                return false;
            }
            if (r < 0) { mpRefused++; if (hello && mesh != null) mesh.DuelRefuseFull(h); return true; }     // 对决模式满了明说；普通多人房满了不回绝：连接留着，有人走了就补上（和局域网一样， 撤回 的回绝）
            if (mesh != null)
            {
                mesh.AddPlayer(p.Pid, h, "bridge:" + who, who, p.Nick, "", Link.HelloHide(s));     // 开了隐藏的人不把平台账号 ID介绍给别人
                if (r == 2)
                {
                    log(L.T("多人：玩家加入，编号 ", "Multi: player joined, id ") + p.Pid + " " + Bridge.FriendName(who));
                    SendMpInfo();
                    mesh.SendIntros(DateTime.UtcNow, true);
                }
                int g;
                if (mesh.HandlePacket(h, s, out g)) { if (g > 0) mpRx++; return true; }
            }
            if (s.StartsWith(WireTag.P, StringComparison.Ordinal)) return true;
            if (mesh != null) return true;
            var d = Kv.Parse(s);
            string q;
            int seq;
            if (!d.TryGetValue("seq", out q) || !int.TryParse(q, out seq)) return true;
            string nm;
            d.TryGetValue("nm", out nm);
            if (!peers.TakeMpState(p, seq, nm)) return true;
            mpRx++;
            var sb = new StringBuilder(s.Length + 8);
            foreach (var raw in s.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("src=")) continue;
                sb.Append(line).Append('\n');
            }
            sb.Append("src=").Append(p.Pid).Append('\n');
            outq.Push(sb.ToString());
            return true;
        }

        public int QPos { get; private set; }
        public int QLen { get; private set; }
        public uint MeshDirectConn { get { return meshDirectConn; } }
        uint meshDirectConn;
        ulong meshPeerId;
        bool isMatchHost;
        string meshToken = "";
        bool meshDirectConnecting, meshDirectConnected;
        DateTime meshDirectStartedAt = DateTime.MinValue;
        DateTime lastDirectRx = DateTime.MinValue;
        DateTime lastDirectRetry = DateTime.MinValue;
        DateTime lastRelayPingAt = DateTime.MinValue;
        double directPing = -1, viaPing = -1;
        double directLoss, viaLoss;
        int directMaxLoss, viaMaxLoss;
        int directLossStreak;
        // ---- 主机下场：第二位选手（连接句柄 conn2）
        public Action<string> OnSecondState { get; set; }
        public bool NoOut { get; set; }
        public bool HasSecond { get { return peers.HasSecond; } }
        public double SinceFromSecond { get { return peers.SinceFromSecond; } }
        public void SendSecond(byte[] b) { var h = conn2; if (h != 0) { tx++; Bridge.SendTo(h, b, 1); } }
        public object PromoteSecond()
        {
            var r = peers.PromoteSecond(double.MaxValue);
            if (r == null) return null;
            var head = (uint)r.Handle;
            var w = r.BridgeId;
            log(L.T("第二位选手上场：", "Second player: ") + Bridge.FriendName(w));
            Bridge.Diag("主机下场：第二位选手 " + w + " 连接 " + head);
            // M2：4N 介绍直连只在解说模式（HostSpecCfg.On）下发；普通"主机下场"保持 M1 的纯中转，客机不会进网状直连
            if (HostSpecCfg.On && conn != 0 && peerId != 0 && head != 0 && w != 0)
            {
                string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                Bridge.SendTo(conn, Wire.Seal(WireTag.P + "MESH\npeer=" + w + "\nmatchhost=1\ntoken=" + token + "\n"), 8);
                Bridge.SendTo(head, Wire.Seal(WireTag.P + "MESH\npeer=" + peerId + "\nmatchhost=0\ntoken=" + token + "\n"), 8);
                Bridge.Diag("主机：发送 " + WireTag.P + "MESH 介绍 " + peerId + " 与 " + w + " 直连");
            }
            return head;
        }
        public object DemoteSecond()
        {
            var r = peers.DemoteSecond();
            if (r == null) return null;
            return (uint)r.Handle;
        }
        public void SecondToPeer()
        {
            PeerRow prev;
            var r = peers.SecondToPeer(out prev);
            if (r == null) return;
            if (prev != null && prev.Handle is uint && (uint)prev.Handle != 0) Bridge.Close((uint)prev.Handle, false);
            peerName = Bridge.FriendName(r.BridgeId);
        }
        DateTime promotedAt = DateTime.MinValue;
        bool specMode;
        public bool SpecMode { get { return specMode; } set { specMode = value; peers.ClearSrcSeq(); } }
        public long SpecTx, SrcH, SrcC;
        public int SpecCount { get { return peers.SpecCount; } }
        // 观战者会不会拼省流包（握手带 dzv=1）
        public bool SpecDzAll { get { return peers.SpecDzAll; } }
        public void SendSpecs(byte[] b)
        {
            var to = peers.SpecHandles<uint>();
            foreach (var h in to) { SpecTx++; Bridge.SendTo(h, b, 1); }
        }

        // ---- 排队（主机）
        public List<object> QueueOrder() { return peers.QueueOrderObj(); }
        public bool HasPeer { get { return peers.HasPeer; } }
        public bool CanPromote { get { return peers.CanPromote; } }
        public double SinceFromPeer { get { return !peers.HasPeer ? 999 : peers.SinceFromPeer; } }
        public void SendTo(object who, byte[] b) { if (who is uint) Bridge.SendTo((uint)who, b, 1); }
        public object PromoteHead()
        {
            PeerRow prev;
            var r = peers.PromoteHead(double.MaxValue, out prev);
            if (r == null) return null;
            if (prev != null && prev.Handle is uint && (uint)prev.Handle != 0) Bridge.Close((uint)prev.Handle, false);     // 原来的对手（掉线的）连接关掉
            var head = (uint)r.Handle;
            var w = r.BridgeId;
            peerName = Bridge.FriendName(w);
            promotedAt = DateTime.UtcNow;
            log(L.T("队首上场：", "Next in queue plays: ") + peerName);
            Bridge.Diag("主机：排队 队首 " + w + " 连接 " + head + " 上场");
            return head;
        }
        public object DemotePeer()
        {
            var r = peers.DemotePeer();
            if (r == null) return null;
            var old = (uint)r.Handle;
            Bridge.Diag("主机：对手 " + r.BridgeId + " 连接 " + old + " 下场观战");
            log(L.T("对手下场观战：", "Opponent moves to spectators: ") + peerName);
            peerName = "";
            return old;
        }

        // 验签没过的原因（站在主机这边说）
        static string RefuseText(string why, bool en)
        {
            switch (why)
            {
                case "bad": return en ? "connection password differs from yours" : "联机密码和你的不一样";
                case "signed": return en ? "they set a connection password, you did not" : "对方填了联机密码，你没填";
                case "unsigned": return en ? "you set a connection password, they did not (or an old version)" : "你设了联机密码，对方没填（或是旧版）";
                case "time": return en ? "the two PCs' clocks differ by over 2 minutes" : "两台电脑时钟差超过 2 分钟";
                default: return en ? "packet rejected" : "包被拒收";
            }
        }

        void SpecTick()
        {
            var now = DateTime.UtcNow;
            var drop = new List<PeerRow>();
            var refuse = new List<KeyValuePair<uint, ulong>>();
            var refuseWhy = new Dictionary<uint, string>();
            var gone = new List<uint>();
            lock (peers.SyncRoot)
            {
                foreach (var kv in pend)
                    if ((now - kv.Value).TotalSeconds > SpecWait) { ulong w; pendWho.TryGetValue(kv.Key, out w); refuse.Add(new KeyValuePair<uint, ulong>(kv.Key, w)); }
                peers.PruneSpecs(SpecLost, drop);
                foreach (var r in refuse) { string bw; if (pendBad.TryGetValue(r.Key, out bw)) refuseWhy[r.Key] = bw; pend.Remove(r.Key); pendWho.Remove(r.Key); pendBad.Remove(r.Key); }
                foreach (var kv in denied) if ((now - kv.Value).TotalSeconds > 3) gone.Add(kv.Key);
                foreach (var h in gone) denied.Remove(h);
            }
            foreach (var h in gone) Bridge.Close(h, true);
            foreach (var r in refuse)
            {
                // 满了、又没说是来观战的（旧版客机 / 想当对手的）：照旧按"房间已满"拒绝
                // 包验签没过的（联机密码对不上等）按真实原因写；对手位空着却没来包的不算"满"
                Bridge.Close(r.Key, false);
                string why;
                refuseWhy.TryGetValue(r.Key, out why);
                var zh = why != null ? RefuseText(why, false) : conn != 0 ? "房间已满" : "连上后 " + SpecWait + " 秒没收到对方的握手";
                var en = why != null ? RefuseText(why, true) : conn != 0 ? "room full" : "no handshake within " + SpecWait + " s";
                Bridge.Diag("主机：拒绝 " + r.Value + "（" + zh + "）");
                log(L.T("拒绝 ", "Refused ") + Bridge.FriendName(r.Value) + L.T("：" + zh, ": " + en));
            }
            foreach (var r in drop)
            {
                var h = (uint)r.Handle;
                Bridge.Close(h, false);
                Bridge.Diag("主机：关掉观战连接 " + h + "（观战者没保活）");
            }
        }

        public BridgeLink(ulong hostId, string fRemote) : this(hostId, new RemoteOut(fRemote)) { }

        public BridgeLink(ulong hostId, RemoteOut outq)
        {
            this.outq = outq;
            host = hostId == 0;
            lock (Bridge.Data) Bridge.Data.Clear();
            Bridge.OnConnStatus = OnStatus;
            if (host)
            {
                listen = Bridge.Listen(Bridge.VPort);      // 桥同时把富状态 fornet 设成 host
                Bridge.Diag("主机：监听虚拟端口 " + Bridge.VPort + (listen != 0 ? " 成功" : " 失败"));
                if (listen == 0) throw new InvalidOperationException(L.T("桥接监听失败", "Bridge listen failed"));
            }
            else
            {
                peerName = Bridge.FriendName(hostId);
                var c = Bridge.ConnectP2P(hostId, Bridge.VPort);
                peers.SetPeer(c, PeerRoute.Direct, hostId, null);
                Bridge.Diag("客机：连 " + peerName + " (" + hostId + ") 虚拟端口 " + Bridge.VPort + " → 句柄 " + c);
                if (c == 0) throw new InvalidOperationException(L.T("桥接连接失败", "Bridge connect failed"));
            }
        }

        void OnStatus(uint h, int st, ulong who)
        {
            Bridge.Diag((host ? "主机" : "客机") + "：连接 " + h + " 状态 " + st + " 对方 " + who + (h == conn ? "（当前连接）" : ""));
            if (!host && mesh != null && st == Bridge.StConnecting && who != 0 && who != peerId && mesh.PidByBridge(who) > 0)
            {
                int mpid = mesh.PidByBridge(who);
                if (Bridge.Accept(h))
                {
                    mesh.SetDirectHandle(mpid, h);
                    Bridge.Diag("客机：接受多人网状直连 " + who + " 连接 " + h + "（编号 " + mpid + "）");
                }
                return;
            }
            if (!host && who != 0 && who == meshPeerId && st == Bridge.StConnecting)
            {
                if (Bridge.Accept(h))
                {
                    meshDirectConn = h;
                    meshDirectConnected = true;
                    lastDirectRx = DateTime.UtcNow;
                    Bridge.Diag("客机：接受网状直连对手 " + who + " 连接 " + h);
                    Bridge.SendTo(h, Wire.Seal(WireTag.P + "MESHHELLO\ntoken=" + meshToken + "\n"), 8);
                }
                return;
            }
            if (!host && h == meshDirectConn && st == Bridge.StConnected)
            {
                meshDirectConnected = true;
                lastDirectRx = DateTime.UtcNow;
                Bridge.Diag("客机：网状直连连接成功 " + h);
                return;
            }
            if (!host && h == meshDirectConn && (st == Bridge.StClosedByPeer || st == Bridge.StProblem || st == Bridge.StNone || st == -1))
            {
                Bridge.Diag("客机：网状直连断开 " + h + "（状态 " + st + "）");
                meshDirectConnected = false;
                Bridge.Close(h, false);
                meshDirectConn = 0;
                lastDirectRetry = DateTime.UtcNow;
                return;
            }
            // M3/M4：多人房里两个玩家之间的桥接平台直连断了：清掉句柄，后台按编号重连（断了就退回经房主 / 第三人中转）
            if (!host && mesh != null && h != conn && mesh.HasHandle(h)
                && (st == Bridge.StClosedByPeer || st == Bridge.StProblem || st == Bridge.StNone || st == -1))
            {
                int dpid = mesh.PidByHandle(h);
                mesh.DropDirectHandle(h);
                Bridge.Close(h, false);
                Bridge.Diag("客机：多人网状直连断开 " + h + "（编号 " + dpid + "），后台重连");
                return;
            }
            if (host && h != conn && st == Bridge.StConnecting && Blacklist.DeniesBridge(who))
            {
                if (Bridge.Accept(h)) lock (peers.SyncRoot) denied[h] = DateTime.UtcNow;
                else Bridge.Close(h, false);
                Bridge.Diag("主机：拒绝 " + who + "（黑名单）");
                log(L.T("拒绝 ", "Refused ") + Bridge.FriendName(who) + L.T("：在黑名单里", ": blacklisted"));
                return;
            }
            if (host && h != conn && st == Bridge.StConnecting)
            {
                // M3 多人房间：来的是玩家，先接住，等握手包分配编号或判定拒绝
                if (MpCfg.Max >= 2)
                {
                    // 验收：先接住才能回"对局中 / 观众满"的原因，但没握手的连接也得有上限（原来满了直接关）
                    int pending; lock (peers.SyncRoot) pending = mpWho.Count;
                    if (pending >= MpCfg.Max - 1 + 4)
                    {
                        Bridge.Close(h, false);
                        Bridge.Diag("主机：多人连接太多，拒绝 " + who);
                        log(L.T("拒绝 ", "Refused ") + Bridge.FriendName(who) + L.T("：多人房间已满", ": multiplayer room full"));
                        return;
                    }
                    if (Bridge.Accept(h))
                    {
                        mpWho[h] = who;
                        Bridge.Diag("主机：接受多人连接 " + who + " 句柄 " + h + "（等握手包分配编号）");
                    }
                    return;
                }
                int busy;
                lock (peers.SyncRoot) busy = pend.Count + peers.SpecCount;
                // 先接住，看它发来的第一个包：带 spec=1 的握手 = 观战者；别的包 = 想当对手（对手位空着就给它，满了按"房间已满"拒绝）。
                // 以前对手位空着时直接接成对手：观战者连空房间会被当成对手
                if (AllowPeer == null && busy < SpecCfg.Max + 1 && Bridge.Accept(h))
                {
                    lock (peers.SyncRoot) { pend[h] = DateTime.UtcNow; pendWho[h] = who; pendBad.Remove(h); }
                    Bridge.Diag("主机：先接住 " + who + " 连接 " + h + "（看第一个包是观战还是对手）");
                    return;
                }
                if (conn != 0)
                {
                    Bridge.Close(h, false);
                    Bridge.Diag("主机：拒绝 " + who + "（房间已满）");
                    log(L.T("拒绝 ", "Refused ") + Bridge.FriendName(who) + L.T("：房间已满", ": room full"));
                    return;
                }
                var allow = AllowPeer;
                if (allow != null && !allow(who))
                {
                    Bridge.Close(h, false);
                    Bridge.Diag("主机：拒绝 " + who + "（不是大厅服务器介绍的对手）");
                    log(L.T("拒绝 ", "Refused ") + Bridge.FriendName(who) + L.T("：不是从" + GameInfo.RankedZh + "房间加入的", ": did not join through the ranked room"));
                    return;
                }
                bool ok = Bridge.Accept(h);
                Bridge.Diag("主机：接受 " + who + " 连接 " + h + (ok ? " 成功" : " 失败"));
                if (ok)
                {
                    peers.SetPeer(h, PeerRoute.Direct, who, null);
                    peerName = Bridge.FriendName(who);
                    log(L.T("桥接对端：", "Bridge peer: ") + peerName);
                }
                return;
            }
            if (h != conn && (st == Bridge.StClosedByPeer || st == Bridge.StProblem || st == Bridge.StNone || st == -1)
                && (MpCfg.Max >= 2 || (mesh != null && mesh.HasHandle(h))))
            {
                mpWho.Remove(h);
                int mpid;
                bool removed = peers.RemoveMp(h, out mpid);
                if (mesh != null && mpid > 0) mesh.Remove(mpid);
                if (removed)
                {
                    Bridge.Close(h, false);
                    Bridge.Diag("多人：连接 " + h + " 断开，编号 " + mpid + " 收回");
                    log(L.T("多人：玩家离开，编号 ", "Multi: player left, id ") + mpid);
                    return;
                }
            }
            if (h != conn && h == conn2 && (st == Bridge.StClosedByPeer || st == Bridge.StProblem || st == Bridge.StNone || st == -1))
            {
                Bridge.Close(h, false);
                peers.SetSecond(null, PeerRoute.Direct, 0UL);
                log(L.T("第二位选手断开", "Second player left"));
                return;
            }
            if (h != conn && (st == Bridge.StClosedByPeer || st == Bridge.StProblem || st == Bridge.StNone || st == -1))
            {
                bool was;
                lock (peers.SyncRoot) { was = pend.Remove(h) | peers.RemoveSpec(h); pendBad.Remove(h); }
                if (was) { Bridge.Close(h, false); log(L.T("观战者离开", "A spectator left")); return; }
            }
            if (h == conn && (st == Bridge.StClosedByPeer || st == Bridge.StProblem || st == Bridge.StNone || st == -1))
            {
                log(L.T("桥接连接断开（状态 ", "Bridge connection closed (state ") + st + "）");
                Bridge.Close(h, false);
                if (host || NoReconnect || (mesh != null && mesh.HostPid != 1)) { peers.SetPeer(null, PeerRoute.Direct); peerName = ""; }
                else
                {
                    var pid = peerId;
                    if (pid != 0)
                    {
                        var c = Bridge.ConnectP2P(pid, Bridge.VPort);   // 客机：重连（主机可能还没开房，或者中继换路）
                        peers.SetPeer(c, PeerRoute.Direct, pid, null);
                        Bridge.Diag("客机：重连 " + pid + " → 句柄 " + c);
                    }
                }
            }
        }

        public void Send(byte[] b)
        {
            if (meshDirectConnected && meshDirectConn != 0)
            {
                tx++;
                Bridge.SendTo(meshDirectConn, b, 1);
            }
            var c = conn;
            if (c == 0) return;
            tx++;
            // 发送标志 1（不可靠、不攒包）：状态包丢了就丢了，下一包会覆盖
            Bridge.SendTo(c, b, 1);
        }

        public int Pump()
        {
            Bridge.Pump();
            string newest = null;
            int got = 0;
            while (true)
            {
                KeyValuePair<uint, byte[]> m;
                lock (Bridge.Data) { if (Bridge.Data.Count == 0) break; m = Bridge.Data.Dequeue(); }
                if (host && conn2 != 0 && m.Key == conn2)
                {
                    // 第二位选手的包：状态包交给 Session 转发；握手 / 可靠事件不进主机的可靠通道
                    peers.LastFromSecond = DateTime.UtcNow;
                    var s2 = wire.Verify(m.Value, 0, m.Value.Length, b => Bridge.SendTo(m.Key, b, 1));
                    if (s2 == null) continue;
                    var s2Row = peers.SecondRow;
                    if (s2Row != null && (s2.StartsWith(WireTag.P + "EV\n", StringComparison.Ordinal) || s2.StartsWith(WireTag.P + "ACK\n", StringComparison.Ordinal)))
                    {
                        if (s2Row.Rel == null)
                        {
                            var to2 = m.Key;
                            s2Row.Rel = new RelChannel(b => Bridge.SendTo(to2, Wire.Seal(b), 1));
                            if (OnDeliverPeerEvent != null) s2Row.Rel.OnDeliver = ev => OnDeliverPeerEvent(s2Row, ev);
                        }
                        if (s2.StartsWith(WireTag.P + "EV\n", StringComparison.Ordinal)) { s2Row.Rel.OnEvents(s2); continue; }
                        if (s2.StartsWith(WireTag.P + "ACK\n", StringComparison.Ordinal)) { s2Row.Rel.OnAck(s2); continue; }
                    }
                    if (s2.StartsWith(WireTag.P, StringComparison.Ordinal) || s2.Contains("hello=1")) continue;
                    int q2 = SeqOf(s2);
                    int g2;
                    if (q2 < 0 || !peers.TakeSecondSeq(q2, out g2)) continue;
                    var cb2 = OnSecondState;
                    if (cb2 != null) cb2(s2);
                    continue;
                }
                if (!host && meshDirectConn != 0 && m.Key == meshDirectConn)
                {
                    lastDirectRx = DateTime.UtcNow;
                    directLossStreak = 0;
                    directLoss = 0;
                    var ds = wire.Verify(m.Value, 0, m.Value.Length, b => Bridge.SendTo(m.Key, b, 1));
                    if (ds == null) continue;
                    if (ds.StartsWith(WireTag.P + "MESHHELLO\n", StringComparison.Ordinal))
                    {
                        meshDirectConnected = true;
                        continue;
                    }
                    if (ds.StartsWith(WireTag.P + "PING\n", StringComparison.Ordinal))
                    {
                        var d = Kv.Parse(ds);
                        string tval;
                        if (d.TryGetValue("t", out tval))
                            Bridge.SendTo(m.Key, Wire.Seal(WireTag.P + "PONG\nt=" + tval + "\n"), 1);
                        continue;
                    }
                    if (ds.StartsWith(WireTag.P + "PONG\n", StringComparison.Ordinal))
                    {
                        var d = Kv.Parse(ds);
                        string tval;
                        long t0;
                        if (d.TryGetValue("t", out tval) && long.TryParse(tval, out t0))
                        {
                            long rtt = (long)(Link.UnixNow() * 1000) - t0;
                            if (rtt >= 0 && rtt < 10000)
                            {
                                directPing = directPing < 0 ? rtt : (directPing * 0.8 + rtt * 0.2);
                                if (peers.PeerRow != null) peers.PeerRow.DirectPing = directPing;
                            }
                        }
                        continue;
                    }
                    if (wire.Control(ds)) continue;
                    int dseq = SeqOf(ds);
                    if (dseq >= 0)
                    {
                        int dg;
                        if (!peers.TakePeerSeq(dseq, out dg)) continue;
                        gap += dg;
                    }
                    newest = ds;
                    got++;
                    if (!NoOut) outq.Push(ds);
                    var fw2 = OnPeerState;
                    if (fw2 != null) fw2(ds);
                    continue;
                }
                if (host && m.Key != conn)
                {
                    bool deny;
                    lock (peers.SyncRoot) deny = denied.ContainsKey(m.Key);
                    if (deny) { var dk = m.Key; wire.Deny(b => Bridge.SendTo(dk, b, 8)); continue; }     // 可靠发送
                    // 观战者 / 等着说明来意的连接：只认 spec=1 握手（保活），别的都不要
                    bool known;
                    bool inMesh = mesh != null && mesh.HasHandle(m.Key);
                    bool knownMp = inMesh || (MpCfg.Max >= 2 && mpWho.ContainsKey(m.Key));
                    lock (peers.SyncRoot) known = pend.ContainsKey(m.Key) || peers.SpecAlive(m.Key);
                    if (!known && !knownMp) continue;
                    long rb0 = wire.RejBad, rs0 = wire.RejSigned, ru0 = wire.RejUnsigned, rt0 = wire.RejTime;
                    var hs = wire.Verify(m.Value, 0, m.Value.Length, b => Bridge.SendTo(m.Key, b, 1));
                    if (hs == null)
                    {
                        // 记下为什么拒收（对方那边会收到 4NNAK、面板自己显示"联机密码不一样"）；5 秒后拒绝时按这个原因写日志
                        string why = wire.RejBad != rb0 ? "bad" : wire.RejSigned != rs0 ? "signed" : wire.RejUnsigned != ru0 ? "unsigned" : wire.RejTime != rt0 ? "time" : null;
                        if (why != null)
                        {
                            bool firstBad;
                            lock (peers.SyncRoot) { firstBad = pend.ContainsKey(m.Key) && !pendBad.ContainsKey(m.Key); if (pend.ContainsKey(m.Key)) pendBad[m.Key] = why; }
                            if (firstBad) Bridge.Diag("主机：连接 " + m.Key + " 的包验签没过（" + RefuseText(why, false) + "）");
                        }
                        continue;
                    }
                    if (inMesh)
                    {
                        int mg;
                        if (mesh.HandlePacket(m.Key, hs, out mg)) { if (mg > 0) got++; continue; }
                    }
                    if (Link.IsMpHello(hs) || (MpCfg.Max >= 2 && hs.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal)))
                    {
                        ulong mw;
                        mpWho.TryGetValue(m.Key, out mw);
                        if (!mpWho.ContainsKey(m.Key)) { lock (peers.SyncRoot) pendWho.TryGetValue(m.Key, out mw); }
                        if (NoteMp(m.Key, hs, mw)) continue;
                    }
                    var spRow = peers.FindByHandle(m.Key);
                    if (spRow != null && (hs.StartsWith(WireTag.P + "EV\n", StringComparison.Ordinal) || hs.StartsWith(WireTag.P + "ACK\n", StringComparison.Ordinal)))
                    {
                        if (spRow.Rel == null)
                        {
                            var toSp = m.Key;
                            spRow.Rel = new RelChannel(b => Bridge.SendTo(toSp, Wire.Seal(b), 1));
                            if (OnDeliverPeerEvent != null) spRow.Rel.OnDeliver = ev => OnDeliverPeerEvent(spRow, ev);
                        }
                        if (hs.StartsWith(WireTag.P + "EV\n", StringComparison.Ordinal)) { spRow.Rel.OnEvents(hs); continue; }
                        if (hs.StartsWith(WireTag.P + "ACK\n", StringComparison.Ordinal)) { spRow.Rel.OnAck(hs); continue; }
                    }
                    if (Link.IsSpecHello(hs))
                    {
                        bool first = false;
                        lock (peers.SyncRoot)
                        {
                            if (!peers.SpecAlive(m.Key) && peers.SpecCount >= SpecCfg.Max) continue;      // 房间设置的上限（等着的连接 5 秒后按满了拒）
                            ulong w = 0;
                            first = pend.Remove(m.Key);
                            pendBad.Remove(m.Key);
                            if (first) pendWho.TryGetValue(m.Key, out w);
                            pendWho.Remove(m.Key);
                            peers.NoteSpec(m.Key, hs, SpecCfg.Max, w);
                        }
                        if (first) { log(L.T("观战者加入（桥接）", "Spectator joined (bridge)")); Bridge.Diag("主机：连接 " + m.Key + " 是观战者"); }
                        continue;
                    }
                    // 不是观战握手：等着说明来意的连接、对手位又空着 → 当对手（这个包接着往下当对手的包处理）
                    // 但可靠事件 / 确认 / 延迟探测这类控制包不是"想当对手"的信号（观战者的可靠通道可能抢在握手前发包）：留在等待里，继续等握手
                    bool pendCtl;
                    lock (peers.SyncRoot) pendCtl = pend.ContainsKey(m.Key);
                    if (pendCtl &&
                        (hs.StartsWith(WireTag.P + "EV\n", StringComparison.Ordinal) || hs.StartsWith(WireTag.P + "ACK\n", StringComparison.Ordinal)
                         || hs.StartsWith(WireTag.P + "PING\n", StringComparison.Ordinal) || hs.StartsWith(WireTag.P + "PONG\n", StringComparison.Ordinal)))
                        continue;
                    bool asPeer = false;
                    ulong pw = 0;
                    lock (peers.SyncRoot)
                        if (conn == 0 && pend.ContainsKey(m.Key))
                        {
                            pend.Remove(m.Key);
                            pendBad.Remove(m.Key);
                            pendWho.TryGetValue(m.Key, out pw);
                            pendWho.Remove(m.Key);
                            asPeer = true;
                        }
                    if (!asPeer)
                    {
                        if (HostSpecCfg.On && conn != 0)
                        {
                            lock (peers.SyncRoot)
                            {
                                if (pend.Remove(m.Key))
                                {
                                    pendBad.Remove(m.Key);
                                    pendWho.TryGetValue(m.Key, out pw);
                                    pendWho.Remove(m.Key);
                                    peers.NoteSpec(m.Key, WireTag.P + "HELLO\nspec=1\n", SpecCfg.Max, pw);
                                    Bridge.Diag("主机：把连接 " + m.Key + " (" + pw + ") 纳进观战排队（解说模式）");
                                    continue;
                                }
                            }
                        }
                        continue;
                    }
                    peers.SetPeer(m.Key, PeerRoute.Direct, pw, null);
                    peerName = Bridge.FriendName(pw);
                    log(L.T("桥接对端：", "Bridge peer: ") + peerName);
                    Bridge.Diag("主机：接受 " + pw + " 连接 " + m.Key + " 当对手");
                }
                if (!host && m.Key != conn && mesh != null && mesh.HasHandle(m.Key))
                {
                    var ms = wire.Verify(m.Value, 0, m.Value.Length, b => Bridge.SendTo(m.Key, b, 1));
                    if (ms == null) continue;
                    int mg;
                    if (mesh.HandlePacket(m.Key, ms, out mg)) { if (mg > 0) got++; continue; }
                    continue;
                }
                if (m.Key != conn || conn == 0) continue;      // 已经关掉的连接剩下的包
                var data = m.Value;
                var s = wire.Verify(data, 0, data.Length, Send);
                if (s == null) continue;
                if (!host && s.StartsWith(WireTag.P + "BUSY|", StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(deniedMsg))
                    {
                        deniedMsg = Link.ParseDeniedMsg(s);
                        log(deniedMsg);
                        Bridge.Diag("被房主拒绝：" + deniedMsg);
                    }
                    continue;
                }
                if (!host && mesh != null)
                {
                    int mg;
                    if (mesh.HandlePacket(m.Key, s, out mg)) { if (mg > 0) got++; continue; }
                }
                if (host) peers.LastFromPeer = DateTime.UtcNow;
                var oa = OnAny;
                if (oa != null) oa(s);
                if (host && Link.IsSpecHello(s))      // 对手自己下去观战（刚叫上场、还没收到 4NROLE 的不算）
                {
                    if ((DateTime.UtcNow - promotedAt).TotalSeconds > Link.PromoteGrace) DemotePeer();
                    continue;
                }
                if (!host && s.StartsWith(WireTag.P + "MESH\n", StringComparison.Ordinal))
                {
                    var q = Kv.Parse(s);
                    string pVal, mhVal, tokVal;
                    ulong pid;
                    if (q.TryGetValue("peer", out pVal) && ulong.TryParse(pVal, out pid))
                    {
                        meshPeerId = pid;
                        isMatchHost = q.TryGetValue("matchhost", out mhVal) && mhVal == "1";
                        meshToken = q.TryGetValue("token", out tokVal) ? tokVal : "";
                        meshDirectConnecting = true;
                        meshDirectStartedAt = DateTime.UtcNow;
                        Bridge.Diag("客机：收到 " + WireTag.P + "MESH，对方 " + pid + "，我是对局主机=" + isMatchHost);
                        if (isMatchHost)
                        {
                            if (listen == 0) listen = Bridge.Listen(Bridge.VPort);
                        }
                        else
                        {
                            meshDirectConn = Bridge.ConnectP2P(pid, Bridge.VPort);
                        }
                    }
                    continue;
                }
                if (!host && s.StartsWith(WireTag.P + "PONG\n", StringComparison.Ordinal))
                {
                    var d = Kv.Parse(s);
                    string tval;
                    long t0;
                    if (d.TryGetValue("t", out tval) && long.TryParse(tval, out t0))
                    {
                        long rtt = (long)(Link.UnixNow() * 1000) - t0;
                        if (rtt >= 0 && rtt < 10000)
                        {
                            viaPing = viaPing < 0 ? rtt : (viaPing * 0.8 + rtt * 0.2);
                            if (peers.PeerRow != null) peers.PeerRow.ViaPing = viaPing;
                        }
                    }
                    continue;
                }
                if (!host && s.StartsWith(WireTag.P + "PING\n", StringComparison.Ordinal))
                {
                    var d = Kv.Parse(s);
                    string tval;
                    if (d.TryGetValue("t", out tval))
                        Bridge.SendTo(m.Key, Wire.Seal(WireTag.P + "PONG\nt=" + tval + "\n"), 1);
                    continue;
                }
                if (!host && s.StartsWith(WireTag.P + "ROLE", StringComparison.Ordinal))      // 主机让换角色
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
                if (!host && s.StartsWith(WireTag.P + "MP", StringComparison.Ordinal))
                {
                    var q = Kv.Parse(s);
                    string a, b2;
                    int pid, n;
                    if (q.TryGetValue("pid", out a) && int.TryParse(a, out pid) && pid != MyPid)
                    {
                        MyPid = pid;
                        if (mesh != null) mesh.MyPid = pid;
                        Bridge.Diag("多人：主机给的编号 " + pid);
                    }
                    if (q.TryGetValue("n", out b2) && int.TryParse(b2, out n)) MpSeen = n;
                    string l;
                    MpRoster = q.TryGetValue("l", out l) ? l : "";
                    if (mesh != null) mesh.PruneRoster(MpRoster);
                    continue;
                }
                if (!host && s.StartsWith(WireTag.P + "Q", StringComparison.Ordinal))         // 排第几
                {
                    var q = Kv.Parse(s);
                    string a, b2;
                    int pos, len;
                    if (q.TryGetValue("pos", out a) && int.TryParse(a, out pos)) QPos = pos;
                    if (q.TryGetValue("n", out b2) && int.TryParse(b2, out len)) QLen = len;
                    continue;
                }
                if (wire.Control(s)) continue;     // 拒收 / 可靠事件 / 确认 / 握手（Wire.Control 都返回 true）
                int seq = SeqOf(s);
                if (seq < 0) continue;
                string src;
                int g;
                if (specMode && Kv.Parse(s).TryGetValue("src", out src))
                {
                    if (!peers.TakeSrcSeq(src, seq, out g)) continue;
                    gap += g;
                    if (src == "h") SrcH++; else SrcC++;
                }
                else
                {
                    if (!peers.TakePeerSeq(seq, out g)) continue;
                    gap += g;
                    viaLoss = 0;
                    if (viaLoss > viaMaxLoss) viaMaxLoss = (int)viaLoss;
                }
                got++;
                newest = s;
                if (!specMode) wire.DzSeenText(s);      // 认定是对手的状态包了：算对方会不会拼省流包
                if (!NoOut && (!meshDirectConnected || host)) outq.Push(s);
                var fw = OnPeerState;
                if (fw != null && host) fw(s);
                var os = OnState;
                if (os != null) os(s);
            }
            var now = DateTime.UtcNow;
            if (!host && meshPeerId != 0)
            {
                if (meshDirectConnecting && !meshDirectConnected && (now - meshDirectStartedAt).TotalSeconds > 5.0)
                {
                    meshDirectConnecting = false;
                    Bridge.Diag("客机：网状直连 5 秒未通，退回中转");
                    if (peers.PeerRow != null) peers.PeerRow.Route = PeerRoute.Via;
                }
                if (meshDirectConnected && (now - lastDirectRx).TotalSeconds > 3.0)
                {
                    meshDirectConnected = false;
                    Bridge.Diag("客机：网状直连 3 秒无包，退回中转，准备重连");
                    if (peers.PeerRow != null) peers.PeerRow.Route = PeerRoute.Via;
                    lastDirectRetry = now;
                    directLossStreak++;
                    directLoss = directLossStreak;
                    if (directLossStreak > directMaxLoss) directMaxLoss = directLossStreak;
                    if (peers.PeerRow != null) peers.PeerRow.DirectMaxLoss = directMaxLoss;
                }
                if (!meshDirectConnected && (now - lastDirectRetry).TotalSeconds > 5.0)
                {
                    lastDirectRetry = now;
                    if (!isMatchHost)
                    {
                        meshDirectConn = Bridge.ConnectP2P(meshPeerId, Bridge.VPort);
                        Bridge.Diag("客机：后台重试直连 " + meshPeerId + " -> 句柄 " + meshDirectConn);
                    }
                }
                if ((now - lastRelayPingAt).TotalSeconds >= 1.0)
                {
                    lastRelayPingAt = now;
                    long u = (long)(Link.UnixNow() * 1000);
                    var pingPkt = Wire.Seal(WireTag.P + "PING\nt=" + u + "\n");
                    if (conn != 0) Bridge.SendTo(conn, pingPkt, 1);
                    if (meshDirectConnected && meshDirectConn != 0) Bridge.SendTo(meshDirectConn, pingPkt, 1);
                }
                if (peers.PeerRow != null)
                {
                    peers.PeerRow.DirectHandle = meshDirectConn != 0 ? (object)meshDirectConn : null;
                    peers.PeerRow.DirectPing = directPing;
                    peers.PeerRow.DirectLoss = directLoss;
                    peers.PeerRow.DirectMaxLoss = directMaxLoss;
                    peers.PeerRow.ViaPing = viaPing;
                    peers.PeerRow.ViaLoss = viaLoss;
                    peers.PeerRow.ViaMaxLoss = viaMaxLoss;
                    peers.PeerRow.Route = meshDirectConnected ? PeerRoute.Direct : PeerRoute.Via;
                }
            }
            outq.Flush();   // 每轮写一个（RemoteOut）
            if (host) SpecTick();
            if (newest != null && !specMode)
            {
                PeerNick = Kv.NickOf(newest) ?? PeerNick;
                var cb = OnPacket;
                if (cb != null) cb(newest);
            }
            return got;
        }

        public string PeerNick { get { return peers.PeerNick; } private set { peers.PeerNick = value; } }

        static int SeqOf(string s)
        {
            string v;
            int n;
            return Kv.Parse(s).TryGetValue("seq", out v) && int.TryParse(v, out n) ? n : -1;
        }

        public string PeerStr { get { return peerId == 0 ? "" : "bridge:" + peerId; } }
        public string PeerName { get { return peerName; } }
        public int LocalPort { get { return 0; } }
        public long Tx { get { return tx; } }
        public long Gap { get { return gap; } }
        public double PingMs { get { var c = conn; int p; lock (Bridge.Ping) return c != 0 && Bridge.Ping.TryGetValue(c, out p) ? p : -1; } }

        public void Close()
        {
            if (mesh != null && mesh.MyPid > 0)     // 成员也告别（只告诉房主）
            {
                mesh.BroadcastBye();
                try { Thread.Sleep(30); } catch (Exception) { }
            }
            Bridge.OnConnStatus = delegate { };
            var c = conn;
            if (c != 0) Bridge.Close(c, true);
            lock (peers.SyncRoot)
            {
                foreach (var h in pend.Keys) Bridge.Close(h, false);
                foreach (var h in peers.SpecHandles<uint>()) Bridge.Close(h, false);
                pend.Clear();
                peers.ClearSpecs();
            }
            if (listen != 0 || meshListen != 0) Bridge.Unlisten();     // 桥同时清富状态 fornet
            Bridge.SetHosting(false);
            peers.SetPeer(null, PeerRoute.Direct);
            listen = 0;
            meshListen = 0;
            lock (peers.SyncRoot) mpWho.Clear();
            if (mesh != null) mesh.Clear();
        }
    }
}
