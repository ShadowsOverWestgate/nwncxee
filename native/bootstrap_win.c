// Windows sibling of bootstrap.c. Windows has no LD_PRELOAD, so we get loaded as a
// PROXY DLL: build this as `version.dll`, drop it next to nwmain.exe, and the game
// loads it (nwmain.exe imports VERSION.dll: GetFileVersionInfoA/SizeA, VerQueryValueA)
// before the real one. We forward every version.dll export to the system copy so the
// game's version calls still work, and in DllMain we start the managed extender --
// exactly what the LD_PRELOAD ctor does.
//
// Build here (Linux cross, clang-cl + lld-link) or on Windows (MSVC) -- see
// build-win-proxy.sh. The forwards below are the whole proxy mechanism.
//
// ponytail: no <windows.h>/<stdlib.h>. The handful of kernel32 calls are declared
// inline so this compiles with clang-cl on Linux -- no MSVC VCTools/CRT headers
// needed (Arch has the Win10 SDK but not vcruntime.h). Add the real headers back
// only if this ever grows past a dozen Win32 calls.
//
// ponytail: forwards hard-code System32\version.<fn>. Correct for a 64-bit DLL in the
// 64-bit client; if we ever ship 32-bit, forward to SysWOW64 instead.

// Forward all of version.dll to the real one so the host's version queries still work.
#pragma comment(linker, "/export:GetFileVersionInfoA=C:\\Windows\\System32\\version.GetFileVersionInfoA")
#pragma comment(linker, "/export:GetFileVersionInfoW=C:\\Windows\\System32\\version.GetFileVersionInfoW")
#pragma comment(linker, "/export:GetFileVersionInfoExA=C:\\Windows\\System32\\version.GetFileVersionInfoExA")
#pragma comment(linker, "/export:GetFileVersionInfoExW=C:\\Windows\\System32\\version.GetFileVersionInfoExW")
#pragma comment(linker, "/export:GetFileVersionInfoSizeA=C:\\Windows\\System32\\version.GetFileVersionInfoSizeA")
#pragma comment(linker, "/export:GetFileVersionInfoSizeW=C:\\Windows\\System32\\version.GetFileVersionInfoSizeW")
#pragma comment(linker, "/export:GetFileVersionInfoSizeExA=C:\\Windows\\System32\\version.GetFileVersionInfoSizeExA")
#pragma comment(linker, "/export:GetFileVersionInfoSizeExW=C:\\Windows\\System32\\version.GetFileVersionInfoSizeExW")
#pragma comment(linker, "/export:GetFileVersionInfoByHandle=C:\\Windows\\System32\\version.GetFileVersionInfoByHandle")
#pragma comment(linker, "/export:VerFindFileA=C:\\Windows\\System32\\version.VerFindFileA")
#pragma comment(linker, "/export:VerFindFileW=C:\\Windows\\System32\\version.VerFindFileW")
#pragma comment(linker, "/export:VerInstallFileA=C:\\Windows\\System32\\version.VerInstallFileA")
#pragma comment(linker, "/export:VerInstallFileW=C:\\Windows\\System32\\version.VerInstallFileW")
#pragma comment(linker, "/export:VerLanguageNameA=C:\\Windows\\System32\\version.VerLanguageNameA")
#pragma comment(linker, "/export:VerLanguageNameW=C:\\Windows\\System32\\version.VerLanguageNameW")
#pragma comment(linker, "/export:VerQueryValueA=C:\\Windows\\System32\\version.VerQueryValueA")
#pragma comment(linker, "/export:VerQueryValueW=C:\\Windows\\System32\\version.VerQueryValueW")

// --- Minimal Win32 surface (see ponytail note above) ------------------------
#define WINAPI __stdcall
typedef int                 BOOL;
typedef unsigned long       DWORD;
typedef void               *HANDLE;
typedef void               *HMODULE;
typedef void               *HINSTANCE;
typedef void               *LPVOID;
typedef const char         *LPCSTR;
typedef char               *LPSTR;
typedef unsigned long long  SIZE_T;
typedef DWORD (WINAPI *LPTHREAD_START_ROUTINE)(LPVOID);
typedef void  (*SOWX_INIT)(void);

#define DLL_PROCESS_ATTACH 1

__declspec(dllimport) BOOL    WINAPI DisableThreadLibraryCalls(HMODULE);
__declspec(dllimport) HANDLE  WINAPI CreateThread(LPVOID, SIZE_T, LPTHREAD_START_ROUTINE, LPVOID, DWORD, DWORD *);
__declspec(dllimport) BOOL    WINAPI CloseHandle(HANDLE);
__declspec(dllimport) HMODULE WINAPI LoadLibraryA(LPCSTR);
__declspec(dllimport) LPVOID  WINAPI GetProcAddress(HMODULE, LPCSTR);
__declspec(dllimport) DWORD   WINAPI GetEnvironmentVariableA(LPCSTR, LPSTR, DWORD);
__declspec(dllimport) void    WINAPI OutputDebugStringA(LPCSTR);
// ---------------------------------------------------------------------------

static DWORD WINAPI sowx_start(LPVOID unused)
{
    (void) unused;
    // Off the loader lock: LoadLibrary + NativeAOT init + funchook installs cannot
    // run inside DllMain without risking a loader-lock deadlock. DllMain kicks this
    // thread and returns; it runs once the loader releases.
    char buf[260];
    DWORD n = GetEnvironmentVariableA("SOWX_MANAGED", buf, sizeof(buf));
    LPCSTR path = (n > 0 && n < sizeof(buf)) ? buf : "sowx_managed.dll"; // app dir / PATH

    HMODULE h = LoadLibraryA(path);
    if (!h) { OutputDebugStringA("[sowx] LoadLibrary(sowx_managed.dll) failed\n"); return 1; }

    SOWX_INIT init = (SOWX_INIT) GetProcAddress(h, "SowxInit");
    if (!init) { OutputDebugStringA("[sowx] GetProcAddress(SowxInit) failed\n"); return 1; }

    init();
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void) reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(inst);
        HANDLE t = CreateThread(0, 0, sowx_start, 0, 0, 0);
        if (t) CloseHandle(t);
    }
    return 1; // TRUE
}
