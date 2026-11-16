// F5（docs/FORNET_F5_F6.md）：本地 AI 对手归游戏适配（11-13 拆分）。基底只留这层接口：
// 开 / 停、难度名、装备配置的透传（netcfg 行 / aiload 命令 / lobby.txt 行），出招逻辑一行都不在基底里。
// 适配把自己的 .cs 编进同一个程序集（build.bat 的 FORNET_GAME_CS / AOT 的 -p:GameCs），在里面实现 partial 方法 Register 给 Impl 赋值；
// 没编进来时 Impl 是 null：难度列表为空、装备配置没有任何键、开本地 AI 报"这个版本没有本地 AI"。
using System;
using System.Collections.Generic;
using System.Text;

namespace ForNet
{
    // 一个正在跑的本地 AI（对战中也能换难度 / 装备，下一帧生效）
    interface ILocalAi
    {
        int Level { set; }
        IAiLoad Load { set; }
        Action<string> Log { set; }
        void Start();
        void Stop();
    }

    // 多人本地 AI 的一个机器人实例（只在主机进程内跑，收发状态包正文，不碰网络与房间管理）
    interface IMpAiBot
    {
        int Pid { get; }
        string Name { get; }
        int Level { set; }
        IAiLoad Load { set; }
        Action<int, string> OnState { set; }
        Action<string> Log { set; }
        void Start();
        void Stop();
        void FeedPacket(int pid, string body);
        void RemovePlayer(int pid);
        void SetMeta(int hostPid, ICollection<int> aiPids);
    }

    // AI 装备：键名和取值规则都由适配定，基底只负责存取和转发
    interface IAiLoad
    {
        string[] CmdKeys { get; }                   // 面板命令 aiload 认的键
        bool Set(string cmdKey, string v);          // 面板命令 aiload 的一项；不合法返回 false（整条命令不改）
        bool LoadCfg(string cfgKey, string v);      // netcfg.txt 的一行；不是自己的键 / 不合法返回 false（照旧用默认）
        void SaveCfg(StringBuilder sb);             // 写回 netcfg.txt（每项一行 key=value\n）
        void LobbyKv(Action<string, string> kv);    // lobby.txt 里给面板标当前选择的行
        IAiLoad Clone();
    }

    interface ILocalAiImpl
    {
        int LevelCount { get; }
        string LevelName(int i);                    // 按当前语言
        IAiLoad NewLoad();
        // aiDir = AI 那个 Session 的目录（net\ai\）；backFrom = 真人离开后重开时离开的人（§85）
        ILocalAi Create(string aiDir, int level, IAiLoad load, string backFrom);
        IMpAiBot CreateMp(int pid, string name, int level, IAiLoad load);
        string[] SetupLogPrefixes();                // 开局那几行日志的开头：重开 AI 时先不盖掉"XXX 已离开房间"（§127）
    }

    static partial class LocalAiSupport
    {
        public static readonly ILocalAiImpl Impl;
        static LocalAiSupport() { ILocalAiImpl i = null; Register(ref i); Impl = i; }
        static partial void Register(ref ILocalAiImpl impl);     // 适配实现；没有实现时编译器整个去掉这次调用

        public static bool Has { get { return Impl != null; } }
        public static int LevelCount { get { return Impl != null ? Impl.LevelCount : 0; } }
        public static string LevelName(int i) { return Impl != null ? Impl.LevelName(i) : ""; }
        public static IAiLoad NewLoad() { return Impl != null ? Impl.NewLoad() : new NoLoad(); }
        public static IMpAiBot CreateMp(int pid, string name, int level, IAiLoad load) { return Impl != null ? Impl.CreateMp(pid, name, level, load) : null; }
        public static string[] SetupLogPrefixes() { return Impl != null ? Impl.SetupLogPrefixes() : new string[0]; }

        sealed class NoLoad : IAiLoad
        {
            public string[] CmdKeys { get { return new string[0]; } }
            public bool Set(string cmdKey, string v) { return false; }
            public bool LoadCfg(string cfgKey, string v) { return false; }
            public void SaveCfg(StringBuilder sb) { }
            public void LobbyKv(Action<string, string> kv) { }
            public IAiLoad Clone() { return this; }
        }
    }
}
