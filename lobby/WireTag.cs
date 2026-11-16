// 线上协议里的名字（docs/USAGE.md "协议名字"）：大厅服务器控制包的标记、对端控制包前缀、哈希盐、环境变量前缀、
// 联机程序插件的 C 导出名前缀、桥接管道名前缀。默认是 4net 的中性名；游戏适配在自己的 .cs（FORNET_GAME_CS）里实现
// partial 方法 Configure 改成自己的名字（已经有玩家 / 服务器在用某套名字时，就一直用那套——线上格式一个字节都不能变）。
// 两台电脑上的联机程序、它们连的大厅服务器必须用同一套名字，否则互相不认。
// 只用 C# 5 语法（exe 用系统自带 csc 编）。
using System;

namespace ForNet
{
    static partial class WireTag
    {
        public static string S = "4N";             // 大厅服务器控制包标记："<S>|REG|…"、"<S>|LIST" …（服务端 --wire-server）
        public static string P = "4N";             // 对端控制包前缀："<P>HELLO"、"<P>EV"、"<P>MPHELLO"、服务器转发头 "<P>R|" …（服务端 --wire-peer）
        public static string Salt = "fornet";      // 房间标记 / 桥接 ID 哈希的盐（房间标记在房间列表里，两边要一样）
        public static string EnvPrefix = "FORNET_";     // 测试 / 调参用的环境变量：<前缀>LOOPBACK、<前缀>NO_IPV6 …
        public static string Abi = "fnx_";         // 联机程序插件导出的函数名前缀：<前缀>init / cmd / tick / shutdown
        public static string Pipe = "fornet_bridge_";   // 桥接 DLL 开的命名管道：\\.\pipe\<前缀><游戏进程号>

        static WireTag() { Configure(); }
        static partial void Configure();           // 适配实现；没有实现时编译器整个去掉这次调用

        // 0.4.0：运行时由适配 manifest 给（GameInfo.Load 调）；有任何一个认得的键就返回 true
        public static bool Apply(System.Collections.Generic.Dictionary<string, string> m)
        {
            bool any = false;
            string v;
            if (m.TryGetValue("wire_server", out v) && v.Length > 0) { S = v; any = true; }
            if (m.TryGetValue("wire_peer", out v) && v.Length > 0) { P = v; any = true; }
            if (m.TryGetValue("wire_salt", out v) && v.Length > 0) { Salt = v; any = true; }
            if (m.TryGetValue("wire_env", out v) && v.Length > 0) { EnvPrefix = v; any = true; }
            if (m.TryGetValue("wire_abi", out v) && v.Length > 0) { Abi = v; any = true; }
            if (m.TryGetValue("wire_pipe", out v) && v.Length > 0) { Pipe = v; any = true; }
            return any;
        }
        public static void Reconfigure() { Configure(); }     // manifest 之后再让编进来的适配盖一遍（编进来的优先）

        public static string Env(string name) { return Environment.GetEnvironmentVariable(EnvPrefix + name); }
    }
}
