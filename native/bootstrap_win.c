// The Windows half of the load path. Unlike bootstrap.c this is NOT a separate module: it is compiled to
// an .obj and linked INTO cx_managed.dll, which then ships as `version.dll`. One file next to nwmain.exe
// is the whole install.
//
// Windows has no LD_PRELOAD, so we get loaded as a PROXY DLL: nwmain.exe imports VERSION.dll
// (GetFileVersionInfoA/SizeA, VerQueryValueA), so a version.dll beside the exe is loaded before the real
// one. We forward every version.dll export to the system copy so the game's version calls still work, and
// start the managed extender on the way past -- exactly what the LD_PRELOAD ctor does.
//
// Why a CRT initializer and not DllMain: the NativeAOT image already owns DllMain (it initializes the
// runtime there), and a second definition would not link. The .CRT$XCU table runs during that same
// DLL_PROCESS_ATTACH, which is early enough to be sure we run and late enough to have a thread to start.
//
// ponytail: no <windows.h>/<stdlib.h>. The handful of kernel32 calls are declared inline so this compiles
// with clang-cl on Linux -- no MSVC VCTools/CRT headers needed (Arch has the Win10 SDK but not vcruntime.h).
// Add the real headers back only if this ever grows past a dozen Win32 calls.
//
// ponytail: forwards hard-code System32\version.<fn>. Correct for a 64-bit DLL in the 64-bit client; if we
// ever ship 32-bit, forward to SysWOW64 instead.

// Forward all of version.dll to the real one so the host's version queries still work. These must stay
// #pragma directives: routed through an MSBuild <LinkerArg> instead, the backslashes come out as forward
// slashes and the NT loader cannot resolve the forward.
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
typedef void               *LPVOID;
typedef unsigned long long  SIZE_T;
typedef DWORD (WINAPI *LPTHREAD_START_ROUTINE)(LPVOID);

__declspec(dllimport) HANDLE WINAPI CreateThread(LPVOID, SIZE_T, LPTHREAD_START_ROUTINE, LPVOID, DWORD, DWORD *);
__declspec(dllimport) BOOL   WINAPI CloseHandle(HANDLE);
// ---------------------------------------------------------------------------

/// The managed entry point, exported by the NativeAOT half of this same DLL.
extern void CxInit(void);

static DWORD WINAPI cx_start(LPVOID unused)
{
    (void) unused;
    // Off the loader lock: NativeAOT runtime startup and the funchook installs cannot run inside
    // DLL_PROCESS_ATTACH without risking a loader-lock deadlock. A thread created there does not run
    // until the loader releases, which is also after the image's own DllMain has started the runtime.
    CxInit();
    return 0;
}

static int cx_boot(void)
{
    HANDLE t = CreateThread(0, 0, cx_start, 0, 0, 0);
    if (t) CloseHandle(t);
    return 0;
}

// The CRT initializer table. `read` + the allocate pragma is the documented way to place a function
// pointer there without a CRT; the linker walks .CRT$XC* in name order during process attach.
#pragma section(".CRT$XCU", long, read)
__declspec(allocate(".CRT$XCU")) int (*cx_boot_)(void) = cx_boot;
