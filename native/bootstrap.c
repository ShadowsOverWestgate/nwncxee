// The only native code in the project: get loaded, hand control to C#.
// LD_PRELOAD this .so; the constructor fires when the host process starts.
// Real version would, after init, install funchook hooks whose targets are
// the C# [UnmanagedCallersOnly] exports.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

__attribute__((constructor))
static void sowx_bootstrap(void)
{
    fprintf(stderr, "[sowx] bootstrap: loaded into pid %d\n", getpid());

    const char *path = getenv("SOWX_MANAGED");
    if (!path) path = "sowx_managed.so"; // resolved via LD_LIBRARY_PATH / rpath

    void *h = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
    if (!h) {
        fprintf(stderr, "[sowx] bootstrap: dlopen(%s) failed: %s\n", path, dlerror());
        return;
    }

    void (*init)(void) = (void (*)(void)) dlsym(h, "SowxInit");
    if (!init) {
        fprintf(stderr, "[sowx] bootstrap: dlsym(SowxInit) failed: %s\n", dlerror());
        return;
    }

    init();
}
