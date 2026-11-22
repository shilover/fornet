// M3 多人混战全互连（/ M3）：房主发介绍信，玩家两两直连；
// 每对每秒测直连 / 经第三人中转的延迟与丢包，按分数选路；丢包高时直连 + 中转双发，接收端按 src + seq 去重。
// 游戏那边仍然只认带 src=<编号> 的包，game_multi.lua 不变。
//
// 协议（都走原来的签名 Wire，旧版联机程序不认识 4NMP* 会当控制包忽略）：
// 4NMP 房主 → 玩家：peer=<编号> addr=<ip:端口> sid=<平台账号 ID> token=<口令> 介绍一个对端
// 4NMPHELLO 玩家 → 玩家：pid=<我的编号> token=<口令> 直连握手
// 4NMPPING / 4NMPPONG 直连延迟探测
// 4NMPRELAY 经第三人中转：头部 from/to/kind/ttl/relay + "--" + 原包
// kind=state 状态包（按 src+seq 去重）；kind=rel 可靠事件；kind=vp / pr 经中转测延迟。
// 旧版（没有 M3 的联机程序）在多人房里仍然走原来的星形转发；新版房主收到没有 mp=1 的握手照旧走 1v1。
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ForNet
{
    // 一条候选中转路线（经 relayPid）的探测结果
    sealed class MpProbe
    {
        public int RelayPid;
        public double Rtt = -1;          // 往返毫秒（A→C→B→C→A）
        public double Loss;              // 0..1
        public int MaxLoss;              // 最长连续丢包（秒）
        public int Sent, Recv;           // 当前 1 秒窗口
        public DateTime LastPong = DateTime.MinValue;
        public DateTime LastSend = DateTime.MinValue;
        public int Miss;

        public MpProbe() { RelayPid = 0; }
    }

    sealed class MpPeerState
    {
        public int Pid;
        public object Handle;            // 控制连接 / 直连 IP 端点（房主看玩家、玩家看房主）
        public object DirectHandle;      // 桥接平台另开的直连句柄（LAN 为 null，直接用 Handle）
        public object AltHandle;         // 对方从另一个地址族（IPv4 / IPv6）发来的握手地址：也认它的包，但发还是发给 Handle
        // 另一个地址族那条线（多半是 IPv6）：房主介绍时带 v6=（对方自己报的），两边同时测，明显更快 / 当前那条断了就换过去（Handle ↔ AltHandle 对调）
        public string V6Addr = "";       // 对方自己报的公网 IPv6 端点（"[v6]:端口"；房主记着给别人介绍）
        public bool AltVerified;         // 另一族收到过对方带口令的握手 / 回了 PONG
        public double AltRtt = -1;
        public DateTime LastAltRx, LastAltHelloAt, LastAltPingAt, LastAltPickAt;
        public int AltBetterTicks;
        public DateTime BornAt = DateTime.UtcNow;   // 第一次知道这个对端的时间（M4：5 秒内先等直连 / 打洞，不急着经房主中转）
        public ulong BridgeId;
        public string Addr = "";         // LAN：ip:端口（房主看到的公网 / 局域网端点）
        public string LanAddr = "";      // M4：对方自己报的局域网端点（同一局域网里房主给的是公网端点时可以先试这个）
        public bool PreferLan;           // 直连一直不通时 4–12 秒改试局域网端点
        public string Nick = "";
        public string Uid = "";          // 状态包里的 uid=（拉黑 / 举报 / "最近对手"用；对方自报，同 1v1）
        public string Token = "";        // 房主介绍时给这一对的一次性口令
        public bool DirectVerified;      // 收到对方带正确口令的 4NMPHELLO
        public volatile bool MdzOk;      // 对方会拼全互连省流包（它的状态包 / 握手 / 探测里带 mdz=1）
        public volatile bool MdaOk;      // 对方还会拼房主代发的 AI 省流包（带 mda=1；只有 没有 的版本不会）
        public volatile bool HostMdz;    // 房主在名单里担保它会拼（对决观众只经房主、不发状态包时只能靠这个）
        public bool Hidden;              // 对方开了"隐藏我的 IP"：房主不把它的地址介绍给别人，和它之间只经房主转发
        public bool IsAi;                // 本机房主托管的多人 AI（不发握手/ping，不超时）
        public DateTime LastHelloAt, LastDirectRx, LastViaRx, LastStateAt, LastPingAt, LastEnsureAt;
        public DateTime LastAnyRx
        {
            get
            {
                DateTime dt = LastDirectRx > LastViaRx ? LastDirectRx : LastViaRx;
                if (LastBuRx > dt) dt = LastBuRx;
                if (LastStateAt > dt) dt = LastStateAt;
                return dt;
            }
        }
        public double DirectRtt = -1, DirectLoss;
        public int DirectMaxLoss, DirectSentWin, DirectRecvWin, DirectMiss;
        public int DirectSentAll, DirectRecvAll;
        public PeerRoute Route = PeerRoute.Direct;
        public int RelayPid;             // 现在用的中转人（0 = 不用中转）
        public int BetterTicks;          // 连续几秒"另一条明显更好"（切换滞后）
        public bool DualSend;            // 直连丢包高：两条都发
        public RelChannel Rel;
        public readonly Dictionary<int, MpProbe> Probes = new Dictionary<int, MpProbe>();
        public int PingRid;
        public DateTime LastProbeAt = DateTime.MinValue;
        public int DirectRxCount, ViaRxCount;
        public long StateTx, FwdTx;      // 对决统计：我发给它的自己的状态包 / 我（当转发机）转给它的选手包
        public string RouteWhy = "";
        public DateTime ByeAt = DateTime.MinValue;     // 成员正常退出时发来的告别（只有房主记；之后 1 秒以上又有包 = 回来了，作废）
        public double PeerHostAge = -1;                // 成员报给我的它最后收到房主包的间隔（秒）
        public DateTime PeerHostAgeAt = DateTime.MinValue;
        public uint BuHandle;                          // 按需升级的桥接连接句柄（0 = 未建）
        public bool BuConnected;
        public bool BuPeerActive;
        public bool BuAbandoned;
        public int BuPhase;                            // 0=测直连, 1=已询问, 2=建连中, 3=测桥接, 4=桥接中, 5=结束
        public DateTime BuConnSince = DateTime.MinValue;
        public DateTime BuSwitchedAt = DateTime.MinValue;
        public DateTime LastBuDirProbeAt = DateTime.MinValue;
        public DateTime LastBuBrProbeAt = DateTime.MinValue;
        public DateTime LastBuAskAt = DateTime.MinValue;
        public DateTime LastBuRx = DateTime.MinValue;
        public DateTime LastBuDirRx = DateTime.MinValue;
        public DateTime LastBuCheckAt = DateTime.MinValue;
        public DateTime BuGiveUpAt = DateTime.MinValue;
        public readonly List<double> BuDirRtts = new List<double>();
        public readonly List<double> BuBrRtts = new List<double>();
        public double BuBridgeRtt = -1;
        public long BuRxCount;

        public MpPeerState() { DirectHandle = null; }
        public object SendHandle { get { return DirectHandle ?? Handle; } }
        public void ResetWin() { DirectSentWin = DirectRecvWin = 0; }
    }

    // ---- 新版 1v1（对决模式 duel）的测速摘要 / 转发机选择：纯逻辑，不碰网络（见 MpMesh 里对决一节）。

    // 一位成员报给房主的测速摘要（4NMPNET）
    sealed class DuelReport
    {
        public DateTime At;
        public readonly Dictionary<int, double> Rtt = new Dictionary<int, double>();    // 对端编号 → 往返毫秒（<0 = 没测到）
        public readonly Dictionary<int, double> Loss = new Dictionary<int, double>();   // 对端编号 → 丢包率 0..1
        public double RxAgeMs = -1;     // 距离上次收到选手状态包多少毫秒（-1 = 还没收到过）
        public double RxLoss;           // 最近一个窗口里收选手包的丢包率（按 seq 空洞算）
        public double RxGapMs;          // 最近一个窗口里两个选手包之间最长的间隔
    }

    // 按选手编号记收包情况（观众报给房主的"收转发包"质量）
    sealed class DuelRxTrack
    {
        public int LastSeq = -1, WinBase = -1, WinGot;
        public long Total;
        public DateTime LastAt = DateTime.MinValue;
        public double WinMaxGapMs;
    }

    // 转发机候选人的"当选"过程（防抖）。Decide 每次调用给出现任是否要换；不碰网络，方便测试
    sealed class DuelRelayElector
    {
        public int Cur;                         // 现任转发机（0 = 没有）
        public int Cand;                        // 正在计时的挑战者
        public DateTime CandSince = DateTime.MinValue;
        public DateTime LastForce = DateTime.MinValue;
        public DateTime RxBreakSince = DateTime.MinValue;

        // scores：候选人 → 分数（+∞ = 没数据，不能靠分数当选）；order：兜底顺序（房主不上场优先、其次最早进房的观众）
        // curBad：现任不能再当（掉线 / 离房 / 上场了），badWhy 是原因；rxBreak：现任在转发，但观众大面积收不到
        // 返回新的现任；why != null 表示换了人（或初选），里面写原因
        public int Decide(DateTime now, bool live, bool curBad, string badWhy, bool rxBreak,
                          IDictionary<int, double> scores, IList<int> order, out string why)
        {
            why = null;
            if (order.Count == 0)
            {
                Cand = 0; RxBreakSince = DateTime.MinValue;
                if (Cur != 0) { Cur = 0; why = "没有观众，不需要转发机"; }
                return Cur;
            }
            bool inOrder = false;
            for (int i = 0; i < order.Count; i++) if (order[i] == Cur) inOrder = true;
            if (Cur == 0 || curBad || !inOrder)
            {
                string r = Cur == 0 ? "初选" : (curBad && badWhy != null ? badWhy : "现任不在候选里");
                int bad = (curBad || !inOrder) ? Cur : 0;
                int pick = Best(scores, order, bad);
                bool scored = pick != 0;
                if (pick == 0) pick = FirstOther(order, bad);
                if (pick == 0) { Cur = 0; return Cur; }
                Cur = pick; Cand = 0; LastForce = now; RxBreakSince = DateTime.MinValue;
                why = r + (scored ? "（按分数）" : "（没数据，按兜底顺序）");
                return Cur;
            }
            if (rxBreak && (now - LastForce).TotalSeconds >= MpMesh.RelayForceCooldownSec)
            {
                if (RxBreakSince == DateTime.MinValue) RxBreakSince = now;
                if ((now - RxBreakSince).TotalSeconds >= 1.0)
                {
                    int pick = Best(scores, order, Cur);
                    if (pick == 0) pick = FirstOther(order, Cur);
                    if (pick != 0 && pick != Cur)
                    {
                        Cur = pick; Cand = 0; LastForce = now; RxBreakSince = DateTime.MinValue;
                        why = "观众大面积收不到转发包，立刻换（局中也换）";
                        return Cur;
                    }
                }
            }
            else RxBreakSince = DateTime.MinValue;
            if (live) { Cand = 0; return Cur; }      // 局中不按分数换；打完（live=0）后重新开始计 5 秒
            int best = Best(scores, order, 0);
            double curScore = double.PositiveInfinity;
            if (scores.ContainsKey(Cur)) curScore = scores[Cur];
            if (best != 0 && best != Cur && scores[best] + MpMesh.RelaySwitchMarginMs < curScore)
            {
                if (Cand != best) { Cand = best; CandSince = now; }
                else if ((now - CandSince).TotalSeconds >= MpMesh.RelaySwitchHoldSec)
                {
                    why = "新候选分数 " + Fmt(scores[best]) + " 比现任 " + Fmt(curScore) + " 好 " + MpMesh.RelaySwitchMarginMs.ToString("0") + " ms 以上，已持续 "
                        + MpMesh.RelaySwitchHoldSec.ToString("0") + " 秒";
                    Cur = best; Cand = 0;
                }
            }
            else Cand = 0;
            return Cur;
        }

        static string Fmt(double v) { return double.IsInfinity(v) ? "∞" : v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); }

        static int Best(IDictionary<int, double> scores, IList<int> order, int exclude)
        {
            int best = 0;
            double bestScore = double.PositiveInfinity;
            for (int i = 0; i < order.Count; i++)
            {
                int pid = order[i];
                double sc;
                if (pid == exclude || !scores.TryGetValue(pid, out sc) || double.IsInfinity(sc)) continue;
                if (sc < bestScore) { bestScore = sc; best = pid; }
            }
            return best;
        }

        static int FirstOther(IList<int> order, int exclude)
        {
            for (int i = 0; i < order.Count; i++) if (order[i] != exclude) return order[i];
            return 0;
        }
    }

    // 每个联机会话一个；Link / BridgeLink 把"发到句柄"和"交给游戏"两个动作接进来
    sealed class MpMesh
    {
        public const double HelloEvery = 0.5, PingEvery = 1.0, ProbeEvery = 1.0, IntroEvery = 3.0;
        public const int MaxRelayPairs = 6;       // 每人最多帮几对中转（11-15 房间放到 12 人、对数从 15 到 66，3 → 6）
        public const double SwitchMargin = 40.0;  // 经中转的往返比直连好 40ms（单程 20ms）才考虑切
        public const int SwitchTicks = 2;         // 连续 2 秒都更好才切，别来回跳
        public const double HostLostAfter = 6.0;  // 6 秒没收到房主任何包（直连或中转都算）就当房主离开
        public const double HostLostFastAfter = 2.5; // 2.5 秒没收到房主包且有其他成员也证实收不到时快速换房主

        public int MyPid;
        public bool IsHost;
        public int HostPid = 1;
        public int Epoch = 0;
        public int MpMax = 4;
        public int MpSeen = 1;
        public string MpRoster = "";
        public Action<int, int> OnHostChanged;
        public Action<int, int, string> OnRosterUpdated;
        DateTime lastMpBroadcast = DateTime.MinValue;
        readonly DateTime bornAt = DateTime.UtcNow;
        public ulong SelfSid = 0UL;
        // 隐藏我的 IP：netcfg hideip=1。开了以后房主介绍我时不带地址（addr / lan / sid），我也不主动直连房主以外的人；
        // 我和别人之间的包都经房主转发。只有房主（和换房主后的新房主）知道我的 IP。我当房主时不起作用（大家本来就要连房主）。
        public static volatile bool HideIpPref;
        // 本机公网 IPv6 端点（握手里报 v6=，房主介绍自己时带上）；没有公网 IPv6 / 关了 IPv6 / 开了隐藏 IP = ""。只问路由不发包，缓存 30 秒
        static IPAddress v6Cache;
        static DateTime v6CacheAt = DateTime.MinValue;
        public static string V6Ep(int port)
        {
            if (!NetBind.V6 || NetBind.Loopback || HideIpPref || port <= 0) return "";
            string force = WireTag.Env("TEST_V6SELF");     // 测试用
            if (!string.IsNullOrEmpty(force)) return Addr.Fmt(new IPEndPoint(IPAddress.Parse(force), port));
            if ((DateTime.UtcNow - v6CacheAt).TotalSeconds > 30) { v6Cache = Addr.OutboundV6(); v6CacheAt = DateTime.UtcNow; }
            return v6Cache == null ? "" : Addr.Fmt(new IPEndPoint(v6Cache, port));
        }
        public static string V6HelloLine(int port)
        {
            string a = V6Ep(port);
            return a.Length > 0 ? "v6=" + a + "\n" : "";
        }
        public bool HideMe = HideIpPref;
        public volatile bool HostHidden;     // 客户端：当前房主是否开了"隐藏我的 IP"
        public volatile bool MpInMatch;      // 客户端：房主通知房间当前是否在进行多人混战对局
        volatile bool hostInMatch, hostInMatchDirty;
        public void SetHostInMatch(bool inMatch)
        {
            bool v = inMatch && !DuelOn;
            if (hostInMatch == v) return;
            hostInMatch = v;
            hostInMatchDirty = true;
        }
        public bool IsHostHidden()
        {
            if (HostHidden) return true;
            lock (sync)
            {
                MpPeerState hp;
                if (peers.TryGetValue(HostPid, out hp) && hp.Hidden) return true;
            }
            return false;
        }
        public void RetargetJoinHost(object newEp)
        {
            if (IsHost || hostConfirmed || newEp == null) return;
            lock (sync)
            {
                MpPeerState ph;
                if (peers.TryGetValue(HostPid, out ph))
                {
                    ph.Handle = newEp;
                    ph.DirectHandle = newEp;
                    ph.AltHandle = null;
                    ph.Addr = newEp.ToString();
                }
                HostAddr = newEp.ToString();
            }
        }
        // 和 p 之间只能经房主转发：我不是房主、p 不是房主，且我或 p 开了隐藏
        bool RelayOnly(MpPeerState p) { return !IsHost && p.Pid != HostPid && p.Pid != waitNewHost && HostPid > 0 && (HideMe || p.Hidden); }     // 等候选房主宣布时先直连它（它接任后要连得上我）
        int RelayOf(MpPeerState p) { return RelayOnly(p) ? HostPid : (p.Route == PeerRoute.Via ? p.RelayPid : BestRelay(p)); }
        public string SelfName = "";
        public Func<string, byte[]> Seal;
        public Action<object, byte[]> SendRaw;
        public Func<int, string, ulong, object> EnsureHandle;   // 桥接平台：按 sid 连/等；LAN：解析 addr
        public Action<string> Deliver;           // 状态包交给本机游戏（已带 src）
        public Action<string> DeliverRel;        // 可靠事件交给本机游戏
        public Func<int, string, bool> ServerRelay;   // 服务器房：直连不通时经服务器中转（返回是否已发）
        public Action<int, string> OnHumanStateForAi; // 房主：收到人类玩家状态包，转喂本机 AI
        public Action<int> OnPeerLeftForAi;           // 房主：玩家离开/超时，清理 AI 里的旧玩家状态
        public Action<int> OnYieldAiForHuman;         // 房主：满员新人加入时让出最高编号 AI（参数 = 被踢的 AI 编号）

        readonly HashSet<int> aiPids = new HashSet<int>();
        public bool IsAiPid(int pid) { lock (sync) return aiPids.Contains(pid); }
        public int AiCount() { lock (sync) return aiPids.Count; }
        public List<int> AiPids()
        {
            lock (sync)
            {
                var l = new List<int>(aiPids);
                l.Sort();
                return l;
            }
        }
        string AiPidsCsvLocked()
        {
            if (aiPids.Count == 0) return "";
            var l = new List<int>(aiPids);
            l.Sort();
            var sb = new StringBuilder();
            for (int i = 0; i < l.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(l[i]);
            }
            return sb.ToString();
        }
        public string AiPidsCsv() { lock (sync) return AiPidsCsvLocked(); }
        public int HumanPeerCount()
        {
            lock (sync)
            {
                int c = 0;
                foreach (var p in peers.Values) if (!p.IsAi && p.Pid != MyPid) c++;
                return c;
            }
        }
        public void AddAiPlayer(int pid, string nick)
        {
            if (!IsHost || pid <= 0 || pid == MyPid) return;
            lock (sync)
            {
                MpPeerState p;
                if (!peers.TryGetValue(pid, out p)) { p = new MpPeerState { Pid = pid }; peers[pid] = p; }
                p.IsAi = true;
                p.Nick = nick ?? ("[AI] " + pid);
                p.LastStateAt = DateTime.UtcNow;
                aiPids.Add(pid);
            }
        }
        public void RemoveAiPlayer(int pid)
        {
            lock (sync)
            {
                aiPids.Remove(pid);
                MpPeerState p;
                if (peers.TryGetValue(pid, out p) && p.IsAi) peers.Remove(pid);
            }
        }
        // 房间列表 / 好友页报的人数：对局外 AI 会被真人挤掉，不算人数（另带 ;ai=<个数> 给面板显示）；
        // 对局中不挤 AI，照旧算进去。不是房主 / 没有 AI = 原样。以前 房主 + 3 AI 报 4/4，列表标 [已满]、真人点不进
        public static int Listed(MpMesh m, int total)
        {
            if (m == null || !m.IsHost) return total;
            int ai = m.AiCount();
            if (ai <= 0 || m.hostInMatch) return total;
            return Math.Max(1, total - ai);
        }
        public static string ListedTag(MpMesh m)
        {
            if (m == null || !m.IsHost) return "";
            int ai = m.AiCount();
            return ai > 0 && !m.hostInMatch ? ";ai=" + ai : "";     // 对局中人数已含 AI，不另带
        }
        public static string Hosting(MpMesh m, int total, int max, string tag)
        {
            return Listed(m, total) + "/" + max + ";" + tag + ListedTag(m);
        }
        public bool TryYieldAiForHuman(int max)
        {
            int evicted = 0;
            lock (sync)
            {
                if (!IsHost || aiPids.Count == 0) return false;
                // 复测 2：对局进行中不挤 AI。挤了以后新人拿到 AI 的编号，房主那局的参加名单 / 击杀统计都按编号记，
                // 新人会直接顶着 AI 的参赛位和战绩上场（没按 先观战）；满员就和普通满员房一样进不来，这局打完再进
                if (hostInMatch) return false;
                int humanTotal = 1;
                foreach (var p in peers.Values) if (!p.IsAi && p.Pid != MyPid) humanTotal++;
                if (humanTotal >= max || humanTotal + aiPids.Count < max) return false;
                foreach (var id in aiPids) if (id > evicted) evicted = id;
                if (evicted <= 0) return false;
                aiPids.Remove(evicted);
                peers.Remove(evicted);
            }
            Log(L.T("多人：真人加入，让出 AI 位置 #", "Multi: human joined, yielding AI slot #") + evicted);
            if (OnYieldAiForHuman != null)
            {
                try { OnYieldAiForHuman(evicted); } catch (Exception) { }
            }
            return true;
        }
        void SetAiPidsFromHost(string csv)
        {
            var next = new HashSet<int>();
            if (!string.IsNullOrEmpty(csv))
            {
                foreach (var part in csv.Split(','))
                {
                    int id;
                    if (int.TryParse(part.Trim(), out id) && id > 0) next.Add(id);
                }
            }
            lock (sync)
            {
                aiPids.Clear();
                foreach (var id in next) aiPids.Add(id);
            }
        }
        public bool NeedVerifiedDirect;  // 服务器房：Handle 只是"介绍的地址"，收到对方包才算直连通
        public Action<int, string, string> ChatIn; // 房主：收到某位玩家的聊天
        public Func<string, int, bool> Dedupe;   // src + seq 去重（PeerTable.TakeSrcSeq）
        public Action<string> Log = delegate { };
        public bool Enabled;
        public long MpTx, MpRx, MpDual, MpFwd, MpDup;
        long mpDecoded, mpDecodeWait;
        public long MpDecoded { get { return System.Threading.Interlocked.Read(ref mpDecoded); } }
        public long MpDecodeWait { get { return System.Threading.Interlocked.Read(ref mpDecodeWait); } }
        readonly object receiveDiagSync = new object();
        readonly HashSet<int> decodedSeen = new HashSet<int>();
        readonly Dictionary<int, DateTime> decodeWaitLog = new Dictionary<int, DateTime>();
        public static volatile int BridgeModePref = 1;
        public bool BridgePrimary;
        public int HostBridgeMode = -1;
        public Func<byte[], string> Verify;
        int EffectiveBridgeMode { get { return IsHost ? BridgeModePref : HostBridgeMode; } }
        bool CanAutoBridgeCfg
        {
            get
            {
                return Enabled && !BridgePrimary && EffectiveBridgeMode == 1 && BridgeUp.On
                    && !HideMe && (Consent.All || Consent.Has("bridge_match")) && Modes.ShowBridge;
            }
        }
        bool CanAutoBridge { get { return CanAutoBridgeCfg && Bridge.Ready && Bridge.MyId != 0; } }
        bool buSub, buListening;
        DateTime lastBuInitAt = DateTime.MinValue;
        const int BuPortBase = 27200;
        const int BuNone = 0, BuAsked = 1, BuConnecting = 2, BuMeasuring = 3, BuActive = 4, BuDone = 5;
        static int BuPort(int pid) { return BuPortBase + pid; }
        static long MonoMs() { return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency); }

        // ---- 离线测试用的"坏网络"：--mp-block a-b:开始秒:结束秒[:丢包率]
        // 只作用于 a、b 两人的直连（握手 / ping / 状态），经第三人的中转不受影响。
        static int blkA, blkB;
        static double blkStart, blkEnd, blkLoss;
        static DateTime blkAt = DateTime.UtcNow;
        static readonly Random blkRnd = new Random(12345);
        public static void SetBlock(string spec)
        {
            blkA = blkB = 0; blkStart = blkEnd = 0; blkLoss = 1.0;
            if (string.IsNullOrEmpty(spec)) return;
            try
            {
                var parts = spec.Split(':', ';');
                var pair = parts[0].Split('-');
                blkA = int.Parse(pair[0]); blkB = int.Parse(pair[1]);
                if (parts.Length > 1) blkStart = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                if (parts.Length > 2) blkEnd = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                if (parts.Length > 3) blkLoss = double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                blkAt = DateTime.UtcNow;
            }
            catch (Exception) { blkA = blkB = 0; }
        }
        public static bool Blocked(int a, int b)
        {
            if (blkA == 0) return false;
            if (!((a == blkA && b == blkB) || (a == blkB && b == blkA))) return false;
            double t = (DateTime.UtcNow - blkAt).TotalSeconds;
            if (t < blkStart || t > blkEnd) return false;
            if (blkLoss >= 1.0) return true;
            lock (blkRnd) return blkRnd.NextDouble() < blkLoss;
        }

        readonly Dictionary<int, MpPeerState> peers = new Dictionary<int, MpPeerState>();
        readonly object sync = new object();
        readonly Dictionary<string, DateTime> relayUse = new Dictionary<string, DateTime>();
        DateTime lastIntro = DateTime.MinValue;
        DateTime lastWin = DateTime.MinValue;

        public MpPeerState Get(int pid)
        {
            lock (sync)
            {
                MpPeerState p;
                return peers.TryGetValue(pid, out p) ? p : null;
            }
        }

        public List<int> Pids() { lock (sync) return new List<int>(peers.Keys); }
        // 房里真人的 [编号, uid, 昵称]（有 uid 的才给）
        public List<string[]> MemberIds()
        {
            var r = new List<string[]>();
            lock (sync)
                foreach (var p in peers.Values)
                    if (!p.IsAi && !string.IsNullOrEmpty(p.Uid)) r.Add(new[] { p.Pid.ToString(), p.Uid, p.Nick ?? "" });
            return r;
        }
        public int Count { get { lock (sync) return peers.Count; } }
        public void Remove(int pid)
        {
            bool fire = false;
            lock (sync)
            {
                MpPeerState p;
                if (peers.TryGetValue(pid, out p))
                {
                    fire = !p.IsAi && pid != MyPid;
                    peers.Remove(pid);
                }
            }
            if (fire && IsHost && OnPeerLeftForAi != null)
            {
                try { OnPeerLeftForAi(pid); } catch (Exception) { }
            }
        }
        public void Clear() { lock (sync) peers.Clear(); }

        // 局域网端点只对真的局域网地址有用；127.0.0.1 是测试回环，跨机时连不过去，别拿它绕
        static bool UsableLan(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            var ep = Addr.ParseEp(s);     // "[v6]:端口" 也认；链路本地 IPv6 要带网卡号，跨机用不上
            if (ep == null) return false;
            var ip = ep.Address;
            return !IPAddress.IsLoopback(ip) && !ip.IsIPv6LinkLocal && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any);
        }

        // 房主：玩家加入 / 玩家地址变了。addr = LAN 的 ip:端口；BridgeId 只在桥接平台用
        public void AddPlayer(int pid, object handle, string addr, ulong sid, string nick, string lan = "", bool hidden = false, string v6 = "")
        {
            if (pid <= 0 || pid == MyPid) return;
            lock (sync)
            {
                MpPeerState p;
                if (!peers.TryGetValue(pid, out p)) { p = new MpPeerState(); p.Pid = pid; peers[pid] = p; }
                // 同一个人从另一族地址（已记成 AltHandle）又发来加入握手：不拿它顶掉当前那条。以前会把 Handle 换成另一族、
                // 原来那条（对方实际发状态包用的）就谁也不认了：它的状态包全被当成陌生地址丢掉，房主看不到这个人的替身（1122 日志 RXPR8J）
                if (handle != null && p.Handle != null && IsAltOnly(p, handle)) { p.LastAltRx = DateTime.UtcNow; handle = null; }
                if (handle != null) { p.Handle = handle; p.DirectVerified = true; p.LastDirectRx = DateTime.UtcNow; }     // 房主接住的控制连接本身就是可用的直连边
                if (!string.IsNullOrEmpty(addr)) p.Addr = addr;
                if (UsableLan(lan)) p.LanAddr = lan;
                if (UsableV6(v6)) p.V6Addr = v6;
                if (sid != 0UL) p.BridgeId = sid;
                if (!string.IsNullOrEmpty(nick)) p.Nick = nick;
                if (hidden && !p.Hidden) { p.Hidden = true; Log(L.T("多人网状：", "Mesh: ") + pid + L.T(" 开了隐藏 IP，不介绍它的地址", " hides its IP; its address is not introduced")); }
                if (string.IsNullOrEmpty(p.Token)) p.Token = PairToken(MyPid, pid);
                if (p.Rel == null) MakeRel(p);
            }
        }

        // 客户端：收到房主介绍（或自己先知道房主端点）
        public int AddIntro(int pid, string addr, ulong sid, string token, object handle, string lan = "", bool hidden = false, string v6 = "")
        {
            if (pid <= 0 || pid == MyPid) return 0;
            lock (sync)
            {
                MpPeerState p;
                if (!peers.TryGetValue(pid, out p)) { p = new MpPeerState(); p.Pid = pid; peers[pid] = p; }
                if (!string.IsNullOrEmpty(addr)) p.Addr = addr;
                if (UsableLan(lan)) p.LanAddr = lan;
                if (UsableV6(v6)) p.V6Addr = v6;
                if (sid != 0UL) p.BridgeId = sid;
                if (!string.IsNullOrEmpty(token)) p.Token = token;
                if (handle != null) p.Handle = handle;
                if (hidden) p.Hidden = true;
                if (p.Rel == null) MakeRel(p);
                return 1;
            }
        }

        // 桥接平台：连接回调里认出了对端，把直连句柄补上（控制连接走 Handle，另开的直连走 DirectHandle）
        public void SetDirectHandle(int pid, object handle)
        {
            lock (sync)
            {
                MpPeerState p;
                if (peers.TryGetValue(pid, out p) && handle != null) p.DirectHandle = handle;
            }
        }

        public bool HasHandle(object h)
        {
            return ByHandle(h) != null;
        }

        public int PidByHandle(object h)
        {
            MpPeerState p = ByHandle(h);
            return p == null ? 0 : p.Pid;
        }

        // 桥接平台：两个玩家之间的直连句柄断了。清掉这一对的两个句柄，下一次 Tick 让 EnsureHandle 重连
        // （编号小的一侧负责重连；另一侧等它连过来）。控制连接（房主那条）不走这里。
        public void DropDirectHandle(object h)
        {
            if (h == null) return;
            lock (sync)
            {
                foreach (var p in peers.Values)
                {
                    bool hit = false;
                    if (h.Equals(p.DirectHandle)) { p.DirectHandle = null; hit = true; }
                    if (h.Equals(p.Handle)) { p.Handle = null; hit = true; }
                    if (h.Equals(p.AltHandle)) { p.AltHandle = null; hit = true; }
                    if (!hit) continue;
                    p.DirectVerified = false;
                    p.DirectRtt = -1;
                    p.DirectLoss = 0;
                    p.DirectMaxLoss = 0;
                    p.DirectMiss = 0;
                    p.ResetWin();
                    p.LastHelloAt = DateTime.MinValue;
                    p.LastEnsureAt = DateTime.MinValue;
                }
            }
        }

        public int PidByBridge(ulong sid)
        {
            if (sid == 0UL) return 0;
            lock (sync)
            {
                foreach (var kv in peers) if (kv.Value.BridgeId == sid) return kv.Key;
            }
            return 0;
        }

        void MakeRel(MpPeerState p)
        {
            p.Rel = new RelChannel(b => SendRel(p, b));
            p.Rel.OnDeliver = ev =>
            {
                if (IsHost)
                {
                    if (ev.StartsWith("chat ", StringComparison.Ordinal) && ChatIn != null)
                        ChatIn(p.Pid, p.Nick, ev.Substring(5));
                    // 玩家要房主的数值表（cfgreq <哈希>）：交给房主自己的游戏，它发回的 cfg 块由 Program 广播给全房
                    else if (ev.StartsWith("cfgreq", StringComparison.Ordinal) && DeliverRel != null)
                        DeliverRel(ev);
                    // 选手这一场的规则整串（rfpx）：交给房主自己的游戏（房主当选手时对手是它）；对决模式再转给其他人
                    // （多人模式只比房主的，不转：不然别人的整串会盖掉房主的）
                    else if (ev.StartsWith("rfpx ", StringComparison.Ordinal))
                    {
                        if (DeliverRel != null) DeliverRel(ev);
                        if (DuelOn) HostRelAll(ev, p.Pid);
                    }
                }
                else if (DeliverRel != null) DeliverRel(ev);
            };
            p.Rel.Log = delegate { };
        }

        static string PairToken(int a, int b, int salt)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            uint h = 2166136261u;
            string s = "mpmesh/" + salt + "/" + lo + "/" + hi;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            return h.ToString("x8");
        }

        readonly int tokenSalt = 0;
        string PairToken(int a, int b) { return PairToken(a, b, tokenSalt); }

        // 房主：把每一对玩家的介绍信重发一遍（新玩家进来 / 3 秒一次兜底）
        public void SendIntros(DateTime now, bool force)
        {
            if (!IsHost || MyPid <= 0) return;
            lock (sync)
            {
                if (!force && (now - lastIntro).TotalSeconds < IntroEvery) return;
                lastIntro = now;
                int[] ids = new int[peers.Count];
                int n = 0;
                foreach (var k in peers.Keys) ids[n++] = k;
                Array.Sort(ids);
                for (int i = 0; i < n; i++)
                {
                    MpPeerState p = peers[ids[i]];
                    if (p.IsAi || p.Handle == null) continue;
                    SendIntroTo(p, HostPid, HostAddrFor(p), SelfSid, PairToken(HostPid, p.Pid), HostAddrFor(p), false, HideMe ? "" : MyV6Addr);
                    for (int j = 0; j < n; j++)
                    {
                        if (i == j) continue;
                        MpPeerState q = peers[ids[j]];
                        if (q.IsAi) continue;
                        if (q.Hidden) SendIntroTo(p, q.Pid, "", 0UL, PairToken(p.Pid, q.Pid), "", true);     // 不给地址，只给口令（换房主后新房主要认它的握手）
                        else SendIntroTo(p, q.Pid, q.Addr, q.BridgeId, PairToken(p.Pid, q.Pid), q.LanAddr, false, q.V6Addr);
                    }
                }
            }
        }

        public string HostAddr = "";      // LAN：房主的 ip:端口；桥接平台空
        // 按玩家算房主地址（IPv6 玩家要拿到房主的 IPv6 地址，IPv4 玩家拿 IPv4 的）；没设 / 算不出退回 HostAddr
        public Func<object, string> HostAddrOf;
        string HostAddrFor(MpPeerState p)
        {
            string a = null;
            if (HostAddrOf != null && p.Handle != null) { try { a = HostAddrOf(p.Handle); } catch (Exception) { a = null; } }
            if (string.IsNullOrEmpty(a)) a = HostAddr;
            return string.IsNullOrEmpty(a) ? "127.0.0.1:0" : a;
        }

        void SendIntroTo(MpPeerState to, int peerPid, string addr, ulong sid, string token, string lan, bool hidden = false, string v6 = "")
        {
            if (to.Handle == null) return;
            var sb = new StringBuilder();
            sb.Append(WireTag.P + "MPMESH\npeer=").Append(peerPid).Append('\n');
            if (!string.IsNullOrEmpty(addr) && addr != "127.0.0.1:0") sb.Append("addr=").Append(addr).Append('\n');
            if (!string.IsNullOrEmpty(lan) && lan != "127.0.0.1:0" && lan != addr) sb.Append("lan=").Append(lan).Append('\n');
            if (!hidden && UsableV6(v6) && v6 != addr) sb.Append("v6=").Append(v6).Append('\n');     // 另一条候选线（旧版不认这个键，忽略）
            if (sid != 0UL) sb.Append("sid=").Append(sid).Append('\n');
            sb.Append("token=").Append(token).Append('\n');
            if (hidden) sb.Append("hid=1\n");
            TrySend(to.Handle, sb.ToString());
        }

        void TrySend(object handle, string body)
        {
            if (handle == null || Seal == null) return;
            if (!BridgePrimary && handle is uint)
            {
                uint bh = (uint)handle;
                if (bh != 0) { try { Bridge.SendTo(bh, Seal(body), 1); } catch (Exception) { } }
                return;
            }
            if (SendRaw == null) return;
            try { SendRaw(handle, Seal(body)); } catch (Exception) { }
        }

        string StripSrc(string body)
        {
            var sb = new StringBuilder(body.Length + 16);
            foreach (var raw in body.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("src=", StringComparison.Ordinal)) continue;
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }

        static bool Critical(string body)
        {
            return body.IndexOf("\nmpd=", StringComparison.Ordinal) >= 0 || body.StartsWith("mpd=", StringComparison.Ordinal)
                || body.IndexOf("\nmpm=", StringComparison.Ordinal) >= 0 || body.StartsWith("mpm=", StringComparison.Ordinal);
        }

        // 每个玩家的状态包：发给每个对端（直连或经第三人）。mpd= / mpm= 关键字段、或直连丢包高时两条都发
        public void BroadcastState(string body)
        {
            if (!Enabled || MyPid <= 0 || string.IsNullOrEmpty(body)) return;
            // 对决观众不发状态包（DuelBroadcastState 里也挡着）：别先编码，不然 mpdz= 把没发出去的包算成"实发完整包"
            //（多人会话 11-15 复测：观众 mpdz=0:3680874/3680874，看着像观众一直在发完整包）
            // 只在一场开打后（live：倒计时 / 交手 / 回合之间）这样挡。待机、叫好选手等两人准备、一场结束时照常发给每个人，
            // 进房的人互相看得见（以前空闲时谁都不是选手、谁都不发，房里的人一个都看不到）
            bool duelPair = DuelOn && DuelLive && (DuelFa > 0 || DuelFb > 0);
            if (duelPair && MyPid != DuelFa && MyPid != DuelFb) return;
            string tagged = StripSrc(DzEncode(StripSrc(body))) + "src=" + MyPid + "\n";
            bool crit = Critical(body);
            if (duelPair) { DuelBroadcastState(tagged, crit); return; }       // 新版 1v1：只发对手 + 转发机；观众不发
            List<MpPeerState> list;
            lock (sync) list = new List<MpPeerState>(peers.Values);
            for (int i = 0; i < list.Count; i++) { if (list[i].IsAi) continue; list[i].StateTx++; SendState(list[i], tagged, crit); }
        }

        // 房主代发本机托管的 AI 状态包：先交给自己的游戏，再发给其他真人成员
        public void BroadcastAiState(int aiPid, string body)
        {
            if (!Enabled || !IsHost || aiPid <= 0 || aiPid == MyPid || string.IsNullOrEmpty(body)) return;
            if (!IsAiPid(aiPid)) return;
            DeliverState(aiPid, body);
            if (DuelOn) return;
            string tagged = StripSrc(AiDzEncode(aiPid, StripSrc(body))) + "src=" + aiPid + "\n";
            bool crit = Critical(body);
            List<MpPeerState> list;
            lock (sync) list = new List<MpPeerState>(peers.Values);
            for (int i = 0; i < list.Count; i++) { if (list[i].IsAi || list[i].Pid == MyPid) continue; list[i].StateTx++; SendState(list[i], tagged, crit); }
        }

        // ---- 全互连状态包省流。
        // 沿用 的 SpecDelta / DzIn：我的状态流统一编一次码（发给每个人的是同一串包，转发机 / 经第三人转出去的也是），
        // 平时只发变了的字段（变了连发 5 包），每 2 秒或房里的人变了发一次完整包；收的那头在交给游戏之前按发送方编号拼回完整包，游戏脚本一点不改。
        // 协商：我的每个状态包、直连握手、延迟探测都带 mdz=1（我会拼）；房主的状态包另带 mdh=<0|1>（房间设置"联机省流"，netcfg dz=）。
        // 只在 房里每个真人都说过 mdz=1、而且房主开着 时才编码；有一个旧版 / 还没说过的（刚进来）就全发完整包。经房主中转、从不直连探测的人
        // 只能靠它自己的状态包报 mdz=1；对决观众不发状态包，又只经房主（隐藏 IP）的话以前这一房一直发完整包—— 改由房主在名单里担保（mdzl=）
        public volatile int HostMdh = -1;              // 房主最近一包里的 mdh=（-1 = 没说 / 旧版房主）
        public volatile bool DzActive;                 // 这一包是不是在省流（面板 / 日志）
        readonly DzIn meshDzIn = new DzIn();
        SpecDelta meshDzOut;
        long dzFullBytes, dzSentBytes;
        public string DzStat
        {
            get
            {
                var e = meshDzOut;
                long full = dzFullBytes + (e != null ? e.FullBytes : 0), sent = dzSentBytes + (e != null ? e.SentBytes : 0);
                long aFull, aSent;
                lock (aiDzOut)
                {
                    aFull = aiDzFullBytes; aSent = aiDzSentBytes;
                    foreach (var ae in aiDzOut.Values) { aFull += ae.FullBytes; aSent += ae.SentBytes; }
                }
                // 第 5 段 = 房主代发 AI 的 实发 / 原样 字节（不是房主 = 0/0）；前 4 段照旧（测试 / 面板按位置读）
                return (DzActive ? "1" : "0") + ":" + sent + "/" + full + ":" + meshDzIn.Merged + ":" + meshDzIn.Dropped + ":" + aSent + "/" + aFull;
            }
        }

        // ---- 房主代发的 AI 状态包也省流。
        // 每个 AI 一个 SpecDelta（AI 的流互相独立）；收的那头按 "a<编号>" 拼（和真人的 "m<编号>" 分开，编号以后换给真人也不串）。
        // 只有房主编码，房主和每个成员都直连 / 控制连接，自己听得到对方说没说 mda=1（会拼 AI 包），不用 的担保。
        // 条件：房主开着"联机省流"而且其他每个真人都说过 mda=1；只有 的成员（会拼真人包、不会拼 AI 包）在就发完整 AI 包，真人包照样省。
        readonly Dictionary<int, SpecDelta> aiDzOut = new Dictionary<int, SpecDelta>();
        long aiDzFullBytes, aiDzSentBytes;
        bool aiDzActive;

        static bool HasLine(string s, string line)
        {
            return s != null && (s.StartsWith(line + "\n", StringComparison.Ordinal) || s.IndexOf("\n" + line + "\n", StringComparison.Ordinal) >= 0);
        }

        bool AllPeersMda(out int sig, out int humans)
        {
            sig = 17;
            humans = 0;
            bool all = true;
            lock (sync)
            {
                foreach (var kv in peers)
                {
                    var p = kv.Value;
                    if (p.IsAi || p.Pid == MyPid) continue;
                    humans++;
                    if (!p.MdaOk) all = false;
                    sig = unchecked(sig * 31 + p.Pid);
                }
            }
            return all && humans > 0;
        }

        string AiDzEncode(int aiPid, string body)
        {
            int sig, humans;
            bool all = AllPeersMda(out sig, out humans);
            bool on = DzCfg.On && all;
            lock (aiDzOut)
            {
                if (!on)
                {
                    if (aiDzActive) DzLog("多人网状：AI 状态省流停了（有人还不会拼 AI 包 / 房主关了 / 房里没别的真人），AI 发完整包",
                                          "Mesh: AI compact state off (someone can't decode AI packets / host turned it off / no other humans), sending full AI packets");
                    aiDzActive = false;
                    foreach (var ae in aiDzOut.Values) { aiDzFullBytes += ae.FullBytes; aiDzSentBytes += ae.SentBytes; }
                    aiDzOut.Clear();
                    if (humans > 0) { aiDzFullBytes += body.Length; aiDzSentBytes += body.Length; }
                    return body;
                }
                if (!aiDzActive) DzLog("多人网状：AI 状态省流开了（房里每个真人都会拼 AI 包）", "Mesh: AI compact state on (every human can decode AI packets)");
                aiDzActive = true;
                SpecDelta e;
                if (!aiDzOut.TryGetValue(aiPid, out e)) { e = new SpecDelta(); aiDzOut[aiPid] = e; }
                return e.Encode(body, sig, DzCfg.Now());
            }
        }

        // 选路说明里的分数：没有候选 = double.MaxValue / 无穷，以前被整串打印成三百多位数字（多人会话 11-15 实测看到）
        static string Sc(double v)
        {
            return double.IsNaN(v) ? "nan" : double.IsInfinity(v) || Math.Abs(v) >= 1e12 ? "inf" : Kv.F(v, "0");
        }

        static bool HasMdz(string s)
        {
            return s != null && (s.StartsWith("mdz=1\n", StringComparison.Ordinal) || s.IndexOf("\nmdz=1\n", StringComparison.Ordinal) >= 0);
        }

        // 房里每个真人都会拼？humans = 真人数（不算 AI 和我）；sig = 这些人的编号算出来的数（人变了 → 下一包发完整包）
        bool AllPeersDz(out int sig, out int humans)
        {
            sig = 17;
            humans = 0;
            bool all = true;
            lock (sync)
            {
                foreach (var kv in peers)
                {
                    var p = kv.Value;
                    if (p.IsAi || p.Pid == MyPid) continue;
                    humans++;
                    if (!p.MdzOk && !p.HostMdz) all = false;
                    sig = unchecked(sig * 31 + p.Pid);
                }
            }
            return all && humans > 0;
        }

        // 房主担保：房主在名单 4NMP 里带 mdzl=<房主亲耳听到说过 mdz=1 的真人编号>（含房主自己）。
        // 对决观众不发状态包、开了隐藏 IP 又只经房主，别人永远听不到它说 mdz=1；以前这时整房发完整包（进房时一包状态都没发出去才会这样，
        // 比如正在读图）。只用房主自己听到的（MdzOk），不转述别人担保的；每 2 秒随名单整份替换，人走了 / 换了就跟着变。
        string HostMdzCsvLocked()
        {
            var sb = new StringBuilder();
            sb.Append(MyPid);
            foreach (var p in peers.Values)
                if (!p.IsAi && p.Pid != MyPid && p.MdzOk) sb.Append(',').Append(p.Pid);
            return sb.ToString();
        }

        void ApplyHostMdz(string csv)
        {
            var ok = new HashSet<int>();
            foreach (var part in (csv ?? "").Split(','))
            {
                int v;
                if (int.TryParse(part.Trim(), out v) && v > 0) ok.Add(v);
            }
            lock (sync)
                foreach (var p in peers.Values) p.HostMdz = ok.Contains(p.Pid);
        }

        void DzLog(string zh, string en)
        {
            Log(L.T(zh, en));
            NetLog.W(zh);      // 也进 net_conn.log（多人会话 11-15：DLL 模式下只在窗口日志里，搜不到）
        }

        string DzEncode(string body)
        {
            // 状态包里写 mdai=1，不写 mda=1：对决房主的状态包里 mda= 是场地（game_duel DUEL.wire），撞名后游戏可能读到 "1"
            //（握手 / 探测包 4NMPHELLO / PING / PONG 是另一种消息，照旧 mda=1）
            string full = body + "mdz=1\nmdai=1\n" + (IsHost ? "mdh=" + (DzCfg.On ? "1" : "0") + "\n" : "");
            int sig, humans;
            bool all = AllPeersDz(out sig, out humans);
            bool on = (IsHost ? DzCfg.On : HostMdh == 1) && all;
            if (!on)
            {
                if (DzActive) DzLog("多人网状：联机省流停了（有人还不会拼 / 房主关了 / 房里没别的真人），发完整包",
                                    "Mesh: compact state off (someone can't decode / host turned it off / no other humans), sending full packets");
                DzActive = false;
                var e = meshDzOut;
                if (e != null) { dzFullBytes += e.FullBytes; dzSentBytes += e.SentBytes; meshDzOut = null; }
                // 房里没别的真人（只有我 + AI）时这包根本没发出去，不算进统计（多人会话 11-15：房主先和 AI 待了一阵，统计里实发占到 44%）
                if (humans > 0) { dzFullBytes += full.Length; dzSentBytes += full.Length; }
                return full;
            }
            if (!DzActive) DzLog("多人网状：联机省流开了（房里每个真人都会拼）", "Mesh: compact state on (every human in the room can decode)");
            DzActive = true;
            if (meshDzOut == null) meshDzOut = new SpecDelta();
            return meshDzOut.Encode(full, sig, DzCfg.Now());
        }

        // 收到的状态包（已去重）拼回完整包；还没收到过这个人的完整包 → null（丢掉，最多等 2 秒）
        string DzDecode(int pid, string tagged)
        {
            string dec = meshDzIn.Take(StripSrc(tagged), null, "m" + pid);
            if (dec == null) return null;
            MpPeerState p = Get(pid);
            // 状态包里的"会拼 AI 包"标记改叫 mdai=1（mda= 是对决场地字段 D1，同一个包里撞名）；11-15 的开发版发 mda=1，也认
            if (p != null) { p.MdzOk = HasMdz(dec); p.MdaOk = HasLine(dec, "mdai=1") || HasLine(dec, "mda=1"); }
            if (pid == HostPid)
            {
                int i = dec.StartsWith("mdh=", StringComparison.Ordinal) ? 0 : dec.IndexOf("\nmdh=", StringComparison.Ordinal);
                if (i > 0) i++;
                HostMdh = i < 0 ? -1 : (i + 4 < dec.Length && dec[i + 4] == '1' ? 1 : 0);
            }
            return dec + "src=" + pid + "\n";
        }

        void NoteDecodeWait(int pid)
        {
            long count = System.Threading.Interlocked.Increment(ref mpDecodeWait);
            var now = DateTime.UtcNow;
            lock (receiveDiagSync)
            {
                DateTime last;
                if (decodeWaitLog.TryGetValue(pid, out last) && (now - last).TotalSeconds < 10) return;
                decodeWaitLog[pid] = now;
            }
            NetLog.W("多人状态解码：来源 " + pid + " 尚未拼成完整包，等待全量状态（累计 " + count + "）");
        }

        void EmitDelivered(int pid, string tagged, bool isAi)
        {
            if (!isAi)
            {
                tagged = DzDecode(pid, tagged);
                if (tagged == null) { NoteDecodeWait(pid); return; }
                // 房里每位玩家的标识（同 1v1 的 PeerUid）：Program 据此把房里的人列进"最近对手"，能拉黑 / 举报
                var uid = Ident.Field("\n" + tagged, "uid");
                if (!string.IsNullOrEmpty(uid)) lock (sync) { MpPeerState pu; if (peers.TryGetValue(pid, out pu) && pu.Uid != uid) pu.Uid = uid; }
            }
            else
            {
                // 房主代发的 AI 包：省流包按 "a<编号>" 拼回；完整包原样（记下当底）
                string dec = meshDzIn.Take(StripSrc(tagged), null, "a" + pid);
                if (dec == null) { NoteDecodeWait(pid); return; }
                tagged = dec + "src=" + pid + "\n";
            }
            System.Threading.Interlocked.Increment(ref mpDecoded);
            bool firstDecoded;
            lock (receiveDiagSync) firstDecoded = decodedSeen.Add(pid);
            if (firstDecoded)
            {
                var fields = Kv.Parse(tagged);
                NetLog.W("多人状态解码：来源 " + pid + " 首个完整包，坐标字段 " + (fields.ContainsKey("x") && fields.ContainsKey("y") ? "齐" : "缺")
                    + "，交付回调 " + (Deliver != null ? "有" : "无"));
            }
            if (Deliver != null) Deliver(tagged);
            if (IsHost && !isAi && OnHumanStateForAi != null)
            {
                try { OnHumanStateForAi(pid, tagged); } catch (Exception) { }
            }
        }

        void SendState(MpPeerState p, string tagged, bool crit)
        {
            // 有一条能用的直连路径（已确认 / 另开的桥接平台直连 / 控制连接 / LAN 地址）就不要绕中转；
            // 桥接平台的"正在连"句柄也算：包先排着，连上就发。
            bool ro = RelayOnly(p);     // 只能经房主：不直连、不等打洞
            bool directOk = !ro && (p.DirectVerified || p.DirectHandle != null || (p.Handle != null && !NeedVerifiedDirect));
            // M4：刚介绍完的这 5 秒先让双方互发握手打洞，别一上来就把状态包丢给房主中转。
            // 关键包（mpd= / mpm=）例外：M3 约定这类字段一直双发，不能等。
            if (!ro && !directOk && !crit && (DateTime.UtcNow - p.BornAt).TotalSeconds < 5.0) return;
            bool dual = crit || p.DualSend;
            int relay = RelayOf(p);
            // 服务器房：没有直连（或直连丢了）时另发一份给服务器，由服务器转给对方
            bool viaServer = false;
            if (!directOk && ServerRelay != null)
            {
                viaServer = ServerRelay(p.Pid, tagged);
                if (viaServer) MpTx++;
            }
            if (!BridgePrimary && p.Route == PeerRoute.Bridge && p.BuHandle != 0 && p.BuConnected && !ro)
            {
                MpTx++;
                TrySend((object)p.BuHandle, tagged);
                if (crit)
                {
                    if (directOk) SendDirect(p, tagged, false);
                    else if (relay != 0 && relay != p.Pid) SendVia(p, relay, "state", tagged);
                }
                return;
            }
            if (p.Route == PeerRoute.Direct || !directOk)
            {
                if (directOk) SendDirect(p, tagged, true);
                // 只有直连不可用、或者丢包高 / 关键字段时才另发一份中转；正常直连不要双发
                if ((!directOk || dual) && relay != 0 && relay != p.Pid)
                {
                    SendVia(p, relay, "state", tagged);
                    if (dual) MpDual++;
                }
            }
            else
            {
                if (relay != 0 && relay != p.Pid) SendVia(p, relay, "state", tagged);
                else SendDirect(p, tagged, true);
                if (dual && directOk) { SendDirect(p, tagged, false); MpDual++; }
            }
        }

        void SendDirect(MpPeerState p, string body, bool count)
        {
            object h = p.SendHandle;
            if (h == null) return;
            if (Blocked(MyPid, p.Pid)) return;
            if (RelayOnly(p)) return;     // 兜底：只经房主的对端绝不直发（直发会把我的地址给它）
            if (count) MpTx++;
            TrySend(h, body);
        }

        void SendVia(MpPeerState p, int relayPid, string kind, string payload)
        {
            MpPeerState r = Get(relayPid);
            if (r == null || r.SendHandle == null) return;
            string body = Envelope(MyPid, p.Pid, kind, relayPid, 1, payload);
            MpTx++;
            TrySend(r.SendHandle, body);
        }

        static string Envelope(int from, int to, string kind, int relay, int ttl, string payload)
        {
            var sb = new StringBuilder(payload.Length + 96);
            sb.Append(WireTag.P + "MPRELAY\nfrom=").Append(from).Append("\nto=").Append(to)
              .Append("\nkind=").Append(kind).Append("\nrelay=").Append(relay).Append("\nttl=").Append(ttl)
              .Append("\n--\n").Append(payload);
            return sb.ToString();
        }

        int BestRelay(MpPeerState p)
        {
            int best = 0;
            double bestScore = double.MaxValue;
            lock (sync)
            {
                foreach (var kv in p.Probes)
                {
                    var pr = kv.Value;
                    if (pr.Rtt < 0 || pr.Miss >= 2) continue;
                    double score = pr.Rtt + pr.Loss * 120.0;
                    if (kv.Key != HostPid) score -= 10.0;      // 能不烦房主就不烦房主
                    if (score < bestScore) { bestScore = score; best = kv.Key; }
                }
            }
            return best;
        }

        void SendRel(MpPeerState p, string body)
        {
            // 可靠事件（聊天 / 回合结果）一直双发：直连 + 桥接（如已升级）+ 当前最好的中转
            if (!BridgePrimary && p.Route == PeerRoute.Bridge && p.BuHandle != 0 && p.BuConnected && !RelayOnly(p))
                TrySend((object)p.BuHandle, body);
            if (p.SendHandle != null && !RelayOnly(p)) TrySend(p.SendHandle, body);
            int relay = RelayOf(p);
            if (relay != 0 && relay != p.Pid && relay != MyPid) SendVia(p, relay, "rel", body);
        }

        // 房主：给某个玩家发一条聊天 / 可靠事件（聊天走这条）
        public void HostRelTo(int pid, string body)
        {
            MpPeerState p = Get(pid);
            if (p != null && p.Rel != null) p.Rel.Send(body);
        }

        public void HostRelAll(string body) { HostRelAll(body, 0); }

        public void HostRelAll(string body, int exceptPid)
        {
            List<MpPeerState> list;
            lock (sync) list = new List<MpPeerState>(peers.Values);
            for (int i = 0; i < list.Count; i++)
                if (list[i].Rel != null && (exceptPid == 0 || list[i].Pid != exceptPid)) list[i].Rel.Send(body);
        }

        // 客户端（含换房主后的存活成员与后进来的成员）：经 mesh 里当前房主的可靠通道发给房主
        public bool SendToHostRel(string body)
        {
            if (!Enabled || IsHost || HostPid <= 0) return false;
            MpPeerState hp = Get(HostPid);
            if (hp == null || hp.Rel == null) return false;
            hp.Rel.Send(body);
            return true;
        }

        // 发送控制包（直连 + 经第三人中转兜底）
        public void SendControl(int toPid, string body)
        {
            if (toPid <= 0 || toPid == MyPid) return;
            MpPeerState p = Get(toPid);
            if (p == null) return;
            bool directOk = !RelayOnly(p) && (p.DirectVerified || (p.Handle != null && !NeedVerifiedDirect));
            bool bridgeOk = !BridgePrimary && p.Route == PeerRoute.Bridge && p.BuHandle != 0 && p.BuConnected && !RelayOnly(p);
            int relay = RelayOf(p);
            if (bridgeOk) TrySend((object)p.BuHandle, body);
            if (directOk && !bridgeOk) SendDirect(p, body, false);
            if ((!directOk && !bridgeOk || p.DualSend || p.Route == PeerRoute.Via) && relay != 0 && relay != p.Pid && relay != MyPid)
            {
                SendVia(p, relay, "ctrl", body);
            }
        }

        public void BroadcastControl(string body, int exceptPid = 0)
        {
            List<MpPeerState> list;
            lock (sync) list = new List<MpPeerState>(peers.Values);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Pid != exceptPid && list[i].Pid != MyPid)
                    SendControl(list[i].Pid, body);
            }
        }

        // 房主正常退出时广播告别； 成员正常退出时只告诉房主（对决转发机走了房主立刻换人，不等 2 秒超时）
        public void BroadcastBye()
        {
            if (!Enabled || MyPid <= 0) return;
            string bye = WireTag.P + "MPBYE\npid=" + MyPid + "\nepoch=" + Epoch + "\n";
            if (!IsHost)
            {
                if (HostPid > 0 && HostPid != MyPid) SendControl(HostPid, bye);
                return;
            }
            List<MpPeerState> list;
            lock (sync) list = new List<MpPeerState>(peers.Values);
            for (int i = 0; i < list.Count; i++)
            {
                MpPeerState p = list[i];
                if (p.Pid == MyPid) continue;
                if (p.SendHandle != null) SendDirect(p, bye, false);
                int relay = RelayOf(p);
                if (relay != 0 && relay != p.Pid && relay != MyPid)
                {
                    SendVia(p, relay, "ctrl", bye);
                }
            }
        }

        // 房主广播名单 4NMP
        public void BroadcastMpInfo()
        {
            if (!IsHost || MyPid <= 0) return;
            PruneAndGetPlayers(MpCfg.Lost);
            lock (sync)
            {
                var players = PruneAndGetPlayers(0);
                var sb = new StringBuilder();
                foreach (var p in players)
                    sb.Append(p.Key).Append(':').Append(p.Value.Replace(",", " ").Replace(":", " ")).Append(',');
                string roster = sb.ToString().TrimEnd(',');
                string aiCsv = AiPidsCsvLocked();
                string mdzCsv = HostMdzCsvLocked();
                int n = players.Count + 1; // 含房主自己
                MpSeen = n;
                MpRoster = roster;

                foreach (var p in peers.Values)
                {
                    if (p.Pid == MyPid || p.IsAi) continue;
                    string msg = WireTag.P + "MP\npid=" + p.Pid + "\nn=" + n + "\nmax=" + MpMax + "\nl=" + roster + "\nepoch=" + Epoch + "\nowner=" + HostPid
                        + (HideMe ? "\nhidehost=1" : "") + (hostInMatch && !DuelOn ? "\ninmatch=1" : "") + "\nbm=" + BridgeModePref
                        + (aiCsv.Length > 0 ? "\nai=" + aiCsv : "") + "\nmdzl=" + mdzCsv + "\n";
                    SendControl(p.Pid, msg);
                }
            }
        }

        public List<KeyValuePair<int, string>> PruneAndGetPlayers(double timeout)
        {
            DateTime now = DateTime.UtcNow;
            var list = new List<KeyValuePair<int, string>>();
            var dead = new List<int>();
            lock (sync)
            {
                var ids = new List<int>(peers.Keys);
                ids.Sort();
                for (int i = 0; i < ids.Count; i++)
                {
                    int id = ids[i];
                    if (id == MyPid || id == HostPid) continue;
                    MpPeerState p = peers[id];
                    if (!p.IsAi && timeout > 0 && p.LastAnyRx != DateTime.MinValue && (now - p.LastAnyRx).TotalSeconds > timeout)
                    {
                        dead.Add(id);
                        continue;
                    }
                    list.Add(new KeyValuePair<int, string>(id, string.IsNullOrEmpty(p.Nick) ? "玩家" + id : p.Nick));
                }
                for (int i = 0; i < dead.Count; i++)
                {
                    peers.Remove(dead[i]);
                    Log(L.T("多人网状：玩家超时离开 ", "Mesh: player timed out ") + dead[i]);
                }
            }
            if (dead.Count > 0 && IsHost && OnPeerLeftForAi != null)
            {
                for (int i = 0; i < dead.Count; i++)
                {
                    try { OnPeerLeftForAi(dead[i]); } catch (Exception) { }
                }
            }
            return list;
        }

        // 换房主状态（客户端）：在等哪个候选人宣布、从什么时候开始等；等太久没宣布就跳过它重选
        int waitNewHost;
        DateTime waitNewHostSince = DateTime.MinValue;
        readonly HashSet<int> skipCands = new HashSet<int>();
        bool hostConfirmed;              // 客户端：已经从房主本人收到过一条认可的 4NMP（入房时那条"1 号"只是占位）
        DateTime lastBadClaimLog = DateTime.MinValue;

        public bool HostConfirmed { get { return hostConfirmed; } }

        // 服务器大厅入房：房主的地址是打洞打出来的，入房前没登记。第一次收到（已验签的）房主名单时先占位记到 HostPid，
        // 之后 OnMpInfo 按占位规则改成真编号。已经认过房主 / 这个地址认识就不动
        public void NoteJoinHost(object from)
        {
            if (from == null || hostConfirmed || IsHost) return;
            if (ByHandle(from) != null) return;
            AddIntro(HostPid, from is IPEndPoint ? from.ToString() : "", 0UL, "", from);     // 经桥接入房：句柄是桥接连接号，没有地址
        }

        // 发这条包的是谁（直连按地址认；不认识 = 0）
        int SenderPid(object from)
        {
            MpPeerState p = ByHandle(from);
            return p == null ? 0 : p.Pid;
        }

        void LogBadClaim(string what)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastBadClaimLog).TotalSeconds < 5.0) return;
            lastBadClaimLog = now;
            Log(L.T("多人网状：忽略来路不对的房主包 ", "Mesh: ignored host packet from wrong sender ") + what);
        }

        // 房主走了（告别 / 超时）。所有人按同一规则算候选人 = 自己看得见的在线玩家里编号最小的；
        // 只有选中自己的那台升代数、宣布；别人不动 HostPid，等它宣布（OnMpInfo 收到更大代数才换）。
        // 这样某一台只是自己和房主断了时不会单方面认一个别人不认的房主；等 6 秒还没人宣布就跳过那个候选人重选。
        public bool AllowHostMigration = true;     // 仅经房主桥接的房没有成员间迁移路径
        public void MigrateHost(string why)
        {
            if (!AllowHostMigration) return;
            DateTime now = DateTime.UtcNow;
            int oldHost, nextHost, nextEpoch = 0;
            bool self;
            lock (sync)
            {
                if (IsHost) return;
                oldHost = HostPid;
                if (waitNewHost != 0)
                {
                    if ((now - waitNewHostSince).TotalSeconds < HostLostAfter) return;
                    skipCands.Add(waitNewHost);
                    Log(L.T("多人网状：候选房主没宣布，跳过 ", "Mesh: candidate host never announced, skipping ") + waitNewHost);
                }
                int minPid = MyPid > 0 && !skipCands.Contains(MyPid) && !HideMe ? MyPid : int.MaxValue;     // 开了隐藏 IP 的人不当新房主
                foreach (var kv in peers)
                {
                    int pid = kv.Key;
                    if (pid == oldHost || pid <= 0 || skipCands.Contains(pid) || aiPids.Contains(pid)) continue;
                    MpPeerState p = kv.Value;
                    if (p.Hidden || p.IsAi) continue;     // 不把 AI 选成新房主
                    double age = (now - p.LastAnyRx).TotalSeconds;
                    bool active = (p.LastAnyRx != DateTime.MinValue && age < HostLostAfter)
                               || (p.LastAnyRx == DateTime.MinValue && (now - p.BornAt).TotalSeconds < HostLostAfter);
                    if (HideMe && RelayOnly(p)) active = true;     // 我和它之间只经旧房主：房主一走就看不到它的动静，不能当它走了
                    if (active && pid < minPid) minPid = pid;
                }
                if (minPid == int.MaxValue) { if (MyPid > 0) minPid = MyPid; else return; }
                nextHost = minPid;
                self = nextHost == MyPid;
                Log(L.T("多人网状：房主离开（", "Mesh: host left (") + why + ") old=" + oldHost + L.T(" 候选=", " candidate=") + nextHost + (self ? L.T("（自己）", " (me)") : ""));
                // 换房主时原房主托管的 AI 直接随原房主消失
                if (aiPids.Count > 0)
                {
                    foreach (var aid in aiPids) peers.Remove(aid);
                    aiPids.Clear();
                }
                if (self)
                {
                    if (peers.ContainsKey(oldHost)) peers.Remove(oldHost);
                    nextEpoch = Epoch + 1;
                    HostPid = nextHost;
                    Epoch = nextEpoch;
                    IsHost = true;
                    hostConfirmed = true;
                    waitNewHost = 0;
                    skipCands.Clear();
                    if (DuelOn) duelDirty = true;
                    Log(L.T("多人网状：我当新房主 epoch=", "Mesh: I am the new host epoch=") + nextEpoch);
                }
                else
                {
                    waitNewHost = nextHost;
                    waitNewHostSince = now;
                }
            }
            if (!self) return;
            if (OnHostChanged != null)
            {
                try { OnHostChanged(nextHost, nextEpoch); } catch (Exception) { }
            }
            BroadcastMpInfo();
            SendIntros(now, true);
        }

        // viaPid > 0 = 经第三人中转来的控制包，发送人是 viaPid（中转头里的 from）
        void OnBye(object from, string s, int viaPid)
        {
            var d = Kv.Parse(s);
            string pv;
            int pid;
            if (!d.TryGetValue("pid", out pv) || !int.TryParse(pv, out pid)) return;
            int sender = viaPid > 0 ? viaPid : SenderPid(from);
            if (sender == 0 || sender != pid) { LogBadClaim("bye pid=" + pid + " sender=" + sender); return; }
            if (IsHost) { OnMemberBye(pid); return; }
            if (pid != HostPid) return;
            Log(L.T("多人网状：收到房主告别 ", "Mesh: received host bye ") + pid);
            MigrateHost("bye");
        }

        // 房主收到成员告别：记下来；对决开着就马上重算转发机（现任走了立刻换）。名单照旧按超时清，不在这里删人
        void OnMemberBye(int pid)
        {
            if (pid <= 0 || pid == MyPid) return;
            bool wasRelay;
            lock (sync)
            {
                MpPeerState p;
                if (!peers.TryGetValue(pid, out p)) return;
                p.ByeAt = DateTime.UtcNow;
                wasRelay = DuelOn && pid == DuelRelay;
                if (DuelOn) lastDuelEval = DateTime.MinValue;
            }
            Log(L.T("多人网状：成员 ", "Mesh: member ") + pid + L.T(" 告别离开", " said bye") + (wasRelay ? L.T("（是现任对决转发机）", " (current duel relay)") : ""));
        }

        // 对决转发机掉线 / 离开的判定（房主用）：告别过、或超过 RelayLostAfterSec 没收到任何包
        bool PeerGone(MpPeerState p, DateTime now)
        {
            if (p.ByeAt != DateTime.MinValue)
            {
                if ((p.LastAnyRx - p.ByeAt).TotalSeconds > 1.0) p.ByeAt = DateTime.MinValue;     // 告别之后 1 秒以上又有包：回来了
                else return true;
            }
            if (p.LastAnyRx != DateTime.MinValue) return (now - p.LastAnyRx).TotalSeconds >= RelayLostAfterSec;
            return (now - p.BornAt).TotalSeconds >= RelayLostAfterSec * 2;
        }

        void OnMpInfo(object from, string s, int viaPid)
        {
            var q = Kv.Parse(s);
            string pv, nv, mv, lv, ev, ov, hhv, imv;
            int pid, n, max, epoch = 0, owner = 1;
            q.TryGetValue("pid", out pv); int.TryParse(pv, out pid);
            q.TryGetValue("n", out nv); int.TryParse(nv, out n);
            q.TryGetValue("max", out mv); int.TryParse(mv, out max);
            q.TryGetValue("l", out lv);
            if (q.TryGetValue("epoch", out ev)) int.TryParse(ev, out epoch);
            if (q.TryGetValue("owner", out ov)) int.TryParse(ov, out owner);
            bool hh = q.TryGetValue("hidehost", out hhv) && hhv == "1";
            bool im = q.TryGetValue("inmatch", out imv) && imv == "1";
            if (owner <= 0 || (owner == MyPid && MyPid > 0)) return;

            // 只认房主本人发的：直连按地址认出的编号 / 中转头里的发送人必须就是 owner。
            // 例外：刚入房时把"连上的那个地址"占位记成了 1 号；换过房主的房间里那台其实是别的编号，第一次收到它的 4NMP 时改过来。
            int sender = viaPid > 0 ? viaPid : SenderPid(from);
            if (sender == 0) { LogBadClaim("unknown sender owner=" + owner); return; }
            int oldHost;
            bool changed = false, demoted = false;
            lock (sync)
            {
                if (sender != owner)
                {
                    MpPeerState ph;
                    if (viaPid == 0 && !hostConfirmed && !IsHost && sender == HostPid && peers.TryGetValue(sender, out ph))
                    {
                        MpPeerState intro;
                        peers.Remove(sender);
                        if (peers.TryGetValue(owner, out intro))
                        {
                            // 介绍信先到、已经有 owner 那条：把占位那条的连接、可靠通道并过去
                            if (intro.Handle == null) intro.Handle = ph.Handle;
                            if (intro.DirectHandle == null) intro.DirectHandle = ph.DirectHandle;
                            if (intro.AltHandle == null) intro.AltHandle = ph.AltHandle;
                            if (ph.Rel != null) intro.Rel = ph.Rel;
                            if (intro.LastDirectRx < ph.LastDirectRx) intro.LastDirectRx = ph.LastDirectRx;
                        }
                        else
                        {
                            ph.Pid = owner;
                            peers[owner] = ph;
                        }
                        Log(L.T("多人网状：入房时占位的 ", "Mesh: join placeholder ") + sender + L.T(" 号其实是 ", " is actually ") + owner);
                        HostPid = owner;
                        sender = owner;
                    }
                    else
                    {
                        LogBadClaim("owner=" + owner + " sender=" + sender);
                        return;
                    }
                }
                MpPeerState fromP;
                if (peers.TryGetValue(owner, out fromP))
                {
                    if (viaPid > 0) fromP.LastViaRx = DateTime.UtcNow; else fromP.LastDirectRx = DateTime.UtcNow;
                }

                oldHost = HostPid;
                // 房主仲裁：代数大的认；代数相同、编号不比现在的大的认；还没认过房主时第一条照收
                if (epoch > Epoch || (epoch == Epoch && (IsHost ? owner < MyPid : owner <= HostPid)) || (!hostConfirmed && !IsHost && owner == HostPid))
                {
                    if (owner != oldHost || epoch != Epoch)
                    {
                        Log(L.T("多人网状：认房主 owner=", "Mesh: accepted host owner=") + owner + " epoch=" + epoch + " (was " + oldHost + " ep=" + Epoch + ")");
                        changed = true;
                    }
                    HostPid = owner;
                    Epoch = epoch;
                    hostConfirmed = true;
                    waitNewHost = 0;
                    skipCands.Clear();
                    if (IsHost)
                    {
                        IsHost = false;
                        demoted = true;
                        Log(L.T("多人网状：降为普通玩家", "Mesh: demoted to normal player"));
                    }
                    if (oldHost != owner && oldHost != MyPid && peers.ContainsKey(oldHost))
                    {
                        peers.Remove(oldHost);
                        Log(L.T("多人网状：旧房主从路线表删除 ", "Mesh: old host removed ") + oldHost);
                    }
                }
                else
                {
                    Log(L.T("多人网状：忽略旧 / 劣势房主宣称 owner=", "Mesh: ignored stale/inferior host claim owner=") + owner + " ep=" + epoch);
                    if (!IsHost) return;
                }
            }
            if (!changed && !demoted && IsHost)
            {
                // 我是房主、对方的宣称更旧 / 编号更大：再广播一次让它降下来
                BroadcastMpInfo();
                return;
            }
            string bmv;
            int bm;
            if (q.TryGetValue("bm", out bmv) && int.TryParse(bmv, out bm) && bm >= 0 && bm <= 2) HostBridgeMode = bm;
            if ((changed || demoted) && OnHostChanged != null)
            {
                try { OnHostChanged(HostPid, Epoch); } catch (Exception) { }
            }

            if (pid > 0 && MyPid != pid) MyPid = pid;
            HostHidden = hh;
            MpInMatch = im;
            if (n > 0) MpSeen = n;
            string aiCsv;
            q.TryGetValue("ai", out aiCsv);
            SetAiPidsFromHost(aiCsv);
            if (lv != null)
            {
                MpRoster = lv;
                PruneRoster(MpRoster);
            }
            if (max > 0) MpMax = max;
            string mdzl;
            if (q.TryGetValue("mdzl", out mdzl)) ApplyHostMdz(mdzl);     // 旧版房主不带 → 不动，只靠自己听到的

            if (OnRosterUpdated != null)
            {
                try { OnRosterUpdated(MyPid, MpSeen, MpRoster); } catch (Exception) { }
            }
        }

        // 收到一个包：是 M3 的协议 / 已知对端的状态包 → true（不再走原来的 1v1 逻辑）
        public bool HandlePacket(object from, string s, out int got)
        {
            got = 0;
            if (!Enabled || s == null) return false;
            if (TestDropV4 && DropV4(from, s)) return true;     // 测试：成员之间的 IPv4 包全丢
            if (s.StartsWith(WireTag.P + "MPMESH\n", StringComparison.Ordinal)) { OnIntro(from, s); return true; }
            if (s.StartsWith(WireTag.P + "MPHELLO\n", StringComparison.Ordinal)) { OnMeshHello(from, s); return true; }
            if (s.StartsWith(WireTag.P + "MPRELAY\n", StringComparison.Ordinal)) { OnRelay(from, s); return true; }
            if (s.StartsWith(WireTag.P + "MPPING\n", StringComparison.Ordinal)) { OnPing(from, s); return true; }
            if (s.StartsWith(WireTag.P + "MPPONG\n", StringComparison.Ordinal)) { OnPong(from, s); return true; }
            if (s.StartsWith(WireTag.P + "BUPING\n", StringComparison.Ordinal)) { OnBuPing(from, s); return true; }
            if (s.StartsWith(WireTag.P + "BUPONG\n", StringComparison.Ordinal)) { OnBuPong(from, s); return true; }
            if (s.StartsWith(WireTag.P + "BUASK\n", StringComparison.Ordinal)) { OnBuAsk(from, s); return true; }
            if (s.StartsWith(WireTag.P + "BUANS\n", StringComparison.Ordinal)) { OnBuAns(from, s); return true; }
            if (s.StartsWith(WireTag.P + "BUREADY\n", StringComparison.Ordinal)) { OnBuReady(from, s); return true; }
            if (s.StartsWith(WireTag.P + "BUGO\n", StringComparison.Ordinal)) { OnBuGo(from, s); return true; }
            if (s.StartsWith(WireTag.P + "MPDUEL\n", StringComparison.Ordinal)) { OnDuel(from, s, 0); return true; }
            if (s.StartsWith(WireTag.P + "MPNET\n", StringComparison.Ordinal)) { OnNet(from, s, 0); return true; }
            if (s.StartsWith(WireTag.P + "MPREFUSE\n", StringComparison.Ordinal)) { OnRefuse(from, s); return true; }
            if (s.StartsWith(WireTag.P + "MPBYE\n", StringComparison.Ordinal) || s.StartsWith(WireTag.P + "MPBYE\r\n", StringComparison.Ordinal)) { OnBye(from, s, 0); return true; }
            if (s.StartsWith(WireTag.P + "MP\n", StringComparison.Ordinal) || s.StartsWith(WireTag.P + "MP\r\n", StringComparison.Ordinal)) { OnMpInfo(from, s, 0); return true; }
            if (s.StartsWith(WireTag.P + "EV\n", StringComparison.Ordinal) || s.StartsWith(WireTag.P + "ACK\n", StringComparison.Ordinal))
            {
                MpPeerState p = ByHandle(from);
                if (p == null || p.Rel == null) return false;
                if (IsBuHandle(p, from)) p.LastBuRx = DateTime.UtcNow; else p.LastDirectRx = DateTime.UtcNow;
                if (s.StartsWith(WireTag.P + "EV\n", StringComparison.Ordinal)) p.Rel.OnEvents(s); else p.Rel.OnAck(s);
                return true;
            }
            if (s.IndexOf("seq=", StringComparison.Ordinal) < 0) return false;
            var d = Kv.Parse(s);
            string src;
            int pid, seq;
            if (!d.TryGetValue("src", out src) || !int.TryParse(src, out pid) || pid == MyPid) return false;
            int senderPid = SenderPid(from);
            bool hasAi = IsAiPid(pid);
            bool fromHostForAi = hasAi && senderPid > 0 && senderPid == HostPid;
            if (hasAi && !fromHostForAi) return true;     // 非房主伪造 AI 状态包：直接丢弃，不落入旧逻辑
            MpPeerState peer = fromHostForAi ? Get(HostPid) : Get(pid);
            if (peer == null || (!fromHostForAi && peer.IsAi)) return false;
            bool fromBridge = IsBuHandle(peer, from);
            bool fromPeer = fromBridge || (from != null && (from.Equals(peer.SendHandle) || from.Equals(peer.Handle) || from.Equals(peer.AltHandle)));
            if (!fromPeer) return false;       // 只认这个对端的直连地址或已升级的桥接句柄；经中转的包走 4NMPRELAY
            if (!fromBridge && Blocked(pid, MyPid)) return true;
            if (fromBridge) { peer.LastBuRx = DateTime.UtcNow; }
            else { peer.LastDirectRx = DateTime.UtcNow; peer.DirectVerified = true; }      // 能收到它带正确签名的状态包 = 直连是通的（握手可能丢了一个）
            if (d.TryGetValue("seq", out src) && int.TryParse(src, out seq) && Dedupe != null && !Dedupe(pid.ToString(), seq)) { MpDup++; return true; }
            peer.LastStateAt = DateTime.UtcNow;
            if (fromBridge) peer.BuRxCount++; else peer.DirectRxCount++;
            MpRx++;
            string taggedRx = StripSrc(s) + "src=" + pid + "\n";
            EmitDelivered(pid, taggedRx, hasAi);
            if (DuelOn) { int sq2; string sv2; DuelAfterRx(pid, d.TryGetValue("seq", out sv2) && int.TryParse(sv2, out sq2) ? sq2 : -1, taggedRx); }
            got = 1;
            return true;
        }

        // 房主 / 客户端：已经登记过的玩家状态包（直连收到，调用方已确认来源）
        public bool DeliverState(int pid, string body)
        {
            MpPeerState p = Get(pid);
            if (p == null) return false;
            var d = Kv.Parse(body);
            string v;
            int seq;
            if (!d.TryGetValue("seq", out v) || !int.TryParse(v, out seq)) return false;
            if (Dedupe != null && !Dedupe(pid.ToString(), seq)) { MpDup++; return true; }
            p.LastStateAt = DateTime.UtcNow;
            p.DirectRxCount++;
            MpRx++;
            string taggedDs = StripSrc(body) + "src=" + pid + "\n";
            EmitDelivered(pid, taggedDs, p.IsAi || IsAiPid(pid));
            if (DuelOn) DuelAfterRx(pid, seq, taggedDs);
            return true;
        }

        // 房主 4NMP 里的名单变了：客户端把已经离开的玩家从路线表里删掉
        public void PruneRoster(string roster)
        {
            var keep = new HashSet<int>();
            keep.Add(HostPid);
            if (!string.IsNullOrEmpty(roster))
                foreach (var part in roster.Split(','))
                {
                    int c = part.IndexOf(':');
                    int pid;
                    string pnick = c > 0 ? part.Substring(c + 1) : "";
                    if (int.TryParse(c > 0 ? part.Substring(0, c) : part, out pid))
                    {
                        keep.Add(pid);
                        if (!string.IsNullOrEmpty(pnick))
                        {
                            MpPeerState np;
                            lock (sync) { if (peers.TryGetValue(pid, out np) && string.IsNullOrEmpty(np.Nick)) np.Nick = pnick; }
                        }
                    }
                }
            List<int> drop = new List<int>();
            lock (sync)
            {
                foreach (var kv in peers) if (kv.Key != HostPid && !keep.Contains(kv.Key)) drop.Add(kv.Key);
                for (int i = 0; i < drop.Count; i++)
                {
                    ClosePeerBridge(peers[drop[i]]);
                    peers.Remove(drop[i]);
                }
            }
            for (int i = 0; i < drop.Count; i++) Log(L.T("多人网状：玩家走了，路线删掉 ", "Mesh: peer gone, route removed ") + drop[i]);
        }

        MpPeerState ByHandle(object h)
        {
            if (h == null) return null;
            lock (sync)
            {
                foreach (var p in peers.Values)
                    if (h.Equals(p.SendHandle) || h.Equals(p.Handle) || h.Equals(p.AltHandle) || IsBuHandle(p, h)) return p;
            }
            return null;
        }

        void OnIntro(object from, string s)
        {
            var d = Kv.Parse(s);
            string v;
            int pid;
            if (!d.TryGetValue("peer", out v) || !int.TryParse(v, out pid) || pid <= 0 || pid == MyPid) return;
            if (from != null)
            {
                int sender = SenderPid(from);
                if (sender == 0 || sender != HostPid) { LogBadClaim("intro sender=" + sender); return; }
            }
            string addr, token, lan, v6;
            d.TryGetValue("addr", out addr);
            d.TryGetValue("lan", out lan);
            d.TryGetValue("token", out token);
            d.TryGetValue("v6", out v6);
            ulong sid = 0UL;
            if (d.TryGetValue("sid", out v)) ulong.TryParse(v, out sid);
            int r = AddIntro(pid, addr, sid, token, null, lan, d.TryGetValue("hid", out v) && v == "1", v6 ?? "");
            if (r > 0) Log(L.T("多人网状：介绍对端 ", "Mesh: introduced peer ") + pid + " " + (addr ?? "") + (sid != 0 ? " sid=" + sid : "") + (string.IsNullOrEmpty(lan) ? "" : " lan=" + lan)
                           + (string.IsNullOrEmpty(v6) ? "" : " v6=" + v6));
        }

        void OnMeshHello(object from, string s)
        {
            var d = Kv.Parse(s);
            string v, token;
            int pid;
            if (!d.TryGetValue("pid", out v) || !int.TryParse(v, out pid) || pid == MyPid) return;
            d.TryGetValue("token", out token);
            MpPeerState p = Get(pid);
            if (p == null) { Log(L.T("多人网状：握手来自没介绍的编号 ", "Mesh: hello from unintroduced id ") + pid); return; }
            if (string.IsNullOrEmpty(p.Token) || p.Token != token)
            {
                Log(L.T("多人网状：握手口令不对，忽略 ", "Mesh: hello token mismatch, ignored ") + pid);
                return;
            }
            if (d.ContainsKey("mdz")) p.MdzOk = true; if (d.ContainsKey("mda")) p.MdaOk = true;
            // 双栈：房主可能把 A 按 IPv4 介绍给 B、把 B 按 IPv6 介绍给 A。握手来源和记着的地址不是同一族时不换
            // （换了两边会对调，最后都往对方不认的地址发，状态包全被丢），只记成"也认这个地址"。同一族照旧换（NAT 换了端口靠这个）
            var fe = from as IPEndPoint;
            var he = p.Handle as IPEndPoint;
            if (fe != null && he != null && fe.AddressFamily != he.AddressFamily)
            {
                // 另一族的握手只说明那条线通，不算当前这条（以前也把 DirectVerified 设上，当前那族不通时要等 2.5 秒才发现）
                p.AltHandle = from;
                bool firstAlt = !p.AltVerified;
                p.AltVerified = true;
                p.LastAltRx = DateTime.UtcNow;
                if (firstAlt) Log(L.T("多人网状：", "Mesh: ") + pid + L.T(" 的另一条线（", " alternate path (") + FamName(fe) + L.T("）也通了", ") is up"));
                return;
            }
            p.Handle = from;
            bool first = !p.DirectVerified;
            p.DirectVerified = true;
            p.LastDirectRx = DateTime.UtcNow;
            p.DirectMiss = 0;
            if (first) Log(L.T("多人网状：直连打通 ", "Mesh: direct link up ") + pid + (fe != null ? "（" + FamName(fe) + "）" : ""));
        }

        void SendHello(MpPeerState p, DateTime now)
        {
            if (p.Handle == null || string.IsNullOrEmpty(p.Token) || MyPid <= 0) return;
            if (Blocked(MyPid, p.Pid)) return;
            p.LastHelloAt = now;
            TrySend(p.Handle, WireTag.P + "MPHELLO\npid=" + MyPid + "\ntoken=" + p.Token + "\nmdz=1\nmda=1\n");     // 我会拼省流包
        }

        // ---- 另一个地址族（IPv4 ↔ IPv6）的候选线：。
        // 房主介绍时带 v6=（对方握手里自己报的公网 IPv6 端点），两边同时往当前这条（Handle）和另一族（AltHandle）发握手 / 探测；
        // 连续 AltPickTicks 秒另一族明显更快（快 AltSwitchMs 以上）或当前这条断了，就对调 Handle / AltHandle。两边各自判，
        // 收包两条都认（ByHandle 认 AltHandle）。经第三人中转 / 桥接照旧接在后面（看的是对调后的 DirectRtt / LastDirectRx）。
        public const double AltSwitchMs = 20.0;
        public const int AltPickTicks = 2;
        public string MyV6Addr = "";     // 我自己的公网 IPv6 端点（房主介绍自己时带上；没有 = ""）

        static bool UsableV6(string a)
        {
            if (string.IsNullOrEmpty(a)) return false;
            var ep = Addr.ParseEp(a);
            return ep != null && ep.AddressFamily == AddressFamily.InterNetworkV6 && ep.Port > 0 && !ep.Address.IsIPv6LinkLocal;
        }

        // 测试钩子（FORNET_=1，只在客机上用）：成员之间（不含房主）从 IPv4 来的网状包全丢 = 模拟"IPv4 这条线不通"
        public static readonly bool TestDropV4 = WireTag.Env("TEST_DROPV4") == "1";
        bool DropV4(object from, string s)
        {
            if (IsHost) return false;
            var fe = from as IPEndPoint;
            if (fe == null || fe.AddressFamily != AddressFamily.InterNetwork) return false;
            var sp = ByHandle(from);
            int spid = sp != null ? sp.Pid : 0;
            if (spid == 0 && s.StartsWith(WireTag.P + "MPHELLO", StringComparison.Ordinal))
            {
                string pv;
                int pp;
                if (Kv.Parse(s).TryGetValue("pid", out pv) && int.TryParse(pv, out pp)) spid = pp;
            }
            return spid != 0 && spid != HostPid;
        }

        static string FamName(IPEndPoint e) { return e != null && e.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4"; }

        // 这个来源是"另一条线"，而不是当前这条
        static bool IsAltOnly(MpPeerState p, object from)
        {
            return from != null && p.AltHandle != null && from.Equals(p.AltHandle) && !from.Equals(p.Handle) && !from.Equals(p.SendHandle);
        }

        // 纯逻辑（测试用）：另一条线这一秒算不算"明显更好"
        public static bool AltBetter(double altRtt, double curRtt, bool curDead)
        {
            if (altRtt < 0) return false;
            if (curDead) return true;
            return curRtt >= 0 && altRtt + AltSwitchMs < curRtt;
        }

        void AltStep(MpPeerState p, DateTime now)
        {
            if (MyPid <= 0 || p.DirectHandle != null || p.Hidden || HideMe || p.Token.Length == 0) return;
            // 只在玩家和玩家之间换：和房主那条是联机程序的控制连接（房主按 IPv4 端点认人），不动
            if (IsHost || p.Pid == HostPid) return;
            var he = p.Handle as IPEndPoint;
            if (he == null) return;
            if (p.AltHandle == null && he.AddressFamily == AddressFamily.InterNetwork && UsableV6(p.V6Addr))
                p.AltHandle = Addr.ParseEp(p.V6Addr);
            var ae = p.AltHandle as IPEndPoint;
            if (ae == null || ae.AddressFamily == he.AddressFamily) return;
            if (ae.AddressFamily == AddressFamily.InterNetworkV6 && !NetBind.V6) return;      // 本机关了 IPv6
            if (Blocked(MyPid, p.Pid)) return;
            if (!p.AltVerified && (now - p.LastAltHelloAt).TotalSeconds >= 2.0)
            {
                p.LastAltHelloAt = now;
                TrySend(ae, WireTag.P + "MPHELLO\npid=" + MyPid + "\ntoken=" + p.Token + "\nmdz=1\nmda=1\n");     // 我会拼省流包
            }
            if ((now - p.LastAltPingAt).TotalSeconds >= PingEvery)
            {
                p.LastAltPingAt = now;
                TrySend(ae, WireTag.P + "MPPING\nt=" + (long)(Link.UnixNow() * 1000) + "\nalt=1\nmdz=1\nmda=1\n");
            }
            if (p.AltRtt >= 0 && (now - p.LastAltRx).TotalSeconds > 2.5) p.AltRtt = -1;
            if ((now - p.LastAltPickAt).TotalSeconds < 1.0) return;
            p.LastAltPickAt = now;
            if ((now - p.BornAt).TotalSeconds < 5.0) { p.AltBetterTicks = 0; return; }     // 刚认识 5 秒内先测，不换（头几个往返偏高，和 M4 的 5 秒规则一致）
            bool curDead = !p.DirectVerified || p.DirectRtt < 0 || (now - p.LastDirectRx).TotalSeconds > 3.0;
            if (AltBetter(p.AltRtt, p.DirectRtt, curDead)) p.AltBetterTicks++; else p.AltBetterTicks = 0;
            if (p.AltBetterTicks < AltPickTicks) return;
            // 对调：另一族成了当前这条；原来那条留着当候选，接着测，变好了还能换回来
            object oldH = p.Handle;
            double oldRtt = p.DirectRtt;
            DateTime oldRx = p.LastDirectRx;
            bool oldOk = p.DirectVerified && !curDead;
            p.Handle = p.AltHandle;
            p.AltHandle = oldH;
            p.DirectRtt = p.AltRtt;
            p.DirectVerified = true;
            p.LastDirectRx = now;
            p.DirectMiss = 0;
            p.DirectLoss = 0.1;
            p.ResetWin();
            p.AltRtt = oldOk ? oldRtt : -1;
            p.AltVerified = oldOk;
            p.LastAltRx = oldRx;
            p.AltBetterTicks = 0;
            Log(L.T("多人网状：和 ", "Mesh: to ") + p.Pid + L.T(" 改走 ", " now via ") + FamName(p.Handle as IPEndPoint)
                + L.T("（", " (") + Kv.F(p.DirectRtt, "0") + " ms" + (oldOk ? L.T("，原来 ", ", was ") + Kv.F(oldRtt, "0") + " ms" : L.T("，原来那条不通", ", previous path down")) + L.T("）", ")"));
        }

        double HostRxAge(DateTime now)
        {
            if (HostPid <= 0) return 0.0;
            MpPeerState hostP = Get(HostPid);
            if (hostP == null) return (now - bornAt).TotalSeconds;
            if (hostP.LastAnyRx == DateTime.MinValue) return (now - hostP.BornAt).TotalSeconds;
            return (now - hostP.LastAnyRx).TotalSeconds;
        }

        void OnPing(object from, string s)
        {
            var d = Kv.Parse(s);
            string t;
            if (!d.TryGetValue("t", out t)) return;
            MpPeerState p = ByHandle(from);
            DateTime now = DateTime.UtcNow;
            if (p != null)
            {
                if (d.ContainsKey("mdz")) p.MdzOk = true; if (d.ContainsKey("mda")) p.MdaOk = true;
                if (IsBuHandle(p, from)) p.LastBuRx = now;
                else if (IsAltOnly(p, from)) p.LastAltRx = now;     // 另一条线的探测不算当前这条
                else p.LastDirectRx = now;
                string hageStr;
                if (d.TryGetValue("hage", out hageStr))
                {
                    double hageVal;
                    if (double.TryParse(hageStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hageVal))
                    {
                        p.PeerHostAge = hageVal;
                        p.PeerHostAgeAt = now;
                    }
                }
            }
            if (p != null && !IsBuHandle(p, from) && Blocked(p.Pid, MyPid)) return;
            double myHage = HostRxAge(now);
            TrySend(from, WireTag.P + "MPPONG\nt=" + t + "\nhage=" + Kv.F(myHage, "0.0") + "\nmdz=1\nmda=1\n");
        }

        void OnPong(object from, string s)
        {
            MpPeerState p = ByHandle(from);
            if (p == null || IsBuHandle(p, from)) return;
            if (Blocked(p.Pid, MyPid)) return;
            var d = Kv.Parse(s);
            DateTime now = DateTime.UtcNow;
            if (d.ContainsKey("mdz")) p.MdzOk = true; if (d.ContainsKey("mda")) p.MdaOk = true;
            if (IsAltOnly(p, from))      // 另一条线的往返
            {
                string ta;
                long ta0;
                if (!d.TryGetValue("t", out ta) || !long.TryParse(ta, out ta0)) return;
                long artt = (long)(Link.UnixNow() * 1000) - ta0;
                if (artt < 0 || artt > 10000) return;
                bool firstAlt = !p.AltVerified;
                p.AltVerified = true;
                p.AltRtt = p.AltRtt < 0 ? artt : p.AltRtt * 0.8 + artt * 0.2;
                p.LastAltRx = now;
                if (firstAlt) Log(L.T("多人网状：", "Mesh: ") + p.Pid + L.T(" 的另一条线（", " alternate path (") + FamName(from as IPEndPoint) + L.T("）也通了", ") is up"));
                return;
            }
            string hageStr;
            if (d.TryGetValue("hage", out hageStr))
            {
                double hageVal;
                if (double.TryParse(hageStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hageVal))
                {
                    p.PeerHostAge = hageVal;
                    p.PeerHostAgeAt = now;
                }
            }
            string t;
            long t0;
            if (!d.TryGetValue("t", out t) || !long.TryParse(t, out t0)) return;
            long rtt = (long)(Link.UnixNow() * 1000) - t0;
            if (rtt < 0 || rtt > 10000) return;
            if (p.DirectRtt < 0)
            {
                // 第一次测到直连 RTT：连接建立前发出去的 ping 不能算丢包（否则刚连上就判成 50% 丢包、
                // 白白双发一两秒；桥接平台第一次 ping 常常排在"正在连接"的句柄里）
                p.DirectLoss = 0.1;
                p.DirectMaxLoss = 0;
                p.DirectMiss = 0;
                p.ResetWin();
            }
            p.DirectVerified = true;
            p.DirectRtt = p.DirectRtt < 0 ? rtt : p.DirectRtt * 0.8 + rtt * 0.2;
            p.DirectRecvWin++;
            p.DirectRecvAll++;
            p.LastDirectRx = now;
            p.DirectMiss = 0;
        }

        void OnRelay(object from, string s)
        {
            int cut = s.IndexOf("\n--\n", StringComparison.Ordinal);
            if (cut < 0) return;
            string head = s.Substring(0, cut);
            string payload = s.Substring(cut + 4);
            var d = Kv.Parse(head);
            string v;
            int fromPid, toPid, relayPid, ttl;
            string kind;
            if (!d.TryGetValue("from", out v) || !int.TryParse(v, out fromPid)) return;
            if (!d.TryGetValue("to", out v) || !int.TryParse(v, out toPid)) return;
            if (!d.TryGetValue("kind", out kind)) return;
            d.TryGetValue("relay", out v); if (!int.TryParse(v, out relayPid)) relayPid = 0;
            d.TryGetValue("ttl", out v); if (!int.TryParse(v, out ttl)) ttl = 1;
            if (toPid == MyPid)
            {
                MpPeerState src = Get(fromPid);
                if (src == null) return;
                if (kind == "state" || kind == "dstate")
                {
                    src.LastViaRx = DateTime.UtcNow;
                    var sd = Kv.Parse(payload);
                    string sv, sq;
                    int spid, seq;
                    int outPid = fromPid;
                    bool outIsAi = IsAiPid(fromPid);
                    if (sd.TryGetValue("src", out sv) && int.TryParse(sv, out spid) && spid > 0 && spid != fromPid)
                    {
                        if (fromPid == HostPid && IsAiPid(spid)) { outPid = spid; outIsAi = true; }
                        else return;     // 非房主不得经中转伪造他人的 src=
                    }
                    if (outIsAi && fromPid != HostPid) return;
                    // 去重键是 outPid + 原包的 seq；以前这里误把 src=（编号，恒定）当成 seq，导致经中转的包
                    // 只收第一份、后面全被当重复丢掉（M4 的 NAT 测试抓到的：rxv=1、mpdup 一直涨）
                    if (sd.TryGetValue("seq", out sq) && int.TryParse(sq, out seq) && Dedupe != null
                        && !Dedupe(outPid.ToString(), seq)) { MpDup++; return; }
                    src.LastStateAt = DateTime.UtcNow;
                    src.ViaRxCount++;
                    MpRx++;
                    string taggedV = StripSrc(payload) + "src=" + outPid + "\n";
                    EmitDelivered(outPid, taggedV, outIsAi);
                    // 新版 1v1：转发机收到选手的包（直连 / 经第三人都算；dstate = 已经是转发机转过来的，不再转）
                    if (DuelOn) { int sq3; string sv3; DuelAfterRx(outPid, sd.TryGetValue("seq", out sv3) && int.TryParse(sv3, out sq3) ? sq3 : -1, taggedV, kind == "state"); }
                }
                else if (kind == "rel")
                {
                    src.LastViaRx = DateTime.UtcNow;
                    if (src.Rel != null) src.Rel.OnEvents(payload);
                }
                else if (kind == "ctrl")
                {
                    // 经中转的控制包只收房主名单 / 告别两种；发送人按中转头里的 from 认
                    src.LastViaRx = DateTime.UtcNow;
                    if (payload.StartsWith(WireTag.P + "MPBYE\n", StringComparison.Ordinal)) OnBye(null, payload, fromPid);
                    else if (payload.StartsWith(WireTag.P + "MP\n", StringComparison.Ordinal)) OnMpInfo(null, payload, fromPid);
                    else if (payload.StartsWith(WireTag.P + "MPDUEL\n", StringComparison.Ordinal)) OnDuel(null, payload, fromPid);
                    else if (payload.StartsWith(WireTag.P + "MPNET\n", StringComparison.Ordinal)) OnNet(null, payload, fromPid);
                }
                else if (kind == "vp")
                {
                    src.LastViaRx = DateTime.UtcNow;
                    // 对方经中转测延迟：原样回，回程还走同一个中转
                    int back = relayPid != 0 ? relayPid : fromPid;
                    MpPeerState via = Get(back);
                    if (via != null && via.SendHandle != null)
                        TrySend(via.SendHandle, Envelope(MyPid, fromPid, "pr", back, 1, payload));
                }
                else if (kind == "pr")
                {
                    src.LastViaRx = DateTime.UtcNow;
                    var pd = Kv.Parse(payload);
                    string t;
                    long t0;
                    if (!pd.TryGetValue("t", out t) || !long.TryParse(t, out t0)) return;
                    long rtt = (long)(Link.UnixNow() * 1000) - t0;
                    if (rtt < 0 || rtt > 20000) return;
                    int cand = relayPid;
                    if (cand == 0) return;
                    MpProbe pr;
                    lock (sync)
                    {
                        if (!src.Probes.TryGetValue(cand, out pr)) { pr = new MpProbe(); pr.RelayPid = cand; src.Probes[cand] = pr; }
                    }
                    pr.Rtt = pr.Rtt < 0 ? rtt : pr.Rtt * 0.8 + rtt * 0.2;
                    pr.Recv++;
                    pr.LastPong = DateTime.UtcNow;
                    pr.Miss = 0;
                }
                return;
            }
            // 我是中转：ttl 用完就不转（只转一跳），每人最多帮 MaxRelayPairs 对
            if (ttl <= 0) return;
            if (kind == "state" || kind == "dstate")
            {
                if (IsAiPid(fromPid) && fromPid != HostPid) return;
                if (fromPid != HostPid)
                {
                    var sd2 = Kv.Parse(payload);
                    string sv2; int sp2;
                    if (sd2.TryGetValue("src", out sv2) && int.TryParse(sv2, out sp2) && sp2 != fromPid && IsAiPid(sp2)) return;
                }
            }
            MpPeerState to = Get(toPid);
            if (to == null || to.IsAi || to.SendHandle == null) return;
            if (Blocked(MyPid, toPid)) return;
            if (kind == "state") MpFwd++;
            // 中转上限只算真正转发的状态 / 可靠事件；ping 探测不占名额，否则几对探测就把名额占满
            if (kind == "state" || kind == "rel" || kind == "ctrl")
            {
                // 名额按"一对人"算，不按方向算。以前 key 是 from>to：B→C 和 C→B 各占一个名额，
                // 4 人房里再帮一对（比如 host→B）就满 3 个，C→B 会被 max 限制静默丢掉，表现成
                // "B 一直收不到经中转的 C"（test_mp_mesh_steam 的 rxv=0 偶发就是这么来的）。
                int lo = Math.Min(fromPid, toPid), hi = Math.Max(fromPid, toPid);
                string key = lo + "-" + hi;
                lock (sync)
                {
                    var dead = new List<string>();
                    foreach (var kv in relayUse) if ((DateTime.UtcNow - kv.Value).TotalSeconds > 5.0) dead.Add(kv.Key);
                    for (int i = 0; i < dead.Count; i++) relayUse.Remove(dead[i]);
                    MpPeerState fp = Get(fromPid);
                    bool hidPair = IsHost && ((fp != null && fp.Hidden) || to.Hidden);     // 隐藏 IP 的人只能经房主，房主不限名额
                    if (!hidPair && !relayUse.ContainsKey(key) && relayUse.Count >= MaxRelayPairs) return;
                    relayUse[key] = DateTime.UtcNow;
                }
            }
            string fwd = Envelope(fromPid, toPid, kind, MyPid, ttl - 1, payload);
            TrySend(to.SendHandle, fwd);
        }

        // 每秒：握手重发、直连 / 候选中转测延迟、选路、更新丢包
        public void Tick(DateTime now)
        {
            if (!Enabled) return;
            if (CanAutoBridgeCfg && !Bridge.Ready && (now - lastBuInitAt).TotalSeconds >= 2.0)
            {
                lastBuInitAt = now;
                Bridge.Init();
            }
            List<MpPeerState> list;
            lock (sync) list = new List<MpPeerState>(peers.Values);
            bool newWin = (now - lastWin).TotalSeconds >= 1.0;

            // 房主掉线超时检测：
            // 快速判定：我 2.5 秒没收到房主的包，而且至少一位别的成员也报告收不到（>= 2.5 秒）就当房主离开；
            // 只有自己一个人收不到按原来的 6 秒。
            if (!IsHost && HostPid > 0 && MyPid > 0)
            {
                MpPeerState hostP = Get(HostPid);
                if (hostP != null)
                {
                    double age = (hostP.LastAnyRx != DateTime.MinValue) ? (now - hostP.LastAnyRx).TotalSeconds : (now - hostP.BornAt).TotalSeconds;
                    bool timedOut = false;
                    if (hostP.LastAnyRx != DateTime.MinValue)
                    {
                        if (age >= HostLostAfter)
                        {
                            timedOut = true;
                        }
                        else if (age >= HostLostFastAfter)
                        {
                            bool otherReports = false;
                            foreach (var kv in peers)
                            {
                                if (kv.Key == HostPid || kv.Key <= 0 || kv.Value.IsAi) continue;
                                MpPeerState other = kv.Value;
                                if (other.PeerHostAgeAt != DateTime.MinValue && (now - other.PeerHostAgeAt).TotalSeconds <= 3.0)
                                {
                                    double otherAge = other.PeerHostAge + (now - other.PeerHostAgeAt).TotalSeconds;
                                    if (otherAge >= HostLostFastAfter)
                                    {
                                        otherReports = true;
                                        break;
                                    }
                                }
                            }
                            if (otherReports)
                            {
                                timedOut = true;
                            }
                        }
                    }
                    else if ((now - hostP.BornAt).TotalSeconds >= HostLostAfter)
                    {
                        timedOut = true;
                    }

                    if (timedOut)
                    {
                        MigrateHost("timeout " + Kv.F(age, "0.0") + "s");
                    }
                }
                else if ((now - bornAt).TotalSeconds >= HostLostAfter)
                {
                    MigrateHost("missing");
                }
            }

            for (int i = 0; i < list.Count; i++)
            {
                MpPeerState p = list[i];
                if (p.IsAi) continue;
                if (RelayOnly(p))     // 只经房主：不解析地址、不握手、不直连测延迟（这些都会把我的地址给对方）
                {
                    if (p.Route != PeerRoute.Via || p.RelayPid != HostPid) { p.Route = PeerRoute.Via; p.RelayPid = HostPid; p.RouteWhy = "hideip"; }
                    if (p.Rel != null) p.Rel.Tick(now);
                    continue;
                }
                // M4：房主看到的对方端点一直打不通（常见于两边在同一个局域网、房主在外网：房主看到的是公网端点），
                // 4 秒后改试对方自己报的局域网端点；12 秒还没通再换回公网端点，后台一直试。
                if (!p.DirectVerified && !string.IsNullOrEmpty(p.LanAddr) && p.LanAddr != p.Addr)
                {
                    double age = (now - p.BornAt).TotalSeconds;
                    bool want = age >= 4.0 && age < 12.0;
                    if (want != p.PreferLan)
                    {
                        p.PreferLan = want;
                        p.Handle = null;
                        p.LastEnsureAt = DateTime.MinValue;
                        p.LastHelloAt = DateTime.MinValue;
                        p.DirectRtt = -1;
                        p.ResetWin();
                    }
                }
                if (p.Handle == null && EnsureHandle != null)
                {
                    if ((now - p.LastEnsureAt).TotalSeconds >= 1.0)
                    {
                        p.LastEnsureAt = now;
                        string use = p.PreferLan && !string.IsNullOrEmpty(p.LanAddr) ? p.LanAddr : p.Addr;
                        try { object h = EnsureHandle(p.Pid, use, p.BridgeId); if (h != null) p.Handle = h; } catch (Exception) { }
                    }
                }
                if (MyPid > 0 && !p.DirectVerified && (now - p.LastHelloAt).TotalSeconds >= HelloEvery)
                    SendHello(p, now);
                AltStep(p, now);
                if (p.SendHandle == null) continue;
                double curHostAge = HostRxAge(now);
                double pingInterval = (!IsHost && HostPid > 0 && curHostAge >= 2.0) ? 0.5 : PingEvery;
                if ((now - p.LastPingAt).TotalSeconds >= pingInterval)
                {
                    p.LastPingAt = now;
                    if (!Blocked(MyPid, p.Pid))
                    {
                        p.PingRid++;
                        long t = (long)(Link.UnixNow() * 1000);
                        TrySend(p.SendHandle, WireTag.P + "MPPING\nt=" + t + "\nrid=" + p.PingRid + "\nhage=" + Kv.F(curHostAge, "0.0") + "\nmdz=1\nmda=1\n");
                        p.DirectSentWin++; p.DirectSentAll++;
                    }
                    else
                    {
                        p.DirectRtt = -1; p.DirectLoss = 1.0;
                        p.DirectMiss++;
                        if (p.DirectMiss > p.DirectMaxLoss) p.DirectMaxLoss = p.DirectMiss;
                    }
                }
                if (p.DirectVerified && (now - p.LastDirectRx).TotalSeconds > 2.5 && p.DirectRtt >= 0)
                {
                    p.DirectRtt = -1; p.DirectLoss = 1.0;
                }
                if (newWin && p.DirectVerified)
                {
                    double loss = p.DirectSentWin > 0 ? 1.0 - (double)p.DirectRecvWin / p.DirectSentWin : (p.DirectRtt < 0 ? 1.0 : 0.0);
                    if (loss < 0) loss = 0;
                    p.DirectLoss = p.DirectLoss * 0.5 + loss * 0.5;
                    if (loss >= 0.5) { p.DirectMiss++; if (p.DirectMiss > p.DirectMaxLoss) p.DirectMaxLoss = p.DirectMiss; } else p.DirectMiss = 0;
                    p.ResetWin();
                }
                if ((now - p.LastProbeAt).TotalSeconds >= ProbeEvery)
                {
                    p.LastProbeAt = now;
                    Probe(now, p, list);
                }
                BuTick(p, now);
                if (newWin) SelectRoute(p, now, list);
                if (p.Rel != null) p.Rel.Tick(now);
            }
            if (newWin) lastWin = now;
            if (IsHost)
            {
                SendIntros(now, false);
                if (hostInMatchDirty || (now - lastMpBroadcast).TotalSeconds >= 2.0)
                {
                    hostInMatchDirty = false;
                    lastMpBroadcast = now;
                    BroadcastMpInfo();
                }
            }
            if (DuelOn || (IsHost && duelOffLeft > 0)) DuelTick(now);
        }

        void Probe(DateTime now, MpPeerState p, List<MpPeerState> list)
        {
            long t = (long)(Link.UnixNow() * 1000);
            for (int i = 0; i < list.Count; i++)
            {
                MpPeerState c = list[i];
                if (c.Pid == p.Pid || c.Pid == MyPid) continue;
                if (c.SendHandle == null || (!c.DirectVerified && c.Pid != HostPid && !IsHost)) continue;
                if (RelayOnly(c)) continue;
                if (Blocked(MyPid, c.Pid)) continue;
                MpProbe pr;
                lock (sync)
                {
                    if (!p.Probes.TryGetValue(c.Pid, out pr)) { pr = new MpProbe(); pr.RelayPid = c.Pid; p.Probes[c.Pid] = pr; }
                }
                if ((now - pr.LastSend).TotalSeconds < ProbeEvery) continue;
                pr.LastSend = now;
                pr.Sent++;
                string body = Envelope(MyPid, p.Pid, "vp", c.Pid, 1, "t=" + t + "\n");
                TrySend(c.SendHandle, body);
            }
            List<MpProbe> ps;
            lock (sync) ps = new List<MpProbe>(p.Probes.Values);
            for (int i = 0; i < ps.Count; i++)
            {
                MpProbe pr = ps[i];
                if ((now - pr.LastPong).TotalSeconds > 2.5)
                {
                    pr.Miss++;
                    if (pr.Miss > pr.MaxLoss) pr.MaxLoss = pr.Miss;
                }
                if (pr.Sent > 0)
                {
                    double loss = 1.0 - (double)pr.Recv / pr.Sent;
                    if (loss < 0) loss = 0;
                    pr.Loss = pr.Loss * 0.5 + loss * 0.5;
                    pr.Sent = 0; pr.Recv = 0;
                }
                else if ((now - pr.LastPong).TotalSeconds > 3.0) pr.Loss = 1.0;
            }
        }

        void SelectRoute(MpPeerState p, DateTime now, List<MpPeerState> list)
        {
            int best = 0;
            double bestScore = double.MaxValue;
            List<MpProbe> probes;
            lock (sync) probes = new List<MpProbe>(p.Probes.Values);
            for (int i = 0; i < probes.Count; i++)
            {
                MpProbe pr = probes[i];
                if (pr.Rtt < 0 || pr.Miss >= 2) continue;
                double score = pr.Rtt + pr.Loss * 120.0;
                if (pr.RelayPid != HostPid) score -= 10.0;
                if (score < bestScore) { bestScore = score; best = pr.RelayPid; }
            }
            bool directUsable = p.DirectVerified && p.DirectRtt >= 0 && p.DirectLoss < 0.5;
            double directScore = p.DirectRtt + p.DirectLoss * 120.0;
            if (!BridgePrimary && p.Route == PeerRoute.Bridge)
            {
                p.DualSend = false;
                p.BetterTicks = 0;
                p.RouteWhy = "B:" + Sc(p.BuBridgeRtt) + ":" + Sc(directScore) + ":0:" + p.BuRxCount;
                return;
            }
            p.RouteWhy = (directUsable ? "D" : "x") + ":" + Sc(directScore) + ":" + Sc(bestScore) + ":" + best + ":" + p.BetterTicks;
            if (!directUsable && best != 0)
            {
                // 刚连上时先等直连 ping 回来（连续 3 秒没回才算丢了），别一上来就绕中转。
                // M4：跨 NAT 的一对可能从来没通过（对称 NAT），握手 / ping 都没回：5 秒后按约定走经第三人 / 房主中转。
                bool neverUp = !p.DirectVerified && (now - p.BornAt).TotalSeconds >= 5.0;
                if (neverUp || p.DirectMiss >= 3 || (p.DirectRtt >= 0 && p.DirectLoss >= 0.5))
                {
                    if (p.Route != PeerRoute.Via || p.RelayPid != best)
                    {
                        p.Route = PeerRoute.Via; p.RelayPid = best; p.BetterTicks = 0;
                        Log(L.T("多人选路：改经 ", "Mesh route: via ") + best + L.T(" 中转（直连丢了，对端 ", " (direct lost, peer ") + p.Pid + "）");
                    }
                }
            }
            else if (directUsable)
            {
                if (p.Route == PeerRoute.Direct)
                {
                    if (best != 0 && bestScore + SwitchMargin < directScore)
                    {
                        p.BetterTicks++;
                        if (p.BetterTicks >= SwitchTicks)
                        {
                            p.Route = PeerRoute.Via; p.RelayPid = best; p.BetterTicks = 0;
                            Log(L.T("多人选路：改经 ", "Mesh route: via ") + best + L.T(" 中转（更快）", " (faster)"));
                        }
                    }
                    else p.BetterTicks = 0;
                }
                else
                {
                    bool relayDead = p.RelayPid == 0 || !p.Probes.ContainsKey(p.RelayPid) || p.Probes[p.RelayPid].Miss >= 2;
                    bool viaBetter = best != 0 && bestScore + SwitchMargin < directScore;
                    // 直连能用时默认回直连：只有中转明显更好（快 40ms 往返 = 单程 20ms 以上）才留在中转
                    if (relayDead || !viaBetter)
                    {
                        p.BetterTicks++;
                        if (p.BetterTicks >= SwitchTicks || relayDead)
                        {
                            p.Route = PeerRoute.Direct; p.RelayPid = 0; p.BetterTicks = 0;
                            Log(L.T("多人选路：切回直连 ", "Mesh route: back to direct ") + p.Pid);
                        }
                    }
                    else p.BetterTicks = 0;
                }
            }
            // 还没测到 RTT（刚连上 / 正在连接）时不双发：这时候的"丢包"是建连延迟，不是真丢
            p.DualSend = p.DirectRtt >= 0 && (p.DirectLoss >= 0.25 || p.DirectMaxLoss >= 2);
        }

        // =====================================================================
        // 新版 1v1（对决模式 duel）网络层（「对决模式的发包路线」、 对决一节）
        // 两位选手之间直连（沿用每对选路）；每位选手另外只发 1 份给"转发机"，转发机把选手的包转给其他所有观众；
        // 观众不发状态包。转发机 = 没上场的人里网络最好的那台，由房主统一算、统一宣布。
        // 协议（都走原来的签名 Wire / 中转头里的 ctrl）：
        // 4NMPDUEL 房主 → 所有人：on= fa= fb= relay= live= cap= epoch= owner= seq= 每 2 秒一次，变化时立刻发
        // 4NMPNET 成员 → 房主：pid= epoch= p=<对端:RTT毫秒:丢包率,…> rxage= rxloss= rxgap= 每 2 秒一次
        // 4NMPREFUSE 房主 → 想加入的人：why=specfull cap=<观众上限> 观众满了，加入被拒
        // 经转发机转出去的选手包 = 4NMPRELAY kind=dstate from=<选手> to=<观众> relay=<转发机>，payload 原样（src=<选手>、seq 不动）
        // =====================================================================
        public const int DuelCapDefault = MpCfg.Limit - 2, DuelCapMax = MpCfg.Limit - 2;     // 11-15 观众 = 房间人数 − 2，房间人数和其他玩法同一个上限（以前 8 / 16）
        public const double DuelAnnounceEvery = 2.0, DuelReportEvery = 2.0, DuelEvalEvery = 0.5;
        // 转发机打分（越小越好，单位 ms）：
        // score = 2 × max(到各位上场选手的 RTT) + mean(到各位其他观众的 RTT)
        // + 300 × (到选手的最大丢包率 + 到观众的平均丢包率) + 现任才有的收包扣分
        // 收包扣分（观众报上来的"收选手包"质量，近似转发机上行不够）= 平均收包丢包率 × 600 + min(300, max(0, 平均最大间隔 - 150) × 0.5)
        public const double RelayFighterWeight = 2.0;
        public const double RelayLossMs = 300.0;
        public const double RelayMissingSpecRttMs = 400.0;     // 候选人没测到某位观众：按 400 ms 算（没测到选手 = 不能靠分数当选）
        public const double RelayRxLossMs = 600.0;
        public const double RelayRxGapFreeMs = 150.0, RelayRxGapWeight = 0.5, RelayRxGapCapMs = 300.0;
        // 防抖：只在两局之间换；新候选好 30 ms 以上、连续 5 秒；现任掉线 / 离房 / 上场 / 观众收包大面积断立刻换
        public const double RelaySwitchMarginMs = 30.0;
        public const double RelaySwitchHoldSec = 5.0;
        public const double RelayLostAfterSec = 2.0;           // 现任 2 秒没动静（直连 / 中转的任何包）= 掉线
        public const double RelayReportFreshSec = 6.0;         // 测速摘要 6 秒没更新 = 过期
        public const double RelayRxReportFreshSec = 3.0;       // rxBreak 要求转发机与观众的收包摘要均在 3 秒内
        public const double RelayRxDeadMs = 2500.0;            // 观众 2.5 秒没收到任何选手的包 = 断
        public const double RelayRxFreshMs = 1000.0;           // 转发机自己直接收选手的包 1 秒内有 = 选手在发
        public const double RelayForceCooldownSec = 4.0;       // 强制换人之后 / 开打之后这么久内不因为"收包断"再换

        public volatile bool DuelOn;
        public volatile int DuelFa, DuelFb, DuelRelay;
        public volatile bool DuelLive;
        public volatile int DuelCap = DuelCapDefault;
        public long DuelFwd, DuelRefused;
        public volatile string RefusedWhy;                      // 客机：房主回的拒绝原因（specfull…）；Session 看到就断开并提示
        public string RefusedMsg = "";
        int duelOutSeq, duelOutEpoch = -1, duelInSeq = -1, duelInEpoch = -1, duelOffLeft;
        bool duelDirty;
        DateTime lastDuelAnnounce = DateTime.MinValue, lastDuelEval = DateTime.MinValue, lastNetReport = DateTime.MinValue, lastDuelRep = DateTime.MinValue;
        readonly DuelRelayElector elector = new DuelRelayElector();
        readonly Dictionary<int, DuelReport> duelReports = new Dictionary<int, DuelReport>();
        readonly Dictionary<int, DuelRxTrack> rxTracks = new Dictionary<int, DuelRxTrack>();
        readonly Dictionary<string, DateTime> refuseLogAt = new Dictionary<string, DateTime>();
        string duelScoreStr = "";

        public string DuelRole
        {
            get
            {
                int me = MyPid;
                if (me > 0 && (me == DuelFa || me == DuelFb)) return "fighter";
                if (me > 0 && me == DuelRelay) return "relay";
                return "spec";
            }
        }

        // ---- 纯函数：转发机候选人的分数（越小越好）。
        // fRtt / fLoss = 候选人到每位上场选手的 RTT(ms) / 丢包率（有几位上场就几项；RTT<0 = 没测到 → 返回 +∞，不能靠分数当选）；
        // sRtt / sLoss = 到每位其他观众的（RTT<0 按 RelayMissingSpecRttMs 算）；
        // rxLoss / rxGapMs = 只有现任才有：观众报上来的收转发包丢包率 / 最大间隔（rxLoss<0 = 没有）
        public static double RelayScore(IList<double> fRtt, IList<double> fLoss, IList<double> sRtt, IList<double> sLoss, double rxLoss, double rxGapMs)
        {
            double worst = 0, worstLoss = 0;
            if (fRtt != null)
                for (int i = 0; i < fRtt.Count; i++)
                {
                    if (fRtt[i] < 0) return double.PositiveInfinity;
                    if (fRtt[i] > worst) worst = fRtt[i];
                    if (fLoss != null && i < fLoss.Count) worstLoss = Math.Max(worstLoss, Clamp01(fLoss[i]));
                }
            double sumR = 0, sumL = 0;
            int n = sRtt == null ? 0 : sRtt.Count;
            for (int j = 0; j < n; j++)
            {
                sumR += sRtt[j] < 0 ? RelayMissingSpecRttMs : sRtt[j];
                if (sLoss != null && j < sLoss.Count && sLoss[j] > 0) sumL += Clamp01(sLoss[j]);
            }
            double score = RelayFighterWeight * worst + (n > 0 ? sumR / n : 0) + RelayLossMs * (worstLoss + (n > 0 ? sumL / n : 0));
            if (rxLoss > 0) score += RelayRxLossMs * Clamp01(rxLoss);
            if (rxGapMs > RelayRxGapFreeMs) score += Math.Min(RelayRxGapCapMs, (rxGapMs - RelayRxGapFreeMs) * RelayRxGapWeight);
            return score;
        }

        static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        // ---- 测试用的"按对给固定延迟 / 丢包"注入（只在 --duel-test / FORNET_DUEL_TEST=1 时生效）。
        // 只改测速摘要里报的数，不动真实的路线选择和收发：spec = "a-b:RTT毫秒[:丢包率],…"，a / b 可以写 * 通配，越具体越优先；空 = 清掉
        sealed class InjEnt { public int A, B; public double Rtt, Loss; }
        static readonly List<InjEnt> injList = new List<InjEnt>();
        static readonly object injSync = new object();
        public static bool TestInject;

        public static void SetInject(string spec)
        {
            var list = new List<InjEnt>();
            if (!string.IsNullOrEmpty(spec))
            {
                foreach (var tok in spec.Split(',', ';'))
                {
                    var t = tok.Trim();
                    if (t.Length == 0) continue;
                    try
                    {
                        var f = t.Split(':');
                        var pr = f[0].Split('-');
                        var e = new InjEnt();
                        e.A = pr[0].Trim() == "*" ? 0 : int.Parse(pr[0]);
                        e.B = pr[1].Trim() == "*" ? 0 : int.Parse(pr[1]);
                        e.Rtt = double.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture);
                        e.Loss = f.Length > 2 ? double.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture) : 0.0;
                        list.Add(e);
                    }
                    catch (Exception) { }
                }
            }
            lock (injSync) { injList.Clear(); injList.AddRange(list); }
        }

        static bool InjectGet(int x, int y, out double rtt, out double loss)
        {
            rtt = -1; loss = 0;
            if (!TestInject) return false;
            int bestSpec = -1;
            lock (injSync)
            {
                for (int i = 0; i < injList.Count; i++)
                {
                    var e = injList[i];
                    bool m1 = (e.A == 0 || e.A == x) && (e.B == 0 || e.B == y);
                    bool m2 = (e.A == 0 || e.A == y) && (e.B == 0 || e.B == x);
                    if (!m1 && !m2) continue;
                    int spec = (e.A != 0 ? 1 : 0) + (e.B != 0 ? 1 : 0);
                    if (spec >= bestSpec) { bestSpec = spec; rtt = e.Rtt; loss = e.Loss; }
                }
            }
            return bestSpec >= 0;
        }

        // ---- 房主：Lua 通过命令设对决状态
        public bool DuelCmdOn(bool on, int cap)
        {
            if (!IsHost) { Log(L.T("对决：不是房主，忽略 duel 命令", "Duel: not the host, ignoring the duel command")); return false; }
            lock (sync)
            {
                if (cap < 0) cap = DuelCap;
                cap = Math.Max(0, Math.Min(DuelCapMax, cap));
                DuelCap = cap;
                if (on != DuelOn)
                {
                    DuelOn = on;
                    if (!on) { DuelFa = DuelFb = DuelRelay = 0; DuelLive = false; duelReports.Clear(); rxTracks.Clear(); duelScoreStr = ""; elector.Cur = 0; elector.Cand = 0; duelOffLeft = 3; lastDuelAnnounce = DateTime.MinValue; }
                    else { duelOffLeft = 0; lastDuelRep = DateTime.MinValue; }
                }
                MpCfg.DuelOn = on;
                if (on)
                {
                    // 房间 = 2 位选手 + 观众，人数上限跟着放宽（MpCfg.Cap；MpCfg.Max 只增不减，已经进来的人不赶）
                    int want = Math.Min(MpCfg.DuelLimit, 2 + cap);
                    if (MpCfg.Max < want) MpCfg.Max = want;
                    if (MpMax < want) MpMax = want;
                }
                duelDirty = true;
                lastDuelEval = DateTime.MinValue;
            }
            Log(L.T("对决：模式 ", "Duel: mode ") + (on ? "on" : "off") + L.T("，观众上限 ", ", spectator cap ") + cap);
            return true;
        }

        public bool DuelCmdSet(int fa, int fb)
        {
            if (!IsHost) return false;
            if (fa < 0) fa = 0;
            if (fb < 0) fb = 0;
            if (fa == fb) fb = 0;
            lock (sync)
            {
                if (fa != DuelFa || fb != DuelFb) elector.LastForce = DateTime.UtcNow;
                DuelFa = fa; DuelFb = fb;
                duelDirty = true;
                lastDuelEval = DateTime.MinValue;
            }
            Log(L.T("对决：这一局选手 ", "Duel: fighters ") + fa + " vs " + fb);
            return true;
        }

        public bool DuelCmdPhase(bool live)
        {
            if (!IsHost) return false;
            lock (sync)
            {
                if (live && !DuelLive) elector.LastForce = DateTime.UtcNow;     // 刚开打：几秒内不因为"收包断"强制换
                if (live != DuelLive) elector.Cand = 0;
                DuelLive = live;
                duelDirty = true;
            }
            Log(L.T("对决：", "Duel: ") + (live ? L.T("开打（转发机局中不按分数换）", "match live (relay is not re-picked by score during a match)") : L.T("打完（转发机可以按分数换）", "match over (relay may be re-picked by score)")));
            return true;
        }

        // ---- 发包路线
        // 对决模式开着：我是选手 → 只发给对手 + 转发机（转发机就是对手时只发一份，没有转发机只发对手）；观众不发状态包
        void DuelBroadcastState(string tagged, bool crit)
        {
            int me = MyPid, fa = DuelFa, fb = DuelFb, relay = DuelRelay;
            if (me != fa && me != fb) return;
            int other = me == fa ? fb : fa;
            if (other > 0 && other != me)
            {
                MpPeerState p = Get(other);
                if (p != null) { p.StateTx++; SendState(p, tagged, crit); }
            }
            if (relay > 0 && relay != other && relay != me)
            {
                MpPeerState p = Get(relay);
                if (p != null) { p.StateTx++; SendState(p, tagged, crit); }
            }
        }

        // 收到并交给游戏的选手包（已去重）：记收包质量；我是转发机就转给每位观众（原样，保留 src / seq）
        void DuelAfterRx(int src, int seq, string tagged, bool allowForward = true)
        {
            int fa = DuelFa, fb = DuelFb;
            if (src != fa && src != fb) return;
            NoteRx(src, seq);
            int me = MyPid;
            if (allowForward && me > 0 && me == DuelRelay && me != fa && me != fb) DuelForward(src, tagged);
        }

        void DuelForward(int src, string tagged)
        {
            List<MpPeerState> list;
            lock (sync) list = new List<MpPeerState>(peers.Values);
            int fa = DuelFa, fb = DuelFb;
            bool crit = Critical(tagged);
            for (int i = 0; i < list.Count; i++)
            {
                MpPeerState p = list[i];
                if (p.Pid == src || p.Pid == fa || p.Pid == fb || p.Pid == MyPid) continue;
                SendFwd(p, src, tagged, crit);
            }
        }

        void SendFwd(MpPeerState p, int src, string tagged, bool crit)
        {
            bool directOk = !RelayOnly(p) && p.DirectVerified || p.DirectHandle != null || (p.Handle != null && !NeedVerifiedDirect);
            bool bridgeOk = !BridgePrimary && p.Route == PeerRoute.Bridge && p.BuHandle != 0 && p.BuConnected && !RelayOnly(p);
            bool sent = false;
            if (bridgeOk)
            {
                TrySend((object)p.BuHandle, Envelope(src, p.Pid, "dstate", MyPid, 0, tagged));
                sent = true;
            }
            else if (directOk && p.SendHandle != null && !Blocked(MyPid, p.Pid))
            {
                TrySend(p.SendHandle, Envelope(src, p.Pid, "dstate", MyPid, 0, tagged));
                sent = true;
            }
            if (!sent || p.Route == PeerRoute.Via || crit || p.DualSend || RelayOnly(p))
            {
                int relay = RelayOf(p);
                if (relay != 0 && relay != p.Pid && relay != MyPid)
                {
                    MpPeerState r = Get(relay);
                    if (r != null && r.SendHandle != null)
                    {
                        TrySend(r.SendHandle, Envelope(src, p.Pid, "dstate", relay, 1, tagged));
                        sent = true;
                    }
                }
            }
            if (sent) { p.FwdTx++; DuelFwd++; }
        }

        // ---- 收包质量（每位成员都记；观众报给房主，现任转发机据此扣分）
        void NoteRx(int src, int seq)
        {
            DateTime now = DateTime.UtcNow;
            lock (sync)
            {
                DuelRxTrack t;
                if (!rxTracks.TryGetValue(src, out t)) { t = new DuelRxTrack(); rxTracks[src] = t; }
                if (seq >= 0)
                {
                    if (t.LastSeq >= 0 && seq < t.LastSeq - 100) { t.WinBase = seq - 1; t.WinGot = 0; t.LastSeq = seq; }     // 选手重开了，seq 从头来
                    if (t.WinBase < 0) t.WinBase = seq - 1;
                    if (seq > t.LastSeq) t.LastSeq = seq;
                }
                t.WinGot++; t.Total++;
                if (t.LastAt != DateTime.MinValue)
                {
                    double g = (now - t.LastAt).TotalMilliseconds;
                    if (g > t.WinMaxGapMs) t.WinMaxGapMs = g;
                }
                t.LastAt = now;
            }
        }

        void FillRx(DuelReport r, DateTime now)
        {
            double minAge = -1, lossSum = 0, gap = 0;
            int n = 0;
            lock (sync)
            {
                DateTime snap = DateTime.UtcNow;     // 在锁内取快照，避免收包线程晚几毫秒把 LastAt 推到 now 后面、算出负数再被四舍五入成 rxage=-1
                int fa = DuelFa, fb = DuelFb;
                foreach (var kv in rxTracks)
                {
                    if (kv.Key != fa && kv.Key != fb) continue;
                    DuelRxTrack t = kv.Value;
                    if (t.LastAt == DateTime.MinValue) continue;
                    double age = (snap - t.LastAt).TotalMilliseconds;
                    if (age < 0) age = 0;
                    if (minAge < 0 || age < minAge) minAge = age;
                    int expected = t.LastSeq - t.WinBase;
                    if (expected > 0)
                    {
                        double l = 1.0 - (double)t.WinGot / expected;
                        lossSum += l < 0 ? 0 : l;
                        n++;
                    }
                    if (t.WinMaxGapMs > gap) gap = t.WinMaxGapMs;
                    t.WinBase = t.LastSeq; t.WinGot = 0; t.WinMaxGapMs = 0;
                }
            }
            r.RxAgeMs = minAge;
            r.RxLoss = n > 0 ? lossSum / n : 0;
            r.RxGapMs = gap;
        }

        // 本机测到的每对 RTT / 丢包摘要：直连能用就报直连的，不能用就报最好的一条经第三人的
        DuelReport MakeReport(DateTime now)
        {
            var r = new DuelReport();
            r.At = now;
            List<MpPeerState> list;
            lock (sync) list = new List<MpPeerState>(peers.Values);
            for (int i = 0; i < list.Count; i++)
            {
                MpPeerState p = list[i];
                if (p.Pid == MyPid || p.Pid <= 0) continue;
                double rtt = -1, loss = 0;
                bool du = p.DirectVerified && p.DirectRtt >= 0 && p.DirectLoss < 0.5;
                if (!BridgePrimary && p.Route == PeerRoute.Bridge && p.BuHandle != 0 && p.BuConnected && p.BuBridgeRtt >= 0) { rtt = p.BuBridgeRtt; loss = 0; }
                else if (du) { rtt = p.DirectRtt; loss = p.DirectLoss; }
                else
                {
                    List<MpProbe> ps;
                    lock (sync) ps = new List<MpProbe>(p.Probes.Values);
                    double bestSc = double.MaxValue;
                    for (int j = 0; j < ps.Count; j++)
                    {
                        MpProbe pr = ps[j];
                        if (pr.Rtt < 0 || pr.Miss >= 2) continue;
                        double sc = pr.Rtt + pr.Loss * 120.0;
                        if (sc < bestSc) { bestSc = sc; rtt = pr.Rtt; loss = pr.Loss; }
                    }
                }
                double ir, il;
                if (InjectGet(MyPid, p.Pid, out ir, out il)) { rtt = ir; loss = il; }
                r.Rtt[p.Pid] = rtt;
                r.Loss[p.Pid] = loss;
            }
            FillRx(r, now);
            return r;
        }

        string NetBody(DuelReport r)
        {
            var sb = new StringBuilder();
            sb.Append(WireTag.P + "MPNET\npid=").Append(MyPid).Append("\nepoch=").Append(Epoch).Append("\np=");
            bool first = true;
            var ids = new List<int>(r.Rtt.Keys);
            ids.Sort();
            for (int i = 0; i < ids.Count; i++)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(ids[i]).Append(':').Append(Kv.F(r.Rtt[ids[i]], "0")).Append(':').Append(Kv.F(r.Loss[ids[i]], "0.000"));
            }
            sb.Append("\nrxage=").Append(Kv.F(r.RxAgeMs, "0")).Append("\nrxloss=").Append(Kv.F(r.RxLoss, "0.000")).Append("\nrxgap=").Append(Kv.F(r.RxGapMs, "0")).Append('\n');
            return sb.ToString();
        }

        static double ParseD(string s, double dflt)
        {
            double v;
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : dflt;
        }

        // 房主收一位成员的测速摘要（直连按地址认发送人 / 中转按中转头里的 from 认）
        void OnNet(object from, string s, int viaPid)
        {
            if (!IsHost || !DuelOn) return;
            var d = Kv.Parse(s);
            string v;
            int pid;
            if (!d.TryGetValue("pid", out v) || !int.TryParse(v, out pid)) return;
            int sender = viaPid > 0 ? viaPid : SenderPid(from);
            if (sender == 0 || sender != pid) { LogBadClaim("net pid=" + pid + " sender=" + sender); return; }
            var r = new DuelReport();
            r.At = DateTime.UtcNow;
            if (d.TryGetValue("p", out v) && v.Length > 0)
            {
                foreach (var part in v.Split(','))
                {
                    var f = part.Split(':');
                    int q;
                    if (f.Length < 3 || !int.TryParse(f[0], out q)) continue;
                    r.Rtt[q] = ParseD(f[1], -1);
                    r.Loss[q] = ParseD(f[2], 0);
                }
            }
            if (d.TryGetValue("rxage", out v)) r.RxAgeMs = ParseD(v, -1);
            if (d.TryGetValue("rxloss", out v)) r.RxLoss = ParseD(v, 0);
            if (d.TryGetValue("rxgap", out v)) r.RxGapMs = ParseD(v, 0);
            lock (sync)
            {
                duelReports[pid] = r;
                MpPeerState fp;
                if (viaPid == 0 && peers.TryGetValue(pid, out fp)) fp.LastDirectRx = DateTime.UtcNow;
            }
        }

        // 成员收房主宣布的对决状态：只认当前房主本人发的（同 ：直连按地址、中转按中转头里的发送人）；
        // 旧的（代数小 / 同代数 seq 不新）不认
        void OnDuel(object from, string s, int viaPid)
        {
            var d = Kv.Parse(s);
            string v;
            int owner = 0, epoch = 0, seq = 0;
            if (!d.TryGetValue("owner", out v) || !int.TryParse(v, out owner) || owner <= 0) return;
            if (d.TryGetValue("epoch", out v)) int.TryParse(v, out epoch);
            if (d.TryGetValue("seq", out v)) int.TryParse(v, out seq);
            int sender = viaPid > 0 ? viaPid : SenderPid(from);
            if (sender == 0 || sender != owner) { LogBadClaim("duel owner=" + owner + " sender=" + sender); return; }
            if (IsHost || owner != HostPid || epoch < Epoch) return;
            bool changed = false;
            string logLine = null;
            lock (sync)
            {
                MpPeerState fp;
                if (peers.TryGetValue(owner, out fp))
                {
                    if (viaPid > 0) fp.LastViaRx = DateTime.UtcNow; else fp.LastDirectRx = DateTime.UtcNow;
                }
                if (epoch < duelInEpoch || (epoch == duelInEpoch && seq <= duelInSeq)) return;
                duelInEpoch = epoch; duelInSeq = seq;
                int fa = 0, fb = 0, relay = 0, cap = DuelCap;
                bool on = d.TryGetValue("on", out v) && v == "1";
                bool live = d.TryGetValue("live", out v) && v == "1";
                if (d.TryGetValue("fa", out v)) int.TryParse(v, out fa);
                if (d.TryGetValue("fb", out v)) int.TryParse(v, out fb);
                if (d.TryGetValue("relay", out v)) int.TryParse(v, out relay);
                if (d.TryGetValue("cap", out v)) int.TryParse(v, out cap);
                changed = on != DuelOn || fa != DuelFa || fb != DuelFb || relay != DuelRelay || live != DuelLive || cap != DuelCap;
                if (changed)
                    logLine = L.T("对决：房主宣布 ", "Duel: host announced ") + (on ? "on" : "off") + " fa=" + fa + " fb=" + fb + " relay=" + relay + " live=" + (live ? 1 : 0) + " cap=" + cap + " epoch=" + epoch;
                DuelFa = fa; DuelFb = fb; DuelRelay = relay; DuelLive = live; DuelCap = cap;
                if (on != DuelOn)
                {
                    DuelOn = on;
                    if (!on) { rxTracks.Clear(); duelReports.Clear(); }
                }
                MpCfg.DuelOn = on;
                elector.Cur = relay;
            }
            if (logLine != null) Log(logLine);
        }

        // ---- 房主：算转发机、宣布
        void DuelTick(DateTime now)
        {
            if (MyPid <= 0) return;
            if (!DuelOn)
            {
                if (IsHost && duelOffLeft > 0 && (now - lastDuelAnnounce).TotalSeconds >= 0.7) { duelOffLeft--; DuelAnnounce(now); }
                return;
            }
            if (IsHost)
            {
                if ((now - lastDuelRep).TotalSeconds >= DuelReportEvery)
                {
                    lastDuelRep = now;
                    var rep = MakeReport(now);
                    lock (sync) duelReports[MyPid] = rep;
                }
                // 现任转发机一到掉线门槛（2 秒没包 / 告别）就马上重算，不等下一个 0.5 秒评估点（最多省 0.5 秒）
                bool relayGone = false;
                int curRelay = DuelRelay;
                if (curRelay > 0 && curRelay != MyPid && (now - lastDuelEval).TotalSeconds >= 0.05)
                {
                    lock (sync)
                    {
                        MpPeerState rp;
                        relayGone = !peers.TryGetValue(curRelay, out rp) || PeerGone(rp, now);
                    }
                }
                if (relayGone || (now - lastDuelEval).TotalSeconds >= DuelEvalEvery) { lastDuelEval = now; DuelEvaluate(now); }
                if (duelDirty || (now - lastDuelAnnounce).TotalSeconds >= DuelAnnounceEvery) DuelAnnounce(now);
            }
            else if ((now - lastNetReport).TotalSeconds >= DuelReportEvery && HostPid > 0 && HostPid != MyPid)
            {
                lastNetReport = now;
                SendControl(HostPid, NetBody(MakeReport(now)));
            }
        }

        void DuelAnnounce(DateTime now)
        {
            string body;
            lock (sync)
            {
                if (duelOutEpoch != Epoch) { duelOutEpoch = Epoch; duelOutSeq = 0; }     // 换了房主（代数变了）：seq 从头
                duelOutSeq++;
                duelDirty = false;
                lastDuelAnnounce = now;
                body = WireTag.P + "MPDUEL\non=" + (DuelOn ? 1 : 0) + "\nfa=" + DuelFa + "\nfb=" + DuelFb + "\nrelay=" + DuelRelay + "\nlive=" + (DuelLive ? 1 : 0)
                     + "\ncap=" + DuelCap + "\nepoch=" + Epoch + "\nowner=" + HostPid + "\nseq=" + duelOutSeq + "\n";
            }
            BroadcastControl(body);
        }

        void DuelEvaluate(DateTime now)
        {
            if (!IsHost || !DuelOn) return;
            int cur;
            string whyE = null, scoreStr, rxLog = "";
            int newRelay;
            lock (sync)
            {
                int me = MyPid;
                var alive = new Dictionary<int, bool>();
                var born = new Dictionary<int, DateTime>();
                alive[me] = true; born[me] = DateTime.MinValue;
                foreach (var kv in peers)
                {
                    int pid = kv.Key;
                    if (pid <= 0 || pid == me) continue;
                    MpPeerState p = kv.Value;
                    bool ok = !PeerGone(p, now);
                    alive[pid] = ok;
                    born[pid] = p.BornAt;
                }
                var staleIds = new List<int>();
                foreach (var k in duelReports.Keys) if (!alive.ContainsKey(k)) staleIds.Add(k);
                for (int i = 0; i < staleIds.Count; i++) duelReports.Remove(staleIds[i]);

                var fighters = new List<int>();
                bool aOk, bOk;
                if (DuelFa > 0 && alive.TryGetValue(DuelFa, out aOk) && aOk) fighters.Add(DuelFa);
                if (DuelFb > 0 && DuelFb != DuelFa && alive.TryGetValue(DuelFb, out bOk) && bOk) fighters.Add(DuelFb);
                // 候选 = 活着的、没上场的；兜底顺序：房主（不上场时）优先，其余按进房先后
                var cands = new List<int>();
                foreach (var kv in alive) if (kv.Value && !fighters.Contains(kv.Key) && !(peers.ContainsKey(kv.Key) && peers[kv.Key].Hidden)) cands.Add(kv.Key);     // 隐藏 IP 的人不当转发机
                cands.Sort(delegate(int a, int b)
                {
                    if (a == me) return b == me ? 0 : -1;
                    if (b == me) return 1;
                    int c = born[a].CompareTo(born[b]);
                    return c != 0 ? c : a.CompareTo(b);
                });

                cur = DuelRelay;
                // 现任的收包情况：其他观众报上来的丢包 / 间隔平均
                double rxLoss = -1, rxGap = 0;
                {
                    double sl = 0, sg = 0; int n = 0;
                    for (int i = 0; i < cands.Count; i++)
                    {
                        int sId = cands[i];
                        DuelReport rs;
                        if (sId == cur || !duelReports.TryGetValue(sId, out rs) || (now - rs.At).TotalSeconds > RelayReportFreshSec || rs.RxAgeMs < 0) continue;
                        sl += rs.RxLoss; sg += rs.RxGapMs; n++;
                    }
                    if (n > 0) { rxLoss = sl / n; rxGap = sg / n; }
                }
                var scores = new Dictionary<int, double>();
                for (int i = 0; i < cands.Count; i++)
                {
                    int c = cands[i];
                    DuelReport rc;
                    if (!duelReports.TryGetValue(c, out rc) || (now - rc.At).TotalSeconds > RelayReportFreshSec) { scores[c] = double.PositiveInfinity; continue; }
                    var fR = new List<double>(); var fL = new List<double>();
                    for (int j = 0; j < fighters.Count; j++)
                    {
                        double v1, v2;
                        if (!rc.Rtt.TryGetValue(fighters[j], out v1)) v1 = -1;
                        if (!rc.Loss.TryGetValue(fighters[j], out v2)) v2 = 0;
                        fR.Add(v1); fL.Add(v2);
                    }
                    var sR = new List<double>(); var sL = new List<double>();
                    for (int j = 0; j < cands.Count; j++)
                    {
                        if (cands[j] == c) continue;
                        double v1, v2;
                        if (!rc.Rtt.TryGetValue(cands[j], out v1)) v1 = -1;
                        if (!rc.Loss.TryGetValue(cands[j], out v2)) v2 = 0;
                        sR.Add(v1); sL.Add(v2);
                    }
                    scores[c] = RelayScore(fR, fL, sR, sL, c == cur ? rxLoss : -1, c == cur ? rxGap : 0);
                }
                var ssb = new StringBuilder();
                for (int i = 0; i < cands.Count; i++)
                {
                    if (i > 0) ssb.Append(',');
                    double sc = scores[cands[i]];
                    ssb.Append(cands[i]).Append(':').Append(double.IsInfinity(sc) ? "-" : Kv.F(sc, "0.0"));
                }
                scoreStr = ssb.ToString();
                duelScoreStr = scoreStr;

                bool curBad = false;
                string badWhy = null;
                if (cur != 0)
                {
                    bool isMember = alive.ContainsKey(cur);
                    if (!isMember) { curBad = true; badWhy = "现任转发机离房了"; }
                    else if (!alive[cur]) { curBad = true; badWhy = "现任转发机掉线（超过 " + RelayLostAfterSec.ToString("0") + " 秒没动静）"; }
                    else if (fighters.Contains(cur)) { curBad = true; badWhy = "现任转发机上场当选手了"; }
                }
                // 观众大面积收不到转发包：现任自己直接收选手的包是新鲜的（选手在发），但至少一半观众 2.5 秒没收到任何选手的包
                bool rxBreak = false;
                string rxDetail = "";
                if (DuelLive && cur != 0 && !curBad && fighters.Count > 0)
                {
                    DuelReport rr;
                    bool relayFresh = duelReports.TryGetValue(cur, out rr)
                                      && (now - rr.At).TotalSeconds <= RelayRxReportFreshSec
                                      && rr.At >= elector.LastForce
                                      && rr.RxAgeMs >= 0 && rr.RxAgeMs <= RelayRxFreshMs;
                    if (relayFresh)
                    {
                        rxDetail = " rx[relay " + cur + " age=" + Kv.F(rr.RxAgeMs, "0") + " rep=" + Kv.F((now - rr.At).TotalMilliseconds, "0") + "ms";
                        int total = 0, dead = 0;
                        for (int i = 0; i < cands.Count; i++)
                        {
                            DuelReport rs;
                            if (cands[i] == cur || !duelReports.TryGetValue(cands[i], out rs)
                                || (now - rs.At).TotalSeconds > RelayRxReportFreshSec
                                || rs.At < elector.LastForce) continue;
                            total++;
                            if (rs.RxAgeMs < 0 || rs.RxAgeMs > RelayRxDeadMs) dead++;
                            rxDetail += "; " + cands[i] + " age=" + Kv.F(rs.RxAgeMs, "0") + " rep=" + Kv.F((now - rs.At).TotalMilliseconds, "0") + "ms";
                        }
                        rxBreak = total > 0 && dead * 2 >= total;
                        rxDetail += "]";
                    }
                }
                elector.Cur = cur;
                newRelay = elector.Decide(now, DuelLive, curBad, badWhy, rxBreak, scores, cands, out whyE);
                if (newRelay != cur) { DuelRelay = newRelay; duelDirty = true; }
                if (rxBreak) rxLog = rxDetail;
            }
            if (newRelay != cur || whyE != null)
                Log(L.T("对决转发机：", "Duel relay: ") + (cur == 0 ? "-" : cur.ToString()) + " → " + (newRelay == 0 ? "-" : newRelay.ToString())
                    + L.T("，原因：", ", why: ") + (whyE ?? "") + L.T("；候选分数 ", "; candidate scores ") + scoreStr + " live=" + (DuelLive ? 1 : 0) + rxLog);
        }

        // ---- 观众已满：加入被拒（房主在 NoteMp 里、给新来的地址回 4NMPREFUSE 带原因）。返回 true = 拒了，调用方别再收这个人
        public bool DuelRefuse(object from)
        {
            if (!IsHost || !DuelOn || from == null) return false;
            if (ByHandle(from) != null) return false;      // 已经在房间里的（重连 / 保活握手）不算新人
            int members;
            lock (sync) members = peers.Count + 1;
            if (members < 2 + DuelCap) return false;
            DuelRefused++;
            TrySend(from, WireTag.P + "MPREFUSE\nwhy=specfull\ncap=" + DuelCap + "\n");
            string key = from.ToString();
            DateTime t;
            if (!refuseLogAt.TryGetValue(key, out t) || (DateTime.UtcNow - t).TotalSeconds > 5.0)
            {
                refuseLogAt[key] = DateTime.UtcNow;
                Log(L.T("对决：观众已满（上限 ", "Duel: spectators full (cap ") + DuelCap + L.T("），拒绝 ", "), refused ") + key);
            }
            return true;
        }

        // 对决模式下房间人数到顶（编号分配满了，没走到"观众已满"那条）：也明说"房间已满"，不让人默默排队。
        // 普通多人房满了照旧排队等空位
        public bool DuelRefuseFull(object from)
        {
            if (!IsHost || !DuelOn || from == null) return false;
            if (ByHandle(from) != null) return false;
            DuelRefused++;
            TrySend(from, WireTag.P + "MPREFUSE\nwhy=full\ncap=" + MpCfg.Max + "\n");
            string key = from.ToString();
            DateTime t;
            if (!refuseLogAt.TryGetValue(key, out t) || (DateTime.UtcNow - t).TotalSeconds > 5.0)
            {
                refuseLogAt[key] = DateTime.UtcNow;
                Log(L.T("对决：房间已满（上限 ", "Duel: room full (cap ") + MpCfg.Max + L.T(" 人），拒绝 ", "), refused ") + key);
            }
            return true;
        }

        // 旧版（不认多人房）发普通 1v1 握手想进多人房 / 对决房：以前什么都不回，对方一直"未连接"。
        // 旧版只认得几条固定提示，最贴切的是"版本不一致：你 vX / 对方 vY，建议两边更新到同一版本"——收到带 av= 的握手就会显示。
        // 所以回一个只带版本号的握手（不带 seq=，对方不会当成连上）；每个地址 1 秒最多一次，日志 5 秒一条
        readonly Dictionary<string, DateTime> oldHelloAt = new Dictionary<string, DateTime>();
        public int OldRefused;
        public bool TellOldClient(object from, string hello)
        {
            if (!IsHost || from == null || hello == null) return false;
            if (!hello.StartsWith(WireTag.P + "HELLO", StringComparison.Ordinal) || hello.Contains("\nmp=1") || !hello.Contains("\npv=")) return false;
            if (ByHandle(from) != null) return false;
            string key = from.ToString();
            DateTime now = DateTime.UtcNow, t;
            if (oldHelloAt.TryGetValue(key, out t) && (now - t).TotalSeconds < 1.0) return true;
            oldHelloAt[key] = now;
            OldRefused++;
            TrySend(from, WireTag.P + "HELLO\npv=" + Proto.Version + "\nav=" + Proto.App + "\nmproom=1\n");
            if (!refuseLogAt.TryGetValue("old:" + key, out t) || (now - t).TotalSeconds > 5.0)
            {
                refuseLogAt["old:" + key] = now;
                string av = null;
                foreach (var ln in hello.Split('\n')) if (ln.StartsWith("av=", StringComparison.Ordinal)) av = ln.Substring(3);
                Log(L.T("多人房：", "Multiplayer room: ") + key + L.T(" 用 1v1 方式来连（对方 v", " tried to join as 1v1 (their v") + (av ?? "?")
                    + L.T("，多半是旧版），回了版本号让它提示更新", ", probably an old version); replied with our version so it shows an update hint"));
            }
            return true;
        }

        // 想加入的人收到房主的拒绝：记下原因，Session 看到后断开并提示
        void OnRefuse(object from, string s)
        {
            if (IsHost || hostConfirmed) return;
            if (from != null && SenderPid(from) == 0) { LogBadClaim("refuse from unknown sender"); return; }
            var d = Kv.Parse(s);
            string why, cap;
            d.TryGetValue("why", out why);
            d.TryGetValue("cap", out cap);
            if (why == "specfull") RefusedMsg = L.T("观众已满（上限 ", "Spectators are full (cap ") + (cap ?? "?") + L.T("）", ")");
            else if (why == "full") RefusedMsg = L.T("房间已满（上限 ", "The room is full (cap ") + (cap ?? "?") + L.T(" 人）", ")");
            else RefusedMsg = L.T("被房主拒绝：", "Refused by the host: ") + (why ?? "");
            if (RefusedWhy == null) Log(L.T("对决：被房主拒绝，原因 ", "Duel: refused by the host, reason ") + (why ?? "?"));
            RefusedWhy = why ?? "refused";
        }

        // session.txt 给游戏的对决字段（对决模式关着 = 什么都不写）
        public string DuelLines()
        {
            if (!DuelOn) return "";
            var sb = new StringBuilder();
            sb.Append("\nduel=1\nduelfa=").Append(DuelFa).Append("\nduelfb=").Append(DuelFb).Append("\nduelrelay=").Append(DuelRelay)
              .Append("\nduelrole=").Append(DuelRole).Append("\nduellive=").Append(DuelLive ? 1 : 0).Append("\nduelcap=").Append(DuelCap)
              .Append("\nduelepoch=").Append(Epoch).Append("\nduelfwd=").Append(DuelFwd).Append("\nduelrefused=").Append(DuelRefused);
            var stx = new StringBuilder();
            var rx = new StringBuilder();
            lock (sync)
            {
                var ids = new List<int>(peers.Keys);
                ids.Sort();
                for (int i = 0; i < ids.Count; i++)
                {
                    MpPeerState p = peers[ids[i]];
                    if (stx.Length > 0) stx.Append(',');
                    stx.Append(p.Pid).Append(':').Append(p.StateTx).Append(':').Append(p.FwdTx);
                }
                var rids = new List<int>(rxTracks.Keys);
                rids.Sort();
                for (int i = 0; i < rids.Count; i++)
                {
                    if (rx.Length > 0) rx.Append(',');
                    rx.Append(rids[i]).Append(':').Append(rxTracks[rids[i]].Total);
                }
                sb.Append("\nduelstx=").Append(stx).Append("\nduelrx=").Append(rx).Append("\nduelsc=").Append(duelScoreStr);
            }
            return sb.ToString();
        }

        public string ProbeStats()
        {
            var sb = new StringBuilder();
            lock (sync)
            {
                var ids = new List<int>(peers.Keys);
                ids.Sort();
                for (int i = 0; i < ids.Count; i++)
                {
                    MpPeerState p = peers[ids[i]];
                    foreach (var kv in p.Probes)
                    {
                        if (sb.Length > 0) sb.Append(';');
                        sb.Append(p.Pid).Append('>').Append(kv.Key).Append(':')
                          .Append(Kv.F(kv.Value.Rtt, "0")).Append(':').Append(Kv.F(kv.Value.Loss, "0.00")).Append(':').Append(kv.Value.Miss);
                    }
                }
            }
            return sb.ToString();
        }

        public string StatsLine()
        {
            var sb = new StringBuilder();
            lock (sync)
            {
                var ids = new List<int>(peers.Keys);
                ids.Sort();
                for (int i = 0; i < ids.Count; i++)
                {
                    MpPeerState p = peers[ids[i]];
                    if (i > 0) sb.Append(';');
                    string rt = p.Route == PeerRoute.Bridge ? "b" : (p.Route == PeerRoute.Direct ? "d" : "v");
                    sb.Append(p.Pid).Append(':').Append(rt).Append(':')
                      .Append(Kv.F(p.DirectRtt, "0")).Append(':').Append(Kv.F(p.DirectLoss, "0.00")).Append(':')
                      .Append(p.Route == PeerRoute.Via ? p.RelayPid : 0).Append(':').Append(p.DualSend ? 1 : 0)
                      .Append(':').Append(p.DirectRxCount).Append(':').Append(p.ViaRxCount).Append(':').Append(p.DirectMaxLoss).Append(':').Append((p.RouteWhy ?? "").Replace(':', '/'));
                }
            }
            return sb.ToString();
        }

        bool IsBuHandle(MpPeerState p, object h)
        {
            return !BridgePrimary && p != null && p.BuHandle != 0 && h != null && h.Equals((object)p.BuHandle);
        }

        MpPeerState PeerByBuHandle(uint h)
        {
            if (h == 0) return null;
            lock (sync)
            {
                foreach (var p in peers.Values)
                    if (p.BuHandle == h) return p;
            }
            return null;
        }

        void EnsureBuConnHandler()
        {
            if (buSub) return;
            Bridge.OnConnStatus += OnBuConnStatus;
            buSub = true;
        }

        // ---- 服务器大厅多人房：直连打不通房主时经桥接入房（房间设置"对局走桥接" = 直连失败时走桥接）。
        // 房主：大厅服务器介绍过的加入者桥接 ID（60 秒内有效）连进来就接住；客机：直连 Tries 次不通后连房主的监听口 BuPort(房主编号)。
        // 这条连接上的包不走升级测速那套，原样交给 ServerLink（JoinBridgeRx：房主按入房 / 成员包处理，客机按房主包处理）。
        // 经桥接入房的人按"隐藏 IP"成员算：房主不介绍它的地址，它和别的成员之间都经房主转发。
        public Func<object, byte[], bool> JoinBridgeRx;
        readonly Dictionary<ulong, DateTime> joinAllow = new Dictionary<ulong, DateTime>();
        readonly HashSet<uint> joinHandles = new HashSet<uint>();
        public volatile bool JoinBridgeUp;       // 客机：连房主的那条桥接已连上
        public volatile bool JoinBridgeLost;     // 客机：连房主的那条桥接断了 / 没连上

        public bool IsJoinHandle(object h)
        {
            if (!(h is uint)) return false;
            lock (sync) return joinHandles.Contains((uint)h);
        }

        // 房主：大厅服务器在 PEER 里带了加入者的桥接 ID
        public void AllowJoinBridge(ulong sid)
        {
            if (sid == 0UL || !IsHost || BridgePrimary) return;
            bool fresh;
            lock (sync)
            {
                fresh = !joinAllow.ContainsKey(sid);
                joinAllow[sid] = DateTime.UtcNow.AddSeconds(60);
            }
            EnsureBuConnHandler();
            EnsureBuListen();
            if (fresh) Log(L.T("多人桥接入房：大厅服务器介绍了一位可经桥接加入的玩家，监听虚拟端口 ", "Mesh bridge join: the lobby introduced a player who may join over the bridge, listening vport ") + BuPort(MyPid));
        }

        // 客机：连房主（hostPid = 房主编号，决定虚拟端口）。返回连接号，0 = 没连成
        public uint ConnectJoinBridge(ulong hostSid, int hostPid)
        {
            if (hostSid == 0UL || IsHost || BridgePrimary) return 0;
            EnsureBuConnHandler();
            JoinBridgeUp = false;
            JoinBridgeLost = false;
            uint h = Bridge.ConnectP2P(hostSid, BuPort(hostPid > 0 ? hostPid : 1));
            if (h != 0) lock (sync) joinHandles.Add(h);
            Log(L.T("多人桥接入房：连房主虚拟端口 ", "Mesh bridge join: connecting to host vport ") + BuPort(hostPid > 0 ? hostPid : 1) + (h != 0 ? "" : L.T("（没连成）", " (failed)")));
            return h;
        }

        // 房主：这位玩家先经 UDP 进了房（房主收得到它、它收不到房主），后来改经桥接又来握手 → 原编号改挂到桥接连接上，地址全清（按隐藏 IP）
        public bool RebindToBridge(int pid, object h)
        {
            if (!IsHost || pid <= 0 || !(h is uint)) return false;
            lock (sync)
            {
                MpPeerState p;
                if (!peers.TryGetValue(pid, out p) || p.IsAi) return false;
                p.Handle = h;
                p.DirectHandle = null;
                p.AltHandle = null;
                p.AltVerified = false;
                p.Addr = "";
                p.LanAddr = "";
                p.V6Addr = "";
                p.Hidden = true;
                p.DirectVerified = true;
                p.LastDirectRx = DateTime.UtcNow;
            }
            Log(L.T("多人桥接入房：", "Mesh bridge join: ") + pid + L.T(" 号改经桥接连着（之前的直连只通一边）", " now connected over the bridge (its direct link was one-way)"));
            return true;
        }

        public void CloseJoinBridge()
        {
            List<uint> hs;
            lock (sync) { hs = new List<uint>(joinHandles); joinHandles.Clear(); joinAllow.Clear(); }
            JoinBridgeUp = false;
            for (int i = 0; i < hs.Count; i++) { try { Bridge.Close(hs[i], false); } catch (Exception) { } }
        }

        // 桥接连接状态：是入房那条就在这里处理完，返回 true
        bool OnJoinConnStatus(uint h, int st, ulong remoteSid)
        {
            bool mine;
            lock (sync) mine = joinHandles.Contains(h);
            if (st == Bridge.StConnecting && !mine && IsHost && remoteSid != 0UL)
            {
                // 自动换线（BridgeUp 测速）和"经桥接入房"共用这一个虚拟端口 / 这一个回调。
                // 这位对端刚发过 BUASK，正在等它的测速连接：这条就是测速那条，必须先让给桥接测速，
                // 否则会被下面当成"经桥接入房"吃掉（测速包全进 JoinBridgeRx），15 秒测不到就放弃、整局不再换线。
                bool buWants = false;
                lock (sync)
                {
                    foreach (var bp in peers.Values)
                        if (bp.Pid > MyPid && bp.BridgeId == remoteSid && bp.BuHandle == 0 && !bp.BuAbandoned
                            && bp.BuPhase >= BuAsked && bp.BuPhase <= BuMeasuring) { buWants = true; break; }
                }
                if (buWants)
                {
                    Log(L.T("多人桥接：", "Mesh bridge: ") + remoteSid + L.T(" 的测速连接（刚发过 BUASK），不按入房处理", " speed-test connection (BUASK just came in), not a bridge join"));
                    return false;
                }
                bool ok;
                lock (sync)
                {
                    DateTime until;
                    ok = joinAllow.TryGetValue(remoteSid, out until) && DateTime.UtcNow < until;
                    if (ok) joinHandles.Add(h);
                }
                if (!ok) return false;
                bool acc = Bridge.Accept(h);
                Log(L.T("多人桥接入房：接住加入者的桥接连接 ", "Mesh bridge join: accepted a joiner's bridge connection ") + h + (acc ? "" : L.T("（接受失败）", " (accept failed)")));
                if (!acc) { lock (sync) joinHandles.Remove(h); try { Bridge.Close(h, false); } catch (Exception) { } }
                return true;
            }
            if (!mine) return false;
            if (st == Bridge.StConnected)
            {
                if (!IsHost) JoinBridgeUp = true;
                Log(L.T("多人桥接入房：桥接已连上（连接 ", "Mesh bridge join: bridge connected (conn ") + h + "）");
            }
            else if (st == Bridge.StClosedByPeer || st == Bridge.StProblem)
            {
                lock (sync) joinHandles.Remove(h);
                try { Bridge.Close(h, false); } catch (Exception) { }
                if (!IsHost) { JoinBridgeUp = false; JoinBridgeLost = true; }
                Log(L.T("多人桥接入房：桥接连接 ", "Mesh bridge join: bridge connection ") + h + (st == Bridge.StClosedByPeer ? L.T(" 被对方关了", " closed by peer") : L.T(" 出错断开", " dropped")));
            }
            return true;
        }

        void EnsureBuListen()
        {
            if (buListening || MyPid <= 0) return;
            Bridge.Listen(BuPort(MyPid));
            Bridge.SetHosting(false);
            buListening = true;
            Log(L.T("多人桥接：开始监听虚拟端口 ", "Mesh bridge: listening vport ") + BuPort(MyPid));
        }

        void ClosePeerBridge(MpPeerState p)
        {
            if (p == null) return;
            uint h = p.BuHandle;
            p.BuHandle = 0;
            p.BuConnected = false;
            if (h != 0) { try { Bridge.Close(h, false); } catch (Exception) { } }
        }

        public void CloseBridge()
        {
            if (BridgePrimary) return;
            List<MpPeerState> list;
            lock (sync)
            {
                list = new List<MpPeerState>(peers.Values);
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    if (p.Route == PeerRoute.Bridge) { p.Route = PeerRoute.Direct; p.RelayPid = 0; p.DualSend = false; }
                    p.BuPhase = BuDone;
                    p.BuAbandoned = true;
                    ClosePeerBridge(p);
                }
            }
            if (buSub) { Bridge.OnConnStatus -= OnBuConnStatus; buSub = false; }
            if (buListening) { try { Bridge.Unlisten(); } catch (Exception) { } buListening = false; }
        }

        public int PumpBridge()
        {
            if (!Enabled || BridgePrimary) return 0;
            bool need = buSub || buListening;
            if (!need) lock (sync) need = joinHandles.Count > 0;
            if (!need)
            {
                lock (sync)
                {
                    foreach (var p in peers.Values)
                        if (p.BuHandle != 0 || (p.BuPhase >= BuConnecting && p.BuPhase <= BuActive)) { need = true; break; }
                }
            }
            if (!need) return 0;
            Bridge.Pump();
            int got = 0;
            while (true)
            {
                KeyValuePair<uint, byte[]> m;
                lock (Bridge.Data) { if (Bridge.Data.Count == 0) break; m = Bridge.Data.Dequeue(); }
                MpPeerState fp = PeerByBuHandle(m.Key);
                if (fp == null)
                {
                    // 入房那条桥接：原样交给 ServerLink（它按直连收包那套验签 / 收人 / 认房主）
                    if (JoinBridgeRx != null && IsJoinHandle((object)m.Key))
                    {
                        bool took = false;
                        try { took = JoinBridgeRx((object)m.Key, m.Value); } catch (Exception ex) { Log("JoinBridgeRx: " + ex.Message); }
                        if (took) got++;
                    }
                    continue;
                }
                string s = Verify != null ? Verify(m.Value) : null;
                if (s == null) continue;
                fp.LastBuRx = DateTime.UtcNow;
                int g;
                long rxBefore = MpRx;
                if (HandlePacket((object)m.Key, s, out g))
                {
                    if (MpRx > rxBefore) got += (int)(MpRx - rxBefore);
                    else got += g;
                }
            }
            return got;
        }

        void OnBuConnStatus(uint h, int st, ulong remoteSid)
        {
            if (BridgePrimary || h == 0) return;
            if (OnJoinConnStatus(h, st, remoteSid)) return;     // 入房那条桥接
            if (st == Bridge.StConnecting)
            {
                MpPeerState existing = PeerByBuHandle(h);
                if (existing != null) return;
                MpPeerState match = null;
                if (CanAutoBridge && remoteSid != 0UL)
                {
                    lock (sync)
                    {
                        foreach (var p in peers.Values)
                        {
                            if (p.Pid > MyPid && p.BridgeId == remoteSid && !p.Hidden && !p.BuAbandoned
                                && p.BuHandle == 0 && (p.BuPhase >= BuAsked && p.BuPhase <= BuActive))
                            {
                                match = p;
                                p.BuHandle = h;
                                p.BuConnected = false;
                                if (p.BuPhase < BuConnecting) { p.BuPhase = BuConnecting; p.BuConnSince = DateTime.UtcNow; }
                                break;
                            }
                        }
                    }
                }
                if (match != null) Bridge.Accept(h);
                else
                {
                    Log(L.T("多人桥接：收到一条桥接连接（对端 ", "Mesh bridge: incoming connection (peer ") + remoteSid
                            + L.T("）但没有对应 / 正在测速的对端 → 关掉", ") with no matching speed-test peer -> closed"));
                    try { Bridge.Close(h, false); } catch (Exception) { }
                }
                return;
            }
            if (st == Bridge.StConnected)
            {
                MpPeerState p = PeerByBuHandle(h);
                if (p == null && remoteSid != 0UL)
                {
                    lock (sync)
                    {
                        foreach (var q in peers.Values)
                        {
                            if (q.Pid > MyPid && q.BridgeId == remoteSid && !q.Hidden && !q.BuAbandoned
                                && q.BuHandle == 0 && q.BuPhase >= BuAsked && q.BuPhase <= BuMeasuring)
                            {
                                q.BuHandle = h;
                                p = q;
                                break;
                            }
                        }
                    }
                }
                if (p != null)
                {
                    p.BuConnected = true;
                    p.LastBuRx = DateTime.UtcNow;
                    if (p.BuPhase < BuMeasuring)
                    {
                        p.BuPhase = BuMeasuring;
                        p.BuConnSince = DateTime.UtcNow;
                        p.BuBrRtts.Clear();
                        p.LastBuBrProbeAt = DateTime.MinValue;
                    }
                    Log(L.T("多人桥接：已连上对端 ", "Mesh bridge: connected peer ") + p.Pid + L.T("，开始测 6 次桥接延迟", ", measuring 6 RTT samples"));
                }
                return;
            }
            if (st == Bridge.StClosedByPeer || st == Bridge.StProblem)
            {
                MpPeerState p = PeerByBuHandle(h);
                if (p != null)
                {
                    if (p.Route == PeerRoute.Bridge)
                        SwitchBackDirect(p, st == Bridge.StClosedByPeer ? "对端关闭桥接" : "桥接连接出错");
                    else if (!p.BuAbandoned)
                        AbandonPeerBridge(p, st == Bridge.StClosedByPeer ? "对端关闭桥接" : "桥接连接失败");
                }
            }
        }

        void SwitchBackDirect(MpPeerState p, string why)
        {
            if (p == null) return;
            if (p.Route == PeerRoute.Bridge)
            {
                p.Route = PeerRoute.Direct;
                p.RelayPid = 0;
                p.BetterTicks = 0;
                p.DualSend = false;
            }
            Log(L.T("多人桥接：对端 ", "Mesh bridge: peer ") + p.Pid + L.T(" 切回直连（", " switched back to direct (") + why + "）");
            AbandonPeerBridge(p, null);
        }

        void AbandonPeerBridge(MpPeerState p, string why)
        {
            if (p == null) return;
            p.BuAbandoned = true;
            p.BuPhase = BuDone;
            p.BuPeerActive = false;
            p.BuGiveUpAt = DateTime.MinValue;
            if (!string.IsNullOrEmpty(why))
                Log(L.T("多人桥接：放弃对端 ", "Mesh bridge: gave up peer ") + p.Pid + "（" + why + "）");
            ClosePeerBridge(p);
        }

        void BuTick(MpPeerState p, DateTime now)
        {
            if (p == null || BridgePrimary) return;
            if (!CanAutoBridgeCfg || RelayOnly(p) || p.Hidden)
            {
                if (p.Route == PeerRoute.Bridge) SwitchBackDirect(p, "当前配置关闭桥接");
                return;
            }
            if (p.BuGiveUpAt != DateTime.MinValue)
            {
                if (p.BuPeerActive) p.BuGiveUpAt = DateTime.MinValue;
                else if ((now - p.BuGiveUpAt).TotalSeconds >= 2.0) { p.BuGiveUpAt = DateTime.MinValue; ClosePeerBridge(p); }
            }
            if (p.DirectVerified && p.SendHandle != null && !Blocked(MyPid, p.Pid) && !p.BuAbandoned && p.BuPhase != BuDone)
            {
                if ((now - p.LastBuDirProbeAt).TotalSeconds >= BridgeUp.ProbeIntervalSec)
                {
                    p.LastBuDirProbeAt = now;
                    TrySend(p.SendHandle, WireTag.P + "BUPING\nk=d\nt=" + MonoMs() + "\n");
                }
            }
            if (!CanAutoBridge || p.BuAbandoned) return;

            if (p.BuPhase == BuNone)
            {
                if (p.DirectVerified && p.BuDirRtts.Count >= BridgeUp.DirectSamples)
                {
                    var rec = BridgeUp.Recent(p.BuDirRtts, BridgeUp.DirectSamples);
                    if (BridgeUp.ShouldStayDirect(rec, BridgeUp.ThresholdMs))
                    {
                        p.BuPhase = BuDone;
                        Log(L.T("多人桥接：直连延迟合适（", "Mesh bridge: direct fast enough (") + Kv.F(BridgeUp.Median(rec), "0") + " <= " + BridgeUp.ThresholdMs + L.T(" ms），对端 ", " ms), peer ") + p.Pid + L.T(" 保持直连", " stays direct"));
                    }
                    else if (BridgeUp.ShouldTry(rec, BridgeUp.ThresholdMs))
                    {
                        EnsureBuConnHandler();
                        if (MyPid < p.Pid) EnsureBuListen();
                        p.BuPhase = BuAsked;
                        p.BuConnSince = now;
                        p.LastBuAskAt = DateTime.MinValue;
                        Log(L.T("多人桥接：直连较慢（", "Mesh bridge: direct slow (") + Kv.F(BridgeUp.Median(rec), "0") + " > " + BridgeUp.ThresholdMs + L.T(" ms），向对端 ", " ms), asking peer ") + p.Pid + L.T(" 询问桥接", " for bridge ID"));
                    }
                }
            }
            else if (p.BuPhase == BuAsked)
            {
                if ((now - p.BuConnSince).TotalSeconds >= BridgeUp.ConnectTimeoutSec)
                {
                    AbandonPeerBridge(p, "询问桥接 15 秒无应答");
                }
                else if ((now - p.LastBuAskAt).TotalSeconds >= BridgeUp.ProbeIntervalSec && p.SendHandle != null && p.DirectVerified && !Blocked(MyPid, p.Pid))
                {
                    p.LastBuAskAt = now;
                    TrySend(p.SendHandle, WireTag.P + "BUASK\nsid=" + Bridge.MyId + "\n");
                }
            }
            else if (p.BuPhase == BuConnecting)
            {
                if ((now - p.BuConnSince).TotalSeconds >= BridgeUp.ConnectTimeoutSec)
                    AbandonPeerBridge(p, "桥接 15 秒未连上");
            }
            else if (p.BuPhase == BuMeasuring)
            {
                if (!p.BuConnected || p.BuHandle == 0) { AbandonPeerBridge(p, "测速期间桥接断开"); return; }
                if ((now - p.BuConnSince).TotalSeconds >= BridgeUp.ConnectTimeoutSec) { AbandonPeerBridge(p, "桥接测速 15 秒超时"); return; }
                if ((now - p.LastBuBrProbeAt).TotalSeconds >= BridgeUp.ProbeIntervalSec)
                {
                    p.LastBuBrProbeAt = now;
                    TrySend((object)p.BuHandle, WireTag.P + "BUPING\nk=b\nt=" + MonoMs() + "\n");
                }
                if (p.BuBrRtts.Count >= BridgeUp.BridgeSamples)
                {
                    double brMed = BridgeUp.Median(BridgeUp.Recent(p.BuBrRtts, BridgeUp.BridgeSamples));
                    double curMed = -1;
                    if (p.Route == PeerRoute.Via && p.RelayPid != 0 && p.Probes.ContainsKey(p.RelayPid) && p.Probes[p.RelayPid].Rtt >= 0)
                        curMed = p.Probes[p.RelayPid].Rtt;
                    else if (p.BuDirRtts.Count > 0)
                        curMed = BridgeUp.Median(BridgeUp.Recent(p.BuDirRtts, BridgeUp.DirectSamples));
                    else
                        curMed = p.DirectRtt;

                    if (BridgeUp.ShouldSwitch(brMed, curMed))
                    {
                        p.Route = PeerRoute.Bridge;
                        p.RelayPid = 0;
                        p.DualSend = false;
                        p.BetterTicks = 0;
                        p.BuBridgeRtt = brMed;
                        p.BuPhase = BuActive;
                        p.BuSwitchedAt = now;
                        p.LastBuRx = now;
                        p.LastBuCheckAt = now;
                        TrySend((object)p.BuHandle, WireTag.P + "BUGO\non=1\n");
                        Log(L.T("多人桥接：对端 ", "Mesh bridge: peer ") + p.Pid + L.T(" 切换到桥接（桥接 ", " switched to bridge (bridge ") + Kv.F(brMed, "0") + " ms + 20 < " + Kv.F(curMed, "0") + " ms）");
                    }
                    else
                    {
                        p.BuAbandoned = true;
                        p.BuPhase = BuDone;
                        p.BuGiveUpAt = p.BuPeerActive ? DateTime.MinValue : now;
                        Log(L.T("多人桥接：桥接不够快（桥接 ", "Mesh bridge: bridge not faster (bridge ") + Kv.F(brMed, "0") + " ms, current " + Kv.F(curMed, "0") + L.T(" ms），对端 ", " ms), peer ") + p.Pid + L.T(" 保持原路线", " stays on current route"));
                    }
                }
            }
            else if (p.BuPhase == BuActive)
            {
                if (p.BuHandle == 0 || !p.BuConnected) { SwitchBackDirect(p, "桥接连接断开"); return; }
                if ((now - p.LastBuRx).TotalSeconds >= BridgeUp.SilentTimeoutSec) { SwitchBackDirect(p, "桥接静默超过 1 秒"); return; }
                if ((now - p.LastBuBrProbeAt).TotalSeconds >= BridgeUp.ProbeIntervalSec)
                {
                    p.LastBuBrProbeAt = now;
                    TrySend((object)p.BuHandle, WireTag.P + "BUPING\nk=b\nt=" + MonoMs() + "\n");
                }
                if ((now - p.BuSwitchedAt).TotalSeconds >= BridgeUp.CheckDelaySec && (now - p.LastBuCheckAt).TotalSeconds >= BridgeUp.ProbeIntervalSec)
                {
                    p.LastBuCheckAt = now;
                    if ((now - p.LastBuDirRx).TotalSeconds <= 3.0 && p.BuDirRtts.Count >= 4 && p.BuBrRtts.Count >= 4)
                    {
                        string why;
                        if (BridgeUp.ShouldSwitchBack(BridgeUp.Recent(p.BuDirRtts, BridgeUp.CheckSamples), BridgeUp.Recent(p.BuBrRtts, BridgeUp.CheckSamples), out why))
                        {
                            TrySend((object)p.BuHandle, WireTag.P + "BUGO\non=0\n");
                            SwitchBackDirect(p, why);
                        }
                    }
                }
            }
        }

        void OnBuAsk(object from, string s)
        {
            if (BridgePrimary || from == null) return;
            MpPeerState p = ByHandle(from);
            if (p == null || IsBuHandle(p, from)) return;     // 只认直连链路上的询问
            if (CanAutoBridgeCfg && !Bridge.Ready && (DateTime.UtcNow - lastBuInitAt).TotalSeconds >= 1.0)
            {
                lastBuInitAt = DateTime.UtcNow;
                Bridge.Init();
            }
            var d = Kv.Parse(s);
            string sidS;
            ulong peerSid = 0;
            if (!d.TryGetValue("sid", out sidS) || !ulong.TryParse(sidS, out peerSid) || peerSid == 0) return;
            if (!CanAutoBridge || RelayOnly(p) || p.Hidden || p.BuAbandoned || !p.DirectVerified)
            {
                TrySend(from, WireTag.P + "BUANS\nok=0\n");
                return;
            }
            p.BridgeId = peerSid;
            EnsureBuConnHandler();
            if (MyPid < p.Pid) EnsureBuListen();
            if (p.BuPhase < BuConnecting || p.BuPhase == BuDone)
            {
                p.BuPhase = BuConnecting;
                p.BuConnSince = DateTime.UtcNow;
            }
            TrySend(from, WireTag.P + "BUANS\nok=1\nsid=" + Bridge.MyId + "\n");
        }

        void OnBuAns(object from, string s)
        {
            if (BridgePrimary || from == null) return;
            MpPeerState p = ByHandle(from);
            if (p == null || IsBuHandle(p, from)) return;
            if (p.BuAbandoned || (p.BuPhase != BuAsked && p.BuPhase != BuConnecting)) return;
            var d = Kv.Parse(s);
            string ok, sidS;
            d.TryGetValue("ok", out ok);
            ulong peerSid = 0;
            if (ok != "1" || !d.TryGetValue("sid", out sidS) || !ulong.TryParse(sidS, out peerSid) || peerSid == 0)
            {
                AbandonPeerBridge(p, "对端关闭或不允许桥接");
                return;
            }
            p.BridgeId = peerSid;
            if (p.BuPhase == BuAsked)
            {
                p.BuPhase = BuConnecting;
                p.BuConnSince = DateTime.UtcNow;
            }
            EnsureBuConnHandler();
            if (MyPid < p.Pid)
            {
                EnsureBuListen();
                TrySend(from, WireTag.P + "BUREADY\nsid=" + Bridge.MyId + "\n");
            }
            else if (p.BuHandle == 0 && p.BuPhase == BuConnecting)
            {
                p.BuHandle = Bridge.ConnectP2P(p.BridgeId, BuPort(p.Pid));
                Log(L.T("多人桥接：拿到对端 ", "Mesh bridge: got peer ") + p.Pid + L.T(" 桥接号，发起连接", " bridge ID, connecting"));
            }
        }

        void OnBuReady(object from, string s)
        {
            if (BridgePrimary || from == null) return;
            MpPeerState p = ByHandle(from);
            if (p == null || IsBuHandle(p, from)) return;
            if (p.BuAbandoned || !CanAutoBridge || p.BridgeId == 0 || MyPid <= p.Pid) return;
            EnsureBuConnHandler();
            if (p.BuPhase < BuConnecting) { p.BuPhase = BuConnecting; p.BuConnSince = DateTime.UtcNow; }
            if (p.BuHandle == 0)
            {
                p.BuHandle = Bridge.ConnectP2P(p.BridgeId, BuPort(p.Pid));
                Log(L.T("多人桥接：收到对端 ", "Mesh bridge: peer ") + p.Pid + L.T(" 就绪，发起桥接连接", " ready, connecting bridge"));
            }
        }

        void OnBuGo(object from, string s)
        {
            if (BridgePrimary || from == null) return;
            MpPeerState p = ByHandle(from);
            if (p == null) return;
            var d = Kv.Parse(s);
            string on;
            if (!d.TryGetValue("on", out on)) return;
            p.BuPeerActive = on == "1";
            if (!p.BuPeerActive && p.BuPhase == BuDone && p.BuAbandoned)
                p.BuGiveUpAt = DateTime.UtcNow;
        }

        void OnBuPing(object from, string s)
        {
            if (BridgePrimary || from == null) return;
            MpPeerState p = ByHandle(from);
            if (p == null) return;
            bool br = IsBuHandle(p, from);
            if (!br && Blocked(p.Pid, MyPid)) return;
            if (br) p.LastBuRx = DateTime.UtcNow; else p.LastDirectRx = DateTime.UtcNow;
            var d = Kv.Parse(s);
            string k, t;
            if (!d.TryGetValue("t", out t)) return;
            d.TryGetValue("k", out k);
            TrySend(from, WireTag.P + "BUPONG\nk=" + (k ?? "d") + "\nt=" + t + "\n");
        }

        void OnBuPong(object from, string s)
        {
            if (BridgePrimary || from == null) return;
            MpPeerState p = ByHandle(from);
            if (p == null) return;
            bool br = IsBuHandle(p, from);
            var d = Kv.Parse(s);
            string k, t;
            long t0;
            if (!d.TryGetValue("t", out t) || !long.TryParse(t, out t0)) return;
            d.TryGetValue("k", out k);
            double rawRtt = Math.Max(0, MonoMs() - t0);
            if (rawRtt > 10000) return;
            if (k == "b" && br)
            {
                double rtt = rawRtt + BridgeUp.BridgeAddMsFor(MyPid, p.Pid);
                BridgeUp.AddRtt(p.BuBrRtts, rtt, BridgeUp.CheckSamples);
                p.BuBridgeRtt = BridgeUp.Median(p.BuBrRtts);
                p.LastBuRx = DateTime.UtcNow;
            }
            else if (k == "d" && !br)
            {
                if (Blocked(p.Pid, MyPid) || Blocked(MyPid, p.Pid)) return;
                double rtt = rawRtt + BridgeUp.DirectAddMsFor(MyPid, p.Pid);
                BridgeUp.AddRtt(p.BuDirRtts, rtt, BridgeUp.CheckSamples);
                p.LastBuDirRx = DateTime.UtcNow;
                p.LastDirectRx = DateTime.UtcNow;
                p.DirectVerified = true;
            }
        }
    }
}
