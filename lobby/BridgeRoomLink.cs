// 0.34（TASK_0.34 最终方案 3）：这个类泛化成"经大厅服务器登记 / 介绍、对局经 " + Bridge.Svc + " 连"的房间——房主开了房间设置"对局走 " + Bridge.Svc时，
// 服务器大厅和武林盟主都用它（Ranked 决定 REG 带不带排位 pid）；没开就走 ServerLink（UDP 直连，0.34 起不中转）。REG 第 16 段 = 1 告诉服务器这是 Steam 房，
// 服务器只把双方 SteamID 互相介绍一次（内存里介绍完即弃）。排位 0.34 起默认也是直连，不再隐含走 Steam（§135 的"只走 " + Bridge.Svc作废）。
// 下面是 §135 原注释：
// §135 武林盟主的对局只走 Steam（用户 2026-11-07："先做对战只走 steam，其他统计数据还是上报给服务器"；这个模式只有带 Steam 的
// 内测包里有）。只在带 Steam 的编译里（build.bat 开发版 / AOT），发布版没有这个文件，也没有武林盟主。
//
// 大厅服务器那一侧和服务器大厅是同一套 4N 协议（登记房间 / 房间列表 / 客机加入拿会话号），所以排位上报、记分、统计都不用改；
// 但不打洞、不中转，对局数据（状态包、可靠事件）全走 BridgeLink：
//   主机：4N|REG|<密钥>|<名字>|<地图>|<已满>||0|1|<pid>|<自己的 SteamID>  每 2 秒；Steam 监听，只接受服务器 PEER 介绍的那个 SteamID
//         （桥开监听时会把富状态设成"在开房"，这里马上清掉：武林盟主房间不出现在 Steam 好友列表里；Steam 大厅只有 Steam 大厅方式才建，
//         也不会混进去。好友直接连进来也会被拒——不是从武林盟主房间加入的，没有会话号也记不了分）
//   客机：4N|JOIN|<房间号>||<pid>|<自己的 SteamID>，收到 4N|PEER|…|<主机 SteamID>（第 7 段）后 Steam 连主机
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ForNet
{
    class BridgeRoomLink : ITransport, IRoomLink
    {
        const double RegEvery = 2.0, JoinEvery = 1.0, QuietAfter = 6.0;

        readonly UdpClient udp;
        IPEndPoint server;              // §327 = pick.Cur
        readonly ServerPick pick;
        readonly bool host;
        readonly string name, key;
        readonly RemoteOut outq;
        BridgeLink steam;
        public ulong PeerBridgeId { get { var st = steam; return st != null ? st.PeerBridgeId : 0; } }     // §174
        string roomId = "", sid, error = "";
        ulong allowed;                  // 主机：服务器介绍的对手 SteamID
        DateTime lastReg = DateTime.MinValue, lastJoin = DateTime.MinValue, lastFromServer = DateTime.MinValue, startedAt;
        Action<string> log = delegate { };
        Wire wire = new Wire(null);

        // host：roomOrNull = null；client：要加入的房间号
        public BridgeRoomLink(IPEndPoint server, string roomOrNull, string name, string fRemote)
        {
            this.server = server;
            this.name = name;
            outq = new RemoteOut(fRemote);
            host = roomOrNull == null;
            if (!host) roomId = roomOrNull.Trim().ToUpperInvariant();
            key = Guid.NewGuid().ToString("N");
            MapOf = () => "?";
            ServerName = "";
            udp = NetBind.QueryUdp(server);     // 只和大厅服务器说话，端口随便；§327 IPv4 / IPv6 都能发
            try { udp.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch (Exception) { }   // 同 Link：忽略 ICMP 端口不可达
            pick = new ServerPick(server, udp);
            startedAt = DateTime.UtcNow;
            if (host)
            {
                steam = new BridgeLink(0, outq);
                steam.AllowPeer = id => id != 0 && id == allowed;
                // 桥开监听时顺手把富状态 fornet 设成 host（Steam 好友方式靠它列"在开房"）：武林盟主的房间不该出现在好友列表里
                // （好友连进来也会被 AllowPeer 拒掉），清掉（用户 2026-11-07"会不会跟 steam 大厅里的混在一起"）
                Bridge.SetHosting(false);
                Hook();
            }
        }

        void Hook()
        {
            steam.Log = log;
            steam.Wire = wire;
            steam.OnPacket = TakeNewest;
        }

        // ---- IRoomLink
        public string RoomId { get { return roomId; } }
        public string Sid { get { return sid; } }
        public string Error { get { return error; } }
        public string RankPid { get; set; }
        public bool Ranked { get; set; }        // 0.34：排位房（REG 第 9 段 1 + pid）还是普通 Steam 房
        public string PeerRk { get; private set; }
        public string PeerMt { get; private set; }
        public string PeerQuit { get; private set; }
        public Action<Dictionary<string, string>> PeerPacket { get; set; }
        public string ServerName { get; set; }
        public Func<string> MapOf { get; set; }

        // 面板的线路：主机 wait（等人）/ steam；客机 join（向服务器报到）/ steamwait（Steam 连接中）/ steam
        public string Route
        {
            get
            {
                if (steam != null && steam.Linked) return "bridge";
                if (host) return "wait";
                return sid == null ? "join" : "bridgewait";
            }
        }

        // 对方最新的状态包：排位上报看 rk= / mt= / quit=，统计看 res=（同 ServerLink.TakeNewest）
        void TakeNewest(string s)
        {
            var d = Kv.Parse(s);
            string v;
            if (d.TryGetValue("rk", out v)) PeerRk = v;
            if (d.TryGetValue("mt", out v)) PeerMt = v;
            if (d.TryGetValue("quit", out v)) PeerQuit = v;
            var cb = PeerPacket;
            if (cb != null) cb(d);
        }

        // ---- ITransport：数据全交给 BridgeLink（客机拿到主机的 SteamID 之前还没有）
        public void Send(byte[] b) { var st = steam; if (st != null) st.Send(b); }

        public int Pump()
        {
            var now = DateTime.UtcNow;
            FromServer(now);
            if (pick.Tick(now)) { server = pick.Cur; lastReg = lastJoin = DateTime.MinValue; NetLog.W("桥接房：大厅服务器没回应，改试 " + ServerPick.Fam(server) + " 地址"); }     // §327
            if (host)
            {
                if ((now - lastReg).TotalSeconds > RegEvery)
                {
                    lastReg = now;
                    bool full = steam != null && steam.Linked;
                    Raw(WireTag.S + "|REG|" + key + "|" + LobbyServer.Clean(name) + "|" + LobbyServer.Clean(MapOf()) + "|" + (full ? "1" : "0")
                        + "||0|" + (Ranked ? "1|" + RankPid : "0|") + "|" + Bridge.MyId + "|0||0|" + Ident.MyRoomHash + "|1");     // §174 第 15 段 = 主机标记；0.34 第 16 段 = 对局走 Steam
                }
            }
            else if (sid == null && (now - lastJoin).TotalSeconds > JoinEvery)
            {
                lastJoin = now;
                Raw(WireTag.S + "|JOIN|" + roomId + "||" + (Ranked ? RankPid : "") + "|" + Bridge.MyId);
            }
            if ((now - startedAt).TotalSeconds > QuietAfter && (now - lastFromServer).TotalSeconds > QuietAfter && (host || sid == null))
                error = L.T("大厅服务器没回应：", "The lobby server is not answering: ") + LobbyServer.Show(ServerName, server);
            return steam != null ? steam.Pump() : 0;
        }

        void Raw(string text)
        {
            var b = Kv.Utf8.GetBytes(text);
            try { udp.Send(b, b.Length, server); } catch (Exception) { }
        }

        void FromServer(DateTime now)
        {
            while (true)
            {
                int avail;
                try { avail = udp.Available; } catch (Exception) { return; }
                if (avail <= 0) return;
                IPEndPoint from = null;
                byte[] data;
                try { data = udp.Receive(ref from); } catch (Exception) { return; }
                from = NetBind.Norm(from);
                if (!pick.Is(from)) continue;     // §327 两个地址都认
                if (pick.Got(from)) server = pick.Cur;
                lastFromServer = now;
                var f = Kv.Utf8.GetString(data).Split('|');
                if (f.Length < 2 || f[0] != WireTag.S) continue;
                switch (f[1])
                {
                    case "REGOK":
                        if (f.Length > 2 && roomId != f[2]) { roomId = f[2]; log(Ranked ? L.T("武林盟主房间已登记：", "Ranked room listed: ") + roomId : L.T("桥接房已登记：", "Bridge room listed: ") + roomId); }
                        error = "";
                        break;
                    case "PEER":     // PEER|房间号|会话号|对方公网|对方局域网|对方 SteamID
                        if (f.Length < 7) { error = L.T("大厅服务器是旧版，不支持\"对局走桥接\"", "The lobby server is too old for bridge rooms"); break; }
                        ulong id;
                        if (!ulong.TryParse(f[6], out id) || id == 0) break;
                        OnPeer(f[3], id);
                        break;
                    case "ERR":
                        var why = f.Length > 3 ? f[3] : "";
                        error = why == "nofound" ? L.T("大厅里没有这个房间（" + roomId + "）", "No such room on the lobby server (" + roomId + ")")
                              : why == "full" ? L.T("房间已满", "The room is full")
                              : why == "noranked" ? L.T("这台大厅服务器没开武林盟主排位", "This lobby server has no ranked mode")
                              : why == "unknown" ? L.T("排位账号还没登记上，稍等几秒", "Ranked account not registered yet, wait a few seconds")
                              : why == "ranked" ? L.T("这是武林盟主排位房间：用\"武林盟主\"方式加入", "This is a ranked room: join it from Ranked")
                              : why == "needsteam" ? L.T("这间房的对局走桥接：你这边的桥接没连上", "This room plays over bridge: your bridge is not connected")
                              : why == "oldhost" ? L.T("房主的联机程序是旧版（不走桥接），请对方更新", "The host has an old version (no bridge); ask them to update")
                              : why == "banned" ? L.T("你被这台大厅服务器禁止参加武林盟主", "You are banned from ranked play on this lobby server")     // §174
                              : L.T("大厅服务器报错：", "Lobby server error: ") + why;
                        break;
                }
            }
        }

        void OnPeer(string newSid, ulong peerBridge)
        {
            if (host)
            {
                if (newSid != sid) log(L.T("有人要加入，等对方经桥接连进来", "A player is joining; waiting for their bridge connection"));
                sid = newSid;
                allowed = peerBridge;
                return;
            }
            if (sid == null) log(L.T("服务器介绍了房主，经桥接连接 ", "Host introduced by the server; connecting via bridge to ") + Bridge.FriendName(peerBridge));
            sid = newSid;
            error = "";
            if (steam == null)
            {
                steam = new BridgeLink(peerBridge, outq);
                Hook();
            }
        }

        public string PeerStr { get { return steam != null ? steam.PeerStr : ""; } }
        public string PeerName { get { return steam != null ? steam.PeerName : ""; } }
        public string PeerNick { get { return steam != null ? steam.PeerNick : null; } }
        public int LocalPort { get { return 0; } }
        public long Tx { get { return steam != null ? steam.Tx : 0; } }
        public long Gap { get { return steam != null ? steam.Gap : 0; } }
        public double PingMs { get { return steam != null ? steam.PingMs : -1; } }
        public RemoteOut Out { get { return outq; } }
        public Action<string> Log
        {
            get { return log; }
            set { log = value ?? delegate { }; if (steam != null) steam.Log = log; }
        }
        public Wire Wire
        {
            get { return wire; }
            set { wire = value ?? new Wire(null); if (steam != null) steam.Wire = wire; }
        }

        public void Close()
        {
            if (host) Raw(WireTag.S + "|UNREG|" + key);
            if (steam != null) steam.Close();
            try { udp.Close(); } catch (Exception) { }
        }
    }
}
