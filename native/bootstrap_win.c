// Windows sibling of bootstrap.c. Windows has no LD_PRELOAD, so we get loaded as a
// PROXY DLL: build this as `version.dll`, drop it next to nwmain.exe, and the game
// loads it (nwmain.exe imports VERSION.dll) before the real one. We forward every
// version.dll export to the system copy so the game's version calls still work, and
// in DllMain we start the managed extender -- exactly what the LD_PRELOAD ctor does.
//
// Build (CI, MSVC or clang-cl):  cl /LD bootstrap_win.c /Fe:version.dll
// The pragma forwards below need MSVC/clang-cl; they are the whole proxy mechanism.
//
// ponytail: forwards hard-code System32\version.<fn>. Correct for a 64-bit DLL in the
// 64-bit client; if we ever ship 32-bit, forward to SysWOW64 instead.
#include <windows.h>
#include <stdlib.h>

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

static DWORD WINAPI sowx_start(LPVOID unused)
{
    // Off the loader lock: LoadLibrary + NativeAOT init + funchook installs cannot
    // run inside DllMain without risking a loader-lock deadlock. DllMain kicks this
    // thread and returns; it runs once the loader releases.
    const char *path = getenv("SOWX_MANAGED");
    if (!path) path = "sowx_managed.dll";           // resolved via app dir / PATH

    HMODULE h = LoadLibraryA(path);
    if (!h) { OutputDebugStringA("[sowx] LoadLibrary(sowx_managed.dll) failed\n"); return 1; }

    void (*init)(void) = (void (*)(void)) GetProcAddress(h, "SowxInit");
    if (!init) { OutputDebugStringA("[sowx] GetProcAddress(SowxInit) failed\n"); return 1; }

    init();
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(inst);
        HANDLE t = CreateThread(NULL, 0, sowx_start, NULL, 0, NULL);
        if (t) CloseHandle(t);
    }
    return TRUE;
}
