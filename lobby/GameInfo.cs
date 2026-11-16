// F5：基底里不写死任何具体游戏。游戏的名字、进程名、模组目录名、开局前备份存档这几样由游戏适配给：
// 适配把自己的 .cs 编进来（FORNET_GAME_CS），实现 partial 方法 Configure 改这里的值（具体适件的实现在它自己的仓库里）。
// 没有适配时用下面的中性默认值。
using System;

namespace ForNet
{
    static partial class GameInfo
    {
        public static string ModName = "fornet";                    // 本地名字前缀：互斥锁 / 和游戏的管道 / UPnP 映射描述 / 本地文件头
        public static string TitleZh = "4net 联机", TitleEn = "4net online";     // 联机窗口标题（exe 版才有窗口）
        public static string GameZh = "这个游戏", GameEn = "this game";         // 提示文字里的游戏名
        public static string AppVersion = "0.0.0";                  // 游戏模组的版本号（和它 Lua 那边的版本一致）：握手时发给对方，两边不一样就提示
        public static string ProcessName = null;                    // 游戏进程名（不带 .exe）：exe 后台模式看游戏还在不在、找桥接管道；null = 只认本进程
        public static Func<string> BackupSave = null;
        public static string RankedZh = "排位", RankedLongZh = "排位", RankedModeZh = "排位（经大厅服务器）";     // 排位方式的中文名（提示文字里用；游戏可以起自己的名字）               // 每局开始前备份存档，返回给日志的一句话；null = 不备份

        static GameInfo() { Configure(); }
        static partial void Configure();                            // 适配实现；没有实现时编译器整个去掉这次调用

        public static string Title { get { return L.T(TitleZh, TitleEn); } }
        public static string Game { get { return L.T(GameZh, GameEn); } }

        // 游戏还开着吗（exe 后台模式用）：不知道进程名时当作开着（不自己退）
        public static bool GameRunning()
        {
            if (string.IsNullOrEmpty(ProcessName)) return true;
            try { return System.Diagnostics.Process.GetProcessesByName(ProcessName).Length > 0; } catch (Exception) { return true; }
        }

        // 可能装着桥接 DLL 的进程：游戏进程（知道名字时）；不知道就只有本进程（DLL 版联机程序就在游戏进程里）
        public static int[] GamePids()
        {
            try
            {
                if (string.IsNullOrEmpty(ProcessName)) return new[] { System.Diagnostics.Process.GetCurrentProcess().Id };
                return Array.ConvertAll(System.Diagnostics.Process.GetProcessesByName(ProcessName), p => p.Id);
            }
            catch (Exception) { return new int[0]; }
        }
    }
}
