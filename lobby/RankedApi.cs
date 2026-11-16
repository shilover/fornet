// RankedApi.cs —— 排位（武林盟主）在联机程序里的接缝（F4c-1，docs/EXTENSIONS.md §4 / §10.4）。
// 基底（Program.cs / Session / ServerLobby）只认这里的接口；Ranked.cs 的 RankClient / MatchStats 实现它们，编译时带 /define:RANKED 才登记
// （build.bat 的 dev / release 都带；`build.bat base` 不带 = 没有排位的基底，连接方式里也没有 ranked）。
// 这是以后把排位搬成插件（lobby_ranked.dll）的边界：插件要能顶替的就是 IRankedClient 这一张表。
using System;
using System.Collections.Generic;
using System.Text;

namespace ForNet
{
    // 一局里本机 / 对方包的武器 / 招式统计（§110.11）。Session 的发包线程记本机包，房间连接的收包线程记对方包
    interface IMatchStats
    {
        void Start();
        void Stop();
        void Local(string wc, string wcs, string ev);
        void Peer(string res);
        string Payload();
    }

    // 排位客户端：密钥 / 上报 / 排行榜 / 统计 / lobby.txt 字段（§110）
    interface IRankedClient
    {
        int Support { get; }            // 这台大厅服务器开没开排位：-1 还不知道，0 没开，1 开了
        string Pid { get; }             // 排位身份（本地密钥的哈希）
        string Me { get; }              // rkme=<分数>|<名次>|…
        string Error { get; }
        IMatchStats Stats { get; }
        Action<string> Log { set; }
        void Poll(string addr, string myNick, bool active, Session s);
        void RequestTop(int page);
        void RequestStats(string kind, int page);
        string ExportKey();
        bool ImportKey(string k, out string why);
        string TitleOf(string pid);
        void WriteLobby(StringBuilder sb, bool active);
        void Close();
    }

    static class RankedSupport
    {
        // 测试 / 以后别的实现可以登记；平时 null（基底不带排位代码，排位在插件里）
        public static Func<string, IRankedClient> Create;
        // F4c-3：排位是扩展包 ext\ranked\lobby_ranked.dll（Ext.cs 装）；装上了就经 PluginRanked 转给它，没装 / 停用 = 没有排位
        public static bool Available { get { return Create != null || Ext.Has(PluginRanked.Name); } }
        public static IRankedClient Make(string dir) { return Create != null ? Create(dir) : Ext.Has(PluginRanked.Name) ? (IRankedClient)new PluginRanked(dir) : new NoRanked(); }
        // 连接方式表里有没有 ranked
        public static string[] Modes(string[] all) { return Available ? all : Array.FindAll(all, x => x != "ranked"); }
    }

    // 没有排位的基底：服务器"没开排位"（Support 0 → 面板不列武林盟主），别的都是空操作
    sealed class NoRanked : IRankedClient
    {
        public int Support { get { return 0; } }
        public string Pid { get { return ""; } }
        public string Me { get { return ""; } }
        public string Error { get { return ""; } }
        public IMatchStats Stats { get { return null; } }
        public Action<string> Log { set { } }
        public void Poll(string addr, string myNick, bool active, Session s) { }
        public void RequestTop(int page) { }
        public void RequestStats(string kind, int page) { }
        public string ExportKey() { return ""; }
        public bool ImportKey(string k, out string why) { why = "no ranked"; return false; }
        public string TitleOf(string pid) { return ""; }
        public void WriteLobby(StringBuilder sb, bool active) { sb.Append("rk=0\n"); }
        public void Close() { }
    }

    // F4c-3：排位搬成扩展包插件 ext\ranked\lobby_ranked.dll（docs/EXTENSIONS.md §4.3）之后，基底这头的 IRankedClient 实现：
    // 所有调用转成 Ext.Cmd("ranked", k=v)，字段从插件的私有回话（_rk* 等，Ext.Reply）读回，WriteLobby 写出和以前一字不差的 rk* 行；
    // 会话信息每轮以快照推给插件（它不认 Session）。插件没装 / 停用（off.txt）时 RankedSupport.Make 给 NoRanked。协议见 ranked/RankedPlugin.cs 文件头
    sealed class PluginRanked : IRankedClient
    {
        public const string Name = "ranked";
        readonly string dir;
        readonly PluginStats stats = new PluginStats();
        Action<string> log = delegate { };
        string lastLogN = "";
        public PluginRanked(string dir) { this.dir = dir; }
        static string R(string k) { string v; return Ext.Reply(Name, k, out v) ? v : ""; }
        static void Send(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1] ?? "";
            Ext.Cmd(Name, d);
        }
        public int Support { get { int s; return int.TryParse(R("_sup"), out s) ? s : -1; } }
        public string Pid { get { return R("_rkpid"); } }
        public string Me { get { return R("_rkme"); } }
        public string Error { get { return R("_rkerr"); } }
        public IMatchStats Stats { get { return stats; } }
        public Action<string> Log { set { log = value ?? delegate { }; } }
        public void Poll(string addr, string myNick, bool active, Session s)
        {
            var rl = s != null ? s.T as IRoomLink : null;
            var d = new Dictionary<string, string>
            {
                { "poll", "1" }, { "dir", dir }, { "addr", addr ?? "" }, { "nick", myNick ?? "" }, { "active", active ? "1" : "0" },
                { "lang", L.En ? "en" : "zh" }, { "ver", Proto.App },
                { "ses", s != null ? "1" : "0" }, { "sno", s != null ? s.Serial.ToString() : "0" }, { "room", rl != null ? "1" : "0" },
                { "role", s != null ? s.Role ?? "" : "" }, { "mode", s != null ? s.Mode ?? "" : "" }, { "conn", s != null && s.Connected ? "1" : "0" },
                { "sid", rl != null ? rl.Sid ?? "" : "" }, { "lmt", s != null ? s.LocalMt ?? "" : "" }, { "pmt", rl != null ? rl.PeerMt ?? "" : "" },
                { "prk", rl != null ? rl.PeerRk ?? "" : "" }, { "pquit", rl != null ? rl.PeerQuit ?? "" : "" }, { "lquit", s != null ? s.LocalQuit ?? "" : "" },
                { "lrkd", s != null ? s.LocalRkd ?? "" : "" }, { "lrfp", s != null ? s.LocalRfp ?? "" : "" },
            };
            Ext.Cmd(Name, d);
            var n = R("_logn");
            if (n.Length > 0 && n != lastLogN) { lastLogN = n; log(R("_log")); }     // 插件的日志行给窗口 / 面板看（和以前 rank.Log 一样）
        }
        public void RequestTop(int page) { Send("req", "top", "page", page.ToString()); }
        public void RequestStats(string kind, int page) { Send("req", "stats", "kind", kind ?? "w", "page", page.ToString()); }
        public string ExportKey() { Send("req", "export"); return R("_key"); }
        public bool ImportKey(string k, out string why)
        {
            Send("req", "import", "key", k ?? "");
            var r = R("_imp");
            if (r == "ok") { why = ""; return true; }
            why = r.StartsWith("err:") ? r.Substring(4) : L.T("排位插件没回应", "Ranked plugin did not answer");
            return false;
        }
        public string TitleOf(string pid)
        {
            if (string.IsNullOrEmpty(pid)) return "";
            var t = R("_who_" + pid);
            if (t.Length == 0) Send("req", "who", "pid", pid);     // 插件 30 秒内不重复问服务器
            return t;
        }
        static readonly string[] Keys = { "rkpid", "rkrules", "rkme", "rkvoid", "rkminver", "rkold", "rkerr", "rknote", "rkres", "rktop", "rkn" };
        public void WriteLobby(StringBuilder sb, bool active)
        {
            Action<string, string> raw = (k, v) => sb.Append(k).Append('=').Append((v ?? "").Replace("\r", " ").Replace("\n", " ")).Append('\n');
            raw("rk", active ? "1" : "0");
            if (!active) return;
            foreach (var k in Keys) raw(k, R("_" + k));
            int n; int.TryParse(R("_rkn"), out n);
            for (int i = 1; i <= n && i <= 10; i++) { raw("rkt" + i, R("_rkt" + i)); raw("rkw" + i, R("_rkw" + i)); }
            raw("rkstat", R("_rkstat"));
            int sn; int.TryParse(R("_rksn"), out sn);
            raw("rksn", sn.ToString());
            for (int i = 1; i <= sn && i <= 10; i++) raw("rks" + i, R("_rks" + i));
        }
        public void Close() { }
    }

    // §110.11 本机 / 对方包的统计：Session 的发包线程、房间连接的收包线程直接推给插件（它那头 MatchStats 自己加锁）
    sealed class PluginStats : IMatchStats
    {
        static void Send(string stat, params string[] kv)
        {
            var d = new Dictionary<string, string> { { "stat", stat } };
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1] ?? "";
            Ext.Cmd(PluginRanked.Name, d);
        }
        public void Start() { Send("start"); }
        public void Stop() { Send("stop"); }
        public void Local(string wc, string wcs, string ev) { Send("local", "wc", wc, "wcs", wcs, "ev", ev); }
        public void Peer(string res) { Send("peer", "res", res); }
        public string Payload() { return ""; }     // 只在插件里用（RSTAT 上报）
    }
}
