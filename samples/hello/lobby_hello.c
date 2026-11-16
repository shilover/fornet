/* 示例联机程序插件（docs/EXTENSIONS_API.md，X2）。和 inproc/game_steam.c 一样不用 CRT：clang --target=x86_64-pc-windows-msvc -ffreestanding。
 * 导出 fnx_init / fnx_cmd / fnx_tick / fnx_shutdown。收到 ping=1 回 pong=1；收到 to=<host:port> data=<文字> 就经基底 send 发出去
 * （基底会查同意：没同意回 sent=denied），tick 里收自己 socket 上的回包放进 got=。 */
#include <stdint.h>

typedef struct {
    int32_t version, id;
    void (*log)(int, const char*);
    int  (*consent)(int, const char*);
    int  (*send)(int, const char*, const uint8_t*, int);
    int  (*recv)(int, char*, int, uint8_t*, int);
    void (*reply)(int, const char*, const char*);
    const char* (*name)(int);
} FnxCtx;

static FnxCtx C;
static int ticks;

static int slen(const char* s) { int n = 0; while (s[n]) n++; return n; }
static int starts(const char* s, const char* p) { int i = 0; for (; p[i]; i++) if (s[i] != p[i]) return 0; return i; }
static void itoa10(int v, char* out) {
    char t[12]; int n = 0, neg = v < 0; if (neg) v = -v;
    do { t[n++] = (char)('0' + v % 10); v /= 10; } while (v);
    int k = 0; if (neg) out[k++] = '-'; while (n) out[k++] = t[--n]; out[k] = 0;
}
/* 取一行 "key=value"（到 \n 或 \0），复制 value（最多 cap-1 字节） */
static int getv(const char* kv, const char* key, char* out, int cap) {
    const char* s = kv;
    while (*s) {
        int m = starts(s, key);
        if (m && s[m] == '=') {
            s += m + 1; int n = 0;
            while (*s && *s != '\n' && n < cap - 1) out[n++] = *s++;
            out[n] = 0; return n;
        }
        while (*s && *s != '\n') s++;
        if (*s) s++;
    }
    return -1;
}

__declspec(dllexport) int fnx_init(const FnxCtx* ctx) {
    if (!ctx || ctx->version < 1) return 1;
    C = *ctx;
    C.log(C.id, "hello 插件 init");
    C.reply(C.id, "ver", "0.1.0");
    return 0;
}
__declspec(dllexport) void fnx_cmd(const char* kv) {
    char v[64], to[128], data[512];
    if (getv(kv, "ping", v, sizeof v) > 0) C.reply(C.id, "pong", v);
    if (getv(kv, "to", to, sizeof to) > 0 && getv(kv, "data", data, sizeof data) >= 0) {
        int r = C.send(C.id, to, (const uint8_t*)data, slen(data));
        if (r >= 0) C.reply(C.id, "sent", "ok");
        else if (r == -2) C.reply(C.id, "sent", "denied");
        else { char b[12]; itoa10(r, b); C.reply(C.id, "sent", b); }
    }
    if (getv(kv, "ask", v, sizeof v) > 0) C.reply(C.id, "has", C.consent(C.id, v) ? "1" : "0");
}
__declspec(dllexport) void fnx_tick(void) {
    uint8_t buf[600]; char from[64];
    ticks++;
    int n = C.recv(C.id, from, sizeof from, buf, sizeof buf - 1);
    if (n > 0) { buf[n] = 0; C.reply(C.id, "got", (const char*)buf); C.reply(C.id, "from", from); }
    if ((ticks % 50) == 0) { char b[12]; itoa10(ticks, b); C.reply(C.id, "ticks", b); }
}
__declspec(dllexport) void fnx_shutdown(void) { if (C.log) C.log(C.id, "hello 插件 shutdown"); }

int __stdcall DllMain(void* h, unsigned long reason, void* res) { (void)h; (void)reason; (void)res; return 1; }
