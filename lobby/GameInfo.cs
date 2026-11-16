// 基底里不写死任何具体游戏。游戏的名字、进程名、模组目录名、版本号、开局前备份存档这几样由游戏适配给，两种给法：
//   1. 运行时（0.4.0 起，推荐）：适配目录 games\<名>\manifest.txt 里写下面 Keys 这些键，联机程序启动时读（GameInfo.Load）。
//      这样别的游戏可以直接用编好的 fornet_lobby.dll，不用自己编。
//   2. 编译时：适配把自己的 .cs 编进来（FORNET_GAME_CS），实现 partial 方法 Configure 改这里的值。两种都给时编进去的优先。
// 都没有时用下面的中性默认值。
// 只用 C# 5 语法（exe 用系统自带 csc 编）。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace ForNet
{
    static partial class GameInfo
    {
        public static string ModName = "fornet";                    // 本地名字前缀：互斥锁 / 和游戏的管道 / UPnP 映射描述 / 本地文件头
        public static string TitleZh = "4net 联机", TitleEn = "4net online";     // 联机窗口标题（exe 版才有窗口）
        public static string GameZh = "这个游戏", GameEn = "this game";         // 提示文字里的游戏名
        public static string AppVersion = "0.0.0";                  // 游戏模组的版本号（和它 Lua 那边的版本一致）：握手时发给对方，两边不一样就提示
        public static string ProcessName = null;                    // 游戏进程名（不带 .exe）：exe 后台模式看游戏还在不在、找桥接管道；null = 只认本进程
        public static Func<string> BackupSave = null;               // 每局开始前备份存档，返回给日志的一句话；null = 不备份
        public static string RankedZh = "排位", RankedLongZh = "排位", RankedModeZh = "排位（经大厅服务器）";     // 排位方式的中文名（提示文字里用；游戏可以起自己的名字）

        public static string Source = "default";                    // 这些值从哪来：default / manifest:<适配名>（日志里写；编进来的 Configure 之后还会再改）
        static bool loaded;

        static GameInfo() { Configure(); }
        static partial void Configure();                            // 适配实现；没有实现时编译器整个去掉这次调用

        public static string Title { get { return L.T(TitleZh, TitleEn); } }
        public static string Game { get { return L.T(GameZh, GameEn); } }

        // manifest 里认的键（docs/FORNET_ADAPTER.md 第 5 节）。没写的键保持默认值
        public static readonly string[] Keys = {
            "mod_name", "process", "game_zh", "game_en", "title_zh", "title_en", "app_version",
            "ranked_zh", "ranked_long_zh", "ranked_mode_zh",
            "wire_server", "wire_peer", "wire_salt", "wire_env", "wire_abi", "wire_pipe",
            "save_dir", "save_file", "save_backup_dir",
        };

        // 联机程序启动时调一次（exe 和 DLL 两条路都在用到名字之前调）：root = 模组目录（联机程序 / DLL 所在目录）。
        // 顺序：默认值 → 适配 manifest → 编进来的 Configure（所以编进来的优先）。调多次只第一次生效
        public static void Load(string root)
        {
            if (loaded) return;
            loaded = true;
            string name;
            var m = AdapterManifest(root, out name);
            if (m != null && Apply(m, root)) Source = "manifest:" + name;
            Configure();
            WireTag.Reconfigure();
        }

        // 把 manifest 的值写进来；有任何一个认得的键就返回 true
        public static bool Apply(Dictionary<string, string> m, string root)
        {
            bool any = false;
            Func<string, string> get = k => { string v; return m.TryGetValue(k, out v) && v.Length > 0 ? v : null; };
            Action<string, Action<string>> set = (k, f) => { var v = get(k); if (v != null) { f(v); any = true; } };
            set("mod_name", v => ModName = v);
            set("process", v => ProcessName = v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? v.Substring(0, v.Length - 4) : v);
            set("game_zh", v => GameZh = v);
            set("game_en", v => GameEn = v);
            set("title_zh", v => TitleZh = v);
            set("title_en", v => TitleEn = v);
            set("app_version", v => AppVersion = v);
            set("ranked_zh", v => RankedZh = v);
            set("ranked_long_zh", v => RankedLongZh = v);
            set("ranked_mode_zh", v => RankedModeZh = v);
            if (WireTag.Apply(m)) any = true;
            var dir = get("save_dir");
            if (dir != null)
            {
                any = true;
                string full = SaveBackup.Resolve(root, dir);
                if (full != null)
                {
                    string file = get("save_file"), outName = get("save_backup_dir") ?? "fornet-save-backups";
                    BackupSave = () => SaveBackup.Run(full, file, outName, DateTime.Now);
                }
            }
            return any;
        }

        // 选哪个适配：和宿主（lua/fornet_host.lua）一样——games\games.txt 第一行非注释的名字；没有就按目录名排序，取第一个有 manifest.txt 的
        public static Dictionary<string, string> AdapterManifest(string root, out string name)
        {
            name = null;
            try
            {
                var games = Path.Combine(root, "games");
                if (!Directory.Exists(games)) return null;
                var pick = Path.Combine(games, "games.txt");
                if (File.Exists(pick))
                    foreach (var raw in File.ReadAllLines(pick))
                    {
                        var l = raw.Trim().TrimStart('﻿');
                        if (l.Length == 0 || l.StartsWith("#") || l.StartsWith("--")) continue;
                        name = l; break;
                    }
                if (name == null)
                {
                    var ds = Directory.GetDirectories(games);
                    Array.Sort(ds, StringComparer.OrdinalIgnoreCase);
                    foreach (var d in ds) if (File.Exists(Path.Combine(d, "manifest.txt"))) { name = Path.GetFileName(d); break; }
                }
                if (name == null) return null;
                var path = Path.Combine(Path.Combine(games, name), "manifest.txt");
                return File.Exists(path) ? Kv.Read(path) : null;
            }
            catch (Exception) { return null; }
        }

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

    // 0.4.0 开局前备份存档（以前每个游戏自己写，搬进基底；manifest 的 save_dir / save_file / save_backup_dir）：
    //   save_dir  = 存档目录，相对模组目录或绝对路径（可以带 %环境变量%）。两种布局都认：
    //               直接放着存档文件；或者下面每个账号一个子目录、子目录里放存档文件（每个子目录各备份一份，文件名前缀 = 子目录名）
    //   save_file = 存档文件名；不写 = 目录里所有文件都备份，"和上一个备份一样就跳过"只在写了的时候比
    //   save_backup_dir = 备份放在 save_dir 下哪个目录，默认 fornet-save-backups
    // 规则：整个账号目录打成 <账号>-backup-<时间>-mp.zip；存档和最新的备份一样就不备份；只清自己打的（-mp.zip）：
    // 最近 20 个全留，14 天内每天留最后一个，更早的删。别的工具打的 <账号>-backup-<时间>.zip（不带 -mp）不碰、但"和上一个一样"会拿它比
    static class SaveBackup
    {
        const string MpSuffix = "-mp.zip";
        public const int KeepRecent = 20, KeepDays = 14;
        static readonly char Sep = Path.DirectorySeparatorChar;

        public static string Resolve(string root, string dir)
        {
            try
            {
                dir = Environment.ExpandEnvironmentVariables(dir);
                return Path.GetFullPath(Path.IsPathRooted(dir) ? dir : Path.Combine(root, dir)).TrimEnd(Sep);
            }
            catch (Exception) { return null; }
        }

        static bool HasSave(string dir, string saveFile, bool deep)
        {
            if (saveFile != null) return File.Exists(Path.Combine(dir, saveFile));
            return Directory.GetFiles(dir, "*", deep ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Length > 0;
        }

        public static string Run(string saveDir, string saveFile, string outName, DateTime now)
        {
            try
            {
                if (!Directory.Exists(saveDir)) return L.T("找不到存档目录，跳过备份", "save folder not found, backup skipped");
                var outDir = Path.Combine(saveDir, outName);
                var accounts = new List<string>();
                if (HasSave(saveDir, saveFile, false)) accounts.Add(saveDir);
                else
                    foreach (var d in Directory.GetDirectories(saveDir))
                    {
                        if (string.Equals(Path.GetFileName(d), outName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (HasSave(d, saveFile, true)) accounts.Add(d);
                    }
                if (accounts.Count == 0) return L.T("没有找到存档，跳过备份", "no save found, backup skipped");
                Directory.CreateDirectory(outDir);
                int made = 0, same = 0, removed = 0;
                foreach (var dir in accounts)
                {
                    var id = Path.GetFileName(dir);
                    if (saveFile != null && SameAsLatest(outDir, id, Path.Combine(dir, saveFile), saveFile)) same++;
                    else
                    {
                        WriteZip(dir, outDir, Path.Combine(outDir, id + "-backup-" + now.ToString("yyyyMMdd-HHmmss") + MpSuffix));
                        made++;
                    }
                    removed += Prune(outDir, id, now);
                }
                string msg = made > 0
                    ? L.T("已备份存档（存档目录" + Sep + outName + "）", "save backed up (save folder" + Sep + outName + ")")
                    : L.T("存档和上一个备份一样，没再备份", "save unchanged since last backup, skipped");
                if (removed > 0) msg += L.T("；清掉 " + removed + " 个旧的自动备份", "; removed " + removed + " old automatic backups");
                return msg;
            }
            catch (Exception ex) { return L.T("备份失败：", "backup failed: ") + ex.Message; }
        }

        static void WriteZip(string dir, string outDir, string zip)
        {
            var tmp = zip + ".part";
            string skip = Path.GetFullPath(outDir).TrimEnd(Sep) + Sep;
            using (var fs = new FileStream(tmp, FileMode.Create))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFullPath(f).StartsWith(skip, StringComparison.OrdinalIgnoreCase)) continue;     // 备份目录在存档目录里：别把备份打进备份
                    var rel = f.Substring(dir.Length + 1).Replace(Sep, '/');
                    var e = za.CreateEntry(rel, CompressionLevel.Optimal);
                    using (var es = e.Open())
                    using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        src.CopyTo(es);
                }
            }
            File.Move(tmp, zip);     // 写完才改名：写一半出错不会留下一个看着正常的坏备份
        }

        static bool Stamp(string file, string id, out DateTime t)
        {
            t = DateTime.MinValue;
            var name = Path.GetFileName(file);
            var pre = id + "-backup-";
            if (!name.StartsWith(pre, StringComparison.OrdinalIgnoreCase) || name.Length < pre.Length + 15) return false;
            return DateTime.TryParseExact(name.Substring(pre.Length, 15), "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t);
        }

        // 最新的那个备份（自己的、别的工具的都算）里的存档文件和现在的逐字节一样？读不出来就当不一样、照常备份
        static bool SameAsLatest(string outDir, string id, string sav, string saveFile)
        {
            string latest = null; DateTime best = DateTime.MinValue;
            foreach (var f in Directory.GetFiles(outDir, id + "-backup-*.zip"))
            {
                DateTime t;
                if (Stamp(f, id, out t) && t >= best) { best = t; latest = f; }
            }
            if (latest == null) return false;
            try
            {
                byte[] cur;
                using (var src = new FileStream(sav, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    cur = new byte[src.Length];
                    int n = 0, r;
                    while (n < cur.Length && (r = src.Read(cur, n, cur.Length - n)) > 0) n += r;
                    if (n != cur.Length) return false;
                }
                using (var zs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var za = new ZipArchive(zs, ZipArchiveMode.Read))     // 不用 ZipFile：exe 版没引用 System.IO.Compression.FileSystem
                {
                    var e = za.GetEntry(saveFile.Replace(Sep, '/'));
                    if (e == null || e.Length != cur.Length) return false;
                    using (var es = e.Open())
                    {
                        var buf = new byte[65536];
                        int pos = 0, r;
                        while ((r = es.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (pos + r > cur.Length) return false;
                            for (int i = 0; i < r; i++) if (buf[i] != cur[pos + i]) return false;
                            pos += r;
                        }
                        return pos == cur.Length;
                    }
                }
            }
            catch (Exception) { return false; }
        }

        static int Prune(string outDir, string id, DateTime now)
        {
            var list = new List<KeyValuePair<DateTime, string>>();
            foreach (var f in Directory.GetFiles(outDir, id + "-backup-*" + MpSuffix))
            {
                DateTime t;
                if (Stamp(f, id, out t)) list.Add(new KeyValuePair<DateTime, string>(t, f));
            }
            list.Sort(delegate (KeyValuePair<DateTime, string> a, KeyValuePair<DateTime, string> b) { return b.Key.CompareTo(a.Key); });
            var days = new HashSet<DateTime>();
            var cutoff = now.Date.AddDays(-(KeepDays - 1));
            int removed = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var day = list[i].Key.Date;
                bool firstOfDay = days.Add(day);
                if (i < KeepRecent) continue;
                if (firstOfDay && day >= cutoff) continue;
                try { File.Delete(list[i].Value); removed++; } catch (Exception) { }
            }
            return removed;
        }
    }
}
