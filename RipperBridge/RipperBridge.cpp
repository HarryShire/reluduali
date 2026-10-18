// RipperBridge.Red32n - a Redacted plugin that lets an outside program talk to the
// running game, frame by frame. Mycelium, 2026-09-29: "it be cool to get some knowledge
// on how a Red32 Plugin Can communicate with an External APP."
//
// How it is loaded: Redacted LoadLibrary()s every *.Red32n in Plugins\ and calls the
// exports below. The ABI was read from ExtendedConsole.Red32n / ExtendedBots.Red32n
// (2026-09-29): every export is cdecl and returns its value in EAX:EDX, so they are
// declared returning a 64-bit integer. A plugin without PreInit is logged as "missing
// an export (PreInit) and will be removed".
//
// What it offers: a named pipe, \\.\pipe\bo2ripper, one text line per request.
//   ping                      -> pong ripperbridge <version>
//   hello                     -> what is hooked and resolved
//   stats                     -> frames, fps, demo tick, end tick, theater base
//   cmd <text>                -> run a console command (ExtendedConsole's Cmd_ExecuteCommand)
//   dvar <name>               -> type and current value, read from the dvar table
//   setdvar <name> <value>    -> write the value into the table (past cheat protection)
//   unprotect / protect <name> -> clear / restore the dvar's cheat flag (0x80) so "cmd <name> <v>" applies
//   unhook / slot             -> take the Present hook out / show who holds the slot
//   asset <type> <name>       -> the engine's own asset lookup (5 = xmodel)
//   read <hexaddr> <len>      -> hex bytes (in-process)
//   write <hexaddr> <hex>     -> write bytes (Redacted only; retail is never touched)
//   loaddemo <path>           -> load a .demo from disk into the theater (from the menus)
//   watch <n>                 -> one line per drawn frame for the next n frames, then "end"
//   font / font apply / font restore -> the font swap's state, or redo / undo it (see "fonts")
//   modes                     -> the display sizes offered to the game (sandbox; see "display modes")
//   rec start <ffmpeg>|<out>|<codec args>|end=<tick>[|start=<tick>][|step=<ms>][|depth=<distance>][|plate=1][|only=world|players][|params=<file>]  -> record:
//                                fixedtime steps the film, our Present hook takes each frame (see "Reluduali recorder")
//   rec stop / rec status
//   cam find | set x y z pitch yaw roll [fov] | path <track> | off | mode 0|1|2 | get | rec <file> | rec stop | status
//                             -> drive / read the theater's FREE CAMERA every frame (see "the camera override")
//   cam lead <ms>|auto | fovlock on|off | diag <file> <n> | diag stop | watch <targets>|dump <file>|off | guard on|off
//                             -> dev10/11: the 100 ms glitch's tools and its fix (see "dev10")
//   wpn rec <file> | wpn rec stop -> the players' held weapons, per frame, on every change (see "held weapons")
//   kf save | jump <tick> [cam] | marker <N> [keep] | list | status | clear | keep on|off | file save|load <path>
//                             -> dev21: keyframes for a draggable timeline, the theater's own marker snapshots kept
//                                outside the marker list (see "keyframes"); dev22: "marker N", the film to his marker N
//   gfx probe <file> | depth <file.pgm> | grab <resource> [k] <file> | hide off|world|players|vs|ps|il|vb <p>|dynamic|static [except] | state | status
//                             -> dev23: one frame of the game's Direct3D 11 calls; its depth buffer read back (see "gfx")
//   steady on [W H] | off | strip on|off | status
//                             -> dev15-dev22: the docked game takes the focus without a camera turn or the click
//   img find <name>|<report file> | swap <name>|<file.dds> | restore <name>|all
//                             -> dev27-dev28: a loaded image found, and swapped live for a DDS (see "live camo swap")
//   fog on | off | test | lock on|off | where scene|present | set <key>=<value> ... | status
//                             -> dev31-dev37: our own fog with blend modes, temporary until locked (see "the temporary fog pass")
//   params load <file> | clear | live on|off | at <tick> | status
//                             -> dev33: parameters keyed in time, applied every step (see "the playback engine")
//   mem findf <x> <y> <z> [tol] | mem find4 <hex>
//                             -> dev37: where a value is in the game's memory (read only)
// The frame hook is IDXGISwapChain::Present, patched in the class vtable, so it chains
// with anything else that hooks the same way. From 0.10.1 it is installed at start
// only with RipperBridge.present beside the exe (the sandbox); otherwise on the first
// capture or watch, because with Steam's overlay injected it crashed the game (see "fonts").
//
// Built for the Windows Sandbox copy of the game. From 0.10 (2026-09-30) it is
// also installed in Mycelium's real Redacted, with his OK, for the font swap.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <share.h>
#include <math.h>
#include <float.h>
#include <limits.h>
#include <tlhelp32.h>
#include <intrin.h>
#pragma intrinsic(_ReturnAddress)

#pragma comment(lib, "d3d11.lib")

typedef unsigned __int64 u64;
static const char *kVersion = "0.7.0";

// ---- addresses in t6mpv43 (RVA from the module base; the exe has no ASLR) --------
// The theater's clock and base (confirmed live), and the dvar table (the dvar walk).
static const DWORD kRvaTick = 0xD54520;
static const DWORD kRvaEndTick = 0x2CF3470;
static const DWORD kRvaTheaterBase = 0x2D83A00;
static const DWORD kRvaDvarPool = 0x261FC48;   // first dvar record, 0x60 bytes each
static const DWORD kDvarStride = 0x60;

static BYTE *g_base;
static HMODULE g_self;
static FILE *g_log;

static void Log(const char *fmt, ...)
{
    if (!g_log) return;
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(g_log, "%02d:%02d:%02d.%03d ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list a; va_start(a, fmt); vfprintf(g_log, fmt, a); va_end(a);
    fputc('\n', g_log); fflush(g_log);
}

// ---- an over-long string must never take the game down either ---------------------
// With the static CRT and no handler of our own, a _s function's bounds check ends the whole game with no error box (the
// code review's C-4: a long take path, image name or params line did it). Ours lets the call fail instead - an empty
// string, then an error answer - and counts it ("hello" shows it). It covers this plugin's CRT only, never the game's.
static volatile LONG g_badParams;
static void __cdecl QuietBadParameter(const wchar_t *, const wchar_t *, const wchar_t *, unsigned int, uintptr_t) { InterlockedIncrement(&g_badParams); }

// ---- safe reads: a bad pointer must never take the game down -----------------------
static bool SafeCopy(void *dst, const void *src, size_t n)
{
    __try { memcpy(dst, src, n); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static int ReadInt(DWORD rva) { int v = 0; SafeCopy(&v, g_base + rva, 4); return v; }
static bool SafeStr(const char *p, char *out, size_t cap)
{
    if ((DWORD)(size_t)p < 0x10000) return false;
    __try {
        size_t i = 0;
        for (; i + 1 < cap && p[i]; i++) { if ((unsigned char)p[i] < 9 || (unsigned char)p[i] > 126) return false; out[i] = p[i]; }
        out[i] = 0; return i > 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// ---- ExtendedConsole's exports (cdecl) ---------------------------------------------
typedef u64 (__cdecl *ExecuteCommand_t)(const char *text, bool immediate);
typedef u64 (__cdecl *AddCommand_t)(const char *name, void (*fn)());
static ExecuteCommand_t g_exec;
static AddCommand_t g_addCommand;

static void ResolveConsole()
{
    HMODULE ec = GetModuleHandleA("ExtendedConsole.Red32n");
    if (!ec) { Log("ExtendedConsole.Red32n is not loaded"); return; }
    g_exec = (ExecuteCommand_t)GetProcAddress(ec, "Cmd_ExecuteCommand");
    g_addCommand = (AddCommand_t)GetProcAddress(ec, "Cmd_AddCommand");
    Log("ExtendedConsole %p: Cmd_ExecuteCommand %p, Cmd_AddCommand %p", ec, g_exec, g_addCommand);
}

// ---- the frame hook ------------------------------------------------------------------
struct FrameInfo { LONG frame; double ms; int tick; };
static const int kRing = 1024;
static FrameInfo g_ring[kRing];
static volatile LONG g_frames;
static volatile double g_fps;
static LARGE_INTEGER g_qpf;
static bool g_hooked;
static volatile HRESULT g_lastPresent;           // DXGI_STATUS_OCCLUDED is 0x087A0001
static volatile LONG g_notOk;

typedef HRESULT (STDMETHODCALLTYPE *Present_t)(IDXGISwapChain *, UINT, UINT);
static Present_t g_present;
static void **g_slot;                           // the vtable entry we patched, for unhook

static double NowMs() { LARGE_INTEGER c; QueryPerformanceCounter(&c); return c.QuadPart * 1000.0 / g_qpf.QuadPart; }

// ---- video: the recorder's frames straight to ffmpeg -----------------------------------
// Each recorded frame's backbuffer is copied and queued for ffmpeg's stdin. A slow encoder
// slows the render (the queue is bounded); it can never drop a frame, because the film only
// steps on once its frame has been taken (see "Reluduali recorder").

struct CapBuf { BYTE *data; DWORD size; volatile LONG full; };
static const int kCapBufs = 3;
static struct {
    volatile bool armed;
    char ffmpeg[MAX_PATH], out[MAX_PATH], codec[1024], pass[64];
    char rate[32]; DWORD prio;          // rate: the video's frame rate as ffmpeg takes it ("1000", "1000/3")
    // render-thread state
    ID3D11DeviceContext *ctx; ID3D11Texture2D *staging, *resolved; UINT w, h; DXGI_FORMAT fmt;
    bool started;                    // ffmpeg launched (at the first frame, when the size is known)
    HANDLE proc, stdinW, writer, bufFree, bufFull;
    CapBuf bufs[kCapBufs]; int nextFill, nextSend;
    FILE *index;
    volatile LONG frames, written, waits; volatile LONGLONG bytes;
    double copyMs, waitMs;
    char err[256];
} g_cap;
// dev23d: the depth pass, beside the recorder's picture (see "the depth pass")
static bool DepArm(const char *spec, char *why, size_t cap);
static bool PlateArm(bool on, char *why, size_t cap);
static void RecOnly(int kind);
static void DepFrame(IDXGISwapChain *sc);
static void DepPresentEnd();
static void DepPresentStart(IDXGISwapChain *sc);
static void DepStop(char *out, size_t cap);
static void DepStatus(char *out, size_t cap);
static struct {                                      // the Reluduali recorder (see "Reluduali recorder")
    volatile bool active, done;
    int start, end, step, lastTick, prevTick, n, endedAt, skips, firstSkip, curTick, stable, firstTick;
    int maxfps;                                      // the game's com_maxfps, put back at the end
} g_rec;


static const char *PixFmt(DXGI_FORMAT f)
{
    switch (f) {
    case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: case DXGI_FORMAT_R8G8B8A8_TYPELESS: return "rgba";
    case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: case DXGI_FORMAT_B8G8R8A8_TYPELESS: return "bgra";
    case DXGI_FORMAT_B8G8R8X8_UNORM: case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB: return "bgr0";
    case DXGI_FORMAT_R10G10B10A2_UNORM: case DXGI_FORMAT_R10G10B10A2_TYPELESS: return "x2bgr10le";
    default: return NULL;
    }
}

static void CapFail(const char *fmt, ...)
{
    va_list a; va_start(a, fmt); _vsnprintf_s(g_cap.err, sizeof(g_cap.err), _TRUNCATE, fmt, a); va_end(a);
    Log("capture: %s", g_cap.err);
    g_cap.armed = false;
}

static DWORD WINAPI CapWriter(LPVOID)
{
    for (;;) {
        WaitForSingleObject(g_cap.bufFull, INFINITE);
        for (;;) {
            CapBuf &b = g_cap.bufs[g_cap.nextSend];
            if (!b.full) break;
            if (!b.data) { b.full = 0; return 0; }           // the stop marker
            DWORD done = 0, n = 0;
            while (done < b.size) {
                if (!WriteFile(g_cap.stdinW, b.data + done, b.size - done, &n, NULL) || !n) {
                    CapFail("ffmpeg stopped reading (error %lu) after %ld frames - see %s.ffmpeg.log", GetLastError(), g_cap.written, g_cap.out);
                    b.full = 0; SetEvent(g_cap.bufFree); return 1;
                }
                done += n;
            }
            InterlockedExchangeAdd64(&g_cap.bytes, b.size);
            InterlockedIncrement(&g_cap.written);
            InterlockedExchange(&b.full, 0);
            g_cap.nextSend = (g_cap.nextSend + 1) % kCapBufs;
            SetEvent(g_cap.bufFree);
        }
    }
}

static bool CapLaunch()
{
    const char *pix = PixFmt(g_cap.fmt);
    if (!pix) { CapFail("backbuffer format %d is not handled", (int)g_cap.fmt); return false; }
    char cmd[2048];
    _snprintf_s(cmd, sizeof(cmd), _TRUNCATE,
        "\"%s\" -hide_banner -nostats -y -f rawvideo -pix_fmt %s -s %ux%u -framerate %s -i - %s \"%s\"",
        g_cap.ffmpeg, pix, g_cap.w, g_cap.h, g_cap.rate, g_cap.codec, g_cap.out);

    SECURITY_ATTRIBUTES sa = { sizeof(sa), NULL, TRUE };
    HANDLE rd = NULL, wr = NULL;
    if (!CreatePipe(&rd, &wr, &sa, 1 << 20)) { CapFail("CreatePipe %lu", GetLastError()); return false; }
    SetHandleInformation(wr, HANDLE_FLAG_INHERIT, 0);
    char logPath[MAX_PATH]; _snprintf_s(logPath, sizeof(logPath), _TRUNCATE, "%s.ffmpeg.log", g_cap.out);
    HANDLE log = CreateFileA(logPath, GENERIC_WRITE, FILE_SHARE_READ, &sa, CREATE_ALWAYS, 0, NULL);
    HANDLE nul = CreateFileA("NUL", GENERIC_WRITE, FILE_SHARE_WRITE, &sa, OPEN_EXISTING, 0, NULL);

    // Only these three handles go to ffmpeg, not every inheritable handle the game holds.
    HANDLE list[3] = { rd, log, nul };
    SIZE_T attrSize = 0; InitializeProcThreadAttributeList(NULL, 1, 0, &attrSize);
    LPPROC_THREAD_ATTRIBUTE_LIST attrs = (LPPROC_THREAD_ATTRIBUTE_LIST)HeapAlloc(GetProcessHeap(), 0, attrSize);
    InitializeProcThreadAttributeList(attrs, 1, 0, &attrSize);
    UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, list, sizeof(list), NULL, NULL);
    STARTUPINFOEXA si = {}; si.StartupInfo.cb = sizeof(si);
    si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    si.StartupInfo.hStdInput = rd; si.StartupInfo.hStdOutput = nul; si.StartupInfo.hStdError = log;
    si.lpAttributeList = attrs;
    PROCESS_INFORMATION pi = {};
    BOOL ok = CreateProcessA(NULL, cmd, NULL, NULL, TRUE, EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | g_cap.prio,
                             NULL, NULL, &si.StartupInfo, &pi);
    DWORD e = GetLastError();
    DeleteProcThreadAttributeList(attrs); HeapFree(GetProcessHeap(), 0, attrs);
    CloseHandle(rd); if (log != INVALID_HANDLE_VALUE) CloseHandle(log); if (nul != INVALID_HANDLE_VALUE) CloseHandle(nul);
    if (!ok) { CloseHandle(wr); CapFail("could not start ffmpeg (%lu): %s", e, cmd); return false; }
    CloseHandle(pi.hThread);
    g_cap.proc = pi.hProcess; g_cap.stdinW = wr;
    Log("capture: ffmpeg pid %lu: %s", pi.dwProcessId, cmd);

    DWORD frameSize = g_cap.w * g_cap.h * 4;
    for (int i = 0; i < kCapBufs; i++) {
        g_cap.bufs[i].data = (BYTE *)VirtualAlloc(NULL, frameSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        g_cap.bufs[i].size = frameSize; g_cap.bufs[i].full = 0;
        if (!g_cap.bufs[i].data) { CapFail("out of memory for frame buffers (%lu bytes each)", frameSize); return false; }
    }
    g_cap.nextFill = g_cap.nextSend = 0;
    g_cap.bufFree = CreateEventA(NULL, FALSE, FALSE, NULL);
    g_cap.bufFull = CreateEventA(NULL, FALSE, FALSE, NULL);
    g_cap.writer = CreateThread(NULL, 0, CapWriter, NULL, 0, NULL);

    char idx[MAX_PATH]; _snprintf_s(idx, sizeof(idx), _TRUNCATE, "%s.frames.csv", g_cap.out);
    g_cap.index = _fsopen(idx, "w", _SH_DENYNO);
    if (g_cap.index) fprintf(g_cap.index, "frame,tick\n");
    g_cap.started = true;
    return true;
}

// Render thread only (inside Present): the immediate context is the game's.
static void CapFrame(IDXGISwapChain *sc, int passFrame)
{
    double t0 = NowMs();
    ID3D11Texture2D *bb = NULL;
    if (FAILED(sc->GetBuffer(0, __uuidof(ID3D11Texture2D), (void **)&bb)) || !bb) { CapFail("GetBuffer failed"); return; }
    D3D11_TEXTURE2D_DESC d; bb->GetDesc(&d);
    if (!g_cap.ctx) { ID3D11Device *dev = NULL; bb->GetDevice(&dev); dev->GetImmediateContext(&g_cap.ctx); dev->Release(); }
    if (!g_cap.staging) {
        ID3D11Device *dev = NULL; bb->GetDevice(&dev);
        D3D11_TEXTURE2D_DESC s = d;
        s.Usage = D3D11_USAGE_STAGING; s.BindFlags = 0; s.CPUAccessFlags = D3D11_CPU_ACCESS_READ; s.MiscFlags = 0;
        s.SampleDesc.Count = 1; s.SampleDesc.Quality = 0; s.MipLevels = 1; s.ArraySize = 1;
        HRESULT hr = dev->CreateTexture2D(&s, NULL, &g_cap.staging);
        if (SUCCEEDED(hr) && d.SampleDesc.Count > 1) {
            D3D11_TEXTURE2D_DESC r = s; r.Usage = D3D11_USAGE_DEFAULT; r.CPUAccessFlags = 0;
            hr = dev->CreateTexture2D(&r, NULL, &g_cap.resolved);
        }
        dev->Release();
        if (FAILED(hr)) { bb->Release(); CapFail("staging texture 0x%08lX", hr); return; }
        g_cap.w = d.Width; g_cap.h = d.Height; g_cap.fmt = d.Format;
        Log("capture: backbuffer %ux%u format %d samples %u", d.Width, d.Height, (int)d.Format, d.SampleDesc.Count);
    }
    if (d.Width != g_cap.w || d.Height != g_cap.h) { bb->Release(); CapFail("the backbuffer changed size mid-capture"); return; }
    if (!g_cap.started && !CapLaunch()) { bb->Release(); return; }

    if (g_cap.resolved) { g_cap.ctx->ResolveSubresource(g_cap.resolved, 0, bb, 0, d.Format); g_cap.ctx->CopyResource(g_cap.staging, g_cap.resolved); }
    else g_cap.ctx->CopyResource(g_cap.staging, bb);
    bb->Release();

    // A free buffer, or wait for the writer (this is where a slow encoder slows the render).
    CapBuf &b = g_cap.bufs[g_cap.nextFill];
    double w0 = NowMs();
    while (b.full && g_cap.armed) { InterlockedIncrement(&g_cap.waits); WaitForSingleObject(g_cap.bufFree, 200); }
    double waited = NowMs() - w0;
    g_cap.waitMs += waited;
    if (!g_cap.armed) return;

    D3D11_MAPPED_SUBRESOURCE m;
    HRESULT hr = g_cap.ctx->Map(g_cap.staging, 0, D3D11_MAP_READ, 0, &m);
    if (FAILED(hr)) { CapFail("Map 0x%08lX", hr); return; }
    DWORD row = g_cap.w * 4;
    for (UINT y = 0; y < g_cap.h; y++) memcpy(b.data + y * row, (BYTE *)m.pData + y * m.RowPitch, row);
    g_cap.ctx->Unmap(g_cap.staging, 0);

    LONG n = InterlockedIncrement(&g_cap.frames);
    // the recorder's own tick (the one it decided on), else the clock as it reads now
    if (g_cap.index) fprintf(g_cap.index, "%ld,%d\n", n - 1, g_rec.active ? g_rec.curTick : ReadInt(kRvaTick));
    InterlockedExchange(&b.full, 1);
    g_cap.nextFill = (g_cap.nextFill + 1) % kCapBufs;
    SetEvent(g_cap.bufFull);
    g_cap.copyMs += NowMs() - t0 - waited;
}

static volatile LONG g_capInFrame;                // CapStop waits for a frame in flight


// Pipe thread: finish the video. Waits for the queue to drain, closes ffmpeg's stdin and
// waits for it to write the file.
static void CapStop(char *out, size_t cap)
{
    bool wasArmed = g_cap.armed;
    g_cap.armed = false;
    for (int i = 0; i < 100 && g_capInFrame; i++) Sleep(20);      // a frame being copied finishes first
    if (g_cap.started) {
        for (int i = 0; i < 600; i++) {                           // up to 60 s for the queue
            bool busy = false; for (int k = 0; k < kCapBufs; k++) busy |= g_cap.bufs[k].full != 0;
            if (!busy || WaitForSingleObject(g_cap.writer, 0) == WAIT_OBJECT_0) break;   // drained, or the writer gave up
            Sleep(100);
        }
        // Stop marker for the writer thread: an empty buffer marked full.
        CapBuf &b = g_cap.bufs[g_cap.nextFill]; BYTE *keep = b.data; b.data = NULL; InterlockedExchange(&b.full, 1); SetEvent(g_cap.bufFull);
        WaitForSingleObject(g_cap.writer, 10000); b.data = keep;
        CloseHandle(g_cap.stdinW); g_cap.stdinW = NULL;
        DWORD code = STILL_ACTIVE;
        if (WaitForSingleObject(g_cap.proc, 300000) == WAIT_OBJECT_0) GetExitCodeProcess(g_cap.proc, &code);
        if (g_cap.index) { fclose(g_cap.index); g_cap.index = NULL; }
        WIN32_FILE_ATTRIBUTE_DATA fa = {}; GetFileAttributesExA(g_cap.out, GetFileExInfoStandard, &fa);
        double mb = ((double)fa.nFileSizeHigh * 4294967296.0 + fa.nFileSizeLow) / 1048576.0;
        _snprintf_s(out, cap, _TRUNCATE, "ok stopped: %ld frames captured, %ld written to ffmpeg, ffmpeg exit %ld, %.1f MB, %s%s",
            g_cap.frames, g_cap.written, (long)code, mb, g_cap.out,
            g_cap.err[0] ? " - ERROR earlier: " : "");
        if (g_cap.err[0]) strncat_s(out, cap, g_cap.err, _TRUNCATE);
        Log("capture: %s", out);
        CloseHandle(g_cap.proc); CloseHandle(g_cap.writer); CloseHandle(g_cap.bufFree); CloseHandle(g_cap.bufFull);
        for (int k = 0; k < kCapBufs; k++) if (g_cap.bufs[k].data) { VirtualFree(g_cap.bufs[k].data, 0, MEM_RELEASE); g_cap.bufs[k].data = NULL; }
        g_cap.proc = g_cap.writer = g_cap.bufFree = g_cap.bufFull = NULL;
        g_cap.started = false;
    }
    else _snprintf_s(out, cap, _TRUNCATE, wasArmed ? "ok stopped: no frames were captured%s%s" : "ok nothing to stop%s%s",
                     g_cap.err[0] ? " - " : "", g_cap.err);
}

// ---- the Reluduali recorder: our own stepper (0.11, 2026-10-03) ----------------------------------------
// Measured in the sandbox (0247): the game's own "fixedtime N" (cheat-protected) moves demo playback exactly N ms
// per drawn frame - the tick goes +1 on every frame at fixedtime 1, +2 at 2. So a recording is: fixedtime N, then
// every drawn frame of a new tick, taken in OUR Present hook before it is shown, into the ffmpeg pipe and
// .frames.csv, until the tick passes the end; then the film is paused and fixedtime put back. A slow encoder holds
// Present, so the replay waits for it and nothing is dropped. Start and end are ticks of the demo's own clock
// (kRvaTick).
static bool DvarProtect(const char *want, bool protect, char *out, size_t cap);
static bool DvarLookup(const char *want, char *out, size_t cap);
static bool InstallPresentHook();

// Lockstep (dev4). 0251: uncapped, the game's main thread ran 151 frames a second while 138 were drawn and taken,
// and 453 of 5000 ticks were never drawn - BO2 renders on its own thread and drops frames the renderer has no time
// for. So the main thread is held: it reads fixedtime once a frame at
// t6mpv43 0x88D017 (read from the exe, 2026-10-03):  A1 <0x2562B40> / 50 / E8 <Dvar_GetInt 0x44DFC0> / 8B E8
// (msec = fixedtime). That call goes to RecFixedTime: while recording, it waits until our Present hook has
// taken the previous frame, then returns the step - one step of the film per drawn frame, whatever the encoder's
// speed. Otherwise it is Dvar_GetInt as before.
static const DWORD kRvaFixedTimeCall = 0x48D01D;     // the E8 at 0x88D01D
static const DWORD kRvaDvarGetInt = 0x4DFC0;         // 0x44DFC0
static const DWORD kRvaFixedTimeDvar = 0x2162B40;    // 0x2562B40: the fixedtime dvar_t*
typedef int (__cdecl *DvarGetInt_t)(void *);
static DvarGetInt_t g_dvarGetInt;
static HANDLE g_recGo;                               // set by Present: the main thread may take one step
static bool g_lockPatched;
static volatile LONG g_lockWaits, g_lockTimeouts;

static void CamFrame(char site);                     // the camera override (below); every frame, main thread (site M)

static int g_camStep;                                // dev12: the fixedtime step of this frame, for CamFrame('M')
static volatile DWORD g_camLastM;                    // dev12: when M last ran (GetTickCount): P leaves the pose to M

// dev21 (2026-10-17): the lockstep's own log, one line per game frame (M: the fixedtime read, before and after its
// wait) and per captured picture (P), written beside the video as <video>.lock.csv on rec stop. Recordings after a
// keyframe jump lost ticks (sandbox 0476-0477: whole 16 ms steps doubled); this says which side breaks.
struct LockEv { char site; LONG seq; int tick, snap, v; double ms, waited; };
static const LONG kLockMax = 40000;
static LockEv *g_lockLog; static volatile LONG g_lockN, g_lockM, g_lockP;
static void LockLog(char site, int v, double waited)
{
    if (!g_lockLog) return;
    LONG i = InterlockedIncrement(&g_lockN) - 1;
    if (i >= kLockMax) return;
    LockEv &e = g_lockLog[i];
    e.site = site; e.seq = site == 'P' ? InterlockedIncrement(&g_lockP) : g_lockM; e.tick = ReadInt(kRvaTick);
    e.snap = ReadInt(0x2CFE30C); e.v = v; e.ms = NowMs(); e.waited = waited;   // 0x30FE30C: the current snapshot's time
}
static bool PresentFlag(const char *name);
// The file only with RipperBridge.locklog beside the exe (sandbox tests). The log itself always runs while recording:
// 0476-0477 lost ticks after keyframe jumps with no log; with it (0481-0487) 14 recordings were clean, the batch's own
// path included - a race in the lockstep whose timing the log changes. So it stays, and only the file is optional.
static void LockDump(const char *video)
{
    if (!g_lockLog || !video || !video[0] || !PresentFlag("RipperBridge.locklog")) return;
    char path[MAX_PATH + 16]; _snprintf_s(path, sizeof(path), _TRUNCATE, "%s.lock.csv", video);
    FILE *f = NULL; if (fopen_s(&f, path, "w") || !f) return;
    fprintf(f, "site,seq,tick,snap,step,ms,waited_ms\n");
    LONG n = g_lockN < kLockMax ? g_lockN : kLockMax;
    for (LONG i = 0; i < n; i++) { const LockEv &e = g_lockLog[i]; fprintf(f, "%c,%ld,%d,%d,%d,%.3f,%.3f\n", e.site, e.seq, e.tick, e.snap, e.v, e.ms, e.waited); }
    fclose(f);
    Log("rec: the lockstep log, %ld lines: %s", n, path);
}

// dev21: the lock probe (2026-10-17, "go, fix the lockstep"). To pair each step with its own frame, the moment the main
// thread hands a finished frame to the render thread has to be found. "lockprobe on" hooks the game's SetEvent and
// WaitForSingleObject imports and logs every call (thread, call site, handle) with the frame starts (M, the fixedtime
// read) and pictures (P); "lockprobe off <file>" writes them out. The hand-over: the main thread's one SetEvent a frame
// on an event the render thread then waits on.
static bool HookImport(const char *dll, const char *name, void *hook, void **orig);
struct ProbeEv { char kind; DWORD tid; DWORD site; DWORD h; int tick; double ms; };
static const LONG kProbeMax = 60000;
static ProbeEv *g_probe; static volatile LONG g_probeN, g_probeOn;
static volatile DWORD g_mainTid, g_renderTid;
typedef BOOL (WINAPI *SetEvent_t)(HANDLE);
typedef DWORD (WINAPI *WaitOne_t)(HANDLE, DWORD);
static SetEvent_t g_realSetEvent; static WaitOne_t g_realWaitOne;
static void ProbeAdd(char kind, HANDLE h, void *site)
{
    if (!g_probe) return;
    LONG i = InterlockedIncrement(&g_probeN) - 1;
    if (i >= kProbeMax) return;
    ProbeEv &e = g_probe[i];
    e.kind = kind; e.tid = GetCurrentThreadId(); e.site = (DWORD)(size_t)site; e.h = (DWORD)(size_t)h; e.tick = ReadInt(kRvaTick); e.ms = NowMs();
}
static BOOL WINAPI ProbeSetEvent(HANDLE h) { if (g_probeOn) ProbeAdd('S', h, _ReturnAddress()); return g_realSetEvent(h); }
static DWORD WINAPI ProbeWaitOne(HANDLE h, DWORD ms)
{
    void *site = _ReturnAddress();
    if (g_probeOn) ProbeAdd('W', h, site);
    DWORD r = g_realWaitOne(h, ms);
    if (g_probeOn) ProbeAdd('w', h, site);
    return r;
}
static void LockProbeCommand(char *arg, char *out, size_t cap)
{
    if (!_stricmp(arg, "on")) {
        if (!g_probe) g_probe = (ProbeEv *)calloc(kProbeMax, sizeof(ProbeEv));
        if (!g_realSetEvent) { void *o = NULL; if (HookImport("KERNEL32.dll", "SetEvent", (void *)ProbeSetEvent, &o) && o) g_realSetEvent = (SetEvent_t)o; }
        if (!g_realWaitOne) { void *o = NULL; if (HookImport("KERNEL32.dll", "WaitForSingleObject", (void *)ProbeWaitOne, &o) && o) g_realWaitOne = (WaitOne_t)o; }
        g_probeN = 0; InterlockedExchange(&g_probeOn, 1);
        _snprintf_s(out, cap, _TRUNCATE, "ok probing (SetEvent %s, WaitForSingleObject %s)", g_realSetEvent ? "hooked" : "NOT hooked", g_realWaitOne ? "hooked" : "NOT hooked");
        return;
    }
    if (!_strnicmp(arg, "off ", 4)) {
        InterlockedExchange(&g_probeOn, 0); Sleep(50);
        FILE *f = NULL;
        if (fopen_s(&f, arg + 4, "w") || !f) { _snprintf_s(out, cap, _TRUNCATE, "error cannot write %s", arg + 4); return; }
        fprintf(f, "kind,role,tid,site,handle,tick,ms\n");
        LONG n = g_probeN < kProbeMax ? g_probeN : kProbeMax;
        for (LONG i = 0; i < n; i++) {
            const ProbeEv &e = g_probe[i];
            const char *role = e.tid == g_mainTid ? "main" : e.tid == g_renderTid ? "render" : "other";
            fprintf(f, "%c,%s,%lu,%08lX,%08lX,%d,%.3f\n", e.kind, role, e.tid, e.site, e.h, e.tick, e.ms);
        }
        fclose(f);
        _snprintf_s(out, cap, _TRUNCATE, "ok %ld events to %s (main %lu render %lu)", n, arg + 4, g_mainTid, g_renderTid);
        return;
    }
    _snprintf_s(out, cap, _TRUNCATE, "error usage: lockprobe on | off <file>");
}

// ---- the lockstep paired by frame (dev21, 2026-10-17, Mycelium: "go, fix the lockstep") ----------------------------------
// Until now the main thread's next step was released by ANY Present (an auto-reset event set after each capture), and a
// picture was labelled with the live tick. One stray Present at the wrong moment (a frame issued just before the start,
// or a repeat) let the main thread step a frame early, and it then stayed a frame ahead for the whole take: every
// picture read a tick the main thread was already changing (0476-0477: 16 ms steps doubled, tick and clock disagreeing
// by one frame back and forth; the 10-14 1000 fps losses too). Read from t6mpv43 (0490):
//   0x710545  call 0x512620(data)  - the main thread hands a finished frame to the render thread, once a frame (the only
//                                    caller): the frame's data pointer into 0x287F7E0, then the renderer's events set.
//   0x49D190  InterlockedExchange(0x287F7E0, 0) - the render thread takes it.
// So each issued frame gets a number and the tick it was issued at (our call in place of 0x710545's). At a Present, the
// slot still holding a pointer means the newest frame is not taken yet, so this Present is the one before it; a frame
// already taken is a repeat - no picture, no release. The main thread steps on only when every frame it has issued has
// been presented, and each picture carries its own frame's tick.
static bool KfCodeIs(DWORD rva, const BYTE *want, size_t n);
static const DWORD kRvaIssueCall = 0x310545;         // 0x710545: E8 -> 0x512620
static const DWORD kRvaIssueFn = 0x112620;           // 0x512620
static const DWORD kRvaFrameSlot = 0x247F7E0;        // 0x287F7E0
static volatile LONG g_issueSeq, g_presentedSeq, g_repeats;
static int g_issueTick[64];
static bool g_issuePatched;
typedef void (__cdecl *IssueFn_t)(void *);
static IssueFn_t g_issueFn;
static void __cdecl IssueHook(void *data)
{
    LONG s = g_issueSeq + 1;
    g_issueTick[s & 63] = ReadInt(kRvaTick);
    InterlockedExchange(&g_issueSeq, s);             // published after its tick
    g_issueFn(data);
}
// whether the hand-over is where this build expects (read only; "pairable" in kf status - the batch asks it)
static bool IssueHookPossible()
{
    if (g_issuePatched) return true;
    BYTE *at = g_base + kRvaIssueCall;
    BYTE c[6];
    if (!SafeCopy(c, at - 1, sizeof(c))) return false;
    static const BYTE head[] = { 0xA1, 0x70, 0xFA, 0x87, 0x02, 0x56 };       // mov eax,[0287FA70h] / push esi
    return c[0] == 0x56 && c[1] == 0xE8 && at + 5 + *(int *)(c + 2) == g_base + kRvaIssueFn && KfCodeIs(kRvaIssueFn, head, sizeof(head));
}
static bool InstallIssueHook(char *why, size_t cap)
{
    if (g_issuePatched) return true;
    BYTE *at = g_base + kRvaIssueCall;
    if (!IssueHookPossible()) {
        _snprintf_s(why, cap, _TRUNCATE, "the frame hand-over is not where this build expects - the lockstep stays unpaired");
        return false;
    }
    BYTE *target = at + 5 + *(int *)(at + 1);
    g_issueFn = (IssueFn_t)target;
    int rel = (int)((BYTE *)&IssueHook - (at + 5));
    DWORD old;
    if (!VirtualProtect(at + 1, 4, PAGE_EXECUTE_READWRITE, &old)) { _snprintf_s(why, cap, _TRUNCATE, "protect failed"); return false; }
    InterlockedExchange((volatile LONG *)(at + 1), rel);
    VirtualProtect(at + 1, 4, old, &old);
    FlushInstructionCache(GetCurrentProcess(), at, 5);
    g_issuePatched = true;
    Log("rec: the frame hand-over at %p now goes through %p (0x512620 at %p)", at, (void *)&IssueHook, target);
    return true;
}
// Render thread, at a Present: which issued frame this is (0 = none new: a repeat, or nothing issued yet)
static LONG PresentedFrame()
{
    LONG s1, s2; DWORD slot = 0; int guard = 0;
    do { s1 = g_issueSeq; SafeCopy(&slot, g_base + kRvaFrameSlot, 4); s2 = g_issueSeq; } while (s1 != s2 && ++guard < 8);
    LONG seq = slot ? s1 - 1 : s1;                   // a frame still waiting in the slot: this Present is the one before it
    return seq > g_presentedSeq ? seq : 0;
}

static void ParStep(int step);                       // dev33: the keyed parameters (the playback engine)
static void ParRecLoaded(); static void ParRecDone();   // dev40
static bool ParLoad(const char *file, char *why, size_t cap);

static int __cdecl RecFixedTime(void *dvar)
{
    int v = g_dvarGetInt(dvar);
    g_camStep = v; g_camLastM = GetTickCount();
    g_mainTid = GetCurrentThreadId();
    if (g_probeOn) ProbeAdd('M', NULL, NULL);
    CamFrame('M');
    if (g_rec.active && v > 0 && g_recGo) {
        InterlockedIncrement(&g_lockM);
        LockLog('M', v, -1);
        double t0 = NowMs();
        InterlockedIncrement(&g_lockWaits);
        if (g_issuePatched) {
            // every frame issued so far presented (and taken) before the next step
            bool timedOut = false;
            while (g_rec.active && g_presentedSeq < g_issueSeq)
                if (WaitForSingleObject(g_recGo, 10000) == WAIT_TIMEOUT) { timedOut = true; break; }
            if (timedOut) { InterlockedIncrement(&g_lockTimeouts); InterlockedExchange(&g_presentedSeq, g_issueSeq); }   // carry on, in step again
        }
        else if (WaitForSingleObject(g_recGo, 10000) == WAIT_TIMEOUT) InterlockedIncrement(&g_lockTimeouts);   // 0255: ffmpeg's start outlasted 500 ms
        LockLog('W', v, NowMs() - t0);
    }
    ParStep(v);                                      // dev33: after the wait, before the step: the values for the next frame
    return v;
}

static bool InstallLockstep(char *why, size_t cap)
{
    if (g_lockPatched) return true;
    BYTE *at = g_base + kRvaFixedTimeCall;
    BYTE c[12];
    if (!SafeCopy(c, at - 6, sizeof(c))) { _snprintf_s(why, cap, _TRUNCATE, "the game's code is unreadable"); return false; }
    BYTE *target = at + 5 + *(int *)(c + 7);
    if (c[0] != 0xA1 || *(DWORD *)(c + 1) != (DWORD)(size_t)(g_base + kRvaFixedTimeDvar) || c[5] != 0x50 || c[6] != 0xE8 ||
        target != g_base + kRvaDvarGetInt || c[11] != 0x8B) {
        _snprintf_s(why, cap, _TRUNCATE, "the fixedtime read is not where this build expects - no lockstep");
        return false;
    }
    g_dvarGetInt = (DvarGetInt_t)target;
    if (!g_recGo) g_recGo = CreateEventA(NULL, FALSE, FALSE, NULL);
    int rel = (int)((BYTE *)&RecFixedTime - (at + 5));
    DWORD old;
    if (!VirtualProtect(at + 1, 4, PAGE_EXECUTE_READWRITE, &old)) { _snprintf_s(why, cap, _TRUNCATE, "protect failed"); return false; }
    InterlockedExchange((volatile LONG *)(at + 1), rel);
    VirtualProtect(at + 1, 4, old, &old);
    FlushInstructionCache(GetCurrentProcess(), at, 5);
    g_lockPatched = true;
    Log("rec: lockstep - the fixedtime read at %p now goes to %p (Dvar_GetInt %p)", at, (void *)&RecFixedTime, target);
    return true;
}

// ---- the camera override (dev9, 2026-10-12) -------------------------------------------
// Found from the game itself in the sandbox (mailbox 0307-0332: memory diffs and test writes through this pipe).
// The theater's FREE CAMERA (F3 until the bar says FREE CAMERA) keeps:
//   its position  at theater + 0xEBC70 (x y z, game units) - kept between frames, so a write holds;
//   its FOV       at theater + 0x8 (degrees on cg_fov's 4:3 convention; cg_fov itself is ignored in FREE CAMERA);
//   its angles    rebuilt every frame (theater + 0xEBC7C, whole 16-bit angles) from the client's accumulated view
//                 angles (pitch yaw roll, unwrapped floats; pitch positive looks down) in the theater's allocation,
//                 found per film load by "cam find" (sandbox 2026-10-12: 0x2E4FE2EC).
// Every frame the main thread's fixedtime read (the lockstep patch, installed on the first cam command) calls
// CamFrame: it writes a held pose ("cam set" - the live link from Blender) or the pose of a track at the film's
// tick ("cam path"), and/or samples the free camera into a track file ("cam rec" - a path flown in BO2).
// Track files: one line per sample, "tick x y z pitch yaw roll fov", '#' lines ignored, ticks ascending; angles
// unwrapped (the writer's job), linear between samples, the first/last sample held outside them.
static const DWORD kTheaterFov = 0x8, kTheaterFreePos = 0xEBC70, kTheaterFreeAng = 0xEBC7C;
// The camera mode (F3), one byte at theater + 0xD4C00: 0 1ST PERSON, 1 3RD PERSON, 2 FREE CAMERA - found by snapshots
// in each mode (0338) and set by writing it (0339: the bar follows, and it holds).
static const DWORD kTheaterCamMode = 0xD4C00;
struct CamRow { int tick; float v[7]; };
static struct {
    volatile LONG mode;              // 0 off, 1 hold (cam set), 2 track (cam path)
    float pose[7]; bool poseFov;
    float *ang;                      // the angle accumulator (cam find)
    BYTE *angTheater;                // the theater it was found under: another film load finds it again
    CamRow *rows; int n;
    volatile LONG frames; int lastTick;
    FILE *rec; int recLast; long recRows;
    char track[MAX_PATH], recPath[MAX_PATH];
    CRITICAL_SECTION lock; bool lockInit;
    volatile LONG lead;              // dev10: sample the track this many ms ahead of the tick read (cam lead)
    float cur[7]; volatile LONG have; // dev10: the pose written last (the guard puts it back)
} g_cam;

// ---- dev10 (2026-10-13): the 100 ms glitch --------------------------------------------
// The 0355 rating clips showed the game's own free-camera pose on exactly every 5th frame at 20 ms a frame (one frame
// per 100 ms of film, the demo's snapshot rate), whatever we wrote. CamFrame runs at two points: M, the main thread's
// fixedtime read at the very start of a frame (the film's tick not yet advanced), and P/H, just before Present (the
// frame already drawn). So on a snapshot frame something between M and the draw replaces our pose, and the next M
// or P puts it back. To find what:
//   cam diag <file> <n>   - for the next n CamFrame calls, one line each, BEFORE our write: the site, the tick, the
//                           frame count, the FOV word, the accumulator, theater + 0xEBBF0..0xEBC8C (40 floats: the
//                           free camera's tangents, position and angles) and theater + 0xD4C00..0xD4C8C (36: the view's
//                           tangents, zoom and axes), then what we wrote ("w ..."). cam diag stop closes it.
//   cam watch <t> [t] [t] [t] - up to four 4-byte write watches in the CPU's debug registers, on every thread of the
//                           game: posx posy posz (+0xEBC70..), derp dery derr (+0xEBC7C..), accp accy accr (the
//                           accumulator), fov (+0x8), ax (+0xD4C54), or +<hex> (theater-relative). Each write from
//                           outside this plugin is logged (tick, register, the address after the writing instruction,
//                           thread, value). cam watch dump <file> | off | status.
//   cam guard on|off      - while watching: the moment the game writes a watched word, put our last pose back.
//   cam lead <ms>         - sample the track at tick + ms (the step while recording: M reads the tick before it
//                           advances, so without it every frame showed the pose of the step before).
// FOUND (sandbox 0362-0364, 2026-10-13): the glitch is the FOV. On every snapshot frame the theater compares a dvar
// (its pointer at 0x0114227C, 90 in the sandbox) with the free camera's FOV word and, if they differ, stores the dvar
// there - after our write, before the view is built - so that one frame is drawn at FOV 90:
//     0x0092EB2F  8B 0D 00 3A 18 03   mov ecx, [theater]
//     0x0092EB35  D9 59 08            fstp dword [ecx+8]
// While our camera drives the FOV the store becomes DD D8 90 (fstp st(0); nop: the same pop, no store), and the
// original bytes go back on "cam off" (cam fovlock on|off|status by hand). The position and angles were never the
// problem: the game rewrites them every frame, but with our values (0362's watches).
// And the lead: M runs before the film's tick advances, so a frame was drawn with the pose of the step before
// (1.5 units behind on the 300 units/s test orbit). The lead is now automatic: the recorder's step while it records.
static const DWORD kRvaFovSyncLoad = 0x52EB2F;       // module RVA of the mov ecx, [theater] before the store
static const BYTE kFovSyncOrig[9] = { 0x8B, 0x0D, 0x00, 0x3A, 0x18, 0x03, 0xD9, 0x59, 0x08 };
static volatile LONG g_fovLocked;

static bool CamFovLock(bool on, char *why, size_t cap)
{
    BYTE *at = g_base + kRvaFovSyncLoad;
    BYTE cur[9];
    if (!SafeCopy(cur, at, 9)) { _snprintf_s(why, cap, _TRUNCATE, "the FOV store is unreadable"); return false; }
    static const BYTE patched[3] = { 0xDD, 0xD8, 0x90 };
    bool isOrig = !memcmp(cur, kFovSyncOrig, 9), isPatched = !memcmp(cur, kFovSyncOrig, 6) && !memcmp(cur + 6, patched, 3);
    if (!isOrig && !isPatched) { _snprintf_s(why, cap, _TRUNCATE, "the FOV store is not where this build expects - not patched"); return false; }
    if (on == isPatched) { g_fovLocked = on; _snprintf_s(why, cap, _TRUNCATE, on ? "locked" : "unlocked"); return true; }
    DWORD old;
    if (!VirtualProtect(at + 6, 3, PAGE_EXECUTE_READWRITE, &old)) { _snprintf_s(why, cap, _TRUNCATE, "protect failed"); return false; }
    memcpy(at + 6, on ? patched : kFovSyncOrig + 6, 3);
    VirtualProtect(at + 6, 3, old, &old);
    FlushInstructionCache(GetCurrentProcess(), at + 6, 3);
    g_fovLocked = on;
    Log("cam: FOV store at %p %s", at + 6, on ? "patched out (our FOV holds on snapshot frames)" : "restored");
    _snprintf_s(why, cap, _TRUNCATE, on ? "locked" : "unlocked");
    return true;
}

// ROLL (sandbox 0367-0369, 2026-10-13): the free camera drew roll 0 whatever the accumulator held. Two places in the
// game zero it (read from t6mpv43.exe with dumpbin, the game's own code):
//     0x00430BCE  75 19   jne  - the view setup at 0x00430BC0 stores 0 into the roll (theater + 0xEBC84) unless one
//                               of two mode checks passes (that is the game's own dolly roll) -> EB (jmp): never zeroed
//     0x00543281  88      cmp esi, 0EBC88h - the free camera's update copies pitch, yaw AND roll from the input
//                               command into theater + 0xEBC7C.. -> 84: pitch and yaw only
// With both, the roll word holds what CamFrame writes and the view is built with it (0369: 20 written, 20 drawn; the
// same word and sign as a theater marker's roll). One byte each, so each write is atomic. Restored on "cam off".
static BYTE *CamTheater();
static const DWORD kRvaRollZeroJump = 0x30BCE, kRvaAngleLoopEnd = 0x143281;
static volatile LONG g_rollLocked;

static bool CamRollLock(bool on, char *why, size_t cap)
{
    BYTE *j = g_base + kRvaRollZeroJump, *e = g_base + kRvaAngleLoopEnd;
    BYTE cj[2], ce[8];
    if (!SafeCopy(cj, j, 2) || !SafeCopy(ce, e - 2, 8)) { _snprintf_s(why, cap, _TRUNCATE, "the roll code is unreadable"); return false; }
    static const BYTE loopRest[5] = { 0xBC, 0x0E, 0x00, 0x7C, 0xCB };
    bool jOk = cj[1] == 0x19 && (cj[0] == 0x75 || cj[0] == 0xEB);
    bool eOk = ce[0] == 0x81 && ce[1] == 0xFE && (ce[2] == 0x88 || ce[2] == 0x84) && !memcmp(ce + 3, loopRest, 5);
    if (!jOk || !eOk) { _snprintf_s(why, cap, _TRUNCATE, "the roll code is not where this build expects - not patched"); return false; }
    BYTE wantJ = on ? 0xEB : 0x75, wantE = on ? 0x84 : 0x88;
    DWORD old;
    if (cj[0] != wantJ && VirtualProtect(j, 1, PAGE_EXECUTE_READWRITE, &old)) { *j = wantJ; VirtualProtect(j, 1, old, &old); FlushInstructionCache(GetCurrentProcess(), j, 1); }
    if (ce[2] != wantE && VirtualProtect(e, 1, PAGE_EXECUTE_READWRITE, &old)) { *e = wantE; VirtualProtect(e, 1, old, &old); FlushInstructionCache(GetCurrentProcess(), e, 1); }
    if (!on) { BYTE *th = CamTheater(); float z = 0; if (th) SafeCopy(th + 0xEBC84, &z, 4); }
    if (g_rollLocked != (LONG)on) Log("cam: roll %s", on ? "ours (the zeroing jump and the third angle copy patched out)" : "the game's again (both bytes restored, roll 0)");
    g_rollLocked = on;
    _snprintf_s(why, cap, _TRUNCATE, on ? "roll ours" : "roll the game's");
    return true;
}

static struct { FILE *f; volatile LONG left; } g_camDiag;
struct WatchHit { int tick; BYTE dr; DWORD eip; DWORD tid; float val; };
static const int kWatchRing = 16384;
static struct {
    DWORD addr[4]; int n; char names[4][16];
    volatile LONG hits; WatchHit ring[kWatchRing];
    volatile LONG guard; volatile LONG guardHits;
    PVOID veh; DWORD selfLo, selfHi; int threads;
} g_watch;

static BYTE *CamTheater() { return (BYTE *)(size_t)(DWORD)ReadInt(kRvaTheaterBase); }

static int AngUnits(float a) { return (int)floor(a * 65536.0 / 360.0 + 0.5) & 0xFFFF; }
static bool AngSame(float a, float b)
{
    if (!(a == a) || a > 1e7f || a < -1e7f) return false;
    int d = (AngUnits(a) - AngUnits(b)) & 0xFFFF;
    return d <= 2 || d >= 0xFFFE;
}

static void CamSample(int tick, float *p)
{
    CamRow *r = g_cam.rows; int n = g_cam.n;
    if (tick <= r[0].tick) { memcpy(p, r[0].v, 28); return; }
    if (tick >= r[n - 1].tick) { memcpy(p, r[n - 1].v, 28); return; }
    int lo = 0, hi = n - 1;
    while (hi - lo > 1) { int m = (lo + hi) / 2; if (r[m].tick <= tick) lo = m; else hi = m; }
    float t = (float)(tick - r[lo].tick) / (float)(r[hi].tick - r[lo].tick);
    for (int k = 0; k < 7; k++) p[k] = r[lo].v[k] + (r[hi].v[k] - r[lo].v[k]) * t;
}

static void CamDiagLine(char site, BYTE *th, int tick, const float *w, bool have)
{
    FILE *f = g_camDiag.f;
    if (!f || g_camDiag.left <= 0) return;
    float e[40] = {}, d[36] = {}, a[3] = {}, fv = 0;
    SafeCopy(e, th + 0xEBBF0, sizeof(e)); SafeCopy(d, th + 0xD4C00, sizeof(d)); SafeCopy(&fv, th + kTheaterFov, 4);
    if (g_cam.ang) SafeCopy(a, g_cam.ang, 12);
    fprintf(f, "%c %d %ld | fov %.7g | acc %.7g %.7g %.7g | E", site, tick, g_frames, fv, a[0], a[1], a[2]);
    for (int i = 0; i < 40; i++) fprintf(f, " %.7g", e[i]);
    fputs(" | D", f);
    for (int i = 0; i < 36; i++) fprintf(f, " %.7g", d[i]);
    if (have) fprintf(f, " | w %.7g %.7g %.7g %.7g %.7g %.7g %.7g\n", w[0], w[1], w[2], w[3], w[4], w[5], w[6]);
    else fputs(" | w -\n", f);
    if (InterlockedDecrement(&g_camDiag.left) <= 0) { fflush(f); }
}

static void CamFrame(char site)
{
    if (!g_cam.lockInit) return;
    LONG mode = g_cam.mode;
    if (!mode && !g_cam.rec && !g_camDiag.f) return;
    BYTE *th = CamTheater();
    if (!th) return;
    // P/H never make the game wait on the pipe. M (the main thread, before the view is built) does wait: 0364's E
    // had 4 frames of 341 drawn a step behind, each where M's TryEnter lost to P's call of the same moment. Every
    // holder of the lock keeps it for microseconds (a pointer swap, a file open, one diag line).
    if (site == 'M') EnterCriticalSection(&g_cam.lock);
    else if (!TryEnterCriticalSection(&g_cam.lock)) return;
    __try {
        int tick = ReadInt(kRvaTick);
        float p[7]; bool have = false, fov = true;
        if (mode == 1) { memcpy(p, g_cam.pose, 28); fov = g_cam.poseFov; have = true; }
        else if (mode == 2 && g_cam.n > 0) {
            // WHICH TIME (0362-0372, every run): at M the view block's time (theater + 0xD4C7C, an int) holds the
            // time of the view just built, V, the film tick reads V - step, and the next view is built at V + step.
            // At P the tick reads V or V - step depending on the phase of the run. So a fixed lead on the tick was
            // right in some runs and a step behind in others: H drew 42 of 340 frames with the pose before (19 near
            // repeats in the video) wherever P's write, the one that happened to be right, lost the lock to M.
            // Now: while the film steps (M runs, fixedtime > 0) only M writes, at V + step; P only writes live.
            int base = tick, vt = 0;
            if (site == 'M' && g_camStep > 0 && SafeCopy(&vt, th + 0xD4C7C, 4) && vt > tick - 2000 && vt < tick + 2000)
                base = vt + g_camStep;
            else if (site != 'M' && g_camStep > 0 && GetTickCount() - g_camLastM < 250) base = INT_MIN;   // M owns it
            if (base != INT_MIN) {
                CamSample(base + (g_cam.lead > 0 ? g_cam.lead : 0), p); have = true;
            }
        }
        if (g_camDiag.f) CamDiagLine(site, th, tick, p, have);
        if (have) {
            memcpy(g_cam.cur, p, 28); g_cam.have = fov ? 2 : 1;
            memcpy(th + kTheaterFreePos, p, 12);
            if (g_cam.ang) memcpy(g_cam.ang, p + 3, 12);
            if (g_rollLocked) memcpy(th + 0xEBC84, p + 5, 4);        // dev12: the roll word itself (see ROLL)
            if (fov && p[6] > 1.0f && p[6] < 170.0f) memcpy(th + kTheaterFov, p + 6, 4);
            InterlockedIncrement(&g_cam.frames);
        }
        if (g_cam.rec && tick != g_cam.recLast) {
            float q[7];
            memcpy(q, th + kTheaterFreePos, 12);
            memcpy(q + 3, g_cam.ang ? g_cam.ang : (float *)(th + kTheaterFreeAng), 12);
            memcpy(q + 6, th + kTheaterFov, 4);
            fprintf(g_cam.rec, "%d %.4f %.4f %.4f %.4f %.4f %.4f %.3f\n", tick, q[0], q[1], q[2], q[3], q[4], q[5], q[6]);
            g_cam.recLast = tick; g_cam.recRows++;
        }
        g_cam.lastTick = tick;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    LeaveCriticalSection(&g_cam.lock);
}

// The write watches (dev10). A data breakpoint traps AFTER the writing instruction, so the address logged is the one
// after it. Writes from this plugin (CamFrame's own) are skipped. The guard writes our last pose back at once; its own
// writes trap too, but from inside this plugin, so they are skipped the same way.
static LONG CALLBACK WatchVeh(EXCEPTION_POINTERS *e)
{
    if (e->ExceptionRecord->ExceptionCode != EXCEPTION_SINGLE_STEP || !g_watch.n) return EXCEPTION_CONTINUE_SEARCH;
    CONTEXT *c = e->ContextRecord;
    DWORD bits = c->Dr6 & 0xF;
    if (!bits) return EXCEPTION_CONTINUE_SEARCH;
    DWORD eip = (DWORD)(size_t)e->ExceptionRecord->ExceptionAddress;
    if (eip < g_watch.selfLo || eip >= g_watch.selfHi) {
        int tick = ReadInt(kRvaTick);
        for (int i = 0; i < 4; i++) {
            if (!(bits & (1u << i)) || i >= g_watch.n) continue;
            LONG k = InterlockedIncrement(&g_watch.hits) - 1;
            WatchHit &h = g_watch.ring[k % kWatchRing];
            h.tick = tick; h.dr = (BYTE)i; h.eip = eip; h.tid = GetCurrentThreadId();
            h.val = 0; SafeCopy(&h.val, (void *)(size_t)g_watch.addr[i], 4);
        }
        if (g_watch.guard && g_cam.mode == 2 && g_cam.have) {
            BYTE *th = CamTheater();
            if (th) {
                SafeCopy(th + kTheaterFreePos, g_cam.cur, 12);
                if (g_cam.ang) SafeCopy(g_cam.ang, g_cam.cur + 3, 12);
                InterlockedIncrement(&g_watch.guardHits);
            }
        }
    }
    c->Dr6 = 0;
    return EXCEPTION_CONTINUE_EXECUTION;
}

// Debug registers on every thread of the game but this one (the pipe's): suspended, set, resumed.
static int WatchApply(DWORD dr7)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return -1;
    THREADENTRY32 te; te.dwSize = sizeof(te);
    DWORD me = GetCurrentThreadId(), pid = GetCurrentProcessId(); int n = 0;
    for (BOOL ok = Thread32First(snap, &te); ok; ok = Thread32Next(snap, &te)) {
        if (te.th32OwnerProcessID != pid || te.th32ThreadID == me) continue;
        HANDLE t = OpenThread(THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_SUSPEND_RESUME, FALSE, te.th32ThreadID);
        if (!t) continue;
        if (SuspendThread(t) != (DWORD)-1) {
            CONTEXT c; memset(&c, 0, sizeof(c)); c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
            if (GetThreadContext(t, &c)) {
                c.Dr0 = g_watch.addr[0]; c.Dr1 = g_watch.addr[1]; c.Dr2 = g_watch.addr[2]; c.Dr3 = g_watch.addr[3];
                c.Dr6 = 0; c.Dr7 = dr7;
                if (SetThreadContext(t, &c)) n++;
            }
            ResumeThread(t);
        }
        CloseHandle(t);
    }
    CloseHandle(snap);
    return n;
}

static void WatchCommand(char *arg, char *out, size_t cap)
{
    BYTE *th = CamTheater();
    if (!strcmp(arg, "off")) {
        int n = WatchApply(0); g_watch.n = 0; InterlockedExchange(&g_watch.guard, 0);
        _snprintf_s(out, cap, _TRUNCATE, "ok watches off on %d threads (%ld hits)", n, g_watch.hits); return;
    }
    if (!strcmp(arg, "status") || !*arg) {
        _snprintf_s(out, cap, _TRUNCATE, "watch %d targets (%s %s %s %s) on %d threads, %ld hits, guard %s (%ld restores)", g_watch.n,
            g_watch.names[0], g_watch.names[1], g_watch.names[2], g_watch.names[3], g_watch.threads, g_watch.hits,
            g_watch.guard ? "on" : "off", g_watch.guardHits);
        return;
    }
    if (!strncmp(arg, "dump ", 5)) {
        FILE *f = NULL;
        if (fopen_s(&f, arg + 5, "w") || !f) { _snprintf_s(out, cap, _TRUNCATE, "error cannot write %s", arg + 5); return; }
        LONG total = g_watch.hits, first = total > kWatchRing ? total - kWatchRing : 0;
        fprintf(f, "# reluduali cam watch: tick register(name) address_after_the_write thread value; %ld hits, theater %08X, accumulator %08X\n",
                total, (unsigned)(size_t)th, (unsigned)(size_t)g_cam.ang);
        for (int i = 0; i < g_watch.n; i++) fprintf(f, "# DR%d %s %08X\n", i, g_watch.names[i], g_watch.addr[i]);
        for (LONG k = first; k < total; k++) {
            WatchHit &h = g_watch.ring[k % kWatchRing];
            fprintf(f, "%d %d %s %08X %lu %.7g\n", h.tick, h.dr, g_watch.names[h.dr], h.eip, h.tid, h.val);
        }
        fclose(f);
        _snprintf_s(out, cap, _TRUNCATE, "ok %ld hits to %s", total - first, arg + 5); return;
    }
    if (!th) { _snprintf_s(out, cap, _TRUNCATE, "error no film is loaded"); return; }
    if (!g_watch.veh) {
        g_watch.veh = AddVectoredExceptionHandler(1, WatchVeh);
        IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)g_self;
        IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)((BYTE *)g_self + dos->e_lfanew);
        g_watch.selfLo = (DWORD)(size_t)g_self; g_watch.selfHi = g_watch.selfLo + nt->OptionalHeader.SizeOfImage;
    }
    DWORD addr[4] = {}; char names[4][16] = {}; int n = 0;
    char buf[200]; strcpy_s(buf, arg); char *ctx = NULL;
    for (char *t = strtok_s(buf, " ", &ctx); t && n < 4; t = strtok_s(NULL, " ", &ctx)) {
        DWORD a = 0;
        if (!strcmp(t, "posx")) a = (DWORD)(size_t)(th + kTheaterFreePos);
        else if (!strcmp(t, "posy")) a = (DWORD)(size_t)(th + kTheaterFreePos + 4);
        else if (!strcmp(t, "posz")) a = (DWORD)(size_t)(th + kTheaterFreePos + 8);
        else if (!strcmp(t, "derp")) a = (DWORD)(size_t)(th + kTheaterFreeAng);
        else if (!strcmp(t, "dery")) a = (DWORD)(size_t)(th + kTheaterFreeAng + 4);
        else if (!strcmp(t, "derr")) a = (DWORD)(size_t)(th + kTheaterFreeAng + 8);
        else if (!strcmp(t, "fov")) a = (DWORD)(size_t)(th + kTheaterFov);
        else if (!strcmp(t, "ax")) a = (DWORD)(size_t)(th + 0xD4C54);
        else if (!strncmp(t, "acc", 3) && g_cam.ang) a = (DWORD)(size_t)(g_cam.ang + (t[3] == 'p' ? 0 : t[3] == 'y' ? 1 : 2));
        else if (t[0] == '+') a = (DWORD)(size_t)(th + strtoul(t + 1, NULL, 16));
        if (!a || (a & 3)) { _snprintf_s(out, cap, _TRUNCATE, "error target '%s' (posx posy posz derp dery derr accp accy accr fov ax +hex; acc needs cam find)", t); return; }
        addr[n] = a; strcpy_s(names[n], t); n++;
    }
    if (!n) { _snprintf_s(out, cap, _TRUNCATE, "error usage: cam watch <target> [..4] | dump <file> | off | status"); return; }
    WatchApply(0); g_watch.n = 0;
    memcpy(g_watch.addr, addr, sizeof(addr)); memcpy(g_watch.names, names, sizeof(names));
    InterlockedExchange(&g_watch.hits, 0); InterlockedExchange(&g_watch.guardHits, 0);
    DWORD dr7 = 0;
    for (int i = 0; i < n; i++) dr7 |= (1u << (2 * i)) | (1u << (16 + 4 * i)) | (3u << (18 + 4 * i));   // local enable, write, 4 bytes
    g_watch.n = n;
    g_watch.threads = WatchApply(dr7);
    Log("cam: watching %d words (%s %s %s %s) on %d threads", n, names[0], names[1], names[2], names[3], g_watch.threads);
    _snprintf_s(out, cap, _TRUNCATE, "ok watching %d words on %d threads", n, g_watch.threads);
}

// The angle accumulator: every float triple in the theater's allocation whose whole 16-bit angles equal the free
// camera's, then the one that turns the camera: yaw + 30 written, a few frames waited, the free camera's yaw read.
static float *g_camLastAng;                          // the accumulator found before: it has stayed at one address
                                                     // across film loads and game restarts (sandbox 2026-10-12)

// Does writing yaw + 30 at c turn the free camera? (put back either way)
static bool CamTurns(float *c, float *derived)
{
    float old = 0, now = 0, want = 0;
    if (!SafeCopy(&old, c + 1, 4) || !(old == old) || old > 1e7f || old < -1e7f) return false;
    want = old + 30.0f;
    if (!SafeCopy(c + 1, &want, 4)) return false;
    Sleep(150);
    SafeCopy(&now, derived + 1, 4);
    bool turned = AngSame(now, want);
    float back = turned ? want - 30.0f : old;
    SafeCopy(c + 1, &back, 4);
    return turned;
}

static bool CamFind(char *out, size_t cap)
{
    BYTE *th = CamTheater();
    if (!th) { _snprintf_s(out, cap, _TRUNCATE, "error no film is loaded"); return false; }
    float d[3]; float *derived = (float *)(th + kTheaterFreeAng);
    if (!SafeCopy(d, derived, 12)) { _snprintf_s(out, cap, _TRUNCATE, "error the free camera is unreadable"); return false; }
    // 1. the address found before (0343: right after a fresh film load the free camera's angles are all 0, and there
    //    is nothing to search for)
    if (g_camLastAng && CamTurns(g_camLastAng, derived)) {
        g_cam.ang = g_camLastAng; g_cam.angTheater = th;
        _snprintf_s(out, cap, _TRUNCATE, "ok angles at %08X (theater %08X; the address found before)", (unsigned)(size_t)g_camLastAng, (unsigned)(size_t)th);
        return true;
    }
    // 2. a yaw too close to 0 to search for: turn a little with the game's own command first
    if (AngUnits(d[1]) < 200 || AngUnits(d[1]) > 65336) {
        if (!g_exec) ResolveConsole();
        if (g_exec) { g_exec("+right\n", false); Sleep(150); g_exec("-right\n", false); Sleep(200); SafeCopy(d, derived, 12); }
        if (AngUnits(d[1]) < 200 || AngUnits(d[1]) > 65336) { _snprintf_s(out, cap, _TRUNCATE, "error turn the free camera a little first (yaw %.2f is too close to 0 to search for)", d[1]); return false; }
    }
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(th, &mbi, sizeof(mbi))) { _snprintf_s(out, cap, _TRUNCATE, "error VirtualQuery"); return false; }
    BYTE *alloc = (BYTE *)mbi.AllocationBase;
    float *cand[64]; int nc = 0; size_t scanned = 0;
    for (BYTE *a = alloc; nc < 64 && VirtualQuery(a, &mbi, sizeof(mbi)) && mbi.AllocationBase == alloc; a = (BYTE *)mbi.BaseAddress + mbi.RegionSize) {
        if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) || !(mbi.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE))) continue;
        float *f = (float *)mbi.BaseAddress; size_t n = mbi.RegionSize / 4;
        scanned += mbi.RegionSize;
        __try {
            for (size_t i = 0; i + 2 < n && nc < 64; i++)
                if (AngSame(f[i + 1], d[1]) && AngSame(f[i], d[0]) && AngSame(f[i + 2], d[2]) && &f[i] != derived) cand[nc++] = &f[i];
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    for (int c = 0; c < nc; c++) {
        if (CamTurns(cand[c], derived)) {
            g_cam.ang = g_camLastAng = cand[c]; g_cam.angTheater = th;
            Log("cam: angles at %p (%d candidates in %u MB of the theater's allocation %p)", cand[c], nc, (unsigned)(scanned >> 20), alloc);
            _snprintf_s(out, cap, _TRUNCATE, "ok angles at %08X (theater %08X; %d candidates in %u MB)", (unsigned)(size_t)cand[c], (unsigned)(size_t)th, nc, (unsigned)(scanned >> 20));
            return true;
        }
    }
    _snprintf_s(out, cap, _TRUNCATE, "error none of %d candidates turned the free camera - is the bar on FREE CAMERA?", nc);
    return false;
}

// FREE CAMERA on, and the angle accumulator known for the film that is loaded (found again after another load).
static bool CamReady(char *out, size_t cap)
{
    BYTE *th = CamTheater();
    if (!th) { _snprintf_s(out, cap, _TRUNCATE, "error no film is loaded"); return false; }
    BYTE mode = 0, was = 0; SafeCopy(&mode, th + kTheaterCamMode, 1); was = mode;
    if (mode != 2) {
        // F3 itself, posted to the game's window, until the mode reads FREE CAMERA: the game's own switch also starts
        // the free camera at the current view and takes its input. Writing only the byte (0343/0345) switched the
        // picture but left the free camera's angles at 0 and deaf to +right. The byte write stays as a last resort.
        HWND w = FindWindowA("CoDBlackOps", NULL);
        UINT sc = MapVirtualKeyA(VK_F3, 0);
        for (int i = 0; i < 4 && mode != 2 && w; i++) {
            PostMessageA(w, WM_KEYDOWN, VK_F3, 1 | (sc << 16));
            Sleep(40);
            PostMessageA(w, WM_KEYUP, VK_F3, 1 | (sc << 16) | (3u << 30));
            Sleep(400);
            SafeCopy(&mode, th + kTheaterCamMode, 1);
        }
        if (mode != 2) {
            BYTE two = 2; DWORD old;
            if (VirtualProtect(th + kTheaterCamMode, 1, PAGE_READWRITE, &old)) { SafeCopy(th + kTheaterCamMode, &two, 1); VirtualProtect(th + kTheaterCamMode, 1, old, &old); }
            Sleep(300);
        }
        Log("cam: FREE CAMERA on (mode was %d; %s)", was, mode == 2 ? "F3" : "written");
    }
    if (g_cam.ang && g_cam.angTheater == th) return true;
    g_cam.ang = NULL;
    return CamFind(out, cap);
}

static bool CamLoad(const char *path, char *out, size_t cap)
{
    FILE *f = NULL;
    if (fopen_s(&f, path, "r") || !f) { _snprintf_s(out, cap, _TRUNCATE, "error cannot open %s", path); return false; }
    int capRows = 4096, n = 0; CamRow *r = (CamRow *)malloc(sizeof(CamRow) * capRows); char line[512];
    while (r && fgets(line, sizeof(line), f)) {
        if (line[0] == '#' || line[0] == '\n' || line[0] == '\r') continue;
        CamRow row;
        if (sscanf_s(line, "%d %f %f %f %f %f %f %f", &row.tick, &row.v[0], &row.v[1], &row.v[2], &row.v[3], &row.v[4], &row.v[5], &row.v[6]) != 8) continue;
        if (n && row.tick <= r[n - 1].tick) continue;
        if (n == capRows) { capRows *= 2; CamRow *g = (CamRow *)realloc(r, sizeof(CamRow) * capRows); if (!g) break; r = g; }
        r[n++] = row;
    }
    fclose(f);
    if (!r || !n) { free(r); _snprintf_s(out, cap, _TRUNCATE, "error no samples in %s", path); return false; }
    EnterCriticalSection(&g_cam.lock);
    CamRow *oldRows = g_cam.rows; g_cam.rows = r; g_cam.n = n; strcpy_s(g_cam.track, path);
    InterlockedExchange(&g_cam.mode, 2);
    LeaveCriticalSection(&g_cam.lock);
    free(oldRows);
    _snprintf_s(out, cap, _TRUNCATE, "ok %d samples, ticks %d..%d", n, r[0].tick, r[n - 1].tick);
    return true;
}

static bool InstallLockstep(char *why, size_t cap);
static int g_camVia;                                 // 0 none yet, 1 our Present hook

// ---- the players' held weapons, recorded per frame (dev9, 2026-10-12) ------------------
// Found from the game in the sandbox (tools\mapimport\weapons_cli.py has the story): each player's entity record,
// 0xF8 bytes from t6mpv43 + 0x2D2D7B0 (slot = entity number), holds the held weapon at +0x48 (0 none, 255 no player
// / dead); the weapon variants are pointers from + 0x2E4CD88 (entry 0 "none"); a variant's first field is its name,
// +0x8 its weapon def, whose block +0x9C0..+0xA80 holds the world model (an XModel*, name first). "wpn rec <file>"
// writes "tick slot number name world_model" whenever a slot's weapon changes (and every slot at the start), from the
// same per-frame call as the camera.
static const DWORD kRvaPlayers = 0x2D2D7B0, kPlayerStride = 0xF8, kPlayerWeapon = 0x48, kRvaVariants = 0x2E4CD88;
static struct { FILE *f; BYTE last[18]; bool primed; long rows; char path[MAX_PATH]; } g_wpn;

static void WpnName(int v, char *name, size_t nc, char *world, size_t wc)
{
    strcpy_s(name, nc, "-"); strcpy_s(world, wc, "-");
    if (v == 0 || v == 255) return;
    BYTE *var = NULL; SafeCopy(&var, g_base + kRvaVariants + 4 * v, 4);
    if ((size_t)var < 0x10000) return;
    const char *np = NULL; SafeCopy(&np, var, 4); SafeStr(np, name, nc);
    BYTE *wd = NULL; SafeCopy(&wd, var + 8, 4);
    if ((size_t)wd < 0x10000) return;
    for (DWORD off = 0x9C0; off < 0xA80; off += 4) {
        BYTE *xm = NULL; SafeCopy(&xm, wd + off, 4);
        if ((size_t)xm < 0x10000) continue;
        const char *xn = NULL; char s[96] = "";
        if (!SafeCopy(&xn, xm, 4) || !SafeStr(xn, s, sizeof(s))) continue;
        size_t L = strlen(s);
        if (!strncmp(s, "t6_wpn", 6) && L > 6 && !strcmp(s + L - 6, "_world")) { strcpy_s(world, wc, s); return; }
    }
}

static void WpnFrame()
{
    if (!g_wpn.f) return;
    int tick = ReadInt(kRvaTick);
    for (int e = 0; e < 18; e++) {
        BYTE v = 255; SafeCopy(&v, g_base + kRvaPlayers + e * kPlayerStride + kPlayerWeapon, 1);
        if (g_wpn.primed && v == g_wpn.last[e]) continue;
        g_wpn.last[e] = v;
        char name[96], world[96]; WpnName(v, name, sizeof(name), world, sizeof(world));
        fprintf(g_wpn.f, "%d %d %d %s %s\n", tick, e, v, name, world);
        g_wpn.rows++;
    }
    g_wpn.primed = true;
}

static void WpnCommand(char *arg, char *out, size_t cap)
{
    if (!strcmp(arg, "rec stop")) {
        FILE *f = g_wpn.f; g_wpn.f = NULL; Sleep(50);
        if (f) fclose(f);
        _snprintf_s(out, cap, _TRUNCATE, "ok %ld changes in %s", g_wpn.rows, g_wpn.path);
        return;
    }
    if (!strncmp(arg, "rec ", 4)) {
        if (!g_camVia) {
            if (InstallPresentHook()) g_camVia = 1;
            else { _snprintf_s(out, cap, _TRUNCATE, "error no per-frame hook: Present is not hooked"); return; }
        }
        FILE *f = _fsopen(arg + 4, "w", _SH_DENYWR);
        if (!f) { _snprintf_s(out, cap, _TRUNCATE, "error cannot write %s", arg + 4); return; }
        fprintf(f, "# reluduali held weapons 1: tick slot number name world_model (0 none, 255 no player / dead)\n");
        g_wpn.primed = false; g_wpn.rows = 0; strcpy_s(g_wpn.path, arg + 4);
        g_wpn.f = f;
        _snprintf_s(out, cap, _TRUNCATE, "ok recording the held weapons to %s", arg + 4);
        return;
    }
    _snprintf_s(out, cap, _TRUNCATE, "wpn %s, %ld changes%s%s | usage: wpn rec <file> | wpn rec stop", g_wpn.f ? "recording" : "idle", g_wpn.rows, g_wpn.f ? " to " : "", g_wpn.f ? g_wpn.path : "");
}

static void CamCommand(char *arg, char *out, size_t cap)
{
    if (!g_cam.lockInit) { InitializeCriticalSection(&g_cam.lock); g_cam.lead = -1; g_cam.lockInit = true; }
    char why[200] = "", why2[200] = "";
    if (strcmp(arg, "status") && strcmp(arg, "get") && !g_camVia) {
        // Once a frame: our own Present hook; plus the fixedtime read, which the main thread only makes while fixedtime
        // is on (the recorder's lockstep: frame-exact for recorded cinematics). Measured 2026-10-12 (0335): with
        // fixedtime 0 that read never runs.
        if (InstallPresentHook()) g_camVia = 1;
        else { _snprintf_s(out, cap, _TRUNCATE, "error no per-frame hook: Present is not hooked"); return; }
        InstallLockstep(why2, sizeof(why2));
        Log("cam: per frame via our Present hook; lockstep read %s", why2[0] ? why2 : "patched");
    }
    BYTE *th = CamTheater();
    if (!strcmp(arg, "find")) {
        // stop driving first: a held pose rewrites the camera every frame, and the search's test turn with it
        // (0341: "cam find" right after a held pose found none)
        InterlockedExchange(&g_cam.mode, 0); Sleep(100);
        g_cam.ang = NULL; CamReady(out, cap); return;
    }
    if (!strcmp(arg, "off")) {
        InterlockedExchange(&g_cam.mode, 0); CamFovLock(false, why, sizeof(why)); CamRollLock(false, why2, sizeof(why2));
        _snprintf_s(out, cap, _TRUNCATE, "ok off (the free camera stays where it is; FOV store %s; %s)", why, why2); return;
    }
    if (!strncmp(arg, "fovlock", 7)) {
        if (!strcmp(arg + 7, " on") || !strcmp(arg + 7, " off")) CamFovLock(arg[9] == 'n', why, sizeof(why));
        _snprintf_s(out, cap, _TRUNCATE, "ok FOV store %s%s%s", g_fovLocked ? "patched out" : "as the game has it", why[0] ? " - " : "", why); return;
    }
    if (!strncmp(arg, "watch", 5)) { WatchCommand(arg[5] == ' ' ? arg + 6 : arg + 5, out, cap); return; }
    if (!strncmp(arg, "guard ", 6)) {
        InterlockedExchange(&g_watch.guard, !strcmp(arg + 6, "on") ? 1 : 0);
        _snprintf_s(out, cap, _TRUNCATE, "ok guard %s (%d words watched)", g_watch.guard ? "on" : "off", g_watch.n); return;
    }
    if (!strncmp(arg, "lead ", 5)) {
        InterlockedExchange(&g_cam.lead, !strcmp(arg + 5, "auto") ? -1 : atoi(arg + 5));
        _snprintf_s(out, cap, _TRUNCATE, "ok the track is sampled %ld ms ahead of the tick read", g_cam.lead); return;
    }
    if (!strcmp(arg, "diag stop")) {
        EnterCriticalSection(&g_cam.lock);
        FILE *f = g_camDiag.f; g_camDiag.f = NULL; g_camDiag.left = 0;
        LeaveCriticalSection(&g_cam.lock);
        if (f) fclose(f);
        _snprintf_s(out, cap, _TRUNCATE, "ok diag closed"); return;
    }
    if (!strncmp(arg, "diag ", 5)) {
        char path[MAX_PATH] = ""; int n = 0;
        if (sscanf_s(arg + 5, "%259s %d", path, (unsigned)sizeof(path), &n) != 2 || n <= 0) { _snprintf_s(out, cap, _TRUNCATE, "error usage: cam diag <file> <calls> | cam diag stop"); return; }
        FILE *f = _fsopen(path, "w", _SH_DENYWR);
        if (!f) { _snprintf_s(out, cap, _TRUNCATE, "error cannot write %s", path); return; }
        fprintf(f, "# reluduali cam diag: site(M main fixedtime read, H our Present) tick frames | fov | acc | E theater+0xEBBF0 x40 | D theater+0xD4C00 x36 | w what we wrote; theater %08X\n", (unsigned)(size_t)th);
        EnterCriticalSection(&g_cam.lock);
        if (g_camDiag.f) fclose(g_camDiag.f);
        g_camDiag.f = f; InterlockedExchange(&g_camDiag.left, n);
        LeaveCriticalSection(&g_cam.lock);
        _snprintf_s(out, cap, _TRUNCATE, "ok logging the next %d camera calls to %s", n, path); return;
    }
    if (!strcmp(arg, "get")) {
        if (!th) { _snprintf_s(out, cap, _TRUNCATE, "error no film is loaded"); return; }
        float q[7] = {};
        SafeCopy(q, th + kTheaterFreePos, 12);
        SafeCopy(q + 3, g_cam.ang ? g_cam.ang : (float *)(th + kTheaterFreeAng), 12);
        SafeCopy(q + 6, th + kTheaterFov, 4);
        _snprintf_s(out, cap, _TRUNCATE, "%d %.4f %.4f %.4f %.4f %.4f %.4f %.3f", ReadInt(kRvaTick), q[0], q[1], q[2], q[3], q[4], q[5], q[6]);
        return;
    }
    if (!strncmp(arg, "set ", 4)) {
        float p[7]; int k = sscanf_s(arg + 4, "%f %f %f %f %f %f %f", &p[0], &p[1], &p[2], &p[3], &p[4], &p[5], &p[6]);
        if (k < 6) { _snprintf_s(out, cap, _TRUNCATE, "error usage: cam set x y z pitch yaw roll [fov]"); return; }
        if (!CamReady(out, cap)) return;
        EnterCriticalSection(&g_cam.lock);
        memcpy(g_cam.pose, p, 28); g_cam.poseFov = k == 7;
        InterlockedExchange(&g_cam.mode, 1);
        LeaveCriticalSection(&g_cam.lock);
        if (k == 7) CamFovLock(true, why, sizeof(why));
        CamRollLock(true, why2, sizeof(why2));
        _snprintf_s(out, cap, _TRUNCATE, "ok");
        return;
    }
    if (!strncmp(arg, "path ", 5)) {
        if (!CamReady(out, cap)) return;
        if (CamLoad(arg + 5, out, cap)) {
            CamFovLock(true, why, sizeof(why)); CamRollLock(true, why2, sizeof(why2));
            size_t L = strlen(out); _snprintf_s(out + L, cap - L, _TRUNCATE, "; FOV store %s; %s", why, why2);
        }
        return;
    }
    if (!strncmp(arg, "mode ", 5)) {
        int m = atoi(arg + 5);
        if (!th || m < 0 || m > 2) { _snprintf_s(out, cap, _TRUNCATE, th ? "error usage: cam mode 0|1|2 (1st person, 3rd person, free camera)" : "error no film is loaded"); return; }
        BYTE b = (BYTE)m; DWORD old;
        if (!VirtualProtect(th + kTheaterCamMode, 1, PAGE_READWRITE, &old)) { _snprintf_s(out, cap, _TRUNCATE, "error protect"); return; }
        SafeCopy(th + kTheaterCamMode, &b, 1); VirtualProtect(th + kTheaterCamMode, 1, old, &old);
        _snprintf_s(out, cap, _TRUNCATE, "ok mode %d", m); return;
    }
    if (!strcmp(arg, "rec stop")) {
        EnterCriticalSection(&g_cam.lock);
        FILE *f = g_cam.rec; g_cam.rec = NULL;
        LeaveCriticalSection(&g_cam.lock);
        if (f) fclose(f);
        _snprintf_s(out, cap, _TRUNCATE, "ok %ld samples in %s", g_cam.recRows, g_cam.recPath);
        return;
    }
    if (!strncmp(arg, "rec ", 4)) {
        FILE *f = _fsopen(arg + 4, "w", _SH_DENYWR);
        if (!f) { _snprintf_s(out, cap, _TRUNCATE, "error cannot write %s", arg + 4); return; }
        fprintf(f, "# reluduali-camtrack 1: the theater's free camera, recorded per tick - tick x y z pitch yaw roll fov\n");
        EnterCriticalSection(&g_cam.lock);
        if (g_cam.rec) fclose(g_cam.rec);
        g_cam.rec = f; g_cam.recLast = INT_MIN; g_cam.recRows = 0; strcpy_s(g_cam.recPath, arg + 4);
        LeaveCriticalSection(&g_cam.lock);
        _snprintf_s(out, cap, _TRUNCATE, "ok recording the free camera to %s", arg + 4);
        return;
    }
    if (!strcmp(arg, "status") || !*arg) {
        _snprintf_s(out, cap, _TRUNCATE, "cam mode %s, angles %08X, frames written %ld, tick %d, track %d samples%s%s, recording %s (%ld), lead %s%ld, FOV store %s, roll %s",
            g_cam.mode == 1 ? "hold" : g_cam.mode == 2 ? "track" : "off", (unsigned)(size_t)g_cam.ang, g_cam.frames, g_cam.lastTick,
            g_cam.n, g_cam.n ? " " : "", g_cam.track, g_cam.rec ? g_cam.recPath : "no", g_cam.recRows,
            g_cam.lead < 0 ? "auto " : "", g_cam.lead < 0 ? (g_rec.active ? (long)g_rec.step : 0L) : g_cam.lead, g_fovLocked ? "patched out" : "the game's", g_rollLocked ? "ours" : "the game's");
        return;
    }
    _snprintf_s(out, cap, _TRUNCATE, "error usage: cam find | set x y z pitch yaw roll [fov] | path <track file> | off | mode 0|1|2 | get | rec <file> | rec stop | status | lead <ms>|auto | fovlock on|off | diag <file> <n>|stop | watch <t..>|dump <file>|off|status | guard on|off");
}

// Where the recorder's per-frame call comes from (dev7): our own Present hook. It needs a throwaway window and swap
// chain to find Present, and in Mycelium's real game Steam's overlay died on exactly that once (0.10, 2026-09-30).
static int g_recVia;                                 // 0 none, 1 our Present hook
static void RecFrameAt(IDXGISwapChain *sc, int tick);

// A Present while recording (render thread): paired, only a new issued frame is taken - with its own tick - and only it
// releases the main thread's next step; unpaired (the hand-over not found), every Present as before.
// Every Present on the recording's hook, recording or not: paired, the presented frames are counted all the time (0491:
// counted only while recording, the frames shown in the 250 ms before it went live were missed, and its first step
// waited 10 s for a Present of a frame already shown); only while recording is a picture taken.
static void RecPresent(IDXGISwapChain *sc)
{
    if (!g_issuePatched) {
        if (!g_rec.active) return;
        LockLog('P', 0, 0);
        __try { RecFrameAt(sc, ReadInt(kRvaTick)); } __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (g_recGo) SetEvent(g_recGo);
        return;
    }
    LONG seq = PresentedFrame();
    if (!seq) { if (g_rec.active) { InterlockedIncrement(&g_repeats); LockLog('R', 0, 0); } return; }   // a repeat: no picture, no release
    if (g_rec.active) {
        LockLog('P', seq, 0);
        __try { RecFrameAt(sc, g_issueTick[seq & 63]); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    InterlockedExchange(&g_presentedSeq, seq);
    if (g_recGo) SetEvent(g_recGo);
}

static void GfxPresent(IDXGISwapChain *sc);         // dev23: the gfx probe and depth (the game's frame from inside)


static void RecFrameAt(IDXGISwapChain *sc, int tick)  // tick: the presented frame's own (paired) or the live one
{
    int prev = g_rec.prevTick; g_rec.prevTick = tick;
    if (tick > g_rec.end) {
        g_rec.active = false; g_rec.done = true; g_rec.endedAt = tick;
        if (g_recGo) SetEvent(g_recGo);
        if (g_exec) { g_exec("demo_pause 1\n", false); g_exec("fixedtime 0\n", false); }
        Log("rec: past the end tick %d at %d after %d frames, %d skipped ticks", g_rec.end, tick, g_rec.n, g_rec.skips);
        return;
    }
    // 0249: the first frame after the unpause carried a tick the game then moved back (20878 -> 20874 -> 20879);
    // 0253: the first step after it came with no frame drawn. Start only once the film has been seen stepping
    // exactly one step a frame 8 frames in a row (so pause at least ~10 ms before the start tick).
    if (g_rec.n == 0) {
        g_rec.stable = (tick == prev + g_rec.step) ? g_rec.stable + 1 : 0;
        if (g_rec.stable < 8) return;
    }
    if (tick < g_rec.start || tick == g_rec.lastTick || !g_cap.armed) return;
    if (g_rec.n > 0 && tick != g_rec.lastTick + g_rec.step) {
        if (!g_rec.skips) g_rec.firstSkip = g_rec.lastTick;
        g_rec.skips++;
        Log("rec: frame %d - tick %d after %d (a step of %d, not %d)", g_rec.n, tick, g_rec.lastTick, tick - g_rec.lastTick, g_rec.step);
    }
    if (g_rec.n == 0) g_rec.firstTick = tick;
    g_rec.lastTick = tick; g_rec.curTick = tick;
    InterlockedExchange(&g_capInFrame, 1);
    __try { CapFrame(sc, g_rec.n++); }
    __except (EXCEPTION_EXECUTE_HANDLER) { CapFail("an exception while taking a frame"); }
    DepFrame(sc);                                    // dev23d: its depth, from the same frame (nothing unless asked for)
    InterlockedExchange(&g_capInFrame, 0);
}

// Pipe thread. "<ffmpeg>|<out>|<codec args>|end=<tick>[|start=<tick>][|step=<ms>][|rate=..][|prio=..]"
// The film should be paused at (or just before) the start; recording begins at once.
static void RecStart(char *arg, char *out, size_t cap)
{
    if (g_cap.armed || g_cap.started || g_rec.active) { _snprintf_s(out, cap, _TRUNCATE, "error a recording or capture is running - rec stop first"); return; }
    if (!g_exec) ResolveConsole();
    if (!g_exec) { _snprintf_s(out, cap, _TRUNCATE, "error no console (ExtendedConsole) to send fixedtime / demo_pause"); return; }
    if (g_hooked || InstallPresentHook()) g_recVia = 1;
    else { _snprintf_s(out, cap, _TRUNCATE, "error no per-frame hook: Present is not hooked"); return; }
    char *field[16] = {}; int n = 0;
    for (char *p = arg; p && n < 16; ) { field[n++] = p; p = strchr(p, '|'); if (p) *p++ = 0; }
    if (n < 3 || !*field[0] || !*field[1]) { _snprintf_s(out, cap, _TRUNCATE, "error usage: rec start <ffmpeg>|<out file>|<codec args>|end=<tick>[|start=<tick>][|step=1][|prio=idle]"); return; }
    if (strlen(field[0]) >= sizeof(g_cap.ffmpeg) || strlen(field[1]) >= sizeof(g_cap.out) - 16 || strlen(field[2]) >= sizeof(g_cap.codec)) {
        _snprintf_s(out, cap, _TRUNCATE, "error the take's path is too long for Windows (over %u characters): a shorter output folder or name", (unsigned)(sizeof(g_cap.out) - 17)); return;
    }
    if (GetFileAttributesA(field[0]) == INVALID_FILE_ATTRIBUTES) { _snprintf_s(out, cap, _TRUNCATE, "error no ffmpeg at %s", field[0]); return; }
    int start = ReadInt(kRvaTick), end = 0, step = 1; char rate[32] = ""; DWORD prio = IDLE_PRIORITY_CLASS; const char *depth = NULL; bool plate = false; int only = 0; const char *params = NULL;
    for (int i = 3; i < n; i++) {
        if (!_strnicmp(field[i], "depth=", 6)) { depth = field[i] + 6; continue; }
        if (!_strnicmp(field[i], "plate=", 6)) { plate = atoi(field[i] + 6) != 0; continue; }
        if (!_stricmp(field[i], "only=world")) { only = 7; continue; }
        if (!_strnicmp(field[i], "params=", 7)) { params = field[i] + 7; continue; }
        if (!_stricmp(field[i], "only=players")) { only = 8; continue; }
        if (!_strnicmp(field[i], "end=", 4)) end = atoi(field[i] + 4);
        else if (!_strnicmp(field[i], "start=", 6)) start = atoi(field[i] + 6);
        else if (!_strnicmp(field[i], "step=", 5)) step = atoi(field[i] + 5);
        else if (!_strnicmp(field[i], "rate=", 5)) strcpy_s(rate, field[i] + 5);
        else if (!_stricmp(field[i], "prio=below")) prio = BELOW_NORMAL_PRIORITY_CLASS;
        else if (!_stricmp(field[i], "prio=normal")) prio = NORMAL_PRIORITY_CLASS;
    }
    if (end <= start || step < 1 || step > 1000) { _snprintf_s(out, cap, _TRUNCATE, "error need end > start (%d) and step 1..1000", start); return; }
    if (!rate[0]) { if (step == 1) strcpy_s(rate, "1000"); else _snprintf_s(rate, sizeof(rate), _TRUNCATE, "1000/%d", step); }
    strcpy_s(g_cap.ffmpeg, field[0]); strcpy_s(g_cap.out, field[1]); strcpy_s(g_cap.codec, field[2]);
    strcpy_s(g_cap.pass, "rec"); strcpy_s(g_cap.rate, rate); g_cap.prio = prio;
    { char dw[200] = ""; if (!DepArm(depth, dw, sizeof(dw))) { _snprintf_s(out, cap, _TRUNCATE, "error depth: %s", dw); return; } }
    { char pw[200] = ""; if (!PlateArm(plate, pw, sizeof(pw))) { _snprintf_s(out, cap, _TRUNCATE, "error plate: %s", pw); return; } }
    RecOnly(only);
    if (params) { char pw[200] = ""; if (!ParLoad(params, pw, sizeof(pw))) { _snprintf_s(out, cap, _TRUNCATE, "error params: %s", pw); RecOnly(0); return; } ParRecLoaded(); Log("rec: params %s (%s)", params, pw); }
    if (g_cap.staging) { g_cap.staging->Release(); g_cap.staging = NULL; }
    if (g_cap.resolved) { g_cap.resolved->Release(); g_cap.resolved = NULL; }
    g_cap.w = g_cap.h = 0; g_cap.frames = g_cap.written = g_cap.waits = 0; g_cap.bytes = 0;
    g_cap.copyMs = g_cap.waitMs = 0; g_cap.err[0] = 0;
    g_rec.start = start; g_rec.end = end; g_rec.step = step; g_rec.lastTick = g_rec.prevTick = -1; g_rec.n = 0;
    g_rec.done = false; g_rec.endedAt = 0; g_rec.skips = 0; g_rec.firstSkip = -1; g_rec.curTick = 0; g_rec.stable = 0; g_rec.firstTick = -1;
    // 0249 lost one tick early on with com_maxfps 60 (the frame cap's sleep): uncapped while recording.
    char mf[120] = ""; g_rec.maxfps = -1;
    if (DvarLookup("com_maxfps", mf, sizeof(mf))) { const char *sp = strrchr(mf, ' '); if (sp) g_rec.maxfps = atoi(sp + 1); }
    char lw[160] = "lockstep on";
    if (!InstallLockstep(lw, sizeof(lw))) Log("rec: %s", lw);
    char iw[160] = "";
    bool fresh = !g_issuePatched;
    if (!InstallIssueHook(iw, sizeof(iw))) Log("rec: %s", iw);
    g_lockWaits = g_lockTimeouts = 0; g_repeats = 0;
    if (fresh && g_issuePatched) InterlockedExchange(&g_presentedSeq, g_issueSeq);   // just installed: nothing issued is owed (from now on every Present is counted)
    if (!g_lockLog) g_lockLog = (LockEv *)calloc(kLockMax, sizeof(LockEv));
    g_lockN = g_lockM = g_lockP = 0;
    if (g_recGo) SetEvent(g_recGo);                  // the first step needs no frame before it
    char r[200]; DvarProtect("fixedtime", false, r, sizeof(r));
    char c[64]; _snprintf_s(c, sizeof(c), _TRUNCATE, "fixedtime %d\n", step);
    g_exec(c, false);
    g_exec("com_maxfps 0\n", false);
    Sleep(250);                                      // fixedtime in force before the film moves
    g_cap.armed = true; g_rec.active = true;
    g_exec("demo_pause 0\n", false);
    Log("rec: ticks %d-%d, %d ms a frame, rate %s, out %s, codec %s (%s)", start, end, step, rate, g_cap.out, g_cap.codec, r);
    _snprintf_s(out, cap, _TRUNCATE, "ok recording ticks %d-%d at %d ms a frame to %s (frames via our Present hook)", start, end, step, g_cap.out);
}

static void RecStop(char *out, size_t cap)
{
    g_rec.active = false;
    if (g_recGo) SetEvent(g_recGo);
    if (g_exec) {
        g_exec("demo_pause 1\n", false); g_exec("fixedtime 0\n", false);
        if (g_rec.maxfps >= 0) { char c[64]; _snprintf_s(c, sizeof(c), _TRUNCATE, "com_maxfps %d\n", g_rec.maxfps); g_exec(c, false); }
    }
    Sleep(300);                                      // the queued "fixedtime 0" runs before the flag comes back
    char r[200]; DvarProtect("fixedtime", true, r, sizeof(r));
    LockDump(g_cap.out);                             // before CapStop, while the out path is ours
    CapStop(out, cap);
    DepStop(out, cap);                               // dev23d: appends the depth pass's own line, if there was one
    RecOnly(0);
    ParRecDone();                                    // dev40
    Log("rec: stopped (%s)", r);
}

static void RecStatus(char *out, size_t cap)
{
    _snprintf_s(out, cap, _TRUNCATE, "rec active %d done %d ticks %d-%d step %d frames %d first_tick %d last_tick %d now %d ended_at %d written %ld skips %d first_skip %d maxfps %d lockstep %d paired %d repeats %ld waits %ld timeouts %ld err %s",
        g_rec.active ? 1 : 0, g_rec.done ? 1 : 0, g_rec.start, g_rec.end, g_rec.step, g_rec.n, g_rec.firstTick, g_rec.lastTick,
        ReadInt(kRvaTick), g_rec.endedAt, g_cap.written, g_rec.skips, g_rec.firstSkip, g_rec.maxfps,
        g_lockPatched ? 1 : 0, g_issuePatched ? 1 : 0, g_repeats, g_lockWaits, g_lockTimeouts, g_cap.err[0] ? g_cap.err : "-");
    DepStatus(out, cap);
}

// The swap chain's size and window, read every 30th frame (dev14, 2026-10-14: the Console's workspace puts the game's
// window in a slot of its own; the recording size must not change with the window - "size" tells).
static volatile UINT g_scW, g_scH; static volatile HWND g_scWnd; static volatile BOOL g_scWindowed;

// No camera turn when the game takes the focus (dev15, 2026-10-14, the Console's workspace). Measured in the sandbox
// (0416-0426, to 0.01 degree): on getting the focus t6mpv43 turns by (the cursor - the point it stored the last time it
// set the cursor, 0x2B69960), 0.0442 degrees a pixel, and then puts the cursor at its window's middle. So a window
// moved while the game was not in front, or a click anywhere but the old middle, turned the camera. "steady on": the
// game's window is subclassed and, the moment it is activated, that point becomes where the cursor is - nothing to turn
// by. "steady off" (the default): the game as it was. The subclass stays once made (taking it out under someone who
// subclassed after us would cut them off); off, it only passes messages on.
static const DWORD kRvaLastCursorSet = 0x2769960;    // 0x2B69960: two ints, screen pixels
static volatile LONG g_steadyOn, g_steadyHits;
static HWND g_steadyWnd; static WNDPROC g_steadyPrev; static BOOL g_steadyUni;
static POINT g_steadyFrom, g_steadyTo;

static void SteadyNow()
{
    POINT p; int was[2];
    if (!g_steadyOn || !GetCursorPos(&p) || !SafeCopy(was, g_base + kRvaLastCursorSet, 8)) return;
    if (was[0] < -40000 || was[0] > 40000 || was[1] < -40000 || was[1] > 40000) return;      // not a point: another build
    int now[2] = { p.x, p.y };
    if (SafeCopy(g_base + kRvaLastCursorSet, now, 8)) {
        g_steadyFrom.x = was[0]; g_steadyFrom.y = was[1]; g_steadyTo = p;
        InterlockedIncrement(&g_steadyHits);
    }
}

static volatile LONG g_steadyEaten, g_steadyOwnW, g_steadyOwnH, g_steadyScaled;
static UINT g_eatUp;

// dev22 (2026-10-17): the eaten click's button, out of every mouse message. Mycelium (10-17): "when they click back in, the
// console should negate input into bo2 and just make bo2 tabbed back in without bo2 accepting a left mouse click".
// dev20 nulled the eaten click's button-up, but each mouse message also says which buttons are held (MK_* in wParam):
// the game's own SetCursorPos to its window's middle on taking the focus, and any move while the button is down, came
// with MK_LBUTTON, which the game reads as the button going down; the next message without it, as the button going up.
// So from the activation until the button is up, the eaten button's bit is cleared from every mouse message. It ends at
// the eaten button-up, at a message to the game that says the button is up already (released over another window), or
// at a new press of that button (the eaten press never reached the game). No time limit: holding the click is eaten too.
static WPARAM g_eatBit;
static volatile LONG g_eatStrip = 1, g_eatStripped, g_pumpEaten;

static bool IsMouseMsg(UINT m) { return m >= WM_MOUSEMOVE && m <= WM_MOUSEHWHEEL; }    // 0x200-0x20E: moves, buttons, wheels

static UINT DownOf(UINT up)
{
    return up == WM_LBUTTONUP ? WM_LBUTTONDOWN : up == WM_RBUTTONUP ? WM_RBUTTONDOWN : up == WM_MBUTTONUP ? WM_MBUTTONDOWN
         : up == WM_XBUTTONUP ? WM_XBUTTONDOWN : 0;
}

// one mouse message to the game while a click is eaten; "removed": it is leaving the queue (a look without PM_REMOVE
// changes the copy only). "loop": from the game's message loop, which sees each message first, so it may end the eat on
// a message that says the button is up; the window procedure gets the loop's cleared copy and must not
static void EatMouse(UINT &m, WPARAM &wp, bool removed, bool loop)
{
    if (!g_eatUp || !IsMouseMsg(m)) return;
    if (m == g_eatUp) {
        m = WM_NULL;
        if (removed) { g_eatUp = 0; if (loop) InterlockedIncrement(&g_pumpEaten); InterlockedIncrement(&g_steadyEaten); }
        return;
    }
    if (m == DownOf(g_eatUp) || m == DownOf(g_eatUp) + 2) { if (removed) g_eatUp = 0; return; }    // + 2: its double click
    if (!g_eatStrip) return;
    if (!(wp & g_eatBit)) { if (removed && loop) g_eatUp = 0; return; }
    wp &= ~g_eatBit;
    if (removed) InterlockedIncrement(&g_eatStripped);
}

// dev19: the Advanced Dolly Cam's roll and zoom, built in (Mycelium, 2026-10-15: "I want it to kind of already be
// enabled"; his tool's keys, which he chose again: scroll rolls, Shift+scroll zooms, 2.5 degrees a notch). Only in the
// theater's FREE CAMERA (Free Roam, Edit Camera, Dolly: the camera byte at theater + 0xD4C00 is 2); anywhere else the
// wheel goes to the game as before. The live roll is theater + 0xEBC84 (the game zeroes it every frame unless the roll
// lock is on: CamRollLock, dev12); the zoom is theater + 0xD4C40 (= FOV - 360), which the game copies into the
// FOV at + 0x8. A marker placed with F keeps both (words 15 and 18), and the game blends them along the path
// (docs\dolly-markers.md, 2026-10-12/13).
static volatile LONG g_dollyOn, g_dollyRolls, g_dollyZooms, g_dollyLockedRoll, g_dollyLockedFov;
static bool CamRollLock(bool on, char *why, size_t cap);

// dev20 (2026-10-16): the zoom, for real. dev19 wrote + 0xD4C40 only, and in FREE ROAM the game puts 90 back there
// every frame while the free camera's FOV word (+ 0x8) stayed 90 (sandbox 0446). + 0x8 is the one the picture uses; the
// game copies a dvar into it on snapshot frames (the store CamFovLock patches out, dev11). So the FOV is written at + 0x8
// with that store locked, and at + 0xD4C40 too, the word Edit Camera's markers keep (word 18).
static bool DollySetFov(BYTE *th, float fov)
{
    if (fov < 5.0f) fov = 5.0f;
    if (fov > 160.0f) fov = 160.0f;
    if (!g_dollyLockedFov && !g_cam.mode) {
        char why[96];
        if (!CamFovLock(true, why, sizeof(why))) return false;
        InterlockedExchange(&g_dollyLockedFov, 1);
    }
    float z = fov - 360.0f;
    SafeCopy(th + 0xD4C40, &z, 4);
    return SafeCopy(th + kTheaterFov, &fov, 4);
}

static void DollyFreeFov()
{
    if (g_dollyLockedFov && !g_cam.mode) { char why[96]; CamFovLock(false, why, sizeof(why)); }
    InterlockedExchange(&g_dollyLockedFov, 0);
}

static bool DollyWheel(int delta, bool shift)
{
    BYTE *th = CamTheater();
    BYTE camMode = 0;
    if (!th || !SafeCopy(&camMode, th + 0xD4C00, 1) || camMode != 2) return false;
    float notches = delta / 120.0f, v = 0;
    if (shift) {
        if (!SafeCopy(&v, th + kTheaterFov, 4)) return false;
        if (!DollySetFov(th, v - 2.5f * notches)) return false;     // up: in (a smaller FOV)
        InterlockedIncrement(&g_dollyZooms);
        return true;
    }
    if (!g_dollyLockedRoll && !g_cam.mode) {
        char why[96];
        if (CamRollLock(true, why, sizeof(why))) InterlockedExchange(&g_dollyLockedRoll, 1);
        else return false;
    }
    if (!SafeCopy(&v, th + 0xEBC84, 4)) return false;
    v += 2.5f * notches;
    if (v > 180.0f) v -= 360.0f;
    if (v < -180.0f) v += 360.0f;
    if (!SafeCopy(th + 0xEBC84, &v, 4)) return false;
    InterlockedIncrement(&g_dollyRolls);
    return true;
}

static LRESULT CALLBACK SteadyProc(HWND w, UINT m, WPARAM wp, LPARAM lp)
{
    // dev17: the menus. Docked smaller than its own size, the game took the pointer's place in its window as a place in
    // its own picture, so its menu cursor sat at (window / own size) of the pointer, toward the top-left (sandbox 0439/
    // 0440, docked at 0.754: the pointer at 201,151 lit BARRACKS at 152,114; Mycelium's round 1: "It's like locating it over
    // here"). Mouse messages carry client coordinates: scaled up to the game's own size here, before the game sees them.
    if (g_steadyOn && g_steadyOwnW > 0 && g_steadyOwnH > 0 &&
        ((m >= WM_MOUSEMOVE && m <= WM_MBUTTONDBLCLK) || (m >= WM_XBUTTONDOWN && m <= WM_XBUTTONDBLCLK))) {
        RECT c;
        if (GetClientRect(w, &c) && c.right > 0 && c.bottom > 0 && (c.right != g_steadyOwnW || c.bottom != g_steadyOwnH)) {
            int x = (short)LOWORD(lp), y = (short)HIWORD(lp);
            x = MulDiv(x, g_steadyOwnW, c.right);
            y = MulDiv(y, g_steadyOwnH, c.bottom);
            lp = MAKELPARAM((WORD)(short)x, (WORD)(short)y);
            InterlockedIncrement(&g_steadyScaled);
        }
    }
    // dev16: the click that brings the docked game to the front only brings it to the front. Mycelium's round 1 on his own
    // game (2026-10-15): no camera turn, but "Did toggle a view though. It did receive that input as I clicked back into
    // it" - the theater took the click as a click. Windows' own click-to-focus: activate, and eat the mouse message.
    // dev22: only a click on the picture (the client area) is eaten; one on a Classic window's title bar or edge moves or
    // sizes it as before
    if (m == WM_MOUSEACTIVATE && g_steadyOn) {
        SteadyNow();
        if (LOWORD(lp) == HTCLIENT) {
            InterlockedIncrement(&g_steadyEaten);
            // dev19: Windows eats only the button-down; BO2 acts on the button-up (sandbox 0445: the eaten click still
            // switched Free Roam to Edit Camera; the game reads its buttons from window messages only), so the up goes too
            UINT down = HIWORD(lp);
            g_eatBit = down == WM_LBUTTONDOWN ? MK_LBUTTON : down == WM_RBUTTONDOWN ? MK_RBUTTON : down == WM_MBUTTONDOWN ? MK_MBUTTON
                     : down == WM_XBUTTONDOWN ? MK_XBUTTON1 | MK_XBUTTON2 : 0;
            g_eatUp = down == WM_LBUTTONDOWN ? WM_LBUTTONUP : down == WM_RBUTTONDOWN ? WM_RBUTTONUP : down == WM_MBUTTONDOWN ? WM_MBUTTONUP
                    : down == WM_XBUTTONDOWN ? WM_XBUTTONUP : 0;
            return MA_ACTIVATEANDEAT;
        }
    }
    // the message loop (PumpFilter) has seen posted mouse messages first; this catches any that came another way
    if (g_eatUp && IsMouseMsg(m)) {
        UINT was = m;
        EatMouse(m, wp, true, false);
        if (m == WM_NULL && was != WM_NULL) return 0;
    }
    if (m == WM_MOUSEWHEEL && g_dollyOn && DollyWheel(GET_WHEEL_DELTA_WPARAM(wp), (GET_KEYSTATE_WPARAM(wp) & MK_SHIFT) != 0)) return 0;
    if ((m == WM_ACTIVATEAPP && wp) || (m == WM_ACTIVATE && LOWORD(wp) != WA_INACTIVE) || m == WM_SETFOCUS) SteadyNow();
    return g_steadyUni ? CallWindowProcW(g_steadyPrev, w, m, wp, lp) : CallWindowProcA(g_steadyPrev, w, m, wp, lp);
}

// dev18: the menus, for real. dev17's scaled mouse messages changed nothing (sandbox 0442: the same rows lit with and
// without it): the menus ask Windows where the pointer is (GetCursorPos, then ScreenToClient - t6mpv43 imports both) and
// read the answer as a place in the game's own picture. So ScreenToClient is wrapped in t6mpv43's import table: for the
// docked game's own window, its answer is scaled from the slot's size to the game's own size. Mouse-look works in screen
// pixels (GetCursorPos / SetCursorPos, the point at 0x2B69960) and does not pass through it.
typedef BOOL (WINAPI *ScreenToClientFn)(HWND, LPPOINT);
static ScreenToClientFn g_realS2C;
static volatile LONG g_s2cCalls, g_s2cScaled;
static const char *g_s2cHook = "not tried";

static BOOL WINAPI SteadyScreenToClient(HWND h, LPPOINT p)
{
    BOOL r = g_realS2C(h, p);
    InterlockedIncrement(&g_s2cCalls);
    if (r && p && g_steadyOn && h == g_steadyWnd && g_steadyOwnW > 0 && g_steadyOwnH > 0) {
        RECT c;
        if (GetClientRect(h, &c) && c.right > 0 && c.bottom > 0 && (c.right != g_steadyOwnW || c.bottom != g_steadyOwnH)) {
            p->x = MulDiv(p->x, g_steadyOwnW, c.right);
            p->y = MulDiv(p->y, g_steadyOwnH, c.bottom);
            InterlockedIncrement(&g_s2cScaled);
        }
    }
    return r;
}

// one import of t6mpv43.exe (the main module), found by name and replaced; the old pointer is kept to call on
static bool HookImport(const char *dll, const char *name, void *hook, void **orig)
{
    BYTE *base = (BYTE *)GetModuleHandleA(NULL);
    __try {
        IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
        IMAGE_NT_HEADERS32 *nt = (IMAGE_NT_HEADERS32 *)(base + dos->e_lfanew);
        IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
        if (!dir.VirtualAddress) return false;
        for (IMAGE_IMPORT_DESCRIPTOR *d = (IMAGE_IMPORT_DESCRIPTOR *)(base + dir.VirtualAddress); d->Name; d++) {
            if (_stricmp((const char *)(base + d->Name), dll)) continue;
            IMAGE_THUNK_DATA32 *names = (IMAGE_THUNK_DATA32 *)(base + (d->OriginalFirstThunk ? d->OriginalFirstThunk : d->FirstThunk));
            IMAGE_THUNK_DATA32 *iat = (IMAGE_THUNK_DATA32 *)(base + d->FirstThunk);
            for (; names->u1.AddressOfData; names++, iat++) {
                if (names->u1.Ordinal & IMAGE_ORDINAL_FLAG32) continue;
                IMAGE_IMPORT_BY_NAME *ibn = (IMAGE_IMPORT_BY_NAME *)(base + names->u1.AddressOfData);
                if (strcmp((const char *)ibn->Name, name)) continue;
                DWORD old;
                if (!VirtualProtect(&iat->u1.Function, sizeof(DWORD), PAGE_READWRITE, &old)) return false;
                *orig = (void *)(size_t)iat->u1.Function;
                iat->u1.Function = (DWORD)(size_t)hook;
                VirtualProtect(&iat->u1.Function, sizeof(DWORD), old, &old);
                return true;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return false;
}

// dev20 (2026-10-16): the click into the docked game, taken out where the game reads it. dev19 ate the click's button-up
// in the window procedure, and the theater still switched Free Roam to Edit Camera (0445, 0446): the game looks at its
// messages in its own loop (PeekMessageA / GetMessageA, both imported) before they are dispatched. So both are wrapped in
// t6mpv43's import table, and the eaten click's button-up comes back to the game as WM_NULL (dispatched, it does nothing).
typedef BOOL (WINAPI *PeekMessageAFn)(LPMSG, HWND, UINT, UINT, UINT);
typedef BOOL (WINAPI *GetMessageAFn)(LPMSG, HWND, UINT, UINT);
static PeekMessageAFn g_realPeekA;
static GetMessageAFn g_realGetA;
static const char *g_pumpHook = "not tried";

// dev22: every mouse message to the game while the click is eaten (EatMouse), not only its button-up
static void PumpFilter(LPMSG m, bool removed)
{
    if (!m || !g_steadyOn || !g_eatUp || !IsMouseMsg(m->message)) return;
    if (m->hwnd != g_steadyWnd && !IsChild(g_steadyWnd, m->hwnd)) return;
    EatMouse(m->message, m->wParam, removed, true);
}

static BOOL WINAPI SteadyPeekA(LPMSG m, HWND h, UINT lo, UINT hi, UINT flags)
{
    BOOL r = g_realPeekA(m, h, lo, hi, flags);
    if (r) PumpFilter(m, (flags & PM_REMOVE) != 0);
    return r;
}

static BOOL WINAPI SteadyGetA(LPMSG m, HWND h, UINT lo, UINT hi)
{
    BOOL r = g_realGetA(m, h, lo, hi);
    if (r > 0) PumpFilter(m, true);
    return r;
}

static BOOL CALLBACK FindGameWindow(HWND w, LPARAM lp)
{
    DWORD pid = 0; char cls[64] = "";
    GetWindowThreadProcessId(w, &pid);
    if (pid != GetCurrentProcessId() || !IsWindowVisible(w)) return TRUE;
    GetClassNameA(w, cls, sizeof(cls));
    if (strcmp(cls, "CoDBlackOps")) return TRUE;
    *(HWND *)lp = w;
    return FALSE;
}

// The game's window, subclassed once (steady and dolly share it). The previous procedure is read first, so a message
// between the two calls never finds it empty. A new window (the game remade it) is subclassed again.
static bool EnsureSubclass(char *out, size_t cap)
{
    HWND w = g_scWnd;
    if (!w || !IsWindow(w)) { w = NULL; EnumWindows(FindGameWindow, (LPARAM)&w); }
    if (!w) { _snprintf_s(out, cap, _TRUNCATE, "error no game window yet"); return false; }
    if (w == g_steadyWnd && IsWindow(g_steadyWnd)) return true;
    g_steadyUni = IsWindowUnicode(w);
    WNDPROC prev = (WNDPROC)(g_steadyUni ? GetWindowLongPtrW(w, GWLP_WNDPROC) : GetWindowLongPtrA(w, GWLP_WNDPROC));
    if (!prev) { _snprintf_s(out, cap, _TRUNCATE, "error no window procedure (%lu)", GetLastError()); return false; }
    g_steadyPrev = prev;
    WNDPROC was = (WNDPROC)(g_steadyUni ? SetWindowLongPtrW(w, GWLP_WNDPROC, (LONG_PTR)SteadyProc)
                                        : SetWindowLongPtrA(w, GWLP_WNDPROC, (LONG_PTR)SteadyProc));
    if (!was) { _snprintf_s(out, cap, _TRUNCATE, "error subclass failed (%lu)", GetLastError()); return false; }
    if (was != prev) g_steadyPrev = was;
    g_steadyWnd = w;
    Log("window %p subclassed (unicode %d)", w, g_steadyUni ? 1 : 0);
    return true;
}

// "dolly on | off | status | fov <deg> | roll <deg>": the wheel's roll and zoom (on: the Console turns it on when the
// bridge answers), and the free camera's FOV and roll set from the Console (Theater's FOV slider: cg_fov is ignored in
// FREE CAMERA).
static void DollyCommand(const char *arg, char *out, size_t cap)
{
    float v = 0;
    BYTE *th = CamTheater();
    if (!_stricmp(arg, "on")) {
        if (!EnsureSubclass(out, cap)) return;
        InterlockedExchange(&g_dollyOn, 1);
    }
    else if (!_stricmp(arg, "off")) {
        InterlockedExchange(&g_dollyOn, 0);
        if (g_dollyLockedRoll && !g_cam.mode) { char why[96]; CamRollLock(false, why, sizeof(why)); InterlockedExchange(&g_dollyLockedRoll, 0); }
        DollyFreeFov();
    }
    else if (!_stricmp(arg, "fov off")) DollyFreeFov();          // the game's own FOV again (its dvar, on the next snapshot)
    else if (!_strnicmp(arg, "fov ", 4) && sscanf_s(arg + 4, "%f", &v) == 1) {
        if (!th) { _snprintf_s(out, cap, _TRUNCATE, "error no film loaded"); return; }
        if (!DollySetFov(th, v)) { _snprintf_s(out, cap, _TRUNCATE, "error the FOV is not writable (or its store is not where this build expects)"); return; }
    }
    else if (!_stricmp(arg, "fovsource")) {
        // which dvar the game copies into the free camera's FOV (its pointer at 0x0114227C, sandbox 0362)
        DWORD dv = 0, namePtr = 0; char name[48] = { 0 }; float val = 0;
        SafeCopy(&dv, g_base + 0xD4227C, 4);
        if (dv) { SafeCopy(&namePtr, (void *)(size_t)dv, 4); if (namePtr) SafeCopy(name, (void *)(size_t)namePtr, sizeof(name) - 1); }
        for (char *c = name; *c; c++) if (*c < 32 || *c > 126) { *c = 0; break; }
        if (th) SafeCopy(&val, th + kTheaterFov, 4);
        _snprintf_s(out, cap, _TRUNCATE, "fovsource dvar %08lX name \"%s\" free_fov %.2f", dv, name, val);
        return;
    }
    else if (!_strnicmp(arg, "roll ", 5) && sscanf_s(arg + 5, "%f", &v) == 1) {
        if (!th) { _snprintf_s(out, cap, _TRUNCATE, "error no film loaded"); return; }
        if (!g_dollyLockedRoll && !g_cam.mode) { char why[96]; if (CamRollLock(true, why, sizeof(why))) InterlockedExchange(&g_dollyLockedRoll, 1); }
        if (!SafeCopy(th + 0xEBC84, &v, 4)) { _snprintf_s(out, cap, _TRUNCATE, "error the roll is not writable"); return; }
    }
    else if (_stricmp(arg, "status") && *arg) { _snprintf_s(out, cap, _TRUNCATE, "error usage: dolly on | off | status | fov <deg> | fov off | fovsource | roll <deg>"); return; }
    float roll = 0, zoom = 0, fov = 0; BYTE camMode = 255;
    if (th) { SafeCopy(&roll, th + 0xEBC84, 4); SafeCopy(&zoom, th + 0xD4C40, 4); SafeCopy(&fov, th + 0x8, 4); SafeCopy(&camMode, th + 0xD4C00, 1); }
    _snprintf_s(out, cap, _TRUNCATE, "dolly on %ld film %d camera %d roll %.2f zoom_fov %.2f fov %.2f rolls %ld zooms %ld roll_locked %ld fov_locked %ld",
        g_dollyOn, th ? 1 : 0, (int)camMode, roll, zoom + 360.0f, fov, g_dollyRolls, g_dollyZooms, g_dollyLockedRoll, g_dollyLockedFov);
}

static void SteadyCommand(const char *arg, char *out, size_t cap)
{
    // "steady on [W H]": W x H = the game's own size (its client when the Console docked it); dev17 scales the mouse to it
    int ow = 0, oh = 0;
    bool on = !_stricmp(arg, "on") || (!_strnicmp(arg, "on ", 3) && sscanf_s(arg + 3, "%d %d", &ow, &oh) == 2), off = !_stricmp(arg, "off");
    if (on && ow > 0 && oh > 0) { InterlockedExchange(&g_steadyOwnW, ow); InterlockedExchange(&g_steadyOwnH, oh); }
    if (on) {
        if (!EnsureSubclass(out, cap)) return;
        if (!g_realS2C) {
            void *orig = NULL;
            if (HookImport("USER32.dll", "ScreenToClient", (void *)SteadyScreenToClient, &orig) && orig) { g_realS2C = (ScreenToClientFn)orig; g_s2cHook = "hooked"; }
            else g_s2cHook = "import not found";
            Log("steady: ScreenToClient %s", g_s2cHook);
        }
        if (!g_realPeekA) {
            void *p = NULL, *g = NULL;
            bool okP = HookImport("USER32.dll", "PeekMessageA", (void *)SteadyPeekA, &p) && p;
            if (okP) g_realPeekA = (PeekMessageAFn)p;
            bool okG = HookImport("USER32.dll", "GetMessageA", (void *)SteadyGetA, &g) && g;
            if (okG) g_realGetA = (GetMessageAFn)g;
            g_pumpHook = okP && okG ? "hooked" : okP ? "peek only" : okG ? "get only" : "imports not found";
            Log("steady: the message loop %s", g_pumpHook);
        }
        InterlockedExchange(&g_steadyOn, 1);
    }
    else if (off) InterlockedExchange(&g_steadyOn, 0);
    else if (!_stricmp(arg, "strip on")) InterlockedExchange(&g_eatStrip, 1);      // dev22's buttons out of the mouse messages (the default)
    else if (!_stricmp(arg, "strip off")) InterlockedExchange(&g_eatStrip, 0);     // dev20 as it was: only the button-up (to compare)
    else if (_stricmp(arg, "status") && *arg) { _snprintf_s(out, cap, _TRUNCATE, "error usage: steady on [W H] | off | strip on | strip off | status"); return; }
    int at[2] = { 0, 0 }; SafeCopy(at, g_base + kRvaLastCursorSet, 8);
    _snprintf_s(out, cap, _TRUNCATE, "steady on %d window %p hits %ld last %d,%d -> %d,%d point %d,%d clicks_eaten %ld own %ldx%ld mouse_scaled %ld screentoclient %s calls %ld scaled %ld loop %s loop_eaten %ld strip %ld stripped %ld eating %d",
        g_steadyOn ? 1 : 0, g_steadyWnd, g_steadyHits, g_steadyFrom.x, g_steadyFrom.y, g_steadyTo.x, g_steadyTo.y, at[0], at[1], g_steadyEaten,
        g_steadyOwnW, g_steadyOwnH, g_steadyScaled, g_s2cHook, g_s2cCalls, g_s2cScaled, g_pumpHook, g_pumpEaten, g_eatStrip, g_eatStripped, g_eatUp ? 1 : 0);
}

static HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain *sc, UINT sync, UINT flags)
{
    if ((g_frames % 30) == 0) {
        DXGI_SWAP_CHAIN_DESC d;
        if (SUCCEEDED(sc->GetDesc(&d))) { g_scW = d.BufferDesc.Width; g_scH = d.BufferDesc.Height; g_scWnd = d.OutputWindow; g_scWindowed = d.Windowed; }
    }
    if ((g_cam.mode || g_cam.rec || g_camDiag.f) && g_camVia == 1) CamFrame('H');
    if (g_wpn.f && g_camVia == 1) __try { WpnFrame(); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    GfxPresent(sc);                                  // dev23
    if (g_recVia == 1) { g_renderTid = GetCurrentThreadId(); if (g_probeOn) ProbeAdd('P', NULL, NULL); }
    if (g_recVia == 1) RecPresent(sc);               // before it is shown: the backbuffer holds this frame
    DepPresentEnd();
    static double windowStart; static LONG windowFrames;
    LONG f = InterlockedIncrement(&g_frames);
    double now = NowMs();
    FrameInfo &slot = g_ring[f % kRing];
    slot.ms = now; slot.tick = ReadInt(kRvaTick); slot.frame = f;   // frame written last
    if (windowStart == 0) windowStart = now;
    if (now - windowStart >= 1000.0) { g_fps = (f - windowFrames) * 1000.0 / (now - windowStart); windowStart = now; windowFrames = f; }
    HRESULT hr = g_present(sc, sync, flags);
    g_lastPresent = hr; if (hr != S_OK) InterlockedIncrement(&g_notOk);
    return hr;
}

static LRESULT CALLBACK DummyProc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcA(h, m, w, l); }

// A flag file beside the exe.
static bool PresentFlag(const char *name)
{
    char flag[MAX_PATH]; GetModuleFileNameA(NULL, flag, MAX_PATH);
    char *sl = strrchr(flag, '\\'); if (sl) strcpy_s(sl + 1, MAX_PATH - (sl + 1 - flag), name);
    return GetFileAttributesA(flag) != INVALID_FILE_ATTRIBUTES;
}

static bool InstallPresentHook()
{
    if (g_hooked) return true;
    // RipperBridge.nohook beside the exe: never hook Present (kept from 2026-09-29).
    if (PresentFlag("RipperBridge.nohook")) { Log("RipperBridge.nohook exists: Present is NOT hooked"); return false; }
    WNDCLASSEXA wc = { sizeof(wc) };
    wc.lpfnWndProc = DummyProc; wc.hInstance = g_self; wc.lpszClassName = "RipperBridgeDummy";
    RegisterClassExA(&wc);
    HWND hwnd = CreateWindowA(wc.lpszClassName, "", WS_OVERLAPPED, 0, 0, 16, 16, NULL, NULL, g_self, NULL);
    if (!hwnd) { Log("dummy window failed: %lu", GetLastError()); return false; }

    DXGI_SWAP_CHAIN_DESC sd = {};
    sd.BufferCount = 1; sd.BufferDesc.Width = 16; sd.BufferDesc.Height = 16;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hwnd; sd.SampleDesc.Count = 1; sd.Windowed = TRUE; sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    IDXGISwapChain *sc = NULL; ID3D11Device *dev = NULL; ID3D11DeviceContext *ctx = NULL;
    HRESULT hr = D3D11CreateDeviceAndSwapChain(NULL, D3D_DRIVER_TYPE_HARDWARE, NULL, 0, NULL, 0, D3D11_SDK_VERSION, &sd, &sc, &dev, NULL, &ctx);
    if (FAILED(hr)) hr = D3D11CreateDeviceAndSwapChain(NULL, D3D_DRIVER_TYPE_WARP, NULL, 0, NULL, 0, D3D11_SDK_VERSION, &sd, &sc, &dev, NULL, &ctx);
    if (FAILED(hr)) { Log("dummy swapchain failed: 0x%08lX", hr); DestroyWindow(hwnd); return false; }

    void **vtable = *(void ***)sc;
    void **slot = &vtable[8];                      // IDXGISwapChain::Present
    DWORD old;
    if (VirtualProtect(slot, sizeof(void *), PAGE_EXECUTE_READWRITE, &old)) {
        g_present = (Present_t)*slot; g_slot = slot;
        *slot = (void *)&HookPresent;
        VirtualProtect(slot, sizeof(void *), old, &old);
        g_hooked = true;
    }
    Log("Present hook: vtable %p slot %p original %p hooked %d", vtable, slot, g_present, g_hooked);
    ctx->Release(); dev->Release(); sc->Release(); DestroyWindow(hwnd);
    return g_hooked;
}

// ---- keyframes: the theater's own marker snapshots, kept outside the marker list (dev21, 2026-10-17) ---------------
// Mycelium, 2026-10-17: "Invisible Markers that are just for the timeline but that have no Camera connected to them. So
// the timeline is fully draggable." Read from t6mpv43 itself at his word ("2 is what Im asking for"; dumpbin of the
// game's exe) - docs\dolly-markers.md, "The F jump":
//   0x5F8110 (client, slot)  the snapshot a placed marker keeps: the current snapshot packed into one message, put at
//                            the end of the theater's snapshot buffer (*(theater + 0xED43C), 0x320000 bytes, used size at
//                            theater + 0xEE70C), and into the marker: word 12 its offset there, 13 the demo file's
//                            position, 14 the snapshot's time (where a jump lands), 16, 17 its size, 18-23.
//                            demo_adddollycammarker calls it on slot <count>, then counts the marker in.
//   0x56A410 (client, slot)  the jump F makes (demo_switchdollycammarker): the file back to word 13, the snapshot
//                            restored from the buffer, the free camera put on the marker. Only in FREE CAMERA's Edit
//                            Camera (theater + 0xD4C00 == 2 and + 0xD4C04 == 1; it checks), with demo_enableDollyCam on.
// So a keyframe is taken on the spare slot past the list's end (the slot the add command would use next) and copied
// out here with its bytes; the slot and the buffer's used size are put back at once, so the game's list never shows
// it. A jump copies a keyframe back into the spare slot for the one call, keeps the user's camera (unless asked), and
// takes it away again. Both run on the game's main thread, from our own console command rb_kf (registered with the
// game's Cmd_AddCommand, 0x5B3070), exactly where the game runs F's command. Pipe: "kf ..." (see KfCommand).
static const DWORD kThMarkers = 0xED440, kThMarkerSize = 0x60, kThCount = 0xEE708, kThUsed = 0xEE70C, kThBuffer = 0xED43C;
static const DWORD kThCamSub = 0xD4C04;              // FREE CAMERA's own mode: 1 = Edit Camera (the dolly), 0 = Free Roam
static const int kMaxMarkers = 50, kBufferCap = 0x320000, kKfMax = 4000;
static const DWORD kRvaCmdAddCommand = 0x1B3070;     // 0x5B3070 (name, fn, storage): the game's own
static const DWORD kRvaSaveSnapshot = 0x1F8110;      // 0x5F8110 (client, slot)
static const DWORD kRvaJumpToMarker = 0x16A410;      // 0x56A410 (client, slot)
static const DWORD kRvaDemoState = 0x2CD3404;        // 0x30D3404: 2 while a film plays (the add and switch commands check it)
static const DWORD kRvaSnapTime = 0x2CFE30C;         // 0x30FE30C: the current snapshot's time (a keyframe's landing)
static const DWORD kRvaClientActive = 0xDC7D74;      // 0x11C7D74: the client; its view angles at +0x2C8C (the jump writes them)
typedef void (__cdecl *GameCmdFn)();
typedef void (__cdecl *CmdAdd_t)(const char *, GameCmdFn, void *);
typedef void (__cdecl *SlotCall_t)(int client, int slot);
struct Kf { int tick, landed, size, load; BYTE block[0x60]; BYTE *data; };
static struct {
    CRITICAL_SECTION lock; bool init;
    Kf *k; int n;                        // sorted by landed
    BYTE *theater;                       // the film load they were taken in
    bool keep;                           // "kf keep on": not cleared by another film load (the reload test)
    // Sandbox 0466: a keyframe from an EARLIER load of the same film jumped 283 ms ahead fine, but one 6 s past
    // anything this load had played took the film down (the game's error drop: the theater closed, no crash). So one
    // from another load is used only up to the furthest tick this load has reached.
    int load, reach;
    // Sandbox 0476-0477: recordings after a keyframe jump lost ticks until the film was loaded again, so the batch asks
    // how many jumps this load has had ("load_jumps" in kf status) and loads the film again first
    int loadJumps;
    volatile LONG op; int want; bool takeCam; char result[300]; HANDLE done;
    bool registered; char why[160];
    LONG saves, jumps, faults; int lastJumpFrom, lastJumpTo;
} g_kf;

static void KfInit()
{
    if (g_kf.init) return;
    InitializeCriticalSection(&g_kf.lock); g_kf.done = CreateEventA(NULL, FALSE, FALSE, NULL);
    g_kf.k = (Kf *)calloc(kKfMax, sizeof(Kf)); g_kf.init = true;
}
static void KfClearLocked() { for (int i = 0; i < g_kf.n; i++) free(g_kf.k[i].data); g_kf.n = 0; }
static void KfClear() { KfInit(); EnterCriticalSection(&g_kf.lock); KfClearLocked(); g_kf.theater = NULL; LeaveCriticalSection(&g_kf.lock); }
static void KfLoaded() { KfInit(); EnterCriticalSection(&g_kf.lock); if (!g_kf.keep) KfClearLocked(); g_kf.theater = NULL; g_kf.loadJumps = 0; LeaveCriticalSection(&g_kf.lock); }   // a loaddemo
static bool KfCodeIs(DWORD rva, const BYTE *want, size_t n) { BYTE c[16]; return n <= sizeof(c) && SafeCopy(c, g_base + rva, n) && !memcmp(c, want, n); }
static bool KfCodeOk(char *why, size_t cap)
{
    static const BYTE save[] = { 0x83, 0xEC, 0x60, 0x8B, 0x44, 0x24, 0x68 };        // sub esp,60h / mov eax,[esp+68h]
    static const BYTE jump[] = { 0xA1, 0x70, 0x09, 0x0D, 0x03, 0x50, 0xE8 };        // mov eax,[030D0970h] / push eax / call
    if (!KfCodeIs(kRvaSaveSnapshot, save, sizeof(save))) { _snprintf_s(why, cap, _TRUNCATE, "the snapshot routine is not where this build expects (0x5F8110)"); return false; }
    if (!KfCodeIs(kRvaJumpToMarker, jump, sizeof(jump))) { _snprintf_s(why, cap, _TRUNCATE, "the jump routine is not where this build expects (0x56A410)"); return false; }
    return true;
}
// the film load the keyframes belong to: another one clears them (unless "kf keep on"), and starts a new reach
static void KfNewLoad() { g_kf.load++; g_kf.reach = 0; g_kf.loadJumps = 0; }
static void KfFilm(BYTE *th) { if (g_kf.theater != th) { if (!g_kf.keep) KfClearLocked(); g_kf.theater = th; KfNewLoad(); } }
static void KfReach(int tick) { if (tick > g_kf.reach) g_kf.reach = tick; }
static int KfAtOrBefore(int tick) { int best = -1; for (int i = 0; i < g_kf.n && g_kf.k[i].landed <= tick; i++) best = i; return best; }

// main thread (rb_kf): take a keyframe at the current moment
static void KfSaveNow(char *r, size_t cap)
{
    BYTE *th = CamTheater();
    if (!th) { _snprintf_s(r, cap, _TRUNCATE, "error no film loaded"); return; }
    int state = ReadInt(kRvaDemoState);
    if (state != 2) { _snprintf_s(r, cap, _TRUNCATE, "error the film is not playing (demo state %d)", state); return; }
    EnterCriticalSection(&g_kf.lock); KfFilm(th); KfReach(ReadInt(kRvaTick)); int load = g_kf.load; LeaveCriticalSection(&g_kf.lock);
    int count = *(int *)(th + kThCount), used = *(int *)(th + kThUsed);
    BYTE *buf = *(BYTE **)(th + kThBuffer);
    if (count < 0 || count >= kMaxMarkers) { _snprintf_s(r, cap, _TRUNCATE, "error the marker list is full (%d of %d): no spare slot", count, kMaxMarkers); return; }
    if (!buf || used < 0 || used + 0x10000 > kBufferCap) { _snprintf_s(r, cap, _TRUNCATE, "error the snapshot buffer is too full (%d of %d bytes)", used, kBufferCap); return; }
    int snap = ReadInt(kRvaSnapTime);
    EnterCriticalSection(&g_kf.lock);
    bool have = false; for (int i = 0; i < g_kf.n; i++) if (g_kf.k[i].landed == snap) have = true;
    int n = g_kf.n;
    LeaveCriticalSection(&g_kf.lock);
    if (have) { _snprintf_s(r, cap, _TRUNCATE, "have %d (%d kept)", snap, n); return; }
    if (n >= kKfMax) { _snprintf_s(r, cap, _TRUNCATE, "error %d keyframes kept already (the most)", n); return; }
    BYTE *slot = th + kThMarkers + kThMarkerSize * count;
    BYTE saved[0x60]; memcpy(saved, slot, sizeof(saved));
    ((SlotCall_t)(g_base + kRvaSaveSnapshot))(0, count);
    int used2 = *(int *)(th + kThUsed), size = used2 - used;
    int off = *(int *)(slot + 0x30), w17 = *(int *)(slot + 0x44), landed = *(int *)(slot + 0x38);
    bool ok = size > 0 && size <= 0x10000 && w17 == size && off == used;
    Kf k = {};
    if (ok) {
        k.tick = ReadInt(kRvaTick); k.landed = landed; k.size = size; k.load = load;
        memcpy(k.block, slot, sizeof(k.block));
        k.data = (BYTE *)malloc(size); if (k.data) memcpy(k.data, buf + used, size); else ok = false;
    }
    *(int *)(th + kThUsed) = used; memcpy(slot, saved, sizeof(saved));      // the list and the buffer as they were
    if (!ok) { _snprintf_s(r, cap, _TRUNCATE, "error the snapshot was not where this build expects (size %d, word17 %d, offset %d of %d): not kept", size, w17, off, used); return; }
    // the rest of the slot as the add command fills it: its time, a camera (the user's, kept on a jump), speed 1
    float *f = (float *)k.block; *(int *)k.block = landed;
    SafeCopy(f + 2, th + kTheaterFreePos, 12); f[5] = f[2] + 10.0f; f[6] = f[3]; f[7] = f[4];
    *(int *)(k.block + 0x20) = 0; f[9] = 1.0f;
    EnterCriticalSection(&g_kf.lock);
    int at = g_kf.n; while (at > 0 && g_kf.k[at - 1].landed > landed) at--;
    memmove(g_kf.k + at + 1, g_kf.k + at, (g_kf.n - at) * sizeof(Kf)); g_kf.k[at] = k; g_kf.n++;
    long bytes = 0; for (int i = 0; i < g_kf.n; i++) bytes += g_kf.k[i].size;
    n = g_kf.n;
    LeaveCriticalSection(&g_kf.lock);
    InterlockedIncrement(&g_kf.saves);
    _snprintf_s(r, cap, _TRUNCATE, "ok kf at %d (tick %d, %d bytes, file %d; %d kept, %ld KB)", landed, k.tick, size, *(int *)(k.block + 0x34), n, bytes / 1024);
}

// main thread (rb_kf): jump to the last keyframe at or before <want>
static void KfJumpNow(int want, bool takeCam, char *r, size_t cap)
{
    BYTE *th = CamTheater();
    if (!th) { _snprintf_s(r, cap, _TRUNCATE, "error no film loaded"); return; }
    int state = ReadInt(kRvaDemoState);
    if (state != 2) { _snprintf_s(r, cap, _TRUNCATE, "error the film is not playing (demo state %d)", state); return; }
    // the keyframe copied out under the lock (a clear from the pipe may free it); no lock held while the game is written
    static BYTE s_data[0x10000];
    int tickNow = ReadInt(kRvaTick);
    EnterCriticalSection(&g_kf.lock);
    bool other = g_kf.theater != th && !g_kf.keep;
    if (!other) KfFilm(th);                                             // kept across a load ("kf keep on"): a new reach
    KfReach(tickNow);
    int load = g_kf.load, reach = g_kf.reach;
    int i = other ? -1 : KfAtOrBefore(want), first = g_kf.n ? g_kf.k[0].landed : -1;
    Kf k = {}; if (i >= 0) { k = g_kf.k[i]; memcpy(s_data, k.data, k.size); k.data = s_data; }
    LeaveCriticalSection(&g_kf.lock);
    if (other) { _snprintf_s(r, cap, _TRUNCATE, "error the keyframes belong to another film load (kf clear)"); return; }
    if (i < 0) { _snprintf_s(r, cap, _TRUNCATE, "error no keyframe at or before %d (%s %d)", want, first < 0 ? "none kept;" : "the first is at", first); return; }
    if (k.load != load && k.landed > reach + 200) {
        _snprintf_s(r, cap, _TRUNCATE, "error the keyframe at %d is from an earlier load, and this one has only played to %d: play there first", k.landed, reach);
        return;
    }
    BYTE *buf = *(BYTE **)(th + kThBuffer);
    int count = *(int *)(th + kThCount), used = *(int *)(th + kThUsed);
    if (count < 0 || count >= kMaxMarkers) { _snprintf_s(r, cap, _TRUNCATE, "error the marker list is full (%d of %d): no spare slot", count, kMaxMarkers); return; }
    if (!buf || used < 0 || used + k.size > kBufferCap) { _snprintf_s(r, cap, _TRUNCATE, "error the snapshot buffer is too full (%d + %d of %d bytes)", used, k.size, kBufferCap); return; }
    BYTE *slot = th + kThMarkers + kThMarkerSize * count;
    BYTE saved[0x60]; memcpy(saved, slot, sizeof(saved));
    memcpy(buf + used, k.data, k.size);
    BYTE blk[0x60]; memcpy(blk, k.block, sizeof(blk)); *(int *)(blk + 0x30) = used;
    memcpy(slot, blk, sizeof(blk));
    // the jump runs only in FREE CAMERA's Edit Camera (0x560040: theater + 0xD4C00 == 2 and + 0xD4C04 == 1; sandbox 0464-
    // 0465: in Free Roam, + 0xD4C04 == 0, every jump was refused), so both for this one call, then back
    BYTE mode = th[kTheaterCamMode]; int sub = *(int *)(th + kThCamSub); bool switched = mode != 2 || sub != 1;
    if (switched) { th[kTheaterCamMode] = 2; *(int *)(th + kThCamSub) = 1; }
    BYTE *cl = (BYTE *)(size_t)(DWORD)ReadInt(kRvaClientActive);
    float pos[3] = {}, ang[3] = {}; bool keepCam = !takeCam && cl;
    if (keepCam) { SafeCopy(pos, th + kTheaterFreePos, 12); SafeCopy(ang, cl + 0x2C8C, 12); }
    int before = ReadInt(kRvaTick);
    // logged before the call: a game error inside it drops to the menu by longjmp, past everything below (0466)
    Log("kf: jumping to %d (file %d, %s load, reach %d) from %d, slot %d, mode %d/%d", k.landed, *(int *)(blk + 0x34),
        k.load == load ? "this" : "an earlier", reach, before, count, (int)mode, sub);
    InterlockedIncrement((volatile LONG *)&g_kf.loadJumps);              // before: an error drop longjmps past what follows
    ((SlotCall_t)(g_base + kRvaJumpToMarker))(0, count);
    memcpy(slot, saved, sizeof(saved));                                 // the spare slot as it was; the buffer's used size never moved
    if (keepCam) { SafeCopy(th + kTheaterFreePos, pos, 12); SafeCopy(cl + 0x2C8C, ang, 12); }
    if (switched) { th[kTheaterCamMode] = mode; *(int *)(th + kThCamSub) = sub; }
    int snapAfter = ReadInt(kRvaSnapTime);                             // the restored snapshot's time, if it jumped
    InterlockedIncrement(&g_kf.jumps); g_kf.lastJumpFrom = before; g_kf.lastJumpTo = k.landed;
    _snprintf_s(r, cap, _TRUNCATE, "%s to the keyframe at %d for %d (tick before %d, snapshot now %d; camera %s%s)",
        snapAfter == k.landed ? "ok jumped" : "error no jump seen (the snapshot time is not the keyframe's)", k.landed, want, before, snapAfter,
        keepCam ? "kept" : "the keyframe's", switched ? "; Edit Camera for the jump only" : "");
}

// dev22 (2026-10-17), the Cam Queue's "jump to marker N": main thread (rb_kf). The film to one of his own markers, the
// game's own jump on its own slot (F on a hovered marker does the same, then opens the marker's menu; this opens none).
// The marker's snapshot is the one the game took when he placed it; a marker without one in this load's buffer (written
// from outside: word 17's size 0, or past the buffer's used end) is refused, not jumped. The camera lands on the marker,
// as F's does, unless "keep" (the camera where it is). Edit Camera for the call only, as KfJumpNow.
static void KfMarkerJumpNow(int n, bool keepCam, char *r, size_t cap)
{
    BYTE *th = CamTheater();
    if (!th) { _snprintf_s(r, cap, _TRUNCATE, "error no film loaded"); return; }
    int state = ReadInt(kRvaDemoState);
    if (state != 2) { _snprintf_s(r, cap, _TRUNCATE, "error the film is not playing (demo state %d)", state); return; }
    int count = *(int *)(th + kThCount), used = *(int *)(th + kThUsed);
    if (count <= 0 || count > kMaxMarkers) { _snprintf_s(r, cap, _TRUNCATE, "error no markers in the film (%d)", count); return; }
    if (n < 1 || n > count) { _snprintf_s(r, cap, _TRUNCATE, "error no marker %d (the film has %d)", n, count); return; }
    BYTE *slot = th + kThMarkers + kThMarkerSize * (n - 1);
    int time = *(int *)slot, off = *(int *)(slot + 0x30), landed = *(int *)(slot + 0x38), size = *(int *)(slot + 0x44);
    if (size <= 0 || size > 0x10000 || off < 0 || off + size > used || landed <= 0) {
        _snprintf_s(r, cap, _TRUNCATE, "error marker %d has no snapshot in this film load (size %d, offset %d of %d): not jumped", n, size, off, used);
        return;
    }
    EnterCriticalSection(&g_kf.lock); KfFilm(th); KfReach(ReadInt(kRvaTick)); LeaveCriticalSection(&g_kf.lock);
    BYTE mode = th[kTheaterCamMode]; int sub = *(int *)(th + kThCamSub); bool switched = mode != 2 || sub != 1;
    if (switched) { th[kTheaterCamMode] = 2; *(int *)(th + kThCamSub) = 1; }
    BYTE *cl = (BYTE *)(size_t)(DWORD)ReadInt(kRvaClientActive);
    float pos[3] = {}, ang[3] = {}; bool kept = keepCam && cl;
    if (kept) { SafeCopy(pos, th + kTheaterFreePos, 12); SafeCopy(ang, cl + 0x2C8C, 12); }
    int before = ReadInt(kRvaTick);
    Log("kf: jumping to marker %d (time %d, lands %d, file %d) from %d, mode %d/%d", n, time, landed, *(int *)(slot + 0x34), before, (int)mode, sub);
    InterlockedIncrement((volatile LONG *)&g_kf.loadJumps);              // the batch reloads a film jumped this way, as after a keyframe
    ((SlotCall_t)(g_base + kRvaJumpToMarker))(0, n - 1);
    if (kept) { SafeCopy(th + kTheaterFreePos, pos, 12); SafeCopy(cl + 0x2C8C, ang, 12); }
    if (switched) { th[kTheaterCamMode] = mode; *(int *)(th + kThCamSub) = sub; }
    int snapAfter = ReadInt(kRvaSnapTime);
    InterlockedIncrement(&g_kf.jumps); g_kf.lastJumpFrom = before; g_kf.lastJumpTo = landed;
    _snprintf_s(r, cap, _TRUNCATE, "%s to marker %d at %d (lands %d; tick before %d, snapshot now %d; camera %s%s)",
        snapAfter == landed ? "ok jumped" : "error no jump seen (the snapshot time is not the marker's)", n, time, landed, before, snapAfter,
        kept ? "kept" : "on the marker", switched ? "; Edit Camera for the jump only" : "");
}

static void __cdecl KfGameCommand()                  // rb_kf, run by the game's command buffer on its main thread
{
    EnterCriticalSection(&g_kf.lock);
    LONG op = g_kf.op; g_kf.op = 0; int want = g_kf.want; bool takeCam = g_kf.takeCam;
    LeaveCriticalSection(&g_kf.lock);
    if (!op) return;                                 // a late one: the pipe gave up waiting for it
    char r[300] = "";
    __try { if (op == 1) KfSaveNow(r, sizeof(r)); else if (op == 3) KfMarkerJumpNow(want, takeCam, r, sizeof(r)); else KfJumpNow(want, takeCam, r, sizeof(r)); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        InterlockedIncrement(&g_kf.faults);
        _snprintf_s(r, sizeof(r), _TRUNCATE, "error the game faulted inside the %s (caught; the film may need loading again)", op == 1 ? "save" : op == 3 ? "marker jump" : "jump");
    }
    Log("kf: %s", r);
    EnterCriticalSection(&g_kf.lock); strcpy_s(g_kf.result, sizeof(g_kf.result), r); LeaveCriticalSection(&g_kf.lock);
    SetEvent(g_kf.done);
}

static bool KfRegister(char *why, size_t cap)
{
    if (g_kf.registered) return true;
    static const BYTE add[] = { 0x56, 0x8B, 0x74, 0x24, 0x08, 0x56, 0xE8 };     // push esi / mov esi,[esp+8] / push esi / call
    if (!KfCodeIs(kRvaCmdAddCommand, add, sizeof(add))) { _snprintf_s(why, cap, _TRUNCATE, "the game's Cmd_AddCommand is not where this build expects (0x5B3070)"); return false; }
    static BYTE storage[0x40];                       // its cmd_function_t (next, name, .., .., function): 0x14 bytes used
    ((CmdAdd_t)(g_base + kRvaCmdAddCommand))("rb_kf", KfGameCommand, storage);
    g_kf.registered = true; Log("kf: rb_kf registered (storage %p)", storage);
    return true;
}

// pipe thread: hand one save or jump to the main thread and wait for it
static void KfRun(LONG op, int want, bool takeCam, char *out, size_t cap)
{
    char why[160] = "";
    if (!KfCodeOk(why, sizeof(why)) || !KfRegister(why, sizeof(why))) { _snprintf_s(out, cap, _TRUNCATE, "error %s", why); return; }
    if (!g_exec) ResolveConsole();
    if (!g_exec) { _snprintf_s(out, cap, _TRUNCATE, "error no Cmd_ExecuteCommand"); return; }
    EnterCriticalSection(&g_kf.lock);
    if (g_kf.op) { LeaveCriticalSection(&g_kf.lock); _snprintf_s(out, cap, _TRUNCATE, "error a keyframe save or jump is already waiting"); return; }
    g_kf.op = op; g_kf.want = want; g_kf.takeCam = takeCam; g_kf.result[0] = 0; ResetEvent(g_kf.done);
    LeaveCriticalSection(&g_kf.lock);
    g_exec("rb_kf\n", false);
    if (WaitForSingleObject(g_kf.done, 3000) != WAIT_OBJECT_0) {
        EnterCriticalSection(&g_kf.lock); bool taken = g_kf.op == 0; g_kf.op = 0; LeaveCriticalSection(&g_kf.lock);
        if (!taken || WaitForSingleObject(g_kf.done, 5000) != WAIT_OBJECT_0) {
            _snprintf_s(out, cap, _TRUNCATE, "error the game did not run rb_kf within %s", taken ? "8 s (it started and did not finish)" : "3 s (in a menu or loading?)");
            return;
        }
    }
    EnterCriticalSection(&g_kf.lock); strcpy_s(out, cap, g_kf.result); LeaveCriticalSection(&g_kf.lock);
}

// "kf file save|load <path>": RBKF1, the count, then per keyframe: tick landed size, the 0x60-byte slot, the bytes
static void KfFile(bool save, const char *path, char *out, size_t cap)
{
    FILE *f = NULL;
    if (fopen_s(&f, path, save ? "wb" : "rb") || !f) { _snprintf_s(out, cap, _TRUNCATE, "error cannot open %s", path); return; }
    EnterCriticalSection(&g_kf.lock);
    int n = 0; bool ok = true;
    if (save) {
        n = g_kf.n; ok = fwrite("RBKF1", 1, 6, f) == 6 && fwrite(&n, 4, 1, f) == 1;
        for (int i = 0; ok && i < n; i++) { const Kf &k = g_kf.k[i];
            ok = fwrite(&k.tick, 4, 1, f) == 1 && fwrite(&k.landed, 4, 1, f) == 1 && fwrite(&k.size, 4, 1, f) == 1 &&
                 fwrite(k.block, 1, 0x60, f) == 0x60 && fwrite(k.data, 1, k.size, f) == (size_t)k.size; }
    } else {
        char magic[6] = ""; ok = fread(magic, 1, 6, f) == 6 && !memcmp(magic, "RBKF1", 6) && fread(&n, 4, 1, f) == 1 && n >= 0 && n <= kKfMax;
        if (ok) KfClearLocked();
        for (int i = 0; ok && i < n; i++) { Kf k = {};
            ok = fread(&k.tick, 4, 1, f) == 1 && fread(&k.landed, 4, 1, f) == 1 && fread(&k.size, 4, 1, f) == 1 && k.size > 0 && k.size <= 0x10000 &&
                 fread(k.block, 1, 0x60, f) == 0x60 && (k.data = (BYTE *)malloc(k.size)) != NULL && fread(k.data, 1, k.size, f) == (size_t)k.size;
            if (ok) g_kf.k[g_kf.n++] = k; else free(k.data); }
        if (ok) { g_kf.theater = CamTheater(); for (int i = 0; i < g_kf.n; i++) g_kf.k[i].load = -1; }   // this film, an earlier load: the reach guard applies
    }
    int kept = g_kf.n;
    LeaveCriticalSection(&g_kf.lock);
    fclose(f);
    _snprintf_s(out, cap, _TRUNCATE, ok ? "ok %s %d keyframes (%d kept)" : "error %s stopped part way (%d in the file; %d kept)", save ? "saved" : "loaded", n, kept);
}

static void KfCommand(char *arg, char *out, size_t cap)
{
    KfInit();
    int t = 0;
    if (!_stricmp(arg, "save")) KfRun(1, 0, false, out, cap);
    else if (!_strnicmp(arg, "jump ", 5) && sscanf_s(arg + 5, "%d", &t) == 1) KfRun(2, t, strstr(arg + 5, " cam") != NULL, out, cap);
    else if (!_strnicmp(arg, "marker ", 7) && sscanf_s(arg + 7, "%d", &t) == 1) KfRun(3, t, strstr(arg + 7, " keep") != NULL, out, cap);   // dev22: his marker N (1 = the first)
    else if (!_stricmp(arg, "clear")) { KfClear(); _snprintf_s(out, cap, _TRUNCATE, "ok cleared"); }
    else if (!_stricmp(arg, "keep on") || !_stricmp(arg, "keep off")) { g_kf.keep = arg[6] == 'n'; _snprintf_s(out, cap, _TRUNCATE, "ok keep %s", g_kf.keep ? "on" : "off"); }
    else if (!_strnicmp(arg, "file save ", 10)) KfFile(true, arg + 10, out, cap);
    else if (!_strnicmp(arg, "file load ", 10)) KfFile(false, arg + 10, out, cap);
    else if (!_stricmp(arg, "list")) {
        EnterCriticalSection(&g_kf.lock);
        int c = _snprintf_s(out, cap, _TRUNCATE, "kf %d:", g_kf.n);
        for (int i = 0; i < g_kf.n && c > 0 && (size_t)c + 12 < cap; i++) c += _snprintf_s(out + c, cap - c, _TRUNCATE, " %d", g_kf.k[i].landed);
        LeaveCriticalSection(&g_kf.lock);
    }
    else if (!_stricmp(arg, "status") || !*arg) {
        BYTE *th = CamTheater(); int count = -1, used = -1, sub = -1; BYTE mode = 255;
        if (th) { SafeCopy(&count, th + kThCount, 4); SafeCopy(&used, th + kThUsed, 4); SafeCopy(&mode, th + kTheaterCamMode, 1); SafeCopy(&sub, th + kThCamSub, 4); }
        EnterCriticalSection(&g_kf.lock);
        long bytes = 0; for (int i = 0; i < g_kf.n; i++) bytes += g_kf.k[i].size;
        _snprintf_s(out, cap, _TRUNCATE, "kf %d kept (%ld KB) first %d last %d film %s keep %d load %d reach %d load_jumps %d pairable %d registered %d saves %ld jumps %ld faults %ld last_jump %d->%d markers %d buffer %d/%d camera %d/%d demo_state %d",
            g_kf.n, bytes / 1024, g_kf.n ? g_kf.k[0].landed : -1, g_kf.n ? g_kf.k[g_kf.n - 1].landed : -1,
            !g_kf.theater ? "none" : g_kf.theater == th ? "this" : "another", g_kf.keep ? 1 : 0, g_kf.load, g_kf.reach, g_kf.loadJumps, IssueHookPossible() ? 1 : 0, g_kf.registered ? 1 : 0,
            g_kf.saves, g_kf.jumps, g_kf.faults, g_kf.lastJumpFrom, g_kf.lastJumpTo, count, used, kBufferCap, (int)mode, sub, ReadInt(kRvaDemoState));
        LeaveCriticalSection(&g_kf.lock);
    }
    else _snprintf_s(out, cap, _TRUNCATE, "error usage: kf save | jump <tick> [cam] | marker <N> [keep] | list | status | clear | keep on|off | file save|load <path>");
}

// ---- display modes for the sandbox (0.10.4, 2026-10-01) ------------------------------
// Mycelium: "Its just really strange that Bo2 Only exposes the current sandbox resolution in its
// settings ... If its 1600x900, we write the resolutions from that as the Max, downwards."
// BO2 asks the display driver for its modes two ways (t6mpv43.exe imports EnumDisplaySettingsA
// and CreateDXGIFactory1). In Windows Sandbox the only display is the Remote Display Adapter,
// which reports ONE mode, the sandbox window's size (measured: tools\sandbox\modes.ps1). With
// RipperBridge.modes beside the exe (run.ps1 writes it; Mycelium's real game never has one) both
// answers become a list: Mycelium's own PC's sizes that fit inside the current size, plus that size.
// Lines "WxH" in the file add sizes. For WINDOWED play: a fullscreen switch would ask the
// sandbox's display for a mode it cannot do.
typedef BOOL (WINAPI *EnumDisplaySettingsA_t)(LPCSTR, DWORD, DEVMODEA *);
typedef HRESULT (STDMETHODCALLTYPE *GetDisplayModeList_t)(IDXGIOutput *, DXGI_FORMAT, UINT, UINT *, DXGI_MODE_DESC *);
typedef HRESULT (STDMETHODCALLTYPE *FindClosest_t)(IDXGIOutput *, const DXGI_MODE_DESC *, DXGI_MODE_DESC *, IUnknown *);
static EnumDisplaySettingsA_t g_enumA;
static GetDisplayModeList_t g_gdml;
static FindClosest_t g_findClosest;
static struct { int w, h; } g_modes[96];
static int g_modeCount;
static volatile LONG g_modeAsksGdi, g_modeAsksDxgi;
static char g_modeHow[200];

static void AddMode(int w, int h)
{
    if (w < 640 || h < 480 || g_modeCount >= 96) return;
    for (int i = 0; i < g_modeCount; i++) if (g_modes[i].w == w && g_modes[i].h == h) return;
    int i = g_modeCount++;
    while (i > 0 && (g_modes[i - 1].w > w || (g_modes[i - 1].w == w && g_modes[i - 1].h > h))) { g_modes[i] = g_modes[i - 1]; i--; }
    g_modes[i].w = w; g_modes[i].h = h;
}

static bool HasMode(UINT w, UINT h)
{
    for (int i = 0; i < g_modeCount; i++) if ((UINT)g_modes[i].w == w && (UINT)g_modes[i].h == h) return true;
    return false;
}

static BOOL WINAPI HookEnumDisplaySettingsA(LPCSTR dev, DWORD i, DEVMODEA *dm)
{
    if (i == ENUM_CURRENT_SETTINGS || i == ENUM_REGISTRY_SETTINGS || !g_modeCount) return g_enumA(dev, i, dm);
    InterlockedIncrement(&g_modeAsksGdi);
    if (i >= (DWORD)g_modeCount) { SetLastError(ERROR_NO_MORE_FILES); return FALSE; }
    if (!g_enumA(dev, ENUM_CURRENT_SETTINGS, dm)) return FALSE;    // the real mode: depth, Hz, flags
    dm->dmPelsWidth = g_modes[i].w; dm->dmPelsHeight = g_modes[i].h; dm->dmBitsPerPel = 32;
    dm->dmFields |= DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL;
    return TRUE;
}

static HRESULT STDMETHODCALLTYPE HookGetDisplayModeList(IDXGIOutput *o, DXGI_FORMAT fmt, UINT flags, UINT *num, DXGI_MODE_DESC *desc)
{
    if (!g_modeCount || !num) return g_gdml(o, fmt, flags, num, desc);
    InterlockedIncrement(&g_modeAsksDxgi);
    UINT n = 0; HRESULT hr = g_gdml(o, fmt, flags, &n, NULL);
    if (FAILED(hr) || n == 0) return hr;                          // nothing real to copy from
    DXGI_MODE_DESC real[16]; UINT m = n < 16 ? n : 16;
    hr = g_gdml(o, fmt, flags, &m, real);
    if (FAILED(hr) && hr != DXGI_ERROR_MORE_DATA) return hr;
    if (!desc) { *num = g_modeCount; return S_OK; }
    UINT k = *num < (UINT)g_modeCount ? *num : (UINT)g_modeCount;
    for (UINT i = 0; i < k; i++) { desc[i] = real[0]; desc[i].Width = g_modes[i].w; desc[i].Height = g_modes[i].h; }
    *num = k;
    return k < (UINT)g_modeCount ? DXGI_ERROR_MORE_DATA : S_OK;
}

static HRESULT STDMETHODCALLTYPE HookFindClosest(IDXGIOutput *o, const DXGI_MODE_DESC *want, DXGI_MODE_DESC *closest, IUnknown *dev)
{
    HRESULT hr = g_findClosest(o, want, closest, dev);
    if (SUCCEEDED(hr) && g_modeCount && want && closest && HasMode(want->Width, want->Height)) {
        closest->Width = want->Width; closest->Height = want->Height;
    }
    return hr;
}

static bool PatchIat(BYTE *mod, const char *dll, const char *fn, void *hook, void **orig)
{
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(mod + ((IMAGE_DOS_HEADER *)mod)->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    for (IMAGE_IMPORT_DESCRIPTOR *d = (IMAGE_IMPORT_DESCRIPTOR *)(mod + dir.VirtualAddress); d->Name; d++) {
        if (_stricmp((char *)(mod + d->Name), dll)) continue;
        IMAGE_THUNK_DATA *names = (IMAGE_THUNK_DATA *)(mod + (d->OriginalFirstThunk ? d->OriginalFirstThunk : d->FirstThunk));
        IMAGE_THUNK_DATA *iat = (IMAGE_THUNK_DATA *)(mod + d->FirstThunk);
        for (; names->u1.AddressOfData; names++, iat++) {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            if (strcmp((char *)((IMAGE_IMPORT_BY_NAME *)(mod + names->u1.AddressOfData))->Name, fn)) continue;
            DWORD old;
            if (!VirtualProtect(&iat->u1.Function, sizeof(void *), PAGE_READWRITE, &old)) return false;
            *orig = (void *)iat->u1.Function; iat->u1.Function = (ULONG_PTR)hook;
            VirtualProtect(&iat->u1.Function, sizeof(void *), old, &old);
            return true;
        }
    }
    return false;
}

static bool PatchSlot(void **slot, void *hook, void **orig)
{
    DWORD old;
    if (!VirtualProtect(slot, sizeof(void *), PAGE_EXECUTE_READWRITE, &old)) return false;
    *orig = *slot; *slot = hook;
    VirtualProtect(slot, sizeof(void *), old, &old);
    return true;
}

// Called from PreInit, before the game builds its renderer and asks for modes.
static void InstallModeHooks()
{
    if (!PresentFlag("RipperBridge.modes")) return;
    DEVMODEA cur = {}; cur.dmSize = sizeof(cur);
    if (!EnumDisplaySettingsA(NULL, ENUM_CURRENT_SETTINGS, &cur)) { Log("modes: no current display mode - not hooked"); return; }
    int W = cur.dmPelsWidth, H = cur.dmPelsHeight;
    // Mycelium's own PC's sizes (main screen, NVIDIA panel customs included; measured 2026-10-01)
    static const int pc[][2] = { {640,480},{720,480},{720,576},{800,600},{1024,768},{1152,864},{1176,664},{1280,720},
        {1280,768},{1280,800},{1280,960},{1280,1024},{1360,768},{1366,768},{1440,1080},{1600,900},{1600,1024},{1600,1200},
        {1680,1050},{1768,992},{1920,808},{1920,1080},{1920,1200},{1920,1440},{2048,1080},{2048,1152},{2048,1280},
        {2048,1536},{2560,1440},{2560,1600},{3840,1608},{3840,2160} };
    for (int i = 0; i < (int)(sizeof(pc) / sizeof(pc[0])); i++) if (pc[i][0] <= W && pc[i][1] <= H) AddMode(pc[i][0], pc[i][1]);
    AddMode(W, H);
    char path[MAX_PATH]; GetModuleFileNameA(NULL, path, MAX_PATH);
    char *sl = strrchr(path, '\\'); if (sl) strcpy_s(sl + 1, MAX_PATH - (sl + 1 - path), "RipperBridge.modes");
    int extra = 0;
    if (FILE *f = fopen(path, "r")) {
        char line[64]; int w, h;
        while (fgets(line, sizeof(line), f)) if (sscanf_s(line, "%dx%d", &w, &h) == 2 && w <= W && h <= H) { AddMode(w, h); extra++; }
        fclose(f);
    }
    bool gdi = PatchIat(g_base, "user32.dll", "EnumDisplaySettingsA", (void *)&HookEnumDisplaySettingsA, (void **)&g_enumA);
    bool dx = false;
    IDXGIFactory1 *fac = NULL;
    if (SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void **)&fac))) {
        IDXGIAdapter1 *a = NULL; IDXGIOutput *o = NULL;
        for (UINT ai = 0; !o && fac->EnumAdapters1(ai, &a) == S_OK; ai++) { a->EnumOutputs(0, &o); a->Release(); }
        if (o) {
            void **vt = *(void ***)o;                  // IDXGIOutput: 8 GetDisplayModeList, 9 FindClosestMatchingMode
            dx = PatchSlot(&vt[8], (void *)&HookGetDisplayModeList, (void **)&g_gdml) &&
                 PatchSlot(&vt[9], (void *)&HookFindClosest, (void **)&g_findClosest);
            o->Release();
        }
        fac->Release();
    }
    sprintf_s(g_modeHow, "desktop %dx%d: %d sizes (%d from the file); EnumDisplaySettingsA %s, DXGI %s",
              W, H, g_modeCount, extra, gdi ? "hooked" : "NOT hooked", dx ? "hooked" : "NOT hooked");
    Log("modes: %s", g_modeHow);
}

static void ModesStatus(char *out, size_t cap)
{
    if (!g_modeHow[0]) { strcpy_s(out, cap, "modes off (no RipperBridge.modes beside the exe)"); return; }
    int n = sprintf_s(out, cap, "modes %s; asked GDI %ld, DXGI %ld:", g_modeHow, g_modeAsksGdi, g_modeAsksDxgi);
    for (int i = 0; i < g_modeCount && n > 0 && (size_t)n < cap - 16; i++) n += sprintf_s(out + n, cap - n, " %dx%d", g_modes[i].w, g_modes[i].h);
}

// ---- dvars, straight from the table -------------------------------------------------
static const char *kTypes[] = { "invalid", "bool", "float", "vec2", "vec3", "vec4", "int", "enum", "string", "color", "int64", "linearRGB", "colorXYZ" };

static bool DvarLookup(const char *want, char *out, size_t cap)
{
    BYTE *rec = g_base + kRvaDvarPool;
    char name[80];
    for (int i = 0; i < 6000; i++, rec += kDvarStride) {
        const char *np = 0;
        if (!SafeCopy(&np, rec, 4) || !SafeStr(np, name, sizeof(name))) return false;   // end of table
        if (_stricmp(name, want) != 0) continue;
        int type = 0; SafeCopy(&type, rec + 0x10, 4); type &= 0xFF;
        const BYTE *v = rec + 0x18;
        const char *tn = type < 13 ? kTypes[type] : "?";
        float f[4] = {}; int iv = 0; const char *sp = 0; char s[256] = "";
        switch (type) {
        case 1: _snprintf_s(out, cap, _TRUNCATE, "%s bool %d", name, v[0] ? 1 : 0); break;
        case 2: SafeCopy(f, v, 4); _snprintf_s(out, cap, _TRUNCATE, "%s float %g", name, f[0]); break;
        case 3: SafeCopy(f, v, 8); _snprintf_s(out, cap, _TRUNCATE, "%s vec2 %g %g", name, f[0], f[1]); break;
        case 4: case 11: case 12: SafeCopy(f, v, 12); _snprintf_s(out, cap, _TRUNCATE, "%s %s %g %g %g", name, tn, f[0], f[1], f[2]); break;
        case 5: SafeCopy(f, v, 16); _snprintf_s(out, cap, _TRUNCATE, "%s vec4 %g %g %g %g", name, f[0], f[1], f[2], f[3]); break;
        case 6: SafeCopy(&iv, v, 4); _snprintf_s(out, cap, _TRUNCATE, "%s int %d", name, iv); break;
        case 8: SafeCopy(&sp, v, 4); SafeStr(sp, s, sizeof(s)); _snprintf_s(out, cap, _TRUNCATE, "%s string \"%s\"", name, s); break;
        case 9: _snprintf_s(out, cap, _TRUNCATE, "%s color %u %u %u %u", name, v[0], v[1], v[2], v[3]); break;
        default: SafeCopy(&iv, v, 4); _snprintf_s(out, cap, _TRUNCATE, "%s %s raw %d", name, tn, iv); break;
        }
        return true;
    }
    return false;
}

// Clear (or restore) a dvar's cheat-protection flag, 0x80 in the flags at +0x0C, so the console's own
// "set" applies it (BatchCore's DvarTable does the same from outside; proven 2026-10-02 with cg_draw2d:
// a record write of a protected dvar reads back set but is never applied, the flag cleared + cmd is).
static bool DvarProtect(const char *want, bool protect, char *out, size_t cap)
{
    BYTE *rec = g_base + kRvaDvarPool; char name[80];
    for (int i = 0; i < 6000; i++, rec += kDvarStride) {
        const char *np = 0;
        if (!SafeCopy(&np, rec, 4) || !SafeStr(np, name, sizeof(name))) break;
        if (_stricmp(name, want) != 0) continue;
        DWORD flags = 0; SafeCopy(&flags, rec + 0x0C, 4);
        DWORD now = protect ? (flags | 0x80) : (flags & ~0x80u);
        DWORD old;
        if (now != flags) {
            if (!VirtualProtect(rec + 0x0C, 4, PAGE_READWRITE, &old)) { _snprintf_s(out, cap, _TRUNCATE, "error protect"); return false; }
            SafeCopy(rec + 0x0C, &now, 4);
            VirtualProtect(rec + 0x0C, 4, old, &old);
        }
        _snprintf_s(out, cap, _TRUNCATE, "ok %s flags 0x%lX -> 0x%lX", name, flags, now);
        return true;
    }
    _snprintf_s(out, cap, _TRUNCATE, "error no such dvar");
    return false;
}

// Write a dvar's value straight into its record, past cheat protection. Measured
// 2026-09-29: dvars flagged 0x80 (r_filmUseTweaks, r_fog, r_fogTweak, r_fullbright,
// r_dof_tweak, cg_fov, cg_draw2d...) refuse a console "set" from the menus AND in a
// demo, yet they are the core of every BO2 editing CFG. Redacted only - Mycelium's rule.
// current (+0x18) and latched (+0x28) are both written; strings are not supported.
static bool DvarWrite(const char *want, const char *value, char *out, size_t cap)
{
    BYTE *rec = g_base + kRvaDvarPool; char name[80];
    for (int i = 0; i < 6000; i++, rec += kDvarStride) {
        const char *np = 0;
        if (!SafeCopy(&np, rec, 4) || !SafeStr(np, name, sizeof(name))) break;
        if (_stricmp(name, want) != 0) continue;
        int type = 0; SafeCopy(&type, rec + 0x10, 4); type &= 0xFF;
        BYTE v[16] = {}; float f[4] = {};
        int n = sscanf_s(value, "%f %f %f %f", &f[0], &f[1], &f[2], &f[3]);
        if (n < 1) { _snprintf_s(out, cap, _TRUNCATE, "error could not read a number from '%s'", value); return false; }
        switch (type) {
        case 1: v[0] = f[0] != 0; break;
        case 2: case 3: case 4: case 5: case 11: case 12: memcpy(v, f, 16); break;
        case 6: case 7: { int iv = (int)f[0]; memcpy(v, &iv, 4); break; }
        case 9: for (int k = 0; k < 4; k++) { float c = k < n ? f[k] : 1.0f; v[k] = (BYTE)(c <= 1.0f ? c * 255.0f + 0.5f : c); } break;
        default: _snprintf_s(out, cap, _TRUNCATE, "error type %d is not writable here", type); return false;
        }
        size_t len = (type == 1) ? 1 : (type == 6 || type == 7 || type == 9 || type == 2) ? 4 : (type == 3) ? 8 : (type == 5) ? 16 : 12;
        DWORD old;
        if (!VirtualProtect(rec + 0x18, 0x20, PAGE_READWRITE, &old)) { _snprintf_s(out, cap, _TRUNCATE, "error protect"); return false; }
        bool ok = SafeCopy(rec + 0x18, v, len) && SafeCopy(rec + 0x28, v, len);
        VirtualProtect(rec + 0x18, 0x20, old, &old);
        Log("setdvar %s = %s (type %d) -> %d", name, value, type, ok);
        if (!ok) { _snprintf_s(out, cap, _TRUNCATE, "error unwritable"); return false; }
        return DvarLookup(name, out, cap);
    }
    _snprintf_s(out, cap, _TRUNCATE, "error no such dvar");
    return false;
}

// ---- assets: the engine's own lookup ---------------------------------------------------
// DB_FindXAssetHeader at base+0x2F9030: (type, name, errorIfMissing, waitTime). Redacted only.
typedef void *(__cdecl *DB_FindXAssetHeader_t)(int type, const char *name, bool errorIfMissing, int waitTime);
static const DWORD kRvaFindAsset = 0x2F9030;

static BYTE *FindAsset(int type, const char *name, int waitTime = -1)
{
    DB_FindXAssetHeader_t find = (DB_FindXAssetHeader_t)(g_base + kRvaFindAsset);
    void *h = NULL;
    __try { h = find(type, name, false, waitTime); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
    return (BYTE *)h;
}

static void AssetName(BYTE *asset, char *out, size_t cap)
{
    const char *np = 0; out[0] = 0;
    if (asset && SafeCopy(&np, asset, 4)) SafeStr(np, out, cap);   // name is the first field
}

// ---- the map's shape, for the Designer's 3D views (2026-10-17) ----------------------------
// Mycelium: "Since we're leaving the maps out, make sure we make a streamlined easy process for users to get them
// themselves or to have the Reluduali Designer Create them itself when installed." So the Designer makes each map's
// shape from the user's own game. Measured in the game itself (sandbox 0567-0571, docs\map-shapes.md): the world's draw
// record is the asset lookup's type 17, "maps/mp/<map>.d3dbsp"; its position stream at +0x1B0 (a vertex every 36
// bytes, the position first; the stream's size in bytes at +0x1AC); its u16 indices at +0x1C8 (their count at +0x1C4),
// local to each surface's block; its surfaces at +0x368, 80 bytes each - word 3 the block's byte offset in the stream,
// word 10's high half the triangle count, word 11 the first index, word 12 the material, words 14-19 the bounds - and
// the first +0x314 of them are the world's own, in world space (the rest are sub-models in their own space).
// Written as RVM2, uncompressed (the Designer gzips it): "RVM2", u32 vertices, u32 triangles, f32[6] bounds, f32[3] per
// vertex, u32[3] per triangle, u8 colour group per triangle (0..23, from the surface's material name).
static const int kAssetWorld = 17;

// The surfaces nobody sees: a material whose name has one of these as a word ("wpc/sky_raid", "mc/clip"; not
// "skyscraper").
// ---- dev46: the map's records, raw, for the Blender export without C2M (2026-10-17; testing\map-export-2026-10-17) ----
// "map probe <dir>": with a map loaded, its records written to <dir>\records.bin (memory read only, nothing written in
// the game) to be decoded offline: the world record and every array it points to (the vertex stream whole, the
// surfaces, the static models' placements); every surface's material, what the material points to, and what that
// points to; the static models' records likewise; the collision record and its entity text. Each record in the file:
// "RREC", u32 kind, u32 address, u32 length, the bytes. strings.txt: every address found to hold a name.
struct ProbeSet { DWORD *slot; DWORD cap; DWORD n; };
static bool ProbeSeen(ProbeSet &s, DWORD v)                  // true if v was there already (else it is added)
{
    if (!s.slot) { s.cap = 1u << 20; s.slot = (DWORD *)calloc(s.cap, 4); s.n = 0; if (!s.slot) return true; }
    if (s.n * 2 > s.cap) return true;                         // full: treat as seen (the probe only gets shorter)
    DWORD i = (v * 2654435761u) & (s.cap - 1);
    while (s.slot[i]) { if (s.slot[i] == v) return true; i = (i + 1) & (s.cap - 1); }
    s.slot[i] = v; s.n++; return false;
}
static size_t ProbeLeft(const void *p)                        // readable bytes from p to the end of its region
{
    if ((size_t)p < 0x10000) return 0;
    MEMORY_BASIC_INFORMATION mi;
    if (!VirtualQuery(p, &mi, sizeof(mi)) || mi.State != MEM_COMMIT || (mi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) || !mi.Protect) return 0;
    return (size_t)((BYTE *)mi.BaseAddress + mi.RegionSize - (BYTE *)p);
}
struct ProbeOut { FILE *rec, *txt; ProbeSet seen, named; size_t total; DWORD records; };
static void ProbeWrite(ProbeOut &o, DWORD kind, const void *p, size_t want)
{
    size_t n = ProbeLeft(p); if (n > want) n = want;
    if (!n || o.total + n > (size_t)400 << 20) return;        // 400 MB at most in all
    BYTE *buf = (BYTE *)malloc(n); if (!buf) return;
    if (SafeCopy(buf, p, n)) {
        DWORD h[4] = { 0x43455252u /* RREC */, kind, (DWORD)(size_t)p, (DWORD)n };
        fwrite(h, 4, 4, o.rec); fwrite(buf, 1, n, o.rec); o.total += n; o.records++;
    }
    free(buf);
}
static void ProbeName(ProbeOut &o, const void *p)            // a name at p (or p points to one), once
{
    char s[300];
    if (ProbeSeen(o.named, (DWORD)(size_t)p)) return;
    if (SafeStr((const char *)p, s, sizeof(s)) && strlen(s) >= 2) fprintf(o.txt, "%08lX\t%s\n", (DWORD)(size_t)p, s);
    const char *q = 0;
    if (SafeCopy(&q, p, 4) && SafeStr(q, s, sizeof(s)) && strlen(s) >= 2) fprintf(o.txt, "%08lX\t->%08lX\t%s\n", (DWORD)(size_t)p, (DWORD)(size_t)q, s);
}
// a record and, for every word in it that points somewhere readable, that place too (depth levels down)
static void ProbeTree(ProbeOut &o, DWORD kind, const void *p, size_t len, int depth, size_t childLen)
{
    if (ProbeSeen(o.seen, (DWORD)(size_t)p)) return;
    ProbeWrite(o, kind, p, len);
    ProbeName(o, p);
    if (depth <= 0) return;
    size_t n = ProbeLeft(p); if (n > len) n = len;
    DWORD *w = (DWORD *)malloc(n + 4); if (!w) return;
    if (SafeCopy(w, p, n & ~3u))
        for (size_t i = 0; i < n / 4; i++)
            if (w[i] >= 0x10000 && ProbeLeft((void *)(size_t)w[i]) >= 4) ProbeTree(o, kind + 1, (void *)(size_t)w[i], childLen, depth - 1, childLen);
    free(w);
}

static bool MapProbe(const char *dir, char *out, size_t cap)
{
    char dv[256], map[64], want[96], got[160], path[600];
    if (!DvarLookup("cl_ingame", dv, sizeof(dv)) || strcmp(dv, "cl_ingame bool 1") != 0) { _snprintf_s(out, cap, _TRUNCATE, "error no map is loaded: play a film first"); return false; }
    if (!DvarLookup("mapname", dv, sizeof(dv))) { _snprintf_s(out, cap, _TRUNCATE, "error no mapname dvar"); return false; }
    const char *q = strchr(dv, '"'), *q2 = q ? strchr(q + 1, '"') : NULL;
    if (!q || !q2 || q2 - q - 1 <= 0 || q2 - q - 1 >= (int)sizeof(map)) { _snprintf_s(out, cap, _TRUNCATE, "error no map is loaded"); return false; }
    memcpy(map, q + 1, q2 - q - 1); map[q2 - q - 1] = 0;
    _snprintf_s(want, sizeof(want), _TRUNCATE, "maps/mp/%s.d3dbsp", map);
    BYTE *w = FindAsset(kAssetWorld, want, 0);
    AssetName(w, got, sizeof(got));
    if (!w || _stricmp(got, want)) { _snprintf_s(out, cap, _TRUNCATE, "error %s is not loaded", map); return false; }
    CreateDirectoryA(dir, NULL);
    ProbeOut o = {};
    _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\records.bin", dir);
    if (fopen_s(&o.rec, path, "wb") || !o.rec) { _snprintf_s(out, cap, _TRUNCATE, "error cannot write %s", path); return false; }
    _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\strings.txt", dir);
    if (fopen_s(&o.txt, path, "w") || !o.txt) { fclose(o.rec); _snprintf_s(out, cap, _TRUNCATE, "error cannot write %s", path); return false; }
    fprintf(o.txt, "# map %s, world record %08lX\n", map, (DWORD)(size_t)w);

    // kind 1: the world record (0x400); kind 2: what each of its words points to (the measured arrays whole, others 64 KB)
    DWORD rec[256] = {};
    SafeCopy(rec, w, sizeof(rec));
    ProbeWrite(o, 1, w, sizeof(rec)); ProbeSeen(o.seen, (DWORD)(size_t)w);
    DWORD posSize = rec[0x1AC / 4], idxCount = rec[0x1C4 / 4], nAll = rec[0x10 / 4], nStatic = rec[0x310 / 4];
    for (int i = 0; i < 256; i++) {
        DWORD v = rec[i];
        if (v < 0x10000 || !ProbeLeft((void *)(size_t)v) || ProbeSeen(o.seen, v)) continue;
        size_t len = 0x10000;
        if (i * 4 == 0x1B0) len = posSize;                                   // the vertex stream
        else if (i * 4 == 0x1BC) len = (size_t)posSize;                      // the second stream (its size: to find)
        else if (i * 4 == 0x1C8) len = (size_t)idxCount * 2;
        else if (i * 4 == 0x368) len = (size_t)nAll * 80;
        else if (i * 4 == 0x364 || i * 4 == 0x36C) len = (size_t)nStatic * 128;
        else if (v > (DWORD)(size_t)w - 0x400000 && v < (DWORD)(size_t)w + 0x400000) len = 0x40000;
        fprintf(o.txt, "# field +0x%03X -> %08lX (%u bytes asked)\n", i * 4, v, (unsigned)len);
        ProbeWrite(o, 2, (void *)(size_t)v, len);
        ProbeName(o, (void *)(size_t)v);
    }
    // kind 10-13: every surface's material, then two levels of what it points to (0x200, then 0x100 each)
    DWORD *surf = (DWORD *)malloc((size_t)nAll * 80);
    DWORD mats = 0;
    if (surf && nAll && nAll < 400000 && SafeCopy(surf, (void *)(size_t)rec[0x368 / 4], (size_t)nAll * 80))
        for (DWORD s = 0; s < nAll; s++) {
            DWORD m = surf[s * 20 + 12];
            if (m < 0x10000 || ProbeLeft((void *)(size_t)m) < 4) continue;
            if (!ProbeSeen(o.seen, m)) { ProbeSeen(o.seen, m); ProbeWrite(o, 10, (void *)(size_t)m, 0x200); ProbeName(o, (void *)(size_t)m); mats++;
                DWORD mw[128] = {};
                SafeCopy(mw, (void *)(size_t)m, sizeof(mw));
                for (int k = 0; k < 128; k++)
                    if (mw[k] >= 0x10000 && ProbeLeft((void *)(size_t)mw[k]) >= 4) ProbeTree(o, 11, (void *)(size_t)mw[k], 0x100, 1, 0x80);
            }
        }
    free(surf);
    // kind 20-22: the static models: every distinct pointer in the placements that leads to a named record, and below it
    DWORD models = 0;
    size_t plen = (size_t)nStatic * 128;
    DWORD *pl = nStatic && nStatic < 200000 ? (DWORD *)malloc(plen) : NULL;
    if (pl && SafeCopy(pl, (void *)(size_t)rec[0x36C / 4], ProbeLeft((void *)(size_t)rec[0x36C / 4]) < plen ? ProbeLeft((void *)(size_t)rec[0x36C / 4]) & ~3u : plen)) {
        for (size_t i = 0; i < plen / 4; i++) {
            DWORD v = pl[i]; const char *nm = 0; char s[200];
            if (v < 0x10000 || ProbeLeft((void *)(size_t)v) < 8 || !SafeCopy(&nm, (void *)(size_t)v, 4) || !SafeStr(nm, s, sizeof(s))) continue;
            if (ProbeSeen(o.seen, v)) continue;
            models++;
            ProbeWrite(o, 20, (void *)(size_t)v, 0x200); ProbeName(o, (void *)(size_t)v);
            DWORD mw[128] = {};
            SafeCopy(mw, (void *)(size_t)v, sizeof(mw));
            for (int k = 0; k < 128; k++)
                if (mw[k] >= 0x10000 && ProbeLeft((void *)(size_t)mw[k]) >= 4) ProbeTree(o, 21, (void *)(size_t)mw[k], 0x200, 1, 0x100);
        }
    }
    free(pl);
    // kind 30-32: the collision record (type 12) and two levels below; any entity text found ("{" first) whole
    BYTE *clip = FindAsset(12, want, 0);
    AssetName(clip, got, sizeof(got));
    DWORD entsLen = 0;
    if (clip && !_stricmp(got, want)) {
        fprintf(o.txt, "# collision record %08lX\n", (DWORD)(size_t)clip);
        ProbeTree(o, 30, clip, 0x400, 2, 0x100);
        DWORD cw[256] = {};
        SafeCopy(cw, clip, sizeof(cw));
        for (int k = 0; k < 256 && !entsLen; k++) {
            DWORD v = cw[k], e[16] = {};
            if (v < 0x10000 || !SafeCopy(e, (void *)(size_t)v, sizeof(e))) continue;
            for (int j = 0; j < 16 && !entsLen; j++) {
                char c[4] = {};
                if (e[j] >= 0x10000 && SafeCopy(c, (void *)(size_t)e[j], 2) && c[0] == '{' && (c[1] == '\n' || c[1] == '\r' || c[1] == ' ')) {
                    size_t n = ProbeLeft((void *)(size_t)e[j]); if (n > (8u << 20)) n = 8u << 20;
                    char *t = (char *)malloc(n + 1);
                    if (t && SafeCopy(t, (void *)(size_t)e[j], n)) {
                        size_t len = strnlen(t, n);
                        _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\entities.txt", dir);
                        FILE *f = NULL;
                        if (!fopen_s(&f, path, "wb") && f) { fwrite(t, 1, len, f); fclose(f); entsLen = (DWORD)len; }
                        fprintf(o.txt, "# entity text at %08lX (collision +0x%03X -> %08lX word %d), %lu bytes\n", e[j], k * 4, v, j, entsLen);
                    }
                    free(t);
                }
            }
        }
    }
    fclose(o.rec); fclose(o.txt);
    free(o.seen.slot); free(o.named.slot);
    _snprintf_s(out, cap, _TRUNCATE, "ok map probe %s: %lu records, %.1f MB, %lu materials, %lu model records, entity text %lu bytes -> %s",
                map, o.records, o.total / 1048576.0, mats, models, entsLen, dir);
    Log("%s", out);
    return true;
}

// dev47: "model probe <dir> <name> [<name> ...]": loaded models (xmodel, type 5) written raw like the map probe, deeper -
// each record 0x200 bytes, what it points to 0x800, one level more 0x400 - to find where a model's surfaces, vertices and
// triangles are. Memory read only.
static bool ModelProbe(const char *args, char *out, size_t cap)
{
    char dir[400] = "", rest[600] = "", path[600];
    if (sscanf_s(args, "%399s %599[^\n]", dir, (unsigned)sizeof(dir), rest, (unsigned)sizeof(rest)) != 2) { _snprintf_s(out, cap, _TRUNCATE, "error usage: model probe <dir> <name> [<name> ...]"); return false; }
    CreateDirectoryA(dir, NULL);
    ProbeOut o = {};
    _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\models.bin", dir);
    if (fopen_s(&o.rec, path, "wb") || !o.rec) { _snprintf_s(out, cap, _TRUNCATE, "error cannot write %s", path); return false; }
    _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\models.txt", dir);
    if (fopen_s(&o.txt, path, "w") || !o.txt) { fclose(o.rec); _snprintf_s(out, cap, _TRUNCATE, "error cannot write %s", path); return false; }
    int found = 0, asked = 0;
    char *ctx = NULL;
    for (char *nm = strtok_s(rest, " ", &ctx); nm; nm = strtok_s(NULL, " ", &ctx)) {
        asked++;
        BYTE *m = FindAsset(5, nm, 0); char got[160]; AssetName(m, got, sizeof(got));
        fprintf(o.txt, "# model %s -> %08lX named '%s'\n", nm, (DWORD)(size_t)m, got);
        if (!m || _stricmp(got, nm)) continue;
        found++;
        ProbeTree(o, 40 + found * 10, m, 0x200, 2, 0x800);
    }
    fclose(o.rec); fclose(o.txt); free(o.seen.slot); free(o.named.slot);
    _snprintf_s(out, cap, _TRUNCATE, "ok model probe: %d of %d found, %lu records, %.1f MB -> %s", found, asked, o.records, o.total / 1048576.0, dir);
    return found > 0;
}

// dev48: "map export <file>": the loaded map for the Blender import without C2M (testing\map-export-2026-10-17,
// docs\map-export.md). Everything measured in 0706/0707, copied as it is with its counts; tools\mapimport\bo2map\rmx.py
// decodes it. Chunks after "RMX1": a 4-character tag, a u32 length, the bytes.
//   INFO  the map's name (NUL), then the world record (0x400)
//   VTX0  the world's vertex stream (36 bytes a vertex, blocks per surface)    VTX1  its second stream (4 bytes a vertex)
//   IDX0  the world's u16 indices      SURF  every surface record (80 bytes)   SMDL  the static props' placements (152 B)
//   MATL  each material once: u32 its address, its record (0x70), its name (NUL), [RMX2: the technique set's name (NUL),
//         u32 nc, nc x 32-byte constants,] u32 n, n x (the 16-byte entry,
//         the image's record 0x50, the image's name NUL)
//   XMDL  each prop model once: u32 its address, its record (0xF8), its name (NUL), then for every surface: its record
//         (0x50), u32 its material's address, its vertices (32 bytes each), its u16 triangles
//   DYNE  the dynamic props: for each list in the collision record, u32 its offset there, u32 n, n x 84 bytes (dev49)
//   ENTS  the entity text
// Memory read only; nothing in the game changes.
struct RmxOut { FILE *f; long chunkAt; bool ok; DWORD bytes; };
static void RmxBegin(RmxOut &o, const char *tag) { o.chunkAt = ftell(o.f); DWORD z = 0; o.ok &= fwrite(tag, 1, 4, o.f) == 4 && fwrite(&z, 4, 1, o.f) == 1; }
static void RmxEnd(RmxOut &o)
{
    long end = ftell(o.f); DWORD len = (DWORD)(end - o.chunkAt - 8);
    fseek(o.f, o.chunkAt + 4, SEEK_SET); o.ok &= fwrite(&len, 4, 1, o.f) == 1; fseek(o.f, end, SEEK_SET); o.bytes += len + 8;
}
static bool RmxCopy(RmxOut &o, const void *p, size_t n)           // n bytes of the game's memory, or zeros where unreadable
{
    if (!n) return true;
    BYTE *b = (BYTE *)calloc(n, 1); if (!b) { o.ok = false; return false; }
    bool got = p && SafeCopy(b, p, n);
    o.ok &= fwrite(b, 1, n, o.f) == n; free(b); return got;
}
static void RmxStr(RmxOut &o, const void *p)
{
    char s[300] = "";
    if (p) SafeStr((const char *)p, s, sizeof(s));
    o.ok &= fwrite(s, 1, strlen(s) + 1, o.f) == strlen(s) + 1;
}
static void RmxU32(RmxOut &o, DWORD v) { o.ok &= fwrite(&v, 4, 1, o.f) == 1; }
static DWORD RmxWord(const void *p) { DWORD v = 0; SafeCopy(&v, p, 4); return v; }

static void RmxMaterial(RmxOut &o, DWORD m)
{
    RmxU32(o, m);
    RmxCopy(o, (void *)(size_t)m, 0x70);
    RmxStr(o, (void *)(size_t)RmxWord((void *)(size_t)m));
    // dev50 (RMX2): the technique set's name (+0x5C: a record whose first word is its name), and the constants
    // (+0x64: 32-byte entries - a name hash, the name's first 12 characters, f32x4; +0x55 their count)
    DWORD ts = RmxWord((void *)(size_t)(m + 0x5C));
    RmxStr(o, ts >= 0x10000 ? (void *)(size_t)RmxWord((void *)(size_t)ts) : NULL);
    DWORD ctab = RmxWord((void *)(size_t)(m + 0x64));
    BYTE ccnt = 0;
    SafeCopy(&ccnt, (void *)(size_t)(m + 0x55), 1);
    DWORD nc = ctab >= 0x10000 && ccnt <= 32 ? ccnt : 0;
    RmxU32(o, nc);
    RmxCopy(o, nc ? (void *)(size_t)ctab : NULL, (size_t)nc * 32);
    DWORD table = RmxWord((void *)(size_t)(m + 0x60));
    BYTE cnt = 0;
    SafeCopy(&cnt, (void *)(size_t)(m + 0x54), 1);                  // +0x54: the texture count (dev49; all 772 of Raid's)
    DWORD n = table >= 0x10000 && cnt <= 32 ? cnt : 0;
    RmxU32(o, n);
    for (DWORD i = 0; i < n; i++) {
        DWORD e = table + i * 16, img = RmxWord((void *)(size_t)(e + 12));
        RmxCopy(o, (void *)(size_t)e, 16);
        RmxCopy(o, img >= 0x10000 ? (void *)(size_t)img : NULL, 0x50);
        RmxStr(o, img >= 0x10000 ? (void *)(size_t)RmxWord((void *)(size_t)(img + 0x48)) : NULL);
    }
}

// a model as measured (0707): 1-4 LODs whose surface counts add up to its own (dev50: a dynamic list's entry once
// pointed at a record named "ms" with 234 "surfaces" and 22 "LODs")
static bool RmxModelOk(DWORD x)
{
    BYTE mr[0xF8] = {};
    if (x < 0x10000 || !SafeCopy(mr, (void *)(size_t)x, sizeof(mr))) return false;
    BYTE ns = mr[6]; WORD lods = *(WORD *)(mr + 0xC4);
    if (!ns || ns > 64 || lods < 1 || lods > 4) return false;
    DWORD sum = 0;
    for (int k = 0; k < lods; k++) sum += *(WORD *)(mr + 0x28 + k * 28 + 4);
    return sum == ns;
}

// the entity text: the collision record's words, one level down, the first "{" text (its address, 0 if none)
static DWORD MapEntityText(BYTE *clip)
{
    DWORD cw[256] = {};
    if (!clip || !SafeCopy(cw, clip, sizeof(cw))) return 0;
    for (int k = 0; k < 256; k++) {
        DWORD e[16] = {};
        if (cw[k] < 0x10000 || !SafeCopy(e, (void *)(size_t)cw[k], sizeof(e))) continue;
        for (int j = 0; j < 16; j++) {
            char c[2] = {};
            if (e[j] >= 0x10000 && SafeCopy(c, (void *)(size_t)e[j], 2) && c[0] == '{' && (c[1] == '\n' || c[1] == '\r' || c[1] == ' ')) return e[j];
        }
    }
    return 0;
}

// dev52: the sky - worldspawn's "skyboxmodel" (Raid: "skybox_mp_raid"), an ordinary model asset
static DWORD MapSkyModel(DWORD ents)
{
    char t[4096] = {}, name[128] = {};
    if (ents < 0x10000) return 0;
    size_t n = ProbeLeft((void *)(size_t)ents); if (n > sizeof(t) - 1) n = sizeof(t) - 1;
    if (!SafeCopy(t, (void *)(size_t)ents, n)) return 0;
    const char *k = strstr(t, "\"skyboxmodel\"");
    const char *a = k ? strchr(k + 13, '"') : NULL, *b = a ? strchr(a + 1, '"') : NULL;
    if (!a || !b || b - a - 1 <= 0 || b - a - 1 >= (int)sizeof(name)) return 0;
    memcpy(name, a + 1, b - a - 1);
    BYTE *m = FindAsset(5, name, 0); char got[160];
    AssetName(m, got, sizeof(got));
    return m && !_stricmp(got, name) && RmxModelOk((DWORD)(size_t)m) ? (DWORD)(size_t)m : 0;
}

static bool MapExport(const char *path, char *out, size_t cap)
{
    char dv[256], map[64], want[96], got[160];
    if (!DvarLookup("cl_ingame", dv, sizeof(dv)) || strcmp(dv, "cl_ingame bool 1") != 0) { _snprintf_s(out, cap, _TRUNCATE, "error no map is loaded: play a film first"); return false; }
    if (!DvarLookup("mapname", dv, sizeof(dv))) { _snprintf_s(out, cap, _TRUNCATE, "error no mapname dvar"); return false; }
    const char *q = strchr(dv, '"'), *q2 = q ? strchr(q + 1, '"') : NULL;
    if (!q || !q2 || q2 - q - 1 <= 0 || q2 - q - 1 >= (int)sizeof(map)) { _snprintf_s(out, cap, _TRUNCATE, "error no map is loaded"); return false; }
    memcpy(map, q + 1, q2 - q - 1); map[q2 - q - 1] = 0;
    _snprintf_s(want, sizeof(want), _TRUNCATE, "maps/mp/%s.d3dbsp", map);
    BYTE *w = FindAsset(kAssetWorld, want, 0);
    AssetName(w, got, sizeof(got));
    if (!w || _stricmp(got, want)) { _snprintf_s(out, cap, _TRUNCATE, "error %s is not loaded", map); return false; }
    DWORD rec[256] = {};
    if (!SafeCopy(rec, w, sizeof(rec))) { _snprintf_s(out, cap, _TRUNCATE, "error the world record cannot be read"); return false; }
    DWORD posSize = rec[0x1AC / 4], idxCount = rec[0x1C4 / 4], nAll = rec[0x10 / 4], nStatic = rec[0x310 / 4];
    DWORD gPos = rec[0x1B0 / 4], gPos2 = rec[0x1BC / 4], gIdx = rec[0x1C8 / 4], gSurf = rec[0x368 / 4], gStatic = rec[0x36C / 4];
    if (posSize < 36 || posSize > 0x10000000 || idxCount > 0x4000000 || !nAll || nAll > 400000 || nStatic > 200000) {
        _snprintf_s(out, cap, _TRUNCATE, "error %s's world record does not read as measured", map); return false;
    }
    DWORD *surf = (DWORD *)malloc((size_t)nAll * 80);
    if (!surf || !SafeCopy(surf, (void *)(size_t)gSurf, (size_t)nAll * 80)) { free(surf); _snprintf_s(out, cap, _TRUNCATE, "error the surfaces cannot be read"); return false; }
    // the second stream's length: past the last surface's vertices (word 7 = its first vertex x 4; vertices from its indices)
    WORD *idx = (WORD *)malloc((size_t)idxCount * 2 + 2);
    if (!idx || !SafeCopy(idx, (void *)(size_t)gIdx, (size_t)idxCount * 2)) { free(surf); free(idx); _snprintf_s(out, cap, _TRUNCATE, "error the indices cannot be read"); return false; }
    size_t len2 = 0;
    for (DWORD s = 0; s < nAll; s++) {
        DWORD ntri = surf[s * 20 + 10] >> 16, first = surf[s * 20 + 11], top = 0;
        if (first + (u64)ntri * 3 > idxCount) continue;
        for (DWORD t = 0; t < ntri * 3; t++) if (idx[first + t] > top) top = idx[first + t];
        size_t end = (size_t)surf[s * 20 + 7] + ((size_t)top + 1) * 4;
        if (ntri && end > len2 && end < 0x10000000) len2 = end;
    }
    FILE *f = NULL;
    if (fopen_s(&f, path, "wb") || !f) { free(surf); free(idx); _snprintf_s(out, cap, _TRUNCATE, "error cannot write %s", path); return false; }
    RmxOut o = { f, 0, true, 0 };
    o.ok &= fwrite("RMX2", 1, 4, f) == 4;                          // dev50: RMX2 (MATL carries the technique set and the constants)
    RmxBegin(o, "INFO"); o.ok &= fwrite(map, 1, strlen(map) + 1, f) == strlen(map) + 1; RmxCopy(o, w, 0x400); RmxEnd(o);
    RmxBegin(o, "VTX0"); bool vOk = RmxCopy(o, (void *)(size_t)gPos, posSize); RmxEnd(o);
    RmxBegin(o, "VTX1"); RmxCopy(o, gPos2 >= 0x10000 ? (void *)(size_t)gPos2 : NULL, len2); RmxEnd(o);
    RmxBegin(o, "IDX0"); o.ok &= fwrite(idx, 2, idxCount, f) == idxCount; RmxEnd(o);
    RmxBegin(o, "SURF"); o.ok &= fwrite(surf, 80, nAll, f) == nAll; RmxEnd(o);
    RmxBegin(o, "SMDL"); RmxCopy(o, (void *)(size_t)gStatic, (size_t)nStatic * 152); RmxEnd(o);
    // the prop models (each once) and every material they or the world use (each once)
    ProbeSet seenM = {}, seenX = {};
    DWORD nMat = 0, nModel = 0;
    const DWORD kDynMax = 8192;
    DWORD *models = (DWORD *)calloc(nStatic + kDynMax * 3 + 1, 4); DWORD nm = 0;
    for (DWORD i = 0; i < nStatic; i++) {
        DWORD x = RmxWord((void *)(size_t)(gStatic + i * 152 + 56));
        if (RmxModelOk(x) && !ProbeSeen(seenX, x)) models[nm++] = x;
    }
    // dev49: the dynamic props (C2M's "dynamic models"): the collision record's lists of 84-byte entries (+0x10 a
    // quaternion, +0x20 the origin, +0x2C the model; 0706: Raid's at clip +0xAC). Each list is read entry by entry while
    // its entries name a model (64 in a row that do not end it), and written whole; their models join the props'.
    BYTE *clip = FindAsset(12, want, 0);
    AssetName(clip, got, sizeof(got));
    if (!clip || _stricmp(got, want)) clip = NULL;
    RmxBegin(o, "DYNE");
    if (clip)
        for (int li = 0; li < 3; li++) {
            static const DWORD at[3] = { 0xA0, 0xA8, 0xAC };
            DWORD base = RmxWord(clip + at[li]), n = 0, miss = 0;
            if (base < 0x10000) continue;
            for (DWORD i = 0; i < kDynMax && miss < 64; i++) {
                DWORD x = RmxWord((void *)(size_t)(base + i * 84 + 44)); char s[8];
                bool named = x >= 0x10000 && SafeStr((const char *)(size_t)RmxWord((void *)(size_t)x), s, sizeof(s)) && RmxModelOk(x);
                if (named) { miss = 0; n = i + 1; if (!ProbeSeen(seenX, x)) models[nm++] = x; } else miss++;
            }
            RmxU32(o, at[li]); RmxU32(o, n);
            RmxCopy(o, (void *)(size_t)base, (size_t)n * 84);
        }
    RmxEnd(o);
    DWORD ents = MapEntityText(clip), sky = MapSkyModel(ents);
    if (sky && !ProbeSeen(seenX, sky)) models[nm++] = sky;
    RmxBegin(o, "SKYB"); RmxU32(o, sky); RmxEnd(o);                  // dev52: which model is the sky (0: none)
    RmxBegin(o, "XMDL");
    for (DWORD i = 0; i < nm; i++) {
        DWORD x = models[i]; BYTE mr[0xF8] = {};
        if (!SafeCopy(mr, (void *)(size_t)x, sizeof(mr))) continue;
        DWORD surfs = *(DWORD *)(mr + 0x20), mats = *(DWORD *)(mr + 0x24); BYTE ns = mr[6];
        RmxU32(o, x); o.ok &= fwrite(mr, 1, sizeof(mr), f) == sizeof(mr); RmxStr(o, (void *)(size_t)*(DWORD *)mr);
        for (BYTE k = 0; k < ns; k++) {
            BYTE sr[0x50] = {};
            SafeCopy(sr, (void *)(size_t)(surfs + k * 0x50), sizeof(sr));
            WORD nv = *(WORD *)(sr + 4), nt = *(WORD *)(sr + 6);
            DWORD verts = *(DWORD *)(sr + 0x20), tris = *(DWORD *)(sr + 0x0C), mat = RmxWord((void *)(size_t)(mats + k * 4));
            o.ok &= fwrite(sr, 1, sizeof(sr), f) == sizeof(sr);
            RmxU32(o, mat);
            RmxCopy(o, verts >= 0x10000 ? (void *)(size_t)verts : NULL, (size_t)nv * 32);
            RmxCopy(o, tris >= 0x10000 ? (void *)(size_t)tris : NULL, (size_t)nt * 6);
        }
        nModel++;
    }
    RmxEnd(o);
    RmxBegin(o, "MATL");
    for (DWORD s = 0; s < nAll; s++) {
        DWORD m = surf[s * 20 + 12];
        if (m >= 0x10000 && !ProbeSeen(seenM, m)) { RmxMaterial(o, m); nMat++; }
    }
    for (DWORD i = 0; i < nm; i++) {
        BYTE mr[0xF8] = {};
        if (!SafeCopy(mr, (void *)(size_t)models[i], sizeof(mr))) continue;
        DWORD mats = *(DWORD *)(mr + 0x24);
        for (BYTE k = 0; k < mr[6]; k++) {
            DWORD m = RmxWord((void *)(size_t)(mats + k * 4));
            if (m >= 0x10000 && !ProbeSeen(seenM, m)) { RmxMaterial(o, m); nMat++; }
        }
    }
    RmxEnd(o);
    // the entity text
    DWORD entsLen = 0;
    RmxBegin(o, "ENTS");
    if (ents) {
        size_t n = ProbeLeft((void *)(size_t)ents); if (n > (8u << 20)) n = 8u << 20;
        char *t = (char *)malloc(n + 1);
        if (t && SafeCopy(t, (void *)(size_t)ents, n)) { size_t len = strnlen(t, n); o.ok &= fwrite(t, 1, len, f) == len; entsLen = (DWORD)len; }
        free(t);
    }
    RmxEnd(o);
    // dev52, to measure: what some of the world record's words point at - +0x21C (+0x218 = 142 on Raid: the 141 "*N"
    // brush models + the world), +0x3D8..+0x3E8 (+0x3DC = 5 = C2M's lights on Raid). Each: u32 offset, u32 address,
    // u32 length, the bytes
    RmxBegin(o, "PEEK");
    {
        static const DWORD at[] = { 0x21C, 0x3D8, 0x3E0, 0x3E4, 0x3E8 };
        for (int k = 0; k < (int)(sizeof(at) / sizeof(at[0])); k++) {
            DWORD a = rec[at[k] / 4];
            size_t len = a >= 0x10000 ? ProbeLeft((void *)(size_t)a) : 0;
            size_t want = at[k] == 0x21C ? (size_t)(rec[0x218 / 4] < 8192 ? rec[0x218 / 4] : 8192) * 0x80 : 0x2000;
            if (len > want) len = want;
            RmxU32(o, at[k]); RmxU32(o, a); RmxU32(o, (DWORD)len);
            RmxCopy(o, len ? (void *)(size_t)a : NULL, len);
        }
    }
    RmxEnd(o);
    bool ok = o.ok && vOk;
    if (fclose(f) != 0) ok = false;
    free(surf); free(idx); free(models); free(seenM.slot); free(seenX.slot);
    if (!ok) _snprintf_s(out, cap, _TRUNCATE, "error %s: the export could not be written whole (disk full, or the map was unloaded)", map);
    else _snprintf_s(out, cap, _TRUNCATE, "ok map export %s: %lu surfaces, %lu props of %lu models, %lu materials, entity text %lu bytes, %.1f MB -> %s",
                     map, nAll, nStatic, nModel, nMat, entsLen, o.bytes / 1048576.0, path);
    Log("%s", out);
    return ok;
}

static bool MapShapeHidden(const char *name)
{
    static const char *words[] = { "sky", "caulk", "clip", "nodraw", "portal", "hint", "shadowcaster", "mantle", "trigger",
                                   "lightgrid", "volume" };
    char low[256]; size_t n = 0;
    for (; name[n] && n + 1 < sizeof(low); n++) low[n] = (char)tolower((unsigned char)name[n]);
    low[n] = 0;
    for (int w = 0; w < (int)(sizeof(words) / sizeof(words[0])); w++) {
        size_t len = strlen(words[w]);
        for (const char *p = strstr(low, words[w]); p; p = strstr(p + 1, words[w])) {
            bool start = p == low || !isalnum((unsigned char)p[-1]);
            bool end = !isalpha((unsigned char)p[len]);
            if (start && end) return true;
        }
    }
    return false;
}

static bool MapShape(const char *path, char *out, size_t cap)
{
    char dv[256], map[64], want[96], got[160];
    // only with a map loaded (a film playing, or a match): asked for a world that is not loaded, the game's lookup
    // makes a default one and stops on an error box (0573: at the main menu, mapname still named the last map)
    if (!DvarLookup("cl_ingame", dv, sizeof(dv)) || strcmp(dv, "cl_ingame bool 1") != 0) {
        _snprintf_s(out, cap, _TRUNCATE, "error no map is loaded: play a film first"); return false;
    }
    if (!DvarLookup("mapname", dv, sizeof(dv))) { _snprintf_s(out, cap, _TRUNCATE, "error no mapname dvar"); return false; }
    const char *q = strchr(dv, '"'), *q2 = q ? strchr(q + 1, '"') : NULL;
    if (!q || !q2 || q2 - q - 1 <= 0 || q2 - q - 1 >= (int)sizeof(map)) { _snprintf_s(out, cap, _TRUNCATE, "error no map is loaded: play a film first"); return false; }
    memcpy(map, q + 1, q2 - q - 1); map[q2 - q - 1] = 0;
    _snprintf_s(want, sizeof(want), _TRUNCATE, "maps/mp/%s.d3dbsp", map);
    BYTE *w = FindAsset(kAssetWorld, want, 0);                // loaded already: never wait on the loader
    AssetName(w, got, sizeof(got));
    if (!w || _stricmp(got, want)) { _snprintf_s(out, cap, _TRUNCATE, "error %s is not loaded: play a film on it first", map); return false; }

    DWORD posSize = 0, idxCount = 0, nWorld = 0; BYTE *gPos = 0, *gIdx = 0, *gSurf = 0;
    if (!SafeCopy(&posSize, w + 0x1AC, 4) || !SafeCopy(&gPos, w + 0x1B0, 4) || !SafeCopy(&idxCount, w + 0x1C4, 4) ||
        !SafeCopy(&gIdx, w + 0x1C8, 4) || !SafeCopy(&nWorld, w + 0x314, 4) || !SafeCopy(&gSurf, w + 0x368, 4) ||
        !gPos || !gIdx || !gSurf || posSize < 36 || posSize > 0x10000000 || idxCount < 3 || idxCount > 0x4000000 ||
        nWorld == 0 || nWorld > 400000) {
        _snprintf_s(out, cap, _TRUNCATE, "error %s's world record does not read as measured (stream %lu bytes, %lu indices, %lu surfaces)",
                    map, posSize, idxCount, nWorld);
        return false;
    }

    // our own copies first: a map unloaded halfway can only spoil the copy, never the game
    BYTE *pos = (BYTE *)malloc(posSize);
    WORD *idx = (WORD *)malloc((size_t)idxCount * 2);
    DWORD *surf = (DWORD *)malloc((size_t)nWorld * 80);
    size_t maxTris = idxCount / 3;
    float *verts = (float *)malloc(idxCount * (size_t)12);           // at most one vertex per index
    DWORD *tris = (DWORD *)malloc(maxTris * 12);
    BYTE *groups = (BYTE *)malloc(maxTris);
    DWORD *stamp = (DWORD *)calloc(65536, 4), *remap = (DWORD *)malloc(65536 * 4);
    bool ok = pos && idx && surf && verts && tris && groups && stamp && remap &&
              SafeCopy(pos, gPos, posSize) && SafeCopy(idx, gIdx, (size_t)idxCount * 2) && SafeCopy(surf, gSurf, (size_t)nWorld * 80);
    DWORD nv = 0, nt = 0, used = 0, hidden = 0;
    float bmin[3] = { FLT_MAX, FLT_MAX, FLT_MAX }, bmax[3] = { -FLT_MAX, -FLT_MAX, -FLT_MAX };
    if (ok) {
        for (DWORD s = 0; s < nWorld; s++) {
            const DWORD *r = surf + s * 20;
            DWORD block = r[3], ntri = r[10] >> 16, first = r[11];
            if (ntri == 0 || block >= posSize || first + (u64)ntri * 3 > idxCount) continue;
            const float *bb = (const float *)(r + 14);
            bool huge = false;
            for (int k = 0; k < 3; k++) if (!(bb[3 + k] - bb[k] < 40000.0f)) huge = true;        // a sky box, or not a surface
            char mat[256] = ""; const char *mp = 0;
            if (r[12] && SafeCopy(&mp, (const void *)(size_t)r[12], 4)) SafeStr(mp, mat, sizeof(mat));
            if (huge || MapShapeHidden(mat)) { hidden++; continue; }
            DWORD hash = 2166136261u;                                                          // FNV-1a: a material keeps its colour
            for (const char *c = mat; *c; c++) { hash ^= (BYTE)tolower((unsigned char)*c); hash *= 16777619u; }
            BYTE group = (BYTE)(hash % 24);
            bool any = false;
            for (DWORD t = 0; t < ntri; t++) {
                DWORD corner[3]; bool bad = false;
                for (int k = 0; k < 3 && !bad; k++) {
                    WORD li = idx[first + t * 3 + k];
                    u64 at = (u64)block + (u64)li * 36;
                    if (at + 12 > posSize) { bad = true; break; }
                    if (stamp[li] != s + 1) {
                        if (nv >= idxCount) { bad = true; break; }                     // never past the allocation (C-12)
                        const float *p = (const float *)(pos + at);
                        if (!_finite(p[0]) || !_finite(p[1]) || !_finite(p[2])) { bad = true; break; }
                        stamp[li] = s + 1; remap[li] = nv;
                        for (int a = 0; a < 3; a++) {
                            verts[nv * 3 + a] = p[a];
                            if (p[a] < bmin[a]) bmin[a] = p[a];
                            if (p[a] > bmax[a]) bmax[a] = p[a];
                        }
                        nv++;
                    }
                    corner[k] = remap[li];
                }
                if (bad || corner[0] == corner[1] || corner[1] == corner[2] || corner[0] == corner[2] || nt >= maxTris) continue;
                tris[nt * 3] = corner[0]; tris[nt * 3 + 1] = corner[1]; tris[nt * 3 + 2] = corner[2];
                groups[nt] = group; nt++; any = true;
            }
            if (any) used++;
        }
    }
    if (ok && nt == 0) { _snprintf_s(out, cap, _TRUNCATE, "error %s: no surfaces to draw (%lu read, %lu left out)", map, nWorld, hidden); ok = false; }
    else if (!ok) _snprintf_s(out, cap, _TRUNCATE, "error %s: the world could not be read (out of memory, or the map was unloaded)", map);
    if (ok) {
        FILE *f = NULL;
        if (fopen_s(&f, path, "wb") || !f) { _snprintf_s(out, cap, _TRUNCATE, "error cannot write %s", path); ok = false; }
        else {
            float b[6] = { bmin[0], bmin[1], bmin[2], bmax[0], bmax[1], bmax[2] };
            ok = fwrite("RVM2", 1, 4, f) == 4 && fwrite(&nv, 4, 1, f) == 1 && fwrite(&nt, 4, 1, f) == 1 && fwrite(b, 4, 6, f) == 6 &&
                 fwrite(verts, 12, nv, f) == nv && fwrite(tris, 12, nt, f) == nt && fwrite(groups, 1, nt, f) == nt;
            if (fclose(f) != 0) ok = false;
            if (!ok) _snprintf_s(out, cap, _TRUNCATE, "error writing %s failed (disk full?)", path);
            else _snprintf_s(out, cap, _TRUNCATE, "ok %s: %lu vertices, %lu triangles from %lu surfaces (%lu left out: sky, clip and the like)",
                             map, nv, nt, used, hidden);
        }
    }
    Log("map shape %s -> %s", map, out);
    free(pos); free(idx); free(surf); free(verts); free(tris); free(groups); free(stamp); free(remap);
    return ok;
}


// ---- fonts: an English game with another font's letters (0.10, 2026-09-30) ---------------
// Mycelium wants Polish BO2 only for its font ("a little nod to the inner circle of my
// community"). BO2 keeps its 9 fonts in <lang>_code_post_gfx_mp together with all 11,300
// lines of menu text, so that file can't be swapped whole. Instead, once the English fonts
// are loaded:
//   1. the letter sheet (image gamefonts_pc_720: 512x1024, one byte a pixel) is repainted
//      with the other font's letters, repacked offline onto a sheet of the same size by
//      tools\zones\make-fontpack.py; UpdateSubresource, on the render thread;
//   2. then each of the 9 Font_s records gets the other font's letter table, kerning, size.
// The texture object and every other record stay as they are. The pack is RipperBridge.font
// beside this plugin; without it nothing happens. Every 5 s the sheet is looked up again
// and repainted, because BO2 may stream the real image in after a placeholder or reload it.
//
// Layouts (T6, read from the zones with tools\zones, 2026-09-30):
//   Font_s   36 bytes: name, pixelHeight, isScalingAllowed, glyphCount, kerningPairsCount,
//                      material, glowMaterial, glyphs, kerningPairs
//   Glyph    24 bytes: letter u16, x0 s8, y0 s8, dx s8, pixelWidth u8, pixelHeight u8, pad,
//                      s0 t0 s1 t1 (floats, 0..1 across the sheet)
//   kerning   8 bytes: first u16, second u16, amount s32
//   GfxImage 80 bytes: texture at +0, width/height u16 at +20/+22, name at +72
#pragma pack(push, 1)
struct T6Glyph { unsigned short letter; signed char x0, y0, dx; unsigned char pw, ph, pad; float s0, t0, s1, t1; };
#pragma pack(pop)
struct T6Font { const char *name; int pixelHeight, isScalingAllowed, glyphCount, kerningCount; void *material, *glowMaterial; T6Glyph *glyphs; void *kerning; };
static_assert(sizeof(T6Glyph) == 24, "Glyph is 24 bytes");
static_assert(sizeof(T6Font) == 36, "Font_s is 36 bytes");
static const int kAssetFont = 0x14, kAssetImage = 0x8;
static const char *kSheetName = "gamefonts_pc_720";

struct FontEntry { char name[64]; int px, scaling, count, kcount; T6Glyph *glyphs; BYTE *kerning; T6Font *target; T6Font saved; };
static struct {
    BYTE *sheet; UINT w, h; int n; FontEntry f[16];
    BYTE *image;                         // the GfxImage record of the letter sheet
    ID3D11Texture2D *tex, *backup;       // the sheet's texture (referenced), and the English copy
    volatile LONG want;                  // 1 apply, 2 restore: set by the worker or the pipe, done in Present
    bool fontsOn;                        // the 9 records carry the pack's tables
    double lastPaint;
    LONG paints;
    char status[400];
} g_font;

static void FontStatus(const char *fmt, ...)
{
    char s[sizeof(g_font.status)];
    va_list a; va_start(a, fmt); _vsnprintf_s(s, sizeof(s), _TRUNCATE, fmt, a); va_end(a);
    if (!strcmp(s, g_font.status)) return;                   // logged once, not every frame it stays true
    strcpy_s(g_font.status, s);
    Log("font: %s", s);
}

static bool FontLoadPack(const char *path)
{
    FILE *fp = NULL;
    if (fopen_s(&fp, path, "rb") || !fp) { FontStatus("cannot open %s", path); return false; }
    char magic[8]; DWORD hdr[4]; bool ok = false;
    if (fread(magic, 1, 8, fp) == 8 && !memcmp(magic, "RBFONT01", 8) && fread(hdr, 4, 4, fp) == 4 &&
        hdr[0] && hdr[1] && hdr[0] * hdr[1] <= (16u << 20) && hdr[2] >= 1 && hdr[2] <= 16) {
        g_font.w = hdr[0]; g_font.h = hdr[1]; g_font.n = (int)hdr[2];
        HANDLE heap = GetProcessHeap();
        g_font.sheet = (BYTE *)HeapAlloc(heap, 0, g_font.w * g_font.h);
        ok = g_font.sheet && fread(g_font.sheet, 1, g_font.w * g_font.h, fp) == g_font.w * g_font.h;
        for (int i = 0; ok && i < g_font.n; i++) {
            FontEntry &e = g_font.f[i]; int v[4];
            ok = fread(e.name, 1, 64, fp) == 64 && fread(v, 4, 4, fp) == 4 && v[2] > 0 && v[2] < 5000 && v[3] >= 0 && v[3] < 100000;
            if (!ok) break;
            e.name[63] = 0; e.px = v[0]; e.scaling = v[1]; e.count = v[2]; e.kcount = v[3];
            e.glyphs = (T6Glyph *)HeapAlloc(heap, 0, sizeof(T6Glyph) * e.count);   // never freed: the game keeps pointing at them
            e.kerning = (BYTE *)HeapAlloc(heap, 0, 8 * (size_t)e.kcount + 8);
            ok = e.glyphs && e.kerning && fread(e.glyphs, sizeof(T6Glyph), e.count, fp) == (size_t)e.count &&
                 fread(e.kerning, 8, e.kcount, fp) == (size_t)e.kcount;
        }
    }
    fclose(fp);
    if (!ok) FontStatus("%s is not a usable font pack", path);
    else Log("font: pack %s: sheet %ux%u, %d fonts", path, g_font.w, g_font.h, g_font.n);
    return ok;
}

// A COM object's first field is a vtable whose first entry (QueryInterface) is code in a
// loaded image. Checked before calling anything through a pointer we only read from memory.
static bool LooksLikeCom(void *p)
{
    void *vt = NULL, *qi = NULL;
    if ((size_t)p < 0x10000 || !SafeCopy(&vt, p, 4) || (size_t)vt < 0x10000 || !SafeCopy(&qi, vt, 4)) return false;
    MEMORY_BASIC_INFORMATION mi;
    if (!VirtualQuery(qi, &mi, sizeof(mi)) || mi.Type != MEM_IMAGE) return false;
    return (mi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
}

// The texture behind a pointer that may be a texture, a shader resource view, or neither.
// The result carries a reference the caller releases.
static ID3D11Texture2D *TextureFrom(void *p)
{
    if (!LooksLikeCom(p)) return NULL;
    IUnknown *u = (IUnknown *)p; ID3D11Texture2D *t = NULL; ID3D11ShaderResourceView *v = NULL;
    __try {
        if (SUCCEEDED(u->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&t))) return t;
        if (SUCCEEDED(u->QueryInterface(__uuidof(ID3D11ShaderResourceView), (void **)&v))) {
            ID3D11Resource *r = NULL; v->GetResource(&r); v->Release();
            if (r) { r->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&t); r->Release(); }
            return t;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return NULL;
}

// The sheet's texture, from its GfxImage: the pointer at +0 itself, or one of the first
// four fields of what it points to. Only a 1-byte-a-pixel texture of the pack's size counts.
static ID3D11Texture2D *SheetTexture(char *how, size_t cap)
{
    void *p0 = NULL;
    if (!g_font.image || !SafeCopy(&p0, g_font.image, 4) || !p0) { _snprintf_s(how, cap, _TRUNCATE, "no texture yet"); return NULL; }
    for (int k = -1; k < 4; k++) {
        void *p = p0;
        if (k >= 0 && !SafeCopy(&p, (BYTE *)p0 + 4 * k, 4)) break;
        ID3D11Texture2D *t = TextureFrom(p);
        if (!t) continue;
        D3D11_TEXTURE2D_DESC d; t->GetDesc(&d);
        bool fits = d.Width == g_font.w && d.Height == g_font.h && d.ArraySize == 1 &&
                    (d.Format == DXGI_FORMAT_A8_UNORM || d.Format == DXGI_FORMAT_R8_UNORM) && d.Usage != D3D11_USAGE_IMMUTABLE;
        _snprintf_s(how, cap, _TRUNCATE, "image+0 %p %s %p: texture %ux%u format %d mips %u usage %d bind 0x%X%s",
            p0, k < 0 ? "itself" : "field", p, d.Width, d.Height, (int)d.Format, d.MipLevels, (int)d.Usage, d.BindFlags,
            fits ? "" : " - not the expected sheet");
        if (fits) return t;
        t->Release();
    }
    if (!how[0]) _snprintf_s(how, cap, _TRUNCATE, "image+0 %p is no texture", p0);
    return NULL;
}

// The GfxImage of the letter sheet: the engine's lookup first; otherwise its inline name,
// searched near the fonts' material, with the record 80 bytes before it.
static bool IsSheetImage(BYTE *img)
{
    const char *np = NULL; char nm[64]; WORD wh[2];
    return img && SafeCopy(&np, img + 72, 4) && SafeStr(np, nm, sizeof(nm)) && !strcmp(nm, kSheetName) &&
           SafeCopy(wh, img + 20, 4) && wh[0] == g_font.w && wh[1] == g_font.h;
}
static BYTE *FindSheetImage(BYTE *around)                  // ("near" is a windows.h macro)
{
    BYTE *img = FindAsset(kAssetImage, kSheetName);
    if (IsSheetImage(img)) return img;
    if (!around) return NULL;
    size_t len = strlen(kSheetName) + 1;
    for (BYTE *at = around > (BYTE *)(1 << 20) ? around - (1 << 20) : around; at < around + (1 << 20); ) {
        MEMORY_BASIC_INFORMATION mi;
        if (!VirtualQuery(at, &mi, sizeof(mi))) break;
        BYTE *end = (BYTE *)mi.BaseAddress + mi.RegionSize;
        bool readable = mi.State == MEM_COMMIT && !(mi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) &&
                        (mi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE));
        if (readable) {
            __try {
                for (BYTE *s = at; s + len <= end; s++)
                    if (*s == 'g' && !memcmp(s, kSheetName, len) && IsSheetImage(s - 80)) return s - 80;
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        }
        at = end;
    }
    return NULL;
}

// Worker side: the 9 fonts and the sheet image, once the game has loaded them.
static bool FontFind()
{
    for (int i = 0; i < g_font.n; i++) {
        FontEntry &e = g_font.f[i];
        T6Font *t = (T6Font *)FindAsset(kAssetFont, e.name);
        char nm[64]; AssetName((BYTE *)t, nm, sizeof(nm));
        if (!t || _stricmp(nm, e.name)) return false;
        T6Font c; if (!SafeCopy(&c, t, sizeof(c)) || c.glyphCount < 32 || c.glyphCount > 5000 || !c.glyphs) return false;
        e.target = t;
        if (!g_font.fontsOn) e.saved = c;
    }
    if (!g_font.image) g_font.image = FindSheetImage((BYTE *)g_font.f[0].saved.material);
    return g_font.image != NULL;
}

static void FontWrite(T6Font *f, T6Glyph *glyphs, int count, void *kern, int kcount, int px, int scaling)
{
    DWORD old;
    if (!VirtualProtect(f, sizeof(*f), PAGE_READWRITE, &old)) return;
    __try {
        // the table before a larger count (and a smaller count before its table), so a reader
        // between the two writes never pairs a count with a table shorter than it
        if (count >= f->glyphCount) { f->glyphs = glyphs; MemoryBarrier(); f->glyphCount = count; }
        else { f->glyphCount = count; MemoryBarrier(); f->glyphs = glyphs; }
        if (kcount >= f->kerningCount) { f->kerning = kern; MemoryBarrier(); f->kerningCount = kcount; }
        else { f->kerningCount = kcount; MemoryBarrier(); f->kerning = kern; }
        f->isScalingAllowed = scaling;
        f->pixelHeight = px;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    VirtualProtect(f, sizeof(*f), old, &old);
}

// Render thread: (re)paint the sheet into whatever texture the image holds now, every mip.
static bool FontPaint()
{
    char how[300] = "";
    ID3D11Texture2D *t = SheetTexture(how, sizeof(how));
    if (!t) { if (g_font.paints == 0) FontStatus("waiting for the sheet: %s", how); return false; }
    ID3D11Device *dev = NULL; ID3D11DeviceContext *ctx = NULL;
    t->GetDevice(&dev); if (dev) dev->GetImmediateContext(&ctx);
    if (!ctx) { if (dev) dev->Release(); t->Release(); return false; }
    D3D11_TEXTURE2D_DESC d; t->GetDesc(&d);
    if (t != g_font.tex) {                                   // a texture we have not painted: keep its English first
        if (g_font.backup) { g_font.backup->Release(); g_font.backup = NULL; }
        D3D11_TEXTURE2D_DESC b = d; b.Usage = D3D11_USAGE_DEFAULT; b.BindFlags = 0; b.CPUAccessFlags = 0; b.MiscFlags = 0;
        if (SUCCEEDED(dev->CreateTexture2D(&b, NULL, &g_font.backup))) ctx->CopyResource(g_font.backup, t);
        if (g_font.tex) g_font.tex->Release();
        g_font.tex = t; t->AddRef();
        Log("font: sheet texture %s", how);
    }
    BYTE *level = g_font.sheet; UINT lw = g_font.w, lh = g_font.h;
    for (UINT m = 0; m < d.MipLevels; m++) {
        ctx->UpdateSubresource(t, D3D11CalcSubresource(m, 0, d.MipLevels), NULL, level, lw, 0);
        if (m + 1 == d.MipLevels) break;
        UINT nw = lw > 1 ? lw / 2 : 1, nh = lh > 1 ? lh / 2 : 1;
        BYTE *next = (BYTE *)malloc(nw * nh);
        if (!next) break;
        for (UINT y = 0; y < nh; y++) for (UINT x = 0; x < nw; x++) {
            UINT x0 = x * 2, x1 = x0 + 1 < lw ? x0 + 1 : x0, y0 = y * 2, y1 = y0 + 1 < lh ? y0 + 1 : y0;
            next[y * nw + x] = (BYTE)((level[y0 * lw + x0] + level[y0 * lw + x1] + level[y1 * lw + x0] + level[y1 * lw + x1] + 2) / 4);
        }
        if (level != g_font.sheet) free(level);
        level = next; lw = nw; lh = nh;
    }
    if (level != g_font.sheet) free(level);
    InterlockedIncrement(&g_font.paints);
    g_font.lastPaint = NowMs();
    ctx->Release(); dev->Release(); t->Release();
    return true;
}

static void FontMaybe()
{
    LONG want = g_font.want;
    if (want == 1) {
        if (!FontPaint()) return;                            // the fonts change only once the sheet has
        for (int i = 0; i < g_font.n; i++) {
            FontEntry &e = g_font.f[i];
            if (e.target) FontWrite(e.target, e.glyphs, e.count, e.kerning, e.kcount, e.px, e.scaling);
        }
        g_font.fontsOn = true;
        InterlockedCompareExchange(&g_font.want, 0, 1);
        FontStatus("on: %d fonts and the %ux%u sheet", g_font.n, g_font.w, g_font.h);
    } else if (want == 2) {
        for (int i = 0; i < g_font.n; i++) {
            FontEntry &e = g_font.f[i];
            if (e.target) FontWrite(e.target, e.saved.glyphs, e.saved.glyphCount, e.saved.kerning, e.saved.kerningCount, e.saved.pixelHeight, e.saved.isScalingAllowed);
        }
        g_font.fontsOn = false;
        if (g_font.tex && g_font.backup) {
            ID3D11Device *dev = NULL; ID3D11DeviceContext *ctx = NULL;
            g_font.tex->GetDevice(&dev); if (dev) dev->GetImmediateContext(&ctx);
            if (ctx) { ctx->CopyResource(g_font.tex, g_font.backup); ctx->Release(); }
            if (dev) dev->Release();
        }
        InterlockedCompareExchange(&g_font.want, 0, 2);
        FontStatus("off: English fonts and sheet put back");
    } else if (g_font.fontsOn && NowMs() - g_font.lastPaint > 5000) {
        FontPaint();                                         // the sheet may have been streamed in again
    }
}

// The engine's lookup must never be asked for a font before the fonts are in. A missing
// font falls back to the default font, fonts/720/consolefont, and before code_post_gfx is
// loaded that is missing too: the game dies with "Could not load default asset
// 'fonts/720/consolefont' for asset type 'font'" (0.10 in Mycelium's game, 2026-09-30 22:29;
// the lookup came from this thread while the game was still starting). So the worker
// first waits, without the engine, for the console font's own record to be in memory: its
// name string with the 36-byte record before it pointing at it - the zone's copy, there
// once code_post_gfx has streamed. Only then, and 3 s later, is the engine asked.
static bool FontZoneLoaded()
{
    static const char kName[] = "fonts/720/consoleFont";
    SYSTEM_INFO si; GetSystemInfo(&si);
    for (BYTE *at = (BYTE *)si.lpMinimumApplicationAddress; at < (BYTE *)si.lpMaximumApplicationAddress; ) {
        MEMORY_BASIC_INFORMATION mi;
        if (!VirtualQuery(at, &mi, sizeof(mi))) break;
        BYTE *end = (BYTE *)mi.BaseAddress + mi.RegionSize;
        if (mi.State == MEM_COMMIT && mi.Type == MEM_PRIVATE && !(mi.Protect & PAGE_GUARD) &&
            (mi.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE)) && mi.RegionSize > 36 + sizeof(kName)) {
            BYTE *lo = (BYTE *)mi.BaseAddress + 36, *hi = end - sizeof(kName);
            __try {
                for (BYTE *s = lo; s <= hi; s++) {
                    s = (BYTE *)memchr(s, 'f', (size_t)(hi - s) + 1);
                    if (!s) break;
                    if (memcmp(s, kName, sizeof(kName)) || *(BYTE **)(s - 36) != s) continue;
                    int px = *(int *)(s - 32), count = *(int *)(s - 24);
                    if (px >= 8 && px <= 128 && count >= 32 && count <= 5000) return true;
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        }
        at = end;
    }
    return false;
}

// The render thread, without Present (0.10.1). In Mycelium's real game Steam's overlay is
// injected, and with 0.10's Present hook and its throwaway window and swap chain the game
// died 21 s in: a stack overflow in gameoverlayrenderer.dll, then an access violation in
// USER32 (2026-09-30 22:35; the 22:29 start ended in USER32 too). So the font is painted from
// ID3D11DeviceContext::OMSetRenderTargets instead, which the game calls on its immediate
// context every frame. The vtable is the game's own: the sheet texture's device, its
// immediate context. (A bare device of our own gave a different context vtable - its hook
// never ran in 0.10.1, 22:49 - most likely the game's device was made with other flags.)
typedef void (STDMETHODCALLTYPE *OMSetRenderTargets_t)(ID3D11DeviceContext *, UINT, ID3D11RenderTargetView *const *, ID3D11DepthStencilView *);
static OMSetRenderTargets_t g_omSet;
static void **g_omSlot;

static void GfxOnBind(ID3D11DeviceContext *ctx, UINT n, ID3D11RenderTargetView *const *rtv, ID3D11DepthStencilView *dsv, const char *how);
static void GfxAfterBind(ID3D11DeviceContext *ctx);

static void STDMETHODCALLTYPE HookOMSetRenderTargets(ID3D11DeviceContext *ctx, UINT n, ID3D11RenderTargetView *const *rtv, ID3D11DepthStencilView *dsv)
{
    if (g_font.want || g_font.fontsOn) {
        __try { if (ctx->GetType() == D3D11_DEVICE_CONTEXT_IMMEDIATE) FontMaybe(); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    GfxOnBind(ctx, n, rtv, dsv, "bind");             // dev23: the gfx probe and depth (nothing unless one is asked for)
    g_omSet(ctx, n, rtv, dsv);
    GfxAfterBind(ctx);
}

static bool InstallContextHook()
{
    if (g_omSlot) return true;
    char how[300] = "";
    ID3D11Texture2D *t = SheetTexture(how, sizeof(how));    // the game's texture, so the game's device
    if (!t) { FontStatus("waiting for the sheet texture: %s", how); return false; }
    ID3D11Device *dev = NULL; ID3D11DeviceContext *ctx = NULL;
    t->GetDevice(&dev); if (dev) dev->GetImmediateContext(&ctx);
    t->Release();
    if (!ctx) { Log("font: no immediate context from the sheet's device"); if (dev) dev->Release(); return false; }
    void **vtable = *(void ***)ctx;
    void **slot = &vtable[33];                              // ID3D11DeviceContext::OMSetRenderTargets
    DWORD old;
    if (VirtualProtect(slot, sizeof(void *), PAGE_EXECUTE_READWRITE, &old)) {
        g_omSet = (OMSetRenderTargets_t)*slot;
        InterlockedExchangePointer(slot, (void *)&HookOMSetRenderTargets);
        VirtualProtect(slot, sizeof(void *), old, &old);
        g_omSlot = slot;
    }
    Log("font: render-thread hook on the game's immediate context %p: vtable %p slot %p original %p hooked %d",
        ctx, vtable, slot, (void *)g_omSet, g_omSlot ? 1 : 0);
    ctx->Release(); dev->Release();
    return g_omSlot != NULL;
}

// ---- the game's own frame from inside (dev23, 2026-10-17): toward compositing passes --------------------------------
// Mycelium (10-17): "We need to build the Compositing Passes from the game itself". The game's own debug dvars for passes
// are dead in the retail renderer (r_debugShader, r_colorMap, r_fullbright: 2026-10-02, 0213). So they come from
// Direct3D 11 itself,
// on the game's immediate context (the font's way in: the swap chain's device, its class vtable).
//   "gfx probe <file>"   one frame's render-target binds, clears and draws, written to a file (the shape of a frame)
//   "gfx depth <file>"   the depth buffer that took the most draws in a frame, at Present, as a 16-bit PGM (raw depth)
//                        and <file>.view.pgm (8-bit, stretched between the nearest and farthest drawn values, to look at)
// Everything runs on the render thread, at its own Present; the pipe only asks and waits.
typedef void (STDMETHODCALLTYPE *ClearRTV_t)(ID3D11DeviceContext *, ID3D11RenderTargetView *, const FLOAT[4]);
typedef void (STDMETHODCALLTYPE *ClearDSV_t)(ID3D11DeviceContext *, ID3D11DepthStencilView *, UINT, FLOAT, UINT8);
typedef void (STDMETHODCALLTYPE *Draw_t)(ID3D11DeviceContext *, UINT, UINT);
typedef void (STDMETHODCALLTYPE *DrawIndexed_t)(ID3D11DeviceContext *, UINT, UINT, INT);
typedef void (STDMETHODCALLTYPE *DrawIndexedInst_t)(ID3D11DeviceContext *, UINT, UINT, UINT, INT, UINT);
typedef void (STDMETHODCALLTYPE *DrawInst_t)(ID3D11DeviceContext *, UINT, UINT, UINT, UINT);
typedef void (STDMETHODCALLTYPE *OMSetRTUav_t)(ID3D11DeviceContext *, UINT, ID3D11RenderTargetView *const *, ID3D11DepthStencilView *,
                                               UINT, UINT, ID3D11UnorderedAccessView *const *, const UINT *);
static ClearRTV_t g_clearRtv; static ClearDSV_t g_clearDsv; static Draw_t g_draw; static DrawIndexed_t g_drawIdx;
static DrawIndexedInst_t g_drawIdxInst; static DrawInst_t g_drawInst; static OMSetRTUav_t g_omSetUav;

struct GfxDsv { ID3D11DepthStencilView *v; LONG draws; };
static struct {
    volatile LONG want;                  // 1 probe, 2 depth: taken at the next Present
    int phase;                           // 0 idle, 1 inside the frame being looked at (from one Present to the next)
    LONG op; bool hooked; char why[200];
    ID3D11DeviceContext *ctx;            // the game's immediate context (only its calls count)
    char path[MAX_PATH]; FILE *f; LONG events;
    LONG draws, drawsIdx;                // draws since the last logged event
    void *seen[512]; int seenN;          // resources described already in this probe
    GfxDsv dsv[64]; int dsvN; int bound; // per depth view: draws while it was bound, this frame
    char result[400]; HANDLE done;
    // dev23b: draws counted on every context (0503: none came on the immediate one), command lists executed
    struct { void *c; LONG n; } ctxs[8]; int ctxN; LONG execs, otherBinds;
    // dev23b "gfx grab <resource> [k] <file>": that render target or depth buffer copied the k-th time the game lets go
    // of it in a frame (multisampled colour resolved first), written out at Present
    void *grab; int grabK, grabLets; bool grabBound, grabNow; ID3D11Resource *grabRes;
    ID3D11Texture2D *grabSt; D3D11_TEXTURE2D_DESC grabDesc; DXGI_FORMAT grabFmt; char grabWhy[200];
    BYTE *grabBuf; UINT grabPitch;       // the copy, read at Present; written to files by the pipe thread
} g_gfx;

static bool GfxHookSlot(void **vt, int i, void *hook, void **orig)
{
    DWORD old;
    if (vt[i] == hook) return true;
    if (!VirtualProtect(&vt[i], sizeof(void *), PAGE_EXECUTE_READWRITE, &old)) return false;
    *orig = vt[i];
    InterlockedExchangePointer(&vt[i], hook);
    VirtualProtect(&vt[i], sizeof(void *), old, &old);
    return true;
}

static void GfxDesc(ID3D11View *v, char *s, size_t cap)
{
    *s = 0;
    if (!v) { strcpy_s(s, cap, "-"); return; }
    ID3D11Resource *r = NULL; v->GetResource(&r);
    if (!r) { strcpy_s(s, cap, "?"); return; }
    bool seen = false;
    for (int i = 0; i < g_gfx.seenN; i++) if (g_gfx.seen[i] == r) seen = true;
    ID3D11Texture2D *t = NULL;
    if (!seen && SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&t)) && t) {
        D3D11_TEXTURE2D_DESC d; t->GetDesc(&d); t->Release();
        _snprintf_s(s, cap, _TRUNCATE, "%p[%ux%u fmt %u x%u bind %X mips %u arr %u]", r, d.Width, d.Height, (UINT)d.Format, d.SampleDesc.Count, d.BindFlags, d.MipLevels, d.ArraySize);
        if (g_gfx.seenN < 512) g_gfx.seen[g_gfx.seenN++] = r;
    }
    else _snprintf_s(s, cap, _TRUNCATE, "%p", r);
    r->Release();
}

static void GfxFlushDraws()
{
    if (g_gfx.f && (g_gfx.draws || g_gfx.drawsIdx)) { fprintf(g_gfx.f, "  draws %ld (indexed %ld)\n", g_gfx.draws + g_gfx.drawsIdx, g_gfx.drawsIdx); g_gfx.events++; }
    g_gfx.draws = g_gfx.drawsIdx = 0;
}

static void DepOnBind(UINT n, ID3D11RenderTargetView *const *rtv, ID3D11DepthStencilView *dsv);
static void DepAfterBind();
static void StOnBind(ID3D11DepthStencilView *dsv);
static void FogOnBind(UINT n, ID3D11RenderTargetView *const *rtv);
static void FogAfterBind(ID3D11DeviceContext *ctx);
static void FogPresentEnd();
static void FogPresent(IDXGISwapChain *sc);
static void StAfterBind(ID3D11DeviceContext *ctx);
static void StProbeStart();
static void StProbeEnd(FILE *f);
static volatile LONG g_gfxInstallWant;

static void GfxOnBind(ID3D11DeviceContext *ctx, UINT n, ID3D11RenderTargetView *const *rtv, ID3D11DepthStencilView *dsv, const char *how)
{
    if (ctx == g_gfx.ctx) { DepOnBind(n, rtv, dsv); StOnBind(dsv); FogOnBind(n, rtv); }   // dev23d depth/plate; dev25 the scene's state; dev31 the fog
    if (!g_gfx.phase) return;
    if (ctx != g_gfx.ctx) { InterlockedIncrement(&g_gfx.otherBinds); return; }
    if (g_gfx.op == 3 && g_gfx.grab && !g_gfx.grabSt) __try {
        bool in = false;
        for (UINT i = 0; i <= n && i <= 8 && !in; i++) {
            ID3D11View *v = i < n ? (ID3D11View *)(rtv ? rtv[i] : NULL) : (ID3D11View *)dsv;
            if (!v) continue;
            ID3D11Resource *r = NULL; v->GetResource(&r);
            if (r == g_gfx.grab) { in = true; if (!g_gfx.grabRes) { g_gfx.grabRes = r; r->AddRef(); } }
            if (r) r->Release();
        }
        if (g_gfx.grabBound && !in && ++g_gfx.grabLets == g_gfx.grabK) g_gfx.grabNow = true;
        g_gfx.grabBound = in;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try {
        int at = -1;
        if (dsv) {
            for (int i = 0; i < g_gfx.dsvN; i++) if (g_gfx.dsv[i].v == dsv) at = i;
            if (at < 0 && g_gfx.dsvN < 64) { at = g_gfx.dsvN++; g_gfx.dsv[at].v = dsv; g_gfx.dsv[at].draws = 0; }
        }
        g_gfx.bound = at;
        if (!g_gfx.f) return;
        GfxFlushDraws();
        char d[200], r[200];
        GfxDesc(dsv, d, sizeof(d));
        fprintf(g_gfx.f, "%s %u rt:", how, n);
        for (UINT i = 0; i < n && i < 8; i++) { GfxDesc(rtv ? rtv[i] : NULL, r, sizeof(r)); fprintf(g_gfx.f, " %s", r); }
        fprintf(g_gfx.f, " | depth %s\n", d);
        g_gfx.events++;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static void GfxCountDraw(ID3D11DeviceContext *ctx, bool indexed)
{
    if (!g_gfx.phase) return;
    int c = 0; while (c < g_gfx.ctxN && g_gfx.ctxs[c].c != ctx) c++;
    if (c == g_gfx.ctxN && c < 8) { g_gfx.ctxs[c].c = ctx; g_gfx.ctxs[c].n = 0; g_gfx.ctxN++; }
    if (c < 8) InterlockedIncrement(&g_gfx.ctxs[c].n);
    if (ctx != g_gfx.ctx) return;
    if (indexed) g_gfx.drawsIdx++; else g_gfx.draws++;
    if (g_gfx.bound >= 0) g_gfx.dsv[g_gfx.bound].draws++;
}

static void STDMETHODCALLTYPE GfxClearRtv(ID3D11DeviceContext *ctx, ID3D11RenderTargetView *v, const FLOAT c[4])
{
    if (g_gfx.phase && g_gfx.f && ctx == g_gfx.ctx) __try {
        GfxFlushDraws(); char d[200]; GfxDesc(v, d, sizeof(d));
        fprintf(g_gfx.f, "clear rt %s to %.3f %.3f %.3f %.3f\n", d, c[0], c[1], c[2], c[3]); g_gfx.events++;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    g_clearRtv(ctx, v, c);
}
static void STDMETHODCALLTYPE GfxClearDsv(ID3D11DeviceContext *ctx, ID3D11DepthStencilView *v, UINT flags, FLOAT depth, UINT8 stencil)
{
    if (g_gfx.phase && g_gfx.f && ctx == g_gfx.ctx) __try {
        GfxFlushDraws(); char d[200]; GfxDesc(v, d, sizeof(d));
        fprintf(g_gfx.f, "clear depth %s flags %u to %.4f stencil %u\n", d, flags, depth, (UINT)stencil); g_gfx.events++;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    g_clearDsv(ctx, v, flags, depth, stencil);
}
static void STDMETHODCALLTYPE GfxDraw(ID3D11DeviceContext *ctx, UINT n, UINT s) { GfxCountDraw(ctx, false); g_draw(ctx, n, s); }
static void STDMETHODCALLTYPE GfxDrawIdx(ID3D11DeviceContext *ctx, UINT n, UINT s, INT b) { GfxCountDraw(ctx, true); g_drawIdx(ctx, n, s, b); }
static void STDMETHODCALLTYPE GfxDrawIdxInst(ID3D11DeviceContext *ctx, UINT n, UINT k, UINT s, INT b, UINT si) { GfxCountDraw(ctx, true); g_drawIdxInst(ctx, n, k, s, b, si); }
static void STDMETHODCALLTYPE GfxDrawInst(ID3D11DeviceContext *ctx, UINT n, UINT k, UINT s, UINT si) { GfxCountDraw(ctx, false); g_drawInst(ctx, n, k, s, si); }
static void STDMETHODCALLTYPE GfxOMSetUav(ID3D11DeviceContext *ctx, UINT n, ID3D11RenderTargetView *const *rtv, ID3D11DepthStencilView *dsv,
                                          UINT us, UINT un, ID3D11UnorderedAccessView *const *uav, const UINT *ic)
{
    if (n != D3D11_KEEP_RENDER_TARGETS_AND_DEPTH_STENCIL) GfxOnBind(ctx, n, rtv, dsv, "bind+uav");
    g_omSetUav(ctx, n, rtv, dsv, us, un, uav, ic);
    GfxAfterBind(ctx);
}
typedef void (STDMETHODCALLTYPE *ExecCL_t)(ID3D11DeviceContext *, ID3D11CommandList *, BOOL);
static ExecCL_t g_execCl;
static void STDMETHODCALLTYPE GfxExecCl(ID3D11DeviceContext *ctx, ID3D11CommandList *cl, BOOL keep)
{
    if (g_gfx.phase) {
        InterlockedIncrement(&g_gfx.execs);
        if (g_gfx.f && ctx == g_gfx.ctx) __try { GfxFlushDraws(); fprintf(g_gfx.f, "execute command list %p (keep state %d)\n", cl, keep); g_gfx.events++; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    g_execCl(ctx, cl, keep);
}

// the typeless group of a format, for a staging copy (CopyResource wants the same group)
static DXGI_FORMAT GfxGroup(DXGI_FORMAT f)
{
    switch (f) {
    case DXGI_FORMAT_D24_UNORM_S8_UINT: case DXGI_FORMAT_R24G8_TYPELESS: case DXGI_FORMAT_R24_UNORM_X8_TYPELESS: return DXGI_FORMAT_R24G8_TYPELESS;
    case DXGI_FORMAT_D32_FLOAT: case DXGI_FORMAT_R32_TYPELESS: case DXGI_FORMAT_R32_FLOAT: return DXGI_FORMAT_R32_TYPELESS;
    case DXGI_FORMAT_D32_FLOAT_S8X24_UINT: case DXGI_FORMAT_R32G8X24_TYPELESS: case DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS: return DXGI_FORMAT_R32G8X24_TYPELESS;
    case DXGI_FORMAT_D16_UNORM: case DXGI_FORMAT_R16_TYPELESS: case DXGI_FORMAT_R16_UNORM: return DXGI_FORMAT_R16_TYPELESS;
    default: return f;
    }
}

// render thread, right after the bind that let the grab's target go: a copy of it (mip 0, slice 0), read at Present
static void GfxAfterBind(ID3D11DeviceContext *ctx)
{
    if (ctx == g_gfx.ctx) { DepAfterBind(); StAfterBind(ctx); FogAfterBind(ctx); }
    if (!g_gfx.grabNow || ctx != g_gfx.ctx) return;
    g_gfx.grabNow = false;
    __try {
        ID3D11Texture2D *tex = NULL;
        if (!g_gfx.grabRes || FAILED(g_gfx.grabRes->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&tex)) || !tex) { strcpy_s(g_gfx.grabWhy, sizeof(g_gfx.grabWhy), "not a 2D texture"); return; }
        D3D11_TEXTURE2D_DESC d; tex->GetDesc(&d);
        ID3D11Device *dev = NULL; tex->GetDevice(&dev);
        ID3D11Texture2D *src = tex, *tmp = NULL;
        DXGI_FORMAT typed = d.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS ? DXGI_FORMAT_R8G8B8A8_UNORM : d.Format == DXGI_FORMAT_R32_TYPELESS ? DXGI_FORMAT_R32_FLOAT
                          : d.Format == DXGI_FORMAT_R16G16B16A16_TYPELESS ? DXGI_FORMAT_R16G16B16A16_FLOAT : d.Format;
        if (d.SampleDesc.Count > 1) {
            if (d.BindFlags & D3D11_BIND_DEPTH_STENCIL) { _snprintf_s(g_gfx.grabWhy, sizeof(g_gfx.grabWhy), _TRUNCATE, "a multisampled depth buffer (x%u) cannot be resolved", d.SampleDesc.Count); tex->Release(); dev->Release(); return; }
            D3D11_TEXTURE2D_DESC td = d; td.SampleDesc.Count = 1; td.SampleDesc.Quality = 0; td.MipLevels = 1; td.ArraySize = 1; td.Format = typed;
            td.BindFlags = 0; td.MiscFlags = 0; td.Usage = D3D11_USAGE_DEFAULT; td.CPUAccessFlags = 0;
            if (FAILED(dev->CreateTexture2D(&td, NULL, &tmp)) || !tmp) { strcpy_s(g_gfx.grabWhy, sizeof(g_gfx.grabWhy), "no texture to resolve into"); tex->Release(); dev->Release(); return; }
            g_gfx.ctx->ResolveSubresource(tmp, 0, tex, 0, typed);
            src = tmp;
        }
        D3D11_TEXTURE2D_DESC sd = d; sd.SampleDesc.Count = 1; sd.SampleDesc.Quality = 0; sd.MipLevels = 1; sd.ArraySize = 1;
        sd.Format = tmp ? typed : GfxGroup(d.Format); sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = 0;
        ID3D11Texture2D *st = NULL;
        HRESULT hr = dev->CreateTexture2D(&sd, NULL, &st);
        if (FAILED(hr) || !st) _snprintf_s(g_gfx.grabWhy, sizeof(g_gfx.grabWhy), _TRUNCATE, "no staging texture of format %u (0x%08lX)", (UINT)sd.Format, hr);
        else {
            g_gfx.ctx->CopySubresourceRegion(st, 0, 0, 0, 0, src, 0, NULL);
            g_gfx.grabSt = st; g_gfx.grabDesc = d; g_gfx.grabFmt = sd.Format;
        }
        if (tmp) tmp->Release();
        tex->Release(); dev->Release();
    } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(g_gfx.grabWhy, sizeof(g_gfx.grabWhy), "a fault copying it (caught)"); }
}

static float GfxHalf(WORD h)
{
    DWORD s = (h >> 15) & 1, e = (h >> 10) & 31, m = h & 1023, f;
    if (e == 0) { if (!m) f = s << 31; else { while (!(m & 1024)) { m <<= 1; e--; } e++; m &= 1023; f = (s << 31) | ((e + 112) << 23) | (m << 13); } }
    else if (e == 31) f = (s << 31) | 0x7F800000 | (m << 13);
    else f = (s << 31) | ((e + 112) << 23) | (m << 13);
    return *(float *)&f;
}

// render thread, at Present: the grabbed copy read into memory, nothing more (0504: writing a 4096x8192 shadow map's
// files here held the frame over 5 s, the pipe gave up, and the next grabs crossed)
static bool GfxGrabRead(char *r, size_t cap)
{
    D3D11_MAPPED_SUBRESOURCE m;
    HRESULT hr = g_gfx.ctx->Map(g_gfx.grabSt, 0, D3D11_MAP_READ, 0, &m);
    if (FAILED(hr)) { _snprintf_s(r, cap, _TRUNCATE, "error the copy could not be read (0x%08lX)", hr); return false; }
    size_t n = (size_t)m.RowPitch * g_gfx.grabDesc.Height;
    g_gfx.grabBuf = (BYTE *)malloc(n);
    if (g_gfx.grabBuf) memcpy(g_gfx.grabBuf, m.pData, n);
    g_gfx.grabPitch = m.RowPitch;
    g_gfx.ctx->Unmap(g_gfx.grabSt, 0);
    if (!g_gfx.grabBuf) { _snprintf_s(r, cap, _TRUNCATE, "error out of memory for a %u-byte copy", (UINT)n); return false; }
    return true;
}

// pipe thread: the grabbed copy written out. Float targets: a PFM (the values as they are) and an 8-bit .view.pgm
// stretched between the smallest and largest finite values; colour: a PPM
static void GfxGrabWrite(char *r, size_t cap)
{
    const BYTE *data = g_gfx.grabBuf; UINT pitch = g_gfx.grabPitch;
    UINT w = g_gfx.grabDesc.Width, h = g_gfx.grabDesc.Height; DXGI_FORMAT f = g_gfx.grabFmt;
    bool flt = f == DXGI_FORMAT_R32_TYPELESS || f == DXGI_FORMAT_R32_FLOAT || f == DXGI_FORMAT_R32G8X24_TYPELESS || f == DXGI_FORMAT_R24G8_TYPELESS || f == DXGI_FORMAT_R16_TYPELESS;
    bool rgb8 = f == DXGI_FORMAT_R8G8B8A8_TYPELESS || f == DXGI_FORMAT_R8G8B8A8_UNORM || f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || f == DXGI_FORMAT_B8G8R8A8_UNORM || f == DXGI_FORMAT_B8G8R8A8_TYPELESS;
    bool rgb16f = f == DXGI_FORMAT_R16G16B16A16_FLOAT || f == DXGI_FORMAT_R16G16B16A16_TYPELESS, rgb10 = f == DXGI_FORMAT_R10G10B10A2_UNORM || f == DXGI_FORMAT_R10G10B10A2_TYPELESS;
    FILE *o = NULL; bool ok = false; double lo = 1e30, hi = -1e30; long zeros = 0, nonfinite = 0;
    if (flt) {
        float *v = (float *)malloc((size_t)w * h * 4);
        for (UINT y = 0; v && y < h; y++) {
            const BYTE *row = data + (size_t)y * pitch;
            for (UINT x = 0; x < w; x++) {
                float z = f == DXGI_FORMAT_R32G8X24_TYPELESS ? *(const float *)(row + x * 8) : f == DXGI_FORMAT_R24G8_TYPELESS ? (*(const DWORD *)(row + x * 4) & 0xFFFFFF) / 16777215.0f
                        : f == DXGI_FORMAT_R16_TYPELESS ? *(const WORD *)(row + x * 2) / 65535.0f : *(const float *)(row + x * 4);
                v[(size_t)y * w + x] = z;
                if (!_finite(z)) nonfinite++; else if (z == 0) zeros++; else { if (z < lo) lo = z; if (z > hi) hi = z; }
            }
        }
        if (v && !fopen_s(&o, g_gfx.path, "wb") && o) {
            fprintf(o, "Pf\n%u %u\n-1.0\n", w, h);
            ok = true;
            for (int y = (int)h - 1; y >= 0 && ok; y--) ok = fwrite(v + (size_t)y * w, 4, w, o) == w;
            fclose(o);
        }
        char vp[MAX_PATH]; _snprintf_s(vp, sizeof(vp), _TRUNCATE, "%s.view.pgm", g_gfx.path);
        if (ok && !fopen_s(&o, vp, "wb") && o) {
            fprintf(o, "P5\n%u %u\n255\n", w, h);
            double span = hi > lo ? hi - lo : 1;
            for (size_t i = 0; i < (size_t)w * h; i++) { float z = v[i]; double t = !_finite(z) || z == 0 ? 0 : (z - lo) / span; if (t < 0) t = 0; if (t > 1) t = 1; fputc((int)(t * 255 + 0.5), o); }
            fclose(o);
        }
        free(v);
    }
    else if (rgb8 || rgb16f || rgb10) {
        bool bgr = f == DXGI_FORMAT_B8G8R8A8_UNORM || f == DXGI_FORMAT_B8G8R8A8_TYPELESS;
        if (!fopen_s(&o, g_gfx.path, "wb") && o) {
            fprintf(o, "P6\n%u %u\n255\n", w, h);
            ok = true;
            for (UINT y = 0; y < h && ok; y++) {
                const BYTE *row = data + (size_t)y * pitch;
                for (UINT x = 0; x < w; x++) {
                    float c[3];
                    if (rgb8) { const BYTE *px = row + x * 4; c[0] = px[bgr ? 2 : 0] / 255.0f; c[1] = px[1] / 255.0f; c[2] = px[bgr ? 0 : 2] / 255.0f; }
                    else if (rgb16f) { const WORD *px = (const WORD *)(row + x * 8); for (int k = 0; k < 3; k++) c[k] = GfxHalf(px[k]); }
                    else { DWORD px = *(const DWORD *)(row + x * 4); c[0] = (px & 1023) / 1023.0f; c[1] = ((px >> 10) & 1023) / 1023.0f; c[2] = ((px >> 20) & 1023) / 1023.0f; }
                    for (int k = 0; k < 3; k++) { if (c[k] > hi) hi = c[k]; if (c[k] < lo) lo = c[k]; double t = c[k] < 0 ? 0 : c[k] > 1 ? 1 : c[k]; fputc((int)(t * 255 + 0.5), o); }
                }
            }
            fclose(o);
        }
    }
    else { _snprintf_s(r, cap, _TRUNCATE, "error format %u is not one this build writes out", (UINT)f); return; }
    _snprintf_s(r, cap, _TRUNCATE, "%s grabbed %p %ux%u format %u (x%u, read as %u) after its let-go %d; values %.6g to %.6g, zeros %ld, not finite %ld; %s",
        ok ? "ok" : "error writing", g_gfx.grab, w, h, (UINT)g_gfx.grabDesc.Format, g_gfx.grabDesc.SampleDesc.Count, (UINT)f, g_gfx.grabK, lo, hi, zeros, nonfinite, g_gfx.path);
}

// ---- the scene's state, toward world-only and players-only passes (dev25, 2026-10-17) ----------------------------------
// Draw hooks do not hold on this context (0505: the runtime puts its own draw entries back), so a draw cannot be
// skipped where it is made. What can be seen is the state each draw is made with: the shaders, the input layout, the
// vertex buffers, and the viewport. To hide a kind of draw, the viewport is moved off the picture while that kind's
// state is bound in the scene (the scene's own depth buffer bound): its draws land nowhere, the rest of the frame is
// untouched, and the game's own viewport is put back the moment the state changes.
//   "gfx hide off | vs <p> | ps <p> | il <p> | vb <p> | dynamic | static [except]"   the rule ("except": hide the rest)
//   "gfx state"                                                                      what is bound, the hooks' calls
// The probe also lists, per frame, the vertex shaders, layouts and vertex-buffer kinds the scene's draws were set with.
typedef void (STDMETHODCALLTYPE *SetVS_t)(ID3D11DeviceContext *, ID3D11VertexShader *, ID3D11ClassInstance *const *, UINT);
typedef void (STDMETHODCALLTYPE *SetPS_t)(ID3D11DeviceContext *, ID3D11PixelShader *, ID3D11ClassInstance *const *, UINT);
typedef void (STDMETHODCALLTYPE *SetIL_t)(ID3D11DeviceContext *, ID3D11InputLayout *);
typedef void (STDMETHODCALLTYPE *SetVB_t)(ID3D11DeviceContext *, UINT, UINT, ID3D11Buffer *const *, const UINT *, const UINT *);
typedef void (STDMETHODCALLTYPE *SetBS_t)(ID3D11DeviceContext *, ID3D11BlendState *, const FLOAT[4], UINT);
typedef void (STDMETHODCALLTYPE *SetDS_t)(ID3D11DeviceContext *, ID3D11DepthStencilState *, UINT);
typedef void (STDMETHODCALLTYPE *SetVP_t)(ID3D11DeviceContext *, UINT, const D3D11_VIEWPORT *);
static SetVS_t g_setVS; static SetPS_t g_setPS; static SetIL_t g_setIL; static SetVB_t g_setVB; static SetBS_t g_setBS; static SetDS_t g_setDS; static SetVP_t g_setVP;

struct StCount { void *p; LONG n, dyn, imm, inst, blend; };   // dev27: per vertex shader, the buffers its draws were set up with
static struct {
    void *vs, *ps, *il, *vb0; UINT stride; int usage;   // bound now; usage: slot 0's buffer's D3D11_USAGE (-1 unknown)
    bool scene;                                        // the scene's depth buffer is bound
    D3D11_VIEWPORT vp[16]; UINT vpN;                   // the game's own viewports
    bool hiding;
    int kind; void *ptr; bool except;                  // the rule: 0 off, 1 vs, 2 ps, 3 il, 4 vb, 5 dynamic vb, 6 static vb,
                                                       // 7 world (hide the players), 8 players (hide all but the players)
    bool blended;                                      // the bound blend state blends render target 0 (effects do; players do not)
    bool inst;                                         // a vertex buffer is bound in slot 1 (instance data)
    void *vbFrom;                                      // dev30: where in the game the last vertex buffers were set from
    void *tess;                                        // dev32: which of the game's surface routines is drawing (its call of 0x7518C0)
    float cam[3]; bool camOk;                          // dev38: the camera's world position, from the per-view constants
    float pl[48][3]; bool plDead[48]; int plN;         // dev38: where the players stand (their entity records), and their bodies, read at each Present
    bool objPlayer;                                    // dev38: the rigid model about to be drawn is a player's (its gun, its gear)
    volatile LONG rigid, rigidPlayer;                  // dev38: rigid models placed, and those found on a player
    struct { void *p; bool on; } blends[128]; int blendN;
    volatile LONG calls[7], hides;                     // per setter (vs ps il vb bs ds vp); times the viewport went off
    void *sceneRes[8]; int sceneN; void *notScene[16]; int notSceneN;   // depth buffers judged so far
    struct { void *p; int usage; } usages[256]; int usageN;
    StCount vsSeen[256], ilSeen[128], usSeen[8], fromSeen[64]; int vsN, ilN, fromN;   // the probe's frame
    UINT bbW, bbH;
} g_st;
static StCount g_tessSeen[24]; static int g_tessN;     // dev32: the surface routines seen in the probe's frame

static int StUsage(ID3D11Buffer *b)
{
    if (!b) return -1;
    for (int i = 0; i < g_st.usageN; i++) if (g_st.usages[i].p == b) return g_st.usages[i].usage;
    D3D11_BUFFER_DESC d; b->GetDesc(&d);
    if (g_st.usageN < 256) { g_st.usages[g_st.usageN].p = b; g_st.usages[g_st.usageN].usage = (int)d.Usage; g_st.usageN++; }
    return (int)d.Usage;
}

// is this depth buffer the scene's? (as big as the picture, and not the HUD's D24S8 that comes with the backbuffer)
static bool StIsScene(ID3D11DepthStencilView *dsv)
{
    if (!dsv) return false;
    ID3D11Resource *r = NULL; dsv->GetResource(&r);
    if (!r) return false;
    bool yes = false, known = false;
    for (int i = 0; i < g_st.sceneN; i++) if (g_st.sceneRes[i] == r) { yes = known = true; }
    for (int i = 0; !known && i < g_st.notSceneN; i++) if (g_st.notScene[i] == r) known = true;
    if (!known) {
        ID3D11Texture2D *t = NULL;
        if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&t)) && t) {
            D3D11_TEXTURE2D_DESC d; t->GetDesc(&d); t->Release();
            yes = g_st.bbW && d.Width == g_st.bbW && d.Height == g_st.bbH && GfxGroup(d.Format) != DXGI_FORMAT_R24G8_TYPELESS;
        }
        if (yes && g_st.sceneN < 8) g_st.sceneRes[g_st.sceneN++] = r;
        else if (!yes && g_st.notSceneN < 16) g_st.notScene[g_st.notSceneN++] = r;
    }
    r->Release();
    return yes;
}

// dev38: a player's draw: skinned (dynamic, unblended: bodies and view hands), or a rigid model of the game's entity
// routine found on a player (guns, gear); the effects blend, the map and its props are static. dev39 (0521): the entity
// routine draws each rigid model twice, its depth first (the call at 0x77EBF4, no lighting in its constants) and its
// colour after (0x77EB26); with only the colour one followed, a hidden gun left its depth behind (a hole the shape of
// the gun in the world pass) and a kept one failed the depth test (no gun in the players pass). Both are followed now.
static bool StRigidSite(void *t) { DWORD a = (DWORD)(size_t)t; return a == 0x77EB26 || a == 0x77EBF4; }
static bool StIsPlayer() { return (g_st.usage == D3D11_USAGE_DYNAMIC && !g_st.blended) || (StRigidSite(g_st.tess) && g_st.objPlayer); }

static bool StMatch()
{
    bool m;
    switch (g_st.kind) {
    case 1: m = g_st.vs == g_st.ptr; break;
    case 2: m = g_st.ps == g_st.ptr; break;
    case 3: m = g_st.il == g_st.ptr; break;
    case 4: m = g_st.vb0 == g_st.ptr; break;
    case 5: m = g_st.usage == D3D11_USAGE_DYNAMIC; break;
    case 6: m = g_st.usage != D3D11_USAGE_DYNAMIC; break;
    // dev26 (sandbox 0509): the players are skinned on the CPU into dynamic vertex buffers, their depth draw and their
    // colour draw both; the world comes from static ones; the only other dynamic draws are the effects, which blend
    case 7: m = StIsPlayer(); break;                   // dev38: with their guns and gear
    case 8: m = !StIsPlayer(); break;
    case 9: m = g_st.vbFrom == g_st.ptr; break;        // dev30: the geometry set up from this place in the game
    case 10: m = g_st.tess == g_st.ptr; break;         // dev32: drawn by this surface routine of the game's
    default: return false;
    }
    return g_st.except ? !m : m;
}

static void StOffscreen(D3D11_VIEWPORT *v) { D3D11_VIEWPORT one = { 0, 0, 1, 1, 0, 1 }; *v = g_st.vpN ? g_st.vp[0] : one; v->TopLeftX = 30000; v->TopLeftY = 30000; v->Width = 1; v->Height = 1; }

static void StApply(ID3D11DeviceContext *ctx)
{
    bool want = g_st.kind && g_st.scene && StMatch();
    if (want == g_st.hiding) return;
    g_st.hiding = want;
    if (want) { D3D11_VIEWPORT v; StOffscreen(&v); g_setVP(ctx, 1, &v); InterlockedIncrement(&g_st.hides); }
    else if (g_st.vpN) g_setVP(ctx, g_st.vpN, g_st.vp);
}

static void StSeen(StCount *t, int *n, int cap, void *p)
{
    if (!g_gfx.phase || g_gfx.op != 1 || !g_st.scene) return;
    for (int i = 0; i < *n; i++) if (t[i].p == p) { t[i].n++; return; }
    if (*n < cap) { memset(&t[*n], 0, sizeof(StCount)); t[*n].p = p; t[*n].n = 1; (*n)++; }
}

static void STDMETHODCALLTYPE StSetVS(ID3D11DeviceContext *ctx, ID3D11VertexShader *s, ID3D11ClassInstance *const *ci, UINT n)
{
    void *ra = _ReturnAddress();
    g_setVS(ctx, s, ci, n);
    if (ctx != g_gfx.ctx) return;
    InterlockedIncrement(&g_st.calls[0]); g_st.vs = s;
    StSeen(g_st.vsSeen, &g_st.vsN, 256, s);
    if (g_gfx.phase && g_gfx.op == 1 && g_gfx.f && g_st.scene) fprintf(g_gfx.f, "vs %p @%p\n", s, ra);
    StApply(ctx);
}
static void STDMETHODCALLTYPE StSetPS(ID3D11DeviceContext *ctx, ID3D11PixelShader *s, ID3D11ClassInstance *const *ci, UINT n)
{
    g_setPS(ctx, s, ci, n);
    if (ctx != g_gfx.ctx) return;
    InterlockedIncrement(&g_st.calls[1]); g_st.ps = s;
    StApply(ctx);
}
static void STDMETHODCALLTYPE StSetIL(ID3D11DeviceContext *ctx, ID3D11InputLayout *l)
{
    g_setIL(ctx, l);
    if (ctx != g_gfx.ctx) return;
    InterlockedIncrement(&g_st.calls[2]); g_st.il = l;
    StSeen(g_st.ilSeen, &g_st.ilN, 128, l);
    StApply(ctx);
}
static void STDMETHODCALLTYPE StSetVB(ID3D11DeviceContext *ctx, UINT start, UINT n, ID3D11Buffer *const *b, const UINT *st, const UINT *off)
{
    void *ra = _ReturnAddress();
    g_st.vbFrom = ra;
    g_setVB(ctx, start, n, b, st, off);
    if (ctx != g_gfx.ctx) return;
    InterlockedIncrement(&g_st.calls[3]);
    if (start == 0 && n > 0 && b) {
        g_st.vb0 = b[0]; g_st.stride = st ? st[0] : 0;
        __try { g_st.usage = StUsage(b[0]); } __except (EXCEPTION_EXECUTE_HANDLER) { g_st.usage = -1; }
        if (g_gfx.phase && g_gfx.op == 1 && g_st.scene && g_st.usage >= 0 && g_st.usage < 8) g_st.usSeen[g_st.usage].n++;
    }
    if (g_gfx.phase && g_gfx.op == 1 && g_st.scene) {
        int i = 0; while (i < g_st.fromN && g_st.fromSeen[i].p != ra) i++;
        if (i == g_st.fromN && i < 64) { memset(&g_st.fromSeen[i], 0, sizeof(StCount)); g_st.fromSeen[i].p = ra; g_st.fromN++; }
        if (i < 64) { StCount &c = g_st.fromSeen[i]; c.n++; if (g_st.usage == D3D11_USAGE_DYNAMIC) c.dyn++; else c.imm++; if (n > 1 && b && b[1]) c.inst++; }
    }
    if (start <= 1 && start + n > 1 && b) g_st.inst = b[1 - start] != NULL;
    if (g_gfx.phase && g_gfx.op == 1 && g_gfx.f && g_st.scene && start == 0 && n > 0 && b)
        fprintf(g_gfx.f, "  vb %p %s stride %u%s @%p\n", b[0], g_st.usage == D3D11_USAGE_DYNAMIC ? "dyn" : "imm", g_st.stride, n > 1 && b[1] ? " inst" : "", ra);
    if (g_gfx.phase && g_gfx.op == 1 && g_st.scene)
        for (int i = 0; i < g_st.vsN; i++) if (g_st.vsSeen[i].p == g_st.vs) {
            StCount &c = g_st.vsSeen[i];
            if (g_st.usage == D3D11_USAGE_DYNAMIC) c.dyn++; else if (g_st.usage >= 0) c.imm++;
            if (g_st.inst) c.inst++;
            if (g_st.blended) c.blend++;
            break;
        }
    StApply(ctx);
}
static bool StBlends(ID3D11BlendState *s)
{
    if (!s) return false;                              // the default: no blending
    for (int i = 0; i < g_st.blendN; i++) if (g_st.blends[i].p == s) return g_st.blends[i].on;
    D3D11_BLEND_DESC d; s->GetDesc(&d);
    bool on = d.RenderTarget[0].BlendEnable != FALSE;
    if (g_st.blendN < 128) { g_st.blends[g_st.blendN].p = s; g_st.blends[g_st.blendN].on = on; g_st.blendN++; }
    return on;
}

static void STDMETHODCALLTYPE StSetBS(ID3D11DeviceContext *ctx, ID3D11BlendState *s, const FLOAT f[4], UINT m)
{
    g_setBS(ctx, s, f, m);
    if (ctx != g_gfx.ctx) return;
    InterlockedIncrement(&g_st.calls[4]);
    __try { g_st.blended = StBlends(s); } __except (EXCEPTION_EXECUTE_HANDLER) { g_st.blended = false; }
    if (g_gfx.phase && g_gfx.op == 1 && g_gfx.f && g_st.scene) fprintf(g_gfx.f, "  bs %s\n", g_st.blended ? "blend" : "opaque");
    StApply(ctx);
}
static void STDMETHODCALLTYPE StSetDS(ID3D11DeviceContext *ctx, ID3D11DepthStencilState *s, UINT r)
{
    g_setDS(ctx, s, r);
    if (ctx == g_gfx.ctx) InterlockedIncrement(&g_st.calls[5]);
}
static void STDMETHODCALLTYPE StSetVP(ID3D11DeviceContext *ctx, UINT n, const D3D11_VIEWPORT *vp)
{
    if (ctx == g_gfx.ctx && vp) {
        InterlockedIncrement(&g_st.calls[6]);
        g_st.vpN = n < 16 ? n : 16; memcpy(g_st.vp, vp, g_st.vpN * sizeof(D3D11_VIEWPORT));
        if (g_st.hiding) { D3D11_VIEWPORT v; StOffscreen(&v); g_setVP(ctx, 1, &v); return; }
    }
    g_setVP(ctx, n, vp);
}

// the binds: is the scene's depth buffer in this one? (before the bind), then the rule again (after it)
static void StOnBind(ID3D11DepthStencilView *dsv) { __try { g_st.scene = StIsScene(dsv); } __except (EXCEPTION_EXECUTE_HANDLER) { g_st.scene = false; } }
static void StAfterBind(ID3D11DeviceContext *ctx) { if (g_st.kind || g_st.hiding) StApply(ctx); }

static bool StInstall(void **vt)
{
    return GfxHookSlot(vt, 11, (void *)&StSetVS, (void **)&g_setVS) && GfxHookSlot(vt, 9, (void *)&StSetPS, (void **)&g_setPS)
        && GfxHookSlot(vt, 17, (void *)&StSetIL, (void **)&g_setIL) && GfxHookSlot(vt, 18, (void *)&StSetVB, (void **)&g_setVB)
        && GfxHookSlot(vt, 35, (void *)&StSetBS, (void **)&g_setBS) && GfxHookSlot(vt, 36, (void *)&StSetDS, (void **)&g_setDS)
        && GfxHookSlot(vt, 44, (void *)&StSetVP, (void **)&g_setVP);
}

static void StProbeStart() { g_st.vsN = g_st.ilN = g_st.fromN = 0; g_tessN = 0; memset(g_st.usSeen, 0, sizeof(g_st.usSeen)); }

static void StProbeEnd(FILE *f)
{
    static const char *us[] = { "default", "immutable", "dynamic", "staging" };
    fprintf(f, "scene vertex shaders (%d), times set while the scene's depth was bound:", g_st.vsN);
    for (int i = 0; i < g_st.vsN; i++) fprintf(f, " %p:%ld", g_st.vsSeen[i].p, g_st.vsSeen[i].n);
    fprintf(f, "\nper vertex shader, the vertex buffers its draws were set up with (vs set dyn imm inst blend):");
    for (int i = 0; i < g_st.vsN; i++) fprintf(f, "\n  %p %ld %ld %ld %ld %ld", g_st.vsSeen[i].p, g_st.vsSeen[i].n, g_st.vsSeen[i].dyn, g_st.vsSeen[i].imm, g_st.vsSeen[i].inst, g_st.vsSeen[i].blend);
    fprintf(f, "\nscene input layouts (%d):", g_st.ilN);
    for (int i = 0; i < g_st.ilN; i++) fprintf(f, " %p:%ld", g_st.ilSeen[i].p, g_st.ilSeen[i].n);
    fprintf(f, "\nwhere the scene's vertex buffers were set from (place: sets dyn imm inst):");
    for (int i = 0; i < g_st.fromN; i++) fprintf(f, "\n  from %p %ld %ld %ld %ld", g_st.fromSeen[i].p, g_st.fromSeen[i].n, g_st.fromSeen[i].dyn, g_st.fromSeen[i].imm, g_st.fromSeen[i].inst);
    fprintf(f, "\nthe game's surface routines that drew in the scene (place: times it took over):");
    for (int i = 0; i < g_tessN; i++) fprintf(f, "\n  via %p %ld", g_tessSeen[i].p, g_tessSeen[i].n);
    fprintf(f, "\nscene vertex buffers by kind:");
    for (int i = 0; i < 4; i++) fprintf(f, " %s:%ld", us[i], g_st.usSeen[i].n);
    fprintf(f, "\n");
}

static void StCommand(const char *arg, char *out, size_t cap)
{
    unsigned p = 0; bool except = strstr(arg, " except") != NULL;
    if (!_stricmp(arg, "off")) { g_st.kind = 0; g_st.ptr = NULL; g_st.except = false; }
    else if (!_strnicmp(arg, "vs ", 3) && sscanf_s(arg + 3, "%x", &p) == 1) { g_st.ptr = (void *)(size_t)p; g_st.except = except; g_st.kind = 1; }
    else if (!_strnicmp(arg, "ps ", 3) && sscanf_s(arg + 3, "%x", &p) == 1) { g_st.ptr = (void *)(size_t)p; g_st.except = except; g_st.kind = 2; }
    else if (!_strnicmp(arg, "il ", 3) && sscanf_s(arg + 3, "%x", &p) == 1) { g_st.ptr = (void *)(size_t)p; g_st.except = except; g_st.kind = 3; }
    else if (!_strnicmp(arg, "vb ", 3) && sscanf_s(arg + 3, "%x", &p) == 1) { g_st.ptr = (void *)(size_t)p; g_st.except = except; g_st.kind = 4; }
    else if (!_strnicmp(arg, "dynamic", 7)) { g_st.ptr = NULL; g_st.except = except; g_st.kind = 5; }
    else if (!_strnicmp(arg, "static", 6)) { g_st.ptr = NULL; g_st.except = except; g_st.kind = 6; }
    else if (!_stricmp(arg, "world")) { g_st.ptr = NULL; g_st.except = false; g_st.kind = 7; }
    else if (!_stricmp(arg, "players")) { g_st.ptr = NULL; g_st.except = false; g_st.kind = 8; }
    else if (!_strnicmp(arg, "from ", 5) && sscanf_s(arg + 5, "%x", &p) == 1) { g_st.ptr = (void *)(size_t)p; g_st.except = except; g_st.kind = 9; }
    else if (!_strnicmp(arg, "via ", 4) && sscanf_s(arg + 4, "%x", &p) == 1) { g_st.ptr = (void *)(size_t)p; g_st.except = except; g_st.kind = 10; }
    else if (*arg) { _snprintf_s(out, cap, _TRUNCATE, "error usage: gfx hide off | world | players | via <place> | from <place> | vs <p> | ps <p> | il <p> | vb <p> | dynamic | static [except]"); return; }
    _snprintf_s(out, cap, _TRUNCATE, "ok hide %s%s %p; hiding now %d, viewport off %ld times; scene %d", g_st.kind == 0 ? "off" : g_st.kind == 1 ? "vs" : g_st.kind == 2 ? "ps" : g_st.kind == 3 ? "il" : g_st.kind == 4 ? "vb" : g_st.kind == 5 ? "dynamic" : g_st.kind == 6 ? "static" : g_st.kind == 7 ? "world" : g_st.kind == 8 ? "players" : g_st.kind == 9 ? "from" : "via",
        g_st.except ? " except" : "", g_st.ptr, g_st.hiding ? 1 : 0, g_st.hides, g_st.scene ? 1 : 0);
}

static void StStatus(char *out, size_t cap)
{
    _snprintf_s(out, cap, _TRUNCATE, "state vs %p ps %p il %p vb %p stride %u usage %d scene %d hide %d hiding %d hides %ld calls vs %ld ps %ld il %ld vb %ld bs %ld ds %ld vp %ld viewport %.0fx%.0f at %.0f,%.0f backbuffer %ux%u; rigid %ld on players %ld camera %.0f,%.0f,%.0f%s players %d",
        g_st.vs, g_st.ps, g_st.il, g_st.vb0, g_st.stride, g_st.usage, g_st.scene ? 1 : 0, g_st.kind, g_st.hiding ? 1 : 0, g_st.hides,
        g_st.calls[0], g_st.calls[1], g_st.calls[2], g_st.calls[3], g_st.calls[4], g_st.calls[5], g_st.calls[6],
        g_st.vpN ? g_st.vp[0].Width : 0.0f, g_st.vpN ? g_st.vp[0].Height : 0.0f, g_st.vpN ? g_st.vp[0].TopLeftX : 0.0f, g_st.vpN ? g_st.vp[0].TopLeftY : 0.0f, g_st.bbW, g_st.bbH,
        g_st.rigid, g_st.rigidPlayer, g_st.cam[0], g_st.cam[1], g_st.cam[2], g_st.camOk ? "" : " (not seen)", g_st.plN);
}

// ---- dev32: which of the game's surface routines is drawing (2026-10-17) ----------------------------------------------
// 0515: the scene's vertex buffers come through one helper of the game's, 0x7518C0 (it keeps the bound stream and calls
// IASetVertexBuffers only when it changes), called from 18 places in 0x7795F6-0x77FECD: the routines that draw each kind
// of surface (the map's, its props', rigid models', skinned models'...). Each of those 18 calls is pointed at a stub of
// ours that notes which routine is calling, applies the hide rule again, and goes on into the helper with every register
// as it was. So a rule can name a routine: "gfx hide via <place>" (the call's own address, as the probe lists them).
static const DWORD kTessHelper = 0x7518C0;
static const DWORD kTessSites[] = { 0x751AEA, 0x7795F6, 0x7797E6, 0x779A83, 0x779CA9, 0x77A17E, 0x77A51D, 0x77A8EC, 0x77B318,
                                    0x77B452, 0x77B536, 0x77D351, 0x77EB26, 0x77EBF4, 0x77F83B, 0x77F919, 0x77FDD5, 0x77FECD };
static DWORD g_tessTarget = kTessHelper;
static volatile LONG g_tessPatched;
static char g_tessWhy[160];

static void __stdcall TessEnter(DWORD ret)
{
    void *site = (void *)(size_t)(ret - 5);
    if (site == g_st.tess) return;
    g_st.tess = site; g_st.objPlayer = false;
    if (g_gfx.phase && g_gfx.op == 1 && g_st.scene) {
        int i = 0; while (i < g_tessN && g_tessSeen[i].p != site) i++;
        if (i == g_tessN && i < 24) { memset(&g_tessSeen[i], 0, sizeof(StCount)); g_tessSeen[i].p = site; g_tessN++; }
        if (i < 24) g_tessSeen[i].n++;
        if (g_gfx.f) fprintf(g_gfx.f, "  via %p\n", site);
    }
    if ((g_st.kind || g_st.hiding) && g_gfx.ctx) StApply(g_gfx.ctx);
}

__declspec(naked) static void TessThunk()
{
    __asm {
        pushad
        pushfd
        push  dword ptr [esp + 36]                   // the call's return address: 32 (pushad) + 4 (pushfd)
        call  TessEnter
        popfd
        popad
        jmp   dword ptr [g_tessTarget]
    }
}

// the 18 calls pointed at the stub, each only if it is still "call 0x7518C0" (another build: nothing is touched)
static bool TessInstall(char *why, size_t cap)
{
    if (g_tessPatched) return true;
    BYTE *base = (BYTE *)GetModuleHandleA(NULL);
    for (int i = 0; i < (int)(sizeof(kTessSites) / sizeof(kTessSites[0])); i++) {
        BYTE *at = base + (kTessSites[i] - 0x400000);
        BYTE op = 0; int rel = 0;
        if (!SafeCopy(&op, at, 1) || !SafeCopy(&rel, at + 1, 4) || op != 0xE8 || (DWORD)(size_t)(at + 5 + rel) != kTessHelper) {
            _snprintf_s(why, cap, _TRUNCATE, "the call at %08lX is not the one this build expects: nothing patched", kTessSites[i]);
            return false;
        }
    }
    for (int i = 0; i < (int)(sizeof(kTessSites) / sizeof(kTessSites[0])); i++) {
        BYTE *at = base + (kTessSites[i] - 0x400000);
        int rel = (int)((BYTE *)&TessThunk - (at + 5));
        DWORD old;
        if (!VirtualProtect(at + 1, 4, PAGE_EXECUTE_READWRITE, &old)) { _snprintf_s(why, cap, _TRUNCATE, "protect failed at %08lX", kTessSites[i]); return false; }
        InterlockedExchange((volatile LONG *)(at + 1), rel);
        VirtualProtect(at + 1, 4, old, &old);
        FlushInstructionCache(GetCurrentProcess(), at, 5);
    }
    InterlockedExchange(&g_tessPatched, 1);
    Log("gfx: the game's 18 calls of its vertex-stream helper now pass through our stub (which routine is drawing)");
    return true;
}

// ---- dev29: the scene's state as a sequence, toward telling a player's gun from the map (2026-10-17) -------------------
// 0510-0511: the players' bodies and the view hands are dynamic (skinned on the CPU); their guns and gear are rigid
// models drawn from static buffers, like the map's. A rigid part attached to a player needs its own matrix every draw;
// the map's surfaces need none and its props take theirs from instancing. So the probe also logs, in order while the
// scene's depth is bound: vertex shader changes (vs), vertex buffers (vb dyn|imm [inst]), vertex-shader constant
// buffers (cb slot ptr size), and the uploads into buffers (map / upd ptr): the per-object pattern should show.
typedef void (STDMETHODCALLTYPE *SetVSCB_t)(ID3D11DeviceContext *, UINT, UINT, ID3D11Buffer *const *);
typedef HRESULT (STDMETHODCALLTYPE *Map_t)(ID3D11DeviceContext *, ID3D11Resource *, UINT, D3D11_MAP, UINT, D3D11_MAPPED_SUBRESOURCE *);
typedef void (STDMETHODCALLTYPE *UpdSub_t)(ID3D11DeviceContext *, ID3D11Resource *, UINT, const D3D11_BOX *, const void *, UINT, UINT);
static SetVSCB_t g_setVSCB; static Map_t g_map; static UpdSub_t g_updSub;
typedef void (STDMETHODCALLTYPE *Unmap_t)(ID3D11DeviceContext *, ID3D11Resource *, UINT);
static Unmap_t g_unmap;
static ID3D11Resource *g_objCb; static void *g_objCbData;      // dev35: the 192-byte per-object constants being written
static ID3D11Resource *g_viewCb; static void *g_viewCbData; static UINT g_viewCbSize; static int g_viewCbN[3];   // dev37: the per-view constants
static ID3D11Resource *g_camCb; static void *g_camCbData;      // dev38: the per-view constants, for the camera's position

// dev38 (0519-0520): the players pass with their guns. Their guns and gear are rigid models of the game's entity routine
// (its call at 0x77EB26), each drawn with 192 bytes of its own constants whose first three rows are its world matrix
// relative to the camera (the translation in floats 3, 7 and 11); the camera's own world position is in the per-view
// constants (2448 bytes, floats 468-470: in 0520 the free camera's position exactly); where each player stands is in
// their entity records (+0, three floats). A rigid model within 48 units across and -16..96 up of a player is that
// player's: the players pass keeps it, the world pass hides it. 0520: every player's gun parts within 7-45 across.
static bool StGunsOn() { return (g_st.kind == 7 || g_st.kind == 8) && g_st.scene; }

static void StRigid(ID3D11DeviceContext *ctx, const float *f)
{
    bool on = false;
    if (StRigidSite(g_st.tess) && g_st.camOk && fabsf(f[3]) + fabsf(f[7]) + fabsf(f[11]) > 1.0f) {   // one at the camera itself (a sky) is no one's
        float x = f[3] + g_st.cam[0], y = f[7] + g_st.cam[1], z = f[11] + g_st.cam[2];
        for (int i = 0; i < g_st.plN && !on; i++) {
            float dx = x - g_st.pl[i][0], dy = y - g_st.pl[i][1], dz = z - g_st.pl[i][2];
            if (g_st.plDead[i]) on = dx * dx + dy * dy < 80.0f * 80.0f && dz > -48.0f && dz < 96.0f;   // a body falls where it will
            else on = dx * dx + dy * dy < 48.0f * 48.0f && dz > -16.0f && dz < 96.0f;
        }
        InterlockedIncrement(&g_st.rigid); if (on) InterlockedIncrement(&g_st.rigidPlayer);
    }
    if (on != g_st.objPlayer) { g_st.objPlayer = on; StApply(ctx); }
}

// render thread, at each Present while a players or world rule is on: where each living player stands, and where each
// one who died in the last 30 s of film fell (0520: a dead player's record is all FF, while the body and its gear lie
// where it stood last)
static struct { float p[3]; int tick; bool ok; } g_plLast[18];
static struct { float p[3]; int tick; } g_corpse[30]; static int g_corpseN;

static void StPlayersNow()
{
    int n = 0, tick = ReadInt(kRvaTick);
    for (int e = 0; e < 18; e++) {
        float q[3]; BYTE w = 255;
        bool ok = SafeCopy(q, g_base + kRvaPlayers + e * kPlayerStride, 12);
        SafeCopy(&w, g_base + kRvaPlayers + e * kPlayerStride + kPlayerWeapon, 1);
        ok = ok && w != 255 && _finite(q[0]) && _finite(q[1]) && _finite(q[2]) && !(q[0] == 0 && q[1] == 0 && q[2] == 0);
        if (ok) {
            memcpy(g_plLast[e].p, q, sizeof(q)); g_plLast[e].tick = tick; g_plLast[e].ok = true;
            memcpy(g_st.pl[n], q, sizeof(q)); g_st.plDead[n] = false; n++;
        }
        else if (g_plLast[e].ok) {                       // gone since the last frame: a body where it stood
            g_plLast[e].ok = false;
            int at;
            if (g_corpseN < 30) at = g_corpseN++;
            else { at = 0; for (int i = 1; i < 30; i++) if (g_corpse[i].tick < g_corpse[at].tick) at = i; }   // the oldest gives way
            memcpy(g_corpse[at].p, g_plLast[e].p, sizeof(q)); g_corpse[at].tick = tick;
        }
    }
    for (int i = 0; i < g_corpseN; i++) {
        int age = tick - g_corpse[i].tick;
        if (age < 0 || age > 30000) { g_corpse[i] = g_corpse[--g_corpseN]; i--; continue; }   // the film went back before it, or long since
        if (n < 48) { memcpy(g_st.pl[n], g_corpse[i].p, sizeof(g_corpse[i].p)); g_st.plDead[n] = true; n++; }
    }
    g_st.plN = n;
}
static volatile LONG g_seqCalls[3];

static bool SeqOn() { return g_gfx.phase && g_gfx.op == 1 && g_gfx.f && g_st.scene; }

static void STDMETHODCALLTYPE SeqSetVSCB(ID3D11DeviceContext *ctx, UINT start, UINT n, ID3D11Buffer *const *b)
{
    g_setVSCB(ctx, start, n, b);
    if (ctx != g_gfx.ctx) return;
    InterlockedIncrement(&g_seqCalls[0]);
    if (SeqOn() && b) __try {
        for (UINT i = 0; i < n && i < 8; i++) {
            UINT size = 0; int usage = -1;
            if (b[i]) { D3D11_BUFFER_DESC d; b[i]->GetDesc(&d); size = d.ByteWidth; usage = (int)d.Usage; }
            fprintf(g_gfx.f, "  cb %u %p %u%s\n", start + i, b[i], size, usage == D3D11_USAGE_DYNAMIC ? " dyn" : "");
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
static HRESULT STDMETHODCALLTYPE SeqMap(ID3D11DeviceContext *ctx, ID3D11Resource *r, UINT sub, D3D11_MAP t, UINT fl, D3D11_MAPPED_SUBRESOURCE *m)
{
    if (ctx == g_gfx.ctx) {
        InterlockedIncrement(&g_seqCalls[1]);
        if (SeqOn()) __try {
            D3D11_RESOURCE_DIMENSION dim; r->GetType(&dim);
            if (dim == D3D11_RESOURCE_DIMENSION_BUFFER) {
                D3D11_BUFFER_DESC d; ((ID3D11Buffer *)r)->GetDesc(&d);
                fprintf(g_gfx.f, "  map %p %u bind %X type %d\n", r, d.ByteWidth, d.BindFlags, (int)t);
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    HRESULT hr = g_map(ctx, r, sub, t, fl, m);
    if (ctx == g_gfx.ctx && SUCCEEDED(hr) && m && (SeqOn() || StGunsOn())) __try {
        D3D11_RESOURCE_DIMENSION dim; r->GetType(&dim);
        if (dim == D3D11_RESOURCE_DIMENSION_BUFFER) {
            D3D11_BUFFER_DESC d; ((ID3D11Buffer *)r)->GetDesc(&d);
            int k = d.ByteWidth == 2448 ? 0 : d.ByteWidth == 1040 ? 1 : d.ByteWidth == 512 ? 2 : -1;
            if (d.ByteWidth == 192 && (d.BindFlags & D3D11_BIND_CONSTANT_BUFFER)) { g_objCb = r; g_objCbData = m->pData; }
            else if (k >= 0 && (d.BindFlags & D3D11_BIND_CONSTANT_BUFFER)) {
                if (k == 0) { g_camCb = r; g_camCbData = m->pData; }
                if (SeqOn() && g_viewCbN[k] < 2) { g_viewCb = r; g_viewCbData = m->pData; g_viewCbSize = d.ByteWidth; }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return hr;
}
static void STDMETHODCALLTYPE SeqUnmap(ID3D11DeviceContext *ctx, ID3D11Resource *r, UINT sub)
{
    if (ctx == g_gfx.ctx && r == g_camCb && g_camCbData) {
        if (g_st.scene) __try {                          // the scene's view (the shadow maps have views of their own)
            const float *f = (const float *)g_camCbData;
            if (_finite(f[468]) && _finite(f[469]) && _finite(f[470]) && f[471] == 1.0f) { memcpy(g_st.cam, f + 468, sizeof(g_st.cam)); g_st.camOk = true; }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        g_camCb = NULL; g_camCbData = NULL;
    }
    if (ctx == g_gfx.ctx && r == g_objCb && g_objCbData) {
        if (SeqOn()) __try {
            const float *f = (const float *)g_objCbData;
            fprintf(g_gfx.f, "  obj via %p:", g_st.tess);
            for (int i = 0; i < 48; i++) fprintf(g_gfx.f, " %.6g", f[i]);
            fprintf(g_gfx.f, "\n");
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (StGunsOn()) __try { StRigid(ctx, (const float *)g_objCbData); } __except (EXCEPTION_EXECUTE_HANDLER) {}
        g_objCb = NULL; g_objCbData = NULL;
    }
    if (ctx == g_gfx.ctx && r == g_viewCb && g_viewCbData) {
        int k = g_viewCbSize == 2448 ? 0 : g_viewCbSize == 1040 ? 1 : 2;
        if (SeqOn()) __try {
            const float *f = (const float *)g_viewCbData;
            fprintf(g_gfx.f, "  cbdump %u via %p:", g_viewCbSize, g_st.tess);
            for (UINT i = 0; i < g_viewCbSize / 4; i++) fprintf(g_gfx.f, " %.6g", f[i]);
            fprintf(g_gfx.f, "\n");
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        g_viewCbN[k]++; g_viewCb = NULL; g_viewCbData = NULL;
    }
    g_unmap(ctx, r, sub);
}
static void STDMETHODCALLTYPE SeqUpdSub(ID3D11DeviceContext *ctx, ID3D11Resource *r, UINT sub, const D3D11_BOX *box, const void *data, UINT rp, UINT dp)
{
    if (ctx == g_gfx.ctx) {
        InterlockedIncrement(&g_seqCalls[2]);
        if (SeqOn()) __try {
            D3D11_RESOURCE_DIMENSION dim; r->GetType(&dim);
            if (dim == D3D11_RESOURCE_DIMENSION_BUFFER) { D3D11_BUFFER_DESC d; ((ID3D11Buffer *)r)->GetDesc(&d); fprintf(g_gfx.f, "  upd %p %u bind %X\n", r, d.ByteWidth, d.BindFlags); }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    g_updSub(ctx, r, sub, box, data, rp, dp);
}
static bool SeqInstall(void **vt)
{
    return GfxHookSlot(vt, 7, (void *)&SeqSetVSCB, (void **)&g_setVSCB) && GfxHookSlot(vt, 14, (void *)&SeqMap, (void **)&g_map)
        && GfxHookSlot(vt, 48, (void *)&SeqUpdSub, (void **)&g_updSub) && GfxHookSlot(vt, 15, (void *)&SeqUnmap, (void **)&g_unmap);
}

// ---- the temporary fog pass (dev31-dev34, 2026-10-17) ----------------------------------------------------------------
// Mycelium (10-17): "Lets see what we can do in game with a TEMP FOG pass that gets recorded to the World Pass when a User
// locks it in place." Fog with blend modes the game never had, drawn into the game's own frame by a pixel shader of ours
// that mixes a fog colour in by the view distance from the game's depth pyramid (0504), with a blend mode (normal, add,
// screen, multiply, overlay, soft light, colour dodge, colour burn) worked in gamma space as an image editor does.
// Where (0516: the "clean plate" target is a side copy of the scene that the frame never builds its picture from, so
// fog drawn there changed nothing): "scene" (the default) draws into the scene itself the moment it is done (the first
// smaller target the frame binds after the scene: its bloom), so the game's own bloom and grade run on it; per sample
// when the scene is multisampled. "present" draws into the finished frame before it is shown (after the grade; over
// the HUD, which recordings hide). Everything the game had bound is saved first and put back after.
// Temporary until locked: "fog lock off" (the default) shows it live and leaves it out of every recording; "fog lock on"
// keeps it in the takes and the world pass; never in the players pass (it would fog their black background).
//   "fog on | off | test | lock on|off | where scene|present | status | set <key>=<value> ...": color=R,G,B (0-255)
//   start= end= (game units) density= (exp curves) opacity= (0-1) curve=linear|exp|exp2 sky= (0-1) mode=<blend>
typedef HRESULT (WINAPI *D3DCompile_t)(LPCVOID, SIZE_T, LPCSTR, const void *, void *, LPCSTR, LPCSTR, UINT, UINT, ID3DBlob **, ID3DBlob **);
struct FogParams { float color[4]; float start, end, density, opacity; float mode, curve, sky, srgb; };
static const char *kFogModes[] = { "normal", "add", "screen", "multiply", "overlay", "softlight", "dodge", "burn" };
struct FogCopy { ID3D11Texture2D *tex; ID3D11ShaderResourceView *srv; D3D11_TEXTURE2D_DESC d; DXGI_FORMAT view; };
static struct {
    volatile LONG on, locked, test, where;             // where: 0 the scene, 1 the finished frame
    FogParams p;
    bool ready, failed; char why[300];
    ID3D11VertexShader *vs; ID3D11PixelShader *ps, *psms; ID3D11Buffer *cb;
    ID3D11RasterizerState *rs; ID3D11BlendState *bs; ID3D11DepthStencilState *ds;
    ID3D11Resource *pyr; ID3D11ShaderResourceView *pyrSrv;
    ID3D11RenderTargetView *sceneRtv; ID3D11Resource *sceneRes; DXGI_FORMAT sceneFmt;
    ID3D11Resource *bb; ID3D11RenderTargetView *bbRtv;
    FogCopy copy;
    bool sceneSeen, now, drawn;
    volatile LONG draws, skipped, atRead;             // dev37: drawn into the resolved scene as the bloom read it
    bool armed, inDraw;                                 // dev37: the scene is done, the resolved scene not read yet; our own draw running
    ID3D11Resource *resolved; ID3D11RenderTargetView *resolvedRtv;   // dev37: the game's resolved scene, and our target view on it
} g_fog = { 0, 0, 0, 0, { { 0.55f, 0.6f, 0.7f, 1 }, 200, 3000, 0.0008f, 0.6f, 0, 0, 0.5f, 0 } };

static const char kFogHlsl[] =
"cbuffer P : register(b0) { float4 fogColor; float4 p0; float4 p1; }\n"
"Texture2D<float> depth : register(t1);\n"
"struct V { float4 pos : SV_Position; };\n"
"V vs(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1); return o; }\n"
"float3 mixmode(float3 b, float3 f, int m) {\n"
"  if (m == 1) return b + f;\n"
"  if (m == 2) return 1 - (1 - b) * (1 - f);\n"
"  if (m == 3) return b * f;\n"
"  if (m == 4) return b < 0.5 ? 2 * b * f : 1 - 2 * (1 - b) * (1 - f);\n"
"  if (m == 5) return (1 - 2 * f) * b * b + 2 * f * b;\n"
"  if (m == 6) return b / max(1 - f, 0.001);\n"
"  if (m == 7) return 1 - (1 - b) / max(f, 0.001);\n"
"  return f;\n"
"}\n"
"float4 fogit(float4 c, float2 xy) {\n"
"  float z = depth.Load(int3(int2(xy), 0));\n"
"  float t;\n"
"  if (!(z < 1e30)) t = p1.z;\n"
"  else { float d = max(z - p0.x, 0);\n"
"    if (p1.y < 0.5) t = saturate(d / max(p0.y - p0.x, 1));\n"
"    else if (p1.y < 1.5) t = 1 - exp(-d * p0.z);\n"
"    else { float e = d * p0.z; t = 1 - exp(-e * e); } }\n"
"  t = saturate(t * p0.w);\n"
"  float3 b = p1.w > 0.5 ? pow(saturate(c.rgb), 1.0 / 2.2) : saturate(c.rgb);\n"
"  float3 o = lerp(b, saturate(mixmode(b, fogColor.rgb, (int)p1.x)), t);\n"
"  return float4(p1.w > 0.5 ? pow(saturate(o), 2.2) : o, c.a);\n"
"}\n"
"#ifdef MS\n"
"Texture2DMS<float4> scene : register(t0);\n"
"float4 ps(V i, uint si : SV_SampleIndex) : SV_Target { return fogit(scene.Load(int2(i.pos.xy), si), i.pos.xy); }\n"
"#else\n"
"Texture2D<float4> scene : register(t0);\n"
"float4 ps(V i) : SV_Target { return fogit(scene.Load(int3(int2(i.pos.xy), 0)), i.pos.xy); }\n"
"#endif\n";

static bool FogIsSrgb(DXGI_FORMAT f) { return f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB || f == DXGI_FORMAT_B8G8R8X8_UNORM_SRGB; }

// render thread: the shaders and states, once (d3dcompiler_47 is part of Windows 10 and 11)
static bool FogBuild(ID3D11Device *dev)
{
    if (g_fog.ready) return true;
    if (g_fog.failed) return false;
    g_fog.failed = true;                               // until it all works
    HMODULE dc = LoadLibraryA("d3dcompiler_47.dll");
    D3DCompile_t compile = dc ? (D3DCompile_t)GetProcAddress(dc, "D3DCompile") : NULL;
    if (!compile) { strcpy_s(g_fog.why, sizeof(g_fog.why), "no d3dcompiler_47.dll"); return false; }
    struct Macro { const char *name, *def; } ms[] = { { "MS", "1" }, { NULL, NULL } };
    ID3DBlob *vsb = NULL, *psb = NULL, *psmsb = NULL, *err = NULL;
    HRESULT hr = compile(kFogHlsl, sizeof(kFogHlsl) - 1, "fog", NULL, NULL, "vs", "vs_4_0", 0, 0, &vsb, &err);
    if (SUCCEEDED(hr)) hr = compile(kFogHlsl, sizeof(kFogHlsl) - 1, "fog", NULL, NULL, "ps", "ps_4_0", 0, 0, &psb, &err);
    if (SUCCEEDED(hr)) hr = compile(kFogHlsl, sizeof(kFogHlsl) - 1, "fog", ms, NULL, "ps", "ps_4_1", 0, 0, &psmsb, &err);
    if (FAILED(hr)) {
        _snprintf_s(g_fog.why, sizeof(g_fog.why), _TRUNCATE, "a shader did not compile: %s", err ? (const char *)err->GetBufferPointer() : "?");
        if (err) err->Release(); if (vsb) vsb->Release(); if (psb) psb->Release(); return false;
    }
    hr = dev->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), NULL, &g_fog.vs);
    if (SUCCEEDED(hr)) hr = dev->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), NULL, &g_fog.ps);
    if (SUCCEEDED(hr)) hr = dev->CreatePixelShader(psmsb->GetBufferPointer(), psmsb->GetBufferSize(), NULL, &g_fog.psms);
    vsb->Release(); psb->Release(); psmsb->Release();
    D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = sizeof(FogParams); bd.Usage = D3D11_USAGE_DYNAMIC; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (SUCCEEDED(hr)) hr = dev->CreateBuffer(&bd, NULL, &g_fog.cb);
    D3D11_RASTERIZER_DESC rd = {}; rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = TRUE; rd.MultisampleEnable = TRUE;
    if (SUCCEEDED(hr)) hr = dev->CreateRasterizerState(&rd, &g_fog.rs);
    D3D11_BLEND_DESC bld = {}; bld.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    if (SUCCEEDED(hr)) hr = dev->CreateBlendState(&bld, &g_fog.bs);
    D3D11_DEPTH_STENCIL_DESC dd = {}; dd.DepthEnable = FALSE; dd.StencilEnable = FALSE;
    if (SUCCEEDED(hr)) hr = dev->CreateDepthStencilState(&dd, &g_fog.ds);
    if (FAILED(hr)) { _snprintf_s(g_fog.why, sizeof(g_fog.why), _TRUNCATE, "a Direct3D object failed (0x%08lX)", hr); return false; }
    g_fog.failed = false; g_fog.ready = true;
    Log("fog: shaders and states built");
    return true;
}

// a copy of the target to read from (a texture cannot be read and drawn to at once), made again when the target changes
static bool FogCopyOf(ID3D11Device *dev, ID3D11Resource *src, DXGI_FORMAT view)
{
    ID3D11Texture2D *t = NULL;
    if (FAILED(src->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&t)) || !t) { strcpy_s(g_fog.why, sizeof(g_fog.why), "the target is not a 2D texture"); return false; }
    D3D11_TEXTURE2D_DESC d; t->GetDesc(&d); t->Release();
    if (g_fog.copy.tex && !memcmp(&d, &g_fog.copy.d, sizeof(d)) && g_fog.copy.view == view) return true;
    if (g_fog.copy.srv) { g_fog.copy.srv->Release(); g_fog.copy.srv = NULL; }
    if (g_fog.copy.tex) { g_fog.copy.tex->Release(); g_fog.copy.tex = NULL; }
    D3D11_TEXTURE2D_DESC cd = d; cd.BindFlags = D3D11_BIND_SHADER_RESOURCE; cd.MiscFlags = 0; cd.CPUAccessFlags = 0; cd.Usage = D3D11_USAGE_DEFAULT; cd.MipLevels = 1; cd.ArraySize = 1;
    HRESULT hr = dev->CreateTexture2D(&cd, NULL, &g_fog.copy.tex);
    D3D11_SHADER_RESOURCE_VIEW_DESC sd = {}; sd.Format = view;
    if (d.SampleDesc.Count > 1) sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DMS;
    else { sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; sd.Texture2D.MipLevels = 1; }
    if (SUCCEEDED(hr)) hr = dev->CreateShaderResourceView(g_fog.copy.tex, &sd, &g_fog.copy.srv);
    if (FAILED(hr)) { _snprintf_s(g_fog.why, sizeof(g_fog.why), _TRUNCATE, "the copy failed (0x%08lX, format %u, x%u)", hr, (UINT)view, d.SampleDesc.Count); return false; }
    g_fog.copy.d = d; g_fog.copy.view = view;
    Log("fog: a copy of %ux%u x%u, read as format %u", d.Width, d.Height, d.SampleDesc.Count, (UINT)view);
    return true;
}

static bool FogWanted() { return g_fog.on && g_st.kind != 8 && (!g_rec.active || g_fog.locked); }   // dev39: never with only the players, live too

// render thread: our fog drawn into one target (the game's view of it), read from a copy; the game's state put back
static void FogDraw(ID3D11DeviceContext *ctx, ID3D11RenderTargetView *target, ID3D11Resource *res, DXGI_FORMAT view)
{
    __try {
        ID3D11Device *dev = NULL; ctx->GetDevice(&dev);
        bool ok = dev && FogBuild(dev) && FogCopyOf(dev, res, view);
        if (ok && !g_fog.pyrSrv && g_fog.pyr) {
            D3D11_SHADER_RESOURCE_VIEW_DESC sd = {}; sd.Format = DXGI_FORMAT_R32_FLOAT; sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; sd.Texture2D.MostDetailedMip = 0; sd.Texture2D.MipLevels = 1;
            if (FAILED(dev->CreateShaderResourceView(g_fog.pyr, &sd, &g_fog.pyrSrv))) { strcpy_s(g_fog.why, sizeof(g_fog.why), "no view on the depth pyramid"); ok = false; }
        }
        if (dev) dev->Release();
        if (!ok || !g_fog.pyrSrv) return;
        g_fog.inDraw = true;
        // the game's state
        ID3D11RenderTargetView *rtv[8] = {}; ID3D11DepthStencilView *dsv = NULL; ctx->OMGetRenderTargets(8, rtv, &dsv);
        ID3D11BlendState *bs = NULL; FLOAT bf[4]; UINT bm = 0; ctx->OMGetBlendState(&bs, bf, &bm);
        ID3D11DepthStencilState *ds = NULL; UINT sref = 0; ctx->OMGetDepthStencilState(&ds, &sref);
        ID3D11RasterizerState *rs = NULL; ctx->RSGetState(&rs);
        D3D11_VIEWPORT vp[16]; UINT vpn = 16; ctx->RSGetViewports(&vpn, vp);
        ID3D11InputLayout *il = NULL; ctx->IAGetInputLayout(&il);
        D3D11_PRIMITIVE_TOPOLOGY topo; ctx->IAGetPrimitiveTopology(&topo);
        ID3D11VertexShader *vs = NULL; ctx->VSGetShader(&vs, NULL, NULL);
        ID3D11PixelShader *ps = NULL; ctx->PSGetShader(&ps, NULL, NULL);
        ID3D11GeometryShader *gs = NULL; ctx->GSGetShader(&gs, NULL, NULL);
        ID3D11ShaderResourceView *srv[2] = {}; ctx->PSGetShaderResources(0, 2, srv);
        ID3D11Buffer *cb = NULL; ctx->PSGetConstantBuffers(0, 1, &cb);
        // ours
        FogParams p = g_fog.p;
        if (g_fog.test) { p.color[0] = 1; p.color[1] = 0; p.color[2] = 1; p.opacity = 0.5f; p.mode = 0; p.curve = 0; p.start = 0; p.end = 1; p.sky = 1; }
        p.srgb = FogIsSrgb(view) ? 1.0f : 0.0f;
        D3D11_MAPPED_SUBRESOURCE m;
        if (SUCCEEDED(ctx->Map(g_fog.cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) { memcpy(m.pData, &p, sizeof(p)); ctx->Unmap(g_fog.cb, 0); }
        ctx->CopyResource(g_fog.copy.tex, res);
        g_omSet(ctx, 1, &target, NULL);
        D3D11_VIEWPORT v = { 0, 0, (FLOAT)g_fog.copy.d.Width, (FLOAT)g_fog.copy.d.Height, 0, 1 };
        g_setVP(ctx, 1, &v);
        g_setIL(ctx, NULL);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        g_setVS(ctx, g_fog.vs, NULL, 0);
        g_setPS(ctx, g_fog.copy.d.SampleDesc.Count > 1 ? g_fog.psms : g_fog.ps, NULL, 0);
        ctx->GSSetShader(NULL, NULL, 0);
        ID3D11ShaderResourceView *ours[2] = { g_fog.copy.srv, g_fog.pyrSrv };
        ctx->PSSetShaderResources(0, 2, ours);
        ctx->PSSetConstantBuffers(0, 1, &g_fog.cb);
        ctx->RSSetState(g_fog.rs);
        g_setBS(ctx, g_fog.bs, NULL, 0xFFFFFFFF);
        g_setDS(ctx, g_fog.ds, 0);
        ctx->Draw(3, 0);                               // through the table: the runtime switches its draw entries
        // the game's state back
        ID3D11ShaderResourceView *none[2] = {};
        ctx->PSSetShaderResources(0, 2, none);
        g_omSet(ctx, 8, rtv, dsv);
        g_setVP(ctx, vpn, vp);
        g_setIL(ctx, il);
        ctx->IASetPrimitiveTopology(topo);
        g_setVS(ctx, vs, NULL, 0);
        g_setPS(ctx, ps, NULL, 0);
        ctx->GSSetShader(gs, NULL, 0);
        ctx->PSSetShaderResources(0, 2, srv);
        ctx->PSSetConstantBuffers(0, 1, &cb);
        ctx->RSSetState(rs);
        g_setBS(ctx, bs, bf, bm);
        g_setDS(ctx, ds, sref);
        for (int i = 0; i < 8; i++) if (rtv[i]) rtv[i]->Release();
        if (dsv) dsv->Release(); if (bs) bs->Release(); if (ds) ds->Release(); if (rs) rs->Release(); if (il) il->Release();
        if (vs) vs->Release(); if (ps) ps->Release(); if (gs) gs->Release(); if (cb) cb->Release();
        for (int i = 0; i < 2; i++) if (srv[i]) srv[i]->Release();
        g_fog.drawn = true; g_fog.inDraw = false;
        InterlockedIncrement(&g_fog.draws);
    } __except (EXCEPTION_EXECUTE_HANDLER) { g_fog.inDraw = false; strcpy_s(g_fog.why, sizeof(g_fog.why), "a fault drawing the fog (caught): fog off"); InterlockedExchange(&g_fog.on, 0); }
}

// render thread, before a bind: the depth pyramid found by its shape; the scene's own target noted (its depth is the
// scene's); the scene done when, after it, the frame binds a smaller target (its bloom)
static void FogOnBind(UINT n, ID3D11RenderTargetView *const *rtv)
{
    if (!g_fog.on || !g_st.bbW || g_fog.drawn) return;
    __try {
        ID3D11RenderTargetView *r0 = n > 0 && rtv ? rtv[0] : NULL;
        if (!r0) return;
        ID3D11Resource *r = NULL; r0->GetResource(&r);
        if (!r) return;
        ID3D11Texture2D *t = NULL; D3D11_TEXTURE2D_DESC d = {};
        if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&t)) && t) { t->GetDesc(&d); t->Release(); }
        bool full = d.Width == g_st.bbW && d.Height == g_st.bbH;
        if (!g_fog.pyr && full && d.Format == DXGI_FORMAT_R32_FLOAT && d.MipLevels > 1 && d.SampleDesc.Count == 1) { g_fog.pyr = r; r->AddRef(); Log("fog: the depth pyramid is %p", r); }
        if (g_st.scene && full) {                      // the scene's colour target, with the scene's depth
            if (g_fog.sceneRes != r) {
                if (g_fog.sceneRtv) g_fog.sceneRtv->Release(); if (g_fog.sceneRes) g_fog.sceneRes->Release();
                g_fog.sceneRtv = r0; r0->AddRef(); g_fog.sceneRes = r; r->AddRef();
                D3D11_RENDER_TARGET_VIEW_DESC vd; r0->GetDesc(&vd); g_fog.sceneFmt = vd.Format;
                Log("fog: the scene is %p (%ux%u x%u, viewed as format %u)", r, d.Width, d.Height, d.SampleDesc.Count, (UINT)vd.Format);
            }
            g_fog.sceneSeen = true;
        }
        else if (g_fog.sceneSeen && d.Width && d.Width < g_st.bbW && !g_fog.where) g_fog.armed = true;   // the scene is done (dev37: the fog waits for its resolved copy to be read)
        r->Release();
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static void FogAfterBind(ID3D11DeviceContext *ctx)
{
    if (!g_fog.now) return;
    g_fog.now = false;
    if (g_fog.drawn || !g_fog.sceneRtv) return;
    if (!FogWanted()) { InterlockedIncrement(&g_fog.skipped); g_fog.drawn = true; return; }
    FogDraw(ctx, g_fog.sceneRtv, g_fog.sceneRes, g_fog.sceneFmt);
}

// render thread, at Present ("where present"): into the finished frame
static void FogPresent(IDXGISwapChain *sc)
{
    if (!g_fog.on || g_fog.where != 1 || g_fog.drawn || !g_gfx.ctx) return;
    if (!FogWanted()) { InterlockedIncrement(&g_fog.skipped); return; }
    __try {
        ID3D11Texture2D *bb = NULL;
        if (FAILED(sc->GetBuffer(0, __uuidof(ID3D11Texture2D), (void **)&bb)) || !bb) return;
        if (g_fog.bb != bb) {
            if (g_fog.bbRtv) { g_fog.bbRtv->Release(); g_fog.bbRtv = NULL; }
            ID3D11Device *dev = NULL; bb->GetDevice(&dev);
            if (dev) { dev->CreateRenderTargetView(bb, NULL, &g_fog.bbRtv); dev->Release(); }
            g_fog.bb = bb;                             // compared by address only; the swap chain keeps it
        }
        D3D11_TEXTURE2D_DESC d; bb->GetDesc(&d);
        if (g_fog.bbRtv) FogDraw(g_gfx.ctx, g_fog.bbRtv, bb, d.Format);
        bb->Release();
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static void FogPresentEnd() { g_fog.drawn = false; g_fog.sceneSeen = false; g_fog.now = false; g_fog.armed = false; }

static void FogCommand(char *arg, char *out, size_t cap)
{
    if (!_stricmp(arg, "on") || !_stricmp(arg, "test")) {
        if (!InstallPresentHook()) { _snprintf_s(out, cap, _TRUNCATE, "error no way to see the frames: Present is not hooked"); return; }
        InterlockedExchange(&g_gfxInstallWant, 1);
        InterlockedExchange(&g_fog.test, !_stricmp(arg, "test") ? 1 : 0);
        g_fog.why[0] = 0;
        InterlockedExchange(&g_fog.on, 1);
    }
    else if (!_stricmp(arg, "off")) { InterlockedExchange(&g_fog.on, 0); InterlockedExchange(&g_fog.test, 0); }
    else if (!_stricmp(arg, "lock on")) InterlockedExchange(&g_fog.locked, 1);
    else if (!_stricmp(arg, "lock off")) InterlockedExchange(&g_fog.locked, 0);
    else if (!_stricmp(arg, "where scene")) InterlockedExchange(&g_fog.where, 0);
    else if (!_stricmp(arg, "where present")) InterlockedExchange(&g_fog.where, 1);
    else if (!_strnicmp(arg, "set ", 4)) {
        FogParams p = g_fog.p;
        char *ctxs = NULL;
        for (char *tok = strtok_s(arg + 4, " ", &ctxs); tok; tok = strtok_s(NULL, " ", &ctxs)) {
            char *eq = strchr(tok, '='); if (!eq) continue; *eq = 0; const char *v = eq + 1;
            if (!_stricmp(tok, "color")) { float r = 0, g = 0, b = 0; if (sscanf_s(v, "%f,%f,%f", &r, &g, &b) == 3) { p.color[0] = r / 255.0f; p.color[1] = g / 255.0f; p.color[2] = b / 255.0f; } }
            else if (!_stricmp(tok, "start")) p.start = (float)atof(v);
            else if (!_stricmp(tok, "end")) p.end = (float)atof(v);
            else if (!_stricmp(tok, "density")) p.density = (float)atof(v);
            else if (!_stricmp(tok, "opacity")) p.opacity = (float)atof(v);
            else if (!_stricmp(tok, "sky")) p.sky = (float)atof(v);
            else if (!_stricmp(tok, "curve")) p.curve = (*v >= '0' && *v <= '9') ? (float)min(2, max(0, atoi(v))) : !_stricmp(v, "exp") ? 1.0f : !_stricmp(v, "exp2") ? 2.0f : 0.0f;
            else if (!_stricmp(tok, "mode")) { if (*v >= '0' && *v <= '9') p.mode = (float)min(7, max(0, atoi(v))); else for (int i = 0; i < 8; i++) if (!_stricmp(v, kFogModes[i])) p.mode = (float)i; }
        }
        g_fog.p = p;                                   // one 48-byte copy; the render thread reads it once a frame
    }
    else if (_stricmp(arg, "status") && *arg) { _snprintf_s(out, cap, _TRUNCATE, "error usage: fog on | off | test | lock on|off | where scene|present | set color=R,G,B start= end= density= opacity= curve=linear|exp|exp2 sky= mode=normal|add|screen|multiply|overlay|softlight|dodge|burn | status"); return; }
    FogParams &p = g_fog.p;
    _snprintf_s(out, cap, _TRUNCATE, "fog on %ld locked %ld test %ld where %s color %d,%d,%d start %.0f end %.0f density %.5f opacity %.2f curve %s sky %.2f mode %s | drawn %ld (into the resolved scene %ld) skipped %ld ready %d scene %p resolved %p pyramid %p%s%s",
        g_fog.on, g_fog.locked, g_fog.test, g_fog.where ? "present" : "scene", (int)(p.color[0] * 255 + 0.5f), (int)(p.color[1] * 255 + 0.5f), (int)(p.color[2] * 255 + 0.5f), p.start, p.end, p.density, p.opacity,
        p.curve > 1.5f ? "exp2" : p.curve > 0.5f ? "exp" : "linear", p.sky, kFogModes[(int)p.mode & 7], g_fog.draws, g_fog.atRead, g_fog.skipped, g_fog.ready ? 1 : 0, g_fog.sceneRes, g_fog.resolved, g_fog.pyr,
        g_fog.why[0] ? " | " : "", g_fog.why);
}

// ---- dev36-dev37: what the frame does with the scene once it is drawn (2026-10-17) ------------------------------------------
// 0518: the fog drawn into the scene when the frame first binds a smaller target (its bloom) changed nothing on screen,
// while the same fog drawn into the finished frame showed. 0519 (the probe's pixel-shader reads): the game resolves the
// multisampled scene into a single-sample texture of its own between its last scene bind and the bloom's bind, and
// both the bloom and the final composite read that texture. The resolve and the copies cannot be followed (the runtime
// puts its own ResolveSubresource and CopyResource back, as it does the draws), but what the pixel shaders read can:
// PSSetShaderResources holds. So the fog now goes into that resolved scene the moment the bloom is about to read it,
// and the game's own bloom and grade run on it. The probe lists, outside the scene, what the pixel shaders read
// (ps-srv), and the players' entity records (0xF8 bytes each from RVA 0x2D2D7B0), toward the players pass's guns.
typedef void (STDMETHODCALLTYPE *SetPSSRV_t)(ID3D11DeviceContext *, UINT, UINT, ID3D11ShaderResourceView *const *);
static SetPSSRV_t g_setPSSRV;

static bool SrvLogOn() { return g_gfx.phase && g_gfx.op == 1 && g_gfx.f; }

// render thread, a pixel shader's textures about to be bound, the fog armed (the scene is done): the first full-size,
// single-sample texture it reads is the resolved scene; the fog goes into it first
static void FogOnPSSRV(ID3D11DeviceContext *ctx, UINT n, ID3D11ShaderResourceView *const *v)
{
    if (!g_fog.armed || g_fog.inDraw || g_fog.drawn || !v) return;
    for (UINT i = 0; i < n && i < 8; i++) {
        if (!v[i]) continue;
        ID3D11Resource *r = NULL; v[i]->GetResource(&r);
        if (!r) continue;
        D3D11_TEXTURE2D_DESC d = {}; ID3D11Texture2D *t = NULL;
        bool is2d = SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&t)) && t;
        if (is2d) { t->GetDesc(&d); t->Release(); }
        bool scene = is2d && d.Width == g_st.bbW && d.Height == g_st.bbH && d.SampleDesc.Count == 1 && (d.BindFlags & D3D11_BIND_RENDER_TARGET) && r != g_fog.sceneRes;
        if (scene) {
            g_fog.armed = false;
            if (!FogWanted()) { InterlockedIncrement(&g_fog.skipped); g_fog.drawn = true; }
            else {
                if (g_fog.resolved != r || !g_fog.resolvedRtv) {
                    if (g_fog.resolvedRtv) { g_fog.resolvedRtv->Release(); g_fog.resolvedRtv = NULL; }
                    ID3D11Device *dev = NULL; ctx->GetDevice(&dev);
                    if (dev) { dev->CreateRenderTargetView(r, NULL, &g_fog.resolvedRtv); dev->Release(); }
                    g_fog.resolved = r;                      // compared by address; our view keeps it alive
                    Log("fog: the resolved scene is %p (%ux%u, format %u)%s", r, d.Width, d.Height, (UINT)d.Format, g_fog.resolvedRtv ? "" : ": no target view on it");
                }
                if (g_fog.resolvedRtv) { FogDraw(ctx, g_fog.resolvedRtv, r, d.Format); InterlockedIncrement(&g_fog.atRead); }
            }
        }
        r->Release();
        if (scene) return;
    }
}

static void STDMETHODCALLTYPE GfxSetPSSRV(ID3D11DeviceContext *ctx, UINT start, UINT n, ID3D11ShaderResourceView *const *v)
{
    if (ctx == g_gfx.ctx) {
        if (SrvLogOn() && !g_st.scene && v && !g_fog.inDraw) __try {
            GfxFlushDraws();
            fprintf(g_gfx.f, "  ps-srv %u:", start);
            for (UINT i = 0; i < n && i < 16; i++) { char d[200]; GfxDesc(v[i], d, sizeof(d)); fprintf(g_gfx.f, " %s", d); }
            fprintf(g_gfx.f, "\n"); g_gfx.events++;
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        __try { FogOnPSSRV(ctx, n, v); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    g_setPSSRV(ctx, start, n, v);
}

static bool SrvInstall(void **vt) { return GfxHookSlot(vt, 8, (void *)&GfxSetPSSRV, (void **)&g_setPSSRV); }

// the probe's frame: every player's entity record (finding where each one stands; memory read only)
static void PlayersDump(FILE *f)
{
    fprintf(f, "# players: slot, held weapon (+0x48; 255 none), then the 0xF8-byte entity record as 62 words (hex)\n");
    for (int e = 0; e < 18; e++) {
        DWORD v[62]; BYTE w = 255;
        if (!SafeCopy(v, g_base + kRvaPlayers + e * kPlayerStride, sizeof(v))) continue;
        SafeCopy(&w, g_base + kRvaPlayers + e * kPlayerStride + kPlayerWeapon, 1);
        fprintf(f, "player %d %d:", e, (int)w);
        for (int i = 0; i < 62; i++) fprintf(f, " %08lX", v[i]);
        fprintf(f, "\n");
    }
}

// ---- dev37: finding a value in the game's memory (2026-10-17) -------------------------------------------------------------
// "mem findf <x> <y> <z> [tol]": three floats in a row, each within tol (default 0.01); "mem find4 <hex>": a 4-byte
// value. Every committed writable page of the process is read (nothing is written); up to 32 places are listed, each as
// an address, and as exe+RVA when it lies in the game's own image (a global, the same every run).
static void MemCommand(const char *arg, char *out, size_t cap)
{
    float want[3] = {}, tol = 0.01f; DWORD w4 = 0; int mode = 0;
    if (!_strnicmp(arg, "findf ", 6)) { if (sscanf_s(arg + 6, "%f %f %f %f", &want[0], &want[1], &want[2], &tol) >= 3) mode = 1; }
    else if (!_strnicmp(arg, "find4 ", 6)) { if (sscanf_s(arg + 6, "%lx", &w4) == 1) mode = 2; }
    if (!mode) { _snprintf_s(out, cap, _TRUNCATE, "error usage: mem findf <x> <y> <z> [tol] | mem find4 <hex>"); return; }
    BYTE *exe = (BYTE *)GetModuleHandleA(NULL); DWORD exeSize = 0;
    __try { IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(exe + ((IMAGE_DOS_HEADER *)exe)->e_lfanew); exeSize = nt->OptionalHeader.SizeOfImage; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    strcpy_s(out, cap, "ok"); int found = 0; double mb = 0; DWORD t0 = GetTickCount();
    const BYTE *mine = (const BYTE *)want;
    MEMORY_BASIC_INFORMATION mi; BYTE *p = NULL;
    while (found < 32 && (size_t)p < 0x7FFE0000 && VirtualQuery(p, &mi, sizeof(mi)) == sizeof(mi)) {
        DWORD pr = mi.Protect;
        bool rw = mi.State == MEM_COMMIT && (pr & (PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) && !(pr & (PAGE_GUARD | PAGE_NOACCESS | PAGE_NOCACHE | PAGE_WRITECOMBINE));   // not the driver's upload memory
        if (rw) {
            mb += mi.RegionSize / 1048576.0;
            const DWORD *q = (const DWORD *)mi.BaseAddress; size_t nw = mi.RegionSize / 4;
            __try {
                for (size_t i = 0; i + 2 < nw && found < 32; i++) {
                    bool hit;
                    if (mode == 2) hit = q[i] == w4;
                    else { const float *f = (const float *)(q + i); hit = fabsf(f[0] - want[0]) <= tol && fabsf(f[1] - want[1]) <= tol && fabsf(f[2] - want[2]) <= tol; }
                    if (!hit || ((const BYTE *)(q + i) >= mine && (const BYTE *)(q + i) < mine + sizeof(want))) continue;
                    const BYTE *a = (const BYTE *)(q + i); char one[48];
                    if (a >= exe && a < exe + exeSize) _snprintf_s(one, sizeof(one), _TRUNCATE, " %p(exe+%lX)", a, (DWORD)(a - exe));
                    else _snprintf_s(one, sizeof(one), _TRUNCATE, " %p", a);
                    if (strlen(out) + strlen(one) + 64 < cap) strcat_s(out, cap, one);
                    found++;
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        }
        p = (BYTE *)mi.BaseAddress + mi.RegionSize;
    }
    char tail[64]; _snprintf_s(tail, sizeof(tail), _TRUNCATE, " | %d found in %.0f MB, %lu ms", found, mb, GetTickCount() - t0);
    strcat_s(out, cap, tail);
}

// ---- the playback engine, step 1: parameters keyed in time (dev33, 2026-10-17) -----------------------------------------
// Mycelium (10-17): "BO2 as the playback engine - Lets start laying this in Place"; his words of 10-17 ~03:00: "Keyframing
// Parameters in the Game itself ... seeing how the keyframes effect BO2 when played back". A parameter track is a text
// file of keys, "tick name=value name=value ...", and lines starting with # are notes:
//   fog.on (0|1, held)  fog.r fog.g fog.b (0-255)  fog.start fog.end (game units)  fog.density  fog.opacity  fog.sky
//   fog.mode (normal|add|screen|multiply|overlay|softlight|dodge|burn, held)  fog.curve (linear|exp|exp2, held)
//   dvar.<name> (any game dvar, a number; sent through the console, so it may land one frame late)
// dev40: and events, "<tick> !cmd <console command>", "<tick> !write <hex address> <hex bytes>" (the game's own image
// only), "<tick> !bridge fog ...|dolly ...": all the events of the last tick reached run once, in order (the Console's
// "exact moves" write every moving slider's lines for every frame, so each tick's events are a whole state)
// Numbers are interpolated linearly between their keys (held before the first and after the last); words are held.
// Applied on the game's main thread at every step of a recording ("rec start ...|params=<file>", exact to the tick:
// the step waits until every frame before it has been shown, so the next frame is the one the values are for), and,
// with "params live on", while the film plays (the preview). "params load <file> | clear | live on|off | at <tick> | status"
struct ParamKey { int tick; float v; };
struct ParamTrack { char name[64]; bool word; ParamKey *k; int n, cap; float last; bool sent; };
static struct {
    CRITICAL_SECTION lock; bool init;
    ParamTrack t[48]; int n;
    char file[MAX_PATH];
    volatile LONG live, applied;
    int lastTick;
    struct Ev { int tick; char kind; char *text; } *ev; int evN, evCap;   // dev40: "<tick> !cmd|!write|!bridge <text>"
    int evGroup; volatile LONG evRun, evBad;            // the group (its first event) run last; events run, refused
    bool byRec;                                         // dev40: loaded by "rec start ...|params=": gone at its stop
} g_par;

static float ParWord(const char *name, const char *v)
{
    if (*v >= '0' && *v <= '9') return (float)atof(v);   // dev37: a number for a mode or a curve too
    if (strstr(name, "mode")) { for (int i = 0; i < 8; i++) if (!_stricmp(v, kFogModes[i])) return (float)i; return 0; }
    if (strstr(name, "curve")) return !_stricmp(v, "exp") ? 1.0f : !_stricmp(v, "exp2") ? 2.0f : 0.0f;
    return (float)atof(v);
}

static ParamTrack *ParTrack(const char *name)
{
    for (int i = 0; i < g_par.n; i++) if (!_stricmp(g_par.t[i].name, name)) return &g_par.t[i];
    if (g_par.n >= 48) return NULL;
    ParamTrack *t = &g_par.t[g_par.n++]; memset(t, 0, sizeof(*t));
    strcpy_s(t->name, sizeof(t->name), name);
    t->word = strstr(name, "mode") || strstr(name, "curve") || !_stricmp(name, "fog.on");
    t->last = -1e30f;
    return t;
}

static void ParClearLocked()
{
    for (int i = 0; i < g_par.n; i++) free(g_par.t[i].k);
    g_par.n = 0; g_par.file[0] = 0;
    for (int i = 0; i < g_par.evN; i++) free(g_par.ev[i].text);
    g_par.evN = 0; g_par.evGroup = -1;
}

// dev40: one event line, "<tick> !cmd <console command>" | "!write <hex address> <hex bytes>" | "!bridge fog ...|dolly ..."
static bool ParEvent(int tick, char *p)
{
    char kind = !_strnicmp(p, "!cmd ", 5) ? 'c' : !_strnicmp(p, "!write ", 7) ? 'w' : !_strnicmp(p, "!bridge ", 8) ? 'b' : 0;
    if (!kind) return false;
    p += kind == 'c' ? 5 : kind == 'w' ? 7 : 8;
    size_t L = strlen(p); while (L && (p[L - 1] == '\r' || p[L - 1] == '\n' || p[L - 1] == ' ')) p[--L] = 0;
    if (!L) return false;
    if (g_par.evN == g_par.evCap) {
        int c = g_par.evCap ? g_par.evCap * 2 : 256;
        void *e = realloc(g_par.ev, c * sizeof(*g_par.ev)); if (!e) return false;
        g_par.ev = (decltype(g_par.ev))e; g_par.evCap = c;
    }
    int at = g_par.evN; while (at > 0 && g_par.ev[at - 1].tick > tick) at--;    // the file is in tick order; kept so if not
    memmove(g_par.ev + at + 1, g_par.ev + at, (g_par.evN - at) * sizeof(*g_par.ev));
    g_par.ev[at].tick = tick; g_par.ev[at].kind = kind; g_par.ev[at].text = _strdup(p);
    g_par.evN++;
    return true;
}

static bool ParLoad(const char *file, char *why, size_t cap)
{
    FILE *f = NULL;
    if (fopen_s(&f, file, "r") || !f) { _snprintf_s(why, cap, _TRUNCATE, "cannot read %s", file); return false; }
    if (!g_par.init) { InitializeCriticalSection(&g_par.lock); g_par.init = true; }
    EnterCriticalSection(&g_par.lock);
    ParClearLocked();
    char line[2048]; int keys = 0, lines = 0;
    while (fgets(line, sizeof(line), f)) {
        char *p = line; while (*p == ' ' || *p == '\t') p++;
        if (*p == '#' || *p == '\r' || *p == '\n' || !*p) continue;
        lines++;
        int tick = atoi(p);
        while (*p && *p != ' ' && *p != '\t') p++;
        while (*p == ' ' || *p == '\t') p++;
        if (*p == '!') { if (ParEvent(tick, p)) keys++; continue; }   // dev40
        char *ctx = NULL;
        for (char *tok = strtok_s(p, " \t\r\n", &ctx); tok; tok = strtok_s(NULL, " \t\r\n", &ctx)) {
            char *eq = strchr(tok, '='); if (!eq) continue; *eq = 0;
            ParamTrack *t = ParTrack(tok);
            if (!t) continue;
            if (t->n == t->cap) { int c = t->cap ? t->cap * 2 : 16; ParamKey *k = (ParamKey *)realloc(t->k, c * sizeof(ParamKey)); if (!k) continue; t->k = k; t->cap = c; }
            int at = t->n; while (at > 0 && t->k[at - 1].tick > tick) at--;
            memmove(t->k + at + 1, t->k + at, (t->n - at) * sizeof(ParamKey));
            t->k[at].tick = tick; t->k[at].v = ParWord(tok, eq + 1); t->n++;
            keys++;
        }
    }
    fclose(f);
    strcpy_s(g_par.file, sizeof(g_par.file), file);
    int tracks = g_par.n, events = g_par.evN;
    g_par.evGroup = -1;
    LeaveCriticalSection(&g_par.lock);
    _snprintf_s(why, cap, _TRUNCATE, "%d keys in %d tracks and %d events from %d lines", keys, tracks, events, lines);
    return true;
}

static float ParAt(const ParamTrack &t, int tick)
{
    if (!t.n) return 0;
    if (tick <= t.k[0].tick) return t.k[0].v;
    if (tick >= t.k[t.n - 1].tick) return t.k[t.n - 1].v;
    int i = 1; while (i < t.n && t.k[i].tick < tick) i++;
    const ParamKey &a = t.k[i - 1], &b = t.k[i];
    if (t.word) return tick >= b.tick ? b.v : a.v;
    float u = b.tick > a.tick ? (float)(tick - a.tick) / (float)(b.tick - a.tick) : 1.0f;
    return a.v + (b.v - a.v) * u;
}

static void FogCommand(char *arg, char *out, size_t cap);
static void DollyCommand(const char *arg, char *out, size_t cap);

// dev40: the events of the last tick at or before this one, once (each tick's events are a whole state: the Console
// writes every moving slider's line for every frame), so a jump back or a second pass of the same ticks runs them again
static void ParEvents(int tick)
{
    int i = g_par.evN - 1; while (i >= 0 && g_par.ev[i].tick > tick) i--;
    if (i < 0) return;
    int g = i; while (g > 0 && g_par.ev[g - 1].tick == g_par.ev[i].tick) g--;
    if (g == g_par.evGroup) return;
    g_par.evGroup = g;
    BYTE *exe = (BYTE *)GetModuleHandleA(NULL); DWORD exeSize = 0;
    __try { IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(exe + ((IMAGE_DOS_HEADER *)exe)->e_lfanew); exeSize = nt->OptionalHeader.SizeOfImage; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    for (int k = g; k <= i; k++) {
        const char *t = g_par.ev[k].text; bool ok = false;
        __try {
            if (g_par.ev[k].kind == 'c' && g_exec) { char c[1100]; _snprintf_s(c, sizeof(c), _TRUNCATE, "%s\n", t); g_exec(c, false); ok = true; }
            else if (g_par.ev[k].kind == 'w') {
                unsigned addr = 0; char hex[961] = "";
                if (sscanf_s(t, "%x %960s", &addr, hex, (unsigned)sizeof(hex)) == 2 && !(strlen(hex) % 2)) {
                    BYTE b[480]; size_t n = strlen(hex) / 2;
                    for (size_t j = 0; j < n; j++) { char h2[3] = { hex[2 * j], hex[2 * j + 1], 0 }; b[j] = (BYTE)strtoul(h2, NULL, 16); }
                    BYTE *a = (BYTE *)(size_t)addr; DWORD old;
                    if (a >= exe && a + n <= exe + exeSize && VirtualProtect(a, n, PAGE_EXECUTE_READWRITE, &old)) { memcpy(a, b, n); VirtualProtect(a, n, old, &old); ok = true; }   // the game's own image only
                }
            }
            else if (g_par.ev[k].kind == 'b') {
                char c[1024], o[1024] = ""; strcpy_s(c, sizeof(c), t);
                if (!_strnicmp(c, "fog ", 4)) { FogCommand(c + 4, o, sizeof(o)); ok = _strnicmp(o, "error", 5) != 0; }
                else if (!_strnicmp(c, "dolly ", 6)) { DollyCommand(c + 6, o, sizeof(o)); ok = _strnicmp(o, "error", 5) != 0; }
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
        InterlockedIncrement(ok ? &g_par.evRun : &g_par.evBad);
    }
}

// main thread: every value at this tick; the fog's straight into its parameters, the dvars through the console
static void ParApply(int tick)
{
    if (!g_par.init || (!g_par.n && !g_par.evN)) return;
    EnterCriticalSection(&g_par.lock);
    ParEvents(tick);
    FogParams p = g_fog.p; bool fog = false; int on = -1;
    for (int i = 0; i < g_par.n; i++) {
        ParamTrack &t = g_par.t[i];
        float v = ParAt(t, tick);
        const char *n = t.name;
        if (!_strnicmp(n, "fog.", 4)) {
            fog = true; n += 4;
            if (!_stricmp(n, "on")) on = v >= 0.5f ? 1 : 0;
            else if (!_stricmp(n, "r")) p.color[0] = v / 255.0f; else if (!_stricmp(n, "g")) p.color[1] = v / 255.0f; else if (!_stricmp(n, "b")) p.color[2] = v / 255.0f;
            else if (!_stricmp(n, "start")) p.start = v; else if (!_stricmp(n, "end")) p.end = v; else if (!_stricmp(n, "density")) p.density = v;
            else if (!_stricmp(n, "opacity")) p.opacity = v; else if (!_stricmp(n, "sky")) p.sky = v;
            else if (!_stricmp(n, "mode")) p.mode = v; else if (!_stricmp(n, "curve")) p.curve = v;
        }
        else if (!_strnicmp(n, "dvar.", 5) && g_exec) {
            if (!t.sent || fabsf(v - t.last) > 1e-4f * (fabsf(v) + 1)) {
                char c[160]; _snprintf_s(c, sizeof(c), _TRUNCATE, "set %s %g\n", n + 5, v);
                g_exec(c, false);
                t.last = v; t.sent = true;
            }
        }
    }
    LeaveCriticalSection(&g_par.lock);
    if (fog) {
        g_fog.p = p;
        if (on >= 0) InterlockedExchange(&g_fog.on, on);
        if (on == 1) { InterlockedExchange(&g_fog.locked, 1); InterlockedExchange(&g_gfxInstallWant, 1); }   // a keyed fog is meant for the takes
    }
    g_par.lastTick = tick;
    InterlockedIncrement(&g_par.applied);
}

// dev40: a recording's own parameters do not outlive it (the next recording, without any, must not replay them)
static void ParRecLoaded() { g_par.byRec = true; }
static void ParRecDone()
{
    if (!g_par.byRec || !g_par.init) return;
    EnterCriticalSection(&g_par.lock); ParClearLocked(); LeaveCriticalSection(&g_par.lock);
    g_par.byRec = false;
    Log("rec: its parameters cleared");
}

static void ParamsCommand(char *arg, char *out, size_t cap)
{
    char why[200] = "";
    if (!_strnicmp(arg, "load ", 5)) {
        if (!ParLoad(arg + 5, why, sizeof(why))) { _snprintf_s(out, cap, _TRUNCATE, "error %s", why); return; }
    }
    else if (!_stricmp(arg, "clear")) { if (g_par.init) { EnterCriticalSection(&g_par.lock); ParClearLocked(); LeaveCriticalSection(&g_par.lock); } InterlockedExchange(&g_par.live, 0); }
    else if (!_stricmp(arg, "live on")) {
        if (!InstallLockstep(why, sizeof(why))) { _snprintf_s(out, cap, _TRUNCATE, "error the per-step hook: %s", why); return; }
        InterlockedExchange(&g_par.live, 1);
    }
    else if (!_stricmp(arg, "live off")) InterlockedExchange(&g_par.live, 0);
    else if (!_strnicmp(arg, "at ", 3)) ParApply(atoi(arg + 3));
    else if (_stricmp(arg, "status") && *arg) { _snprintf_s(out, cap, _TRUNCATE, "error usage: params load <file> | clear | live on|off | at <tick> | status"); return; }
    int keys = 0; for (int i = 0; i < g_par.n; i++) keys += g_par.t[i].n;
    _snprintf_s(out, cap, _TRUNCATE, "params %d tracks %d keys %d events (run %ld, refused %ld) live %ld applied %ld last_tick %d file %s%s%s", g_par.n, keys, g_par.evN, g_par.evRun, g_par.evBad,
        g_par.live, g_par.applied, g_par.lastTick, g_par.file[0] ? g_par.file : "-", why[0] ? " | " : "", why);
}

// main thread, each step (RecFixedTime): the keyed values for the frame this step makes (recording, or the live preview)
static void ParStep(int step)
{
    if (!g_par.init || (!g_par.n && !g_par.evN) || !(g_rec.active || g_par.live)) return;
    __try { ParApply(ReadInt(kRvaTick) + step); } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// dev26: a recording's own rule ("rec start ...|only=world|players"): set at its start, taken off at its stop
static void RecOnly(int kind)
{
    if (kind) InterlockedExchange(&g_gfxInstallWant, 1);
    g_st.ptr = NULL; g_st.except = false; g_st.kind = kind;
    if (kind) Log("rec: only the %s", kind == 7 ? "world (the players hidden)" : "players (the rest hidden)");
}

// render thread, at a Present: the context's methods hooked once (the font may have OMSetRenderTargets already)
static bool GfxInstall(IDXGISwapChain *sc)
{
    if (g_gfx.hooked) return true;
    ID3D11Device *dev = NULL; ID3D11DeviceContext *ctx = NULL;
    if (FAILED(sc->GetDevice(__uuidof(ID3D11Device), (void **)&dev)) || !dev) { strcpy_s(g_gfx.why, sizeof(g_gfx.why), "no Direct3D 11 device behind the swap chain"); return false; }
    dev->GetImmediateContext(&ctx); dev->Release();
    if (!ctx) { strcpy_s(g_gfx.why, sizeof(g_gfx.why), "no immediate context"); return false; }
    void **vt = *(void ***)ctx;
    bool ok = true;
    if (!g_omSlot) { void *o = NULL; if (GfxHookSlot(vt, 33, (void *)&HookOMSetRenderTargets, &o)) { g_omSet = (OMSetRenderTargets_t)o; g_omSlot = &vt[33]; } else ok = false; }
    ok = ok && GfxHookSlot(vt, 50, (void *)&GfxClearRtv, (void **)&g_clearRtv) && GfxHookSlot(vt, 53, (void *)&GfxClearDsv, (void **)&g_clearDsv)
            && GfxHookSlot(vt, 13, (void *)&GfxDraw, (void **)&g_draw) && GfxHookSlot(vt, 12, (void *)&GfxDrawIdx, (void **)&g_drawIdx)
            && GfxHookSlot(vt, 20, (void *)&GfxDrawIdxInst, (void **)&g_drawIdxInst) && GfxHookSlot(vt, 21, (void *)&GfxDrawInst, (void **)&g_drawInst)
            && GfxHookSlot(vt, 34, (void *)&GfxOMSetUav, (void **)&g_omSetUav) && GfxHookSlot(vt, 58, (void *)&GfxExecCl, (void **)&g_execCl)
            && StInstall(vt) && SeqInstall(vt) && SrvInstall(vt);
    { DXGI_SWAP_CHAIN_DESC sd; if (SUCCEEDED(sc->GetDesc(&sd))) { g_st.bbW = sd.BufferDesc.Width; g_st.bbH = sd.BufferDesc.Height; } }
    if (ok && !TessInstall(g_tessWhy, sizeof(g_tessWhy))) Log("gfx: the surface routines are not followed: %s", g_tessWhy);   // render thread: none of them runs now
    g_gfx.ctx = ctx; ctx->Release();                 // the game keeps it alive; we only compare against it
    if (!ok) { strcpy_s(g_gfx.why, sizeof(g_gfx.why), "a context method could not be hooked"); return false; }
    g_gfx.hooked = true;
    Log("gfx: the immediate context %p hooked (vtable %p; OMSetRenderTargets %s)", ctx, vt, g_omSlot == &vt[33] ? "shared with the font" : "elsewhere?");
    return true;
}

// render thread: the depth view that took the most draws this frame, read back as raw depth
static void GfxDepthNow(char *r, size_t cap)
{
    int best = -1;
    for (int i = 0; i < g_gfx.dsvN; i++) if (best < 0 || g_gfx.dsv[i].draws > g_gfx.dsv[best].draws) best = i;
    if (best < 0 || !g_gfx.dsv[best].draws) { _snprintf_s(r, cap, _TRUNCATE, "error no depth buffer took a draw this frame"); return; }
    ID3D11DepthStencilView *v = g_gfx.dsv[best].v;
    ID3D11Resource *res = NULL; v->GetResource(&res);
    ID3D11Texture2D *tex = NULL;
    if (!res || FAILED(res->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&tex)) || !tex) { if (res) res->Release(); _snprintf_s(r, cap, _TRUNCATE, "error the depth view is not a 2D texture"); return; }
    res->Release();
    D3D11_TEXTURE2D_DESC d; tex->GetDesc(&d);
    if (d.SampleDesc.Count > 1) { tex->Release(); _snprintf_s(r, cap, _TRUNCATE, "error the depth buffer is multisampled (x%u): not read yet", d.SampleDesc.Count); return; }
    DXGI_FORMAT f = d.Format, sf = f; int bytes = 4;
    switch (f) {
    case DXGI_FORMAT_D24_UNORM_S8_UINT: case DXGI_FORMAT_R24G8_TYPELESS: case DXGI_FORMAT_R24_UNORM_X8_TYPELESS: sf = DXGI_FORMAT_R24G8_TYPELESS; break;
    case DXGI_FORMAT_D32_FLOAT: case DXGI_FORMAT_R32_TYPELESS: case DXGI_FORMAT_R32_FLOAT: sf = DXGI_FORMAT_R32_TYPELESS; break;
    case DXGI_FORMAT_D32_FLOAT_S8X24_UINT: case DXGI_FORMAT_R32G8X24_TYPELESS: sf = DXGI_FORMAT_R32G8X24_TYPELESS; bytes = 8; break;
    case DXGI_FORMAT_D16_UNORM: case DXGI_FORMAT_R16_TYPELESS: sf = DXGI_FORMAT_R16_TYPELESS; bytes = 2; break;
    default: tex->Release(); _snprintf_s(r, cap, _TRUNCATE, "error depth format %u is not one this build reads", (UINT)f); return;
    }
    D3D11_TEXTURE2D_DESC sd = d; sd.Format = sf; sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    sd.MiscFlags = 0; sd.MipLevels = 1; sd.ArraySize = 1;
    ID3D11Device *dev = NULL; tex->GetDevice(&dev);
    ID3D11Texture2D *st = NULL;
    HRESULT hr = dev ? dev->CreateTexture2D(&sd, NULL, &st) : E_FAIL;
    if (FAILED(hr) || !st) { tex->Release(); if (dev) dev->Release(); _snprintf_s(r, cap, _TRUNCATE, "error a staging copy of format %u failed (0x%08lX)", (UINT)sf, hr); return; }
    if (d.MipLevels == 1 && d.ArraySize == 1) g_gfx.ctx->CopyResource(st, tex);
    else g_gfx.ctx->CopySubresourceRegion(st, 0, 0, 0, 0, tex, 0, NULL);
    D3D11_MAPPED_SUBRESOURCE m;
    hr = g_gfx.ctx->Map(st, 0, D3D11_MAP_READ, 0, &m);
    if (FAILED(hr)) { st->Release(); tex->Release(); dev->Release(); _snprintf_s(r, cap, _TRUNCATE, "error the copy could not be read (0x%08lX)", hr); return; }
    UINT w = d.Width, hgt = d.Height;
    WORD *raw = (WORD *)malloc((size_t)w * hgt * 2);
    double lo = 1e9, hi = -1e9; long far0 = 0, far1 = 0;
    for (UINT y = 0; raw && y < hgt; y++) {
        BYTE *row = (BYTE *)m.pData + (size_t)y * m.RowPitch;
        for (UINT x = 0; x < w; x++) {
            double z;
            if (bytes == 2) z = *(WORD *)(row + x * 2) / 65535.0;
            else if (sf == DXGI_FORMAT_R24G8_TYPELESS) z = (*(DWORD *)(row + x * 4) & 0xFFFFFF) / 16777215.0;
            else z = *(float *)(row + x * bytes);
            if (z < 0) z = 0; if (z > 1) z = 1;
            raw[(size_t)y * w + x] = (WORD)(z * 65535.0 + 0.5);
            if (z <= 0.0) far0++; else if (z >= 1.0) far1++;
            else { if (z < lo) lo = z; if (z > hi) hi = z; }
        }
    }
    g_gfx.ctx->Unmap(st, 0);
    st->Release(); tex->Release(); dev->Release();
    if (!raw) { _snprintf_s(r, cap, _TRUNCATE, "error out of memory"); return; }
    // the raw depth, 16-bit big-endian PGM; and a stretched 8-bit one to look at
    FILE *o = NULL; bool ok = false;
    if (!fopen_s(&o, g_gfx.path, "wb") && o) {
        fprintf(o, "P5\n%u %u\n65535\n", w, hgt);
        ok = true;
        for (size_t i = 0; i < (size_t)w * hgt && ok; i++) { BYTE be[2] = { (BYTE)(raw[i] >> 8), (BYTE)raw[i] }; ok = fwrite(be, 1, 2, o) == 2; }
        fclose(o);
    }
    char vp[MAX_PATH]; _snprintf_s(vp, sizeof(vp), _TRUNCATE, "%s.view.pgm", g_gfx.path);
    if (ok && !fopen_s(&o, vp, "wb") && o) {
        fprintf(o, "P5\n%u %u\n255\n", w, hgt);
        double span = hi > lo ? hi - lo : 1;
        BYTE *line = (BYTE *)malloc(w);
        for (UINT y = 0; line && y < hgt; y++) {
            for (UINT x = 0; x < w; x++) {
                double z = raw[(size_t)y * w + x] / 65535.0;
                double t = z <= 0 ? 0 : z >= 1 ? 1 : (z - lo) / span;
                line[x] = (BYTE)(t * 255.0 + 0.5);
            }
            fwrite(line, 1, w, o);
        }
        free(line); fclose(o);
    }
    free(raw);
    _snprintf_s(r, cap, _TRUNCATE, "%s depth %ux%u format %u (read as %u) from view %d of %d (%ld draws); drawn range %.6f-%.6f; at 0: %ld px, at 1: %ld px; %s",
        ok ? "ok" : "error writing", w, hgt, (UINT)f, (UINT)sf, best + 1, g_gfx.dsvN, g_gfx.dsv[best].draws, lo, hi, far0, far1, g_gfx.path);
}

// ---- dev51: the map's textures out of the game (the Blender import without C2M, step 3) ---------------------------
// "map textures <dir>": every image the loaded map's materials use (the world's surfaces, the static and dynamic props'
// models), each read from its Direct3D texture on the render thread (8 a frame: a staging copy, every mip and slice
// mapped) and written as <dir>\<image name>.dds (the DX10 header; BC1-3 also as the old DXT1/3/5). Read only.
static bool ImgIsD3d(void *p, char *desc, size_t cap);   // below, with the camo swap
struct TexJob { DWORD *imgs; int n, next, written, skipped; double bytes; char dir[400]; char why[200]; volatile LONG want; HANDLE done; };
static TexJob g_tex;

static bool TexFormat(DXGI_FORMAT f, UINT *block, UINT *bytes)   // block: 4 for BC formats (bytes a block), 1 else (bytes a pixel)
{
    switch (f) {
    case DXGI_FORMAT_BC1_TYPELESS: case DXGI_FORMAT_BC1_UNORM: case DXGI_FORMAT_BC1_UNORM_SRGB:
    case DXGI_FORMAT_BC4_TYPELESS: case DXGI_FORMAT_BC4_UNORM: case DXGI_FORMAT_BC4_SNORM: *block = 4; *bytes = 8; return true;
    case DXGI_FORMAT_BC2_TYPELESS: case DXGI_FORMAT_BC2_UNORM: case DXGI_FORMAT_BC2_UNORM_SRGB:
    case DXGI_FORMAT_BC3_TYPELESS: case DXGI_FORMAT_BC3_UNORM: case DXGI_FORMAT_BC3_UNORM_SRGB:
    case DXGI_FORMAT_BC5_TYPELESS: case DXGI_FORMAT_BC5_UNORM: case DXGI_FORMAT_BC5_SNORM:
    case DXGI_FORMAT_BC6H_TYPELESS: case DXGI_FORMAT_BC6H_UF16: case DXGI_FORMAT_BC6H_SF16:
    case DXGI_FORMAT_BC7_TYPELESS: case DXGI_FORMAT_BC7_UNORM: case DXGI_FORMAT_BC7_UNORM_SRGB: *block = 4; *bytes = 16; return true;
    case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: case DXGI_FORMAT_R8G8B8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: case DXGI_FORMAT_B8G8R8X8_UNORM:
    case DXGI_FORMAT_R10G10B10A2_UNORM: case DXGI_FORMAT_R11G11B10_FLOAT: case DXGI_FORMAT_R32_FLOAT:
    case DXGI_FORMAT_R16G16_UNORM: case DXGI_FORMAT_R16G16_FLOAT: *block = 1; *bytes = 4; return true;
    case DXGI_FORMAT_R8G8_UNORM: case DXGI_FORMAT_R16_UNORM: case DXGI_FORMAT_R16_FLOAT: case DXGI_FORMAT_B5G6R5_UNORM: *block = 1; *bytes = 2; return true;
    case DXGI_FORMAT_R8_UNORM: case DXGI_FORMAT_A8_UNORM: *block = 1; *bytes = 1; return true;
    case DXGI_FORMAT_R16G16B16A16_FLOAT: case DXGI_FORMAT_R16G16B16A16_UNORM: *block = 1; *bytes = 8; return true;
    case DXGI_FORMAT_R32G32B32A32_FLOAT: *block = 1; *bytes = 16; return true;
    default: return false;
    }
}

static void TexName(const char *name, char *out, size_t cap)          // a file name: the image's own, Windows-safe
{
    size_t i = 0;
    for (; name[i] && i + 1 < cap; i++) { char c = name[i]; out[i] = strchr("<>:\"/\\|?*", c) || (unsigned char)c < 32 ? '_' : c; }
    out[i] = 0;
}

// render thread: one image to <dir>\<name>.dds; false when it has no readable 2D texture
static bool TexOne(ID3D11DeviceContext *ctx, DWORD img, double *bytes, char *why, size_t cap)
{
    char name[300] = "", file[300], path[800];
    if (!SafeStr((const char *)(size_t)RmxWord((void *)(size_t)(img + 0x48)), name, sizeof(name))) { _snprintf_s(why, cap, _TRUNCATE, "no name"); return false; }
    void *view = (void *)(size_t)RmxWord((void *)(size_t)img);
    char desc[160];
    if (!ImgIsD3d(view, desc, sizeof(desc))) { _snprintf_s(why, cap, _TRUNCATE, "%s: no Direct3D view", name); return false; }
    ID3D11ShaderResourceView *srv = NULL; ID3D11Resource *res = NULL; ID3D11Texture2D *tex = NULL, *st = NULL; ID3D11Device *dev = NULL;
    bool ok = false;
    __try {
        if (FAILED(((IUnknown *)view)->QueryInterface(__uuidof(ID3D11ShaderResourceView), (void **)&srv)) || !srv) { _snprintf_s(why, cap, _TRUNCATE, "%s: not a view", name); return false; }
        srv->GetResource(&res); srv->Release(); srv = NULL;
        if (!res || FAILED(res->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&tex)) || !tex) { if (res) res->Release(); _snprintf_s(why, cap, _TRUNCATE, "%s: not a 2D texture", name); return false; }
        res->Release(); res = NULL;
        D3D11_TEXTURE2D_DESC d; tex->GetDesc(&d);
        UINT block = 1, bpb = 4;
        if (!TexFormat(d.Format, &block, &bpb) || d.SampleDesc.Count > 1) { _snprintf_s(why, cap, _TRUNCATE, "%s: format %u not read", name, (UINT)d.Format); tex->Release(); return false; }
        D3D11_TEXTURE2D_DESC sd = d; sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = d.MiscFlags & D3D11_RESOURCE_MISC_TEXTURECUBE;
        tex->GetDevice(&dev);
        if (!dev || FAILED(dev->CreateTexture2D(&sd, NULL, &st)) || !st) { _snprintf_s(why, cap, _TRUNCATE, "%s: no staging copy", name); tex->Release(); if (dev) dev->Release(); return false; }
        ctx->CopyResource(st, tex);
        TexName(name, file, sizeof(file));
        _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\%s.dds", g_tex.dir, file);
        FILE *f = NULL;
        if (!fopen_s(&f, path, "wb") && f) {
            // DDS: "DDS ", the 124-byte header, the DX10 header, then every slice's mips
            DWORD h[31] = {}; h[0] = 124; h[1] = 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | (block == 4 ? 0x80000 : 0x8);
            h[2] = d.Height; h[3] = d.Width;
            h[4] = block == 4 ? ((d.Width + 3) / 4) * ((d.Height + 3) / 4) * bpb : d.Width * bpb;
            h[6] = d.MipLevels; h[18] = 32; h[19] = 0x4;                                  // pixel format: FourCC
            const char *cc = "DX10";
            if (d.Format == DXGI_FORMAT_BC1_UNORM || d.Format == DXGI_FORMAT_BC1_UNORM_SRGB) cc = "DXT1";
            else if (d.Format == DXGI_FORMAT_BC2_UNORM || d.Format == DXGI_FORMAT_BC2_UNORM_SRGB) cc = "DXT3";
            else if (d.Format == DXGI_FORMAT_BC3_UNORM || d.Format == DXGI_FORMAT_BC3_UNORM_SRGB) cc = "DXT5";
            memcpy(&h[20], cc, 4);
            h[26] = 0x1000 | (d.MipLevels > 1 ? 0x400008 : 0) | ((sd.MiscFlags & D3D11_RESOURCE_MISC_TEXTURECUBE) ? 0x8 : 0);
            if (sd.MiscFlags & D3D11_RESOURCE_MISC_TEXTURECUBE) h[27] = 0xFE00;
            ok = fwrite("DDS ", 1, 4, f) == 4 && fwrite(h, 4, 31, f) == 31;
            if (!strcmp(cc, "DX10")) {
                DWORD dx10[5] = { (DWORD)d.Format, 3, (sd.MiscFlags & D3D11_RESOURCE_MISC_TEXTURECUBE) ? 0x4u : 0u,
                                  (sd.MiscFlags & D3D11_RESOURCE_MISC_TEXTURECUBE) ? d.ArraySize / 6 : d.ArraySize, 0 };
                ok = ok && fwrite(dx10, 4, 5, f) == 5;
            }
            for (UINT a = 0; ok && a < d.ArraySize; a++)
                for (UINT m = 0; ok && m < d.MipLevels; m++) {
                    UINT w = d.Width >> m, hh = d.Height >> m; if (!w) w = 1; if (!hh) hh = 1;
                    UINT rowBytes = block == 4 ? ((w + 3) / 4) * bpb : w * bpb, rows = block == 4 ? (hh + 3) / 4 : hh;
                    D3D11_MAPPED_SUBRESOURCE mp;
                    UINT sub = D3D11CalcSubresource(m, a, d.MipLevels);
                    if (FAILED(ctx->Map(st, sub, D3D11_MAP_READ, 0, &mp))) { ok = false; break; }
                    for (UINT r = 0; ok && r < rows; r++) ok = fwrite((BYTE *)mp.pData + (size_t)r * mp.RowPitch, 1, rowBytes, f) == rowBytes;
                    ctx->Unmap(st, sub);
                    *bytes += (double)rowBytes * rows;
                }
            if (fclose(f) != 0) ok = false;
            if (!ok) DeleteFileA(path);
        }
        if (!ok) _snprintf_s(why, cap, _TRUNCATE, "%s: could not be written", name);
    } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; _snprintf_s(why, cap, _TRUNCATE, "%s: a fault (caught)", name); }
    if (st) st->Release(); if (tex) tex->Release(); if (dev) dev->Release();
    return ok;
}

static void TexPresent(IDXGISwapChain *sc)
{
    if (!g_tex.want) return;
    ID3D11Device *dev = NULL; ID3D11DeviceContext *ctx = NULL;
    if (FAILED(sc->GetDevice(__uuidof(ID3D11Device), (void **)&dev)) || !dev) return;
    dev->GetImmediateContext(&ctx);
    if (ctx)
        for (int k = 0; k < 8 && g_tex.next < g_tex.n; k++, g_tex.next++) {
            double b = 0; char why[200];
            if (TexOne(ctx, g_tex.imgs[g_tex.next], &b, why, sizeof(why))) { g_tex.written++; g_tex.bytes += b; }
            else { g_tex.skipped++; if (!g_tex.why[0]) strcpy_s(g_tex.why, sizeof(g_tex.why), why); }
        }
    if (ctx) ctx->Release();
    dev->Release();
    if (g_tex.next >= g_tex.n) { InterlockedExchange(&g_tex.want, 0); SetEvent(g_tex.done); }
}

// render thread, every Present while something is asked for: a frame is looked at from one Present to the next
static void GfxPresent(IDXGISwapChain *sc)
{
    __try { TexPresent(sc); } __except (EXCEPTION_EXECUTE_HANDLER) { InterlockedExchange(&g_tex.want, 0); if (g_tex.done) SetEvent(g_tex.done); }   // dev51
    DepPresentStart(sc);                             // dev23d: the depth pass needs the binds and the picture's size
    FogPresent(sc);                                  // dev34: the fog into the finished frame ("fog where present"), before the recorder takes it
    if (!g_gfx.hooked && g_gfxInstallWant) GfxInstall(sc);   // dev25: "gfx hide" needs the hooks
    if (g_gfx.hooked) __try { StPlayersNow(); } __except (EXCEPTION_EXECUTE_HANDLER) {}   // dev38: always, so a body from before a pass started is known
    if (g_gfx.hooked) { static int n; if (++n % 120 == 0) { DXGI_SWAP_CHAIN_DESC sd; if (SUCCEEDED(sc->GetDesc(&sd))) { g_st.bbW = sd.BufferDesc.Width; g_st.bbH = sd.BufferDesc.Height; } } }
    if (!g_gfx.phase && !g_gfx.want) return;
    if (g_gfx.phase) {                               // the frame just finished
        g_gfx.phase = 0;
        if (g_gfx.op == 1) {
            if (g_gfx.f) {
                GfxFlushDraws(); fprintf(g_gfx.f, "present (%ld events); binds on other contexts %ld; command lists executed %ld; draws by context:", g_gfx.events, g_gfx.otherBinds, g_gfx.execs);
                for (int i = 0; i < g_gfx.ctxN; i++) fprintf(g_gfx.f, " %p%s %ld", g_gfx.ctxs[i].c, g_gfx.ctxs[i].c == g_gfx.ctx ? " (immediate)" : "", g_gfx.ctxs[i].n);
                fprintf(g_gfx.f, "\n"); StProbeEnd(g_gfx.f); fclose(g_gfx.f); g_gfx.f = NULL;
            }
            _snprintf_s(g_gfx.result, sizeof(g_gfx.result), _TRUNCATE, "ok one frame: %ld events, %d depth views, %d contexts drew, %ld command lists, %ld binds elsewhere; %s",
                g_gfx.events, g_gfx.dsvN, g_gfx.ctxN, g_gfx.execs, g_gfx.otherBinds, g_gfx.path);
        }
        else if (g_gfx.op == 3) {
            if (g_gfx.grabSt) __try { if (GfxGrabRead(g_gfx.result, sizeof(g_gfx.result))) strcpy_s(g_gfx.result, sizeof(g_gfx.result), "ok read"); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(g_gfx.result, sizeof(g_gfx.result), "error a fault reading the grab (caught)"); }
            else _snprintf_s(g_gfx.result, sizeof(g_gfx.result), _TRUNCATE, "error %p was not grabbed: %s (let go %d times this frame, wanted %d)", g_gfx.grab,
                             g_gfx.grabWhy[0] ? g_gfx.grabWhy : g_gfx.grabRes ? "the game let go of it fewer times" : "it was never bound this frame", g_gfx.grabLets, g_gfx.grabK);
            if (g_gfx.grabSt) { g_gfx.grabSt->Release(); g_gfx.grabSt = NULL; }
            if (g_gfx.grabRes) { g_gfx.grabRes->Release(); g_gfx.grabRes = NULL; }
        }
        else __try { GfxDepthNow(g_gfx.result, sizeof(g_gfx.result)); }
        __except (EXCEPTION_EXECUTE_HANDLER) { _snprintf_s(g_gfx.result, sizeof(g_gfx.result), _TRUNCATE, "error a fault reading the depth buffer (caught)"); }
        Log("gfx: %s", g_gfx.result);
        SetEvent(g_gfx.done);
        return;
    }
    LONG op = InterlockedExchange(&g_gfx.want, 0);
    if (!op) return;
    if (!GfxInstall(sc)) { _snprintf_s(g_gfx.result, sizeof(g_gfx.result), _TRUNCATE, "error %s", g_gfx.why); SetEvent(g_gfx.done); return; }
    g_gfx.op = op; g_gfx.events = 0; g_gfx.draws = g_gfx.drawsIdx = 0; g_gfx.seenN = 0; g_gfx.dsvN = 0; g_gfx.bound = -1;
    g_gfx.ctxN = 0; g_gfx.execs = g_gfx.otherBinds = 0; g_gfx.grabLets = 0; g_gfx.grabBound = g_gfx.grabNow = false; g_gfx.grabWhy[0] = 0;
    g_gfx.f = NULL;
    if (op == 1 && (fopen_s(&g_gfx.f, g_gfx.path, "w") || !g_gfx.f)) { g_gfx.f = NULL; _snprintf_s(g_gfx.result, sizeof(g_gfx.result), _TRUNCATE, "error cannot write %s", g_gfx.path); SetEvent(g_gfx.done); return; }
    if (g_gfx.f) { fprintf(g_gfx.f, "# RipperBridge gfx probe: one frame of the game's immediate context %p, tick %d\n", g_gfx.ctx, ReadInt(kRvaTick)); StProbeStart(); PlayersDump(g_gfx.f); memset(g_viewCbN, 0, sizeof(g_viewCbN)); }
    g_gfx.phase = 1;
}

// dev51: "map textures <dir>" - the images of the loaded map's materials (as "map export" finds them), handed to the
// render thread (TexPresent, from the Present hook) and written as DDS; waits for it (5 minutes at most)
static void MapTextures(const char *dir, char *out, size_t cap)
{
    char dv[256], map[64], want[96], got[160];
    if (!DvarLookup("cl_ingame", dv, sizeof(dv)) || strcmp(dv, "cl_ingame bool 1") != 0) { _snprintf_s(out, cap, _TRUNCATE, "error no map is loaded: play a film first"); return; }
    if (!DvarLookup("mapname", dv, sizeof(dv))) { _snprintf_s(out, cap, _TRUNCATE, "error no mapname dvar"); return; }
    const char *q = strchr(dv, '"'), *q2 = q ? strchr(q + 1, '"') : NULL;
    if (!q || !q2 || q2 - q - 1 <= 0 || q2 - q - 1 >= (int)sizeof(map)) { _snprintf_s(out, cap, _TRUNCATE, "error no map is loaded"); return; }
    memcpy(map, q + 1, q2 - q - 1); map[q2 - q - 1] = 0;
    _snprintf_s(want, sizeof(want), _TRUNCATE, "maps/mp/%s.d3dbsp", map);
    BYTE *w = FindAsset(kAssetWorld, want, 0);
    AssetName(w, got, sizeof(got));
    if (!w || _stricmp(got, want)) { _snprintf_s(out, cap, _TRUNCATE, "error %s is not loaded", map); return; }
    if (g_tex.want) { _snprintf_s(out, cap, _TRUNCATE, "error textures are already on their way"); return; }
    DWORD rec[256] = {};
    SafeCopy(rec, w, sizeof(rec));
    DWORD nAll = rec[0x10 / 4], nStatic = rec[0x310 / 4], gSurf = rec[0x368 / 4], gStatic = rec[0x36C / 4];
    if (!nAll || nAll > 400000 || nStatic > 200000) { _snprintf_s(out, cap, _TRUNCATE, "error the world record does not read as measured"); return; }
    // the materials: the world's surfaces', then the props' models' (static and dynamic, models as measured)
    ProbeSet seenM = {}, seenI = {}, seenX = {};
    DWORD *mats = (DWORD *)calloc(nAll + 65536, 4); int nm = 0;
    DWORD *imgs = (DWORD *)calloc(65536, 4); int ni = 0;
    if (!mats || !imgs) { free(mats); free(imgs); _snprintf_s(out, cap, _TRUNCATE, "error out of memory"); return; }
    for (DWORD s = 0; s < nAll; s++) {
        DWORD m = RmxWord((void *)(size_t)(gSurf + s * 80 + 48));
        if (m >= 0x10000 && !ProbeSeen(seenM, m)) mats[nm++] = m;
    }
    auto addModel = [&](DWORD x) {
        if (!RmxModelOk(x) || ProbeSeen(seenX, x)) return;
        BYTE mr[0xF8] = {}; SafeCopy(mr, (void *)(size_t)x, sizeof(mr));
        DWORD ms = *(DWORD *)(mr + 0x24);
        for (BYTE k = 0; k < mr[6] && nm < (int)nAll + 65536; k++) {
            DWORD m = RmxWord((void *)(size_t)(ms + k * 4));
            if (m >= 0x10000 && !ProbeSeen(seenM, m)) mats[nm++] = m;
        }
    };
    for (DWORD i = 0; i < nStatic; i++) addModel(RmxWord((void *)(size_t)(gStatic + i * 152 + 56)));
    BYTE *clip = FindAsset(12, want, 0);
    AssetName(clip, got, sizeof(got));
    if (clip && !_stricmp(got, want)) addModel(MapSkyModel(MapEntityText(clip)));      // dev52: the sky's
    if (clip && !_stricmp(got, want))
        for (int li = 0; li < 3; li++) {
            static const DWORD at[3] = { 0xA0, 0xA8, 0xAC };
            DWORD base = RmxWord(clip + at[li]);
            if (base < 0x10000) continue;
            for (DWORD i = 0, miss = 0; i < 8192 && miss < 64; i++) {
                DWORD x = RmxWord((void *)(size_t)(base + i * 84 + 44));
                if (RmxModelOk(x)) { miss = 0; addModel(x); } else miss++;
            }
        }
    for (int i = 0; i < nm; i++) {
        DWORD table = RmxWord((void *)(size_t)(mats[i] + 0x60)); BYTE cnt = 0;
        SafeCopy(&cnt, (void *)(size_t)(mats[i] + 0x54), 1);
        for (BYTE k = 0; table >= 0x10000 && k < cnt && k < 32 && ni < 65536; k++) {
            DWORD img = RmxWord((void *)(size_t)(table + k * 16 + 12));
            if (img >= 0x10000 && !ProbeSeen(seenI, img)) imgs[ni++] = img;
        }
    }
    free(mats); free(seenM.slot); free(seenX.slot); free(seenI.slot);
    // frames: as "gfx" sees them
    if (!InstallPresentHook()) { free(imgs); _snprintf_s(out, cap, _TRUNCATE, "error no way to see the frames: Present is not hooked"); return; }
    CreateDirectoryA(dir, NULL);
    if (!g_tex.done) g_tex.done = CreateEventA(NULL, FALSE, FALSE, NULL);
    free(g_tex.imgs);
    g_tex.imgs = imgs; g_tex.n = ni; g_tex.next = 0; g_tex.written = g_tex.skipped = 0; g_tex.bytes = 0; g_tex.why[0] = 0;
    strcpy_s(g_tex.dir, sizeof(g_tex.dir), dir);
    ResetEvent(g_tex.done);
    InterlockedExchange(&g_tex.want, 1);
    DWORD t0 = GetTickCount();
    if (WaitForSingleObject(g_tex.done, 300000) != WAIT_OBJECT_0) { InterlockedExchange(&g_tex.want, 0); _snprintf_s(out, cap, _TRUNCATE, "error the textures were not all read within 5 minutes (%d of %d; is the game drawing?)", g_tex.next, g_tex.n); return; }
    _snprintf_s(out, cap, _TRUNCATE, "ok map textures %s: %d images, %d written (%.1f MB), %d skipped%s%s, %.1f s -> %s", map, g_tex.n, g_tex.written,
                g_tex.bytes / 1048576.0, g_tex.skipped, g_tex.why[0] ? ", the first: " : "", g_tex.why, (GetTickCount() - t0) / 1000.0, dir);
    Log("%s", out);
}

// which code each hooked context slot runs now: ours, or someone's who wrote it after us (a module name)
static void GfxSlots(char *s, size_t cap)
{
    static const int slots[] = { 12, 13, 20, 21, 33, 34, 50, 53, 58, 9, 11, 17, 18, 35, 36, 44, 7, 14, 48, 15, 8 };
    void *ours[] = { (void *)&GfxDrawIdx, (void *)&GfxDraw, (void *)&GfxDrawIdxInst, (void *)&GfxDrawInst, (void *)&HookOMSetRenderTargets,
                     (void *)&GfxOMSetUav, (void *)&GfxClearRtv, (void *)&GfxClearDsv, (void *)&GfxExecCl,
                     (void *)&StSetPS, (void *)&StSetVS, (void *)&StSetIL, (void *)&StSetVB, (void *)&StSetBS, (void *)&StSetDS, (void *)&StSetVP,
                     (void *)&SeqSetVSCB, (void *)&SeqMap, (void *)&SeqUpdSub, (void *)&SeqUnmap, (void *)&GfxSetPSSRV };
    *s = 0;
    if (!g_gfx.ctx) return;
    __try {
        void **vt = *(void ***)g_gfx.ctx;
        size_t at = 0;
        for (int i = 0; i < 21 && at + 30 < cap; i++) {
            void *f = vt[slots[i]]; char mod[MAX_PATH] = "?"; HMODULE hm = NULL;
            if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)f, &hm) && hm) {
                GetModuleFileNameA(hm, mod, sizeof(mod)); char *b = strrchr(mod, '\\'); if (b) memmove(mod, b + 1, strlen(b));
            }
            at += _snprintf_s(s + at, cap - at, _TRUNCATE, " %d:%s", slots[i], f == ours[i] ? "ours" : mod);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(s, cap, " unreadable"); }
}

static void GfxCommand(const char *arg, char *out, size_t cap)
{
    if (!g_gfx.done) g_gfx.done = CreateEventA(NULL, FALSE, FALSE, NULL);
    if (!_strnicmp(arg, "hide", 4) || !_stricmp(arg, "state")) {
        if (!InstallPresentHook()) { _snprintf_s(out, cap, _TRUNCATE, "error no way to see the frames: Present is not hooked"); return; }
        if (!g_gfx.hooked) { InterlockedExchange(&g_gfxInstallWant, 1); for (int i = 0; i < 50 && !g_gfx.hooked; i++) Sleep(100); }
        if (!g_gfx.hooked) { _snprintf_s(out, cap, _TRUNCATE, "error the context is not hooked yet (%s)", g_gfx.why); return; }
        if (!_stricmp(arg, "state")) StStatus(out, cap); else StCommand(arg[4] == ' ' ? arg + 5 : arg + 4, out, cap);
        return;
    }
    LONG op = !_strnicmp(arg, "probe ", 6) ? 1 : !_strnicmp(arg, "depth ", 6) ? 2 : !_strnicmp(arg, "grab ", 5) ? 3 : 0;
    if (!op) {
        char sl[600]; GfxSlots(sl, sizeof(sl));
        _snprintf_s(out, cap, _TRUNCATE, !_stricmp(arg, "status") || !*arg ? "gfx hooked %d context %p frames via %s; slots%s%s%s" : "error usage: gfx probe <file> | depth <file.pgm> | grab <resource> [k] <file> | status",
            g_gfx.hooked ? 1 : 0, g_gfx.ctx, g_hooked ? "our Present hook" : "nothing yet", sl,
            g_gfx.why[0] ? "; " : "", g_gfx.why);
        return;
    }
    if (g_gfx.want || g_gfx.phase || g_gfx.grabBuf) { _snprintf_s(out, cap, _TRUNCATE, "error a gfx look is already on its way"); return; }
    // frames: our own Present hook
    if (!InstallPresentHook()) { _snprintf_s(out, cap, _TRUNCATE, "error no way to see the frames: Present is not hooked"); return; }
    if (op == 3) {
        // grab <resource, hex, as the probe prints it> [k: which let-go in the frame, 1 = the first] <file>
        unsigned res = 0; int k = 1, used = 0, used2 = 0;
        if (sscanf_s(arg + 5, "%x %n", &res, &used) != 1 || !res) { _snprintf_s(out, cap, _TRUNCATE, "error usage: gfx grab <resource> [k] <file>"); return; }
        const char *rest = arg + 5 + used;
        if (sscanf_s(rest, "%d %n", &k, &used2) == 1 && used2 > 0 && rest[used2]) rest += used2; else k = 1;
        g_gfx.grab = (void *)(size_t)res; g_gfx.grabK = k < 1 ? 1 : k;
        strcpy_s(g_gfx.path, sizeof(g_gfx.path), rest);
    }
    else strcpy_s(g_gfx.path, sizeof(g_gfx.path), arg + 6);
    ResetEvent(g_gfx.done);
    InterlockedExchange(&g_gfx.want, op);
    if (WaitForSingleObject(g_gfx.done, 15000) != WAIT_OBJECT_0) { InterlockedExchange(&g_gfx.want, 0); _snprintf_s(out, cap, _TRUNCATE, "error no frame within 15 s (is the game drawing?)"); return; }
    if (op == 3 && g_gfx.grabBuf) {
        __try { GfxGrabWrite(out, cap); } __except (EXCEPTION_EXECUTE_HANDLER) { _snprintf_s(out, cap, _TRUNCATE, "error a fault writing the grab (caught)"); }
        BYTE *b = g_gfx.grabBuf; g_gfx.grabBuf = NULL; free(b);
        return;
    }
    strcpy_s(out, cap, g_gfx.result);
}

// ---- passes beside the recorder's picture (dev23d depth, dev24 the clean plate; 2026-10-17) ---------------------------
// Each is one of the game's own render targets, found by its shape the first time a bind holds it, copied the moment
// the game lets go of it in a frame (once a frame), and written with that frame's picture, to an ffmpeg of its own.
// A frame with no copy is written black and counted ("missing"), so a pass keeps the picture's frame numbers.
//
// The depth pass (dev23d). Sandbox 0504-0505: the game builds an R32_FLOAT pyramid of the scene's view depth every frame
// (as wide as the picture, 11 mips); its top level holds the distance from the camera in game units (4.06 at the gun,
// 9577 across the garage, 51861 from a free camera up high; the sky not finite), already resolved from the 4x
// multisampled scene. Linear over a distance (the "depth=" value),
// 16-bit grey, near white, at and past the distance black, the sky black; a PNG sequence beside the video,
// "<video name> - depth\depth_000000.png". "rec start ...|depth=2000" (the distance in game units).
//
// The clean plate (dev24, Mycelium: "Start the clean plate pass"). The scene resolved into an R8G8B8A8_UNORM_SRGB target as
// big as the picture, before bloom, the colour grade and the HUD (0504-0505: the HUD-free garage, darker than the final
// picture). A video beside the picture's, with the picture's own codec: "<video name> - plate.<ext>". "|plate=1".
struct PassStream {
    const char *name;                    // "depth", "plate"
    bool on; float dist;                 // dist: the depth's distance
    char out[MAX_PATH], ffmpeg[MAX_PATH], rate[32], codec[1024]; DWORD prio;
    ID3D11Resource *src;
    bool bound, letGo, copied;           // this frame
    ID3D11Texture2D *st;
    bool started; HANDLE proc, stdinW, writer, bufFree, bufFull;
    CapBuf bufs[kCapBufs]; int nextFill, nextSend;
    volatile LONG frames, written, missing; char err[200];
};
static PassStream g_pass[2] = { { "depth" }, { "plate" } };
static UINT g_passW, g_passH;            // the picture's size
static PassStream &g_dep = g_pass[0];

static bool PassAny() { return g_pass[0].on || g_pass[1].on; }

static bool PassShape(int k, const D3D11_TEXTURE2D_DESC &d)
{
    if (d.Width != g_passW || d.Height != g_passH || d.SampleDesc.Count != 1) return false;
    if (k == 0) return (d.Format == DXGI_FORMAT_R32_FLOAT || d.Format == DXGI_FORMAT_R32_TYPELESS) && d.MipLevels > 1;
    return d.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB && d.MipLevels == 1 && (d.BindFlags & D3D11_BIND_SHADER_RESOURCE);
}

static void PassReset(PassStream &p)
{
    if (p.src) { p.src->Release(); p.src = NULL; }
    if (p.st) { p.st->Release(); p.st = NULL; }
    p.bound = p.letGo = p.copied = false; p.started = false;
    p.frames = p.written = p.missing = 0; p.err[0] = 0;
    strcpy_s(p.ffmpeg, sizeof(p.ffmpeg), g_cap.ffmpeg); strcpy_s(p.rate, sizeof(p.rate), g_cap.rate); p.prio = g_cap.prio;
}

// the video's name without its extension, and its extension
static void PassStem(char *stem, size_t cap, const char **ext)
{
    strcpy_s(stem, cap, g_cap.out);
    char *dot = strrchr(stem, '.'), *sl = strrchr(stem, '\\');
    *ext = "";
    if (dot && (!sl || dot > sl)) { *ext = g_cap.out + (dot - stem); *dot = 0; }
}

static bool DepArm(const char *spec, char *why, size_t cap)
{
    g_dep.on = false; g_passW = g_passH = 0;
    if (!spec || !*spec) return true;
    float d = (float)atof(spec);
    if (d < 1 || d > 1000000) { _snprintf_s(why, cap, _TRUNCATE, "the distance must be 1..1000000 game units (got %s)", spec); return false; }
    char stem[MAX_PATH]; const char *ext; PassStem(stem, sizeof(stem), &ext);
    _snprintf_s(g_dep.out, sizeof(g_dep.out), _TRUNCATE, "%s - depth", stem);
    if (!CreateDirectoryA(g_dep.out, NULL) && GetLastError() != ERROR_ALREADY_EXISTS) { _snprintf_s(why, cap, _TRUNCATE, "cannot make %s (%lu)", g_dep.out, GetLastError()); return false; }
    PassReset(g_dep);
    g_dep.dist = d; g_dep.on = true;
    Log("depth: on, %.0f units, into %s", d, g_dep.out);
    return true;
}

static bool PlateArm(bool on, char *why, size_t cap)
{
    PassStream &p = g_pass[1];
    p.on = false;
    if (!on) return true;
    char stem[MAX_PATH]; const char *ext; PassStem(stem, sizeof(stem), &ext);
    _snprintf_s(p.out, sizeof(p.out), _TRUNCATE, "%s - plate%s", stem, *ext ? ext : ".avi");
    PassReset(p);
    strcpy_s(p.codec, sizeof(p.codec), g_cap.codec);
    p.on = true;
    Log("plate: on, into %s", p.out);
    (void)why; (void)cap;
    return true;
}

// render thread, before a bind: is a pass's target in it? (each found the first time a bind holds one of its shape)
static void DepOnBind(UINT n, ID3D11RenderTargetView *const *rtv, ID3D11DepthStencilView *dsv)
{
    if (!PassAny() || !g_rec.active || !g_passW) return;
    __try {
        bool in[2] = { false, false };
        for (UINT i = 0; i < n && i < 8 && rtv; i++) {
            if (!rtv[i]) continue;
            ID3D11Resource *r = NULL; rtv[i]->GetResource(&r);
            if (!r) continue;
            for (int k = 0; k < 2; k++) {
                PassStream &p = g_pass[k];
                if (!p.on) continue;
                if (!p.src) {
                    ID3D11Texture2D *t = NULL;
                    if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&t)) && t) {
                        D3D11_TEXTURE2D_DESC d; t->GetDesc(&d); t->Release();
                        if (PassShape(k, d)) { p.src = r; r->AddRef(); Log("%s: the game's target is %p (%ux%u, format %u, %u mips)", p.name, r, d.Width, d.Height, (UINT)d.Format, d.MipLevels); }
                    }
                }
                if (r == p.src) in[k] = true;
            }
            r->Release();
        }
        for (int k = 0; k < 2; k++) {
            if (g_pass[k].bound && !in[k]) g_pass[k].letGo = true;
            g_pass[k].bound = in[k];
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// render thread, after that bind: a target just let go is copied (once a frame each)
static void DepAfterBind()
{
    for (int k = 0; k < 2; k++) {
        PassStream &p = g_pass[k];
        if (!p.letGo) continue;
        p.letGo = false;
        if (p.copied || !p.src || !p.on) continue;
        __try {
            if (!p.st) {
                ID3D11Device *dev = NULL; p.src->GetDevice(&dev);
                D3D11_TEXTURE2D_DESC sd = {}; sd.Width = g_passW; sd.Height = g_passH; sd.MipLevels = 1; sd.ArraySize = 1;
                sd.Format = k == 0 ? DXGI_FORMAT_R32_TYPELESS : DXGI_FORMAT_R8G8B8A8_TYPELESS;
                sd.SampleDesc.Count = 1; sd.Usage = D3D11_USAGE_STAGING; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                HRESULT hr = dev ? dev->CreateTexture2D(&sd, NULL, &p.st) : E_FAIL;
                if (dev) dev->Release();
                if (FAILED(hr)) { _snprintf_s(p.err, sizeof(p.err), _TRUNCATE, "no staging texture (0x%08lX)", hr); p.st = NULL; continue; }
            }
            g_gfx.ctx->CopySubresourceRegion(p.st, 0, 0, 0, 0, p.src, 0, NULL);
            p.copied = true;
        } __except (EXCEPTION_EXECUTE_HANDLER) { _snprintf_s(p.err, sizeof(p.err), _TRUNCATE, "a fault copying the %s (caught)", p.name); }
    }
}

// render thread, the start of every Present: the context's binds hooked, the picture's size known (before frame 0)
static void DepPresentStart(IDXGISwapChain *sc)
{
    if (!PassAny() || !g_rec.active) return;
    if (!g_gfx.hooked) GfxInstall(sc);
    if (!g_passW) { DXGI_SWAP_CHAIN_DESC d; if (SUCCEEDED(sc->GetDesc(&d))) { g_passW = d.BufferDesc.Width; g_passH = d.BufferDesc.Height; } }
}

// render thread, the end of every Present: the next frame takes its own copies
static void DepPresentEnd() { for (int k = 0; k < 2; k++) { g_pass[k].copied = false; g_pass[k].bound = false; g_pass[k].letGo = false; } FogPresentEnd(); }

static DWORD WINAPI PassWriter(LPVOID arg)
{
    PassStream &p = *(PassStream *)arg;
    for (;;) {
        WaitForSingleObject(p.bufFull, INFINITE);
        for (;;) {
            CapBuf &b = p.bufs[p.nextSend];
            if (!b.full) break;
            if (!b.data) { b.full = 0; return 0; }           // the stop marker
            DWORD done = 0, n = 0;
            while (done < b.size) {
                if (!WriteFile(p.stdinW, b.data + done, b.size - done, &n, NULL) || !n) {
                    _snprintf_s(p.err, sizeof(p.err), _TRUNCATE, "ffmpeg (%s) stopped reading (error %lu) after %ld frames", p.name, GetLastError(), p.written);
                    b.full = 0; SetEvent(p.bufFree); return 1;
                }
                done += n;
            }
            InterlockedIncrement(&p.written);
            InterlockedExchange(&b.full, 0);
            p.nextSend = (p.nextSend + 1) % kCapBufs;
            SetEvent(p.bufFree);
        }
    }
}

static bool PassLaunch(PassStream &p, int k)
{
    char cmd[2048], logPath[MAX_PATH];
    if (k == 0) {
        _snprintf_s(cmd, sizeof(cmd), _TRUNCATE,
            "\"%s\" -hide_banner -nostats -y -f rawvideo -pix_fmt gray16le -s %ux%u -framerate %s -i - -c:v png -pix_fmt gray16be -start_number 0 \"%s\\depth_%%06d.png\"",
            p.ffmpeg, g_passW, g_passH, p.rate, p.out);
        _snprintf_s(logPath, sizeof(logPath), _TRUNCATE, "%s\\ffmpeg.log", p.out);
    }
    else {
        _snprintf_s(cmd, sizeof(cmd), _TRUNCATE, "\"%s\" -hide_banner -nostats -y -f rawvideo -pix_fmt rgba -s %ux%u -framerate %s -i - %s \"%s\"",
            p.ffmpeg, g_passW, g_passH, p.rate, p.codec, p.out);
        _snprintf_s(logPath, sizeof(logPath), _TRUNCATE, "%s.ffmpeg.log", p.out);
    }
    SECURITY_ATTRIBUTES sa = { sizeof(sa), NULL, TRUE };
    HANDLE rd = NULL, wr = NULL;
    if (!CreatePipe(&rd, &wr, &sa, 1 << 20)) { _snprintf_s(p.err, sizeof(p.err), _TRUNCATE, "CreatePipe %lu", GetLastError()); return false; }
    SetHandleInformation(wr, HANDLE_FLAG_INHERIT, 0);
    HANDLE log = CreateFileA(logPath, GENERIC_WRITE, FILE_SHARE_READ, &sa, CREATE_ALWAYS, 0, NULL);
    HANDLE nul = CreateFileA("NUL", GENERIC_WRITE, FILE_SHARE_WRITE, &sa, OPEN_EXISTING, 0, NULL);
    HANDLE list[3] = { rd, log, nul };
    SIZE_T attrSize = 0; InitializeProcThreadAttributeList(NULL, 1, 0, &attrSize);
    LPPROC_THREAD_ATTRIBUTE_LIST attrs = (LPPROC_THREAD_ATTRIBUTE_LIST)HeapAlloc(GetProcessHeap(), 0, attrSize);
    InitializeProcThreadAttributeList(attrs, 1, 0, &attrSize);
    UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, list, sizeof(list), NULL, NULL);
    STARTUPINFOEXA si = {}; si.StartupInfo.cb = sizeof(si);
    si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    si.StartupInfo.hStdInput = rd; si.StartupInfo.hStdOutput = nul; si.StartupInfo.hStdError = log;
    si.lpAttributeList = attrs;
    PROCESS_INFORMATION pi = {};
    BOOL ok = CreateProcessA(NULL, cmd, NULL, NULL, TRUE, EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | p.prio, NULL, NULL, &si.StartupInfo, &pi);
    DWORD e = GetLastError();
    DeleteProcThreadAttributeList(attrs); HeapFree(GetProcessHeap(), 0, attrs);
    CloseHandle(rd); if (log != INVALID_HANDLE_VALUE) CloseHandle(log); if (nul != INVALID_HANDLE_VALUE) CloseHandle(nul);
    if (!ok) { CloseHandle(wr); _snprintf_s(p.err, sizeof(p.err), _TRUNCATE, "could not start ffmpeg for the %s (%lu)", p.name, e); return false; }
    CloseHandle(pi.hThread);
    p.proc = pi.hProcess; p.stdinW = wr;
    Log("%s: ffmpeg pid %lu: %s", p.name, pi.dwProcessId, cmd);
    DWORD size = g_passW * g_passH * (k == 0 ? 2 : 4);
    for (int i = 0; i < kCapBufs; i++) {
        p.bufs[i].data = (BYTE *)VirtualAlloc(NULL, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        p.bufs[i].size = size; p.bufs[i].full = 0;
        if (!p.bufs[i].data) { _snprintf_s(p.err, sizeof(p.err), _TRUNCATE, "out of memory for %s buffers", p.name); return false; }
    }
    p.nextFill = p.nextSend = 0;
    p.bufFree = CreateEventA(NULL, FALSE, FALSE, NULL);
    p.bufFull = CreateEventA(NULL, FALSE, FALSE, NULL);
    p.writer = CreateThread(NULL, 0, PassWriter, &p, 0, NULL);
    p.started = true;
    return true;
}

// render thread, with each recorded picture (RecFrameAt): each pass's copy of this frame, to its ffmpeg
static void DepFrame(IDXGISwapChain *sc)
{
    if (!PassAny()) return;
    if (!g_passW) { DXGI_SWAP_CHAIN_DESC d; if (FAILED(sc->GetDesc(&d))) return; g_passW = d.BufferDesc.Width; g_passH = d.BufferDesc.Height; }
    for (int k = 0; k < 2; k++) {
        PassStream &p = g_pass[k];
        if (!p.on || p.err[0]) continue;
        __try {
            if (!p.started && !PassLaunch(p, k)) continue;
            CapBuf &b = p.bufs[p.nextFill];
            while (b.full && !p.err[0]) WaitForSingleObject(p.bufFree, 200);
            if (p.err[0]) continue;
            D3D11_MAPPED_SUBRESOURCE m;
            if (p.copied && p.st && SUCCEEDED(g_gfx.ctx->Map(p.st, 0, D3D11_MAP_READ, 0, &m))) {
                if (k == 0) {
                    WORD *o = (WORD *)b.data; float inv = 1.0f / p.dist;
                    for (UINT y = 0; y < g_passH; y++) {
                        const float *row = (const float *)((const BYTE *)m.pData + (size_t)y * m.RowPitch);
                        WORD *dst = o + (size_t)y * g_passW;
                        for (UINT x = 0; x < g_passW; x++) {
                            float z = row[x];
                            float t = _finite(z) && z > 0 ? 1.0f - z * inv : 0.0f;       // near white, the distance and past it black
                            dst[x] = t <= 0 ? 0 : t >= 1 ? 65535 : (WORD)(t * 65535.0f + 0.5f);
                        }
                    }
                }
                else {
                    DWORD row = g_passW * 4;
                    for (UINT y = 0; y < g_passH; y++) memcpy(b.data + (size_t)y * row, (const BYTE *)m.pData + (size_t)y * m.RowPitch, row);
                }
                g_gfx.ctx->Unmap(p.st, 0);
            }
            else { memset(b.data, 0, b.size); InterlockedIncrement(&p.missing); }
            InterlockedIncrement(&p.frames);
            InterlockedExchange(&b.full, 1);
            p.nextFill = (p.nextFill + 1) % kCapBufs;
            SetEvent(p.bufFull);
        } __except (EXCEPTION_EXECUTE_HANDLER) { _snprintf_s(p.err, sizeof(p.err), _TRUNCATE, "a fault writing a %s frame (caught)", p.name); }
    }
}

// pipe thread (RecStop, after the picture's CapStop): each pass's ffmpeg finished; a line each added to the reply
static void DepStop(char *out, size_t cap)
{
    for (int k = 0; k < 2; k++) {
        PassStream &p = g_pass[k];
        if (!p.on) continue;
        p.on = false;
        DWORD code = STILL_ACTIVE;
        if (p.started) {
            for (int i = 0; i < 600; i++) {
                bool busy = false; for (int j = 0; j < kCapBufs; j++) busy |= p.bufs[j].full != 0;
                if (!busy || WaitForSingleObject(p.writer, 0) == WAIT_OBJECT_0) break;
                Sleep(100);
            }
            CapBuf &b = p.bufs[p.nextFill]; BYTE *keep = b.data; b.data = NULL; InterlockedExchange(&b.full, 1); SetEvent(p.bufFull);
            WaitForSingleObject(p.writer, 10000); b.data = keep;
            CloseHandle(p.stdinW); p.stdinW = NULL;
            if (WaitForSingleObject(p.proc, 300000) == WAIT_OBJECT_0) GetExitCodeProcess(p.proc, &code);
            CloseHandle(p.proc); CloseHandle(p.writer); CloseHandle(p.bufFree); CloseHandle(p.bufFull);
            for (int j = 0; j < kCapBufs; j++) if (p.bufs[j].data) { VirtualFree(p.bufs[j].data, 0, MEM_RELEASE); p.bufs[j].data = NULL; }
            p.proc = p.writer = p.bufFree = p.bufFull = NULL;
            p.started = false;
        }
        if (p.st) { p.st->Release(); p.st = NULL; }
        if (p.src) { p.src->Release(); p.src = NULL; }
        size_t L = strlen(out);
        if (k == 0) _snprintf_s(out + L, cap - L, _TRUNCATE, "; depth (%.0f units): %ld frames, %ld written, %ld missing (black), ffmpeg exit %ld, %s%s%s",
            p.dist, p.frames, p.written, p.missing, (long)code, p.out, p.err[0] ? " - ERROR: " : "", p.err);
        else _snprintf_s(out + L, cap - L, _TRUNCATE, "; plate: %ld frames, %ld written, %ld missing (black), ffmpeg exit %ld, %s%s%s",
            p.frames, p.written, p.missing, (long)code, p.out, p.err[0] ? " - ERROR: " : "", p.err);
        Log("%s: %s", p.name, out + L);
    }
}

static void DepStatus(char *out, size_t cap)
{
    size_t L = strlen(out);
    _snprintf_s(out + L, cap - L, _TRUNCATE, " depth %.0f frames %ld written %ld missing %ld source %p err %s plate %d frames %ld written %ld missing %ld source %p err %s",
        g_pass[0].on ? g_pass[0].dist : 0.0f, g_pass[0].frames, g_pass[0].written, g_pass[0].missing, g_pass[0].src, g_pass[0].err[0] ? g_pass[0].err : "-",
        g_pass[1].on ? 1 : 0, g_pass[1].frames, g_pass[1].written, g_pass[1].missing, g_pass[1].src, g_pass[1].err[0] ? g_pass[1].err : "-");
}

// ---- live camo swap, step 1: finding a loaded image (dev27, 2026-10-17) ------------------------------------------------
// Mycelium (10-17): "replacing camos without closing the game". His camo builds are .iwi files named after the game's own
// images ("~-gcamo_dragon_col", "~-gmtl_t6_wpn_sniper_dsr50_col"); the game holds each loaded image as a record that
// points to its name and to its Direct3D texture. "img find <name>": the name's string in memory, every pointer to it
// (the records), and in the 0x60 bytes before each pointer, which words are Direct3D objects (a texture or a view, with
// its size and format). Read only; it measures the record's layout before anything is swapped.
static bool ImgIsD3d(void *p, char *desc, size_t cap)
{
    *desc = 0;
    if ((size_t)p < 0x10000 || ((size_t)p & 3)) return false;
    static BYTE *d3dLo, *d3dHi;
    if (!d3dLo) {
        HMODULE m = GetModuleHandleA("d3d11.dll");
        MEMORY_BASIC_INFORMATION mi;
        if (!m || !VirtualQuery(m, &mi, sizeof(mi))) return false;
        d3dLo = (BYTE *)m; IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(d3dLo + ((IMAGE_DOS_HEADER *)d3dLo)->e_lfanew);
        d3dHi = d3dLo + nt->OptionalHeader.SizeOfImage;
    }
    __try {
        BYTE *vt = *(BYTE **)p;
        if (vt < d3dLo || vt >= d3dHi) return false;          // a COM object whose vtable is in d3d11.dll
        IUnknown *u = (IUnknown *)p;
        ID3D11ShaderResourceView *srv = NULL; ID3D11Texture2D *t = NULL;
        if (SUCCEEDED(u->QueryInterface(__uuidof(ID3D11ShaderResourceView), (void **)&srv)) && srv) {
            ID3D11Resource *r = NULL; srv->GetResource(&r); srv->Release();
            if (r && SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&t)) && t) {
                D3D11_TEXTURE2D_DESC d; t->GetDesc(&d); t->Release();
                _snprintf_s(desc, cap, _TRUNCATE, "view of texture %p %ux%u fmt %u mips %u", r, d.Width, d.Height, (UINT)d.Format, d.MipLevels);
            }
            else _snprintf_s(desc, cap, _TRUNCATE, "view (not a 2D texture)");
            if (r) r->Release();
            return true;
        }
        if (SUCCEEDED(u->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&t)) && t) {
            D3D11_TEXTURE2D_DESC d; t->GetDesc(&d); t->Release();
            _snprintf_s(desc, cap, _TRUNCATE, "texture %ux%u fmt %u mips %u", d.Width, d.Height, (UINT)d.Format, d.MipLevels);
            return true;
        }
        _snprintf_s(desc, cap, _TRUNCATE, "a d3d11 object");
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// every committed read-write region (the game's heap and its zones), each visited by a callback
template <class F> static void ImgEachRegion(F f)
{
    SYSTEM_INFO si; GetSystemInfo(&si);
    for (BYTE *at = (BYTE *)si.lpMinimumApplicationAddress; at < (BYTE *)si.lpMaximumApplicationAddress; ) {
        MEMORY_BASIC_INFORMATION mi;
        if (!VirtualQuery(at, &mi, sizeof(mi))) break;
        BYTE *end = (BYTE *)mi.BaseAddress + mi.RegionSize;
        if (mi.State == MEM_COMMIT && !(mi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) && (mi.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE | PAGE_READONLY | PAGE_WRITECOPY)))
            f((BYTE *)mi.BaseAddress, end);
        at = end;
    }
}

static void ImgFind(const char *name, char *out, size_t cap)
{
    size_t n = strlen(name);
    if (n < 3 || n > 120) { _snprintf_s(out, cap, _TRUNCATE, "error usage: img find <image name>"); return; }
    BYTE *strs[16]; int strN = 0;
    ImgEachRegion([&](BYTE *lo, BYTE *hi) {
        __try {
            for (BYTE *s = lo; s + n < hi && strN < 16; s++) {
                s = (BYTE *)memchr(s, name[0], (size_t)(hi - n - s));
                if (!s) break;
                if (!memcmp(s, name, n) && s[n] == 0 && (s == lo || s[-1] == 0 || s[-1] == '\n')) strs[strN++] = s;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    });
    size_t at = _snprintf_s(out, cap, _TRUNCATE, "img \"%s\": %d copies of the name", name, strN);
    int recs = 0;
    for (int i = 0; i < strN && at + 200 < cap; i++) {
        DWORD want = (DWORD)(size_t)strs[i];
        ImgEachRegion([&](BYTE *lo, BYTE *hi) {
            __try {
                for (BYTE *q = lo; q + 4 <= hi && recs < 6 && at + 300 < cap; q += 4) {
                    if (*(DWORD *)q != want) continue;
                    recs++;
                    at += _snprintf_s(out + at, cap - at, _TRUNCATE, " | name %p pointed at from %p:", strs[i], q);
                    for (int k = -24; k < 4 && at + 120 < cap; k++) {
                        void *v = *(void **)(q + 4 * k); char d[160];
                        if (ImgIsD3d(v, d, sizeof(d))) at += _snprintf_s(out + at, cap - at, _TRUNCATE, " [%+d] %p %s;", 4 * k, v, d);
                    }
                    at += _snprintf_s(out + at, cap - at, _TRUNCATE, " words before:");
                    for (int k = -12; k < 0 && at + 12 < cap; k++) at += _snprintf_s(out + at, cap - at, _TRUNCATE, " %08X", *(DWORD *)(q + 4 * k));
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        });
    }
}

// ---- live camo swap, step 2 (dev28, 2026-10-17) -----------------------------------------------------------------------
// Sandbox 0511: a loaded image's record is 0x50 bytes; its first word is the image's Direct3D view (a shader resource
// view of its 2D texture: the DSR-50's diffuse, 1024x1024 BC3, 11 mips), and +0x48 points to its name. So an image is
// swapped by giving that first word another view: "img swap <name>|<file.dds>" builds a texture from the DDS (made from
// a camo build's .iwi by OpenAssetTools' ImageConverter) on the game's own device and puts its view there in one
// 4-byte write; the game's own view is kept, and "img restore <name>|all" puts it back. Nothing else in the record
// changes, so the next frame draws with the new texture. The DDS's own format is used (Mycelium's builds are 32-bit RGBA
// with all mips; the game's are compressed), so it need not match the game's.
#ifndef MAKEFOURCC
#define MAKEFOURCC(a, b, c, d) ((DWORD)(BYTE)(a) | ((DWORD)(BYTE)(b) << 8) | ((DWORD)(BYTE)(c) << 16) | ((DWORD)(BYTE)(d) << 24))
#endif
struct ImgSwap { BYTE *rec; ID3D11ShaderResourceView *mine; void *theirs; char name[128]; };
static ImgSwap g_imgSwaps[64]; static int g_imgSwapN;

// the record whose first word is a 2D texture's view and whose name (at +0x48) is this one
static BYTE *ImgRecord(const char *name)
{
    size_t n = strlen(name);
    BYTE *strs[16]; int strN = 0;
    ImgEachRegion([&](BYTE *lo, BYTE *hi) {
        __try {
            for (BYTE *s = lo; s + n < hi && strN < 16; s++) {
                s = (BYTE *)memchr(s, name[0], (size_t)(hi - n - s));
                if (!s) break;
                if (!memcmp(s, name, n) && s[n] == 0 && (s == lo || s[-1] == 0)) strs[strN++] = s;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    });
    BYTE *found = NULL;
    for (int i = 0; i < strN && !found; i++) {
        DWORD want = (DWORD)(size_t)strs[i];
        ImgEachRegion([&](BYTE *lo, BYTE *hi) {
            if (found) return;
            __try {
                for (BYTE *q = lo + 0x48; q + 4 <= hi; q += 4) {
                    if (*(DWORD *)q != want) continue;
                    char d[160];
                    if (ImgIsD3d(*(void **)(q - 0x48), d, sizeof(d)) && !strncmp(d, "view of texture", 15)) { found = q - 0x48; return; }
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        });
    }
    return found;
}

static int ImgBlockBytes(DXGI_FORMAT f)
{
    switch (f) {
    case DXGI_FORMAT_BC1_UNORM: case DXGI_FORMAT_BC1_UNORM_SRGB: case DXGI_FORMAT_BC4_UNORM: case DXGI_FORMAT_BC4_SNORM: return 8;
    case DXGI_FORMAT_BC2_UNORM: case DXGI_FORMAT_BC2_UNORM_SRGB: case DXGI_FORMAT_BC3_UNORM: case DXGI_FORMAT_BC3_UNORM_SRGB:
    case DXGI_FORMAT_BC5_UNORM: case DXGI_FORMAT_BC5_SNORM: case DXGI_FORMAT_BC7_UNORM: case DXGI_FORMAT_BC7_UNORM_SRGB: return 16;
    default: return 0;
    }
}

// a DDS file's 2D texture: format, size, mips, and each mip's bytes (pointers into the file's own buffer)
static bool ImgLoadDds(const BYTE *b, size_t size, D3D11_TEXTURE2D_DESC *d, D3D11_SUBRESOURCE_DATA *sub, int subCap, char *why, size_t cap)
{
    if (size < 128 || memcmp(b, "DDS ", 4)) { strcpy_s(why, cap, "not a DDS file"); return false; }
    DWORD h = *(DWORD *)(b + 12), w = *(DWORD *)(b + 16), mips = *(DWORD *)(b + 28);
    DWORD pff = *(DWORD *)(b + 80), four = *(DWORD *)(b + 84), bits = *(DWORD *)(b + 88), rm = *(DWORD *)(b + 92), bm = *(DWORD *)(b + 100);
    size_t at = 128;
    DXGI_FORMAT f = DXGI_FORMAT_UNKNOWN;
    if (pff & 4) {                                             // a FourCC
        if (four == MAKEFOURCC('D', 'X', '1', '0')) { if (size < 148) { strcpy_s(why, cap, "a short DX10 header"); return false; } f = (DXGI_FORMAT)*(DWORD *)(b + 128); at = 148; }
        else if (four == MAKEFOURCC('D', 'X', 'T', '1')) f = DXGI_FORMAT_BC1_UNORM;
        else if (four == MAKEFOURCC('D', 'X', 'T', '3')) f = DXGI_FORMAT_BC2_UNORM;
        else if (four == MAKEFOURCC('D', 'X', 'T', '5')) f = DXGI_FORMAT_BC3_UNORM;
        else if (four == MAKEFOURCC('A', 'T', 'I', '1') || four == MAKEFOURCC('B', 'C', '4', 'U')) f = DXGI_FORMAT_BC4_UNORM;
        else if (four == MAKEFOURCC('A', 'T', 'I', '2') || four == MAKEFOURCC('B', 'C', '5', 'U')) f = DXGI_FORMAT_BC5_UNORM;
    }
    else if (bits == 32) f = rm == 0x000000FF ? DXGI_FORMAT_R8G8B8A8_UNORM : bm == 0x000000FF ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_UNKNOWN;
    if (f == DXGI_FORMAT_UNKNOWN) { _snprintf_s(why, cap, _TRUNCATE, "a DDS format this build does not read (fourcc %08lX, %lu bits)", four, bits); return false; }
    if (!mips) mips = 1;
    if ((int)mips > subCap) mips = subCap;
    int block = ImgBlockBytes(f), bpp = block ? 0 : 4;
    UINT mw = w, mh = h;
    for (UINT m = 0; m < mips; m++) {
        UINT pitch = block ? max(1u, (mw + 3) / 4) * block : mw * bpp, rows = block ? max(1u, (mh + 3) / 4) : mh;
        if (at + (size_t)pitch * rows > size) { _snprintf_s(why, cap, _TRUNCATE, "the DDS ends inside mip %u", m); return false; }
        sub[m].pSysMem = b + at; sub[m].SysMemPitch = pitch; sub[m].SysMemSlicePitch = pitch * rows;
        at += (size_t)pitch * rows;
        mw = max(1u, mw / 2); mh = max(1u, mh / 2);
    }
    memset(d, 0, sizeof(*d));
    d->Width = w; d->Height = h; d->MipLevels = mips; d->ArraySize = 1; d->Format = f; d->SampleDesc.Count = 1;
    d->Usage = D3D11_USAGE_IMMUTABLE; d->BindFlags = D3D11_BIND_SHADER_RESOURCE;
    return true;
}

static void ImgSwapCmd(const char *name, const char *file, char *out, size_t cap)
{
    BYTE *rec = ImgRecord(name);
    if (!rec) { _snprintf_s(out, cap, _TRUNCATE, "error no loaded image named \"%s\" (is its weapon or map in the film?)", name); return; }
    FILE *f = NULL;
    if (fopen_s(&f, file, "rb") || !f) { _snprintf_s(out, cap, _TRUNCATE, "error cannot read %s", file); return; }
    fseek(f, 0, SEEK_END); long size = ftell(f); fseek(f, 0, SEEK_SET);
    BYTE *b = size > 0 && size < 256 * 1024 * 1024 ? (BYTE *)malloc(size) : NULL;
    bool read = b && fread(b, 1, size, f) == (size_t)size;
    fclose(f);
    if (!read) { free(b); _snprintf_s(out, cap, _TRUNCATE, "error cannot read %s", file); return; }
    D3D11_TEXTURE2D_DESC d; D3D11_SUBRESOURCE_DATA sub[16]; char why[200];
    if (!ImgLoadDds(b, size, &d, sub, 16, why, sizeof(why))) { free(b); _snprintf_s(out, cap, _TRUNCATE, "error %s: %s", file, why); return; }
    ID3D11ShaderResourceView *theirs = *(ID3D11ShaderResourceView **)rec;
    ID3D11Device *dev = NULL;
    __try { theirs->GetDevice(&dev); } __except (EXCEPTION_EXECUTE_HANDLER) { dev = NULL; }
    if (!dev) { free(b); _snprintf_s(out, cap, _TRUNCATE, "error the image's view has no device"); return; }
    ID3D11Texture2D *tex = NULL; ID3D11ShaderResourceView *mine = NULL;
    HRESULT hr = dev->CreateTexture2D(&d, sub, &tex);
    if (SUCCEEDED(hr)) hr = dev->CreateShaderResourceView(tex, NULL, &mine);
    dev->Release(); free(b);
    if (tex) tex->Release();                                  // the view keeps it
    if (FAILED(hr) || !mine) { _snprintf_s(out, cap, _TRUNCATE, "error the new texture failed (0x%08lX)", hr); return; }
    // References: ours, and one handed to the game with the record (if the film or map is unloaded while ours is in it,
    // the game may release what the record holds; that must not be our only one)
    int i = 0; while (i < g_imgSwapN && g_imgSwaps[i].rec != rec) i++;
    if (i < g_imgSwapN && g_imgSwaps[i].mine && (void *)theirs != (void *)g_imgSwaps[i].mine) {
        g_imgSwaps[i].mine->Release(); g_imgSwaps[i].mine = NULL;   // the record was made again (a new load): ours is gone from it
    }
    if (i == g_imgSwapN) {
        if (g_imgSwapN >= 64) { mine->Release(); _snprintf_s(out, cap, _TRUNCATE, "error 64 images swapped already: restore some"); return; }
        g_imgSwapN++;
    }
    if (!g_imgSwaps[i].mine) { g_imgSwaps[i].rec = rec; g_imgSwaps[i].theirs = theirs; strcpy_s(g_imgSwaps[i].name, sizeof(g_imgSwaps[i].name), name); }
    ID3D11ShaderResourceView *was = g_imgSwaps[i].mine;
    mine->AddRef();                                            // the game's
    InterlockedExchangePointer((void **)rec, mine);            // the next frame draws with it
    g_imgSwaps[i].mine = mine;
    if (was) { Sleep(100); was->Release(); was->Release(); }   // an earlier swap of ours (and the game's ref on it), after the frame that may still use it
    Log("img: %s swapped (record %p) for %s, %ux%u format %u %u mips", name, rec, file, d.Width, d.Height, (UINT)d.Format, d.MipLevels);
    _snprintf_s(out, cap, _TRUNCATE, "ok %s now draws %s (%ux%u, format %u, %u mips); \"img restore %s\" puts the game's back", name, file, d.Width, d.Height, (UINT)d.Format, d.MipLevels, name);
}

static void ImgRestore(const char *name, char *out, size_t cap)
{
    int n = 0, gone = 0;
    for (int i = 0; i < g_imgSwapN; i++) {
        if (_stricmp(name, "all") && _stricmp(name, g_imgSwaps[i].name)) continue;
        if (!g_imgSwaps[i].mine) continue;
        ID3D11ShaderResourceView *m = g_imgSwaps[i].mine; g_imgSwaps[i].mine = NULL;
        void *now = NULL;
        __try { now = *(void **)g_imgSwaps[i].rec; } __except (EXCEPTION_EXECUTE_HANDLER) { now = NULL; }
        if (now == (void *)m) {                                // still ours: the game's own back, both our refs dropped
            InterlockedExchangePointer((void **)g_imgSwaps[i].rec, g_imgSwaps[i].theirs);
            Sleep(100); m->Release(); m->Release();
            n++;
        }
        else { m->Release(); gone++; }                         // the record was unloaded or made again: nothing to put back
    }
    if (n || gone) _snprintf_s(out, cap, _TRUNCATE, "ok %d image(s) back to the game's own%s", n, gone ? " (others were already gone with a new load)" : "");
    else _snprintf_s(out, cap, _TRUNCATE, "error nothing of ours to put back for \"%s\"", name);
}

// "img find <name>|<file>": the whole report into the file, its start as the answer
static void ImgCommand(const char *arg, char *out, size_t cap)
{
    if (!_strnicmp(arg, "swap ", 5)) {
        if (strlen(arg + 5) >= 520) { _snprintf_s(out, cap, _TRUNCATE, "error the image name and file are too long (over 519 characters)"); return; }
        char name[520]; strcpy_s(name, sizeof(name), arg + 5);
        char *bar = strchr(name, '|');
        if (!bar) { _snprintf_s(out, cap, _TRUNCATE, "error usage: img swap <image name>|<file.dds>"); return; }
        *bar = 0; ImgSwapCmd(name, bar + 1, out, cap); return;
    }
    if (!_strnicmp(arg, "restore ", 8)) { ImgRestore(arg + 8, out, cap); return; }
    if (_strnicmp(arg, "find ", 5)) { _snprintf_s(out, cap, _TRUNCATE, "error usage: img find <name>|<report file> | swap <name>|<file.dds> | restore <name>|all"); return; }
    if (strlen(arg + 5) >= 520) { _snprintf_s(out, cap, _TRUNCATE, "error the image name and file are too long (over 519 characters)"); return; }
    char name[520]; strcpy_s(name, sizeof(name), arg + 5);
    char *bar = strchr(name, '|'); const char *file = NULL;
    if (bar) { *bar = 0; file = arg + 5 + (bar - name) + 1; }
    static char big[32768];
    ImgFind(name, big, sizeof(big));
    if (file) { FILE *f = NULL; if (!fopen_s(&f, file, "w") && f) { fputs(big, f); fputs("\n", f); fclose(f); } }
    strncpy_s(out, cap, big, _TRUNCATE);
}

static DWORD WINAPI FontThread(LPVOID)
{
    char path[MAX_PATH]; GetModuleFileNameA(g_self, path, MAX_PATH);
    char *sl = strrchr(path, '\\'); if (sl) strcpy_s(sl + 1, MAX_PATH - (sl + 1 - path), "RipperBridge.font");
    if (GetFileAttributesA(path) == INVALID_FILE_ATTRIBUTES) { FontStatus("no pack (%s): fonts left alone", path); return 0; }
    if (!FontLoadPack(path)) return 0;
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    FontStatus("pack loaded, waiting for the fonts' zone (without asking the engine)");
    // First choice: Redacted's own log. Zones load one after another, and the fonts are in
    // code_post_gfx_mp, so "Loading zone 'ui_mp'" means they are in - no memory is read. The
    // scan of memory is the fallback when there is no RedactedBase.log beside the exe.
    char rlog[MAX_PATH]; GetModuleFileNameA(NULL, rlog, MAX_PATH);
    char *rs = strrchr(rlog, '\\'); if (rs) strcpy_s(rs + 1, MAX_PATH - (rs + 1 - rlog), "RedactedBase.log");
    double t0 = NowMs(); const char *how = NULL;
    for (int i = 0; !how; i++) {
        if (i >= 360) { FontStatus("the fonts' zone was not loaded after 3 minutes: left alone"); return 0; }
        HANDLE f = CreateFileA(rlog, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
        if (f != INVALID_HANDLE_VALUE) {
            static char buf[1 << 20]; DWORD n = 0;
            ReadFile(f, buf, sizeof(buf) - 1, &n, NULL); CloseHandle(f); buf[n] = 0;
            if (strstr(buf, "Loading zone 'ui_mp'")) how = "RedactedBase.log shows ui_mp loading";
        } else if (i >= 20 && FontZoneLoaded()) how = "the console font's record is in memory";
        if (!how) Sleep(500);
    }
    Log("font: the fonts' zone is in (%s, %.1f s after the pack loaded); the engine is asked in 3 s", how, (NowMs() - t0) / 1000.0);
    Sleep(3000);
    for (int i = 0; i < 240; i++) {                          // up to 2 minutes
        if (FontFind()) {
            char line[300]; int c = 0;
            for (int k = 0; k < g_font.n; k++) {
                const FontEntry &e = g_font.f[k];
                c += _snprintf_s(line + c, sizeof(line) - c, _TRUNCATE, "%s%s %d->%d", k ? ", " : "", strrchr(e.name, '/') + 1, e.saved.glyphCount, e.count);
                if (c < 0) break;
            }
            Log("font: found the fonts (%s) and the sheet image at %p", line, g_font.image);
            int tries = 0;                                   // the sheet's texture may come a little later
            while (!InstallContextHook()) { if (++tries > 240) { FontStatus("no render-thread hook after 2 minutes: left alone"); return 0; } Sleep(500); }
            InterlockedExchange(&g_font.want, 1);
            return 0;
        }
        Sleep(500);
    }
    FontStatus("the fonts or the sheet image were not found in 2 minutes: left alone");
    return 0;
}

// ---- the pipe ---------------------------------------------------------------------------
static bool WriteLine(HANDLE h, const char *s)
{
    DWORD n; size_t len = strlen(s);
    if (!WriteFile(h, s, (DWORD)len, &n, NULL)) return false;
    return WriteFile(h, "\n", 1, &n, NULL) != 0;
}

static void Handle(HANDLE h, char *line)
{
    char out[1024];
    char *arg = strchr(line, ' ');
    if (arg) *arg++ = 0; else arg = line + strlen(line);

    if (!strcmp(line, "ping")) { _snprintf_s(out, sizeof(out), _TRUNCATE, "pong ripperbridge %s", kVersion); WriteLine(h, out); }
    else if (!strcmp(line, "hello")) {
        _snprintf_s(out, sizeof(out), _TRUNCATE, "ripperbridge %s pid %lu base %p present_hooked %d exec %d addcommand %d bad_params %ld",
            kVersion, GetCurrentProcessId(), g_base, g_hooked ? 1 : 0, g_exec ? 1 : 0, g_addCommand ? 1 : 0, g_badParams);
        WriteLine(h, out);
    }
    else if (!strcmp(line, "stats")) {
        if (g_kf.init && g_kf.theater && g_kf.theater == CamTheater()) {   // dev21: how far this film load has played (the Console asks 4x a second)
            EnterCriticalSection(&g_kf.lock); KfReach(ReadInt(kRvaTick)); LeaveCriticalSection(&g_kf.lock);
        }
        _snprintf_s(out, sizeof(out), _TRUNCATE, "frames %ld fps %.1f tick %d endtick %d theater 0x%08X present_last 0x%08lX present_not_ok %ld",
            g_frames, g_fps, ReadInt(kRvaTick), ReadInt(kRvaEndTick), ReadInt(kRvaTheaterBase), (unsigned long)g_lastPresent, g_notOk);
        WriteLine(h, out);
    }
    else if (!strcmp(line, "cmd")) {
        if (!g_exec) ResolveConsole();
        if (!g_exec) WriteLine(h, "error no Cmd_ExecuteCommand");
        else {
            // ExtendedConsole's immediate=false branch is Cbuf_AddText(0, text): a queued
            // command needs its newline, or it waits in the buffer (measured 2026-09-29).
            char q[1100]; _snprintf_s(q, sizeof(q), _TRUNCATE, "%s\n", arg);
            g_exec(q, false); Log("cmd: %s", arg); WriteLine(h, "ok");
        }
    }
    else if (!strcmp(line, "dvar")) {
        if (DvarLookup(arg, out, sizeof(out))) WriteLine(h, out); else WriteLine(h, "error no such dvar");
    }
    else if (!strcmp(line, "model") && !strncmp(arg, "probe ", 6)) { ModelProbe(arg + 6, out, sizeof(out)); WriteLine(h, out); }   // dev47
    else if (!strcmp(line, "asset")) {
        int type = 0; char name[128] = "";
        if (sscanf_s(arg, "%d %127s", &type, name, (unsigned)sizeof(name)) != 2) { WriteLine(h, "error usage: asset <type> <name>  (5 = xmodel)"); return; }
        BYTE *p = FindAsset(type, name); char got[64]; AssetName(p, got, sizeof(got));
        _snprintf_s(out, sizeof(out), _TRUNCATE, "asset %d %s -> %p named '%s'", type, name, p, got);
        WriteLine(h, out);
    }
    else if (!strcmp(line, "map")) {                    // "map shape <file>": the loaded map's shape for the 3D views (RVM2)
        if (!strncmp(arg, "probe ", 6) && arg[6]) { MapProbe(arg + 6, out, sizeof(out)); WriteLine(h, out); return; }   // dev46
        if (!strncmp(arg, "export ", 7) && arg[7]) { MapExport(arg + 7, out, sizeof(out)); WriteLine(h, out); return; } // dev48
        if (!strncmp(arg, "textures ", 9) && arg[9]) { MapTextures(arg + 9, out, sizeof(out)); WriteLine(h, out); return; } // dev51
        if (strncmp(arg, "shape ", 6) || !arg[6]) { WriteLine(h, "error usage: map shape <file> | map probe <dir>"); return; }
        MapShape(arg + 6, out, sizeof(out));
        WriteLine(h, out);
    }
    else if (!strcmp(line, "unprotect") || !strcmp(line, "protect")) {
        if (!*arg) { WriteLine(h, "error usage: unprotect <dvar> / protect <dvar>"); return; }
        DvarProtect(arg, !strcmp(line, "protect"), out, sizeof(out)); WriteLine(h, out);
    }
    else if (!strcmp(line, "setdvar")) {
        char *val = strchr(arg, ' ');
        if (!val) { WriteLine(h, "error usage: setdvar <name> <value...>"); return; }
        *val++ = 0;
        DvarWrite(arg, val, out, sizeof(out));
        WriteLine(h, out);
    }
    else if (!strcmp(line, "read")) {
        unsigned addr = 0, len = 0;
        if (sscanf_s(arg, "%x %u", &addr, &len) != 2 || len == 0 || len > 480) { WriteLine(h, "error usage: read <hexaddr> <len<=480>"); return; }
        BYTE buf[480];
        if (!SafeCopy(buf, (void *)(size_t)addr, len)) { WriteLine(h, "error unreadable"); return; }
        char *o = out; for (unsigned i = 0; i < len; i++) o += sprintf_s(o, 3, "%02X", buf[i]);
        WriteLine(h, out);
    }
    else if (!strcmp(line, "write")) {
        unsigned addr = 0; char hex[961] = "";
        if (sscanf_s(arg, "%x %960s", &addr, hex, (unsigned)sizeof(hex)) != 2 || strlen(hex) % 2) { WriteLine(h, "error usage: write <hexaddr> <hexbytes>"); return; }
        BYTE buf[480]; size_t n = strlen(hex) / 2;
        for (size_t i = 0; i < n; i++) { char b[3] = { hex[2 * i], hex[2 * i + 1], 0 }; buf[i] = (BYTE)strtoul(b, NULL, 16); }
        DWORD old;
        if (!VirtualProtect((void *)(size_t)addr, n, PAGE_EXECUTE_READWRITE, &old)) { WriteLine(h, "error protect"); return; }
        bool ok = SafeCopy((void *)(size_t)addr, buf, n);
        VirtualProtect((void *)(size_t)addr, n, old, &old);
        Log("write %08X %s -> %d", addr, hex, ok);
        WriteLine(h, ok ? "ok" : "error unwritable");
    }
    else if (!strcmp(line, "unhook")) {
        // Put Present back. Only safe while the slot still holds OUR hook; if something
        // hooked after us, taking ours out would cut it off, so refuse.
        if (!g_hooked || !g_slot) { WriteLine(h, "error not hooked"); return; }
        if (*g_slot != (void *)&HookPresent) { WriteLine(h, "error the slot no longer holds our hook - something hooked after us"); return; }
        DWORD old; if (!VirtualProtect(g_slot, sizeof(void *), PAGE_EXECUTE_READWRITE, &old)) { WriteLine(h, "error protect"); return; }
        *g_slot = (void *)g_present; VirtualProtect(g_slot, sizeof(void *), old, &old); g_hooked = false;
        Log("unhooked Present"); WriteLine(h, "ok unhooked");
    }
    else if (!strcmp(line, "slot")) {
        _snprintf_s(out, sizeof(out), _TRUNCATE, "slot %p holds %p; ours %p; original %p", g_slot, g_slot ? *g_slot : 0, (void *)&HookPresent, (void *)g_present);
        WriteLine(h, out);
    }
    else if (!strcmp(line, "loaddemo")) {
        // A film from disk, loaded in-process. The
        // game plays films from memory - normally ones it downloaded - so the file is
        // copied in, described by a {data, size} record at two addresses, its thumbnail
        // written to two slots, one check patched (0xEB = jmp), ui_mapname set from the
        // file name, and "demo_play demo" sent. The t6mpv43 branch has no summary slot.
        // Never while a film is playing: "Loading a demo while one is playing kills the
        // client" - end playback and wait for TheaterBase to reach 0 first.
        if (ReadInt(kRvaTheaterBase) != 0) { WriteLine(h, "error a film is loaded (TheaterBase != 0) - end playback first"); return; }
        // the copy of the film loaded last: no film is loaded now (above), so nothing reads it any more (the code review's
        // C-6: each load kept its own, a film per take all night in a 32-bit game)
        static BYTE *s_lastFilm = NULL;
        if (s_lastFilm) { VirtualFree(s_lastFilm, 0, MEM_RELEASE); s_lastFilm = NULL; }
        if (!g_exec) ResolveConsole();
        if (!g_exec) { WriteLine(h, "error no Cmd_ExecuteCommand"); return; }
        HANDLE f = CreateFileA(arg, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        if (f == INVALID_HANDLE_VALUE) { WriteLine(h, "error cannot open the demo file"); return; }
        DWORD size = GetFileSize(f, NULL), got = 0;
        BYTE *data = (BYTE *)VirtualAlloc(NULL, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        BOOL ok = data && ReadFile(f, data, size, &got, NULL) && got == size;
        CloseHandle(f);
        if (!ok) { if (data) VirtualFree(data, 0, MEM_RELEASE); WriteLine(h, "error reading the demo"); return; }
        s_lastFilm = data;

        char thumbPath[MAX_PATH]; _snprintf_s(thumbPath, sizeof(thumbPath), _TRUNCATE, "%s.thumbnail", arg);
        BYTE thumb[4096]; DWORD thumbLen = 0;
        HANDLE t = CreateFileA(thumbPath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        if (t != INVALID_HANDLE_VALUE) { ReadFile(t, thumb, sizeof(thumb), &thumbLen, NULL); CloseHandle(t); }

        int info[2] = { (int)(size_t)data, (int)size };
        SafeCopy(g_base + 0x2E046D0, info, 8);
        SafeCopy(g_base + 0x2E05678, info, 8);
        if (thumbLen) { SafeCopy(g_base + 0x2B17A38, thumb, thumbLen); SafeCopy(g_base + 0x2BB9AB8, thumb, thumbLen); }
        BYTE *patch = g_base + 0x1C79E6; DWORD old;
        if (VirtualProtect(patch, 1, PAGE_EXECUTE_READWRITE, &old)) { *patch = 0xEB; VirtualProtect(patch, 1, old, &old); }

        // map from the file name: parts[1] + "_" + parts[2], nuketown gets _2020
        const char *base = strrchr(arg, '\\'); base = base ? base + 1 : arg;
        char parts[4][64] = {}; int np = 0; const char *s = base;
        while (np < 4 && *s) { int n = 0; while (*s && *s != '_' && n < 63) parts[np][n++] = *s++; parts[np][n] = 0; np++; if (*s == '_') s++; }
        char map[160]; _snprintf_s(map, sizeof(map), _TRUNCATE, "%s_%s%s", parts[1], parts[2], !strcmp(parts[2], "nuketown") ? "_2020" : "");
        for (char *m = map; *m; m++) if (!isalnum((unsigned char)*m) && *m != '_') *m = '_';   // a shared film's name runs no command (C-16)
        char cmd[256]; _snprintf_s(cmd, sizeof(cmd), _TRUNCATE, "ui_mapname %s\n", map);
        g_exec(cmd, false);
        g_exec("demo_play demo\n", false);
        KfLoaded();                                            // dev21: keyframes belong to the film load they were taken in
        Log("loaddemo %s: %lu bytes at %p, thumbnail %lu, map %s", arg, size, data, thumbLen, map);
        _snprintf_s(out, sizeof(out), _TRUNCATE, "ok %lu bytes, map %s", size, map);
        WriteLine(h, out);
    }
    else if (!strcmp(line, "watch")) {
        int want = atoi(arg); if (want <= 0) want = 60; if (want > 100000) want = 100000;
        if (!g_hooked && !InstallPresentHook()) { WriteLine(h, "error present not hooked"); return; }
        LONG next = g_frames + 1; double deadline = NowMs() + 30000;
        for (int sent = 0; sent < want && NowMs() < deadline; ) {
            LONG cur = g_frames;
            if (cur < next) { Sleep(1); continue; }
            if (cur - next >= kRing - 8) next = cur - 8;               // fell behind: skip ahead
            FrameInfo fi = g_ring[next % kRing];
            if (fi.frame != next) { Sleep(0); continue; }             // slot not written yet
            _snprintf_s(out, sizeof(out), _TRUNCATE, "f %ld ms %.3f tick %d", fi.frame, fi.ms, fi.tick);
            if (!WriteLine(h, out)) return;
            next++; sent++;
        }
        WriteLine(h, "end");
    }
    else if (!strcmp(line, "modes")) { ModesStatus(out, sizeof(out)); WriteLine(h, out); }
    else if (!strcmp(line, "font")) {
        if (!_stricmp(arg, "restore")) { InterlockedExchange(&g_font.want, 2); WriteLine(h, "ok restoring on the next frame"); return; }
        if (!_stricmp(arg, "apply")) {
            if (!g_font.sheet || !g_font.image) { WriteLine(h, "error no pack loaded or the sheet image not found"); return; }
            InterlockedExchange(&g_font.want, 1); WriteLine(h, "ok applying on the next frame"); return;
        }
        _snprintf_s(out, sizeof(out), _TRUNCATE, "font %s; on %d; sheet paints %ld; image %p; texture %p",
            g_font.status[0] ? g_font.status : "idle", g_font.fontsOn ? 1 : 0, g_font.paints, g_font.image, g_font.tex);
        WriteLine(h, out);
    }
    else if (!strcmp(line, "cam")) { CamCommand(arg, out, sizeof(out)); WriteLine(h, out); }
    else if (!strcmp(line, "wpn")) { WpnCommand(arg, out, sizeof(out)); WriteLine(h, out); }
    else if (!strcmp(line, "rec")) {
        if (!_strnicmp(arg, "start ", 6)) RecStart(arg + 6, out, sizeof(out));
        else if (!_stricmp(arg, "stop")) RecStop(out, sizeof(out));
        else if (!_stricmp(arg, "status")) RecStatus(out, sizeof(out));
        else _snprintf_s(out, sizeof(out), _TRUNCATE, "error usage: rec start <ffmpeg>|<out>|<codec args>|end=<tick>[|start=..][|step=..] / rec stop / rec status");
        WriteLine(h, out);
    }
    else if (!strcmp(line, "size")) {
        HWND w = g_scWnd; RECT c = {}, r = {};
        if (w) { GetClientRect(w, &c); GetWindowRect(w, &r); }
        _snprintf_s(out, sizeof(out), _TRUNCATE, "backbuffer %ux%u windowed %d window %p at %d,%d client %ldx%ld style 0x%08lX iconic %d foreground %d owner %p",
            g_scW, g_scH, g_scWindowed ? 1 : 0, w, (int)r.left, (int)r.top, c.right - c.left, c.bottom - c.top,
            w ? (unsigned long)GetWindowLongA(w, GWL_STYLE) : 0UL, w && IsIconic(w) ? 1 : 0, w && GetForegroundWindow() == w ? 1 : 0,
            w ? (void *)GetWindow(w, GW_OWNER) : NULL);
        WriteLine(h, out);
    }
    else if (!strcmp(line, "steady")) { SteadyCommand(arg, out, sizeof(out)); WriteLine(h, out); }
    else if (!strcmp(line, "dolly")) { DollyCommand(arg, out, sizeof(out)); WriteLine(h, out); }
    else if (!strcmp(line, "kf")) { KfCommand(arg, out, sizeof(out)); WriteLine(h, out); }
    else if (!strcmp(line, "gfx")) { GfxCommand(arg, out, sizeof(out)); WriteLine(h, out); }
    else if (!strcmp(line, "img")) { ImgCommand(arg, out, sizeof(out)); WriteLine(h, out); }
    else if (!strcmp(line, "fog")) { FogCommand(arg, out, sizeof(out)); WriteLine(h, out); }
    else if (!strcmp(line, "params")) { ParamsCommand(arg, out, sizeof(out)); WriteLine(h, out); }
    else if (!strcmp(line, "mem")) { MemCommand(arg, out, sizeof(out)); WriteLine(h, out); }
    else if (!strcmp(line, "lockprobe")) { LockProbeCommand(arg, out, sizeof(out)); WriteLine(h, out); }
    else { _snprintf_s(out, sizeof(out), _TRUNCATE, "error unknown command '%s'", line); WriteLine(h, out); }
}

static void Serve(HANDLE h)
{
    char buf[4096]; size_t have = 0;
    for (;;) {
        DWORD n = 0;
        if (!ReadFile(h, buf + have, (DWORD)(sizeof(buf) - 1 - have), &n, NULL) || n == 0) return;
        have += n; buf[have] = 0;
        char *start = buf, *nl;
        while ((nl = strchr(start, '\n')) != NULL) {
            *nl = 0; if (nl > start && nl[-1] == '\r') nl[-1] = 0;
            if (*start) Handle(h, start);
            start = nl + 1;
        }
        have = strlen(start); memmove(buf, start, have + 1);
        if (have >= sizeof(buf) - 1) have = 0;                         // a line too long: drop it
    }
}

static DWORD WINAPI PipeThread(LPVOID)
{
    for (;;) {
        HANDLE h = CreateNamedPipeA("\\\\.\\pipe\\bo2ripper", PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 0, NULL);
        if (h == INVALID_HANDLE_VALUE) { Log("CreateNamedPipe failed: %lu", GetLastError()); Sleep(2000); continue; }
        if (ConnectNamedPipe(h, NULL) || GetLastError() == ERROR_PIPE_CONNECTED) { Log("client connected"); Serve(h); Log("client gone"); }
        DisconnectNamedPipe(h); CloseHandle(h);
    }
}

static DWORD WINAPI StartThread(LPVOID)
{
    Sleep(3000);                                  // let the game make its own device first
    ResolveConsole();
    // Present is hooked at start only when RipperBridge.present is beside the exe (the sandbox
    // copy); otherwise only when a capture or watch asks for it. In Mycelium's real game Steam's
    // overlay is injected, and Present hooked from here killed the game 21 s in (stack
    // overflow in gameoverlayrenderer.dll, 2026-09-30 22:35). RipperBridge.nohook still means
    // never.
    if (PresentFlag("RipperBridge.present")) InstallPresentHook();
    else Log("Present not hooked at start (no RipperBridge.present); Steam overlay %s",
             GetModuleHandleA("gameoverlayrenderer.dll") ? "loaded" : "not loaded yet");
    CloseHandle(CreateThread(NULL, 0, FontThread, NULL, 0, NULL));   // paints from its own hook
    return 0;
}

static void Start()
{
    static bool started; if (started) return; started = true;
    char path[MAX_PATH]; GetModuleFileNameA(NULL, path, MAX_PATH);
    char *slash = strrchr(path, '\\'); if (slash) strcpy_s(slash + 1, MAX_PATH - (slash + 1 - path), "RipperBridge.log");
    g_log = _fsopen(path, "a", _SH_DENYNO);   // readable while the game runs
    g_base = (BYTE *)GetModuleHandleA(NULL);
    QueryPerformanceFrequency(&g_qpf);
    Log("RipperBridge %s starting in pid %lu, module base %p", kVersion, GetCurrentProcessId(), g_base);
    InstallModeHooks();                           // before the renderer asks (sandbox only)
    CloseHandle(CreateThread(NULL, 0, PipeThread, NULL, 0, NULL));
    CloseHandle(CreateThread(NULL, 0, StartThread, NULL, 0, NULL));
}

// ---- the Redacted plugin exports -----------------------------------------------------
extern "C" {
__declspec(dllexport) u64 __cdecl PreInit() { Start(); return 1; }
__declspec(dllexport) u64 __cdecl PostInit() { return 1; }
__declspec(dllexport) u64 __cdecl Uninit() { Log("Uninit"); return 1; }
__declspec(dllexport) u64 __cdecl OfficialMod() { return 0; }
__declspec(dllexport) u64 __cdecl DependencyCount() { return 0; }
__declspec(dllexport) u64 __cdecl GetDependency(int) { return (u64)(size_t)""; }
__declspec(dllexport) u64 __cdecl AuthorInfo() { return (u64)(size_t)"Mycelium (Reluduali)"; }
__declspec(dllexport) u64 __cdecl ExtendedInfo() { return (u64)(size_t)"Named-pipe bridge for outside apps: \\\\.\\pipe\\bo2ripper"; }
__declspec(dllexport) u64 __cdecl SendMessageB() { return 1; }
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_self = h; DisableThreadLibraryCalls(h); _set_invalid_parameter_handler(QuietBadParameter); }
    return TRUE;
}
