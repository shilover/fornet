// 对端表（docs/MESH.md §4 / §9 M1）：把散在 Link / BridgeLink / ServerLink / Session 里的对端状态
// （对手、第二位选手、观战者、多人混战玩家、序号去重、最后收包时间、排队先后、刚下场标记、省流编码器、可靠通道占位）收进统一的表。
// C# 5 语法（Windows 自带 .NET Framework 4 csc.exe 编译）。
using System;
using System.Collections.Generic;

namespace ForNet
{
    enum PeerRole
    {
        None = 0,
        Peer = 1,       // 1v1 对手（或客机眼里的主机）
        Second = 2,     // 主机下场时的第二位选手
        Spec = 3,       // 观战者（含排队等待上场与只观战）
        Mp = 4          // 多人混战玩家（编号 2..MpCfg.Max）
    }

    enum PeerRoute
    {
        Direct = 0,     // 直连（UDP 端点，或带 Steam 的编译里经 Steam 的连接）
        Via = 1,        // 经主机或服务器转发
        Bridge = 2      // 按需升级到桥接通道
    }

    interface IPeerTableHost
    {
        PeerTable Peers { get; }
    }

    // 对端表里的一行：一个对端（对手 / 第二位选手 / 观战者 / 多人玩家）
    sealed class PeerRow
    {
        public int Pid;                 // 编号：1 = 对手/主机，2..N = 第二位选手或多人玩家，0 = 观战者
        public PeerRole Role;           // 当前角色
        public object Handle;           // 传输句柄：IPEndPoint、uint（Steam 连接句柄）或标记对象
        public PeerRoute Route;         // 路径：Direct / Via
        public int LastSeq = -1;        // 收到的最大状态包序号
        public DateTime LastRx = DateTime.MinValue;     // 最后收包时间（UTC）
        public int QueueOrder;          // 观战排队次序（1 起；0 = 只观战不排队或非观战者）
        public bool JustSteppedDown;    // 刚下场标记（自动补位时跳过，切回排队或有人上场后清掉）
        public bool Dz;                 // 对端会不会拼省流包（握手或状态包带 dzv=1）
        public SpecDelta DzOut;         // 发给该对端的省流编码器（可空）
        public RelChannel Rel;          // 可靠通道占位（M1 可空，M2 拆成每对端一条）
        public string Nick = "";        // 状态包里的昵称（nm=）
        public ulong BridgeId;           // SteamID64（BridgeLink 用，UDP 为 0）
        public double DirectPing { get; set; }  // M2 直连延迟（毫秒，-1 = 未测或不通）
        public double DirectLoss { get; set; }  // M2 直连丢包率（0.0 ~ 1.0）
        public int DirectMaxLoss { get; set; }  // M2 直连最长连续丢包
        public double ViaPing { get; set; }     // M2 中转延迟（毫秒）
        public double ViaLoss { get; set; }     // M2 中转丢包率
        public int ViaMaxLoss { get; set; }     // M2 中转最长连续丢包
        public object DirectHandle { get; set; } // M2 直连句柄（uint 或 IPEndPoint）

        public PeerRow(int pid, PeerRole role, object handle, PeerRoute route)
        {
            Pid = pid;
            Role = role;
            Handle = handle;
            Route = route;
            Rel = null;
            DirectPing = -1;
            ViaPing = -1;
        }

        public void ResetSeq()
        {
            LastSeq = -1;
        }

        public void ResetDz()
        {
            DzOut = null;
        }

        // 收包序号去重：乱序到达的旧包（落后 100 以内）丢弃；回退超过 100 视为对方重开局/重启，照收。
        public bool TakeSeq(int seq, out int gap)
        {
            gap = 0;
            if (seq <= LastSeq && seq > LastSeq - 100) return false;
            if (LastSeq >= 0 && seq > LastSeq + 1) gap = seq - LastSeq - 1;
            LastSeq = seq;
            return true;
        }

        public string EncodeDz(string body, int count, DateTime now)
        {
            if (DzOut == null) DzOut = new SpecDelta();
            return DzOut.Encode(body, count, now);
        }
    }

    // 统一对端表：管理 Peer（对手）、Second（第二位选手）、Specs（观战与排队）、Mp（多人玩家）以及多路序号和省流编码器。
    // 锁范围：内部用 sync 互斥，公开 SyncRoot 供传输层与额外状态（如 BridgeLink.pend / denied）共用同一把锁，锁范围不小于重构前。
    sealed class PeerTable
    {
        readonly object sync = new object();
        public object SyncRoot { get { return sync; } }

        PeerRow peerRow;
        PeerRow secondRow;
        readonly Dictionary<object, PeerRow> specs = new Dictionary<object, PeerRow>();
        readonly List<object> order = new List<object>();
        object lastDown;
        readonly Dictionary<object, PeerRow> mp = new Dictionary<object, PeerRow>();
        readonly Dictionary<string, int> srcSeq = new Dictionary<string, int>();
        readonly Dictionary<string, PeerRow> specStreams = new Dictionary<string, PeerRow>();

        void RefreshQueueOrder()
        {
            foreach (var kv in specs) kv.Value.QueueOrder = 0;
            int pos = 0;
            for (int i = 0; i < order.Count; i++)
            {
                PeerRow r;
                if (specs.TryGetValue(order[i], out r)) r.QueueOrder = ++pos;
            }
        }

        void SetLastDownInternal(object h)
        {
            lastDown = h;
            foreach (var kv in specs) kv.Value.JustSteppedDown = h != null && kv.Key.Equals(h);
        }

        public object LastDown
        {
            get { lock (sync) return lastDown; }
            set { lock (sync) SetLastDownInternal(value); }
        }

        // ---- 对手（Peer）与第二位选手（Second）
        public PeerRow PeerRow { get { lock (sync) return peerRow; } }
        public PeerRow SecondRow { get { lock (sync) return secondRow; } }

        public object PeerHandle { get { lock (sync) return peerRow != null ? peerRow.Handle : null; } }
        public object SecondHandle { get { lock (sync) return secondRow != null ? secondRow.Handle : null; } }

        public T GetPeer<T>() where T : class { lock (sync) return peerRow != null ? peerRow.Handle as T : null; }
        public T GetSecond<T>() where T : class { lock (sync) return secondRow != null ? secondRow.Handle as T : null; }

        public uint PeerConn { get { lock (sync) return peerRow != null && peerRow.Handle is uint ? (uint)peerRow.Handle : 0u; } }
        public uint SecondConn { get { lock (sync) return secondRow != null && secondRow.Handle is uint ? (uint)secondRow.Handle : 0u; } }

        public ulong PeerBridgeId
        {
            get { lock (sync) return peerRow != null ? peerRow.BridgeId : 0UL; }
            set { lock (sync) { if (peerRow != null) peerRow.BridgeId = value; } }
        }

        public ulong SecondBridgeId
        {
            get { lock (sync) return secondRow != null ? secondRow.BridgeId : 0UL; }
            set { lock (sync) { if (secondRow != null) secondRow.BridgeId = value; } }
        }

        public string PeerNick
        {
            get { lock (sync) return peerRow != null && peerRow.Nick.Length > 0 ? peerRow.Nick : null; }
            set { lock (sync) { if (peerRow != null && !string.IsNullOrEmpty(value)) peerRow.Nick = value; } }
        }

        public bool HasPeer { get { lock (sync) return peerRow != null && peerRow.Handle != null; } }
        public bool HasSecond { get { lock (sync) return secondRow != null && secondRow.Handle != null; } }

        public void SetPeer(object handle, PeerRoute route)
        {
            SetPeer(handle, route, 0UL, null);
        }

        public void SetPeer(object handle, PeerRoute route, ulong bridgeId, string nick)
        {
            lock (sync)
            {
                if (handle == null || (handle is uint && (uint)handle == 0u))
                {
                    peerRow = null;
                    return;
                }
                if (peerRow != null && peerRow.Handle != null && peerRow.Handle.Equals(handle))
                {
                    peerRow.Route = route;
                    if (bridgeId != 0UL) peerRow.BridgeId = bridgeId;
                    if (!string.IsNullOrEmpty(nick)) peerRow.Nick = nick;
                    return;
                }
                var prevRx = peerRow != null ? peerRow.LastRx : DateTime.MinValue;
                var prevNick = peerRow != null ? peerRow.Nick : "";
                peerRow = new PeerRow(1, PeerRole.Peer, handle, route)
                {
                    LastRx = prevRx,
                    BridgeId = bridgeId,
                    Nick = !string.IsNullOrEmpty(nick) ? nick : prevNick
                };
            }
        }

        public void SetSecond(object handle, PeerRoute route, ulong bridgeId)
        {
            lock (sync)
            {
                if (handle == null || (handle is uint && (uint)handle == 0u))
                {
                    secondRow = null;
                    return;
                }
                secondRow = new PeerRow(2, PeerRole.Second, handle, route)
                {
                    BridgeId = bridgeId
                };
            }
        }

        public DateTime LastFromPeer
        {
            get { lock (sync) return peerRow != null ? peerRow.LastRx : DateTime.MinValue; }
            set
            {
                lock (sync)
                {
                    if (peerRow == null) peerRow = new PeerRow(1, PeerRole.Peer, null, PeerRoute.Direct);
                    peerRow.LastRx = value;
                }
            }
        }

        public DateTime LastFromSecond
        {
            get { lock (sync) return secondRow != null ? secondRow.LastRx : DateTime.MinValue; }
            set
            {
                lock (sync)
                {
                    if (secondRow == null) secondRow = new PeerRow(2, PeerRole.Second, null, PeerRoute.Direct);
                    secondRow.LastRx = value;
                }
            }
        }

        public double SinceFromPeer
        {
            get { lock (sync) return (DateTime.UtcNow - (peerRow != null ? peerRow.LastRx : DateTime.MinValue)).TotalSeconds; }
        }

        public double SinceFromSecond
        {
            get { lock (sync) return (DateTime.UtcNow - (secondRow != null ? secondRow.LastRx : DateTime.MinValue)).TotalSeconds; }
        }

        public void ResetPeerSeq()
        {
            lock (sync)
            {
                if (peerRow != null) peerRow.ResetSeq();
            }
        }

        public void ResetSecondSeq()
        {
            lock (sync)
            {
                if (secondRow != null) secondRow.ResetSeq();
            }
        }

        public bool TakePeerSeq(int seq, out int gap)
        {
            lock (sync)
            {
                if (peerRow == null) peerRow = new PeerRow(1, PeerRole.Peer, null, PeerRoute.Direct);
                return peerRow.TakeSeq(seq, out gap);
            }
        }

        public bool TakeSecondSeq(int seq, out int gap)
        {
            lock (sync)
            {
                if (secondRow == null) secondRow = new PeerRow(2, PeerRole.Second, null, PeerRoute.Direct);
                return secondRow.TakeSeq(seq, out gap);
            }
        }

        // 观战者 / 多人客机：按 src= 分路去重
        public void ClearSrcSeq()
        {
            lock (sync)
            {
                srcSeq.Clear();
                if (peerRow != null) peerRow.ResetSeq();
            }
        }

        public bool TakeSrcSeq(string src, int seq, out int gap)
        {
            gap = 0;
            lock (sync)
            {
                int ls;
                if (!srcSeq.TryGetValue(src, out ls)) ls = -1;
                if (seq <= ls && seq > ls - 100) return false;
                if (ls >= 0 && seq > ls + 1) gap = seq - ls - 1;
                srcSeq[src] = seq;
                return true;
            }
        }

        // ---- 观战者（Spec）与排队（QueueOrder）
        public int SpecCount { get { lock (sync) return specs.Count; } }
        public List<PeerRow> SpecRows { get { lock (sync) return new List<PeerRow>(specs.Values); } }

        public bool SpecAlive(object handle)
        {
            lock (sync) return handle != null && specs.ContainsKey(handle);
        }

        public PeerRow FindSpec(object handle)
        {
            lock (sync)
            {
                PeerRow r;
                return handle != null && specs.TryGetValue(handle, out r) ? r : null;
            }
        }

        public PeerRow FindByHandle(object handle)
        {
            if (handle == null) return null;
            lock (sync)
            {
                if (peerRow != null && handle.Equals(peerRow.Handle)) return peerRow;
                if (secondRow != null && handle.Equals(secondRow.Handle)) return secondRow;
                PeerRow r;
                if (specs.TryGetValue(handle, out r)) return r;
                if (mp.TryGetValue(handle, out r)) return r;
                return null;
            }
        }

        public bool RemoveSpec(object handle)
        {
            lock (sync)
            {
                if (handle == null || !specs.Remove(handle)) return false;
                order.Remove(handle);
                if (lastDown != null && lastDown.Equals(handle)) lastDown = null;
                RefreshQueueOrder();
                return true;
            }
        }

        public void ClearSpecs()
        {
            lock (sync)
            {
                specs.Clear();
                order.Clear();
                lastDown = null;
            }
        }

        // 清理超时的观战者，返回剩余观战人数，被清理的行放进 gone
        public int PruneSpecs(double lostSec, List<PeerRow> gone)
        {
            lock (sync)
            {
                var now = DateTime.UtcNow;
                foreach (var kv in specs)
                    if ((now - kv.Value.LastRx).TotalSeconds > lostSec) gone.Add(kv.Value);
                for (int i = 0; i < gone.Count; i++)
                {
                    var h = gone[i].Handle;
                    specs.Remove(h);
                    order.Remove(h);
                    if (lastDown != null && lastDown.Equals(h)) lastDown = null;
                }
                if (gone.Count > 0) RefreshQueueOrder();
                return specs.Count;
            }
        }

        public bool SpecDzAll
        {
            get
            {
                lock (sync)
                {
                    if (specs.Count == 0) return false;
                    foreach (var kv in specs) if (!kv.Value.Dz) return false;
                    return true;
                }
            }
        }

        public List<T> SpecHandles<T>()
        {
            lock (sync)
            {
                var l = new List<T>(specs.Count);
                foreach (var k in specs.Keys) l.Add((T)k);
                return l;
            }
        }

        public List<object> QueueOrderObj()
        {
            lock (sync)
            {
                var l = new List<object>(order.Count);
                for (int i = 0; i < order.Count; i++)
                    if (specs.ContainsKey(order[i])) l.Add(order[i]);
                return l;
            }
        }

        public List<T> QueueOrder<T>()
        {
            lock (sync)
            {
                var l = new List<T>(order.Count);
                for (int i = 0; i < order.Count; i++)
                    if (specs.ContainsKey(order[i])) l.Add((T)order[i]);
                return l;
            }
        }

        public bool CanPromote
        {
            get
            {
                lock (sync)
                {
                    for (int i = 0; i < order.Count; i++)
                    {
                        var h = order[i];
                        if (specs.ContainsKey(h) && !h.Equals(lastDown)) return true;
                    }
                    return false;
                }
            }
        }

        // 记录观战者包/握手：返回 0 = 不是观战者，-1 = 超出上限被拒，1 = 已在观战列表并更新，2 = 新加入观战
        public int NoteSpec(object from, string s, int maxSpec, ulong bridgeId)
        {
            bool isHello = Link.IsSpecHello(s);
            bool noq = s.Contains("\nnoq=1");
            bool up = s.Contains("\nup=1");
            bool dzv = s.Contains("\ndzv=1");
            lock (sync)
            {
                PeerRow r;
                if (specs.TryGetValue(from, out r))
                {
                    r.LastRx = DateTime.UtcNow;
                    if (bridgeId != 0UL) r.BridgeId = bridgeId;
                    if (isHello)
                    {
                        r.Dz = dzv;
                        if (up && from.Equals(lastDown)) SetLastDownInternal(null);
                        if (noq) order.Remove(from);
                        else if (!order.Contains(from)) order.Add(from);
                        RefreshQueueOrder();
                    }
                    return 1;
                }
                if (!isHello) return 0;
                if (specs.Count >= maxSpec) return -1;
                r = new PeerRow(0, PeerRole.Spec, from, PeerRoute.Direct)
                {
                    LastRx = DateTime.UtcNow,
                    Dz = dzv,
                    BridgeId = bridgeId
                };
                specs[from] = r;
                order.Remove(from);
                if (!noq) order.Add(from);
                RefreshQueueOrder();
                return 2;
            }
        }

        // 队首上场当对手（lostSec = 观战保活超时秒数；Steam 传 double.MaxValue 由 SpecTick 统一超时）
        public PeerRow PromoteHead(double lostSec, out PeerRow prevPeer)
        {
            lock (sync)
            {
                prevPeer = peerRow;
                PeerRow head = null;
                var now = DateTime.UtcNow;
                for (int i = 0; i < order.Count; i++)
                {
                    var h = order[i];
                    PeerRow r;
                    if (specs.TryGetValue(h, out r) && (now - r.LastRx).TotalSeconds <= lostSec && !h.Equals(lastDown))
                    {
                        head = r;
                        break;
                    }
                }
                if (head == null) return null;
                specs.Remove(head.Handle);
                order.Remove(head.Handle);
                head.Pid = 1;
                head.Role = PeerRole.Peer;
                head.LastSeq = -1;
                head.LastRx = now;
                head.QueueOrder = 0;
                head.JustSteppedDown = false;
                head.DzOut = null;
                peerRow = head;
                SetLastDownInternal(null);
                RefreshQueueOrder();
                return head;
            }
        }

        // 现在的对手下场，排到队尾当观战者
        public PeerRow DemotePeer(object fallbackHandle)
        {
            lock (sync)
            {
                if ((peerRow == null || peerRow.Handle == null) && fallbackHandle != null)
                    peerRow = new PeerRow(1, PeerRole.Peer, fallbackHandle, PeerRoute.Direct);
                return DemotePeer();
            }
        }

        public PeerRow DemotePeer()
        {
            lock (sync)
            {
                var old = peerRow;
                if (old == null || old.Handle == null || (old.Handle is uint && (uint)old.Handle == 0u)) return null;
                old.Pid = 0;
                old.Role = PeerRole.Spec;
                old.LastRx = DateTime.UtcNow;
                old.DzOut = null;
                specs[old.Handle] = old;
                order.Remove(old.Handle);
                order.Add(old.Handle);
                peerRow = null;
                SetLastDownInternal(old.Handle);
                RefreshQueueOrder();
                return old;
            }
        }

        // 队首上场当第二位选手（主机下场转发）
        public PeerRow PromoteSecond(double lostSec)
        {
            lock (sync)
            {
                PeerRow head = null;
                var now = DateTime.UtcNow;
                for (int i = 0; i < order.Count; i++)
                {
                    var h = order[i];
                    PeerRow r;
                    if (specs.TryGetValue(h, out r) && (now - r.LastRx).TotalSeconds <= lostSec && !h.Equals(lastDown))
                    {
                        head = r;
                        break;
                    }
                }
                if (head == null) return null;
                specs.Remove(head.Handle);
                order.Remove(head.Handle);
                head.Pid = 2;
                head.Role = PeerRole.Second;
                head.LastSeq = -1;
                head.LastRx = now;
                head.QueueOrder = 0;
                head.JustSteppedDown = false;
                head.DzOut = null;
                secondRow = head;
                SetLastDownInternal(null);
                RefreshQueueOrder();
                return head;
            }
        }

        // 第二位选手下场回观战队尾
        public PeerRow DemoteSecond(object fallbackHandle)
        {
            lock (sync)
            {
                if ((secondRow == null || secondRow.Handle == null) && fallbackHandle != null)
                    secondRow = new PeerRow(2, PeerRole.Second, fallbackHandle, PeerRoute.Direct);
                return DemoteSecond();
            }
        }

        public PeerRow DemoteSecond()
        {
            lock (sync)
            {
                var old = secondRow;
                if (old == null || old.Handle == null || (old.Handle is uint && (uint)old.Handle == 0u)) return null;
                old.Pid = 0;
                old.Role = PeerRole.Spec;
                old.LastRx = DateTime.UtcNow;
                old.DzOut = null;
                specs[old.Handle] = old;
                order.Remove(old.Handle);
                order.Add(old.Handle);
                secondRow = null;
                SetLastDownInternal(old.Handle);
                RefreshQueueOrder();
                return old;
            }
        }

        // 第一位走了、没人排队：第二位选手直接改当第一位对手
        public PeerRow SecondToPeer(out PeerRow prevPeer)
        {
            lock (sync)
            {
                prevPeer = peerRow;
                if (secondRow == null || secondRow.Handle == null || (secondRow.Handle is uint && (uint)secondRow.Handle == 0u))
                    return null;
                var s = secondRow;
                secondRow = null;
                s.Pid = 1;
                s.Role = PeerRole.Peer;
                s.LastSeq = -1;
                s.DzOut = null;
                peerRow = s;
                return s;
            }
        }

        // ---- 多人混战（Mp，编号 2..MpCfg.Max）
        public List<KeyValuePair<int, string>> MpPlayers(double lostSec, List<PeerRow> gone)
        {
            var list = new List<KeyValuePair<int, string>>();
            lock (sync)
            {
                var now = DateTime.UtcNow;
                foreach (var kv in mp)
                    if ((now - kv.Value.LastRx).TotalSeconds > lostSec) gone.Add(kv.Value);
                for (int i = 0; i < gone.Count; i++) mp.Remove(gone[i].Handle);
                foreach (var kv in mp) list.Add(new KeyValuePair<int, string>(kv.Value.Pid, kv.Value.Nick));
            }
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            return list;
        }

        public List<T> MpHandlesExcept<T>(int exceptPid)
        {
            var to = new List<T>();
            lock (sync)
                foreach (var kv in mp)
                    if (kv.Value.Pid != exceptPid) to.Add((T)kv.Key);
            return to;
        }

        public List<KeyValuePair<T, int>> MpSnapshot<T>()
        {
            var to = new List<KeyValuePair<T, int>>();
            lock (sync)
                foreach (var kv in mp)
                    to.Add(new KeyValuePair<T, int>((T)kv.Key, kv.Value.Pid));
            return to;
        }

        // 多人混战收包登记：返回 0 = 不认识且不是多人握手，-1 = 满员被拒，1 = 已有玩家更新，2 = 新玩家分配编号
        public int NoteMp(object from, bool isMpHello, int maxPlayers, int limitPlayers, double lostSec, out PeerRow row, ICollection<int> extraUsed = null, int preferredPid = 0)
        {
            lock (sync)
            {
                DateTime now0 = DateTime.UtcNow;
                if (!mp.TryGetValue(from, out row))
                {
                    if (preferredPid > 0)
                    {
                        row = new PeerRow(preferredPid, PeerRole.Mp, from, PeerRoute.Direct) { LastRx = now0 };
                        mp[from] = row;
                        return 1;
                    }
                    if (!isMpHello) return 0;
                    var used = new HashSet<int>();
                    foreach (var kv in mp)
                        if ((now0 - kv.Value.LastRx).TotalSeconds <= lostSec) used.Add(kv.Value.Pid);
                    if (extraUsed != null)
                        foreach (var ep in extraUsed) used.Add(ep);
                    int pid = 0;
                    int cap = Math.Min(maxPlayers, limitPlayers);
                    // §316 按人头数挡：房主可以把上限设得比现在的人数少（在的人不赶），这时有空编号也不收，
                    // 等人数降到上限以下才收（以前只看 2..cap 里有没有空编号：上限 3、房里 5 人、2 号走了，新人照样能进 2 号）。
                    // 网状（extraUsed 带着在场成员和我自己）= used 就是全部人；旧路径房主自己是 1 号不在 mp 里，补 1
                    int people = used.Count + (extraUsed == null && !used.Contains(1) ? 1 : 0);
                    if (people >= cap) return -1;
                    for (int i = 2; i <= cap; i++)
                        if (!used.Contains(i)) { pid = i; break; }
                    if (pid == 0 && !used.Contains(1) && (extraUsed == null || extraUsed.Count > 0)) pid = 1;     // §305 网状里 1 号只在真的空着时补（extraUsed 带着在场成员和我自己）
                    if (pid == 0) return -1;
                    var stale = new List<object>();
                    foreach (var kv in mp)
                        if (kv.Value.Pid == pid) stale.Add(kv.Key);
                    for (int i = 0; i < stale.Count; i++) mp.Remove(stale[i]);
                    row = new PeerRow(pid, PeerRole.Mp, from, PeerRoute.Direct) { LastRx = now0 };
                    mp[from] = row;
                    return 2;
                }
                row.LastRx = DateTime.UtcNow;
                return 1;
            }
        }

        public bool TakeMpState(PeerRow row, int seq, string nick)
        {
            lock (sync)
            {
                int gap;
                if (!row.TakeSeq(seq, out gap)) return false;
                if (nick != null) row.Nick = nick;
                return true;
            }
        }

        // 连接断了：把这个多人玩家从表里拿掉（Steam 用；UDP 靠 MpCfg.Lost 超时）
        public bool RemoveMp(object handle, out int pid)
        {
            pid = 0;
            lock (sync)
            {
                PeerRow r;
                if (handle == null || !mp.TryGetValue(handle, out r)) return false;
                pid = r.Pid;
                mp.Remove(handle);
                return true;
            }
        }

        // ---- 省流编码器（按对端行 / 观战来源行存）
        public void ResetRelayDz()
        {
            lock (sync)
            {
                if (peerRow != null) { peerRow.Dz = false; peerRow.DzOut = null; }
                if (secondRow != null) { secondRow.Dz = false; secondRow.DzOut = null; }
            }
        }

        public void SetFighterDz(bool firstFighter, bool dzv)
        {
            lock (sync)
            {
                if (firstFighter)
                {
                    if (peerRow == null) peerRow = new PeerRow(1, PeerRole.Peer, null, PeerRoute.Via);
                    peerRow.Dz = dzv;
                }
                else
                {
                    if (secondRow == null) secondRow = new PeerRow(2, PeerRole.Second, null, PeerRoute.Via);
                    secondRow.Dz = dzv;
                }
            }
        }

        public bool GetFighterDz(bool firstFighter)
        {
            lock (sync)
            {
                var r = firstFighter ? peerRow : secondRow;
                return r != null && r.Dz;
            }
        }

        public SpecDelta EnsureFighterDzOut(bool toSecond, bool enable)
        {
            lock (sync)
            {
                var r = toSecond ? secondRow : peerRow;
                if (!enable) { if (r != null) r.DzOut = null; return null; }
                if (r == null)
                {
                    r = new PeerRow(toSecond ? 2 : 1, toSecond ? PeerRole.Second : PeerRole.Peer, null, PeerRoute.Via);
                    if (toSecond) secondRow = r; else peerRow = r;
                }
                if (r.DzOut == null) r.DzOut = new SpecDelta();
                return r.DzOut;
            }
        }

        public void ResetSpecStreamDz(string src)
        {
            lock (sync)
            {
                PeerRow r;
                if (specStreams.TryGetValue(src, out r)) r.DzOut = null;
            }
        }

        public string EncodeSpecStreamDz(string src, string body, int specCount, DateTime now)
        {
            lock (sync)
            {
                PeerRow r;
                if (!specStreams.TryGetValue(src, out r))
                {
                    r = new PeerRow(0, PeerRole.Spec, src, PeerRoute.Via);
                    specStreams[src] = r;
                }
                return r.EncodeDz(body, specCount, now);
            }
        }
    }
}
