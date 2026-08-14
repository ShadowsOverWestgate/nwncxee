using Sowx.Framework;

namespace Sowx.Services.Chat.Render;

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
    public const uint TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803;
    public const int  NEAREST = 0x2600, CLAMP_TO_EDGE = 0x812F;
    public const uint UNPACK_ALIGNMENT = 0x0CF5;
    // glGetIntegerv pnames for save/restore.
    public const uint VIEWPORT = 0x0BA2, CURRENT_PROGRAM = 0x8B8D, VERTEX_ARRAY_BINDING = 0x85B5;
    public const uint ACTIVE_TEXTURE = 0x84E0, TEXTURE_BINDING_2D = 0x8069, ARRAY_BUFFER_BINDING = 0x8894;
    public const uint SCISSOR_BOX = 0x0C10;

    public static delegate* unmanaged<uint, int*, void> GetIntegerv;
    public static delegate* unmanaged<uint, byte> IsEnabled;   // GLboolean glIsEnabled(GLenum)
    public static delegate* unmanaged<uint> GetError;          // GLenum glGetError(void)
    public static delegate* unmanaged<uint, void> Enable, Disable, ActiveTexture, UseProgram, BindVertexArray;
    public static delegate* unmanaged<uint, uint, void> BindTexture, BindBuffer, BlendFunc;
    public static delegate* unmanaged<int, uint*, void> GenTextures, GenVertexArrays, GenBuffers;
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    public static delegate* unmanaged<uint, uint, int, void> TexParameteri;
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
    public static delegate* unmanaged<uint> CreateProgram;
    public static delegate* unmanaged<uint, uint, void> AttachShader;
    public static delegate* unmanaged<uint, void> LinkProgram;
    public static delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    public static delegate* unmanaged<int, int, byte, float*, void> UniformMatrix4fv;
    public static delegate* unmanaged<int, int, void> Uniform1i;

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
        CreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        AttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        LinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        UniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
        Uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");

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
