// Light-loop microbench: the real cx_lightfrag + cx_localshadow (composed by compose.py) shading a
// full-screen ground plane at 2544x1379 under the rainy plaza's light load -- 32 lights in every
// cluster, 21 with a cube, statics baked, every cube holding a mover page. Times three receivers:
// baked+wet, baked+matte, unbaked. Relative numbers only: the lit stage's own register pressure is
// not here. Build + run (NVIDIA; for Mesa add LB_MESA=1 LIBGL_ALWAYS_SOFTWARE=1 and the 50_mesa.json vendor
// file with --compile -- llvmpipe proves the GLSL front end radeonsi shares, not a timing):
//   gcc -O2 tools/lightbench/bench.c -o /tmp/lightbench -lEGL -lGL
//   tools/lightbench/compose.py managed/Alloy/Shaders /tmp/lb.glsl
//   __EGL_VENDOR_LIBRARY_FILENAMES=/usr/share/glvnd/egl_vendor.d/10_nvidia.json /tmp/lightbench /tmp/lb.glsl
// --full on both compose.py and the bench runs the real lit stage instead: the number that tracks the game.
#define GL_GLEXT_PROTOTYPES
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GL/gl.h>
#include <GL/glext.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { W = 2544, H = 1379, GX = 32, GY = 18, GZ = 24, CAP = 128, NL = 32, CUBES = 21, DYNPAGES = 32, SPH = 16 };
static const char* VS = "#version 460 core\nvoid main(){ vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }";
// --full: the lit stage's own varyings over the same ground plane (uv 0..1 across the screen)
static const char* VS_FULL = "#version 460 core\n"
    "out vec2 vUV; out vec3 vWorld; out vec3 vNrm; out float vDepth; out float vAnchorZ; out vec2 vChart; flat out int vDraw;\n"
    "void main(){ vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);\n"
    "  vWorld = vec3(p.x * 80.0 - 40.0, 0.0, p.y * 60.0 - 10.0); vNrm = vec3(0.0, 1.0, 0.0);\n"
    "  vDepth = length(vec3(0.0, 25.0, -30.0) - vWorld); vAnchorZ = 0.0; vUV = p * 8.0; vChart = p * 512.0; vDraw = 0; }";

static GLuint sh(GLenum t, const char* s)
{
    GLuint o = glCreateShader(t); glShaderSource(o, 1, &s, 0); glCompileShader(o);
    GLint ok; glGetShaderiv(o, GL_COMPILE_STATUS, &ok);
    if (!ok) { static char log[1 << 16]; glGetShaderInfoLog(o, sizeof log, 0, log); fprintf(stderr, "compile:\n%s\n", log); exit(1); }
    return o;
}
static GLuint cubeArray(int size, int cubes)
{
    GLuint t; glGenTextures(1, &t); glBindTexture(GL_TEXTURE_CUBE_MAP_ARRAY, t);
    glTexStorage3D(GL_TEXTURE_CUBE_MAP_ARRAY, 1, GL_DEPTH_COMPONENT32F, size, size, cubes * 6);
    float* px = malloc((size_t)size * size * sizeof(float));
    for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)   // blockers in a third of the face, lit elsewhere: partial occlusion everywhere
            px[y * size + x] = ((x / 32 + y / 32) % 3 == 0) ? 0.3f : 1.0f;
    for (int l = 0; l < cubes * 6; l++)
        glTexSubImage3D(GL_TEXTURE_CUBE_MAP_ARRAY, 0, 0, 0, l, size, size, 1, GL_DEPTH_COMPONENT, GL_FLOAT, px);
    free(px);
    return t;
}
static GLint U(GLuint p, const char* n) { return glGetUniformLocation(p, n); }

int main(int argc, char** argv)
{
    PFNEGLQUERYDEVICESEXTPROC qd = (void*)eglGetProcAddress("eglQueryDevicesEXT");
    PFNEGLGETPLATFORMDISPLAYEXTPROC gpd = (void*)eglGetProcAddress("eglGetPlatformDisplayEXT");
    EGLDeviceEXT devs[8]; EGLint nd = 0; qd(8, devs, &nd);
    EGLDisplay d = getenv("LB_MESA") ? gpd(0x31DD /* EGL_PLATFORM_SURFACELESS_MESA */, EGL_DEFAULT_DISPLAY, 0) : gpd(EGL_PLATFORM_DEVICE_EXT, devs[0], 0); eglInitialize(d, 0, 0); eglBindAPI(EGL_OPENGL_API);
    EGLint ca[] = { EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT, EGL_NONE }; EGLConfig cfg; EGLint n;
    eglChooseConfig(d, ca, &cfg, 1, &n);
    EGLint cx[] = { EGL_CONTEXT_MAJOR_VERSION, 4, EGL_CONTEXT_MINOR_VERSION, 6, EGL_CONTEXT_OPENGL_PROFILE_MASK, EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT, EGL_NONE };
    EGLContext c = eglCreateContext(d, cfg, EGL_NO_CONTEXT, cx);
    EGLint pa[] = { EGL_WIDTH, 8, EGL_HEIGHT, 8, EGL_NONE }; EGLSurface sf = eglCreatePbufferSurface(d, cfg, pa);
    if (!eglMakeCurrent(d, sf, sf, c)) { fprintf(stderr, "no ctx\n"); return 1; }
    printf("%s\n", glGetString(GL_RENDERER));

    FILE* f = fopen(argv[1], "rb"); fseek(f, 0, SEEK_END); long L = ftell(f); rewind(f);
    char* fs = malloc(L + 1); fread(fs, 1, L, f); fs[L] = 0;
    int full = argc > 2 && !strcmp(argv[argc - 1], "--full");
    GLuint p = glCreateProgram(); glAttachShader(p, sh(GL_VERTEX_SHADER, full ? VS_FULL : VS)); glAttachShader(p, sh(GL_FRAGMENT_SHADER, fs)); glLinkProgram(p);
    GLint ok; glGetProgramiv(p, GL_LINK_STATUS, &ok);
    if (!ok) { static char log[1 << 16]; glGetProgramInfoLog(p, sizeof log, 0, log); fprintf(stderr, "link:\n%s\n", log); return 1; }
    if (argc > 2 && !strcmp(argv[2], "--compile")) { puts("compiled + linked"); return 0; }
    glUseProgram(p);

    // lights: a grid over the plane, 0..27 static (0..16 with a cube), 28..31 dynamic with a cube
    float lights[NL * 8], cubeLight[64 * 4] = { 0 }, dynPage[64] = { 0 }, fade[64], movers[64 * SPH * 4], union_[64 * 4];
    const float mv[4][4] = { { 0, 1, 15, 1 }, { -10, 1, 25, 1 }, { 12, 1, 5, 1 }, { 5, 1, 35, 1 } };
    for (int i = 0; i < NL; i++)
    {
        float x = -35.0f + (i % 8) * 10.0f, z = -5.0f + (i / 8) * 13.0f;
        int slot = i < 17 ? i : i >= 28 ? 17 + (i - 28) : -1;
        float w = (float)slot + (i < 28 ? 256.0f : 0.0f);
        float* r = lights + i * 8;
        r[0] = x; r[1] = 4.0f; r[2] = z; r[3] = 36.0f; r[4] = 0.9f; r[5] = 0.7f; r[6] = 0.5f; r[7] = w;
        if (slot >= 0) { cubeLight[slot * 4] = x; cubeLight[slot * 4 + 1] = 4.0f; cubeLight[slot * 4 + 2] = z; cubeLight[slot * 4 + 3] = 36.0f; dynPage[slot] = (float)slot + 1.0f; }
    }
    for (int s = 0; s < 64; s++)
    {
        fade[s] = 1.0f;
        for (int k = 0; k < SPH; k++)   // four movers per slot, the rest unused (r < 0), as CasterPass writes them
            if (k < 4) memcpy(movers + (s * SPH + k) * 4, mv[k], sizeof mv[k]); else movers[(s * SPH + k) * 4 + 3] = -1.0f;
        union_[s * 4] = 1; union_[s * 4 + 1] = 1; union_[s * 4 + 2] = 20; union_[s * 4 + 3] = 22;   // the pre-split union sphere
    }
    uint32_t* counts = malloc(GX * GY * GZ * 4); uint32_t* idx = malloc((size_t)GX * GY * GZ * CAP * 4);
    int binned = getenv("LB_LIGHTS") ? atoi(getenv("LB_LIGHTS")) : NL;   // the bincap A/B: lights kept per cluster
    // LB_LIVEFIRST: dynamic lights first, then the cube statics -- the list a baked receiver must walk
    static const int liveFirst[NL] = { 28, 29, 30, 31, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
                                       17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27 };
    // LB_PACK: the partitioned build's layout (live first, live count in the high half) -- off for a
    // shader tree from before the split, which reads the count raw
    int pack = getenv("LB_PACK") != 0, lf = pack || getenv("LB_LIVEFIRST") != 0, live = binned < 21 ? binned : 21;
    for (int cl = 0; cl < GX * GY * GZ; cl++)
    {
        counts[cl] = pack ? (uint32_t)binned | ((uint32_t)live << 16) : (uint32_t)binned;
        for (int k = 0; k < NL; k++) idx[cl * CAP + k] = lf ? liveFirst[k] : k;
    }
    GLuint b[7]; glGenBuffers(7, b);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[0]); glBufferData(GL_SHADER_STORAGE_BUFFER, sizeof lights, lights, GL_STATIC_DRAW);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[1]); glBufferData(GL_SHADER_STORAGE_BUFFER, GX * GY * GZ * 4, counts, GL_STATIC_DRAW);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[2]); glBufferData(GL_SHADER_STORAGE_BUFFER, (size_t)GX * GY * GZ * CAP * 4, idx, GL_STATIC_DRAW);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[3]); glBufferData(GL_SHADER_STORAGE_BUFFER, sizeof movers, movers, GL_STATIC_DRAW);
    glBindBufferBase(GL_SHADER_STORAGE_BUFFER, 0, b[0]); glBindBufferBase(GL_SHADER_STORAGE_BUFFER, 1, b[1]);
    glBindBufferBase(GL_SHADER_STORAGE_BUFFER, 2, b[2]); glBindBufferBase(GL_SHADER_STORAGE_BUFFER, 25, b[3]);
    // --full: one zeroed CxDraw record (240 B), draw 0 -> slot 0, and slot 0's chart on page 0
    float rec[60] = { 0 }; int drawIdx = 0; float chart[4] = { 0, 0, 0, 1 };
    rec[0] = rec[5] = rec[10] = rec[15] = 1.0f;
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[4]); glBufferData(GL_SHADER_STORAGE_BUFFER, sizeof rec, rec, GL_STATIC_DRAW);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[5]); glBufferData(GL_SHADER_STORAGE_BUFFER, sizeof drawIdx, &drawIdx, GL_STATIC_DRAW);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[6]); glBufferData(GL_SHADER_STORAGE_BUFFER, sizeof chart, chart, GL_STATIC_DRAW);
    glBindBufferBase(GL_SHADER_STORAGE_BUFFER, 4, b[4]); glBindBufferBase(GL_SHADER_STORAGE_BUFFER, 9, b[5]);
    glBindBufferBase(GL_SHADER_STORAGE_BUFFER, 6, b[6]);

    GLuint st = cubeArray(512, CUBES), dy = cubeArray(256, DYNPAGES), smp[2];
    glGenSamplers(2, smp);
    glSamplerParameteri(smp[0], GL_TEXTURE_COMPARE_MODE, GL_COMPARE_REF_TO_TEXTURE); glSamplerParameteri(smp[0], GL_TEXTURE_COMPARE_FUNC, GL_LEQUAL);
    glSamplerParameteri(smp[0], GL_TEXTURE_MIN_FILTER, GL_LINEAR); glSamplerParameteri(smp[0], GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glSamplerParameteri(smp[1], GL_TEXTURE_MIN_FILTER, GL_NEAREST); glSamplerParameteri(smp[1], GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    const struct { int unit; GLuint tex, smp; } bind[] = { { 10, st, smp[0] }, { 11, dy, smp[0] }, { 16, st, smp[1] }, { 17, dy, smp[1] } };
    for (int i = 0; i < 4; i++) { glActiveTexture(GL_TEXTURE0 + bind[i].unit); glBindTexture(GL_TEXTURE_CUBE_MAP_ARRAY, bind[i].tex); glBindSampler(bind[i].unit, bind[i].smp); }
    glUniform1i(U(p, "uLocalShadow"), 10); glUniform1i(U(p, "uLocalDynShadow"), 11);

    glUniform3i(U(p, "uGrid"), GX, GY, GZ); glUniform4f(U(p, "uTile"), 0, 0, (float)W / GX, (float)H / GY);
    glUniform2f(U(p, "uZ"), 0.1f, 300.0f); glUniform3f(U(p, "uFalloff"), 1, 1, 1); glUniform2f(U(p, "uAtten"), 0.41f, 136.334f);
    glUniform1f(U(p, "uReach"), 0.8f); glUniform2f(U(p, "uLightOcc"), 1, 0.5f); glUniform1i(U(p, "uLmMovers"), 1);
    glUniform1i(U(p, "uLocalCubes"), CUBES); glUniform4fv(U(p, "uLocalLight"), 64, cubeLight);
    glUniform2f(U(p, "uLocalPlane"), 1.0f, 1.0f / 512); glUniform4f(U(p, "uLocalSoft"), 1.0f, 1.0f, 0.0f, 2.0f);
    glUniform4f(U(p, "uLocalPcss"), 1.0f, 0.0f, 8.0f, 40.0f); glUniform1fv(U(p, "uLocalDynPage"), 64, dynPage);
    glUniform1fv(U(p, "uLocalFade"), 64, fade); glUniform1f(U(p, "uLocalFloor"), 0.1f);
    if (U(p, "uLocalDynSphere") >= 0) glUniform4fv(U(p, "uLocalDynSphere"), 64, union_);
    glUniform2f(U(p, "uRes"), W, H);

    GLuint fb, rb, rb2; glGenFramebuffers(1, &fb); glGenRenderbuffers(1, &rb);
    glBindRenderbuffer(GL_RENDERBUFFER, rb); glRenderbufferStorage(GL_RENDERBUFFER, GL_RGBA16F, W, H);
    glBindFramebuffer(GL_FRAMEBUFFER, fb); glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, rb);
    glGenRenderbuffers(1, &rb2); glBindRenderbuffer(GL_RENDERBUFFER, rb2); glRenderbufferStorage(GL_RENDERBUFFER, GL_R16F, W, H);
    glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT1, GL_RENDERBUFFER, rb2);
    GLenum bufs[2] = { GL_COLOR_ATTACHMENT0, GL_COLOR_ATTACHMENT1 }; glDrawBuffers(2, bufs);
    glViewport(0, 0, W, H);
    GLuint vao; glGenVertexArrays(1, &vao); glBindVertexArray(vao);

    const struct { const char* name; float baked, rough; } cases[] = { { "baked wet", 1, 0.35f }, { "baked matte", 1, 0.8f }, { "unbaked", 0, 0.8f } };
    GLuint q; glGenQueries(1, &q);
    for (int k = 0; k < 3; k++)
    {
        glUniform1f(U(p, "uBaked"), cases[k].baked); glUniform1f(U(p, "uRough"), cases[k].rough);
        glUniform1i(U(p, "uLmPages"), cases[k].baked > 0.5f ? 1 : 0);   // --full: the bake gate
        float wet = cases[k].rough < 0.5f ? 1.0f : 0.0f;
        glUniform4f(U(p, "uWeather"), wet, 0.0f, wet, 0.0f);
        for (int i = 0; i < 10; i++) glDrawArrays(GL_TRIANGLES, 0, 3);
        glFinish();
        double ms[40];
        for (int i = 0; i < 40; i++)
        {
            glBeginQuery(GL_TIME_ELAPSED, q); glDrawArrays(GL_TRIANGLES, 0, 3); glEndQuery(GL_TIME_ELAPSED);
            GLuint64 ns; glGetQueryObjectui64v(q, GL_QUERY_RESULT, &ns); ms[i] = ns / 1e6;
        }
        for (int i = 1; i < 40; i++) for (int j = i; j > 0 && ms[j - 1] > ms[j]; j--) { double t = ms[j]; ms[j] = ms[j - 1]; ms[j - 1] = t; }
        float px[4]; glReadPixels(W / 2, H / 3, 1, 1, GL_RGBA, GL_FLOAT, px);
        printf("%-12s centre (%.2f %.2f %.2f)  median %.3f ms  (min %.3f, max %.3f)\n", cases[k].name, px[0], px[1], px[2], ms[20], ms[0], ms[39]);
    }
    return 0;
}
