// 服务器大厅（mode=server）：自建服务端 server/fornet-lobby-server.mjs 登记 / 列出房间，
// 加入时把双方的公网地址互相告诉对方，两边往对方的地址互发包打洞（UDP hole punching）；
// 直连试 3 次（每次 2.5 秒）都没收到对方的包：0.34 起对局不再改走服务器中转（AllowRelay 一律 false，提示玩家开"对局走 " + Bridge.Svc或转发端口）；中转那套代码留着给自建服务器 / 观战。
// 协议见服务端文件头。REG / JOIN / 中转都从游戏用的那个 UDP 端口发，服务器看到的才是 NAT 给这个端口的公网地址。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ForNet
{
    static class LobbyServer
    {
        public const int DefaultPort = 27020;

        // 设置里填的 "域名或IP[:端口]" → 端点；没填或认不出返回 null。
        // IPv4 / IPv6 都认（a.b.c.d、[v6]:端口、裸 IPv6、域名的 A 和 AAAA、localhost）。域名两种都有时返回先用的那个，
        // 另一个记在 AltOf 里（ServerPick 没回应时换过去）：本机有 IPv4 出口先 IPv4（和以前一样），没有才先 IPv6；
        // 上次哪一族先回应过（Worked）下次就先用它。IPv6 关了（netcfg ipv6=0）只要 IPv4
        public static IPEndPoint Resolve(string addr)
        {
            var all = ResolveAll(addr);
            return all.Count > 0 ? all[0] : null;
        }

        // 旧规则：只要 IPv4（扩展包 API 2 用：插件那边的地址解析不认 [v6]:端口）
        public static IPEndPoint Resolve4(string addr) { return Addr.Parse(addr, DefaultPort); }

        static readonly object altLock = new object();
        static readonly Dictionary<IPAddress, IPEndPoint> altOf = new Dictionary<IPAddress, IPEndPoint>();
        static readonly HashSet<IPAddress> worked = new HashSet<IPAddress>();

        public static List<IPEndPoint> ResolveAll(string addr)
        {
            var list = Addr.ParseServer(addr, DefaultPort);
            if (list.Count == 2)
            {
                lock (altLock)
                {
                    bool v4First = NetBind.Loopback ? !NetBind.Loopback6 : Addr.PrimaryLocal() != null;
                    if (worked.Contains(list[0].Address) != worked.Contains(list[1].Address)) v4First = worked.Contains(list[0].Address);
                    if (!v4First) list.Reverse();
                    altOf[list[0].Address] = list[1];
                    altOf[list[1].Address] = list[0];
                }
            }
            return list;
        }

        // 同一个服务器的另一族地址（Resolve 时域名两种都有才有）；没有返回 null
        public static IPEndPoint AltOf(IPEndPoint ep)
        {
            if (ep == null) return null;
            lock (altLock) { IPEndPoint a; return altOf.TryGetValue(ep.Address, out a) && a.Port == ep.Port ? a : null; }
        }

        // 这个地址回应过：下次 Resolve 先用这一族
        public static void Worked(IPEndPoint ep)
        {
            if (ep == null) return;
            lock (altLock)
            {
                if (!worked.Add(ep.Address)) return;
                IPEndPoint a;
                if (altOf.TryGetValue(ep.Address, out a)) worked.Remove(a.Address);
            }
        }

        // 告诉服务器自己的局域网地址：两边在同一个路由器后面时，公网地址互发不一定通（路由器不支持回环），局域网地址能通
        public static string LocalAddr(int port)
        {
            var ip = Addr.PrimaryLocal();
            return ip == null ? "" : ip + ":" + port;
        }

        // 服务器转来的地址文本："a.b.c.d:端口" / "[v6]:端口"
        public static IPEndPoint ParseEp(string s) { return Addr.ParseEp(s); }

        // 提示里显示的服务器：设置里填的是域名就显示域名（括号里带解析出的 IP），填的是 IP 就只显示 IP
        public static string Show(string text, IPEndPoint ep)
        {
            text = (text ?? "").Trim();
            if (ep == null) return text;
            int c = text.LastIndexOf(':');
            IPAddress ip;
            if (text.Length == 0 || Addr.ParseEp(text) != null || IPAddress.TryParse(c > 0 ? text.Substring(0, c) : text, out ip) || IPAddress.TryParse(text.Trim('[', ']'), out ip)) return ep.ToString();
            return text + "（" + ep + "）";
        }

        public static string Clean(string s) { return (s ?? "").Replace("|", " ").Replace("\t", " ").Replace("\r", " ").Replace("\n", " "); }
    }

    // 一个大厅服务器的两个地址（域名同时有 A 和 AAAA）：先用 Cur；一直没回应就每 2.5 秒换另一个试；哪个先回应就定下来
    // （LobbyServer.Worked 记住，下次先用这一族）。只有一个地址 / socket 发不了另一族时 Other = null，和以前一样。
    // 收包认服务器：两个地址都认；服务器是 IPv6 时同一网段（前 64 位）同端口的也认（服务器机器有好几个 IPv6 时回包源地址可能换）
    class ServerPick
    {
        public IPEndPoint Cur, Other;
        public bool Answered;
        DateTime nextSwap;
        public int Swaps;

        public ServerPick(IPEndPoint primary, UdpClient sock)
        {
            Cur = primary;
            var alt = LobbyServer.AltOf(primary);
            Other = alt != null && NetBind.CanSend(sock, alt) ? alt : null;
            nextSwap = DateTime.UtcNow.AddSeconds(2.5);
        }

        static bool Match(IPEndPoint from, IPEndPoint s)
        {
            if (from == null || s == null) return false;
            if (from.Equals(s)) return true;
            return from.Port == s.Port && Addr.SameV6Prefix(from.Address, s.Address);
        }

        public bool Is(IPEndPoint from) { return Match(from, Cur) || Match(from, Other); }
        public bool IsCur(IPEndPoint from) { return Match(from, Cur); }

        // 只问一句的请求（房间列表 / 数值表）：还没定时两个地址都发，定了只发 Cur
        public void SendQuery(UdpClient sock, byte[] b)
        {
            try { sock.Send(b, b.Length, Cur); } catch (Exception) { }
            if (!Answered && Other != null) try { sock.Send(b, b.Length, Other); } catch (Exception) { }
        }

        // 收到服务器的包（from 已经 Norm 过）。还没定、回应的是另一个 → 换过来；返回 true = 刚换了
        public bool Got(IPEndPoint from)
        {
            if (Answered) return false;
            Answered = true;
            bool swap = !Match(from, Cur) && Match(from, Other);
            if (swap) { var t = Cur; Cur = Other; Other = t; Swaps++; }
            LobbyServer.Worked(Cur);
            return swap;
        }

        // 还没回应过：到时间换另一个地址；返回 true = 刚换了（调用方马上重发）
        public bool Tick(DateTime now)
        {
            if (Answered || Other == null || now < nextSwap) return false;
            var t = Cur; Cur = Other; Other = t; Swaps++;
            nextSwap = now.AddSeconds(2.5);
            return true;
        }

        public static string Fam(IPEndPoint ep) { return ep != null && ep.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4"; }
    }

    // 收状态包的公共部分（同 Link.Pump）：只要比上一个新的序号、数缺口、按发送时间估延迟、取昵称
    class RxState
    {
        readonly PeerTable peers;
        public long Rx, Gap;
        public double DelaySum;
        public long DelayN;
        public string Newest;
        public string PeerNick { get { return peers.PeerNick; } set { peers.PeerNick = value; } }

        public RxState(PeerTable peers) { this.peers = peers; }

        public void Reset() { peers.ResetPeerSeq(); }

        public bool Take(string s)
        {
            if (s.StartsWith(WireTag.P + "HELLO") || s.StartsWith(WireTag.P + "PUNCH") || s.Contains("hello=1")) return false;
            var d = Kv.Parse(s);
            string v;
            int seq, g;
            if (!d.TryGetValue("seq", out v) || !int.TryParse(v, out seq)) return false;
            if (!peers.TakePeerSeq(seq, out g)) return false;
            Gap += g;
            Rx++;
            double t;
            if (d.TryGetValue("st", out v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t))
            {
                DelaySum += Link.UnixNow() - t;
                DelayN++;
            }
            Newest = s;
            return true;
        }
    }

    // 服务器大厅的一条连接。host：登记房间、等服务器介绍客机；client：JOIN 房间号 → 打洞 → 不通就中转
    // 经大厅服务器开房 / 加入的连接共有的那一侧：服务器大厅（ServerLink，UDP 打洞 / 中转）和排位（BridgeRoomLink，对局数据走桥接平台）。
    // 主程序（房间号、线路、报错、对方昵称前的称号）和排位上报（RankClient.Track：会话号、对方状态包里的 rk= / mt= / quit=）只认这个
    interface IRoomLink
    {
        string RoomId { get; }
        string Sid { get; }                 // 服务器给的会话号（排位上报用）
        string Route { get; }               // 面板的线路（RouteName 翻成文字）
        string Error { get; }
        string RankPid { get; set; }        // 排位房间：自己的 pid（登记 / 加入带上）；null = 普通服务器大厅
        string PeerRk { get; }              // 对方状态包里的 rk= / mt= / quit=（排位房间才取）
        string PeerMt { get; }
        string PeerQuit { get; }
        Action<Dictionary<string, string>> PeerPacket { get; set; }     // 每个新的对方状态包（排位统计看 res=）
        string ServerName { get; set; }     // 设置里填的服务器地址（提示里显示用，LobbyServer.Show）
        Func<string> MapOf { get; set; }
    }

    class ServerLink : ITransport, IRoomLink, ISpecSink, IQueueHost, IRelayHost, IPeerTableHost, IMpLink
    {
        public const int Tries = 3;              // 直连重试 3 次连不上改走服务器中转
        public const double TryWindow = 2.5;     // 每次直连等多少秒
        const double LostAfter = 6.0;            // 连上后多久没收到对方的包算断了（客机重新走一遍加入，主机重新等人）
        // 客机直连 3 次不通、中转也一个包没收到：多半有一方在校园网 / 公司网（严格 NAT、对海外服务器的高频 UDP 限流），只能主动连出去
        public static string BlockedHint { get { return L.T("连不上：如果有一方在校园网 / 公司网，请让对方建房、你来加入",
                                                            "Can't connect: if either of you is on a campus / office network, let the other player host and you join"); } }

        readonly UdpClient udp;
        IPEndPoint server;                 // 现在用的服务器地址（= pick.Cur；IPv4 / IPv6 没回应时会换）
        ServerPick pick;
        readonly string name;
        string key;
        readonly RemoteOut outq;
        bool host;
        public readonly bool SimNoDirect;        // 测试用（--sim-no-direct）：丢掉所有不是服务器发来的包 = 模拟打洞不通
        public Func<string> MapOf { get; set; }
        public string ServerName { get; set; }
        readonly PeerTable peers = new PeerTable();
        public PeerTable Peers { get { return peers; } }
        readonly RxState rx;
        long tx;
        Action<string> log = delegate { };
        Wire wire = new Wire(null);
        public Wire Wire { get { return wire; } set { wire = value ?? new Wire(null); } }
        public RemoteOut Out { get { return outq; } }

        string roomId = "", sid, error = "";
        readonly List<IPEndPoint> cands = new List<IPEndPoint>();
        IPEndPoint peer { get { return peers.GetPeer<IPEndPoint>(); } set { peers.SetPeer(value, PeerRoute.Direct); } }
        bool relay, relayOk, joined;
        bool blocked;      // 中转一直没收到对方的包：BlockedHint 留着，直到直连或中转真的通了
        bool noRelay;      // 服务器没开 --relay（回了 ERR …|norelay / hostnorelay）或自己关了中转：不再进中转，提示留着，一轮轮接着试直连
        // 自己允不允许走服务器中转（设置里的"允许中转"，默认关）。客机关着：直连不通也不请求中转；
        // 主机关着：登记房间时告诉服务器（REG 第 7 段 0），服务器拒绝给这个房间中转；旧服务器不认这一段，主机就不理中转来的包
        public bool AllowRelay;
        // 房间设置"对局走桥接"：旧版一直桥接仍用 BridgeRoomLink；新版一直桥接 / 直连失败桥接均保留这里的 IMpLink。
        // 主机：FallbackMyId = 自己的桥接 ID（同意过桥接、桥在才给）→ 登记带上（REG 第 11 段）、第 16 段 = 2；一直开着桥接监听，直连和桥接谁先通用谁
        // 客机：房间列表第 11 列 = 2 → FallbackRoom；同意过桥接才给 FallbackMyId（JOIN 第 6 段）。直连 Tries 次都不通 → 改经桥接连主机
        public ulong FallbackMyId;
        public bool FallbackRoom;
        public bool MpAlwaysBridge;             // 新版房：大厅介绍后立即 JoinBridge，禁止对局 UDP 直连
        BridgeLink fb;
        bool bridged, fbFailed;
        ulong fbPeer;                        // 客机：主机的桥接 ID；主机：服务器介绍的客机桥接 ID
        // 多人房直连失败走桥接（入房那条桥接由 MpMesh 管：房主接住大厅介绍过的加入者，客机连房主的 BuPort(房主编号)）
        public int FallbackHostPid = 1;      // 客机：房主编号（房间列表标签 hb=，换过房主才有；没有 = 1）
        uint mpFbH;                          // 客机：连房主的那条桥接（0 = 没走桥接）
        DateTime mpFbAt, mpFbHelloAt;
        DateTime mpFbTryAt = DateTime.MinValue;     // 一直桥接：上次尝试经桥接连房主（当场失败后限频重试）
        const double MpFbRetrySec = 3.0;
        bool mpFbLostLogged;
        readonly Dictionary<string, int> mpSidPid = new Dictionary<string, int>();     // 房主：大厅会话号 → 编号（同一人改经桥接时沿用）
        bool MpFbHost { get { return isMpHost && mesh != null && FallbackMyId != 0 && Bridge.Ready && Bridge.MyId != 0; } }
        // 模式 1 直连慢时自动试桥接（netcfg bridgeup / bridgeupms；测试用 FORNET_BU_*_ADD_MS 给实测 RTT 加固定偏移）
        public bool BridgeUpOn = true;
        public int BridgeUpMs = 150;
        static readonly int BuDirectAddMs = BuReadEnvInt(WireTag.EnvPrefix + "BU_DIRECT_ADD_MS");
        static readonly int BuBridgeAddMs = BuReadEnvInt(WireTag.EnvPrefix + "BU_BRIDGE_ADD_MS");
        static int BuReadEnvInt(string k) { int v; return int.TryParse(Environment.GetEnvironmentVariable(k) ?? "", out v) ? v : 0; }
        bool buAsked, buUp, buGaveUp, buPeerCan, buPeerSeen;
        int buGot;
        DateTime buStartAt = DateTime.MinValue, buSwitchedAt = DateTime.MinValue, buCheckAt = DateTime.MinValue;
        DateTime buLastDirPing = DateTime.MinValue, buLastFbPing = DateTime.MinValue, buLastGo = DateTime.MinValue, buFbLastRx = DateTime.MinValue;
        readonly List<double> buDirSamples = new List<double>(), buFbSamples = new List<double>();
        double buDirPing = -1, buFbPing = -1;
        bool BuRoomOk { get { return RankPid == null && !isMpHost && !isMpJoin && !serverRoom && (host ? FallbackMyId != 0 : FallbackRoom); } }
        bool BuCan { get { return BridgeUpOn && BuRoomOk && FallbackMyId != 0 && Bridge.Ready && Bridge.MyId != 0 && fbPeer != 0; } }
        static long BuNowMs() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }
        // 排位：排位房间（主机 REG 第 8、9 段 / 客机 JOIN 第 4 段带上自己的 pid）。null = 普通服务器大厅
        public string RankPid { get; set; }
        // 对方状态包里的 rk=（对方 pid）、mt=（主机的对局状态，客机看这个）、quit=（客机弃权的局号，主机看这个）
        public string PeerRk { get; private set; }
        public string PeerMt { get; private set; }
        public string PeerQuit { get; private set; }
        public Action<Dictionary<string, string>> PeerPacket { get; set; }
        // 观战：主机把两路包经服务器发给观战者（4NW|房间号）。Watchers = 服务器在 REGOK 里报的观战人数；
        // OnPeerState = 收到对手的一个状态包（Session 加 src=c 转发）
        public volatile int Watchers;
        public Action<string> OnPeerState { get; set; }
        public long WatchTx;
        public int SpecCount { get { return Watchers; } }
        public void SendSpecs(byte[] b) { SendWatch(b); }
        // 服务器在 REGOK 第 7 段报"这个房间的观战者都会拼省流包"（WATCH 第 6 段 = dz）；旧服务器没有这段 = 不会，发完整包
        public volatile bool WatchDz;
        public bool SpecDzAll { get { return WatchDz; } }
        public bool SpecMode { get { return false; } set { } }      // 服务器大厅的观战者走 WatchLink，不走这里
        public Action<string> OnRole { get; set; }
        public int QPos { get; private set; }
        public int QLen { get { return 0; } }
        public string JoinToken;          // 客机：服务器叫我上场时给的口令（JOIN 第 7 段）
        public volatile bool AiRoom;      // AI 对战时等人的房间：REG 第 14 段 = 1（列表第 9 列，别人能观战 AI 对打）

        // ---- M5：服务器自己开的房。服务器当控制面房主（名单 / 编号 / 规则 / 开始结束计分 / 聊天 / 观战），
        // 玩家之间照旧 MeshMp 直连互发状态，直连 5 秒不通可请求服务器中转（SRLY / SRELAYIN）。
        bool serverRoom;
        bool serverRoomMp;
        int serverPid, serverPlayers;
        string serverToken = "";        // 服务器房会话口令（SHOST 第 7 段；之后所有上报 / 聊天 / 中转都要带）
        string serverRoster = "", serverScores = "", serverMpm = "";
        int serverMatchHost = -1;
        MpMesh mesh;
        public MpMesh Mesh { get { return mesh; } }
        public bool ServerRoom { get { return serverRoom; } }
        public bool ServerRoomMp { get { return serverRoomMp; } }
        public Action<string, bool> OnRoleEx { get; set; }      // 服务器发来的角色 + 是不是对局主机
        public Action<string> OnServerEvent { get; set; }        // 聊天 / 捡道具结果 → Session 的 Rel.DeliverLocal
        readonly HashSet<int> srvRelayAsk = new HashSet<int>();
        DateTime lastSrvJoin = DateTime.MinValue, lastSrvState = DateTime.MinValue, lastSrvReport = DateTime.MinValue;
        string lastPickSent = "";

        // ---- 玩家开的多人房（Mode == "server"）
        public int MpMax;
        public string MpTag = "";
        public volatile string DeniedMsg;
        public bool IsMpInMatch { get { return Session.IsInMatchTag(MpTag); } }
        bool isMpHost;
        bool isMpJoin;
        int myPid;
        int mpSeen;
        string mpRoster = "";
        string rehostOldSid;
        bool rehostConfirmed;
        DateTime rehostSent = DateTime.MinValue;
        DateTime rehostStarted = DateTime.MinValue;
        // 当上新房主后先等 1.5 秒再找服务器换房主：两台同时当选时（各自看不见对方）几毫秒内就靠代数 / 编号分出胜负，
        // 输的那台降回去、不发；免得两台都发、服务器记成后到的那台
        const double RehostDelay = 1.5;
        DateTime rehostDue = DateTime.MinValue;
        DateTime rehostLastTx = DateTime.MinValue;
        class MpPunch { public string Sid; public IPEndPoint Pub, Local, Alt; public DateTime Until; }
        readonly Dictionary<string, MpPunch> mpPunches = new Dictionary<string, MpPunch>();
        public Action<int, string> OnMpState { get; set; }
        public void UpdateMpTag(string m, string tm, string st)
        {
            MpTag = "m=" + (m ?? "elim") + ";tm=" + (tm ?? "0") + ";st=" + (st ?? "idle");
            if (host) lastReg = DateTime.MinValue;
        }

        public void SetMpState(string st)
        {
            var next = Session.UpdateTagState(string.IsNullOrEmpty(MpTag) ? "m=elim;tm=0;st=idle" : MpTag, st);
            if (next != MpTag)
            {
                MpTag = next;
                if (host) lastReg = DateTime.MinValue;
            }
        }

        public void PromoteToMpHost()
        {
            if (isMpHost) return;
            Log(L.T("多人：成为新房主", "Multi: became new host"));
            NetLog.W("多人：成为新房主");
            host = true;
            isMpHost = true;
            isMpJoin = false;
            rehostOldSid = sid;
            rehostConfirmed = false;
            rehostDue = DateTime.UtcNow.AddSeconds(RehostDelay);
            rehostSent = rehostDue;
            rehostStarted = rehostDue;
            rehostLastTx = DateTime.MinValue;
            if (MpMax < 2) MpMax = MpCfg.Max > 0 ? MpCfg.Max : 4;
            AllowRelay = false;
            key = Guid.NewGuid().ToString("N");
            if (mesh != null)
            {
                mesh.IsHost = true;
            }
        }

        public void DemoteToMpClient(int newHost)
        {
            if (!isMpHost) return;
            Log(L.T("多人：降为普通玩家", "Multi: demoted to client"));
            NetLog.W("多人：降为普通玩家");
            host = false;
            isMpHost = false;
            isMpJoin = true;
            rehostConfirmed = true;      // 还没发的换房主申请作废
            if (mesh != null)
            {
                mesh.IsHost = false;
            }
        }

        void SendRehost()
        {
            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(rehostOldSid)) return;
            string localAddr = LobbyServer.LocalAddr(LocalPort);
            Raw(WireTag.S + "|REHOST|" + roomId + "|" + rehostOldSid + "|" + key + "|" + localAddr, server);
            NetLog.W("向大厅服务器申请换房主：房间 " + roomId);
        }

        // IMpLink（Session 的多人全互连走这些；服务器房也是客户端，Mesh 由 SHOST 消息创建）
        public int MyPid { get { return mesh != null && mesh.MyPid > 0 ? mesh.MyPid : (serverRoom ? serverPid : (isMpHost ? 1 : myPid)); } }
        public int MpSeen { get { return mesh != null && mesh.MpSeen > 0 ? mesh.MpSeen : (serverRoom ? serverPlayers : (isMpHost ? MpCount + 1 : mpSeen)); } }
        public string MpRoster { get { return mesh != null && !string.IsNullOrEmpty(mesh.MpRoster) ? mesh.MpRoster : (serverRoom ? serverRoster : (isMpHost ? HostMpRoster() : mpRoster)); } }
        public int MpCount { get { return serverRoom ? serverPlayers : MpPlayers().Count; } }
        public bool MpJoinMode { get { return serverRoom ? serverRoomMp : isMpJoin; } }
        public List<KeyValuePair<int, string>> MpPlayers()
        {
            if (serverRoom) return new List<KeyValuePair<int, string>>();
            if (mesh != null) return mesh.PruneAndGetPlayers(MpCfg.Lost);
            var gone = new List<PeerRow>();
            var list = peers.MpPlayers(MpCfg.Lost, gone);
            for (int i = 0; i < gone.Count; i++)
            {
                Log(L.T("多人：玩家离开 ", "Multi: player left ") + gone[i].Pid + " " + gone[i].Handle);
                if (mesh != null) mesh.Remove(gone[i].Pid);
            }
            return list;
        }
        string HostMpRoster()
        {
            var players = MpPlayers();
            var sb = new StringBuilder();
            foreach (var p in players) sb.Append(p.Key).Append(':').Append(p.Value.Replace(",", " ").Replace(":", " ")).Append(',');
            return sb.ToString().TrimEnd(',');
        }
        public void SendMpInfo()
        {
            if (serverRoom || !isMpHost) return;
            if (mesh != null)
            {
                mesh.BroadcastMpInfo();
                return;
            }
            var players = MpPlayers();
            var sb = new StringBuilder();
            foreach (var p in players) sb.Append(p.Key).Append(':').Append(p.Value.Replace(",", " ").Replace(":", " ")).Append(',');
            var roster = sb.ToString().TrimEnd(',');
            var to = peers.MpSnapshot<object>();
            foreach (var kv in to)
            {
                var b = wire.Seal(WireTag.P + "MP\npid=" + kv.Value + "\nn=" + (players.Count + 1) + "\nmax=" + MpMax + "\nl=" + roster + "\n");
                RawTo(b, kv.Key);
            }
        }
        public void SendMp(byte[] b, int except)
        {
            var to = peers.MpHandlesExcept<object>(except);
            foreach (var h in to) RawTo(b, h);     // 经桥接入房的人句柄是桥接连接号
        }
        public string MpStatsLines()
        {
            if (mesh == null) return "";
            int hostPid = serverRoom ? serverMatchHost : mesh.HostPid;
            return "\nmpmesh=" + mesh.StatsLine() + "\nmptx=" + mesh.MpTx + "\nmprx=" + mesh.MpRx + "\nmpdual=" + mesh.MpDual
                + "\nmpfwd=" + mesh.MpFwd + "\nmpdup=" + mesh.MpDup + "\nmpdz=" + mesh.DzStat + "\nmpprobe=" + mesh.ProbeStats()
                + (serverScores.Length > 0 ? "\nmps=" + serverScores : "") + (serverMpm.Length > 0 ? "\nsrvmpm=" + serverMpm : "")
                + (hostPid >= 0 ? "\nmphost=" + hostPid : "")
                + "\nmpbridge=" + (MpAlwaysBridge ? "always" : FallbackMyId != 0 ? "fallback" : "off")
                + "\nmpjoinbridge=" + (mpFbH != 0 ? "1" : "0")
                + "\nmpdecoded=" + mesh.MpDecoded + "\nmpdecodewait=" + mesh.MpDecodeWait;
        }
        public string LocalAddrForHost(int hostPid)
        {
            return "";     // 服务器大厅不在游戏内面板/状态栏暴露玩家或房主 IP
        }
        // 客机：收到房主（或经房主转来的）多人包。from = 房主的 UDP 端点，或 经桥接入房时连房主的那条桥接连接号
        void MpJoinText(object from, string textJ, DateTime now, ref int got)
        {
            if (textJ.StartsWith(WireTag.P + "BUSY|", StringComparison.Ordinal))
            {
                if (DeniedMsg == null)
                {
                    DeniedMsg = Link.ParseDeniedMsg(textJ);
                    error = DeniedMsg;
                    log(DeniedMsg);
                    NetLog.W("多人：被房主拒绝，" + DeniedMsg);
                }
                return;
            }
            if (mesh != null)
            {
                int gJ;
                int oldPid = myPid;
                if (textJ.StartsWith(WireTag.P + "MP\n", StringComparison.Ordinal) || textJ.StartsWith(WireTag.P + "MPMESH\n", StringComparison.Ordinal) || textJ.StartsWith(WireTag.P + "MPREFUSE\n", StringComparison.Ordinal)) mesh.NoteJoinHost(from);
                if (mesh.HandlePacket(from, textJ, out gJ))
                {
                    if (gJ > 0) { got += gJ; }
                    lastFromPeer = now;
                    if (mesh.MyPid > 0 && mesh.MyPid != oldPid)
                    {
                        if (noRelay) { noRelay = false; error = ""; }     // 进了房：直连 / 桥接不通的提示收掉
                        myPid = mesh.MyPid;
                        Log(L.T("多人：主机给的编号 ", "Multi: my id ") + myPid);
                        NetLog.W("多人：主机给的编号 " + myPid);
                    }
                    myPid = mesh.MyPid;
                    mpSeen = mesh.MpSeen;
                    mpRoster = mesh.MpRoster;
                    return;
                }
            }
            if (textJ.StartsWith(WireTag.P + "MP\n", StringComparison.Ordinal) || textJ.StartsWith(WireTag.P + "MP\r\n", StringComparison.Ordinal))
            {
                var q = Kv.Parse(textJ);
                string a, b2;
                int pid, n, owner = 1;
                if (q.TryGetValue("pid", out a) && int.TryParse(a, out pid) && pid != myPid)
                {
                    myPid = pid;
                    if (mesh != null) mesh.MyPid = pid;
                    Log(L.T("多人：主机给的编号 ", "Multi: my id ") + pid);
                    NetLog.W("多人：主机给的编号 " + pid);
                }
                if (q.TryGetValue("n", out b2) && int.TryParse(b2, out n)) mpSeen = n;
                string l;
                mpRoster = q.TryGetValue("l", out l) ? l : "";
                if (mesh != null)
                {
                    mesh.AddIntro(owner, from is IPEndPoint ? from.ToString() : "", 0UL, "", from);
                    mesh.PruneRoster(mpRoster);
                }
                got++;
                lastFromPeer = now;
                return;
            }
            return;
        }

        // 入房那条桥接上的包（MpMesh.PumpBridge 交过来）：和 UDP 收包同一套验签，房主按入房 / 成员包处理，客机按房主包处理
        bool OnJoinBridgeRx(object h, byte[] data)
        {
            var now = DateTime.UtcNow;
            string text;
            if (Starts(data, WireTag.P + "PUNCH") || Starts(data, WireTag.P + "HELLO\nsid=")) text = Kv.Utf8.GetString(data);
            else
            {
                text = wire.Verify(data, 0, data.Length, x => RawTo(x, h));
                if (text == null) return false;
            }
            if (isMpHost)
            {
                if (!NoteMpHost(h, text, now)) return false;
                lastFromPeer = now;
                return true;
            }
            if (isMpJoin)
            {
                int g = 0;
                MpJoinText(h, text, now, ref g);
                return true;
            }
            return false;
        }

        // 同一个客机（大厅会话号 sid 一样）从第二个地址发来加入握手：服务器介绍了公网 + 内网（起还有另一族），
        // 客机往每个地址都打洞，两条都通时房主以前按地址认人、分了两个编号（多出来的"影子号"收不到状态包，却一直占名额，还多让掉一个 AI）。
        // 现在：会话号已经对应一个还活着的编号、而且那个编号 3 秒内有动静 → 认成同一个人。另一族的地址挂成它的 AltHandle
        // （ByHandle 也认它的包；房主这边 AltStep 不换路），同一族的丢掉。原编号 3 秒都没动静才放行（NAT 换端口重进照旧按新人走、原编号自己超时）
        int dupSidLogged, unknownMpState;
        bool SameSessionOtherAddr(object from, string helloSid, DateTime now)
        {
            var fe = from as IPEndPoint;
            if (fe == null || mesh == null || string.IsNullOrEmpty(helloSid)) return false;
            int pid;
            MpPeerState op;
            if (!mpSidPid.TryGetValue(helloSid, out pid) || (op = mesh.Get(pid)) == null) return false;
            var he = op.Handle as IPEndPoint;
            if (he == null || he.Equals(fe) || mesh.PidByHandle(fe) > 0) return false;     // 已经认得的地址（含备用地址）照常处理
            DateTime seen = op.LastAnyRx > op.BornAt ? op.LastAnyRx : op.BornAt;
            if ((now - seen).TotalSeconds >= 3.0) return false;
            if (fe.AddressFamily != he.AddressFamily && op.AltHandle == null)
            {
                op.AltHandle = fe;
                NetLog.W("多人：编号 " + pid + " 的另一族地址 " + NetLog.Ip(fe) + " 也通了（同一会话 " + helloSid + "），记成备用地址，不另分编号");
            }
            else if (dupSidLogged++ < 5)
                NetLog.W("多人：编号 " + pid + " 从第二个地址 " + NetLog.Ip(fe) + " 发来加入握手（同一会话 " + helloSid + "），不另分编号");
            return true;
        }

        bool NoteMpHost(object from, string s, DateTime now)
        {
            if (MpMax < 2) return false;
            bool viaFb = !(from is IPEndPoint);                       // 经桥接入房：没有地址，按隐藏 IP 成员算
            string fromAddr = viaFb ? "" : from.ToString();
            bool hello0 = Link.IsMpHello(s);
            string helloSid = null;
            if (hello0) Kv.Parse(s).TryGetValue("sid", out helloSid);
            // 同一个人（大厅会话号一样）先经 UDP 进了房、又改经桥接来：沿用原编号，不再分第二个
            if (viaFb && hello0 && mesh != null && !string.IsNullOrEmpty(helloSid) && mesh.PidByHandle(from) == 0)
            {
                int oldPid;
                MpPeerState op;
                if (mpSidPid.TryGetValue(helloSid, out oldPid) && (op = mesh.Get(oldPid)) != null && op.Handle is IPEndPoint)
                {
                    int rp;
                    peers.RemoveMp(op.Handle, out rp);
                    if (mesh.RebindToBridge(oldPid, from))
                    {
                        PeerRow pr;
                        peers.NoteMp(from, false, MpMax, MpCfg.Cap, MpCfg.Lost, out pr, null, oldPid);
                        NetLog.W("多人：编号 " + oldPid + " 的直连只通一边，改经桥接（沿用原编号）");
                        mesh.SendIntros(DateTime.UtcNow, true);
                        SendMpInfo();
                        int g0;
                        mesh.HandlePacket(from, s, out g0);
                        return true;
                    }
                }
            }
            bool hello = Link.IsMpHello(s);
            PeerRow p;
            if (hello && !viaFb && SameSessionOtherAddr(from, helloSid, now)) return true;     // 同一个人从第二个地址来：不分新编号、不让 AI、不回绝
            if (hello && mesh != null && mesh.DuelRefuse(from)) return true;     // 对决模式观众已满：回 4NMPREFUSE 带原因
            // 对局中拒收新人：只在对局中、而且是加入握手时才查"是不是已经在房里"；对决模式不拦（观众打着也能进）
            if (!MpCfg.MidJoin && IsMpInMatch && !MpCfg.DuelOn && (hello || (s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal) && !Link.IsSpecHello(s)))
                && !((mesh != null && mesh.HasHandle(from)) || peers.NoteMp(from, false, MpMax, MpCfg.Limit, MpCfg.Lost, out p) == 1))
            {
                var rej = wire.Seal(WireTag.P + "BUSY|inmatch\n");
                RawTo(rej, from);
                return true;
            }
            int existingPid = mesh != null ? mesh.PidByHandle(from) : 0;
            if (existingPid > 0)
            {
                if (hello) mesh.AddPlayer(existingPid, from, fromAddr, 0UL, Link.HelloNick(s), viaFb ? "" : Link.HelloLan(s), viaFb || Link.HelloHide(s), viaFb ? "" : Link.HelloV6(s));
                int g;
                if (mesh.HandlePacket(from, s, out g)) return true;
                return true;
            }
            if (hello && mesh != null && mesh.IsHost && !Link.HelloWatch(s)) mesh.TryYieldAiForHuman(MpMax);     // 观战加入不挤 AI
            var extraUsed = mesh != null ? mesh.Pids() : null;
            if (extraUsed != null && mesh.MyPid > 0) extraUsed.Add(mesh.MyPid);     // 不再写死占住 1 号：换房主后走掉的 1 号可以补给新人（mesh.Pids 里还在的成员照样占着）
            int r = peers.NoteMp(from, hello, MpMax, MpCfg.Cap, MpCfg.Lost, out p, extraUsed, existingPid);
            if (r == 0)
            {
                // 不认识的地址发来的状态包：不交给游戏（下面照旧返回 true，会话结束的"共收状态包"仍会算上它）
                if (!hello && !viaFb && s.Contains("seq=") && unknownMpState++ < 3)
                    NetLog.W("多人：地址 " + NetLog.Ip(from as IPEndPoint) + " 不对应任何编号，它发来的状态包丢掉（不交给游戏）");
                return !Link.IsSpecHello(s) && (s.Contains("seq=") || s.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal));
            }
            if (r < 0) { if (hello && mesh != null) mesh.DuelRefuseFull(from); return true; }     // 对决模式满了明说；普通多人房满了不回绝：握手留着，有人走了就补上
            if (r == 2)
            {
                if (!string.IsNullOrEmpty(helloSid)) { if (mpSidPid.Count > 64) mpSidPid.Clear(); mpSidPid[helloSid] = p.Pid; }     // 记下大厅会话号 → 编号
                log(L.T("多人：玩家加入，编号 ", "Multi: player joined, id ") + p.Pid + " " + (viaFb ? L.T("（经桥接）", "(via bridge)") : fromAddr));
                NetLog.W("多人：玩家加入 编号 " + p.Pid + " " + (viaFb ? (MpAlwaysBridge ? "经桥接（一直桥接）" : "经桥接（直连失败走桥接）") : NetLog.Ip((IPEndPoint)from)));
                if (mesh != null)
                {
                    mesh.AddPlayer(p.Pid, from, fromAddr, p.BridgeId, p.Nick, viaFb ? "" : Link.HelloLan(s), viaFb || Link.HelloHide(s), viaFb ? "" : Link.HelloV6(s));
                    mesh.MyV6Addr = MpMesh.V6Ep(LocalPort);     // 房主介绍自己时带上
                    mesh.SendIntros(DateTime.UtcNow, true);
                }
                SendMpInfo();
            }
            else if (mesh != null) mesh.AddPlayer(p.Pid, from, fromAddr, p.BridgeId, p.Nick, viaFb ? "" : Link.HelloLan(s), viaFb || Link.HelloHide(s), viaFb ? "" : Link.HelloV6(s));
            if (mesh != null)
            {
                int g;
                if (mesh.HandlePacket(from, s, out g))
                {
                    return true;
                }
            }
            if (s.StartsWith(WireTag.P, StringComparison.Ordinal) || s.Contains("hello=1")) return true;
            return true;
        }
        public void EnableMpMesh(bool hostMode)
        {
            if (!serverRoom && !isMpHost && !isMpJoin) return;          // 普通 1v1 服务器大厅不开 Mesh
            if (mesh != null) return;
            mesh = new MpMesh();
            if (MpAlwaysBridge) { mesh.HideMe = true; mesh.AllowHostMigration = false; }     // 强制房不迁移到 UDP 新房主
            mesh.IsHost = serverRoom ? false : hostMode;
            mesh.NeedVerifiedDirect = true;   // 介绍的地址不代表直连通了（可能被 NAT 挡住），收到包才算
            mesh.MyPid = serverRoom ? serverPid : (hostMode ? 1 : 0);
            mesh.Seal = b => wire.Seal(b);
            mesh.SendRaw = delegate(object h, byte[] b) { RawTo(b, h); };     // 桥接连接号也能发
            mesh.JoinBridgeRx = OnJoinBridgeRx;
            mesh.Deliver = delegate(string tagged)
            {
                if (NoOut) return;
                // 1v1 服务器房：游戏这一层还是普通 1v1，收到带 src= 会被当成多人包（main.lua netRecv）；
                // 去掉 src 再交给游戏。多人房保留 src（game_multi 按编号分替身）。
                if (serverRoom && !serverRoomMp)
                {
                    var sb = new StringBuilder(tagged.Length);
                    foreach (var raw in tagged.Split('\n'))
                        if (raw.Length > 0 && !raw.StartsWith("src=", StringComparison.Ordinal)) sb.Append(raw).Append('\n');
                    outq.Push(sb.ToString());
                }
                else outq.Push(tagged);
            };
            mesh.DeliverRel = delegate(string ev) { var cb = OnServerEvent; if (cb != null) cb(ev); };
            mesh.EnsureHandle = delegate(int pid, string addr, ulong sid0)
            {
                if (string.IsNullOrEmpty(addr)) return null;
                int c = addr.LastIndexOf(':');
                IPAddress ip;
                int p;
                if (c <= 0 || !IPAddress.TryParse(addr.Substring(0, c), out ip) || !int.TryParse(addr.Substring(c + 1), out p)) return null;
                return new IPEndPoint(ip, p);
            };
            mesh.Dedupe = delegate(string src, int seq)
            {
                int g;
                return peers.TakeSrcSeq(src, seq, out g);
            };
            mesh.ServerRelay = delegate(int pid, string body)
            {
                if (!serverRoom || serverPid <= 0) return false;
                sendSrv(WireTag.S + "|SRLY|" + pid + "|" + serverToken + "\n" + body);
                return true;
            };
            mesh.Log = m => Log(m);
            mesh.Enabled = true;
            mesh.OnRosterUpdated = delegate(int pid, int seen, string roster)
            {
                myPid = pid;
                mpSeen = seen;
                mpRoster = roster;
            };
            mesh.OnHostChanged = delegate(int newHost, int newEpoch)
            {
                if (mesh.MyPid == newHost)
                {
                    PromoteToMpHost();
                }
                else if (isMpHost && mesh.MyPid != newHost)
                {
                    DemoteToMpClient(newHost);
                }
            };
        }
        void sendSrv(string text) { try { var b = Kv.Utf8.GetBytes(text); Raw(b, server); } catch (Exception) { } }
        void srvEvent(string ev) { var cb = OnServerEvent; if (cb != null) cb(ev); }

        // 服务器房：把本机状态低频上报（4 次/秒）。观战转发 + 比赛计时 / 双方上报交叉核对 / 捡道具判定用。
        // 游戏侧只加了这一条调用，状态包本身仍由 MeshMp 直连发给其他玩家。
        public void StateTap(string body)
        {
            if (!serverRoom || serverPid <= 0 || string.IsNullOrEmpty(body)) return;
            var now = DateTime.UtcNow;
            if ((now - lastSrvState).TotalSeconds >= 0.25)
            {
                lastSrvState = now;
                sendSrv(WireTag.S + "|SSTATE|" + serverPid + "|" + serverToken + "\n" + body);
            }
            if ((now - lastSrvReport).TotalSeconds < 0.25) return;
            lastSrvReport = now;
            var d = Kv.Parse(body);
            string v;
            var sb = new StringBuilder();
            int dead = 0;
            if (d.TryGetValue("mpd", out v))
            {
                var parts = v.Split(':');
                if (parts.Length > 1) int.TryParse(parts[1], out dead);
            }
            else if (d.TryGetValue("dead", out v)) int.TryParse(v, out dead);
            if (dead > 0) sb.Append("dead=").Append(dead).Append(';');
            if (d.TryGetValue("ko", out v) && v.Length > 0 && v.Length < 8) sb.Append("ko=").Append(v).Append(';');
            string x, y, z;
            if (d.TryGetValue("x", out x) && d.TryGetValue("y", out y) && d.TryGetValue("z", out z))
                sb.Append("x=").Append(x).Append(";y=").Append(y).Append(";z=").Append(z).Append(';');
            if (d.TryGetValue("pick", out v) && v.Length > 0 && v != lastPickSent)
            {
                lastPickSent = v;
                sb.Append("pick=").Append(LobbyServer.Clean(v)).Append(';');
            }
            if (sb.Length > 0) sendSrv(WireTag.S + "|SREPORT|" + serverPid + "|" + serverToken + "|" + sb.ToString());
        }

        // ---- 排队（主机）：上场由服务器叫（YOURTURN），这里只管让现在的对手下场：发 4NROLE spec，3 秒后丢掉这个对手
        DateTime demoteAt = DateTime.MinValue;
        public List<object> QueueOrder() { var l = new List<object>(); for (int i = 0; i < Watchers; i++) l.Add(i); return l; }
        public object PromoteHead() { return null; }
        public object DemotePeer()
        {
            if (peer == null && !relay) return null;
            demoteAt = DateTime.UtcNow;
            NetLog.W("排队：让对手下场观战（3 秒后断开，服务器叫队首上场）");
            return "peer";
        }
        public void SendTo(object who, byte[] b) { if ("peer".Equals(who)) Send(b); }
        public bool HasPeer { get { return peer != null || relay; } }
        public double SinceFromPeer { get { return peers.SinceFromPeer; } }
        public bool CanPromote { get { return Watchers > 0; } }
        void DropPeer()
        {
            demoteAt = DateTime.MinValue;
            if (buUp || buAsked) CloseFb();
            BuReset();
            sid = null; peer = null; relay = false; relayOk = false; rx.Reset();
            lastFromPeer = DateTime.MinValue;
            cands.Clear();
            NetLog.W("排队：对手已下场，等服务器介绍下一位");
        }

        // ---- 主机下场：第二位选手由服务器挑（队首）、双向转发（4NU 发出去 / 4NV 收进来）
        public volatile bool SecondOn;          // 服务器 REGOK 第 6 段
        DateTime lastSecondReq = DateTime.MinValue;
        DateTime lastFrom2 { get { return peers.LastFromSecond; } set { peers.LastFromSecond = value; } }
        bool wantSecond;
        public Action<string> OnSecondState { get; set; }
        public bool NoOut { get; set; }
        public bool HasSecond { get { return wantSecond && (SecondOn || peers.SinceFromSecond < 3); } }
        public double SinceFromSecond { get { return peers.SinceFromSecond; } }
        public object PromoteSecond()
        {
            if (Watchers == 0) return null;
            wantSecond = true;
            peers.SetSecond("second", PeerRoute.Via, 0UL);
            lastFrom2 = DateTime.UtcNow;
            lastSecondReq = DateTime.MinValue;
            NetLog.W("主机下场：请服务器挑第二位选手");
            return "second";      // 上场通知由服务器发（SECOND），Session 发的 4NROLE 这里不发（SendTo 只认 "peer"）
        }
        public object DemoteSecond()
        {
            if (!wantSecond) return null;
            wantSecond = false;
            peers.SetSecond(null, PeerRoute.Via, 0UL);
            Raw(WireTag.S + "|HOSTUP|" + key, server);
            NetLog.W("主机回来打：第二位选手回去观战");
            return null;
        }
        public void SecondToPeer() { DemoteSecond(); }
        public void SendSecond(byte[] b)
        {
            if (!wantSecond || roomId.Length == 0) return;
            var head = Kv.Utf8.GetBytes(WireTag.P + "U|" + roomId + "\n");
            var pkt = new byte[head.Length + b.Length];
            Buffer.BlockCopy(head, 0, pkt, 0, head.Length);
            Buffer.BlockCopy(b, 0, pkt, head.Length, b.Length);
            tx++;
            Raw(pkt, server);
        }

        // 主机发来的换角色（客机这边）：直连和中转两条路都要看
        bool RoleCtl(string s)
        {
            if (host || !s.StartsWith(WireTag.P + "ROLE", StringComparison.Ordinal)) return false;
            string rv;
            if (Kv.Parse(s).TryGetValue("role", out rv)) { var cb = OnRole; if (cb != null) cb(rv); }
            return true;
        }
        public void SendWatch(byte[] b)
        {
            if (!host || roomId.Length == 0 || Watchers == 0) return;
            var head = Kv.Utf8.GetBytes(WireTag.P + "W|" + roomId + "\n");
            var pkt = new byte[head.Length + b.Length];
            Buffer.BlockCopy(head, 0, pkt, 0, head.Length);
            Buffer.BlockCopy(b, 0, pkt, head.Length, b.Length);
            WatchTx++;
            Raw(pkt, server);
        }
        int attempt = 1;
        DateTime attemptAt, lastJoin, lastPunch, lastReg, lastRelayReq, lastFromServer = DateTime.MinValue, punchUntil;
        DateTime lastFromPeer { get { return peers.LastFromPeer; } set { peers.LastFromPeer = value; } }
        // 连接诊断（net\net_conn.log）：只在状态变化时写；计数在每轮打洞 / 每个会话开始时清零
        readonly DateTime born = DateTime.UtcNow;
        DateTime relayAt, relayReqAt, joinAt;
        bool srvAnswered, srvQuietLogged, regLogged, joinLogged, punchEndLogged, altOkLogged;
        string peerAlt = "";
        int unknownPkts, noAckPkts, relayIgnored, relayBadSid;
        long relayRx;
        string noRelayWhy = "", lastErrWhy = "", peerPub = "", peerLocal = "", busySid = "";

        // host：roomOrNull = null；client：要加入的房间号
        public ServerLink(IPEndPoint server, string roomOrNull, int port, string name, string fRemote, bool simNoDirect)
        {
            this.server = server;
            outq = new RemoteOut(fRemote);
            rx = new RxState(peers);
            this.name = name;
            MapOf = () => "?";
            ServerName = "";
            SimNoDirect = simNoDirect;
            host = roomOrNull == null;
            if (!host) roomId = roomOrNull.Trim().ToUpperInvariant();
            isMpHost = host && MpCfg.Max >= 2;
            isMpJoin = !host && MpCfg.Join;
            if (isMpHost)
            {
                MpMax = MpCfg.Max;
                MpTag = "m=elim;tm=0;st=idle";
                AllowRelay = false;
                log(L.T("多人房不经服务器中转（房主设了直连失败时走桥接的话，打不通改走桥接）", "Multiplayer rooms never use the server relay (the bridge is the fallback if the host allows it)"));
                NetLog.W("多人房不经服务器中转；直连失败时走桥接看房主的\"对局走桥接\"设置");
            }
            if (isMpJoin)
            {
                AllowRelay = false;
            }
            key = Guid.NewGuid().ToString("N");
            // 双栈：主机的"输入 IP 直连"端口也收 IPv6。 服务器是 IPv6 时按它开（IPv6 关了也开双栈、回环测试绑 ::1）
            udp = NetBind.SessionUdp(port, server != null && server.AddressFamily == AddressFamily.InterNetworkV6 ? server : null);
            try { udp.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch (Exception) { }   // 同 Link：忽略 ICMP 端口不可达
            pick = new ServerPick(server, udp);     // 域名同时有 A 和 AAAA：一个没回应换另一个
            attemptAt = DateTime.UtcNow;
        }

        public string RoomId { get { return roomId; } }
        public IPEndPoint Server { get { return server; } }
        public string Sid { get { return sid; } }       // 服务器给的会话号（排位上报用）
        public bool Relay { get { return relay; } }
        public string Error { get { return error; } }
        public int LocalPort { get { return ((IPEndPoint)udp.Client.LocalEndPoint).Port; } }
        public string PeerStr { get { return bridged ? L.T("经桥接", "via bridge") : relay ? L.T("经服务器中转", "via server relay") : (peer != null ? peer.ToString() : ""); } }
        // 对方的 IP（直连时；中转时看到的是服务器，不算）
        public IPAddress PeerIp { get { var p = peer; return relay || bridged || p == null ? null : p.Address; } }
        // 诊断里写的（地址脱敏）
        public string PeerDiag { get { return bridged ? "经桥接" : relay ? "经服务器中转" : peer != null ? NetLog.Ip(peer) : "（还没有）"; } }
        public string ServerDiag
        {
            get
            {
                var t = (ServerName ?? "").Trim();
                foreach (var c in t) if (char.IsLetter(c)) return t + "（" + NetLog.Ip(server) + "）";
                return NetLog.Ip(server);     // 填的是 IP（或互通过来的房间）：也脱敏
            }
        }
        static string Ms(DateTime from, DateTime now) { return (int)(now - from).TotalMilliseconds + " ms"; }
        string Cand(IPEndPoint ep)
        {
            int i = cands.IndexOf(ep);
            if (i > 0 && cands[0].AddressFamily != ep.AddressFamily) return "另一族候选（" + ServerPick.Fam(ep) + "）";
            return i == 0 ? "公网候选" : i == 1 ? "内网候选" : "不在候选里";
        }
        public string PeerName { get { return ""; } }
        public string PeerNick { get { var f0 = fb; return bridged && f0 != null ? f0.PeerNick : rx.PeerNick; } }
        public long Tx { get { return tx; } }
        public long Gap { get { var f0 = fb; return bridged && f0 != null ? f0.Gap : rx.Gap; } }
        public double PingMs
        {
            get
            {
                var f0 = fb;
                if (bridged && f0 != null) return f0.PingMs;
                if (buUp) return buFbPing >= 0 ? buFbPing : (f0 != null ? f0.PingMs : -1);
                if (buDirPing >= 0) return buDirPing;
                return rx.DelayN > 0 ? rx.DelaySum / rx.DelayN * 2000 : -1;
            }
        }
        public Action<string> Log { get { return log; } set { log = value ?? delegate { }; } }

        // 给面板看的线路：host = wait / punch / direct / relay；client = punch1..3 / relay / direct；服务器没回应 = noserver
        public string Route
        {
            get
            {
                if (bridged) { var f0 = fb; return f0 != null && f0.Linked ? "bridge" : "bridgewait"; }
                if (buUp) return "bridge";
                if (relay) return "relay";
                if (peer != null) return "direct";
                if (host) return sid != null && DateTime.UtcNow < punchUntil ? "punch" : "wait";
                return "punch" + attempt;
            }
        }

        bool ServerQuiet { get { return (DateTime.UtcNow - lastFromServer).TotalSeconds > 5; } }

        static bool Starts(byte[] d, string head)
        {
            if (d.Length < head.Length) return false;
            for (int i = 0; i < head.Length; i++) if (d[i] != (byte)head[i]) return false;
            return true;
        }

        void Raw(string text, IPEndPoint to) { Raw(Kv.Utf8.GetBytes(text), to); }
        void Raw(byte[] b, IPEndPoint to)
        {
            // 强制策略覆盖所有 Mesh 探测 / 迁移 / 备用路径，不只是初次入房握手。
            if (MpAlwaysBridge && (pick == null || !pick.Is(to))) return;
            try { udp.Send(b, b.Length, to); } catch (Exception) { }
        }
        // 句柄可能是 UDP 端点，也可能是经桥接入房的桥接连接号
        void RawTo(byte[] b, object to)
        {
            var ep = to as IPEndPoint;
            if (ep != null) { Raw(b, ep); return; }
            if (to is uint) { try { Bridge.SendTo((uint)to, b, 1); } catch (Exception) { } }
        }

        public void Send(byte[] b)
        {
            if (serverRoom)
            {
                // 服务器房里状态包走 Mesh.BroadcastState；这里只接聊天这类可靠事件，交给服务器广播。
                string txt;
                try { txt = Kv.Utf8.GetString(b); } catch (Exception) { return; }
                // 聊天的事件体在可靠通道里是 "4NEV\n<序号>…chat <文字>"：服务器房只把聊天摘出来交给服务器广播
                string chat = null;
                if (txt.StartsWith("chat ", StringComparison.Ordinal)) chat = txt.Substring(5);
                else
                {
                    // 可靠事件 4NEV\nsid=…\ne12=chat <文字>：找 "=chat "
                    int ci = txt.IndexOf("=chat ", StringComparison.Ordinal);
                    if (ci >= 0) chat = txt.Substring(ci + 6);
                }
                if (chat != null)
                {
                    int nl = chat.IndexOf('\n');
                    if (nl >= 0) chat = chat.Substring(0, nl);
                    sendSrv(WireTag.S + "|SCHAT|" + serverToken + "|" + LobbyServer.Clean(chat));
                }
                return;
            }
            if (bridged)     // 直连不通改走了桥接
            {
                var f0 = fb;
                if (f0 != null) { tx++; f0.Send(b); }
                return;
            }
            if (buUp)
            {
                var f0 = fb;
                if (f0 != null && f0.Linked) { tx++; f0.Send(b); return; }
            }
            if (relay)
            {
                if (sid == null) return;
                var head = Kv.Utf8.GetBytes(WireTag.P + "R|" + sid + "\n");
                var pkt = new byte[head.Length + b.Length];
                Buffer.BlockCopy(head, 0, pkt, 0, head.Length);
                Buffer.BlockCopy(b, 0, pkt, head.Length, b.Length);
                tx++;
                Raw(pkt, server);
                return;
            }
            if (peer == null) return;   // 还在打洞：握手包由 Pump 里的打洞代替
            tx++;
            Raw(b, peer);
        }

        // 主机拉黑了正连着的对手：放掉这条连接、马上重新登记（不再等 3 秒没包才报"房间空了"）。
        // 以前被拉黑的人立刻重新加入时，服务器还按"有人"回 full，客机卡在"连接中"、收不到拒绝（test_blacklist ② 偶发失败）。
        // 命令线程只置标记，收包线程（这里）来做，免得和 Pump 抢字段
        public volatile bool KickReq, Kicked;
        public int Pump()
        {
            var now = DateTime.UtcNow;
            int got = 0;
            if (KickReq)
            {
                KickReq = false;
                if (host && (peer != null || relay)) { DropPeer(); lastReg = DateTime.MinValue; Kicked = true; NetLog.W("拉黑：放掉当前对手，马上重新登记房间"); }
            }
            while (true)
            {
                int avail;
                try { avail = udp.Available; } catch (Exception) { break; }
                if (avail <= 0) break;
                IPEndPoint from = null;
                byte[] data;
                try { data = udp.Receive(ref from); } catch (Exception) { break; }
                from = NetBind.Norm(from);     // 双栈 socket 收到的 IPv4 包是 ::ffff:a.b.c.d
                if (pick.Is(from))     // 服务器的两个地址都认
                {
                    if (pick.Got(from)) PickSwapped("先回应的是 " + ServerPick.Fam(pick.Cur) + " 地址，改用它");
                    got += FromServer(data, now);
                    continue;
                }
                if (SimNoDirect || MpAlwaysBridge) continue;
                if (serverRoom)
                {
                    // 服务器介绍后玩家之间的直连包（MeshMp 协议 + 带 src 的状态包）。
                    var to3 = from;
                    var txt3 = wire.Verify(data, 0, data.Length, x => Raw(x, to3));
                    if (txt3 != null && mesh != null)
                    {
                        int g3;
                        if (mesh.HandlePacket(from, txt3, out g3))
                        {
                            if (g3 > 0) { got += g3; lastFromPeer = now; }
                            continue;
                        }
                    }
                    continue;       // 服务器房不认别的陌生包
                }
                if (isMpHost)
                {
                    string textMp;
                    if (Starts(data, WireTag.P + "PUNCH") || Starts(data, WireTag.P + "HELLO\nsid=")) textMp = Kv.Utf8.GetString(data);
                    else
                    {
                        var toMp = from;
                        textMp = wire.Verify(data, 0, data.Length, x => Raw(x, toMp));
                        if (textMp == null) continue;
                    }
                    if (NoteMpHost(from, textMp, now))
                    {
                        got++;
                        lastFromPeer = now;
                        continue;
                    }
                    continue;
                }
                if (isMpJoin)
                {
                    string textJ;
                    if (Starts(data, WireTag.P + "PUNCH") || Starts(data, WireTag.P + "HELLO\nsid=")) textJ = Kv.Utf8.GetString(data);
                    else
                    {
                        var toJ = from;
                        textJ = wire.Verify(data, 0, data.Length, x => Raw(x, toJ));
                        if (textJ == null) continue;
                    }
                    MpJoinText(from, textJ, now, ref got);
                    continue;
                }
                if (relay || bridged) continue;     // 已经改走中转 / 桥接：之后到的直连包不理（免得两条路来回切）
                // v0.20.3 输入 IP 直连：主机还没有客机时，也收陌生地址发来的包（握手 / 状态），其余只收认识的地址
                bool known = from.Equals(peer) || cands.Contains(from);
                if (!known && !(host && peer == null && sid == null))
                {
                    if (unknownPkts++ == 0)
                        NetLog.W("收到不在候选里的地址 " + NetLog.Ip(from) + " 的包，丢掉（对方的 NAT 可能给这次连接换了端口）；候选："
                                 + string.Join(" / ", cands.ConvertAll(NetLog.Ip).ToArray()));
                    continue;
                }
                // 打洞包（4NPUNCH / 打洞用的 4NHELLO sid=）是这一层自己的，不签名；其余先验签（v0.22.0）
                string text;
                if (Starts(data, WireTag.P + "PUNCH") || Starts(data, WireTag.P + "HELLO\nsid=")) text = Kv.Utf8.GetString(data);
                else
                {
                    var to = from;
                    text = wire.Verify(data, 0, data.Length, b => Raw(b, to));
                    if (text == null) continue;
                }
                if (!known && (text.StartsWith(WireTag.P + "HELLO") || text.Contains("hello=1") || text.Contains("seq=")))
                {
                    peer = from;
                    rx.Reset();
                    log(L.T("客机用 IP 直连进来：", "Direct-IP client: ") + from);
                    NetLog.W("客机用 IP 直连进来：" + NetLog.Ip(from) + "（第一个包 " + Link.Head(text) + "）");
                }
                if (!from.Equals(peer) && !cands.Contains(from)) continue;
                // 客机还没锁定对端时主机回的 4NDENY（验过签、来自候选地址）也要认：被拉黑后再加入，
                // 主机只回拒绝、不回带 ack=1 的打洞包，下面的 ack 判断会把它当没通的打洞包跳过 → 客机一直卡在"连接中"
                if (!host && peer == null && text.StartsWith(WireTag.P + "DENY", StringComparison.Ordinal)) { wire.Control(text); continue; }
                if (!host && text.StartsWith(WireTag.P + "BUSY|", StringComparison.Ordinal))
                {
                    if (DeniedMsg == null)
                    {
                        DeniedMsg = Link.ParseDeniedMsg(text);
                        error = DeniedMsg;
                        log(DeniedMsg);
                        NetLog.W("被房主拒绝：" + DeniedMsg);
                    }
                    continue;
                }
                if (peer == null)
                {
                    // 客机要确认是双向通的：主机的打洞包带 ack=1（= 主机收到过我们的包）或已经是状态包才算直连。
                    // 只有"主机 → 客机"通的时候（客机的包到不了主机），没有这一步客机会以为连上了、永远不改走中转
                    if (!host && text.StartsWith(WireTag.P) && text.IndexOf("ack=1", StringComparison.Ordinal) < 0)
                    {
                        if (noAckPkts++ == 0) NetLog.W("收到主机的打洞包（" + Cand(from) + "）但没带 ack：主机→客机通了，客机→主机还没通，接着打洞");
                        continue;
                    }
                    peer = from;
                    rx.Reset();
                    log(L.T("直连成功：", "Direct connection: ") + from + (host ? "" : L.T("（第 " + attempt + " 次）", " (try " + attempt + ")")));
                    NetLog.W("直连成功：" + NetLog.Ip(from) + "（" + Cand(from) + (host ? "" : "，第 " + attempt + " 次，这次打洞 " + Ms(attemptAt, now)) + "）");
                    if (noRelay || blocked) { error = ""; blocked = false; }
                }
                lastFromPeer = now;
                if (RoleCtl(text) || BuCtl(text, false) || wire.Control(text)) continue;
                if (rx.Take(text)) { got++; wire.DzSeenText(text); if (!NoOut) outq.Push(text); var fw = OnPeerState; if (fw != null) fw(text); }
            }
            got += PumpFallback(now);
            got += BuTick(now);
            outq.Flush();   // 每轮写一个（RemoteOut）
            if (got > 0) TakeNewest();
            if (host && demoteAt != DateTime.MinValue && (now - demoteAt).TotalSeconds > 3.2) DropPeer();
            if (host && wantSecond && !SecondOn && (now - lastSecondReq).TotalSeconds > 2) { lastSecondReq = now; Raw(WireTag.S + "|HOSTDOWN|" + key, server); }
            if (pick.Tick(now))      // 一直没回应：换另一族地址试，马上重发登记 / 加入
            {
                PickSwapped(ServerPick.Fam(pick.Other) + " 地址 2.5 秒没回应，改试 " + ServerPick.Fam(pick.Cur));
                lastReg = lastJoin = lastSrvJoin = DateTime.MinValue;
            }
            if (host) HostTick(now); else ClientTick(now);
            ServerWatch(now);
            return got;
        }

        void PickSwapped(string why)
        {
            server = pick.Cur;
            NetLog.W("大厅服务器：" + why + "（" + NetLog.Ip(server) + "）");
        }

        // 大厅服务器有没有回应：第一次回应、5 秒没回应、恢复各写一行
        void ServerWatch(DateTime now)
        {
            if (bridged && !host) return;     // 客机改走桥接后不再和大厅服务器说话，服务器不回是正常的
            if (lastFromServer != DateTime.MinValue && !ServerQuiet)
            {
                if (!srvAnswered) NetLog.W(srvQuietLogged ? "大厅服务器恢复回应" : "大厅服务器有回应（开始后 " + Ms(born, lastFromServer) + "）");
                srvAnswered = true; srvQuietLogged = false;
                return;
            }
            if (srvQuietLogged || (lastFromServer == DateTime.MinValue && (now - born).TotalSeconds < 5)) return;
            srvQuietLogged = true; srvAnswered = false;
            NetLog.W(lastFromServer == DateTime.MinValue ? "大厅服务器 5 秒都没回应：服务器 " + ServerDiag + "（地址填错 / 服务器没开 / 本机防火墙挡了 UDP）"
                                                         : "大厅服务器 5 秒没回应了");
        }

        // 最新的对方状态包：昵称； 排位房间再取 rk= / mt= / quit=
        void TakeNewest()
        {
            var s = rx.Newest;
            if (s == null) return;       // 服务器房：状态包走 MeshMp，不经 RxState，这里可能是空的
            rx.PeerNick = Kv.NickOf(s) ?? rx.PeerNick;
            if (RankPid == null || s == null) return;
            var d = Kv.Parse(s);
            string v;
            if (d.TryGetValue("rk", out v)) PeerRk = v;
            if (d.TryGetValue("mt", out v)) PeerMt = v;
            if (d.TryGetValue("quit", out v)) PeerQuit = v;
            var cb = PeerPacket;
            if (cb != null) cb(d);
        }

        int FromServer(byte[] data, DateTime now)
        {
            lastFromServer = now;
            // 服务器房的中转状态包按原始字节取（不能先 Split('|')：状态包里的名字 / 地图可能带 |）
            if (serverRoom && data.Length > 14 && data[0] == 'P' && data[1] == 'B' && data[2] == 'Z' && data[3] == 'S'
                && data[4] == '|' && data[5] == 'S' && data[6] == 'R' && data[7] == 'E' && data[8] == 'L'
                && data[9] == 'A' && data[10] == 'Y' && data[11] == 'I' && data[12] == 'N' && data[13] == '|')
            {
                int nl0 = Array.IndexOf(data, (byte)'\n', 14);
                if (nl0 > 14)
                {
                    int pid0;
                    if (int.TryParse(Encoding.ASCII.GetString(data, 14, nl0 - 14), out pid0))
                    {
                        var body0 = Encoding.UTF8.GetString(data, nl0 + 1, data.Length - nl0 - 1);
                        lastFromPeer = now;
                        if (mesh != null) mesh.DeliverState(pid0, body0);
                    }
                }
                return 0;
            }
            if (host && data.Length > 5 && data[0] == 'P' && data[1] == 'B' && data[2] == 'Z' && data[3] == 'V' && data[4] == '|')
            {
                // 第二位选手经服务器发来的包
                int nl2 = Array.IndexOf(data, (byte)'\n');
                if (nl2 < 0 || !wantSecond) return 0;
                var b2 = wire.Verify(data, nl2 + 1, data.Length - nl2 - 1, x => { });
                if (b2 == null) return 0;
                lastFrom2 = now;
                if (b2.StartsWith(WireTag.P, StringComparison.Ordinal) || b2.Contains("hello=1")) return 0;
                string q2s;
                int q2, g2;
                if (!Kv.Parse(b2).TryGetValue("seq", out q2s) || !int.TryParse(q2s, out q2)) return 0;
                if (!peers.TakeSecondSeq(q2, out g2)) return 0;
                var cb2 = OnSecondState;
                if (cb2 != null) cb2(b2);
                return 0;
            }
            if (data.Length > 5 && data[0] == 'P' && data[1] == 'B' && data[2] == 'Z' && data[3] == 'R' && data[4] == '|')
            {
                // 中转来的状态包：4NR|<会话号>\n<包>
                int nl = Array.IndexOf(data, (byte)'\n');
                if (nl < 0 || sid == null || Encoding.ASCII.GetString(data, 5, nl - 5) != sid)
                {
                    if (relayBadSid++ == 0) NetLog.W("收到中转包但会话号对不上（本机 " + (sid ?? "没有会话") + "），丢掉");
                    return 0;
                }
                if (!relay)
                {
                    if (!host || !AllowRelay)
                    {
                        if (relayIgnored++ == 0) NetLog.W(host ? "收到中转来的包，但本机设置里没允许中转，丢掉" : "还在打洞时收到中转包，丢掉");
                        return 0;
                    }
                    relay = true; peer = null; rx.Reset(); relayAt = now; relayRx = 0;
                    log(L.T("对方改走服务器中转", "Peer switched to server relay"));
                    NetLog.W("对方改走服务器中转（收到第一个中转包时切过来）");
                }
                var body = wire.Verify(data, nl + 1, data.Length - nl - 1, Send);
                if (body == null) return 0;
                if (blocked) { error = ""; blocked = false; }
                if (relayRx++ == 0) NetLog.W("中转收到对方第一个包（" + Link.Head(body) + "，切到中转后 " + Ms(relayAt, now) + "）");
                lastFromPeer = now;
                if (RoleCtl(body) || wire.Control(body) || !rx.Take(body)) return 0;
                wire.DzSeenText(body);         // 认定是对手的状态包了：算对方会不会拼省流包
                if (!NoOut) outq.Push(body);   // 中转来的也要排队写（漏了这条路，中转时对方状态一帧都写不出去）
                var fw2 = OnPeerState;
                if (fw2 != null) fw2(body);
                TakeNewest();
                return 1;
            }
            var f = Kv.Utf8.GetString(data).Split('|');
            if (f.Length < 2 || f[0] != WireTag.S) return 0;
            switch (f[1])
            {
                case "REHOSTOK":
                    if (f.Length > 2)
                    {
                        rehostConfirmed = true;
                        roomId = f[2];
                        log(L.T("大厅换房主成功：房间 ", "Rehost succeeded on lobby: room ") + roomId);
                        NetLog.W("大厅换房主成功：房间 " + roomId);
                    }
                    break;
                case "REGOK":
                    int wn;
                    if (f.Length > 4 && int.TryParse(f[4], out wn)) Watchers = wn;     // 观战人数（旧服务器没有这段）
                    SecondOn = f.Length > 5 && f[5] == "1";                             // 有没有第二位
                    WatchDz = f.Length > 6 && f[6] == "1";                              // 观战者都会拼省流包（旧服务器没有 = 发完整包）
                    if (f.Length > 2 && roomId != f[2])
                    {
                        roomId = f[2]; log(L.T("房间已登记到大厅：", "Room listed on the lobby server: ") + roomId);
                        NetLog.W("房间已登记：房间号 " + roomId + "（开始后 " + Ms(born, now) + "）");
                    }
                    error = "";
                    break;
                case "PEER":
                    if (f.Length < 6) break;
                    OnPeer(f[3], f[4], f[5], now, f.Length > 7 ? f[7] : "");     // 第 8 段 = 对方另一族的地址
                    {
                        ulong bid;     // 第 7 段 = 对方的桥接 ID（直连失败走桥接的房）；服务器 30 秒内只给一次，重试时没有就留着上次的
                        if (!(f.Length > 6 && ulong.TryParse(f[6].Trim(), out bid))) bid = 0;
                        if (host) fbPeer = bid;     // 主机：服务器每次都带客机的（没带 = 这个客机不走桥接），换人时跟着换
                        else if (bid != 0) fbPeer = bid;
                        if (host && bid != 0 && MpFbHost) mesh.AllowJoinBridge(bid);     // 多人房：这位加入者直连不通时可经桥接进来
                    }
                    break;
                case "ALTOK":     // 服务器收到了另一族的报到
                    if (!altOkLogged) { altOkLogged = true; NetLog.W("大厅服务器也记下了本机的 " + (f.Length > 3 && f[3].StartsWith("[") ? "IPv6" : "IPv4") + " 地址（介绍给对方时两种都给）"); }
                    break;
                case "ERR":
                    var why = f.Length > 3 ? f[3] : "";
                    if (why != lastErrWhy)
                    {
                        lastErrWhy = why;
                        NetLog.W("大厅服务器报错 " + why + "：" + (why == "norelay" ? "服务器没开中转" : why == "hostnorelay" ? "主机关了中转"
                                 : why == "nofound" ? "没有这个房间（" + roomId + "）" : why == "full" ? "房间已满" : why == "inmatch" ? "对局进行中" : "（见面板提示）")
                                 + (relay && !relayOk ? "（请求中转后 " + Ms(relayReqAt, now) + "）" : ""));
                    }
                    if (why == "norelay" || why == "hostnorelay")
                    {
                        bool byHost = why == "hostnorelay";
                        if (!noRelay) log(byHost ? L.T("主机关了中转，接着试直连", "The host does not allow relay; retrying direct")
                                                 : L.T("大厅服务器没开中转，接着试直连", "The lobby server has no relay; retrying direct"));
                        noRelay = true;
                        noRelayWhy = why;
                        error = byHost ? L.T("直连不通，主机关了中转：主机转发端口或用 Tailscale", "Direct failed, the host does not allow relay")
                                       : L.T("直连不通，服务器没开中转：主机转发端口或用 Tailscale", "Direct failed, server has no relay");
                        if (!host && relay && !relayOk)   // 中转等不来：回去重新加入、再打几轮洞（等中转时直连包是不收的）
                        {
                            NetLog.W("中转用不了（" + why + "），重新加入再试直连");
                            Restart(now);
                        }
                        break;
                    }
                    if (why == "hostalive")
                    {
                        if (host && isMpHost && !rehostConfirmed && !string.IsNullOrEmpty(rehostOldSid) && (now - rehostStarted).TotalSeconds < 8.0)
                        {
                            rehostDue = now.AddSeconds(1.5);
                            rehostSent = rehostDue;
                            NetLog.W("大厅反馈原房主心跳仍在（hostalive），保留当前房间号 " + roomId + " 稍后重试 REHOST");
                        }
                        break;
                    }
                    error = why == "nofound" ? L.T("大厅里没有这个房间（" + roomId + "）", "No such room on the lobby server (" + roomId + ")")
                          : why == "full" ? L.T("房间已满", "The room is full")
                          : why == "inmatch" ? L.T("对局进行中，这局打完再进", "Match in progress, join after it ends")
                          // 排位房间
                          : why == "noranked" ? L.T("这台大厅服务器没开" + GameInfo.RankedLongZh, "This lobby server has no ranked mode")
                          : why == "unknown" ? L.T("排位账号还没登记上，稍等几秒", "Ranked account not registered yet, wait a few seconds")
                          : why == "ranked" ? L.T("这是" + GameInfo.RankedLongZh + "房间：用\"" + GameInfo.RankedZh + "\"方式加入", "This is a ranked room: join it from Ranked")
                          : why == "banned" ? L.T("你被这台大厅服务器禁止参加" + GameInfo.RankedZh, "You are banned from ranked play on this lobby server")
                          : L.T("大厅服务器报错：", "Lobby server error: ") + why;
                    if (why == "inmatch" && DeniedMsg == null) { DeniedMsg = error; log(error); }
                    break;
                case "RELAYOK":
                    if (f.Length > 2 && f[2] == sid && !relayOk)
                    {
                        relayOk = true; log(L.T("服务器中转已就绪", "Server relay ready"));
                        NetLog.W("服务器同意中转（RELAYOK，请求后 " + Ms(relayReqAt, now) + "）");
                    }
                    break;
                case "RELAYON":
                    // 刚切过来时还没收到中转的包：从现在起算"多久没动静"，不然主机会马上当成对方走了
                    if (f.Length > 2 && f[2] == sid && !relay && AllowRelay)
                    {
                        relay = true; peer = null; rx.Reset(); lastFromPeer = now; relayAt = now; relayRx = 0;
                        log(L.T("对方改走服务器中转", "Peer switched to server relay"));
                        NetLog.W("服务器通知对方改走中转（RELAYON）");
                    }
                    else if (f.Length > 2 && f[2] == sid && !relay && relayIgnored++ == 0) NetLog.W("服务器通知对方改走中转，但本机设置里没允许中转，不理");
                    break;

                // ---- M5 服务器自己开的房 ----
                case "SHOST":
                    if (f.Length < 5) break;
                    serverRoom = true;
                    serverPid = int.TryParse(f[3], out wn) ? wn : 0;
                    serverPlayers = 0;
                    if (f.Length > 4) int.TryParse(f[4], out serverPlayers);
                    serverRoomMp = f.Length > 5 && f[5] == "mp";
                    if (f.Length > 6) serverToken = f[6];       // 会话口令（旧服务器没这段 = 空）
                    joined = true; attemptAt = now; error = "";
                    if (mesh == null) EnableMpMesh(false); else mesh.MyPid = serverPid;
                    NetLog.W("服务器房：我是编号 " + serverPid + "（" + serverPlayers + " 人，" + (serverRoomMp ? "多人" : "1v1") + "）");
                    break;
                case "SMHOST":
                    if (f.Length > 2)
                    {
                        int mh3;
                        if (int.TryParse(f[2], out mh3))
                        {
                            if (mh3 != serverMatchHost) NetLog.W("服务器房：对局主机 → " + mh3);
                            serverMatchHost = mh3;
                        }
                    }
                    break;
                case "SMESH":
                    if (f.Length < 7 || mesh == null) break;
                    {
                        int pid3;
                        if (!int.TryParse(f[2], out pid3)) break;
                        var sb3 = new StringBuilder();
                        sb3.Append(WireTag.P + "MPMESH\npeer=").Append(pid3).Append('\n');
                        if (!string.IsNullOrEmpty(f[3])) sb3.Append("addr=").Append(f[3]).Append('\n');
                        if (!string.IsNullOrEmpty(f[4]) && f[4] != f[3]) sb3.Append("lan=").Append(f[4]).Append('\n');
                        sb3.Append("token=").Append(f[5]).Append('\n');
                        int g3;
                        mesh.HandlePacket(null, sb3.ToString(), out g3);
                    }
                    break;
                case "SROLE":
                    if (f.Length < 4) break;
                    {
                        bool mh3 = f[3] == "1";
                        var cb3 = OnRoleEx;
                        if (cb3 != null) cb3(f[2], mh3);
                        var cb3b = OnRole;
                        if (cb3b != null) cb3b(f[2]);
                        NetLog.W("服务器房：角色 " + f[2] + (mh3 ? "（对局主机）" : ""));
                    }
                    break;
                case "SRULES":
                    if (f.Length > 2 && wire != null) wire.Control(WireTag.P + "RULES\n" + f[2] + "\n");
                    break;
                case "SROSTER":
                    if (f.Length > 3)
                    {
                        int n3;
                        if (int.TryParse(f[2], out n3)) serverPlayers = n3;
                        serverRoster = f[3];
                        if (mesh != null) mesh.PruneRoster(serverRoster);
                    }
                    break;
                case "SLEFT":
                    if (f.Length > 2 && mesh != null)
                    {
                        int pid3;
                        if (int.TryParse(f[2], out pid3)) mesh.Remove(pid3);
                    }
                    break;
                case "SCHAT":
                    if (f.Length > 4) srvEvent("chatn " + f[3] + "|" + string.Join("|", f, 4, f.Length - 4));
                    break;
                case "SMPM":
                    if (f.Length > 2)
                    {
                        serverMpm = f[2];
                        if (!NoOut) outq.Push("src=1\n" + serverMpm + "\n");      // 游戏里当"服务器（编号 1）"的对局状态
                    }
                    break;
                case "SSCORE":
                    if (f.Length > 2) serverScores = f[2];
                    break;
                case "SPICKED":
                    if (f.Length > 4) srvEvent("picked " + f[2] + " " + f[3] + " " + f[4]);
                    break;
                case "SPICKERR":
                    if (f.Length > 3) srvEvent("pickerr " + f[2] + " " + f[3]);
                    break;
                case "SRELAYOK":
                    if (f.Length > 2) NetLog.W("服务器房：服务器同意中转 → " + f[2]);
                    break;
            }
            return 0;
        }

        void OnPeer(string newSid, string pub, string local, DateTime now, string alt)
        {
            var altEp = NetBind.CanSend(udp, LobbyServer.ParseEp(alt)) ? LobbyServer.ParseEp(alt) : null;     // 对方另一族的地址（本机发不了那一族就不要）
            if (altEp != null && alt != peerAlt) NetLog.W("服务器也给了对方的 " + ServerPick.Fam(altEp) + " 地址 " + NetLog.Ip(altEp) + "（会话 " + newSid + "），两种都打洞");
            peerAlt = alt ?? "";
            if (host)
            {
                if (isMpHost)
                {
                    var a0 = LobbyServer.ParseEp(pub);
                    var b0 = LobbyServer.ParseEp(local);
                    if (!MpAlwaysBridge) mpPunches[newSid] = new MpPunch { Sid = newSid, Pub = a0, Local = b0, Alt = altEp, Until = now.AddSeconds(Tries * TryWindow + 2) };
                    Log(L.T("多人：有人要加入（", "Multi: player joining (") + pub + L.T("），开始打洞", "), punching"));
                    NetLog.W("多人：有人要加入 会话 " + newSid + "，对方公网 " + NetLog.Ip(pub) + "，内网 " + NetLog.Ip(local));
                    return;
                }
                // 正连着一个人（3 秒内有包）时不换人；服务器那边也会因为"已满"拒掉新的加入
                if (newSid != sid && (now - lastFromPeer).TotalSeconds < 3)
                {
                    if (busySid != newSid) { busySid = newSid; NetLog.W("正连着人，忽略新的加入（会话 " + newSid + "）"); }
                    return;
                }
                if (newSid != sid)
                {
                    if (buUp || buAsked) CloseFb();
                    BuReset();
                    sid = newSid; peer = null; relay = false; rx.Reset();
                    log(L.T("有人要加入（", "A player is joining (") + pub + L.T("），开始打洞", "), punching"));
                    NetLog.W("有人要加入：会话 " + newSid + "，对方公网 " + NetLog.Ip(pub) + "，内网 " + NetLog.Ip(local) + "，开始打洞");
                    peerPub = pub; peerLocal = local; unknownPkts = noAckPkts = relayIgnored = relayBadSid = 0; relayRx = 0;
                }
                else if (pub != peerPub || local != peerLocal)
                {
                    NetLog.W("客机重试，地址变了：公网 " + NetLog.Ip(pub) + "，内网 " + NetLog.Ip(local) + "（NAT 每次换端口 = 对称型 NAT，打洞很难通）");
                    peerPub = pub; peerLocal = local;
                }
                else NetLog.W("客机重试（同一会话），再打一轮洞");
                punchEndLogged = false;
                punchUntil = now.AddSeconds(Tries * TryWindow + 2);
            }
            else
            {
                if (sid != newSid)
                {
                    if (buUp || buAsked) CloseFb();
                    BuReset();
                }
                if (sid == null) log(L.T("服务器介绍主机地址 ", "Host address from the lobby server: ") + pub + (local.Length > 0 ? " / " + local : ""));
                if (sid != newSid || pub != peerPub || local != peerLocal)
                    NetLog.W("服务器给主机地址：公网 " + NetLog.Ip(pub) + "，内网 " + NetLog.Ip(local) + "（会话 " + newSid + "，JOIN 后 " + Ms(joinAt, now) + "），开始第 " + attempt + " 次打洞");
                peerPub = pub; peerLocal = local;
                sid = newSid;
                joined = true;
                if (pick.Other != null) Raw(WireTag.S + "|ALT|c|" + newSid, pick.Other);     // 从另一族也报一次，服务器转告主机（旧服务器不认，丢掉）
                if (!noRelay && !blocked) error = "";      // 没中转 / 连不上的提示留着，直到直连真的通了
            }
            cands.Clear();
            var a = LobbyServer.ParseEp(pub);
            var b = LobbyServer.ParseEp(local);
            if (a != null) cands.Add(a);
            if (b != null && !b.Equals(a)) cands.Add(b);
            if (altEp != null && !cands.Contains(altEp)) cands.Add(altEp);     // 第三个候选：另一族
        }

        void Punch(DateTime now, string what)
        {
            if ((now - lastPunch).TotalSeconds < 0.2 || sid == null) return;
            lastPunch = now;
            foreach (var c in cands) Raw(what, c);
        }

        void HostTick(DateTime now)
        {
            if (isMpHost) MpPlayers();
            bool skipReg = false;
            if (!rehostConfirmed && !string.IsNullOrEmpty(rehostOldSid))
            {
                // 等待 / 申请中不发登记：新密钥服务器还不认识，这时登记会另开一个房间
                if (now < rehostDue) skipReg = true;
                else if ((now - rehostSent).TotalSeconds >= 2.0)
                {
                    NetLog.W("换房主未收到服务器确认（可能旧版服务器），改成重新登记房间");
                    rehostConfirmed = true;
                    roomId = "";
                    lastReg = DateTime.MinValue;
                }
                else
                {
                    if ((now - rehostLastTx).TotalSeconds >= 0.5) { rehostLastTx = now; SendRehost(); }
                    skipReg = true;
                }
            }
            if (!skipReg && (now - lastReg).TotalSeconds > 2)
            {
                lastReg = now;
                int listedCur = MpMesh.Listed(mesh, MpCount + 1);     // 对局外 AI 不算人数（真人来了会挤掉 AI）
                bool full = isMpHost ? (listedCur >= MpMax) : (now - lastFromPeer).TotalSeconds < 3;
                if (!regLogged) { regLogged = true; NetLog.W("开始登记房间：大厅服务器 " + ServerDiag + "，本机内网 " + NetLog.Ip(LobbyServer.LocalAddr(LocalPort)) + "，允许中转 " + (AllowRelay ? "是" : "否") + "（每 2 秒一次）"); }
                bool mpFb = MpFbHost || (isMpHost && MpAlwaysBridge && mesh != null && FallbackMyId != 0);     // 桥暂不可用也保留强制策略，不能谎报成直连房
                string mpExtra = isMpHost ? ("|" + (mpFb ? (MpAlwaysBridge ? "1" : "2") : "0") + "|" + MpMax + "|" + listedCur + "|" + LobbyServer.Clean(MpTag + MpMesh.ListedTag(mesh) + (mpFb && mesh.MyPid > 1 ? ";hb=" + mesh.MyPid : ""))) : "";
                Raw(WireTag.S + "|REG|" + key + "|" + LobbyServer.Clean(name) + "|" + LobbyServer.Clean(MapOf()) + "|" + (full ? "1" : "0")
                    + "|" + LobbyServer.LocalAddr(LocalPort) + "|" + (AllowRelay ? "1" : "0")
                    + (RankPid != null ? "|1|" + RankPid + "|" : "|0||") + ((fb != null && !isMpHost) || mpFb ? FallbackMyId.ToString() : "") + "|" + (SpecCfg.Max > 0 ? "1" : "0") + "|" + SpecCfg.Max + "|" + (AiRoom ? "1" : "0")
                    + "|" + Ident.MyRoomHash + (isMpHost ? mpExtra : fb != null ? "|2" : ""), server);
                if (pick.Answered && pick.Other != null && !string.IsNullOrEmpty(roomId)) Raw(WireTag.S + "|ALT|h|" + key, pick.Other);     // 另一族也报到（客机那一族和我不一样时服务器介绍这个）     // 第 11 段 = 主机桥接 ID、第 16 段 = 2（直连失败时走桥接）；桥接监听没开就不带； 多人房第 16 段固定 0（一律直连）、17–19 段 = 上限 / 人数 / 标签     // 第 12 段 = 认观战（排位房间服务器不给看），第 13 段 = 人数上限； 第 15 段 = 主机标记
                if (lastFromServer == DateTime.MinValue ? (now - attemptAt).TotalSeconds > 5 : ServerQuiet)
                    error = L.T("大厅服务器没回应：", "The lobby server is not answering: ") + LobbyServer.Show(ServerName, server);
            }
            if (isMpHost)
            {
                if ((now - lastPunch).TotalSeconds >= 0.2)
                {
                    lastPunch = now;
                    var expired = new List<string>();
                    foreach (var kv in mpPunches)
                    {
                        if (now < kv.Value.Until)
                        {
                            var b = Kv.Utf8.GetBytes(WireTag.P + "PUNCH\nsid=" + kv.Value.Sid + "\n");
                            if (kv.Value.Pub != null) Raw(b, kv.Value.Pub);
                            if (kv.Value.Local != null && !kv.Value.Local.Equals(kv.Value.Pub)) Raw(b, kv.Value.Local);
                            if (kv.Value.Alt != null) Raw(b, kv.Value.Alt);
                        }
                        else expired.Add(kv.Key);
                    }
                    for (int i = 0; i < expired.Count; i++) mpPunches.Remove(expired[i]);
                }
                return;
            }
            // 收到客机的包之后也接着打：带 ack=1 告诉客机"你的包我收到了"（客机靠它确认双向通）
            if (!relay && now < punchUntil) Punch(now, WireTag.P + "PUNCH\nsid=" + sid + "\n" + (peer != null ? "ack=1\n" : ""));
            if (sid != null && peer == null && !relay && now >= punchUntil && !punchEndLogged)
            {
                punchEndLogged = true;
                NetLog.W("打洞时间到了，还没收到客机的包（不在候选里的包 " + unknownPkts + " 个）：等客机重试或改走中转");
            }
            // 对方走了：清掉，等下一个人
            if ((peer != null || relay || bridged) && (now - lastFromPeer).TotalSeconds > LostAfter)
            {
                log(L.T("对方 " + LostAfter + " 秒没动静，房间重新开放", "No packets for " + LostAfter + " s, room open again"));
                NetLog.W("对方 " + LostAfter + " 秒没动静，房间重新开放（之前" + (bridged ? "经桥接" : relay ? "经中转，中转收包 " + relayRx + " 个" : "直连 " + NetLog.Ip(peer))
                         + "，这个会话收状态包 " + rx.Rx + " 个）");
                peer = null; relay = false; relayOk = false; sid = null; cands.Clear();
                if (bridged || buUp || buAsked) CloseFb();     // 关掉这条桥接连接，下一轮 EnsureHostFb 重开监听
                BuReset();
            }
        }

        void ClientTick(DateTime now)
        {
            if (serverRoom)
            {
                // 服务器房：每 1 秒重发一次 JOIN（掉线 / 服务器重启后自动回来），不再走原来的打洞 / 中转状态机
                if ((now - lastSrvJoin).TotalSeconds > 1.0)
                {
                    lastSrvJoin = now;
                    sendSrv(WireTag.S + "|JOIN|" + roomId + "|" + LobbyServer.LocalAddr(LocalPort)
                        + (serverToken.Length > 0 ? "||" + serverToken : ""));     // 第 6 段 = 会话口令（换端口重新握手用）
                }
                if (mesh != null)
                {
                    // 直连 5 秒还没通：请服务器替这一对中转（服务器开 --relay 才同意）
                    var pids = mesh.Pids();
                    for (int i = 0; i < pids.Count; i++)
                    {
                        var p = mesh.Get(pids[i]);
                        if (p == null || p.DirectVerified) continue;
                        if ((now - p.BornAt).TotalSeconds < 5.0 || srvRelayAsk.Contains(p.Pid)) continue;
                        srvRelayAsk.Add(p.Pid);
                        sendSrv(WireTag.S + "|SRELAY|" + p.Pid + "|" + serverToken);
                    }
                }
                return;
            }
            if (isMpJoin)
            {
                if (mpFbH != 0)
                {
                    MpFbTick(now);
                    return;
                }
                if (!joined && (now - lastJoin).TotalSeconds > 0.5)
                {
                    lastJoin = now;
                    if (!joinLogged) { joinLogged = true; joinAt = now; NetLog.W("发多人加入请求（JOIN）：房间 " + roomId + "，本机内网 " + NetLog.Ip(LobbyServer.LocalAddr(LocalPort)) + "，第 " + attempt + " 次"); }
                    string brMp = FallbackRoom && FallbackMyId != 0 ? FallbackMyId.ToString() : "";     // 多人房直连失败走桥接：第 6 段 = 自己的桥接 ID
                    Raw(WireTag.S + "|JOIN|" + roomId + "|" + LobbyServer.LocalAddr(LocalPort) + (brMp != "" ? "||" + brMp : ""), server);
                }
                if (MpAlwaysBridge)
                {
                    // PEER 已给出会话号和桥接 ID；不等三轮 UDP，也绝不回落直连。
                    // 桥接当场拒绝（句柄 0：桥没就绪、对方 ID 不对、同一账号连自己…）时隔 MpFbRetrySec 秒再试：
                    // 以前每轮 Tick（约 6 ms）都重连一次，一分钟往 net_conn.log / bridge_conn.log 各写四千多行
                    if (sid != null && (now - mpFbTryAt).TotalSeconds >= MpFbRetrySec)
                    {
                        mpFbTryAt = now;
                        if (!TryMpBridgeFallback(now))
                        {
                            joined = false;
                            if (error.Length > 0) noRelay = true;     // 提示留到下次重试（重发 JOIN 拿到的 PEER 不清），进了房才收
                        }
                    }
                    return;
                }
                Punch(now, WireTag.P + "HELLO\nsid=" + sid + "\nmp=1\nlan=" + LobbyServer.LocalAddr(LocalPort) + "\n" + (MpMesh.HideIpPref ? "hideip=1\n" : "") + (MpCfg.JoinWatch ? "watch=1\n" : "") + MpMesh.V6HelloLine(LocalPort));     // watch=1 观战加入     //
                if ((now - attemptAt).TotalSeconds < TryWindow) return;
                if (sid == null)
                {
                    if (error.Length == 0 || ServerQuiet) error = L.T("大厅服务器没回应：", "The lobby server is not answering: ") + LobbyServer.Show(ServerName, server);
                    attemptAt = now;
                    return;
                }
                if (myPid <= 0 && attempt < Tries)
                {
                    attempt++;
                    attemptAt = now;
                    joined = false;
                    joinLogged = false;
                    log(L.T("多人直连没通，第 " + attempt + " 次重试", "No direct link yet, try " + attempt));
                    NetLog.W("第 " + (attempt - 1) + " 次直连 " + TryWindow + " 秒没通");
                    return;
                }
                if (myPid <= 0)
                {
                    if (TryMpBridgeFallback(now)) return;     // 房主允许直连失败时走桥接
                    if (!FallbackRoom) error = L.T("直连不通（对局不经服务器中转）：房主可在房间设置里把\"对局走桥接\"设成直连失败时走桥接，或转发端口 / 用局域网", "Direct failed (no match relay): the host can set \"play over bridge\" in room settings, or forward the host's port / use LAN");
                    if (error.Length > 0) noRelay = true;     // 提示留着（下一轮 PEER 不清），直到真的进了房
                    log(L.T("多人直连 " + Tries + " 次都没通，重新加入再试", "Direct failed " + Tries + " times, joining again"));
                    Restart(now);
                    return;
                }
                return;
            }
            if (bridged)     // 已改走桥接：没动静就从头再来（重新加入、再试直连）
            {
                var f0 = fb;
                if ((now - lastFromPeer).TotalSeconds > LostAfter + (f0 != null && f0.Linked ? 0 : 6))
                {
                    log(L.T("桥接断了，重新加入", "Bridge lost, joining again"));
                    NetLog.W("客机：桥接 " + LostAfter + " 秒没动静（连上过 " + (f0 != null && f0.Linked ? "是" : "否") + "），重新加入");
                    Restart(now);
                }
                return;
            }
            if (relay)
            {
                if (!relayOk && !noRelay && (now - lastRelayReq).TotalSeconds > 0.5) { lastRelayReq = now; Raw(WireTag.S + "|RELAY|" + sid, server); }
                if ((now - lastFromPeer).TotalSeconds > LostAfter + (relayOk ? 0 : 4))
                {
                    log(L.T("中转也断了，重新加入", "Relay lost, joining again"));
                    NetLog.W((relayRx == 0 ? "中转一直没收到对方的包" : "中转断了") + "，重新加入（服务器同意中转 " + (relayOk ? "是" : "否")
                             + "，中转收包 " + relayRx + " 个，" + (relayRx == 0 ? "切到中转后 " + Ms(relayAt, now) : "最后收包在 " + Ms(lastFromPeer, now) + " 前") + "）");
                    if (relayRx == 0) { blocked = true; error = BlockedHint; }
                    Restart(now);
                }
                return;
            }
            if (peer != null)
            {
                if ((now - lastFromPeer).TotalSeconds > LostAfter)
                {
                    log(L.T("直连断了，重新加入", "Direct link lost, joining again"));
                    NetLog.W("直连断了：" + LostAfter + " 秒没收到对方的包，重新加入");
                    Restart(now);
                }
                return;
            }
            // 打洞中：没拿到会话前每 0.5 秒发 JOIN；拿到后每 0.2 秒往主机的公网 / 局域网地址发握手
            if (!joined && (now - lastJoin).TotalSeconds > 0.5)
            {
                lastJoin = now;
                if (!joinLogged) { joinLogged = true; joinAt = now; NetLog.W("发加入请求（JOIN）：房间 " + roomId + "，本机内网 " + NetLog.Ip(LobbyServer.LocalAddr(LocalPort)) + "，第 " + attempt + " 次"); }
                string brF = FallbackRoom && FallbackMyId != 0 ? FallbackMyId.ToString() : "";     // 第 6 段 = 自己的桥接 ID（房间允许 + 同意过才带）
                Raw(WireTag.S + "|JOIN|" + roomId + "|" + LobbyServer.LocalAddr(LocalPort)
                    + (RankPid != null || brF != "" || JoinToken != null ? "|" + (RankPid ?? "") : "")
                    + (brF != "" || JoinToken != null ? "|" + brF : "")
                    + (JoinToken != null ? "|" + JoinToken : ""), server);     // 第 7 段 = 上场口令     // 第 7 段 = 上场口令
            }
            Punch(now, WireTag.P + "HELLO\nsid=" + sid + "\n");
            if ((now - attemptAt).TotalSeconds < TryWindow) return;
            if (sid == null)
            {
                // 连服务器都没回：没法中转，一直重试 JOIN
                if (error.Length == 0 || ServerQuiet) error = L.T("大厅服务器没回应：", "The lobby server is not answering: ") + LobbyServer.Show(ServerName, server);
                attemptAt = now;
                return;
            }
            if (attempt < Tries)
            {
                attempt++;
                attemptAt = now;
                joined = false;                // 重新 JOIN 一次：服务器重发 PEER 给主机，主机再打一轮洞
                joinLogged = false;
                log(L.T("直连没通，第 " + attempt + " 次重试", "No direct link yet, try " + attempt));
                NetLog.W("第 " + (attempt - 1) + " 次直连 " + TryWindow + " 秒没通（不在候选里的包 " + unknownPkts + " 个，没带 ack 的打洞包 " + noAckPkts + " 个）");
                unknownPkts = noAckPkts = 0;
                return;
            }
            if (TryBridgeFallback(now)) return;     // 主机允许直连失败时走桥接
            if (!AllowRelay && !noRelay)
            {
                // 0.34（最终方案 3）：对局不再经服务器中转。直连不通 = 告诉玩家另两条路：房间设置"对局走 " + Bridge.Svc（需桥接平台）、主机转发端口 / 局域网
                noRelay = true;
                noRelayWhy = "0.34 对局不中转";
                if (!FallbackRoom) error = L.T("直连不通（对局不经服务器中转）：主机可在房间设置里把\"对局走桥接\"设成直连失败时走桥接，或转发端口 / 用局域网", "Direct failed (no match relay): the host can set \"play over bridge\" in room settings, or forward the host's port / use LAN");
                log(L.T("直连 " + Tries + " 次都没通，对局不中转，重新加入再试", "Direct failed " + Tries + " times, no match relay; joining again"));
                NetLog.W("直连 " + Tries + " 次都没通，0.34 对局不中转，重新加入再试");
                Restart(now);
                return;
            }
            if (noRelay)
            {
                // 已经知道服务器没开中转：不再进中转，从头再来一轮直连（对方开了端口转发 / UPnP 之后就能连上）
                log(L.T("直连 " + Tries + " 次都没通，服务器没开中转，重新加入再试", "Direct failed " + Tries + " times, no relay on the server; joining again"));
                NetLog.W("直连 " + Tries + " 次都没通，中转用不了（" + noRelayWhy + "），重新加入再试");
                Restart(now);
                return;
            }
            relay = true;
            relayOk = false;
            lastFromPeer = now;
            rx.Reset();
            relayAt = relayReqAt = now; relayRx = 0;
            log(L.T("直连 " + Tries + " 次都没通，改走服务器中转", "Direct failed " + Tries + " times, using server relay"));
            NetLog.W("直连 " + Tries + " 次都没通（最后一次：不在候选里的包 " + unknownPkts + " 个，没带 ack 的打洞包 " + noAckPkts + " 个），请求服务器中转（RELAY）");
        }

        // ---- 直连失败时走桥接
        void HookFb(BridgeLink f0)
        {
            f0.Log = log;
            f0.Wire = wire;
        }
        void CloseFb()
        {
            var f0 = fb;
            fb = null;
            bridged = false;
            buUp = false;
            buAsked = false;
            if (f0 != null) { try { f0.Close(); } catch (Exception) { } }
        }
        // 主机：允许直连失败走桥接时一直开着桥接监听（只收服务器介绍的那个客机）
        void EnsureHostFb()
        {
            if (!host || isMpHost || FallbackMyId == 0 || fb != null || fbFailed || serverRoom || (peer != null && buGaveUp)) return;     // 多人房的桥接归 MpMesh
            try
            {
                var f0 = new BridgeLink(0, outq);
                f0.AllowPeer = id => id != 0 && id == fbPeer;
                HookFb(f0);
                fb = f0;
                Bridge.SetHosting(false);     // 同 BridgeRoomLink：服务器大厅的房不出现在好友列表的"在开房"里
                NetLog.W("主机：直连失败时走桥接，桥接监听已开（桥接 ID 只经大厅服务器介绍给加入的客机）");
            }
            catch (Exception ex) { fbFailed = true; NetLog.W("主机：桥接监听开不了，这间房只走直连：" + ex.Message); }
        }
        int PumpFallback(DateTime now)
        {
            if (host) EnsureHostFb();
            var f0 = fb;
            if (f0 == null || relay || (peer != null && !bridged)) return 0;     // 直连先通了：桥接那边不管
            int g = 0;
            try { g = f0.Pump(); } catch (Exception ex) { NetLog.W("桥接收包出错：" + ex.Message); }
            if (f0.Linked)
            {
                if (!bridged && host)
                {
                    bridged = true;
                    lastFromPeer = now;
                    log(L.T("对方直连不通，经桥接连进来了", "The player could not connect directly and came in over the bridge"));
                    NetLog.W("主机：客机经桥接连进来（会话 " + sid + "）");
                }
                if (g > 0) lastFromPeer = now;
            }
            return g;
        }
        // 多人房客机：直连 Tries 次都不通 → 房主允许、自己也同意过桥接时改经桥接连房主（连接归 MpMesh，包回到 MpJoinText）。
        // 经桥接进来的人按"隐藏 IP"成员算（和别的成员之间都经房主转发，不直连、不报地址）
        bool TryMpBridgeFallback(DateTime now)
        {
            if (host || !isMpJoin || !FallbackRoom || mesh == null) return false;
            if (FallbackMyId == 0)
            {
                error = L.T("直连不通。房主允许直连失败时走桥接，但你这边没启用桥接扩展包或没同意桥接（系统设置 → 已加载的扩展）",
                            "Direct failed. The host allows the bridge, but you have not enabled the bridge extension or agreed to it (System settings -> Extensions)");
                return false;
            }
            if (fbPeer == 0)
            {
                error = L.T("直连不通，大厅服务器没给房主的桥接 ID（服务器是旧版，或房主的桥接没开）", "Direct failed; the lobby server gave no bridge ID for the host (old server, or the host's bridge is off)");
                return false;
            }
            if (!Bridge.Init()) { error = L.T("直连不通，桥接也用不了：", "Direct failed and the bridge is unavailable: ") + Bridge.Error; return false; }
            uint h = mesh.ConnectJoinBridge(fbPeer, FallbackHostPid);
            if (h == 0) { error = L.T("直连不通，桥接也没连上", "Direct failed and the bridge did not connect"); NetLog.W("客机：桥接连接没建起来"); return false; }
            mpFbH = h;
            mpFbAt = now;
            mpFbHelloAt = DateTime.MinValue;
            mesh.HideMe = true;
            error = "";
            log(MpAlwaysBridge ? L.T("新版房一直桥接，直接经桥接连房主", "Always-bridge room: joining the host over the bridge")
                : L.T("多人直连 " + Tries + " 次都没通，改经桥接连房主", "Direct failed " + Tries + " times, joining the host over the bridge"));
            NetLog.W("客机：" + (MpAlwaysBridge ? "新版房一直桥接，直接经桥接连房主" : "多人直连 " + Tries + " 次都没通，改经桥接连房主") + "（房主编号 " + FallbackHostPid + "）");
            return true;
        }
        void MpFbTick(DateTime now)
        {
            if (mesh == null) { CloseMpFb(); return; }
            if (myPid <= 0)
            {
                if (mesh.JoinBridgeLost || (now - mpFbAt).TotalSeconds > 20)
                {
                    log(L.T("经桥接也没进房，重新加入再试", "Could not join over the bridge either; joining again"));
                    NetLog.W("客机：经桥接 " + (mesh.JoinBridgeLost ? "连接断了" : "20 秒没拿到编号") + "（连上过 " + (mesh.JoinBridgeUp ? "是" : "否") + "），重新加入");
                    Restart(now);
                    return;
                }
                if ((now - mpFbHelloAt).TotalSeconds >= 0.5)
                {
                    mpFbHelloAt = now;
                    var hello = Kv.Utf8.GetBytes(WireTag.P + "HELLO\nsid=" + sid + "\nmp=1\nhideip=1\n" + (MpCfg.JoinWatch ? "watch=1\n" : ""));
                    RawTo(hello, mpFbH);
                }
                return;
            }
            if (mesh.JoinBridgeLost && !mpFbLostLogged)
            {
                mpFbLostLogged = true;     // 之后照普通掉线处理（会话那边按收包超时判）；mpFbH 留着，免得又往房主打洞、被当成新人再分一个编号
                NetLog.W("客机：连房主的桥接断了（已在房里，编号 " + myPid + "）");
                if (MpAlwaysBridge) error = L.T("房主桥接已断开：请重新加入；房主离开后需要重开房间", "Host bridge disconnected: rejoin; if the host left, create a new room");
            }
        }
        void CloseMpFb()
        {
            if (mpFbH == 0) return;
            mpFbH = 0;
            mpFbLostLogged = false;
            if (mesh != null) { try { mesh.CloseJoinBridge(); } catch (Exception) { } mesh.HideMe = MpAlwaysBridge || MpMesh.HideIpPref; }
        }

        // 客机：直连 Tries 次都不通 → 主机允许、自己也同意过桥接时改经桥接连。返回 true = 已切过去
        bool TryBridgeFallback(DateTime now)
        {
            if (host || !FallbackRoom) return false;
            if (FallbackMyId == 0)
            {
                error = L.T("直连不通。主机允许直连失败时走桥接，但你这边没启用桥接扩展包或没同意桥接（系统设置 → 已加载的扩展）",
                            "Direct failed. The host allows the bridge, but you have not enabled the bridge extension or agreed to it (System settings -> Extensions)");
                return false;
            }
            if (fbPeer == 0)
            {
                error = L.T("直连不通，大厅服务器没给主机的桥接 ID（服务器是旧版，或主机的桥接没开）", "Direct failed; the lobby server gave no bridge ID for the host (old server, or the host's bridge is off)");
                return false;
            }
            if (!Bridge.Init()) { error = L.T("直连不通，桥接也用不了：", "Direct failed and the bridge is unavailable: ") + Bridge.Error; return false; }
            try
            {
                var f0 = new BridgeLink(fbPeer, outq);
                HookFb(f0);
                fb = f0;
                bridged = true;
                peer = null;
                rx.Reset();
                lastFromPeer = now;
                error = "";
                log(L.T("直连 " + Tries + " 次都没通，改经桥接连主机", "Direct failed " + Tries + " times, connecting to the host over the bridge"));
                NetLog.W("客机：直连 " + Tries + " 次都没通，改经桥接连主机（会话 " + sid + "）");
                return true;
            }
            catch (Exception ex)
            {
                error = L.T("直连不通，桥接也没连上：", "Direct failed and the bridge did not connect: ") + ex.Message;
                NetLog.W("客机：桥接连接失败：" + ex.Message);
                return false;
            }
        }

        void BuReset()
        {
            buAsked = false; buUp = false; buGaveUp = false; buPeerCan = false; buPeerSeen = false;
            buStartAt = buSwitchedAt = buCheckAt = DateTime.MinValue;
            buLastDirPing = buLastFbPing = buLastGo = buFbLastRx = DateTime.MinValue;
            buDirSamples.Clear(); buFbSamples.Clear();
            buDirPing = -1; buFbPing = -1;
        }
        static void BuAddSample(List<double> list, double val)
        {
            list.Add(val);
            if (list.Count > 8) list.RemoveAt(0);
        }
        static double BuMedian(List<double> list)
        {
            int n = list.Count;
            if (n == 0) return -1;
            var a = new List<double>(list);
            a.Sort();
            return (n & 1) == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) * 0.5;
        }
        static double BuSpread(List<double> list)
        {
            int n = list.Count;
            if (n < 3) return 0;
            var a = new List<double>(list);
            a.Sort();
            return a[n - 2] - a[1];
        }
        void BuHook(BridgeLink f0)
        {
            f0.NoOut = true;
            f0.NoReconnect = true;
            f0.OnRole = rv => { var cb = OnRole; if (cb != null) cb(rv); };
            f0.OnAny = s => { buFbLastRx = DateTime.UtcNow; BuCtl(s, true); };
            f0.OnState = s =>
            {
                lastFromPeer = DateTime.UtcNow;
                if (rx.Take(s))
                {
                    buGot++;
                    if (!NoOut) outq.Push(s);
                    var fw = OnPeerState;
                    if (fw != null) fw(s);
                }
            };
        }
        void BuGiveUp(DateTime now, string why, bool backToDirect)
        {
            bool was = buUp || buAsked;
            buGaveUp = true;
            if (backToDirect)
                log(L.T("桥接不稳或较慢，切回直连", "Bridge slower or unstable, switched back to direct"));
            if (was || why != null)
                NetLog.W("桥接自动切线：" + (why ?? "放弃，继续直连"));
            CloseFb();
        }
        bool BuCtl(string s, bool viaBridge)
        {
            if (!s.StartsWith(WireTag.P + "BU", StringComparison.Ordinal)) return false;
            if (s.StartsWith(WireTag.P + "BUPING", StringComparison.Ordinal))
            {
                var d = Kv.Parse(s);
                string tv, vv;
                if (!viaBridge && d.TryGetValue("v", out vv)) { buPeerSeen = true; buPeerCan = vv == "1"; }
                if (d.TryGetValue("t", out tv))
                {
                    var reply = wire.Seal(WireTag.P + "BUPONG\nt=" + tv + "\nv=" + (BuCan ? "1" : "0") + "\n");
                    if (viaBridge) { var f0 = fb; if (f0 != null) f0.SendCtl(reply); }
                    else if (peer != null) Raw(reply, peer);
                }
                return true;
            }
            if (s.StartsWith(WireTag.P + "BUPONG", StringComparison.Ordinal))
            {
                var d = Kv.Parse(s);
                string tv, vv;
                long t0;
                if (!viaBridge && d.TryGetValue("v", out vv)) { buPeerSeen = true; buPeerCan = vv == "1"; }
                if (d.TryGetValue("t", out tv) && long.TryParse(tv, out t0))
                {
                    long raw = BuNowMs() - t0;
                    if (raw >= 0 && raw < 10000)
                    {
                        if (!viaBridge) { BuAddSample(buDirSamples, raw + BuDirectAddMs); buDirPing = BuMedian(buDirSamples); }
                        else { BuAddSample(buFbSamples, raw + BuBridgeAddMs); buFbPing = BuMedian(buFbSamples); }
                    }
                }
                return true;
            }
            if (s.StartsWith(WireTag.P + "BUGO", StringComparison.Ordinal))
            {
                if (!host && !buAsked && !buUp && !buGaveUp && !bridged && peer != null && !relay && BuCan && fb == null)
                {
                    try
                    {
                        var f0 = new BridgeLink(fbPeer, outq);
                        HookFb(f0);
                        BuHook(f0);
                        fb = f0;
                        buAsked = true;
                        buStartAt = DateTime.UtcNow;
                        buFbSamples.Clear();
                        buLastFbPing = DateTime.MinValue;
                        buFbLastRx = buStartAt;
                        NetLog.W("桥接自动切线：直连偏慢，主机通知试连桥接（目标 " + fbPeer + "）");
                    }
                    catch (Exception ex) { BuGiveUp(DateTime.UtcNow, "客机开桥接失败：" + ex.Message, false); }
                }
                return true;
            }
            if (s.StartsWith(WireTag.P + "BUUSE", StringComparison.Ordinal))
            {
                if (!host && !buUp && !buGaveUp && !bridged && fb != null && fb.Linked)
                {
                    buUp = true;
                    buAsked = false;
                    buSwitchedAt = DateTime.UtcNow;
                    buFbLastRx = buSwitchedAt;
                    log(L.T("直连延迟高，已切换经桥接发包", "High direct latency; switched sending to bridge"));
                    NetLog.W("桥接自动切线：收到主机 " + WireTag.P + "BUUSE，切换经桥接发包");
                }
                return true;
            }
            if (s.StartsWith(WireTag.P + "BUOFF", StringComparison.Ordinal))
            {
                if (!host && (buUp || buAsked))
                {
                    bool wasUp = buUp;
                    BuGiveUp(DateTime.UtcNow, wasUp ? "收到主机 " + WireTag.P + "BUOFF，切回直连" : "收到主机 " + WireTag.P + "BUOFF，继续直连", wasUp);
                }
                return true;
            }
            return false;
        }
        int BuTick(DateTime now)
        {
            buGot = 0;
            if (bridged || relay || peer == null || !BuRoomOk) return 0;
            if (!buUp && buGaveUp) return 0;
            // 未切换过（且两边都还满足条件）或已经切走桥接后：直连每 0.5 秒测一次 RTT
            if ((buUp || BuCan) && (now - buLastDirPing).TotalSeconds >= 0.5)
            {
                buLastDirPing = now;
                if (buStartAt == DateTime.MinValue) buStartAt = now;
                if (host || buUp || buAsked || (now - buStartAt).TotalSeconds <= 10.0)
                    Raw(wire.Seal(WireTag.P + "BUPING\nt=" + BuNowMs() + "\nv=" + (BuCan ? "1" : "0") + "\n"), peer);
            }
            // 阶段 1：主机直连收满 8 个样本后只判一次
            if (host && !buAsked && !buUp && !buGaveUp && BuCan && buDirSamples.Count >= 8)
            {
                double dirMed = BuMedian(buDirSamples);
                if (dirMed <= BridgeUpMs)
                {
                    BuGiveUp(now, "直连中位 " + dirMed.ToString("0") + " ms <= " + BridgeUpMs + " ms，继续直连", false);
                    return 0;
                }
                if (!buPeerSeen || !buPeerCan)
                {
                    BuGiveUp(now, "直连中位 " + dirMed.ToString("0") + " ms 偏慢，但对方桥接不可用，继续直连", false);
                    return 0;
                }
                EnsureHostFb();
                if (fb == null)
                {
                    BuGiveUp(now, "直连偏慢但主机桥接监听开不了，继续直连", false);
                    return 0;
                }
                BuHook(fb);
                buAsked = true;
                buStartAt = now;
                buLastGo = DateTime.MinValue;
                buFbSamples.Clear();
                buLastFbPing = DateTime.MinValue;
                buFbLastRx = now;
                NetLog.W("桥接自动切线：直连中位 " + dirMed.ToString("0") + " ms > " + BridgeUpMs + " ms，通知客机试连桥接");
            }
            if (!buAsked && !buUp) return 0;
            var f0 = fb;
            if (host && buAsked && (f0 == null || !f0.Linked) && (now - buLastGo).TotalSeconds >= 1.0)
            {
                buLastGo = now;
                Raw(wire.Seal(WireTag.P + "BUGO\n"), peer);
            }
            if (f0 != null)
            {
                try { f0.Pump(); } catch (Exception ex) { NetLog.W("桥接自动切线：桥接收包出错：" + ex.Message); }
            }
            // 阶段 2：试连 / 测速超时（主机 15 秒 / 客机 20 秒）
            if (buAsked && !buUp)
            {
                double lim = host ? 15.0 : 20.0;
                if ((now - buStartAt).TotalSeconds > lim)
                {
                    if (host && peer != null) Raw(wire.Seal(WireTag.P + "BUOFF\n"), peer);
                    BuGiveUp(now, lim + " 秒内没连上或没完成桥接测速", false);
                    return buGot;
                }
            }
            // 阶段 2 & 3：桥接每 0.5 秒测一次 RTT
            if (f0 != null && f0.Linked && (now - buLastFbPing).TotalSeconds >= 0.5)
            {
                buLastFbPing = now;
                f0.SendCtl(wire.Seal(WireTag.P + "BUPING\nt=" + BuNowMs() + "\nv=1\n"));
            }
            // 阶段 2 决策：主机收集满 6 个桥接样本后比较中位数
            if (host && buAsked && !buUp && buFbSamples.Count >= 6 && buDirSamples.Count > 0)
            {
                double fbMed = BuMedian(buFbSamples), dirMed = BuMedian(buDirSamples);
                if (fbMed + 20.0 < dirMed)
                {
                    buUp = true;
                    buAsked = false;
                    buSwitchedAt = now;
                    buCheckAt = now;
                    buFbLastRx = now;
                    var u = wire.Seal(WireTag.P + "BUUSE\n");
                    Raw(u, peer);
                    if (f0 != null) f0.SendCtl(u);
                    log(L.T("直连延迟高，已切换经桥接发包", "High direct latency; switched sending to bridge"));
                    NetLog.W("桥接自动切线：切换经桥接发包（桥接中位 " + fbMed.ToString("0") + " ms + 20 < 直连中位 " + dirMed.ToString("0") + " ms）");
                }
                else
                {
                    Raw(wire.Seal(WireTag.P + "BUOFF\n"), peer);
                    BuGiveUp(now, "桥接不够快（桥接中位 " + fbMed.ToString("0") + " ms，直连中位 " + dirMed.ToString("0") + " ms），继续直连", false);
                }
                return buGot;
            }
            // 阶段 3 监控：断流 / 断开（超过 1 秒无包或桥断开）或切换 4 秒后主机检查质量变差
            if (buUp)
            {
                if (f0 == null || !f0.Linked || (now - buFbLastRx).TotalSeconds > 1.0)
                {
                    if (host && peer != null) Raw(wire.Seal(WireTag.P + "BUOFF\n"), peer);
                    BuGiveUp(now, (f0 == null || !f0.Linked) ? "桥接断开，切回直连" : "桥接超过 1 秒无包，切回直连", true);
                    return buGot;
                }
                if (host && (now - buSwitchedAt).TotalSeconds >= 4.0 && (now - buCheckAt).TotalSeconds >= 0.5)
                {
                    buCheckAt = now;
                    if (buFbSamples.Count >= 8 && buDirSamples.Count >= 8)
                    {
                        double fbMed = BuMedian(buFbSamples), dirMed = BuMedian(buDirSamples);
                        double fbSpread = BuSpread(buFbSamples), dirSpread = BuSpread(buDirSamples);
                        if (fbMed > dirMed + 20.0 || (fbSpread > 100.0 && fbSpread > dirSpread + 50.0))
                        {
                            if (peer != null) Raw(wire.Seal(WireTag.P + "BUOFF\n"), peer);
                            BuGiveUp(now, "桥接变差（桥接中位 " + fbMed.ToString("0") + " / 抖动 " + fbSpread.ToString("0")
                                         + " ms，直连中位 " + dirMed.ToString("0") + " / 抖动 " + dirSpread.ToString("0") + " ms），切回直连", true);
                        }
                    }
                }
            }
            return buGot;
        }

        void Restart(DateTime now)
        {
            CloseMpFb();
            CloseFb();
            BuReset();
            peer = null; relay = false; relayOk = false; joined = false; sid = null; cands.Clear();
            attempt = 1; attemptAt = now; rx.Reset();
            joinLogged = false; lastErrWhy = ""; unknownPkts = noAckPkts = relayIgnored = relayBadSid = 0; relayRx = 0;
        }

        public void Close()
        {
            if (mesh != null && mesh.MyPid > 0)     // 成员也告别（只告诉房主）
            {
                mesh.BroadcastBye();
                try { System.Threading.Thread.Sleep(30); } catch (Exception) { }
            }
            if (serverRoom) sendSrv(WireTag.S + "|SLEAVE|" + serverToken);
            else if (host) Raw(WireTag.S + "|UNREG|" + key, server);
            NetLog.W(host ? "关房间：向大厅服务器注销" : "客机离开");
            CloseMpFb();
            if (mesh != null) { try { mesh.CloseJoinBridge(); } catch (Exception) { } }
            CloseFb();
            BuReset();
            try { udp.Close(); } catch (Exception) { }
        }
    }

    // 不在联机时也要列房间：自己一个 UDP 口，每 2 秒问一次服务器（界面线程的 Tick 里调，不阻塞）
    // 服务器大厅观战：观战者只跟大厅服务器说话（4N|WATCH 保活），主机上传的两路包由服务器原样转来（4NW|房间号\n<包>）。
    // 不打洞、不连主机，所以观战者在什么网络都能看；要服务器开了中转。观战者不发状态包（Send 什么都不做）
    class WatchLink : ITransport, ISpecSink
    {
        readonly UdpClient udp;
        IPEndPoint server;                 // = pick.Cur
        ServerPick pick;
        readonly string roomId;
        readonly RemoteOut outq;
        readonly Dictionary<string, int> srcSeq = new Dictionary<string, int>();
        DateTime lastWatch = DateTime.MinValue, lastFromServer = DateTime.MinValue;
        Action<string> log = delegate { };
        Wire wire = new Wire(null);
        long tx, gap;
        public long SrcH, SrcC, Stale;
        public volatile int Watchers;
        public string Error = "";
        string nick;
        public bool Down;                       // 刚下场的：WATCH 带 down，服务器先不叫我上场
        public volatile string TurnToken;       // 服务器叫我上场（YOURTURN）的口令
        public int SpecCount { get { return 0; } }
        public void SendSpecs(byte[] b) { }
        public bool SpecDzAll { get { return false; } }     // 观战者这头不转发
        public Action<string> OnPeerState { get; set; }
        bool specMode = true;
        public bool SpecMode { get { return specMode; } set { specMode = value; } }      // false = 当第二位选手（经服务器和主机互发）
        public Action<string> OnRole { get; set; }
        public int QPos { get; private set; }
        int lastSeqU = -1;
        public int QLen { get { return Watchers; } }

        public IPEndPoint Server { get { return server; } }

        public WatchLink(IPEndPoint server, string room, string fRemote)
        {
            this.server = server;
            roomId = (room ?? "").Trim().ToUpperInvariant();
            outq = new RemoteOut(fRemote);
            udp = NetBind.QueryUdp(server);     // IPv4 / IPv6 都能发
            try { udp.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch (Exception) { }
            pick = new ServerPick(server, udp);
        }

        public string RoomId { get { return roomId; } }
        public int LocalPort { get { return ((IPEndPoint)udp.Client.LocalEndPoint).Port; } }
        public string PeerStr { get { return L.T("观战房间 ", "watching room ") + roomId; } }
        public string PeerName { get { return ""; } }
        public string PeerNick { get { return nick; } }
        public long Tx { get { return tx; } }
        public long Gap { get { return gap; } }
        public double PingMs { get { return -1; } }
        public Action<string> Log { get { return log; } set { log = value ?? delegate { }; } }
        public Wire Wire { get { return wire; } set { wire = value ?? new Wire(null); } }
        public RemoteOut Out { get { return outq; } }
        // 当第二位选手时：自己的状态包经服务器发给主机（4NV）；观战时什么都不发
        public void Send(byte[] b)
        {
            if (specMode) return;
            var head = Kv.Utf8.GetBytes(WireTag.P + "V|" + roomId + "\n");
            var pkt = new byte[head.Length + b.Length];
            Buffer.BlockCopy(head, 0, pkt, 0, head.Length);
            Buffer.BlockCopy(b, 0, pkt, head.Length, b.Length);
            tx++;
            try { udp.Send(pkt, pkt.Length, server); } catch (Exception) { }
        }
        public void Close()
        {
            try { var b = Kv.Utf8.GetBytes(WireTag.S + "|UNWATCH|" + roomId); udp.Send(b, b.Length, server); } catch (Exception) { }
            try { udp.Close(); } catch (Exception) { }
        }

        public int Pump()
        {
            var now = DateTime.UtcNow;
            if (pick.Tick(now)) { server = pick.Cur; lastWatch = DateTime.MinValue; NetLog.W("观战：大厅服务器没回应，改试 " + ServerPick.Fam(server) + " 地址"); }
            if ((now - lastWatch).TotalSeconds > 2)
            {
                lastWatch = now;
                tx++;
                // 第 4 段 down = 刚下场；第 5 段 noq = 只观战、不排队
                // 第 6 段 dz = 我会拼省流观战包（第 5 段总带上，空 = 排队；旧服务器只看到第 4 / 5 段）
                try { var b = Kv.Utf8.GetBytes(WireTag.S + "|WATCH|" + roomId + "|" + (SpecCfg.Up ? "up" : Down ? "down" : "") + "|" + (SpecCfg.NoQueue ? "noq" : "") + "|dz"); udp.Send(b, b.Length, server); } catch (Exception) { }
                if (lastFromServer != DateTime.MinValue && (now - lastFromServer).TotalSeconds > 6)
                    Error = L.T("大厅服务器没回应", "The lobby server is not answering");
            }
            int got = 0;
            while (true)
            {
                int avail;
                try { avail = udp.Available; } catch (Exception) { break; }
                if (avail <= 0) break;
                IPEndPoint from = null;
                byte[] data;
                try { data = udp.Receive(ref from); } catch (Exception) { break; }
                from = NetBind.Norm(from);
                if (!pick.Is(from)) continue;     // 两个地址都认
                if (pick.Got(from)) server = pick.Cur;
                lastFromServer = now;
                if (data.Length > 5 && data[0] == 'P' && data[1] == 'B' && data[2] == 'Z' && data[3] == 'U' && data[4] == '|')
                {
                    // 主机经服务器转给我（第二位选手）的包：对手的状态包，不带 src
                    if (specMode) continue;
                    int nlu = Array.IndexOf(data, (byte)'\n');
                    if (nlu < 0) continue;
                    var bu = wire.Verify(data, nlu + 1, data.Length - nlu - 1, x => { });
                    if (bu == null || wire.Control(bu)) continue;
                    string qs;
                    int qu;
                    if (!Kv.Parse(bu).TryGetValue("seq", out qs) || !int.TryParse(qs, out qu)) continue;
                    if (qu <= lastSeqU && qu > lastSeqU - 100) continue;
                    lastSeqU = qu;
                    got++;
                    nick = Kv.NickOf(bu) ?? nick;
                    wire.DzSeenText(bu);       // 第二位选手：主机转来的包（带主机的 dzv / dzh）
                    outq.Push(bu);
                    continue;
                }
                if (data.Length > 5 && data[0] == 'P' && data[1] == 'B' && data[2] == 'Z' && data[3] == 'W' && data[4] == '|')
                {
                    if (!specMode) continue;
                    int nl = Array.IndexOf(data, (byte)'\n');
                    if (nl < 0) continue;
                    var body = wire.Verify(data, nl + 1, data.Length - nl - 1, b => { });
                    if (body == null || wire.Control(body)) continue;
                    var d = Kv.Parse(body);
                    string seqS, src;
                    int seq;
                    if (!d.TryGetValue("seq", out seqS) || !int.TryParse(seqS, out seq) || !d.TryGetValue("src", out src)) continue;
                    int ls;
                    if (!srcSeq.TryGetValue(src, out ls)) ls = -1;
                    if (seq <= ls && seq > ls - 100) { Stale++; continue; }
                    if (ls >= 0 && seq > ls + 1) gap += seq - ls - 1;
                    srcSeq[src] = seq;
                    if (src == "h") { SrcH++; nick = Kv.NickOf(body) ?? nick; } else SrcC++;
                    got++;
                    outq.Push(body);
                    continue;
                }
                var f = Kv.Utf8.GetString(data).Split('|');
                if (f.Length < 3 || f[0] != WireTag.S) continue;
                int n;
                if (f[1] == "WATCHOK")
                {
                    Error = "";
                    if (f.Length > 3 && int.TryParse(f[3], out n)) Watchers = n;
                    if (f.Length > 4 && int.TryParse(f[4], out n)) QPos = n;
                }
                else if (f[1] == "SECOND" && f.Length > 3)
                {
                    bool on = f[3] == "1";
                    if (on == specMode) { lastSeqU = -1; var cb = OnRole; if (cb != null) cb(on ? "client" : "spec"); }
                }
                else if (f[1] == "YOURTURN" && f.Length > 3 && TurnToken == null)
                {
                    TurnToken = f[3];
                    log(L.T("轮到你上场了", "Your turn to play"));
                    NetLog.W("排队：服务器叫我上场");
                }
                else if (f[1] == "ERR")
                {
                    var why = f.Length > 3 ? f[3] : "";
                    var e = why == "nofound" ? L.T("大厅里没有这个房间（" + roomId + "）", "No such room (" + roomId + ")")
                          : why == "nowatch" ? L.T("这个房间的主机是旧版，不能观战", "The host's version does not support spectating")
                          : why == "norelay" ? L.T("这台大厅服务器没开中转，不能观战", "This lobby server has no relay, so no spectating")
                          : why == "watchfull" ? L.T("观战人数满了", "Too many spectators")
                          : why == "ranked" ? L.T(GameInfo.RankedLongZh + "房间不能观战", "Ranked rooms can't be watched")
                          : L.T("大厅服务器报错：", "Lobby server error: ") + why;
                    if (e != Error) { Error = e; log(e); NetLog.W("观战：" + e); }
                }
            }
            outq.Flush();
            return got;
        }
    }

    // 数值表（标准数值基准 + 武器平衡）从大厅服务器拉。**0.34（最终方案 4）起不再自动拉**：只有玩家在面板点"获取服务器数值表"
    // （cmd=cfgfetch，Lua 那边先弹二次确认）才问一次 4N|CFGQ|<我有的哈希>，5 秒没回再问，20 秒没回就放弃。
    // 服务器回 4N|CFG|<哈希>|<第几块>|<共几块>|<内容>（内容里本来就有 |，只按前 5 个 | 切）；共 0 块 = 我的已经是最新 / 服务器没有（哈希 -）。
    // 收齐、FNV-1a 哈希对得上才写 net\srvcfg.txt（h= / s=），Lua 启动和每场开局前读它。结果写 lobby.txt cfgst=（面板显示）、cfgh= / cfgat=（现有缓存的哈希 / 时间）。
    class CfgFetch
    {
        UdpClient sock;
        IPEndPoint server;
        ServerPick pick;
        string serverText = null, have = "", partsHash = null;
        string[] parts;
        DateTime askedAt = DateTime.MinValue, startedAt = DateTime.MinValue;
        public const int RetrySec = 5, GiveUpSec = 20;
        public volatile bool Want;              // 面板要求拉一次
        public volatile string Status = "";     // 给面板：空 / 正在获取 / 结果
        public string Have { get { return have; } }
        public long HaveAt;                     // srvcfg.txt 的修改时间（unix 秒），没有 = 0

        public static string Fnv(string s)
        {
            uint h = 0x811c9dc5;
            foreach (var b in Kv.Utf8.GetBytes(s)) { h ^= b; h *= 0x01000193; }
            return h.ToString("x8");
        }

        public void Request()
        {
            Want = true;
            askedAt = DateTime.MinValue;
            startedAt = DateTime.UtcNow;
            Status = L.T("正在向大厅服务器要数值表…", "Asking the lobby server for the table...");
        }

        public void Poll(string addr, string dir)
        {
            var now = DateTime.UtcNow;
            addr = (addr ?? "").Trim();
            if (addr != serverText)
            {
                serverText = addr;
                server = null;
                pick = null;
                Close();
                have = ReadHave(dir);
            }
            if (!Want)
            {
                if (sock != null) Drain(dir, now);     // 迟到的回包：照样收（已经是最新的那种不写盘）
                return;
            }
            if (addr.Length == 0) { Want = false; Status = L.T("没填大厅服务器", "No lobby server set"); return; }
            if (server == null)
            {
                server = LobbyServer.Resolve(addr);
                if (server == null) { Want = false; Status = L.T("大厅服务器地址解析不了", "Cannot resolve the lobby server"); return; }
            }
            if (sock == null)
            {
                try
                {
                    sock = NetBind.QueryUdp(server);     // IPv4 / IPv6 都能发
                    try { sock.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch (Exception) { }
                    pick = new ServerPick(server, sock);
                }
                catch (Exception ex) { sock = null; Want = false; Status = L.T("开不了端口：", "Cannot open a socket: ") + ex.Message; return; }
            }
            Drain(dir, now);
            if (!Want) return;
            if ((now - startedAt).TotalSeconds > GiveUpSec)
            {
                Want = false;
                Status = L.T("大厅服务器没回应（旧版服务器 / 没开 --cfg）", "The lobby server did not answer (old server / no --cfg)");
                NetLog.W("数值表：" + Status);
                return;
            }
            if ((now - askedAt).TotalSeconds >= RetrySec)
            {
                askedAt = now;
                var b = Kv.Utf8.GetBytes(WireTag.S + "|CFGQ|" + (have == "" ? "-" : have));
                pick.SendQuery(sock, b);     // 还没定哪一族时两个地址都问
            }
        }

        void Drain(string dir, DateTime now)
        {
            while (true)
            {
                int avail;
                try { avail = sock.Available; } catch (Exception) { break; }
                if (avail <= 0) break;
                IPEndPoint from = null;
                byte[] data;
                try { data = sock.Receive(ref from); } catch (Exception) { break; }
                from = NetBind.Norm(from);
                if (server == null || pick == null || !pick.Is(from)) continue;
                if (pick.Got(from)) server = pick.Cur;
                if (!pick.IsCur(from)) continue;     // 另一族迟到的回包不要（两边都回了一整份）
                var f = Kv.Utf8.GetString(data).Split(new[] { '|' }, 6);
                if (f.Length < 6 || f[0] != WireTag.S || f[1] != "CFG") continue;
                int i, n;
                if (!int.TryParse(f[3], out i) || !int.TryParse(f[4], out n)) continue;
                if (n == 0)
                {
                    if (!Want) continue;
                    Want = false;
                    Status = f[2] == "-" ? L.T("大厅服务器上没有数值表", "The lobby server has no table")
                           : L.T("已经是最新（", "Already up to date (") + f[2] + L.T("）", ")");
                    if (f[2] != have && f[2] != "-") NetLog.W("数值表：服务器说是 " + f[2] + "，本地缓存 " + (have == "" ? "（没有）" : have));
                    continue;
                }
                if (n > 200 || i < 1 || i > n) continue;
                if (partsHash != f[2] || parts == null || parts.Length != n) { partsHash = f[2]; parts = new string[n]; }
                parts[i - 1] = f[5];
                bool all = true;
                foreach (var x in parts) if (x == null) { all = false; break; }
                if (!all) continue;
                var text = string.Concat(parts);
                parts = null;
                if (Fnv(text) != partsHash) { NetLog.W("数值表：服务器发来的收齐了但哈希对不上（" + Fnv(text) + " ≠ " + partsHash + "），不用"); Status = L.T("收到的表坏了，没用", "Received table is corrupt; ignored"); Want = false; continue; }
                try
                {
                    var tmp = Path.Combine(dir, "srvcfg.txt.tmp");
                    File.WriteAllText(tmp, "h=" + partsHash + "\ns=" + text + "\nend=1\n", Kv.Utf8);
                    var dst = Path.Combine(dir, "srvcfg.txt");
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(tmp, dst);
                    have = partsHash;
                    HaveAt = (long)Link.UnixNow();
                    Status = L.T("已更新：", "Updated: ") + have + L.T("（", " (") + text.Length + L.T(" 字节）", " bytes)");
                    NetLog.W("数值表：从服务器拿到 " + have + "（" + text.Length + " 字节），写进 srvcfg.txt");
                }
                catch (Exception ex) { NetLog.W("数值表：写 srvcfg.txt 失败：" + ex.Message); Status = L.T("写不进 srvcfg.txt：", "Cannot write srvcfg.txt: ") + ex.Message; }
                Want = false;
            }
        }

        string ReadHave(string dir)
        {
            try
            {
                var f = Path.Combine(dir, "srvcfg.txt");
                HaveAt = File.Exists(f) ? (long)(File.GetLastWriteTimeUtc(f) - new DateTime(1970, 1, 1)).TotalSeconds : 0;
                foreach (var line in File.ReadAllLines(f, Kv.Utf8))
                    if (line.StartsWith("h=")) return line.Substring(2).Trim();
            }
            catch (Exception) { }
            return "";
        }

        public void Close() { if (sock != null) { try { sock.Close(); } catch (Exception) { } sock = null; } }
    }

    class LobbyList
    {
        UdpClient sock;
        IPEndPoint server;
        ServerPick pick;
        string serverText = "";
        DateTime lastAsk = DateTime.MinValue, lastAnswer = DateTime.MinValue;
        List<string[]> rooms = new List<string[]>();   // {房间号, 名字, 地图, 已满[, 所在服务器（空 = 这台）]}
        public string Error = "";

        // 手输房间号没有列表缓存时，先确定协议 / 桥接策略，不能猜成旧版 UDP 房。
        public static string[] Lookup(string addr, string code)
        {
            var ep = LobbyServer.Resolve(addr);
            if (ep == null) return null;
            try
            {
                using (var query = NetBind.QueryUdp(ep))
                {
                    query.Client.ReceiveTimeout = 500;
                    var route = new ServerPick(ep, query);
                    var request = Kv.Utf8.GetBytes(WireTag.S + "|LIST|" + code.Trim().ToUpperInvariant());
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        route.SendQuery(query, request);
                        try
                        {
                            IPEndPoint from = null;
                            var data = query.Receive(ref from);
                            if (!route.Is(NetBind.Norm(from))) continue;
                            var lines = Kv.Utf8.GetString(data).Split('\n');
                            if (lines[0] != WireTag.S + "|ROOMS") continue;
                            for (int i = 1; i < lines.Length; i++)
                            {
                                var row = lines[i].Split('\t');
                                if (row.Length >= 4 && string.Equals(row[0].Trim(), code.Trim(), StringComparison.OrdinalIgnoreCase)) return row;
                            }
                            return null;
                        }
                        catch (SocketException) { }
                    }
                }
            }
            catch (Exception ex) { NetLog.W("查询入房策略失败：" + ex.Message); }
            return null;
        }

        public List<string[]> Poll(string addr)
        {
            var now = DateTime.UtcNow;
            if (addr != serverText)
            {
                serverText = addr;
                server = null;
                pick = null;
                Close();     // 换服务器换 socket（可能换地址族）
                rooms = new List<string[]>();
                lastAnswer = DateTime.MinValue;
                lastAsk = DateTime.MinValue;
                startedAt = DateTime.MinValue;
                listOk = listQuietLogged = false;
                Error = "";
            }
            if (server == null && (now - lastAsk).TotalSeconds > 5)
            {
                lastAsk = now;
                server = LobbyServer.Resolve(addr);
                if (server == null)
                {
                    Error = addr.Trim().Length == 0 ? L.T("先在设置里填大厅服务器地址（可自己架设）", "Set or host a lobby server first")
                                                     : L.T("认不出大厅服务器地址：", "Can't resolve the lobby server: ") + addr;
                    return rooms;
                }
                lastAsk = DateTime.MinValue;
            }
            if (server == null) return rooms;
            if (sock == null)
            {
                try
                {
                    sock = NetBind.QueryUdp(server);     // IPv4 / IPv6 都能发
                    try { sock.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch (Exception) { }
                }
                catch (Exception ex) { Error = ex.Message; return rooms; }
            }
            if (pick == null) pick = new ServerPick(server, sock);
            while (true)
            {
                int avail;
                try { avail = sock.Available; } catch (Exception) { break; }
                if (avail <= 0) break;
                IPEndPoint from = null;
                byte[] data;
                try { data = sock.Receive(ref from); } catch (Exception) { break; }
                from = NetBind.Norm(from);
                if (!pick.Is(from)) continue;     // 两个地址都认，先回的定下来
                if (pick.Got(from)) { server = pick.Cur; NetLog.W("房间列表：大厅服务器的 " + ServerPick.Fam(server) + " 地址先回应，改用它"); }
                if (!pick.IsCur(from)) continue;
                var lines = Kv.Utf8.GetString(data).Split('\n');
                if (lines[0] != WireTag.S + "|ROOMS") continue;
                var list = new List<string[]>();
                for (int i = 1; i < lines.Length; i++)
                {
                    var p = lines[i].Split('\t');
                    if (p.Length >= 4) list.Add(p);
                }
                rooms = list;
                if (!listOk) { listOk = true; NetLog.W("房间列表：大厅服务器有回应（" + list.Count + " 个房间）"); }
                lastAnswer = now;
                Error = "";
            }
            if ((now - lastAsk).TotalSeconds > 2)
            {
                lastAsk = now;
                var b = Kv.Utf8.GetBytes(WireTag.S + "|LIST");
                pick.SendQuery(sock, b);     // 还没定哪一族时两个地址都问
            }
            if (startedAt == DateTime.MinValue) startedAt = now;
            // 刚开始问的 4 秒内不报；之后 6 秒没回答 = 服务器没开 / 地址填错 / 被防火墙挡
            if ((now - startedAt).TotalSeconds > 4 && (now - lastAnswer).TotalSeconds > 6)
            {
                Error = L.T("大厅服务器没回应：", "The lobby server is not answering: ") + LobbyServer.Show(serverText, server);
                rooms = new List<string[]>();
                if (listOk || !listQuietLogged)
                {
                    listOk = false; listQuietLogged = true;
                    NetLog.W("房间列表：大厅服务器没回应（" + (lastAnswer == DateTime.MinValue ? "一直" : "6 秒") + "没回答列表请求；服务器 "
                             + NetLog.Ip(server) + "）");
                }
            }
            return rooms;
        }

        DateTime startedAt = DateTime.MinValue;
        bool listOk, listQuietLogged;     // net_conn.log 只记有回应 / 没回应的变化

        // 第一份房间列表还没回来（也没报错）：面板显示"正在获取房间…"而不是"没有房间"
        public bool Listing { get { return serverText.Trim().Length > 0 && lastAnswer == DateTime.MinValue && Error.Length == 0; } }
        // 面板打开房间列表页时：下次 Poll 马上问，不等 2 秒
        public void Refresh() { lastAsk = DateTime.MinValue; }

        public void Close() { if (sock != null) { try { sock.Close(); } catch (Exception) { } sock = null; } }
    }
}
