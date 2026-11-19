// 连接方式的辅助（net-ui 分支，docs/NETWORK_UI.md）：模式名、配置文件、UPnP 端口映射、Tailscale 节点、地址解析。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
#if !AOT
using System.Web.Script.Serialization;
#endif

namespace ForNet
{
    // 连接方式。bridge 经游戏里的桥接 DLL（扩展包提供的外部联机服务）连；lan 是 UDP；
    // v0.20.3：广域网（wan）并进服务器大厅——
    // 服务器大厅里"输入 IP 直连"走原来广域网的 UDP 直连；主机监听设置里的端口，没人经大厅加入时也接受直接连进来的客机。
    // v0.20.2：虚拟局域网（vlan）并进局域网——局域网顺带单播给
    // Tailscale 节点、列出节点、显示 / 复制虚拟网卡地址。旧配置 / 旧面板发来的 vlan 一律当 lan（Valid）；
    // server 是自建服务器大厅（UDP 打洞，不通走服务器中转，ServerLobby.cs）。顺序 = 游戏内面板按 End 换方式的顺序
    static class Modes
    {
        // 起开发版默认不列桥接平台两种； （私人内测的人不用手改 netcfg）：
        // 带桥接平台的版本（开发版 / 内测包）改成默认列，netcfg.txt 写桥接平台=0 才藏。公开版不编这段，不受影响。
        // Known = 全部（读"最近连过的"用，藏起来的也留着）
        // 平台好友改走游戏里的桥接 DLL，桥不在（Bridge.Available）时也不列； 的平台大厅 v0.34 起移除（旧配置 steamlobby → 桥接平台）
        public static bool ShowBridge = true;
        public static readonly string[] Known = { "bridge", "server", "ranked", "lan", "ai" };     // v0.20.4 定的顺序； 本地 AI 放最后； 排位跟在服务器大厅后面
        static readonly string[] Plain = { "server", "ranked", "lan", "ai" };
        public static string[] All { get { return RankedSupport.Modes(ShowBridge && Bridge.Available ? Known : Plain); } }     // F4c-1：没编排位就没有 ranked

        public static string Valid(string m) { if (m == "steamlobby") m = "server"; if (m == "steam") m = "bridge"; return m == "wan" ? "server" : Array.IndexOf(All, m) >= 0 ? m : "lan"; }     // 0.34 旧的平台大厅 → 服务器大厅；F4c-2 旧方式名桥接平台 → bridge（面板没有桥接平台标签了）     // vlan → lan、wan → server、steamlobby → 桥接平台（v0.34 删大厅；没桥接平台时自然退 lan）

        // 能开房的方式（AI 对战时等人的房间）：除了 ai 都行，顺序同 All。
        // 排位（ranked）也算（11-19：AI 对练时可以开一个武林盟主房间等真人，房间挂在排位房间列表里）；
        // 没装 / 停用排位扩展包时 All 里本来就没有 ranked，这里跟着没有。真正开房时还要这台大厅服务器开了 --ranked、
        // 账号已登记（Program.StartWait 再查一遍，查不过只提示、AI 对战照常）
        public static string[] Rooms { get { return Array.FindAll(All, x => x != "ai"); } }
        public static string ValidRoom(string m) { m = Valid(m); return Array.IndexOf(Rooms, m) >= 0 ? m : "lan"; }     // ai 不能开等人的房间

        // 要登桥接平台的方式（选了才初始化桥接平台，见 Bridge.cs 文件头）
        public static bool IsBridge(string m) { return m == "bridge"; }

        // 走服务器大厅那一套（ServerLink）的方式：服务器大厅、 排位（排位房间只在服务器大厅里登记）
        public static bool IsServer(string m) { return m == "server" || m == "ranked"; }

        public static string Name(string m)
        {
            switch (m)
            {
                case "bridge": return L.T("桥接好友", "Bridge friends");
                case "server": return L.T("服务器大厅（含 IP 直连）", "Lobby server (incl. direct IP)");
                case "ranked": return L.T(GameInfo.RankedModeZh, "Ranked (via the lobby server)");
                case "ai": return L.T("本地 AI 对战（不联网）", "Local AI battle (offline)");
                default: return L.T("局域网（含 Tailscale / ZeroTier）", "LAN (incl. Tailscale / ZeroTier)");
            }
        }
    }

    // net\netcfg.txt：上次的方式、端口、昵称、UPnP 开关、大厅服务器地址、最近连过的地址（每种方式各 5 个）
    class Cfg
    {
        public string Mode = "lan", Name = Environment.MachineName;
        // 服务器大厅的地址 "域名或IP[:端口]"（默认端口 LobbyServer.DefaultPort）。默认空：发布版不连作者的服务器，
        // 玩家自己架（server/README.md）或填别人架的；netcfg.txt 里有 server= 的以文件为准
        public string Server = "";
        public int Port = Session.GamePort;
        public bool Upnp;
        public bool Relay;          // 服务器大厅：允许走服务器中转（默认关；服务器也得开 --relay）
        public bool BridgeFriends;   // 0.34 最终方案 2：在"最近"页显示平台好友、向好友公开"正在开房"（默认关，开要二次确认 steam_friends）
        public bool HideIp;         // 多人房隐藏我的 IP（netcfg hideip=1）
        public bool Hide;           // 0.34 隐身：登记 / 广播不带主机标记（bh）、不写富状态——别人的"最近" / 好友列表里看不到我
        // 房间设置"对局走桥接"三档：0 关闭 / 1 直连失败时走桥接（默认）/ 2 一直桥接。只管我开的房；
        // 客机一律按主机的设置连（自己设成什么都不管）。用桥接要同意过 bridge_match：1 档没同意就只走直连，2 档开房时会问
        public int BridgeMode = 1;
        public bool BridgeRoom { get { return BridgeMode == 2; } }     // 一直桥接（以前的"对局走桥接平台：开"）
        public bool BridgeUp = true;    // 模式 1 直连慢时自动试桥接（默认开，netcfg bridgeup=0 关）
        public int BridgeUpMs = 150;    // 触发试桥接的直连中位 RTT 阈值（0-2000，默认 150 ms）
        public bool Safe;           // 0.34 安全模式（L0，本地硬开关）：只留局域网 / 本地 AI，不连大厅服务器、不碰桥接平台；不依赖服务器，不会自己恢复
        public int MaxSpec = 4;     // 房间设置：观战 / 排队人数上限（0 = 不让观战；0 / 2 / 4 / 8）
        public bool HostSpec;       // M2 房间设置：主机只观战（解说模式）
        public int AiLevel = 1;     // 本地 AI 难度：0 简单 / 1 普通 / 2 困难（LocalAi.Levels）
        public IAiLoad AiLoad = LocalAiSupport.NewLoad();   // AI 装备（键名由游戏适配定；不合法的值不认，用默认）
        public bool AiRoom;                 // AI 对战时同时开房等真人（开关）
        public string AiRoomMode = "lan";   // 等人的房间用哪种连接方式（不能是 ai）
        public bool Bridge = true;      // 列不列桥接平台两种方式（默认列；netcfg.txt 写桥接平台=0 才藏，见 Modes.ShowBridge）
        public string Secret = "";     // v0.22.0 联机密码（明文存这里：只是两个人对局用的共享口令）；空 = 不签名
        public readonly Dictionary<string, List<string>> Recent = new Dictionary<string, List<string>>();
        readonly string path;

        public Cfg(string dir)
        {
            path = Path.Combine(dir, "netcfg.txt");
            var d = Kv.Read(path);
            if (d == null) return;
            string v;
            int n;
            if (d.TryGetValue("bridge", out v) || d.TryGetValue("steam", out v)) Bridge = v == "1";     // 旧配置键桥接平台= 照认     // 要在认 mode 之前：藏着时 mode=桥接平台会被当成 lan
            // 桥接平台两种方式不见了。（11-06，默认藏）那几版存配置时自动写了桥接平台=0，
            // 改回默认列以后这行还在、照样藏。新版存配置时多写 steamset=1：没有它的桥接平台=0 当成旧版自动写的、不算（照常列），
            // 有它的桥接平台=0 才是玩家自己关的
            string set;
            if (!Bridge && !d.TryGetValue("bridgeset", out set) && !d.TryGetValue("steamset", out set)) Bridge = true;
            Modes.ShowBridge = Bridge;
            if (d.TryGetValue("mode", out v)) { Mode = Modes.Valid(v); if (Mode == "bridge") Mode = "server"; }     // 0.34 评审：桥接平台方式只在从"最近"页连好友时临时用，启动时落到服务器大厅
            if (d.TryGetValue("name", out v) && v.Length > 0) Name = v;
            if (d.TryGetValue("port", out v) && int.TryParse(v, out n) && n > 0 && n < 65536) Port = n;
            if (d.TryGetValue("upnp", out v)) Upnp = v == "1";
            if (d.TryGetValue("relay", out v)) Relay = v == "1";
            if (d.TryGetValue("bridgefriends", out v) || d.TryGetValue("steamfriends", out v)) BridgeFriends = v == "1";     // 旧键 steamfriends= 照认     // 0.34
            if (d.TryGetValue("hide", out v)) { Hide = v == "1"; Ident.Hidden = Hide; }
            if (d.TryGetValue("hideip", out v)) { HideIp = v == "1"; ForNet.MpMesh.HideIpPref = HideIp; }
            // 新键 bridgemode=0/1/2；旧键 bridgeroom= / steamroom=：1（以前的"开"）= 一直桥接，0（以前的默认关）= 新默认 1
            int bm;
            if (d.TryGetValue("bridgemode", out v) && int.TryParse(v, out bm) && bm >= 0 && bm <= 2) BridgeMode = bm;
            else if (d.TryGetValue("bridgeroom", out v) || d.TryGetValue("steamroom", out v)) BridgeMode = v == "1" ? 2 : 1;     // 旧键 steamroom= 照认     // 0.34（公开版没有桥接平台，内测版留下的 steamroom=1 不认）
            if (d.TryGetValue("safe", out v)) Safe = v == "1";     // 0.34 L0
            if (d.TryGetValue("ipv6", out v)) NetBind.V6 = v != "0";     // 手动写 ipv6=0 退回只用 IPv4（面板里没有这项）
            ForNet.Bridge.AllowPresence(BridgeFriends && !Hide && !Safe);     // 0.34 窗口 / 命令行两条路都经这里（安全模式启动时也不开）（Cfg 里桥接平台是字段名，要写全名）：桥连上后才真的发 R
            if (d.TryGetValue("maxspec", out v) && int.TryParse(v, out n) && n >= 0 && n <= 16) MaxSpec = n;     // 对决可到 16
            SpecCfg.Max = Math.Min(MaxSpec, SpecCfg.OldMax);     // 旧 1v1 观战仍最多 8
            if (d.TryGetValue("specnoq", out v)) SpecCfg.NoQueue = v == "1";
            if (d.TryGetValue("hostspec", out v)) HostSpec = v == "1";
            HostSpecCfg.On = HostSpec;
            if (d.TryGetValue("dz", out v)) DzCfg.On = v != "0";     // 联机省流（主机的房间设置，默认开）
            if (d.TryGetValue("bridgeup", out v)) BridgeUp = v != "0";
            if (d.TryGetValue("bridgeupms", out v) && int.TryParse(v, out n) && n >= 0 && n <= 2000) BridgeUpMs = n;
            SyncMeshPrefs();     // 多人房桥接自动择优（MpMesh / BridgeUp）和旧 1v1 共用 bridgeup / bridgeupms
            int lv;
            if (d.TryGetValue("ailevel", out v) && int.TryParse(v, out lv) && lv >= 0 && lv < Math.Max(3, LocalAiSupport.LevelCount)) AiLevel = lv;     // 游戏的本地 AI 可以有 3 档以上（以前只认 0–2，第 4 档重启后变回第 1 档）
            foreach (var kv in d) AiLoad.LoadCfg(kv.Key, kv.Value);     // F5：AI 装备的键归游戏适配（LocalAiApi.cs）
            if (d.TryGetValue("airoom", out v)) AiRoom = v == "1";
            if (d.TryGetValue("airoommode", out v)) AiRoomMode = Modes.ValidRoom(v);
            if (d.TryGetValue("server", out v)) Server = v.Trim();
            if (d.TryGetValue("secret", out v)) Secret = v.Trim();
            foreach (var m in Modes.Known)
                if (d.TryGetValue("recent_" + m, out v) && v.Length > 0) Recent[m] = new List<string>(v.Split(','));
            // 旧版虚拟局域网 / 广域网的"最近"并到局域网 / 服务器大厅后面（去重、最多 5 个），下次保存时旧的键就没了
            MergeOld(d, "vlan", "lan");
            MergeOld(d, "wan", "server");
        }

        void MergeOld(Dictionary<string, string> d, string old, string into)
        {
            string v;
            if (!d.TryGetValue("recent_" + old, out v) || v.Length == 0) return;
            var l = RecentOf(into);
            foreach (var t in v.Split(',')) if (t.Length > 0 && !l.Contains(t) && l.Count < 5) l.Add(t);
        }

        public List<string> RecentOf(string mode)
        {
            List<string> l;
            if (!Recent.TryGetValue(mode, out l)) Recent[mode] = l = new List<string>();
            return l;
        }

        public void ClearRecent() { Recent.Clear(); Save(); }     // 0.34 "清除本地联机记录"

        public void Remember(string mode, string target)
        {
            var l = RecentOf(mode);
            l.Remove(target);
            l.Insert(0, target.Replace(",", ""));
            while (l.Count > 5) l.RemoveAt(l.Count - 1);
            Save();
        }

        internal void SyncMeshPrefs()
        {
            ForNet.MpMesh.HideIpPref = HideIp;
            ForNet.MpMesh.BridgeModePref = BridgeMode;
            ForNet.BridgeUp.On = BridgeUp;
            ForNet.BridgeUp.ThresholdMs = BridgeUpMs;
        }

        public void Save()
        {
            SyncMeshPrefs();
            var sb = new System.Text.StringBuilder();
            sb.Append("mode=").Append(Mode).Append('\n').Append("name=").Append(Name.Replace("\n", " ")).Append('\n')
              .Append("port=").Append(Port).Append('\n').Append("upnp=").Append(Upnp ? 1 : 0).Append('\n')
              .Append("relay=").Append(Relay ? 1 : 0).Append('\n').Append("bridge=").Append(Bridge ? 1 : 0).Append('\n')
              .Append("bridgefriends=").Append(BridgeFriends ? 1 : 0).Append('\n').Append("hide=").Append(Hide ? 1 : 0).Append('\n').Append("hideip=").Append(HideIp ? 1 : 0).Append('\n')
              .Append("bridgemode=").Append(BridgeMode).Append('\n')
              .Append("safe=").Append(Safe ? 1 : 0).Append('\n')
              .Append("maxspec=").Append(MaxSpec).Append('\n').Append("specnoq=").Append(SpecCfg.NoQueue ? 1 : 0).Append('\n')
              .Append("hostspec=").Append(HostSpec ? 1 : 0).Append('\n')
              .Append("dz=").Append(DzCfg.On ? 1 : 0).Append('\n')
              .Append("bridgeset=1\n")     // 这一版起桥接平台= 是认真的（见构造函数）
              .Append("server=").Append(Server.Replace("\n", " ")).Append('\n')
              .Append("secret=").Append(Secret.Replace("\n", " ")).Append('\n')
              .Append("ailevel=").Append(AiLevel).Append('\n');
            if (!NetBind.V6) sb.Append("ipv6=0\n");     // 只在关的时候写（默认开，不多一行）
            if (!BridgeUp) sb.Append("bridgeup=0\n");
            if (BridgeUpMs != 150) sb.Append("bridgeupms=").Append(BridgeUpMs).Append('\n');
            AiLoad.SaveCfg(sb);     // F5：AI 装备的行由游戏适配写（顺序和以前一样）
            sb.Append("airoom=").Append(AiRoom ? 1 : 0).Append('\n').Append("airoommode=").Append(AiRoomMode).Append('\n');
            foreach (var kv in Recent) sb.Append("recent_").Append(kv.Key).Append('=').Append(string.Join(",", kv.Value)).Append('\n');
            Kv.WriteAtomic(path, sb.ToString());
        }
    }

    static class Addr
    {
        // "IP[:端口]" 或主机名 → IPv4 端点；认不出返回 null。大厅服务器地址 / room@服务器 用这个（和服务器说话的 socket 只有 IPv4）
        public static IPEndPoint Parse(string text, int defPort)
        {
            var ep = ParseDirect(text, defPort);
            return ep == null || ep.AddressFamily != AddressFamily.InterNetwork ? null : ep;
        }

        // 输入 IP 直连 / 多人加入 / 观战输入地址：IPv4 和 IPv6 都认。
        // 1.2.3.4、1.2.3.4:27015、[2001:db8::1]:27015、[2001:db8::1]、2001:db8::1（裸 IPv6 不带端口：多个冒号就不拆端口）、
        // 域名[:端口]（先找 IPv4，没有再用 IPv6）。IPv4 映射成 IPv6 的（::ffff:1.2.3.4）还原成 IPv4
        public static IPEndPoint ParseDirect(string text, int defPort)
        {
            string host;
            int port;
            if (!Split(text, defPort, out host, out port)) return null;
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                if (host.IndexOf('.') < 0 || host.IndexOf(':') >= 0) return null;
                try
                {
                    var all = Dns.GetHostAddresses(host);
                    ip = Array.Find(all, a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? Array.Find(all, a => a.AddressFamily == AddressFamily.InterNetworkV6);
                }
                catch (Exception) { ip = null; }
            }
            if (ip == null) return null;
            return new IPEndPoint(NetBind.Norm(ip), port);
        }

        // 大厅服务器地址："IP[:端口]"、"[v6]:端口"、裸 IPv6、域名[:端口]（A 和 AAAA 都要）、localhost。
        // 返回最多两个端点：先 IPv4 再 IPv6（先用哪个由 LobbyServer.ResolveAll 定）；IPv6 关了（NetBind.V6 = false）只留 IPv4
        public static List<IPEndPoint> ParseServer(string text, int defPort)
        {
            var list = new List<IPEndPoint>();
            string host;
            int port;
            if (!Split(text, defPort, out host, out port)) return list;
            IPAddress ip;
            IPAddress v4 = null, v6 = null;
            if (IPAddress.TryParse(host, out ip))
            {
                ip = NetBind.Norm(ip);
                if (ip.AddressFamily == AddressFamily.InterNetwork) v4 = ip; else v6 = ip;
            }
            else
            {
                bool local = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
                if (!local && (host.IndexOf('.') < 0 || host.IndexOf(':') >= 0)) return list;
                try
                {
                    var all = Dns.GetHostAddresses(host);
                    v4 = Array.Find(all, a => a.AddressFamily == AddressFamily.InterNetwork);
                    v6 = Array.Find(all, a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal);
                }
                catch (Exception) { }
            }
            if (v4 != null) list.Add(new IPEndPoint(v4, port));
            if (v6 != null && (NetBind.V6 || v4 == null)) list.Add(new IPEndPoint(v6, port));     // 只填了 IPv6 字面量时照给（SessionUdp 按对方开 IPv6）
            return list;
        }

        // 只按字面量拆（不做 DNS）："ip:端口" / "[v6]:端口" → 端点；端口必须有。服务器 / 房主转来的地址文本用这个
        public static IPEndPoint ParseEp(string text)
        {
            string host;
            int port;
            IPAddress ip;
            if (!Split(text, -1, out host, out port) || port <= 0 || !IPAddress.TryParse(host, out ip)) return null;
            return new IPEndPoint(NetBind.Norm(ip), port);
        }

        // 拆 "主机[:端口]"：方括号里是 IPv6；没方括号时只有一个冒号才当端口分隔（多个冒号 = 裸 IPv6）
        static bool Split(string text, int defPort, out string host, out int port)
        {
            text = (text ?? "").Trim();
            host = text;
            port = defPort;
            if (text.Length == 0) return false;
            if (text[0] == '[')
            {
                int rb = text.IndexOf(']');
                if (rb < 2) return false;
                host = text.Substring(1, rb - 1);
                var rest = text.Substring(rb + 1).Trim();
                if (rest.Length == 0) return true;
                return rest[0] == ':' && int.TryParse(rest.Substring(1), out port) && port > 0 && port <= 65535;
            }
            int c = text.LastIndexOf(':');
            if (c > 0 && text.IndexOf(':') == c)
            {
                host = text.Substring(0, c);
                return int.TryParse(text.Substring(c + 1), out port) && port > 0 && port <= 65535;
            }
            return c != 0;
        }

        // 端点 → 文字：IPv4 "1.2.3.4:5"，IPv6 "[2001:db8::1]:5"（和 IPEndPoint.ToString 一样，只是先把映射地址还原）
        public static string Fmt(IPEndPoint ep) { return ep == null ? "" : new IPEndPoint(NetBind.Norm(ep.Address), ep.Port).ToString(); }

        // 只认地址字面量（剪贴板用：不做 DNS，免得把一段随便的文字当主机名去解析）：IPv4 要四段，IPv6 要有两个以上冒号
        public static bool LooksLikeIp(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length < 2 || s.Length > 64) return false;
            string host;
            int port;
            IPAddress ip;
            if (!Split(s, 1, out host, out port) || !IPAddress.TryParse(host, out ip)) return false;
            return ip.AddressFamily == AddressFamily.InterNetwork ? host.Split('.').Length == 4 : host.Split(':').Length >= 3;
        }

        // 两个 IPv6 地址前 64 位一样（同一个网段：Windows 的临时地址和固定地址就是这样）
        public static bool SameV6Prefix(IPAddress a, IPAddress b)
        {
            if (a == null || b == null || a.AddressFamily != AddressFamily.InterNetworkV6 || b.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (a.IsIPv4MappedToIPv6 || b.IsIPv4MappedToIPv6) return false;
            var x = a.GetAddressBytes();
            var y = b.GetAddressBytes();
            for (int i = 0; i < 8; i++) if (x[i] != y[i]) return false;
            return true;
        }

        // 本机发往公网 IPv6 时系统会用的源地址（朋友"输入 IP 直连"要填的就是它：主机回包也从这个地址出去）。
        // 只问路由、不发包；没有公网 IPv6 返回 null
        public static IPAddress OutboundV6()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(new IPEndPoint(IPAddress.Parse("2001:4860:4860::8888"), 53));
                    var a = ((IPEndPoint)s.LocalEndPoint).Address;
                    return IsGlobalV6(a) ? a : null;
                }
            }
            catch (Exception) { return null; }
        }

        // 2000::/3 全球单播（排除文档用的 2001:db8::/32）
        public static bool IsGlobalV6(IPAddress a)
        {
            if (a == null || a.AddressFamily != AddressFamily.InterNetworkV6 || a.IsIPv4MappedToIPv6) return false;
            var b = a.GetAddressBytes();
            return (b[0] & 0xE0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
        }

        // "桥接平台:<17 位数字>" 或 17 位数字 → 平台账号 ID；否则 0
        public static ulong BridgeId(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("bridge:")) s = s.Substring(7);
            ulong v;
            return s.Length == 17 && s.StartsWith("7656") && ulong.TryParse(s, out v) ? v : 0;
        }

        // 有默认网关的那张网卡的 IPv4（UPnP 要告诉路由器转发到哪）
        public static IPAddress PrimaryLocal()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var props = ni.GetIPProperties();
                    bool gw = false;
                    foreach (var g in props.GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) gw = true;
                    if (!gw) continue;
                    foreach (var ua in props.UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) return ua.Address;
                }
            }
            catch (Exception) { }
            return null;
        }

        public static bool IsVirtualLan(string ip)
        {
            // Tailscale 100.64.0.0/10；ZeroTier 地址段是用户自己定的，靠网卡名认（见 Program.LocalIps）
            var p = ip.Split('.');
            int a, b;
            return p.Length == 4 && int.TryParse(p[0], out a) && int.TryParse(p[1], out b) && a == 100 && b >= 64 && b < 128;
        }
    }

    // 非桥接平台方式（服务器大厅 / 输入 IP 直连 / 局域网）的连接诊断：net\net_conn.log，仿 的 steam_conn.log。
    // 一行一个事件（带毫秒时间），只在事件发生时写，不每帧写；游戏里的联机程序每个进程第一次 Start 时把上一份改名成 net_conn.prev.log。
    // IP 一律经 Ip 脱敏：只留前两段 + 本进程随机盐的短哈希（同一份日志里看得出是不是同一个地址，盐不落盘）。写失败吞掉
    static class NetLog
    {
        static string path;
        static bool rotated;
        static readonly object lk = new object();
        static readonly string salt = Guid.NewGuid().ToString("N");
        // 联机程序是哪种：csc 编的 exe / NativeAOT 编的无界面 exe（测试用）/ 游戏进程里的 fornet_lobby.dll（AotEntry 改成 DLL）
        public static string Build =
#if AOT
            "AOT exe";
#else
            "exe";
#endif

        // rotate：游戏里的联机程序（--bg / DLL）改名；命令行测试（RunCli）不改名（两个进程可能共用一个 net 目录）
        public static void Start(string dir, bool rotate)
        {
            try
            {
                lock (lk)
                {
                    path = Path.Combine(dir, "net_conn.log");
                    if (!rotate || rotated) return;
                    rotated = true;     // DLL 版面板"退出"后再开，还在同一个游戏进程里：不再改名
                    var prev = Path.Combine(dir, "net_conn.prev.log");
                    if (File.Exists(path)) { File.Delete(prev); File.Move(path, prev); }
                }
            }
            catch (Exception) { }
        }

        public static void W(string m)
        {
            var p = path;
            if (p == null) return;
            try
            {
                lock (lk)
                {
                    var b = Kv.Utf8.GetBytes(DateTime.Now.ToString("HH:mm:ss.fff ") + m + "\r\n");
                    using (var f = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) f.Write(b, 0, b.Length);
                }
            }
            catch (Exception) { }
        }

        // 203.0.113.7:27015 → "[公网]203.0.*.*#3fa2:27015"；内网 / 回环 / CGNAT（Tailscale 等）另标
        public static string Ip(IPEndPoint ep) { return ep == null ? "-" : Ip(ep.Address) + ":" + ep.Port; }
        public static string Ip(IPAddress a)
        {
            if (a == null) return "-";
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            var s = a.ToString();
            uint h = 2166136261;
            foreach (var c in salt + s) { h ^= c; h *= 16777619; }
            var tag = (h & 0xffff).ToString("x4");
            if (a.AddressFamily != AddressFamily.InterNetwork)
            {
                // IPv6：只留前两组 + 短哈希；种类按前缀分
                var b6 = a.GetAddressBytes();
                string k6 = IPAddress.IsLoopback(a) ? "回环" : a.IsIPv6LinkLocal ? "链路本地" : (b6[0] & 0xFE) == 0xFC ? "内网" : Addr.IsGlobalV6(a) ? "公网" : "其他";
                var g = s.Split('%')[0].Split(':');
                return "[IPv6 " + k6 + "]" + (g.Length > 2 && g[0].Length > 0 && g[1].Length > 0 ? g[0] + ":" + g[1] + ":" : "") + "*#" + tag;
            }
            var b = a.GetAddressBytes();
            string kind = b[0] == 127 ? "回环" : b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) ? "内网"
                        : b[0] == 100 && b[1] >= 64 && b[1] < 128 ? "CGNAT/虚拟网" : "公网";
            return "[" + kind + "]" + b[0] + "." + b[1] + ".*.*#" + tag;
        }
        // 服务器发来的 "IP:端口" 文本；认不出也不原样写
        public static string Ip(string ipPort)
        {
            if (string.IsNullOrEmpty(ipPort)) return "（空）";
            var ep = Addr.ParseEp(ipPort);
            return ep != null ? Ip(ep) : "（认不出的地址）";
        }
    }

    // 广域网：用 Windows 自带的 UPnP（HNetCfg.NATUPnP，COM）让路由器把 UDP 端口转发到本机。路由器没开 UPnP 就失败，提示手动转发
    static class Upnp
    {
        public static volatile string Status = "";
        public static volatile string PublicIp = "";
        static int mapped;
        public static int MappedPort { get { return mapped; } }     // 11-15 面板"复制我的 IPv4 地址"用（公网 IP:转发的端口）

        public static void MapAsync(int port)
        {
            Status = L.T("UPnP：正在请求路由器转发 UDP ", "UPnP: asking the router to forward UDP ") + port + "…";
            NetLog.W("UPnP：请求路由器转发 UDP " + port);
            Safe.Pool("upnp-map", delegate
            {
                try
                {
                    var local = Addr.PrimaryLocal();
                    if (local == null) throw new Exception(L.T("没找到有网关的网卡", "no adapter with a gateway"));
                    if (!Nat.Available()) throw new Exception(L.T("路由器没开 UPnP（或 Windows 的网络发现关了）", "the router has UPnP off (or Windows network discovery is off)"));
                    Nat.Remove(port);
                    string ext = Nat.Add(port, local.ToString());
                    mapped = port;
                    PublicIp = ext ?? "";
                    Status = L.T("UPnP：已转发 UDP ", "UPnP: forwarding UDP ") + port + " → " + local
                             + (PublicIp.Length > 0 ? L.T("，公网 IP ", ", public IP ") + PublicIp : "");
                    IPAddress pub;
                    NetLog.W("UPnP 成功：UDP " + port + " → " + NetLog.Ip(local) + "，路由器报的公网 IP "
                             + (IPAddress.TryParse(PublicIp, out pub) ? NetLog.Ip(pub) : "（没给）"));
                }
                catch (Exception ex)
                {
                    var e = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    Status = L.T("UPnP 失败：", "UPnP failed: ") + e.Message + L.T("。请在路由器里手动把 UDP ", ". Forward UDP ") + port
                             + L.T(" 转发到本机", " to this PC in your router");
                    NetLog.W("UPnP 失败（UDP " + port + "）：" + e.Message);
                }
            });
        }

        public static void Unmap()
        {
            int port = mapped;
            if (port == 0) return;
            mapped = 0;
            PublicIp = "";
            Status = "";
            Safe.Pool("upnp-unmap", delegate
            {
                try
                {
                    Nat.Remove(port);
                }
                catch (Exception) { }
            });
        }

    }

    // 路由器的静态端口映射表（HNetCfg.NATUPnP 的 StaticPortMappingCollection）。
    // exe：COM 晚绑定（InvokeMember）。AOT 版（fornet_lobby.dll）：NativeAOT 不支持 COM 晚绑定，按接口的虚表直接调
    // （IUPnPNAT / IStaticPortMappingCollection / IStaticPortMapping 都是 IDispatch 双接口，前 7 格是 IUnknown + IDispatch）
    static class Nat
    {
#if !AOT
        static object Collection()
        {
            var t = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
            if (t == null) return null;
            var nat = Activator.CreateInstance(t);
            return t.InvokeMember("StaticPortMappingCollection", BindingFlags.GetProperty, null, nat, null);
        }

        public static bool Available() { return Collection() != null; }

        public static void Remove(int port)
        {
            try
            {
                var maps = Collection();
                if (maps != null) maps.GetType().InvokeMember("Remove", BindingFlags.InvokeMethod, null, maps, new object[] { port, "UDP" });
            }
            catch (Exception) { }
        }

        // 返回路由器报的公网 IP（拿不到给 ""）
        public static string Add(int port, string local)
        {
            var maps = Collection();
            if (maps == null) throw new Exception("UPnP");
            var m = maps.GetType().InvokeMember("Add", BindingFlags.InvokeMethod, null, maps, new object[] { port, "UDP", port, local, true, GameInfo.ModName });
            try { return (string)m.GetType().InvokeMember("ExternalIPAddress", BindingFlags.GetProperty, null, m, null) ?? ""; } catch (Exception) { return ""; }
        }
#else
        static readonly Guid ClsidNat = new Guid("AE1E00AA-3FD5-403C-8A27-2BBDC30CD0E1"), IidNat = new Guid("B171C812-CC76-485A-94D8-B6B3A2794E99");
        [System.Runtime.InteropServices.DllImport("ole32")] static extern int CoInitializeEx(IntPtr reserved, uint model);
        [System.Runtime.InteropServices.DllImport("ole32")] static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint ctx, in Guid iid, out IntPtr obj);
        [System.Runtime.InteropServices.DllImport("oleaut32", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr SysAllocString(string s);
        [System.Runtime.InteropServices.DllImport("oleaut32")] static extern void SysFreeString(IntPtr b);

        static unsafe IntPtr Slot(IntPtr obj, int i) { return (*(IntPtr**)obj)[i]; }
        static unsafe void Release(IntPtr obj) { if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj); }

        // 线程池线程默认没进 COM 套间：进多线程套间（已经进过返回 S_FALSE / RPC_E_CHANGED_MODE，都能用）
        static unsafe IntPtr Collection()
        {
            CoInitializeEx(IntPtr.Zero, 0 /* COINIT_ */);
            IntPtr nat, maps = IntPtr.Zero;
            if (CoCreateInstance(ClsidNat, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, IidNat, out nat) < 0 || nat == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(nat, 7))(nat, &maps);   // get_StaticPortMappingCollection
                return hr < 0 ? IntPtr.Zero : maps;   // 路由器没开 UPnP 时这里是 S_OK + NULL
            }
            finally { Release(nat); }
        }

        public static bool Available()
        {
            var maps = Collection();
            Release(maps);
            return maps != IntPtr.Zero;
        }

        public static unsafe void Remove(int port)
        {
            var maps = Collection();
            if (maps == IntPtr.Zero) return;
            var udp = SysAllocString("UDP");
            try { ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)Slot(maps, 10))(maps, port, udp); }   // Remove(lExternalPort, bstrProtocol)
            finally { SysFreeString(udp); Release(maps); }
        }

        public static unsafe string Add(int port, string local)
        {
            var maps = Collection();
            if (maps == IntPtr.Zero) throw new Exception("UPnP");
            IntPtr udp = SysAllocString("UDP"), client = SysAllocString(local), desc = SysAllocString(GameInfo.ModName), m = IntPtr.Zero;
            try
            {
                // Add(lExternalPort, bstrProtocol, lInternalPort, bstrInternalClient, bEnabled (VARIANT_BOOL), bstrDescription, ppSPM)
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int, IntPtr, short, IntPtr, IntPtr*, int>)Slot(maps, 11))(maps, port, udp, port, client, -1, desc, &m);
                if (hr < 0) throw new Exception("UPnP Add 0x" + hr.ToString("X8"));
                if (m == IntPtr.Zero) return "";
                IntPtr ext = IntPtr.Zero;
                int hr2 = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(m, 7))(m, &ext);         // get_ExternalIPAddress
                string s = hr2 >= 0 && ext != IntPtr.Zero ? System.Runtime.InteropServices.Marshal.PtrToStringBSTR(ext) : "";
                if (ext != IntPtr.Zero) SysFreeString(ext);
                return s ?? "";
            }
            finally { SysFreeString(udp); SysFreeString(client); SysFreeString(desc); Release(m); Release(maps); }
        }
#endif
    }

    // 虚拟局域网：Tailscale 不转发广播，所以主机把房间广播单播给每个在线节点；加入方也把在线节点列成可加入目标。
    // 节点表来自 `tailscale status --json`（没装 Tailscale 就是空表）；后台刷新，15 秒一次
    static class Tailscale
    {
        static volatile List<string[]> peers = new List<string[]>();   // {IP, 主机名}
        static DateTime at = DateTime.MinValue;
        static int busy;

        public static List<string[]> Peers()
        {
            if ((DateTime.UtcNow - at).TotalSeconds > 15 && Interlocked.Exchange(ref busy, 1) == 0)
            {
                at = DateTime.UtcNow;
                ThreadPool.QueueUserWorkItem(delegate { try { peers = Query(); } catch (Exception) { } finally { busy = 0; } });
            }
            return peers;
        }

        static string Exe()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var p = Path.Combine(pf, @"Tailscale\tailscale.exe");
            if (File.Exists(p)) return p;
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { var q = Path.Combine(d.Trim(), "tailscale.exe"); if (File.Exists(q)) return q; } catch (Exception) { }
            }
            return null;
        }

        static List<string[]> Query()
        {
            var list = new List<string[]>();
            var exe = Exe();
            if (exe == null) return list;
            var psi = new ProcessStartInfo(exe, "status --json")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Kv.Utf8
            };
            string json;
            using (var p = Process.Start(psi))
            {
                json = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
            }
            return Parse(json);
        }

        // 只取 Peer 里在线的节点：HostName、TailscaleIPs 里的 IPv4
#if AOT
        // AOT 版没有 JavaScriptSerializer：用 System.Text.Json 读同样的字段
        public static List<string[]> Parse(string json)
        {
            var list = new List<string[]>();
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    System.Text.Json.JsonElement peers;
                    if (!doc.RootElement.TryGetProperty("Peer", out peers) || peers.ValueKind != System.Text.Json.JsonValueKind.Object) return list;
                    foreach (var kv in peers.EnumerateObject())
                    {
                        var n = kv.Value;
                        System.Text.Json.JsonElement on, ips, host;
                        if (!n.TryGetProperty("Online", out on) || on.ValueKind != System.Text.Json.JsonValueKind.True) continue;
                        if (!n.TryGetProperty("TailscaleIPs", out ips) || ips.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                        string h = n.TryGetProperty("HostName", out host) && host.ValueKind == System.Text.Json.JsonValueKind.String ? host.GetString() : null;
                        foreach (var ip in ips.EnumerateArray())
                        {
                            var s = ip.ValueKind == System.Text.Json.JsonValueKind.String ? ip.GetString() : null;
                            if (s != null && s.IndexOf(':') < 0) { list.Add(new[] { s, h ?? s }); break; }
                        }
                    }
                }
            }
            catch (Exception) { }
            list.Sort((a, b) => string.CompareOrdinal(a[1], b[1]));
            return list;
        }
#else
        public static List<string[]> Parse(string json)
        {
            var list = new List<string[]>();
            var root = new JavaScriptSerializer { MaxJsonLength = 1 << 24 }.DeserializeObject(json) as Dictionary<string, object>;
            object po;
            if (root == null || !root.TryGetValue("Peer", out po) || !(po is Dictionary<string, object>)) return list;
            foreach (var kv in (Dictionary<string, object>)po)
            {
                var n = kv.Value as Dictionary<string, object>;
                if (n == null) continue;
                object on, ips, host;
                if (!n.TryGetValue("Online", out on) || !(on is bool) || !(bool)on) continue;
                if (!n.TryGetValue("TailscaleIPs", out ips) || !(ips is object[])) continue;
                n.TryGetValue("HostName", out host);
                foreach (var ip in (object[])ips)
                {
                    var s = ip as string;
                    if (s != null && s.IndexOf(':') < 0) { list.Add(new[] { s, (host as string) ?? s }); break; }
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a[1], b[1]));
            return list;
        }
#endif
    }

    // ---- 拉黑（举报 0.34 已删）
    // 标识：uid = 本机第一次运行时随机生成的安装号（net\uid.txt，12 位十六进制）。握手和每个状态包只带 uid=（0.34 评审：以前还带 sh=，
    // 平台账号 ID空间只有 2^31 量级、固定前缀 SHA-256 几分钟就能穷举，等于明文，所以**不再上网**）。
    // sh = 平台账号 ID的哈希，只在本机用（黑名单 / 最近一起玩过），而且用本机安装号当盐：别的机器拿到文件也反推不出；
    // 它只来自 桥接连接上验过的对方平台账号 ID（NoteOpp），不来自包里自报的字段
    static class Ident
    {
        public static string Uid = "";
        static ulong shFor;
        static string shText = "";

        public static void Load(string dir)
        {
            var f = Path.Combine(dir, "uid.txt");
            Uid = Clean(Kv.ReadText(f));
            if (Uid.Length >= 8) return;
            var b = new byte[6];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            try { Kv.WriteAtomic(f, BitConverter.ToString(b).Replace("-", "").ToLowerInvariant() + "\n"); } catch (Exception) { }
            Uid = Clean(Kv.ReadText(f));
        }

        static string Clean(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in (s ?? "").Trim().ToLowerInvariant()) if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) sb.Append(c);
            return sb.Length > 16 ? sb.ToString(0, 16) : sb.ToString();
        }

        public static string Sh
        {
            get
            {
                if (!Bridge.Ready || Bridge.MyId == 0) return "";
                if (shFor != Bridge.MyId) { shText = BridgeHash(Bridge.MyId); shFor = Bridge.MyId; }
                return shText;
            }
        }

        static string H(string s)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Kv.Utf8.GetBytes(s))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }
        public static string BridgeHash(ulong id) { return id == 0 ? "" : H(WireTag.Salt + "-sid|" + Uid + "|" + id); }     // 0.34 评审：加本机安装号当盐，只在本机比对
        public static string BridgeHash(string id) { ulong v; return ulong.TryParse(id, out v) ? BridgeHash(v) : ""; }
        // 房间列表里的主机标记：uid 的哈希（列表谁都能看，不直接放 uid）
        public static string RoomHash(string uid) { return string.IsNullOrEmpty(uid) ? "" : H(WireTag.Salt + "-room|" + uid); }
        public static volatile bool Hidden;     // 0.34 隐身（Cfg.Hide）：登记 / 广播里不放自己的主机标记
        public static string MyRoomHash { get { return Hidden ? "" : RoomHash(Uid); } }

        // 握手 / 状态包里加的行：uid=（安装号）；隐身时再加 hd=1（对方的"最近一起玩过"不记我）。0.34 评审起不再带 sh=
        public static string Lines()
        {
            return (Uid.Length > 0 ? "uid=" + Uid + "\n" : "") + (Hidden ? "hd=1\n" : "");
        }

        // 包里某个字段（"\n键=值"；包的第一行不会是这两个键）
        public static string Field(string body, string key)
        {
            int i = body.IndexOf("\n" + key + "=", StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length + 2;
            int e = body.IndexOf('\n', i);
            return (e < 0 ? body.Substring(i) : body.Substring(i, e - i)).Trim();
        }
    }

    // 0.34 评审：Lua 的二次确认记在 net\rules.txt 的 consent_<键>=<文案版本>（PREF）。联机程序在发房间列表 / 排位查询之前也看一眼：
    // 0.33 留下的 netcfg（mode=server、airoom=1）不该让它启动就向服务器发包。只认"有没有同意过"，版本对不对由 Lua 管
    static class Consent
    {
        static string file; static DateTime readAt = DateTime.MinValue, mtime; static HashSet<string> keys = new HashSet<string>();
        static Dictionary<string, string> addrs = new Dictionary<string, string>();     // consent_<key>_addr：server / ranked 的同意绑定当时填的地址（Lua CONSENT.PER_SERVER）
        public static volatile bool All;     // --consent-all（测试）
        public static volatile string ServerAddr = "";     // 主程序每轮写 cfg.Server（规范化：去空白、小写）
        static string netDir;
        public static void Init(string dir) { file = Path.Combine(dir, "rules.txt"); netDir = dir; }
        // 11-14：玩法 mod 装着没有（<net 目录>\..\mods\<名>\on.txt，和 Ext.Load 扫的同一个目录）。观战 mod 在时 1v1 另要 consent_spec
        public static bool ModOn(string name)
        {
            if (netDir == null) return false;
            try { return File.Exists(Path.Combine(netDir, "..", "mods", name, "on.txt")); } catch (Exception) { return false; }
        }
        public static bool Has(string key)
        {
            if (All) return true;
            if (file == null) return false;
            if (!HasRaw(key) && !(key.StartsWith("bridge_") && HasRaw("steam_" + key.Substring(7)))) return false;     // F4c-2：0.34 写的 consent_steam_* 照认
            if (key == "server" || key == "ranked")     // 0.34：换了服务器地址 = 另一台，上次的同意不算
            {
                string a; var want = (ServerAddr ?? "").Trim().ToLowerInvariant();
                return addrs.TryGetValue(key, out a) && (a == "*" || (a == want && want.Length > 0));     // "*" = Lua grantAll（测试 / 安全模式重置）
            }
            return true;
        }
        static bool HasRaw(string key)
        {
            var now = DateTime.UtcNow;
            if ((now - readAt).TotalSeconds >= 2)
            {
                readAt = now;
                try
                {
                    var mt = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
                    if (mt != mtime)
                    {
                        mtime = mt;
                        var k = new HashSet<string>(); var ad = new Dictionary<string, string>();
                        if (File.Exists(file))
                            foreach (var line in File.ReadAllLines(file, Kv.Utf8))
                            {
                                var t = line.Trim();
                                int i = t.IndexOf('=');
                                if (!t.StartsWith("consent_") || i <= 8) continue;
                                var name = t.Substring(8, i - 8); var val = t.Substring(i + 1).Trim();
                                if (name.EndsWith("_addr")) { ad[name.Substring(0, name.Length - 5)] = val.ToLowerInvariant(); continue; }
                                int v; if (int.TryParse(val, out v) && v > 0) k.Add(name);
                            }
                        keys = k; addrs = ad;
                    }
                }
                catch (Exception) { }
            }
            return keys.Contains(key);
        }
    }

    // 一个对手（当前 / 最近）：主程序每轮看连着的会话记下来，面板的拉黑用
    class Opp
    {
        public string Uid = "", Sh = "", Nick = "", Mode = "", Room = "", Mt = "", Ver = "", Rk = "";     // 0.34：不再记平台账号 ID明文 / IP（桥接平台验过的平台账号 ID算成 Sh）
        public DateTime First, Last;
        public bool Same(Opp o)
        {
            if (Uid.Length > 0 && o.Uid.Length > 0) return Uid == o.Uid;
            return Sh.Length > 0 && o.Sh.Length > 0 && Sh == o.Sh;
        }
        public bool HasId { get { return Uid.Length > 0 || Sh.Length > 0; } }
        // 同一个人又看到一次：新看到的非空字段盖上去
        public void Merge(Opp o)
        {
            Func<string, string, string> m = (old, nw) => nw.Length > 0 ? nw : old;
            Uid = m(Uid, o.Uid); Sh = m(Sh, o.Sh); Nick = m(Nick, o.Nick);
            Room = m(Room, o.Room); Mt = m(Mt, o.Mt); Ver = m(Ver, o.Ver); Rk = m(Rk, o.Rk);
            Mode = o.Mode;
        }
    }

    // 黑名单：net\blacklist.txt，一行一个（uid= sh= nm= mode= at=，Tab 分开）。启动读一次，改了写一次；
    // 收包时查的是内存里的集合（每个包两次哈希表查找）。
    // 0.34：不再存明文平台账号 ID（sid=）和 IP（ip=）。旧文件里的 sid= 加载时算成 sh=（平台账号 ID的哈希），ip= 丢掉，
    // 迁移过就重写一次文件。代价：对方什么标识都没报（旧版客户端）时拦不住，以前靠 IP 兜底的那几条也没了。
    static class Blacklist
    {
        public class Entry { public string Uid = "", Sh = "", Nick = "", Mode = ""; public long At; }
        public const int Max = 200;
        static readonly object gate = new object();
        static List<Entry> list = new List<Entry>();
        static volatile HashSet<string> uids = new HashSet<string>(), shs = new HashSet<string>(), rooms = new HashSet<string>();
        static string file;

        public static void Load(string dir)
        {
            file = Path.Combine(dir, "blacklist.txt");
            var l = new List<Entry>();
            bool migrated = false;
            foreach (var raw in (Kv.ReadText(file) ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                var e = new Entry();
                foreach (var part in line.Split('\t'))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    var v = part.Substring(eq + 1).Trim();
                    switch (part.Substring(0, eq))
                    {
                        case "uid": e.Uid = v; break;
                        case "sh": e.Sh = v; break;
                        case "sid": if (v.Length > 0) { if (e.Sh.Length == 0) e.Sh = Ident.BridgeHash(v); migrated = true; } break;     // 旧文件：明文平台账号 ID → 哈希
                        case "ip": if (v.Length > 0) migrated = true; break;     // 旧文件：IP 丢掉
                        case "nm": e.Nick = v; break;
                        case "mode": e.Mode = v; break;
                        case "at": long.TryParse(v, out e.At); break;
                    }
                }
                if (e.Uid.Length > 0 || e.Sh.Length > 0) l.Add(e);
                else if (line.Contains("=")) migrated = true;     // 只有 IP 的那几条：没法再认，丢掉
            }
            lock (gate) { list = l; Rebuild(); if (migrated) { Save(); NetLog.W("黑名单：旧格式（sid= / ip=）已迁成只存哈希"); } }
        }

        // 收包线程只读这几个集合：整个换掉，不在原地改
        static void Rebuild()
        {
            var u = new HashSet<string>(); var h = new HashSet<string>(); var r = new HashSet<string>();
            foreach (var e in list)
            {
                if (e.Uid.Length > 0) { u.Add(e.Uid); r.Add(Ident.RoomHash(e.Uid)); }
                if (e.Sh.Length > 0) h.Add(e.Sh);
            }
            uids = u; shs = h; rooms = r;
        }

        static void Save()
        {
            if (file == null) return;
            var sb = new System.Text.StringBuilder("# " + GameInfo.ModName + " blacklist: edit it in the in-game panel. Only hashed ids are kept.\n");
            foreach (var e in list)
                sb.Append("uid=").Append(e.Uid).Append("\tsh=").Append(e.Sh)
                  .Append("\tnm=").Append(e.Nick.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ")).Append("\tmode=").Append(e.Mode)
                  .Append("\tat=").Append(e.At).Append('\n');
            try { Kv.WriteAtomic(file, sb.ToString()); } catch (Exception ex) { NetLog.W("黑名单写不进去：" + ex.Message); }
        }

        public static int Count { get { lock (gate) return list.Count; } }
        public static List<Entry> Snapshot() { lock (gate) return new List<Entry>(list); }

        // 拉黑一个对手；已经在名单里就补全标识。返回 false = 什么标识都没有（还没收到对方的包 / 旧版客户端不报 uid）
        public static bool Add(Opp o)
        {
            if (!o.HasId) return false;
            lock (gate)
            {
                Entry e = null;
                foreach (var x in list)
                    if ((o.Uid.Length > 0 && x.Uid == o.Uid) || (o.Sh.Length > 0 && x.Sh == o.Sh)) { e = x; break; }
                if (e == null)
                {
                    if (list.Count >= Max) list.RemoveAt(0);
                    e = new Entry();
                    list.Add(e);
                }
                if (o.Uid.Length > 0) e.Uid = o.Uid;
                if (o.Sh.Length > 0) e.Sh = o.Sh;
                if (o.Nick.Length > 0) e.Nick = o.Nick;
                e.Mode = o.Mode;
                e.At = (long)Link.UnixNow();
                Rebuild();
                Save();
            }
            return true;
        }

        public static bool Remove(int index)
        {
            lock (gate)
            {
                if (index < 0 || index >= list.Count) return false;
                list.RemoveAt(index);
                Rebuild();
                Save();
            }
            return true;
        }

        // 收到的包（握手 / 状态包）是不是黑名单里的人发的
        public static bool DeniesBody(string body)
        {
            var u = uids; var h = shs;
            if (body == null || (u.Count == 0 && h.Count == 0)) return false;
            var uid = Ident.Field(body, "uid");
            if (uid != null && u.Contains(uid)) return true;
            var sh = Ident.Field(body, "sh");
            return sh != null && h.Contains(sh);
        }
        public static bool DeniesBridge(ulong id) { return id != 0 && shs.Contains(Ident.BridgeHash(id)); }
        public static bool DeniesSh(string sh) { return !string.IsNullOrEmpty(sh) && shs.Contains(sh); }     // 0.34 "最近"页过滤拉黑的人
        public static bool HidesRoom(string roomHash) { return !string.IsNullOrEmpty(roomHash) && rooms.Contains(roomHash); }
        public static bool Has(Opp o) { return (o.Uid.Length > 0 && uids.Contains(o.Uid)) || (o.Sh.Length > 0 && shs.Contains(o.Sh)); }

        // 主机回给被拒的人（签名照常，对方面板显示"被房主拒绝"）
        public static string DenyBody { get { return WireTag.P + "DENY\n"; } }
    }

    // 0.34 最近一起玩过的人（最终方案 2 /）：net\history.txt，本地、不上传。一行一条（Tab 分开）：
    // bh=<对方主机标记 = 对方安装号的哈希> sh=<对方桥接平台哈希> nm=<昵称> at=<unix> src=<lan|server|桥接平台>
    // 只记真正连上并连着超过 45 秒 / 开打了的对手（Program.Tick）；最多 50 条，30 天后丢；面板"清除本地联机记录"删整个文件。
    // 匹配：服务器房间列表第 10 列 / 局域网广播最后一段就是对方的 bh；平台好友按 BridgeHash 对 sh（不存平台账号 ID）。
    static class History
    {
        public class Entry { public string Bh = "", Sh = "", Nick = "", Src = ""; public long At; }
        public const int Max = 50, KeepDays = 30;
        static readonly object gate = new object();
        static List<Entry> list = new List<Entry>();
        static string file;

        public static void Load(string dir)
        {
            file = Path.Combine(dir, "history.txt");
            var l = new List<Entry>();
            long cut = (long)Link.UnixNow() - KeepDays * 86400L;
            foreach (var raw in (Kv.ReadText(file) ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                var e = new Entry();
                foreach (var part in line.Split('\t'))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    var v = part.Substring(eq + 1).Trim();
                    switch (part.Substring(0, eq))
                    {
                        case "bh": e.Bh = v; break;
                        case "sh": e.Sh = v; break;
                        case "nm": e.Nick = v; break;
                        case "src": e.Src = v; break;
                        case "at": long.TryParse(v, out e.At); break;
                    }
                }
                if ((e.Bh.Length > 0 || e.Sh.Length > 0) && e.At >= cut) l.Add(e);
            }
            lock (gate) list = l;
        }

        static void Save()
        {
            if (file == null) return;
            var sb = new System.Text.StringBuilder("# " + GameInfo.ModName + " recently played with (local only, TASK_0.34 3.1). Delete this file or use the panel to clear.\n");
            foreach (var e in list)
                sb.Append("bh=").Append(e.Bh).Append("\tsh=").Append(e.Sh).Append("\tnm=").Append(e.Nick.Replace("\t", " ").Replace("\r", " ").Replace("\n", " "))
                  .Append("\tat=").Append(e.At).Append("\tsrc=").Append(e.Src).Append('\n');
            try { Kv.WriteAtomic(file, sb.ToString()); } catch (Exception ex) { NetLog.W("最近记录写不进去：" + ex.Message); }
        }

        // 记一次（同一个人更新时间 / 昵称；1 分钟内不重复写盘）
        public static void Note(Opp o, string src)
        {
            var bh = o.Uid.Length > 0 ? Ident.RoomHash(o.Uid) : "";
            if (bh.Length == 0 && o.Sh.Length == 0) return;
            long now = (long)Link.UnixNow();
            lock (gate)
            {
                Entry e = null;
                foreach (var x in list) if ((bh.Length > 0 && x.Bh == bh) || (o.Sh.Length > 0 && x.Sh.Length > 0 && x.Sh == o.Sh)) { e = x; break; }
                if (e != null && now - e.At < 60) return;
                if (e == null) e = new Entry(); else list.Remove(e);
                list.Insert(0, e);
                while (list.Count > Max) list.RemoveAt(list.Count - 1);
                if (bh.Length > 0) e.Bh = bh;
                if (o.Sh.Length > 0) e.Sh = o.Sh;
                if (o.Nick.Length > 0) e.Nick = o.Nick;
                e.Src = src == "ranked" ? "server" : src;
                e.At = now;
                Save();
            }
        }

        public static void Clear()
        {
            lock (gate) { list.Clear(); try { if (file != null && File.Exists(file)) File.Delete(file); } catch (Exception) { } }
        }
        public static int Count { get { lock (gate) return list.Count; } }
        public static List<Entry> Snapshot() { lock (gate) return new List<Entry>(list); }
    }

    // 下线标志（0.34 L0/L1/L2、到期日）X3 整个删了（：插件开关取代；基底不再有"不经同意"的例外）。只剩本地安全模式 Cfg.Safe。

    // 举报（Reporter）0.34 整个删了： / 。玩家有问题直接联系作者。
}
