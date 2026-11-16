// 传输层接口（§98 从 Bridge.cs 挪出来：发布版不编译 Bridge.cs，见 build.bat release）。
using System;
using System.Collections.Generic;
using System.Text;

namespace ForNet
{
    // 传输层：UDP（Link、ServerLink）和开发版的 Steam（BridgeLink）都实现它；Session 只认这个接口
    interface ITransport
    {
        void Send(byte[] b);
        int Pump();                 // 收包，排队写 state_remote.txt（RemoteOut 每次写一个），返回有效状态包数
        string PeerStr { get; }
        string PeerName { get; }    // 显示用：Steam 名字；UDP 为空
        string PeerNick { get; }    // v0.18.7：对方在联机设置里填的昵称（状态包里的 nm=），还没收到时为空
        int LocalPort { get; }      // UDP 端口；Steam 为 0
        long Tx { get; }
        long Gap { get; }
        double PingMs { get; }      // Steam 报的 ping；UDP 用包里的发送时间估单程 ×2
        Action<string> Log { get; set; }
        RemoteOut Out { get; }      // §85 写 state_remote.txt 的队列（等人的房间转正时换路径）
        Wire Wire { get; set; }     // v0.22.0：收到的包先验签（Reliable.cs），事件 / 确认交给可靠通道；Session 设
        void Close();
    }

    // §141 房间设置：观战 / 排队人数上限（联机程序的 netcfg.txt maxspec=，面板改；0 = 不让观战）。
    // §223 发给观战者的包不再瘦身（Slim 删了）：和对手收到的一模一样
    // §223.3 省流（实测 11-10 录像：完整包平均 2290 字节、29 包/秒，一路 65 KB/秒；以前瘦身 510 字节）：大字段（武器效果 wge 1035 字节、
    // 被动 pas、禁用名单、wc / wcs …）一局里几乎不变却每包都发。每一路记着上次转出去的值，只转变了的字段（变了之后连续 REPEAT 包都带，
    // 防丢包），原包里没了的字段在 del= 里说（也连发 REPEAT 包），每 REFRESH 秒和观战人数变了时发一次全量。包头 dz=1 = 省流包、
    // 没有 = 全量；观战那边（game_spec.lua SP.unpack）拼回完整包再用，和收到完整包一模一样
    // §223.4 对战的状态包也省流（用户 11-10"做掉"、"默认也是用省流模式，主机控制"、"要做个保底兼容"）。
    // 联机程序之间做：发之前 SpecDelta 编码（Session.SendText），收到在 Wire.Verify 里拼回完整包，游戏脚本看到的还是完整包、一点不改。
    // 协商：新版每个状态包带 dzv=1（我会拼）和 dzk=<发送方编号>（同一个 Wire 收两个人的包时分开拼：主机下场观战）；
    // 主机自己的包另带 dzh=<0|1>（房间设置"联机省流"，netcfg dz=，默认开）。只在对方会拼、而且主机开着时才发省流包：
    // 主机看自己的设置 + 客机的 dzv；客机只认主机自己的包里的 dzh=1（旧版主机不带 → 一直发完整包）。完整包那套留着兜底
    static class DzCfg
    {
        public static volatile bool On = true;      // 我当主机时的设置
        public static Func<DateTime> Now = () => DateTime.UtcNow;     // 省流编码用的时钟（测试换成按录像时间走的）
    }

    sealed class DzIn
    {
        readonly Dictionary<string, Dictionary<string, string>> sticky = new Dictionary<string, Dictionary<string, string>>();
        readonly Dictionary<string, int> lastSeq = new Dictionary<string, int>();
        readonly Dictionary<string, long> used = new Dictionary<string, long>();
        long useN;
        public long Merged, Dropped, Stale, Evicted;
        // §223.6 最多记这么多个发送方（审查：没设密码时谁都能发未签名包，每个不同的 dzk 存一整份包，以前没上限）。
        // 正常最多 2 个（对手 + 主机下场时的第二位），超了挤掉最久没用的
        public const int MaxSenders = 24;     // 11-15 多人房最多 12 人（对决 18）：8 → 24
        public int Senders { get { lock (sticky) return sticky.Count; } }

        void Touch(string k)
        {
            used[k] = ++useN;
            if (sticky.Count <= MaxSenders) return;
            string oldest = null;
            long o = long.MaxValue;
            foreach (var kv in used) if (kv.Key != k && kv.Value < o) { o = kv.Value; oldest = kv.Key; }
            if (oldest == null) return;
            sticky.Remove(oldest); lastSeq.Remove(oldest); used.Remove(oldest);
            Evicted++;
        }

        // 乱序晚到的旧包（序号不比已拼过的新、差得不多）：不拼进去，不然旧值会盖掉新值、等下一个完整包才纠正。差很多 = 对方重启了，照收
        bool Old(string k, Dictionary<string, string> d)
        {
            string s;
            int seq, last;
            if (!d.TryGetValue("seq", out s) || !int.TryParse(s, out seq)) return false;
            if (lastSeq.TryGetValue(k, out last) && seq <= last && seq > last - 100) return true;
            lastSeq[k] = seq;
            return false;
        }

        // 一个收到的包：控制包 / 观战包（带 src=）/ 不是状态包 → 原样；完整状态包 → 记下（按 dzk）原样返回；
        // 省流包（dz=1）→ 拼回完整包；这个发送方还没收到过完整包 → null（丢掉，等下一个完整包，最多 REFRESH 秒）
        // key：§328 全互连按发送方编号拼（调用方已去掉 src=、给定 key）；null = 对战 / 观战原来的按 dzk 拼、带 src= 的放过
        public string Take(string body, Action<Dictionary<string, string>> onFull, string key = null)
        {
            if (body == null || body.StartsWith(WireTag.P, StringComparison.Ordinal)) return body;
            var d = new Dictionary<string, string>();
            foreach (var raw in body.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq > 0) d[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            if ((key == null && d.ContainsKey("src")) || !d.ContainsKey("seq") && !d.ContainsKey("dz")) return body;
            string k = key;
            if (k == null && !d.TryGetValue("dzk", out k)) k = "";
            string dz;
            if (!d.TryGetValue("dz", out dz) || dz != "1")
            {
                lock (sticky)
                {
                    if (Old(k, d)) { Stale++; return body; }     // 完整包晚到：不当底（原样交出去，序号去重会丢）
                    sticky[k] = new Dictionary<string, string>(d);
                    Touch(k);
                }
                if (onFull != null) onFull(d);
                return body;
            }
            Dictionary<string, string> s;
            lock (sticky)
            {
                if (!sticky.TryGetValue(k, out s)) { Dropped++; return null; }
                if (Old(k, d)) { Stale++; return null; }
                Touch(k);
                string del;
                if (d.TryGetValue("del", out del)) foreach (var x in del.Split(',')) if (x.Length > 0) s.Remove(x);
                foreach (var kv in d) if (kv.Key != "dz" && kv.Key != "del") s[kv.Key] = kv.Value;
                var sb = new StringBuilder();
                foreach (var kv in s) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                Merged++;
                return sb.ToString();
            }
        }
    }

    sealed class SpecDelta
    {
        public const double REFRESH = 2.0;
        public const int REPEAT = 5;
        Dictionary<string, string> last = new Dictionary<string, string>();
        readonly Dictionary<string, int> rep = new Dictionary<string, int>();
        readonly Dictionary<string, int> delRep = new Dictionary<string, int>();
        DateTime fullAt = DateTime.MinValue;
        int lastCount = -1;
        public long FullBytes, SentBytes;      // 统计：原样全发会是多少 / 实际发了多少

        // body = 状态包（键=值 一行一个，不带 src=）；返回要发的内容（不带 src=）
        public string Encode(string body, int specCount, DateTime now)
        {
            var cur = new Dictionary<string, string>();
            var order = new List<string>();
            foreach (var raw in body.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line.Substring(0, eq);
                if (k == "src" || k == "dz" || k == "del") continue;
                if (!cur.ContainsKey(k)) order.Add(k);
                cur[k] = line.Substring(eq + 1);
            }
            bool full = specCount != lastCount || (now - fullAt).TotalSeconds >= REFRESH;
            lastCount = specCount;
            var sb = new StringBuilder();
            // §223.6 全量包也照常记账（审查：以前全量时清掉 rep / delRep，在全量那一帧变的字段只发这一次，这个包丢了就错到下一个全量）。
            // 变了的字段 / 没了的字段在后面的省流包里照样连发 REPEAT 包（全量包算其中一次）
            var send = new List<string>();
            foreach (var k in order)
            {
                string o;
                if (!last.TryGetValue(k, out o) || o != cur[k]) rep[k] = REPEAT;
                int r;
                if (rep.TryGetValue(k, out r) && r > 0)
                {
                    send.Add(k);
                    if (r == 1) rep.Remove(k); else rep[k] = r - 1;
                }
                delRep.Remove(k);
            }
            foreach (var k in last.Keys) if (!cur.ContainsKey(k)) { delRep[k] = REPEAT; rep.Remove(k); }
            var del = new List<string>();
            foreach (var k in new List<string>(delRep.Keys))
            {
                del.Add(k);
                if (delRep[k] <= 1) delRep.Remove(k); else delRep[k]--;
            }
            if (full)
            {
                fullAt = now;
                foreach (var k in order) sb.Append(k).Append('=').Append(cur[k]).Append('\n');      // 全量：没了的字段不用说（整包就是全部）
            }
            else
            {
                foreach (var k in send) sb.Append(k).Append('=').Append(cur[k]).Append('\n');
                if (del.Count > 0) sb.Append("del=").Append(string.Join(",", del)).Append('\n');
                sb.Append("dz=1\n");
            }
            last = cur;
            FullBytes += body.Length;
            SentBytes += sb.Length;
            return sb.ToString();
        }
    }

    static class SpecCfg
    {
        public static volatile int Max = 4;
        public const int OldMax = 8;          // §295 旧 1v1 观战（主机一个人转给所有观众）最多收几个；房间设置能到 16 是给新版 1v1（对决）用的
        // §141 只观战、不排队上场（观战者自己的设置，netcfg specnoq=）：观战握手带 noq=1 / WATCH 第 5 段 noq，主机 / 服务器不把它排进队
        public static volatile bool NoQueue;
        // §141 观战者把开关切回"排队"：之后 3 秒观战握手带 up=1 / WATCH 第 4 段 up，主机 / 服务器去掉"刚下场、先不叫"的标记
        static long upUntil;
        public static void WantUp() { System.Threading.Interlocked.Exchange(ref upUntil, DateTime.UtcNow.AddSeconds(3).Ticks); }
        public static bool Up { get { return DateTime.UtcNow.Ticks < System.Threading.Interlocked.Read(ref upUntil); } }
    }

    // 多人混战（docs/MULTI.md ②a）：主机的房间收几个人（连主机自己，0 / 1 = 不收，最多 Limit = 12，11-15 前是 6）；客机这次是不是按多人加入（握手带 mp=1）。
    // 先只有局域网 / 输入 IP（Link）
    static class MpCfg
    {
        public const int Limit = 12;           // 用户 11-10：多人房间 3–6 人；11-15 用户要放到 12（"其他模式为啥不能设置更多，比如到 12 人"）
        public static volatile int Max;
        public static volatile bool Join;
        public static volatile bool JoinWatch;   // §324 这次加入是"观战"（从列表点观战 / 开着只观战点房间）：session.txt 报 mpwatch=1，对决模式里直接当"只看不上场"
        public const double Lost = 6.0;          // 主机：一位玩家这么多秒没包 = 走了，编号空出来
        // 新版 1v1（对决模式 duel）：房间 = 2 位选手 + 观众。11-15 用户（"3 个模式房间人数是一个配置"、"新版 1v1 的观战人数不要超过其他两个模式的上限，不要做特例"）：
        // 和其他玩法同一个上限（以前放宽到 18 = 2 + 16 观众）
        public const int DuelLimit = Limit;
        public static volatile bool DuelOn;
        public static int Cap { get { return DuelOn ? DuelLimit : Limit; } }     // 编号分配用的人数上限
        // §322 用户 11-15："淘汰积分也能中途进"、"新版默认都能进，不需要单独的观战"：对局中不再拒新人（以前 §289.1 回 4NBUSY|inmatch）。
        // 进来的人这一局不参加、先观战，下一局照常（游戏脚本 game_multi.lua 管）。改回 false = 恢复对局中拒收
        public const bool MidJoin = true;
    }

    // M2 房间设置：主机只观战（解说模式）
    static class HostSpecCfg
    {
        public static volatile bool On;
    }

    // §141 观战：能当主机给观战者发包的传输（Link 直接发、ServerLink 经大厅服务器、BridgeLink 经 Steam 连接），
    // 以及当观战者收两路包（src=h / src=c，按来源分别去重）
    interface ISpecSink
    {
        int SpecCount { get; }                  // 现在有几个观战者（0 = 不用转发）
        void SendSpecs(byte[] sealedPacket);    // 发给每个观战者
        bool SpecDzAll { get; }                 // §223.4 现在在看的观战者都会拼省流包（握手 / WATCH 带 dz）：是才发省流包，有一个旧版就都发完整包
        Action<string> OnPeerState { get; set; }  // 主机：收到对手的一个状态包
        bool SpecMode { get; set; }             // 观战者：收主机转来的两路包
        Action<string> OnRole { get; set; }     // §141.7 观战者 / 对手：主机发来 4NROLE（轮到你上场 / 你下场观战）
        int QPos { get; }                       // §141.7 观战者：排第几（0 = 不知道）
        int QLen { get; }
    }

    // §141.7 排队：主机这边能让观战者上场 / 让对手下场的传输（Link 按 IP 端点、BridgeLink 按连接句柄，都当 object 用）
    interface IQueueHost
    {
        List<object> QueueOrder();              // 排队的观战者，先来的在前
        object PromoteHead();                   // 队首上场当对手，返回它（没人排队 = null）
        object DemotePeer();                    // 现在的对手下场排到队尾，返回它
        void SendTo(object who, byte[] sealedPacket);
        bool HasPeer { get; }
        double SinceFromPeer { get; }
        bool CanPromote { get; }                // 有人能上场（刚下场的那个不算）
    }

    // M3：多人混战全互连的联机程序一侧（Link / BridgeLink）。MpMesh 自己做介绍、选路、双发、去重。
    interface IMpLink
    {
        MpMesh Mesh { get; }
        bool MpJoinMode { get; }
        void EnableMpMesh(bool host);
        int MyPid { get; }
        int MpSeen { get; }
        string MpRoster { get; }
        int MpCount { get; }
        List<KeyValuePair<int, string>> MpPlayers();
        void SendMpInfo();
        string MpStatsLines();
        string LocalAddrForHost(int hostPid);
    }

    // §141.8 主机下场：队首当第二位选手，两位选手的包都经主机互相转发（主机自己观战）。Link / BridgeLink 实现
    interface IRelayHost
    {
        object PromoteSecond();                 // 队首当第二位选手（从队里拿掉），返回它
        object DemoteSecond();                  // 第二位选手下场排到队尾，返回它
        void SecondToPeer();                    // 第一位走了、没人补：第二位改当第一位（主机回来跟他打）
        bool HasSecond { get; }
        double SinceFromSecond { get; }
        Action<string> OnSecondState { get; set; }
        void SendSecond(byte[] sealedPacket);
        bool NoOut { get; set; }                // 转发时不把第一位的包直接交给游戏（Session 加上来源再交）
    }
}
