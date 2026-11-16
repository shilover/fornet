// 多人网状（UDP 房间 bridgemode=1）单对按需升级到桥接的纯决策逻辑与常量。
// C# 5 语法（Windows 自带 .NET Framework 4 csc.exe 编译；同时兼容 .NET 8 Native AOT）。
using System;
using System.Collections.Generic;

namespace ForNet
{
    static class BridgeUp
    {
        public const int DefaultThresholdMs = 150;     // netcfg bridgeupms 默认值（ms）
        public const int MarginMs = 20;                // 切过去要快出 20ms；切回来要慢过直连 20ms
        public const int DirectSamples = 8;            // 先量 8 次直连 RTT（0.5s 一次）
        public const int BridgeSamples = 6;            // 桥接连上后量 6 次 RTT 再决定切不切
        public const int CheckSamples = 8;             // 切过去后用最近 8 次样本持续比较
        public const double ProbeIntervalSec = 0.5;
        public const double ConnectTimeoutSec = 15.0;  // 15 秒桥接还没通就放弃这一对
        public const double SilentTimeoutSec = 1.0;    // 切过去后桥接 1 秒没包 → 切回直连
        public const double CheckDelaySec = 4.0;       // 切过去 4 秒后开始每 0.5 秒比对
        public const int JitterMs = 100;               // 桥接抖动 > 100ms 且 > 直连 + 50ms → 切回
        public const int JitterOverMs = 50;

        // netcfg bridgeup=0|1（默认 1）、bridgeupms=150
        public static volatile bool On = true;
        public static volatile int ThresholdMs = DefaultThresholdMs;

        sealed class AddSpec
        {
            public int Default;
            public readonly Dictionary<string, int> Pairs = new Dictionary<string, int>();
            public readonly Dictionary<int, int> Peers = new Dictionary<int, int>();
        }

        static AddSpec ParseAdd(string raw)
        {
            var sp = new AddSpec();
            if (string.IsNullOrEmpty(raw)) return sp;
            raw = raw.Trim();
            int single;
            if (int.TryParse(raw, out single))
            {
                sp.Default = single > 0 ? single : 0;
                return sp;
            }
            string[] parts = raw.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0) continue;
                int c = part.IndexOf(':');
                if (c <= 0) continue;
                string lhs = part.Substring(0, c).Trim();
                int ms;
                if (!int.TryParse(part.Substring(c + 1).Trim(), out ms) || ms < 0) continue;
                int dash = lhs.IndexOf('-');
                if (dash > 0)
                {
                    int a, b;
                    if (int.TryParse(lhs.Substring(0, dash).Trim(), out a) && int.TryParse(lhs.Substring(dash + 1).Trim(), out b) && a > 0 && b > 0)
                    {
                        int lo = a < b ? a : b, hi = a < b ? b : a;
                        sp.Pairs[lo + "-" + hi] = ms;
                    }
                }
                else
                {
                    int pid;
                    if (int.TryParse(lhs, out pid) && pid > 0) sp.Peers[pid] = ms;
                }
            }
            return sp;
        }

        static readonly AddSpec directAdd = ParseAdd(WireTag.Env("BU_DIRECT_ADD_MS"));
        static readonly AddSpec bridgeAdd = ParseAdd(WireTag.Env("BU_BRIDGE_ADD_MS"));

        public static int DirectAddMs { get { return directAdd.Default; } }
        public static int BridgeAddMs { get { return bridgeAdd.Default; } }

        static int LookupAdd(AddSpec sp, int myPid, int peerPid)
        {
            if (myPid > 0 && peerPid > 0)
            {
                int lo = myPid < peerPid ? myPid : peerPid;
                int hi = myPid < peerPid ? peerPid : myPid;
                int v;
                if (sp.Pairs.TryGetValue(lo + "-" + hi, out v)) return v;
            }
            if (peerPid > 0)
            {
                int v;
                if (sp.Peers.TryGetValue(peerPid, out v)) return v;
            }
            return sp.Default;
        }

        public static int DirectAddMsFor(int myPid, int peerPid) { return LookupAdd(directAdd, myPid, peerPid); }
        public static int BridgeAddMsFor(int myPid, int peerPid) { return LookupAdd(bridgeAdd, myPid, peerPid); }

        public static void AddRtt(List<double> list, double ms)
        {
            AddRtt(list, ms, CheckSamples);
        }

        public static void AddRtt(List<double> list, double ms, int cap)
        {
            if (list == null) return;
            list.Add(ms >= 0 ? ms : 0);
            while (list.Count > cap) list.RemoveAt(0);
        }

        public static List<double> Recent(List<double> xs, int n)
        {
            var r = new List<double>();
            if (xs == null || xs.Count == 0 || n <= 0) return r;
            int start = xs.Count > n ? xs.Count - n : 0;
            for (int i = start; i < xs.Count; i++) r.Add(xs[i]);
            return r;
        }

        public static double Median(List<double> xs)
        {
            if (xs == null || xs.Count == 0) return -1;
            var a = new List<double>(xs);
            a.Sort();
            int n = a.Count;
            return (n & 1) == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) * 0.5;
        }

        // 抖动：样本数 >= 4 时去掉最高、最低各 1 个后的最大减最小；少于 4 个时用最大减最小
        public static double Spread(List<double> xs)
        {
            if (xs == null || xs.Count < 2) return 0;
            var a = new List<double>(xs);
            a.Sort();
            if (a.Count >= 4) return a[a.Count - 2] - a[1];
            return a[a.Count - 1] - a[0];
        }

        // 已量满 8 次直连样本且中位数 > thresholdMs → 需要尝试升级桥接
        public static bool ShouldTry(List<double> dirRecent, int thresholdMs)
        {
            if (dirRecent == null || dirRecent.Count < DirectSamples) return false;
            return Median(dirRecent) > thresholdMs;
        }

        // 已量满 8 次直连样本且中位数 <= thresholdMs → 直连够快，保持直连
        public static bool ShouldStayDirect(List<double> dirRecent, int thresholdMs)
        {
            if (dirRecent == null || dirRecent.Count < DirectSamples) return false;
            return Median(dirRecent) <= thresholdMs;
        }

        // 当前路径中位数 < 0（直连不通 / 暂无样本）或 桥接中位数 + 20 < 当前路径中位数 → 切到桥接
        public static bool ShouldSwitch(double bridgeMedian, double currentRouteMedian)
        {
            if (bridgeMedian < 0) return false;
            if (currentRouteMedian < 0) return true;
            return bridgeMedian + MarginMs < currentRouteMedian;
        }

        // 切到桥接 4 秒后持续比较：桥接慢过直连 + 20ms，或桥接抖动 > 100 且 > 直连抖动 + 50 → 切回直连
        public static bool ShouldSwitchBack(List<double> dirRecent, List<double> brRecent, out string reason)
        {
            reason = null;
            if (brRecent == null || brRecent.Count == 0) return false;
            double bm = Median(brRecent);
            double dm = Median(dirRecent);
            if (dm >= 0 && bm > dm + MarginMs)
            {
                reason = "桥接延迟高过直连 (" + bm.ToString("0") + " > " + dm.ToString("0") + " + " + MarginMs + " ms)";
                return true;
            }
            double bs = Spread(brRecent);
            double ds = Spread(dirRecent);
            if (bs > JitterMs && bs > ds + JitterOverMs)
            {
                reason = "桥接抖动过大 (" + bs.ToString("0") + " > " + ds.ToString("0") + " + " + JitterOverMs + " ms)";
                return true;
            }
            return false;
        }
    }
}
