using Sowx.Framework;

namespace Sowx.Ui;

/// <summary>
/// Minimal OpenGL 3.3-core entry points for the owned text draw. The client requests a CORE
/// profile (verified: SDL_GL_SetAttribute PROFILE_MASK=CORE, 3.3), so there is no fixed-function
/// pipeline -- everything goes through shaders/VAO/VBO. Modern GL (&gt;=1.3) isn't a guaranteed
/// libGL export, so we resolve every pointer through the client's exported SDL_GL_GetProcAddress
/// at first draw, when a context is current. All calls are blittable and allocation-free.
/// </summary>
public static unsafe class Gl
{
    // GLenum constants used by the text path.
    public const uint TEXTURE_2D = 0x0DE1, BLEND = 0x0BE2, DEPTH_TEST = 0x0B71;
    public const uint CULL_FACE = 0x0B44, SCISSOR_TEST = 0x0C11;
    public const uint SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303;
    public const uint TRIANGLES = 0x0004, FLOAT = 0x1406, UNSIGNED_BYTE = 0x1401, FALSE = 0;
    public const uint ARRAY_BUFFER = 0x8892, STATIC_DRAW = 0x88E4, DYNAMIC_DRAW = 0x88E8;
    public const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30;
    public const uint COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    public const uint TEXTURE0 = 0x84C0;
    public const uint R8 = 0x8229, RED = 0x1903;
    public const uint TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800;
    public const uint TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803, TEXTURE_WRAP_R = 0x8072;
    public const int  NEAREST = 0x2600, LINEAR = 0x2601, CLAMP_TO_EDGE = 0x812F;
    // Cube map (shadow cubes, #65 slice 6). Faces are POSITIVE_X + 0..5 (+X,-X,+Y,-Y,+Z,-Z).
    public const uint TEXTURE_CUBE_MAP = 0x8513, TEXTURE_CUBE_MAP_POSITIVE_X = 0x8515, TEXTURE_BINDING_CUBE_MAP = 0x8514;
    public const uint TEXTURE_2D_ARRAY = 0x8C1A, TEXTURE_BINDING_2D_ARRAY = 0x8C1D, SAMPLER_BINDING = 0x8919;
    public const uint UNPACK_ALIGNMENT = 0x0CF5;
    // glGetIntegerv pnames for save/restore.
    public const uint VIEWPORT = 0x0BA2, CURRENT_PROGRAM = 0x8B8D, VERTEX_ARRAY_BINDING = 0x85B5;
    public const uint ACTIVE_TEXTURE = 0x84E0, TEXTURE_BINDING_2D = 0x8069, ARRAY_BUFFER_BINDING = 0x8894;
    public const uint SCISSOR_BOX = 0x0C10;
    public const uint FRAMEBUFFER_BINDING = 0x8CA6, DEPTH_WRITEMASK = 0x0B72, DEPTH_FUNC = 0x0B74, DEPTH_RANGE = 0x0B70;
    // FBO / depth-pass (shadow map, #65 slice 3).
    public const uint FRAMEBUFFER = 0x8D40, DEPTH_ATTACHMENT = 0x8D00, FRAMEBUFFER_COMPLETE = 0x8CD5;
    public const uint DEPTH_COMPONENT = 0x1902, DEPTH_COMPONENT24 = 0x81A6, DEPTH_COMPONENT16 = 0x81A5, UNSIGNED_INT = 0x1405;
    public const uint DEPTH_BUFFER_BIT = 0x0100, LEQUAL = 0x0203, NONE = 0;
    public const uint TEXTURE_COMPARE_MODE = 0x884C, TEXTURE_COMPARE_FUNC = 0x884D, COMPARE_REF_TO_TEXTURE = 0x884E;

    public static delegate* unmanaged<uint, int*, void> GetIntegerv;
    public static delegate* unmanaged<uint, byte> IsEnabled;   // GLboolean glIsEnabled(GLenum)
    public static delegate* unmanaged<uint> GetError;          // GLenum glGetError(void)
    public static delegate* unmanaged<uint, void> Enable, Disable, ActiveTexture, UseProgram, BindVertexArray;
    public static delegate* unmanaged<uint, uint, void> BindTexture, BindBuffer, BlendFunc;
    public static delegate* unmanaged<int, uint*, void> GenTextures, GenVertexArrays, GenBuffers;
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    public static delegate* unmanaged<uint, uint, int, void> TexParameteri;
    public static delegate* unmanaged<uint, uint, int*, void> GetTexParameteriv;
    public static delegate* unmanaged<uint, int, void> PixelStorei;
    public static delegate* unmanaged<uint, IntPtr, void*, uint, void> BufferData;
    public static delegate* unmanaged<uint, int, uint, byte, int, void*, void> VertexAttribPointer;
    public static delegate* unmanaged<uint, void> EnableVertexAttribArray;
    public static delegate* unmanaged<uint, int, int, void> DrawArrays;
    public static delegate* unmanaged<int, int, int, int, void> Scissor;   // glScissor(x,y,w,h)
    public static delegate* unmanaged<uint, uint> CreateShader;
    public static delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    public static delegate* unmanaged<uint, void> CompileShader, DeleteShader;
    public static delegate* unmanaged<uint, uint, int*, void> GetShaderiv, GetProgramiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog;
    public static delegate* unmanaged<uint, int, int*, uint*, void> GetAttachedShaders;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderSource;
    /// <summary>glTexStorage3D(target, levels, internalformat, w, h, depth) -- immutable storage (view-able).</summary>
    public static delegate* unmanaged<uint, int, uint, int, int, int, void> TexStorage3D;
    /// <summary>glTextureView(texture, target, origtexture, internalformat, minlevel, numlevels, minlayer, numlayers).</summary>
    public static delegate* unmanaged<uint, uint, uint, uint, uint, uint, uint, uint, void> TextureView;
    public static delegate* unmanaged<int, uint*, void> GenSamplers;
    public static delegate* unmanaged<uint, uint, void> BindSampler;
    public static delegate* unmanaged<uint, uint, int, void> SamplerParameteri;
    public static delegate* unmanaged<uint, int, float*, void> GetUniformfv;
    public static delegate* unmanaged<uint, int, int*, void> GetUniformiv;
    public static delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, int, void*, void> GetTextureSubImage;
    public static delegate* unmanaged<uint> CreateProgram;
    public static delegate* unmanaged<uint, uint, void> AttachShader;
    public static delegate* unmanaged<uint, void> LinkProgram;
    public static delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    public static delegate* unmanaged<int, int, byte, float*, void> UniformMatrix4fv;
    public static delegate* unmanaged<int, int, void> Uniform1i;
    public static delegate* unmanaged<int, float, void> Uniform1f;
    public static delegate* unmanaged<int, float, float, void> Uniform2f;
    public static delegate* unmanaged<int, int, float*, void> Uniform1fv;   // (loc, count, ptr) -- per-slot floats
    public static delegate* unmanaged<int, int, int*, void> Uniform1iv;     // (loc, count, ptr) -- per-slot ints
    public static delegate* unmanaged<int, int, float*, void> Uniform3fv;   // (loc, count, ptr) -- per-slot vec3
    // FBO / depth-pass entry points (#65 slice 3).
    public static delegate* unmanaged<int, uint*, void> GenFramebuffers, DeleteFramebuffers;
    public static delegate* unmanaged<uint, uint, void> BindFramebuffer;
    public static delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    /// <summary>glFramebufferTextureLayer (GL 3.2): target, attachment, texture, level, layer -- attaches one
    /// layer of an array texture (the depth-cube 2D-array render path).</summary>
    public static delegate* unmanaged<uint, uint, uint, int, int, void> FramebufferTextureLayer;
    /// <summary>glTexImage3D: target, level, internalFormat, w, h, depth, border, format, type, data.</summary>
    public static delegate* unmanaged<uint, int, int, int, int, int, int, uint, uint, void*, void> TexImage3D;
    public static delegate* unmanaged<uint, uint> CheckFramebufferStatus;
    public static delegate* unmanaged<uint, void> DrawBuffer, ReadBuffer, DepthFunc, Clear;
    public static delegate* unmanaged<byte, void> DepthMask;
    public static delegate* unmanaged<double, void> ClearDepth;
    public static delegate* unmanaged<double, double, void> DepthRange;
    public static delegate* unmanaged<uint, float*, void> GetFloatv;
    public static delegate* unmanaged<int, int, int, int, void> Viewport;
    public static delegate* unmanaged<int, uint*, void> DeleteTextures;
    /// <summary>glCopyImageSubData (GL 4.3 / ARB_copy_image): srcName, srcTarget, srcLevel, sx, sy, sz,
    /// dstName, dstTarget, dstLevel, dx, dy, dz, w, h, depth. Null on drivers without it -- callers fall back.</summary>
    public static delegate* unmanaged<uint, uint, int, int, int, int, uint, uint, int, int, int, int, int, int, int, void> CopyImageSubData;

    private static delegate* unmanaged<byte*, IntPtr> _getProc;
    private static bool _ready, _failed;

    /// <summary>Resolve every entry point once (context must be current). False = a required proc was
    /// missing; latched so the hot hook doesn't re-resolve or log every frame after a failure.</summary>
    public static bool Init(ExtenderLog log)
    {
        if (_ready) return true;
        if (_failed) return false;
        _getProc = (delegate* unmanaged<byte*, IntPtr>)Native.Sym(SymbolMap.SdlGlGetProcAddress);
        if (_getProc == null) { log.Info("Gl: SDL_GL_GetProcAddress unresolved"); _failed = true; return false; }

        GetIntegerv = (delegate* unmanaged<uint, int*, void>)P("glGetIntegerv");
        IsEnabled = (delegate* unmanaged<uint, byte>)P("glIsEnabled");
        GetError = (delegate* unmanaged<uint>)P("glGetError");
        Enable = (delegate* unmanaged<uint, void>)P("glEnable");
        Disable = (delegate* unmanaged<uint, void>)P("glDisable");
        BlendFunc = (delegate* unmanaged<uint, uint, void>)P("glBlendFunc");
        ActiveTexture = (delegate* unmanaged<uint, void>)P("glActiveTexture");
        UseProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
        BindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray");
        BindTexture = (delegate* unmanaged<uint, uint, void>)P("glBindTexture");
        BindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer");
        GenTextures = (delegate* unmanaged<int, uint*, void>)P("glGenTextures");
        GenVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays");
        GenBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
        TexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage2D");
        TexParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTexParameteri");
        GetTexParameteriv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetTexParameteriv");
        PixelStorei = (delegate* unmanaged<uint, int, void>)P("glPixelStorei");
        BufferData = (delegate* unmanaged<uint, IntPtr, void*, uint, void>)P("glBufferData");
        VertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)P("glVertexAttribPointer");
        EnableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glEnableVertexAttribArray");
        DrawArrays = (delegate* unmanaged<uint, int, int, void>)P("glDrawArrays");
        Scissor = (delegate* unmanaged<int, int, int, int, void>)P("glScissor");
        CreateShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
        ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
        CompileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
        DeleteShader = (delegate* unmanaged<uint, void>)P("glDeleteShader");
        GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
        GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
        GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
        GetAttachedShaders = (delegate* unmanaged<uint, int, int*, uint*, void>)P("glGetAttachedShaders");
        GetShaderSource = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderSource");
        TexStorage3D = (delegate* unmanaged<uint, int, uint, int, int, int, void>)P("glTexStorage3D");
        TextureView = (delegate* unmanaged<uint, uint, uint, uint, uint, uint, uint, uint, void>)P("glTextureView");
        GenSamplers = (delegate* unmanaged<int, uint*, void>)P("glGenSamplers");
        BindSampler = (delegate* unmanaged<uint, uint, void>)P("glBindSampler");
        SamplerParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glSamplerParameteri");
        GetUniformfv = (delegate* unmanaged<uint, int, float*, void>)P("glGetUniformfv");
        GetUniformiv = (delegate* unmanaged<uint, int, int*, void>)P("glGetUniformiv");
        GetTextureSubImage = (delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, int, void*, void>)P("glGetTextureSubImage");
        CreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        AttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        LinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        UniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
        Uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");
        Uniform1f = (delegate* unmanaged<int, float, void>)P("glUniform1f");
        Uniform2f = (delegate* unmanaged<int, float, float, void>)P("glUniform2f");
        Uniform1fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform1fv");
        Uniform1iv = (delegate* unmanaged<int, int, int*, void>)P("glUniform1iv");
        Uniform3fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform3fv");
        GenFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
        DeleteFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteFramebuffers");
        BindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer");
        FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)P("glFramebufferTexture2D");
        FramebufferTextureLayer = (delegate* unmanaged<uint, uint, uint, int, int, void>)P("glFramebufferTextureLayer");
        TexImage3D = (delegate* unmanaged<uint, int, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage3D");
        CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)P("glCheckFramebufferStatus");
        DrawBuffer = (delegate* unmanaged<uint, void>)P("glDrawBuffer");
        ReadBuffer = (delegate* unmanaged<uint, void>)P("glReadBuffer");
        DepthFunc = (delegate* unmanaged<uint, void>)P("glDepthFunc");
        Clear = (delegate* unmanaged<uint, void>)P("glClear");
        DepthMask = (delegate* unmanaged<byte, void>)P("glDepthMask");
        ClearDepth = (delegate* unmanaged<double, void>)P("glClearDepth");
        DepthRange = (delegate* unmanaged<double, double, void>)P("glDepthRange");
        GetFloatv = (delegate* unmanaged<uint, float*, void>)P("glGetFloatv");
        Viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
        DeleteTextures = (delegate* unmanaged<int, uint*, void>)P("glDeleteTextures");
        CopyImageSubData = (delegate* unmanaged<uint, uint, int, int, int, int, uint, uint, int, int, int, int, int, int, int, void>)P("glCopyImageSubData");

        // FBO procs are NOT in this gate: text/chat rendering must not regress if a client lacks them.
        // ShadowMapService null-checks GenFramebuffers/FramebufferTexture2D itself before using the FBO path.
        _ready = CreateProgram != null && GenVertexArrays != null && BufferData != null && DrawArrays != null;
        if (!_ready) { log.Info("Gl: a required GL 3.x proc was missing"); _failed = true; }
        return _ready;
    }

    /// <summary>Resolve one entry point by ASCII name (gl* names are ASCII, so byte-cast is exact).</summary>
    private static IntPtr P(string name)
    {
        Span<byte> b = stackalloc byte[name.Length + 1];
        for (int i = 0; i < name.Length; i++) b[i] = (byte)name[i];
        b[name.Length] = 0;
        fixed (byte* p = b) return _getProc(p);
    }
}
