// The only native code in the project: get loaded, hand control to C#.
// LD_PRELOAD this .so; the constructor fires when the host process starts.
// Real version would, after init, install funchook hooks whose targets are
// the C# [UnmanagedCallersOnly] exports.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

// Under the Steam runtime LD_PRELOAD reaches every helper it spawns ahead of the client (wrappers, reaper),
// and each one would boot the extender, read ITS OWN build-id and log a false "client build changed".
// Only a process whose executable is named nwmain* is the client.
static int cx_is_client(void)
{
    char exe[4096];
    ssize_t n = readlink("/proc/self/exe", exe, sizeof exe - 1);
    if (n <= 0) return 0;
    exe[n] = 0;
    const char *base = strrchr(exe, '/');
    base = base ? base + 1 : exe;
    return strncmp(base, "nwmain", 6) == 0;
}

__attribute__((constructor))
static void cx_bootstrap(void)
{
    if (!cx_is_client()) return;
    fprintf(stderr, "[cx] bootstrap: loaded into pid %d\n", getpid());

    const char *path = getenv("CX_MANAGED");
    if (!path) path = "cx_managed.so"; // resolved via LD_LIBRARY_PATH / rpath

    void *h = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
    if (!h) {
        fprintf(stderr, "[cx] bootstrap: dlopen(%s) failed: %s\n", path, dlerror());
        return;
    }

    void (*init)(void) = (void (*)(void)) dlsym(h, "CxInit");
    if (!init) {
        fprintf(stderr, "[cx] bootstrap: dlsym(CxInit) failed: %s\n", dlerror());
        return;
    }

    init();
}
