using Sowx.Framework;

namespace Sowx.Graphics;

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
    /// <summary>glGetTexLevelParameteriv queries: the level's internal format, and its alpha channel width.</summary>
    public const uint TEXTURE_INTERNAL_FORMAT = 0x1003, TEXTURE_ALPHA_SIZE = 0x805F;
    /// <summary>#211: the level queries the owned-texture copy sizes itself from -- is this level a block
    /// format, how many bytes does it occupy, and (when it is not) how wide is each channel.</summary>
    public const uint TEXTURE_COMPRESSED = 0x86A1, TEXTURE_COMPRESSED_IMAGE_SIZE = 0x86A0,
                      TEXTURE_RED_SIZE = 0x805C, TEXTURE_GREEN_SIZE = 0x805D, TEXTURE_BLUE_SIZE = 0x805E;
    public const uint TEXTURE_WIDTH = 0x1000, TEXTURE_HEIGHT = 0x1001;
    public const uint CULL_FACE = 0x0B44, SCISSOR_TEST = 0x0C11;
    /// <summary>#292: the face <c>glCullFace</c> discards. BACK is the engine's own default
    /// (<c>Scene::RenderDrawBucket</c> @0x46d060 restores <c>SetCullFace(1)</c> on every exit).</summary>
    public const uint BACK = 0x0405;
    /// <summary>#125: GL_DEPTH_CLAMP. The Alloy sky replays a dome the engine drew under its OWN projection
    /// (far 1000.0) beneath the world's, so it can cross the world far plane; clamping keeps the far-plane
    /// fragments rather than letting them clip into a hole at the horizon.</summary>
    public const uint DEPTH_CLAMP = 0x864F;
    public const uint SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303, ONE_MINUS_SRC_COLOR = 0x0301;
    public const uint COLOR_ATTACHMENT1 = 0x8CE1;
    public const uint POLYGON_OFFSET_FILL = 0x8037;
    public const uint TRIANGLES = 0x0004, FLOAT = 0x1406, UNSIGNED_BYTE = 0x1401, FALSE = 0;
    public const uint BYTE = 0x1400;
    public const uint ARRAY_BUFFER = 0x8892, STATIC_DRAW = 0x88E4, DYNAMIC_DRAW = 0x88E8;
    /// <summary>#212: the two scratch targets glCopyBufferSubData copies between, and the size query that
    /// says how much. Copy targets exist so a buffer can be read and written without disturbing whatever the
    /// caller had bound for drawing.</summary>
    public const uint COPY_READ_BUFFER = 0x8F36, COPY_WRITE_BUFFER = 0x8F37, BUFFER_SIZE = 0x8764;
    public const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30;
    /// <summary>#147: the tessellation pair, and the primitive mode that feeds them. GL 4.0+, so core on the
    /// 4.6 context the Alloy pass already requires -- water is the one class whose geometry is coarse enough
    /// to need subdividing and cheap enough to afford it.</summary>
    public const uint TESS_CONTROL_SHADER = 0x8E88, TESS_EVALUATION_SHADER = 0x8E87;
    public const uint PATCHES = 0x000E, PATCH_VERTICES = 0x8E72, MAX_TESS_GEN_LEVEL = 0x8E7E;
    public const uint COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    /// <summary>GL 4.6 SPIR-V: the one binary format a shader blob may be in, and the enumeration that says
    /// whether this driver accepts it at all.</summary>
    public const uint SHADER_BINARY_FORMAT_SPIR_V = 0x9551, NUM_SHADER_BINARY_FORMATS = 0x8DF9,
                      SHADER_BINARY_FORMATS = 0x8DF8;
    public const uint TEXTURE0 = 0x84C0;
    public const uint R8 = 0x8229, RED = 0x1903;
    /// <summary>glClearBufferfv's buffer selector.</summary>
    public const uint COLOR = 0x1800;
    /// <summary>#243: the hi-Z pyramid's format -- one float per texel, holding the MAX window depth of the
    /// region it covers.</summary>
    public const uint R32F = 0x822E;
    public const uint TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800;
    public const uint TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803, TEXTURE_WRAP_R = 0x8072;
    public const int  NEAREST = 0x2600, LINEAR = 0x2601, CLAMP_TO_EDGE = 0x812F, REPEAT = 0x2901;
    // Cube map (shadow cubes, #65 slice 6). Faces are POSITIVE_X + 0..5 (+X,-X,+Y,-Y,+Z,-Z).
    public const uint TEXTURE_CUBE_MAP = 0x8513, TEXTURE_CUBE_MAP_POSITIVE_X = 0x8515, TEXTURE_BINDING_CUBE_MAP = 0x8514;
    public const uint TEXTURE_2D_ARRAY = 0x8C1A, TEXTURE_BINDING_2D_ARRAY = 0x8C1D, SAMPLER_BINDING = 0x8919;
    /// <summary>Cube map array (GL 4.0) -- the local-light shadow cubes (#95). Layer 6*cube + face, so
    /// <c>glFramebufferTextureLayer</c> addresses one face. <c>TEXTURE_CUBE_MAP_SEAMLESS</c> is a global
    /// enable, not a texture parameter: it makes filtering blend across face borders, which is what removes
    /// the seam the interim cubes fought with an over-scanned face frustum.</summary>
    public const uint TEXTURE_CUBE_MAP_ARRAY = 0x9009, TEXTURE_BINDING_CUBE_MAP_ARRAY = 0x900A,
                      TEXTURE_CUBE_MAP_SEAMLESS = 0x884F;
    public const uint UNPACK_ALIGNMENT = 0x0CF5;
    /// <summary>#265: the BCn transcode's readback alignment and its two S3TC targets (EXT_texture_compression_s3tc,
    /// universally present -- the engine's own DDS path uploads these very enums).</summary>
    public const uint PACK_ALIGNMENT = 0x0D05,
                      COMPRESSED_RGB_S3TC_DXT1 = 0x83F0, COMPRESSED_RGBA_S3TC_DXT5 = 0x83F3;
    /// <summary>#265: the rest of the readback's pixel-store surface -- row shape, and the two PBO bindings
    /// that would silently turn a client pointer into a buffer offset.</summary>
    public const uint PACK_ROW_LENGTH = 0x0D02, PACK_SKIP_ROWS = 0x0D03, PACK_SKIP_PIXELS = 0x0D04,
                      PIXEL_PACK_BUFFER = 0x88EB, PIXEL_UNPACK_BUFFER = 0x88EC,
                      PIXEL_PACK_BUFFER_BINDING = 0x88ED, PIXEL_UNPACK_BUFFER_BINDING = 0x88EF;
    // glGetIntegerv pnames for save/restore.
    public const uint VIEWPORT = 0x0BA2, CURRENT_PROGRAM = 0x8B8D, VERTEX_ARRAY_BINDING = 0x85B5;
    public const uint ACTIVE_TEXTURE = 0x84E0, TEXTURE_BINDING_2D = 0x8069, ARRAY_BUFFER_BINDING = 0x8894;
    public const uint SCISSOR_BOX = 0x0C10;
    public const uint FRAMEBUFFER_BINDING = 0x8CA6, DEPTH_WRITEMASK = 0x0B72, DEPTH_FUNC = 0x0B74, DEPTH_RANGE = 0x0B70;
    // FBO / depth-pass (shadow map, #65 slice 3).
    public const uint FRAMEBUFFER = 0x8D40, DEPTH_ATTACHMENT = 0x8D00, FRAMEBUFFER_COMPLETE = 0x8CD5;
    // Alloy world draw (#81): engine-VAO attribute harvest + engine-FBO mirroring in the 4.6 context.
    public const uint ELEMENT_ARRAY_BUFFER = 0x8893, ELEMENT_ARRAY_BUFFER_BINDING = 0x8895;
    public const uint VERTEX_ATTRIB_ARRAY_ENABLED = 0x8622, VERTEX_ATTRIB_ARRAY_SIZE = 0x8623,
                      VERTEX_ATTRIB_ARRAY_STRIDE = 0x8624, VERTEX_ATTRIB_ARRAY_TYPE = 0x8625,
                      VERTEX_ATTRIB_ARRAY_NORMALIZED = 0x886A, VERTEX_ATTRIB_ARRAY_BUFFER_BINDING = 0x889F,
                      VERTEX_ATTRIB_ARRAY_POINTER = 0x8645;
    public const uint COLOR_ATTACHMENT0 = 0x8CE0, FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE = 0x8CD0,
                      FRAMEBUFFER_ATTACHMENT_OBJECT_NAME = 0x8CD1, RENDERBUFFER = 0x8D41,
                      ATTACHMENT_TEXTURE = 0x1702, FRAMEBUFFER_SRGB = 0x8DB9;
    public const uint DEPTH_COMPONENT = 0x1902, DEPTH_COMPONENT24 = 0x81A6, DEPTH_COMPONENT16 = 0x81A5, UNSIGNED_INT = 0x1405;
    public const uint DEPTH_BUFFER_BIT = 0x0100, COLOR_BUFFER_BIT = 0x4000, LESS = 0x0201, LEQUAL = 0x0203, NONE = 0;
    /// <summary>GL_EQUAL. #364: the highlight mask tests a cutout batch against the depth its own colour
    /// pass wrote with the discard applied, so only the surviving fragments match.</summary>
    public const uint EQUAL = 0x0202;
    /// <summary>#132: the current clear colour, as glGetFloatv reads it back -- the engine's own value, so
    /// the Alloy HDR target starts from what the engine cleared to instead of from a guess.</summary>
    public const uint COLOR_CLEAR_VALUE = 0x0C22;
    /// <summary>#129: the depth-attachment probe the Alloy framebuffer copy sizes itself from. A blit of
    /// DEPTH_BUFFER_BIT needs the two attachments to agree on format, so the copy allocates from what the
    /// engine's own attachment reports rather than assuming 24-bit. DEPTH (0x1801) is the attachment POINT
    /// the query takes for the default framebuffer, where DEPTH_ATTACHMENT is not a legal name.</summary>
    public const uint FRAMEBUFFER_ATTACHMENT_DEPTH_SIZE = 0x8216, FRAMEBUFFER_ATTACHMENT_STENCIL_SIZE = 0x8217,
                      DEPTH = 0x1801, STENCIL = 0x1802, STENCIL_ATTACHMENT = 0x8D20;
    /// <summary>#214: FLOAT vs UNSIGNED_NORMALIZED for an attachment's components. The one thing bit widths
    /// cannot tell apart -- DEPTH_COMPONENT32 and DEPTH_COMPONENT32F both report 32 -- and a depth blit
    /// between the two is INVALID_OPERATION, so the owned depth allocation has to ask.</summary>
    public const uint FRAMEBUFFER_ATTACHMENT_COMPONENT_TYPE = 0x8211, UNSIGNED_NORMALIZED = 0x8C17;
    /// <summary>#214: the engine's own glClearDepth value, read in the engine context beside
    /// COLOR_CLEAR_VALUE. Per-context state, so the owned 4.6 context cannot be asked for it.</summary>
    public const uint DEPTH_CLEAR_VALUE = 0x0B73;
    public const uint DEPTH_COMPONENT32F = 0x8CAC, DEPTH24_STENCIL8 = 0x88F0, DEPTH_STENCIL = 0x84F9,
                      UNSIGNED_INT_24_8 = 0x84FA, DEPTH_STENCIL_ATTACHMENT = 0x821A, STENCIL_BUFFER_BIT = 0x0400,
                      DEPTH_COMPONENT32 = 0x81A7, DEPTH32F_STENCIL8 = 0x8CAD,
                      FLOAT_32_UNSIGNED_INT_24_8_REV = 0x8DAD, UNSIGNED_SHORT = 0x1403,
                      RENDERBUFFER_INTERNAL_FORMAT = 0x8D44;
    /// <summary>#121's IBL probe: a half-float colour cube, mip-filtered by roughness. RGBA16F because the
    /// probe stores LINEAR radiance -- an 8-bit target would band the sky and clip anything a torch overbrights.
    /// <c>TEXTURE_MAX_LEVEL</c> caps the chain glGenerateMipmap builds, which is also the LOD the shader's
    /// roughness maps onto.</summary>
    public const uint RGBA16F = 0x881A, RGBA = 0x1908, HALF_FLOAT = 0x140B, TEXTURE_MAX_LEVEL = 0x813D;
    /// <summary>The unsized base format. Never a legal glTexStorage2D internalformat -- named so that a
    /// driver answering it for TEXTURE_INTERNAL_FORMAT can be refused rather than promoted (#360).</summary>
    public const uint RGB = 0x1907;
    public const int LINEAR_MIPMAP_LINEAR = 0x2703, LINEAR_MIPMAP_NEAREST = 0x2701;
    /// <summary>#243: the hi-Z pyramid's minification filter -- an INTERPOLATED max is not a max.</summary>
    public const int NEAREST_MIPMAP_NEAREST = 0x2700;
    /// <summary>#139's HDR resolve: the split read/draw binding points glBlitFramebuffer works between, and
    /// the level clamps that keep a bloom chain's read level out of the range the draw level is attached in
    /// (sampling a level of a texture another level of which is the current attachment is only defined while
    /// BASE/MAX_LEVEL exclude it).</summary>
    public const uint READ_FRAMEBUFFER = 0x8CA8, DRAW_FRAMEBUFFER = 0x8CA9;
    public const uint TEXTURE_BASE_LEVEL = 0x813C;
    /// <summary>The per-texture component swizzle. Default is the identity (R,G,B,A), but the engine sets it
    /// for its single-channel legacy formats -- so a texture COPIED rather than re-uploaded has to carry
    /// these across or it samples differently from the original it is standing in for (#360).</summary>
    public const uint TEXTURE_SWIZZLE_R = 0x8E42, TEXTURE_SWIZZLE_G = 0x8E43,
                      TEXTURE_SWIZZLE_B = 0x8E44, TEXTURE_SWIZZLE_A = 0x8E45;
    /// <summary>Additive blend factors, for the bloom chain's upsample accumulation.</summary>
    public const uint ZERO = 0, ONE = 1;
    /// <summary>Non-multisample completeness codes, told apart so the HDR target can say WHY it stood down --
    /// a multisampled engine attachment beside a single-sample colour texture is the expected refusal.</summary>
    public const uint FRAMEBUFFER_INCOMPLETE_MULTISAMPLE = 0x8D56;
    /// <summary>#127 grass softening. <c>TEXTURE_MAX_ANISOTROPY</c> is core since 4.6 (same value as the old
    /// EXT), and <c>MAX_TEXTURE_MAX_ANISOTROPY</c> is the driver's ceiling to clamp against.
    /// <c>TEXTURE_LOD_BIAS</c> is sampler state, so it can be set per-draw through a sampler object without
    /// touching the engine's own texture parameters.</summary>
    public const uint TEXTURE_MAX_ANISOTROPY = 0x84FE, MAX_TEXTURE_MAX_ANISOTROPY = 0x84FF,
                      TEXTURE_LOD_BIAS = 0x8501;
    /// <summary>Sample count of the bound draw framebuffer. 1 (or 0) means alpha-to-coverage has nothing to
    /// modulate and the Alloy grass pass falls back to dithering its cutout edge.</summary>
    public const uint SAMPLES = 0x80A9, SAMPLE_BUFFERS = 0x80A8;
    /// <summary>#127: the grass displacement field's format -- RG the signed XY push, B the press-down.</summary>
    public const uint RGBA8 = 0x8058;
    /// <summary>#211: the sized formats an unsized engine upload has to be promoted to before
    /// <see cref="TexStorage2D"/> will take it, and the unsized enums the engine actually uses
    /// (<c>glTexImage2D</c> @0x4cad3d passes GL_RGB/GL_RGBA/GL_RED verbatim).</summary>
    public const uint RGB8 = 0x8051, RG8 = 0x822B, RG = 0x8227, RGB_ = 0x1907, RGBA_ = 0x1908;
    /// <summary>#127: alpha-to-coverage -- the fragment's alpha becomes a COVERAGE MASK over the target's
    /// samples. Each sample is still written whole or not at all, so it is antialiasing without ever
    /// producing a partially transparent depth-writing fragment. Inert where SAMPLES is 1.</summary>
    public const uint SAMPLE_ALPHA_TO_COVERAGE = 0x809E;
    public const uint TEXTURE_COMPARE_MODE = 0x884C, TEXTURE_COMPARE_FUNC = 0x884D, COMPARE_REF_TO_TEXTURE = 0x884E;
    // clustered forward+ on the Alloy path (#83): compute dispatch, SSBOs and the GPU timer for the
    // cluster-build budget. All 4.6-core, so they exist only inside the #80 share-listed context.
    public const uint COMPUTE_SHADER = 0x91B9, SHADER_STORAGE_BUFFER = 0x90D2;
    public const uint SHADER_STORAGE_BARRIER_BIT = 0x2000;
    /// <summary>#243: the two barrier bits the cull dispatch needs after it -- its writes are consumed as
    /// indirect commands and as a parameter-buffer count, neither of which SHADER_STORAGE alone covers.</summary>
    public const uint COMMAND_BARRIER_BIT = 0x0040, BUFFER_UPDATE_BARRIER_BIT = 0x0200;
    /// <summary>Sample counts of an existing attachment: renderbuffer (glGetRenderbufferParameteriv) and
    /// multisample texture (glGetTexLevelParameteriv on TEXTURE_2D_MULTISAMPLE).</summary>
    public const uint NUM_EXTENSIONS = 0x821D, EXTENSIONS = 0x1F03;
    /// <summary>The implementation limits Alloy's stages exceed the GL 4.6 floor on (queried once, logged by name).</summary>
    public const uint MAX_VERTEX_SHADER_STORAGE_BLOCKS = 0x90D6, MAX_SHADER_STORAGE_BUFFER_BINDINGS = 0x90DD,
                      MAX_TEXTURE_IMAGE_UNITS = 0x8872;
    public const uint RENDERBUFFER_SAMPLES = 0x8CAB, TEXTURE_SAMPLES = 0x9106, TEXTURE_2D_MULTISAMPLE = 0x9100, RENDERBUFFER_BINDING = 0x8CA7;
    public const uint TIME_ELAPSED = 0x88BF, QUERY_RESULT = 0x8866, QUERY_RESULT_AVAILABLE = 0x8867, TIMESTAMP = 0x8E28;
    /// <summary>glBeginQuery target counting fragments that passed depth+stencil (#119): the overdraw meter
    /// for the Alloy colour pass. Not ANY_SAMPLES_PASSED -- that one is boolean and answers a different
    /// question.</summary>
    public const uint SAMPLES_PASSED = 0x8914;
    // #93 multidraw-indirect from persistent-mapped storage. The MAP_* bits are glBufferStorage flags AND
    // glMapBufferRange flags: the map's set must be a subset of the storage's or the map fails.
    public const uint DRAW_INDIRECT_BUFFER = 0x8F3F;
    /// <summary>GL_PARAMETER_BUFFER (GL 4.6): where <see cref="MultiDrawElementsIndirectCount"/> reads its
    /// draw count from, so the count never has to reach the CPU.</summary>
    public const uint PARAMETER_BUFFER = 0x80EE;
    public const uint MAP_WRITE_BIT = 0x0002, MAP_PERSISTENT_BIT = 0x0040, MAP_COHERENT_BIT = 0x0080;
    /// <summary>#244: the read side of the same flag set -- for mapping an existing buffer back to the host,
    /// which is a readback and belongs to diagnostics, not to a frame.</summary>
    public const uint MAP_READ_BIT = 0x0001;
    public const uint SYNC_GPU_COMMANDS_COMPLETE = 0x9117, SYNC_FLUSH_COMMANDS_BIT = 0x0001;
    public const uint ALREADY_SIGNALED = 0x911A, CONDITION_SATISFIED = 0x911C;

    public static delegate* unmanaged<uint, int*, void> GetIntegerv;
    public static delegate* unmanaged<uint, byte> IsEnabled;   // GLboolean glIsEnabled(GLenum)
    public static delegate* unmanaged<uint> GetError;          // GLenum glGetError(void)
    public static delegate* unmanaged<uint, void> Enable, Disable, ActiveTexture, UseProgram, BindVertexArray,
                                                  CullFace;
    public static delegate* unmanaged<uint, uint, void> BindTexture, BindBuffer, BlendFunc;
    /// <summary>glBlendFunci (GL 4.0): a blend pair per draw buffer -- the OIT accumulation pair needs two.</summary>
    public static delegate* unmanaged<uint, uint, uint, void> BlendFunci;
    /// <summary>glDrawBuffers: which colour attachments the bound FBO writes.</summary>
    public static delegate* unmanaged<int, uint*, void> DrawBuffers;
    public static delegate* unmanaged<int, uint*, void> GenTextures, GenVertexArrays, GenBuffers, DeleteBuffers;
    /// <summary>#212: glCopyBufferSubData (GL 3.1) -- readTarget, writeTarget, readOffset, writeOffset, size.
    /// The GPU-side buffer copy owned geometry is made of.</summary>
    public static delegate* unmanaged<uint, uint, IntPtr, IntPtr, IntPtr, void> CopyBufferSubData;
    public static delegate* unmanaged<uint, uint, int*, void> GetBufferParameteriv;
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    public static delegate* unmanaged<uint, uint, int, void> TexParameteri;
    public static delegate* unmanaged<uint, uint, int*, void> GetTexParameteriv;
    /// <summary>Per-LEVEL texture state -- the internal format and channel sizes. The one way to ask a
    /// texture whether it actually carries alpha, which no engine-side opacity flag answers reliably.</summary>
    public static delegate* unmanaged<uint, int, uint, int*, void> GetTexLevelParameteriv;
    /// <summary>glGetRenderbufferParameteriv (GL 3.0): target, pname, out. #129 reads
    /// RENDERBUFFER_INTERNAL_FORMAT off the engine's depth attachment -- a depth BLIT requires the two
    /// formats to be identical, and the attachment's bit widths do not distinguish a packed D24S8 from a
    /// plain D24.</summary>
    public static delegate* unmanaged<uint, uint, int*, void> GetRenderbufferParameteriv;
    /// <summary>glGetTexImage: decompresses a level through the driver's own decode path, which is the
    /// only way to ask what the SAMPLER returns rather than what the format declares.</summary>
    public static delegate* unmanaged<uint, int, uint, uint, void*, void> GetTexImage;
    public static delegate* unmanaged<uint, int, void> PixelStorei;
    /// <summary>glCompressedTexImage2D: the #265 transcode's upload -- pre-encoded BCn blocks, allocating form.</summary>
    public static delegate* unmanaged<uint, int, uint, int, int, int, int, void*, void> CompressedTexImage2D;
    /// <summary>glPatchParameteri: how many vertices make one patch. Three -- the Alloy pass feeds the
    /// engine's own triangle index buffer straight through as patches.</summary>
    public static delegate* unmanaged<uint, int, void> PatchParameteri;
    public static delegate* unmanaged<uint, IntPtr, void*, uint, void> BufferData;
    public static delegate* unmanaged<uint, int, uint, byte, int, void*, void> VertexAttribPointer;
    public static delegate* unmanaged<uint, void> EnableVertexAttribArray;
    public static delegate* unmanaged<uint, int, int, void> DrawArrays;
    public static delegate* unmanaged<int, int, int, int, void> Scissor;   // glScissor(x,y,w,h)
    public static delegate* unmanaged<uint, uint> CreateShader;
    public static delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    public static delegate* unmanaged<uint, void> CompileShader, DeleteShader;
    /// <summary>glShaderBinary(count, shaders*, binaryFormat, binary*, length) -- loads a SPIR-V module into
    /// shader objects. Nothing is compiled until <see cref="SpecializeShader"/> names an entry point.</summary>
    public static delegate* unmanaged<int, uint*, uint, void*, int, void> ShaderBinary;
    /// <summary>glSpecializeShader(shader, entryPoint*, numConstants, index*, value*) -- the SPIR-V
    /// counterpart of glCompileShader; COMPILE_STATUS reports its result.</summary>
    public static delegate* unmanaged<uint, byte*, uint, uint*, uint*, void> SpecializeShader;
    public static delegate* unmanaged<uint, uint, int*, void> GetShaderiv, GetProgramiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog;
    public static delegate* unmanaged<uint, int, int*, uint*, void> GetAttachedShaders;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderSource;
    /// <summary>glTexStorage3D(target, levels, internalformat, w, h, depth) -- immutable storage (view-able).</summary>
    public static delegate* unmanaged<uint, int, uint, int, int, int, void> TexStorage3D;
    /// <summary>glTextureView(texture, target, origtexture, internalformat, minlevel, numlevels, minlayer, numlayers).</summary>
    public static delegate* unmanaged<uint, uint, uint, uint, uint, uint, uint, uint, void> TextureView;
    public static delegate* unmanaged<int, uint*, void> GenSamplers;
    /// <summary>glClearBufferfv: clear one attachment of the bound FBO without touching the clear-colour state.</summary>
    public static delegate* unmanaged<uint, int, float*, void> ClearBufferfv;
    public static delegate* unmanaged<uint, uint, void> BindSampler;
    public static delegate* unmanaged<uint, uint, int, void> SamplerParameteri;
    public static delegate* unmanaged<uint, uint, float, void> SamplerParameterf;
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
    public static delegate* unmanaged<int, float, float, float, void> Uniform3f;
    public static delegate* unmanaged<int, float, float, float, float, void> Uniform4f;
    public static delegate* unmanaged<int, int, float*, void> Uniform1fv;   // (loc, count, ptr) -- per-slot floats
    public static delegate* unmanaged<int, int, int*, void> Uniform1iv;     // (loc, count, ptr) -- per-slot ints
    public static delegate* unmanaged<int, int, float*, void> Uniform3fv;   // (loc, count, ptr) -- per-slot vec3
    public static delegate* unmanaged<int, int, float*, void> Uniform4fv;   // (loc, count, ptr) -- per-slot vec4
    // Alloy program cloning (ADR 0007 phase 3): enumerate + mirror engine-program uniforms.
    public static delegate* unmanaged<int, int, float*, void> Uniform2fv;
    public static delegate* unmanaged<int, int, int*, void> Uniform2iv, Uniform3iv, Uniform4iv;
    public static delegate* unmanaged<int, int, byte, float*, void> UniformMatrix2fv, UniformMatrix3fv;
    /// <summary>glGetActiveUniform: prog, index, bufSize, length*, size*, type*, name* -- uniform enumeration.</summary>
    public static delegate* unmanaged<uint, uint, int, int*, int*, uint*, byte*, void> GetActiveUniform;
    // Lighting UBO (ADR 0007 modern path): one std140 block shared by every Alloy program.
    public static delegate* unmanaged<uint, IntPtr, IntPtr, void*, void> BufferSubData;
    /// <summary>glClearBufferSubData (GL 4.3): target, internalformat, offset, size, format, type, data --
    /// a NULL data fills zeros. Server-side, so no client upload into a buffer the GPU may still be reading.</summary>
    public static delegate* unmanaged<uint, uint, IntPtr, IntPtr, uint, uint, void*, void> ClearBufferSubData;
    public const uint R32UI = 0x8236, RED_INTEGER = 0x8D94;
    /// <summary>Zero the first <paramref name="bytes"/> of <paramref name="buf"/> on the GPU. A client-side
    /// BufferSubData into a buffer the previous dispatch may still read is an implicit sync the spec permits
    /// the driver to take as a stall (Khronos, Buffer Object Streaming); a clear has no such term. Unmeasured.</summary>
    public static void ZeroBuffer(uint buf, long bytes)
    {
        BindBuffer(SHADER_STORAGE_BUFFER, buf);
        ClearBufferSubData(SHADER_STORAGE_BUFFER, R32UI, IntPtr.Zero, (IntPtr)bytes, RED_INTEGER, UNSIGNED_INT, null);
        BindBuffer(SHADER_STORAGE_BUFFER, 0);
    }
    public static delegate* unmanaged<uint, uint, uint, void> BindBufferBase;
    public static delegate* unmanaged<uint, byte*, uint> GetUniformBlockIndex;
    public static delegate* unmanaged<uint, uint, uint, void> UniformBlockBinding;
    public static delegate* unmanaged<uint, uint, byte*, void> BindAttribLocation;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetProgramInfoLog;
    // FBO / depth-pass entry points (#65 slice 3).
    public static delegate* unmanaged<int, uint*, void> GenFramebuffers, DeleteFramebuffers;
    public static delegate* unmanaged<uint, uint, void> BindFramebuffer;
    public static delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    /// <summary>glFramebufferTextureLayer (GL 3.2): target, attachment, texture, level, layer -- attaches one
    /// layer of an array texture (the depth-cube 2D-array render path).</summary>
    public static delegate* unmanaged<uint, uint, uint, int, int, void> FramebufferTextureLayer;
    /// <summary>glFramebufferTexture (GL 3.2): target, attachment, texture, level -- attaches EVERY layer of
    /// an array texture at once (a layered attachment), which is what lets a stage route draws by
    /// <c>gl_Layer</c> (#307's layered bake).</summary>
    public static delegate* unmanaged<uint, uint, uint, int, void> FramebufferTexture;
    /// <summary>glTexImage3D: target, level, internalFormat, w, h, depth, border, format, type, data.</summary>
    public static delegate* unmanaged<uint, int, int, int, int, int, int, uint, uint, void*, void> TexImage3D;
    public static delegate* unmanaged<uint, uint> CheckFramebufferStatus;
    /// <summary>glBlitFramebuffer (GL 3.0): srcX0,srcY0,srcX1,srcY1, dstX0,dstY0,dstX1,dstY1, mask, filter.
    /// Colour blits convert format (RGBA8 -&gt; RGBA16F is legal); depth blits do NOT, which is why #139's
    /// HDR target shares the engine's depth attachment rather than copying it.</summary>
    public static delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void> BlitFramebuffer;
    /// <summary>glTextureBarrier (GL 4.5): makes a texture that is also the bound draw target readable, for
    /// the one legal shape -- each fragment reads its own texel once, then writes it.</summary>
    public static delegate* unmanaged<void> TextureBarrier;
    /// <summary>glGenerateMipmap: builds the whole chain from level 0 on the bound texture. The probe's
    /// roughness blur IS this chain (#121).</summary>
    public static delegate* unmanaged<uint, void> GenerateMipmap;
    public static delegate* unmanaged<float, float, float, float, void> ClearColor;
    public static delegate* unmanaged<uint, void> DrawBuffer, ReadBuffer, DepthFunc, Clear;
    /// <summary>glReadPixels: x, y, w, h, format, type, out. Stalls the pipeline -- diagnostic paths only
    /// (#129's /water diag reads the framebuffer copy back once to settle what is actually in it).</summary>
    public static delegate* unmanaged<int, int, int, int, uint, uint, void*, void> ReadPixels;
    public static delegate* unmanaged<byte, void> DepthMask;
    public static delegate* unmanaged<float, float, void> PolygonOffset;
    public static delegate* unmanaged<byte, byte, byte, byte, void> ColorMask;
    public static delegate* unmanaged<double, void> ClearDepth;
    public static delegate* unmanaged<double, double, void> DepthRange;
    public static delegate* unmanaged<uint, float*, void> GetFloatv;
    public static delegate* unmanaged<int, int, int, int, void> Viewport;
    public static delegate* unmanaged<int, uint*, void> DeleteTextures;
    /// <summary>glCopyImageSubData (GL 4.3 / ARB_copy_image): srcName, srcTarget, srcLevel, sx, sy, sz,
    /// dstName, dstTarget, dstLevel, dx, dy, dz, w, h, depth. Null on drivers without it -- callers fall back.</summary>
    public static delegate* unmanaged<uint, uint, int, int, int, int, uint, uint, int, int, int, int, int, int, int, void> CopyImageSubData;
    /// <summary>glTexStorage2D (GL 4.2): target, levels, internalFormat, width, height. Immutable storage --
    /// what <see cref="CopyImageSubData"/> needs on the destination, compressed formats included.</summary>
    public static delegate* unmanaged<uint, int, uint, int, int, void> TexStorage2D;
    /// <summary>glCompressedTexSubImage2D (GL 1.3): target, level, xoffset, yoffset, width, height, format, imageSize, data.</summary>
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, int, void*, void> CompressedTexSubImage2D;
    /// <summary>glMultiDrawElementsBaseVertex (GL 3.2 core): mode, counts*, type, indices**, drawcount,
    /// basevertex*. One call submits a whole batch group (ADR 0006 phase 1). Null on ancient drivers.</summary>
    public static delegate* unmanaged<uint, int*, uint, void**, int, int*, void> MultiDrawElementsBaseVertex;

    // Alloy world draw (#81)
    public static delegate* unmanaged<uint, uint, int*, void> GetVertexAttribiv;
    public static delegate* unmanaged<uint, uint, void**, void> GetVertexAttribPointerv;
    public static delegate* unmanaged<uint, uint, uint, int*, void> GetFramebufferAttachmentParameteriv;
    public static delegate* unmanaged<uint, uint, uint, uint, void> FramebufferRenderbuffer;
    /// <summary>glBindRenderbuffer (GL 3.0): target, name. Only ever used to ask a renderbuffer its own
    /// internal format (#129's depth-copy probe); the binding is restored to 0 immediately.</summary>
    public static delegate* unmanaged<uint, uint, void> BindRenderbuffer;
    /// <summary>glBindTextureUnit (GL 4.5 DSA): unit, texture. One call where ActiveTexture+BindTexture+
    /// ActiveTexture was three -- the Alloy colour pass changes texture state ~4000 times a frame (#151).</summary>
    public static delegate* unmanaged<uint, uint, void> BindTextureUnit;
    /// <summary>ARB_bindless_texture: texture -> 64-bit handle, and residency. Presence of the extension is
    /// checked by string (GLX hands back non-null pointers for unsupported extensions).</summary>
    public static delegate* unmanaged<uint, ulong> GetTextureHandleARB;
    public static delegate* unmanaged<ulong, void> MakeTextureHandleResidentARB;
    /// <summary>#211: the other half of residency. A texture must not be deleted while a handle to it is
    /// resident -- the handle survives the object and a draw that reads it is #220's corruption.</summary>
    public static delegate* unmanaged<ulong, void> MakeTextureHandleNonResidentARB;
    /// <summary>glGetStringi (GL 3.0): pname, index -- the core-profile extension enumeration.</summary>
    public static delegate* unmanaged<uint, uint, byte*> GetStringi;
    public static delegate* unmanaged<int, uint*, void> DeleteVertexArrays;
    public static delegate* unmanaged<uint, void> DeleteProgram;
    // #83 clustered forward+
    public static delegate* unmanaged<uint, uint, uint, void> DispatchCompute;
    public static delegate* unmanaged<uint, void> MemoryBarrier;
    public static delegate* unmanaged<uint, IntPtr, IntPtr, void*, void> GetBufferSubData;
    public static delegate* unmanaged<int, uint*, void> GenQueries;
    public static delegate* unmanaged<uint, uint, void> BeginQuery;
    public static delegate* unmanaged<uint, void> EndQuery;
    public static delegate* unmanaged<uint, uint, void> QueryCounter;
    public static delegate* unmanaged<uint, uint, int*, void> GetQueryObjectiv;
    public static delegate* unmanaged<uint, uint, ulong*, void> GetQueryObjectui64v;
    public static delegate* unmanaged<int, int, int, int, void> Uniform3i;
    // #93 Alloy caster submission. All 4.6-core, so they exist only inside the #80 share-listed context;
    // every one is null-checked before the path arms.
    /// <summary>glBufferStorage (GL 4.4): target, size, data, flags. Immutable store -- the only way to get
    /// a mapping that survives use, which is what "persistent-mapped" means.</summary>
    public static delegate* unmanaged<uint, IntPtr, void*, uint, void> BufferStorage;
    /// <summary>glMapBufferRange (GL 3.0): target, offset, length, access. Returns the client pointer.</summary>
    public static delegate* unmanaged<uint, IntPtr, IntPtr, uint, void*> MapBufferRange;
    /// <summary>glUnmapBuffer (GL 1.5): target. False means the contents were lost while mapped and the
    /// caller must re-read -- which for a read-only diagnostic map means discard, not retry.</summary>
    public static delegate* unmanaged<uint, byte> UnmapBuffer;
    /// <summary>glMultiDrawElementsIndirect (GL 4.3): mode, type, indirect (byte offset into the bound
    /// DRAW_INDIRECT_BUFFER), drawcount, stride (0 = tightly packed).</summary>
    public static delegate* unmanaged<uint, uint, void*, int, int, void> MultiDrawElementsIndirect;
    /// <summary>glMultiDrawElementsIndirectCount (GL 4.6 / ARB_indirect_parameters): mode, type, indirect
    /// (byte offset into the bound DRAW_INDIRECT_BUFFER), drawcount (byte offset into the bound
    /// PARAMETER_BUFFER -- a GPU-side count, which is the whole point), maxdrawcount, stride.
    /// <para>#238: the one entry point a GPU-culled draw list needs and the only one missing. Without it the
    /// count a compute cull produces has to come back to the CPU to be passed as an argument, which is a
    /// stall on the very frame the cull was meant to make cheaper. Bound here ahead of its first caller
    /// because binding it is free and being blocked on it later is not.</para>
    /// <para><b>Non-null proves nothing.</b> GLX hands back a pointer for essentially any name, supported or
    /// not, so the first caller must gate on the extension STRING (GL 4.6 or <c>GL_ARB_indirect_parameters</c>)
    /// the way <c>AlloyRenderer.HasBindless</c> does -- never on a null check. The ARB-suffixed lookup below
    /// is for the platforms where that spelling is the only one, not a capability test.</para></summary>
    public static delegate* unmanaged<uint, uint, void*, IntPtr, int, int, void> MultiDrawElementsIndirectCount;
    /// <summary>glVertexAttribDivisor (GL 3.3): index, divisor. 1 = the attribute advances once per
    /// INSTANCE rather than per vertex, which is what makes an instance stream an instance stream.</summary>
    public static delegate* unmanaged<uint, uint, void> VertexAttribDivisor;
    /// <summary>glDrawArraysInstancedBaseInstance (GL 4.2): mode, first, count, instancecount, baseinstance.
    /// The base-instance form is the one #127 needs: every grass patch is a contiguous run inside ONE
    /// instance buffer, so a patch draws by naming its offset instead of rebinding a pointer. Note
    /// <c>gl_InstanceID</c> does NOT include the base -- divisor-1 vertex attributes do, which is why the
    /// per-blade data is an attribute stream and not an SSBO indexed by the id.</summary>
    public static delegate* unmanaged<uint, int, int, int, uint, void> DrawArraysInstancedBaseInstance;
    public static delegate* unmanaged<uint, uint, IntPtr> FenceSync;
    public static delegate* unmanaged<IntPtr, uint, ulong, uint> ClientWaitSync;
    public static delegate* unmanaged<IntPtr, void> DeleteSync;

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
        GetTexLevelParameteriv = (delegate* unmanaged<uint, int, uint, int*, void>)P("glGetTexLevelParameteriv");
        GetRenderbufferParameteriv =
            (delegate* unmanaged<uint, uint, int*, void>)P("glGetRenderbufferParameteriv");
        GetTexImage = (delegate* unmanaged<uint, int, uint, uint, void*, void>)P("glGetTexImage");
        IsEnabled = (delegate* unmanaged<uint, byte>)P("glIsEnabled");
        GetError = (delegate* unmanaged<uint>)P("glGetError");
        Enable = (delegate* unmanaged<uint, void>)P("glEnable");
        Disable = (delegate* unmanaged<uint, void>)P("glDisable");
        CullFace = (delegate* unmanaged<uint, void>)P("glCullFace");
        BlendFunc = (delegate* unmanaged<uint, uint, void>)P("glBlendFunc");
        BlendFunci = (delegate* unmanaged<uint, uint, uint, void>)P("glBlendFunci");
        DrawBuffers = (delegate* unmanaged<int, uint*, void>)P("glDrawBuffers");
        ActiveTexture = (delegate* unmanaged<uint, void>)P("glActiveTexture");
        UseProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
        BindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray");
        BindTexture = (delegate* unmanaged<uint, uint, void>)P("glBindTexture");
        BindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer");
        GenTextures = (delegate* unmanaged<int, uint*, void>)P("glGenTextures");
        GenVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays");
        GenBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
        DeleteBuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteBuffers");
        CopyBufferSubData = (delegate* unmanaged<uint, uint, IntPtr, IntPtr, IntPtr, void>)P("glCopyBufferSubData");
        GetBufferParameteriv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetBufferParameteriv");
        TexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage2D");
        TexParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTexParameteri");
        GetTexParameteriv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetTexParameteriv");
        PatchParameteri = (delegate* unmanaged<uint, int, void>)P("glPatchParameteri");
        PixelStorei = (delegate* unmanaged<uint, int, void>)P("glPixelStorei");
        CompressedTexImage2D = (delegate* unmanaged<uint, int, uint, int, int, int, int, void*, void>)P("glCompressedTexImage2D");
        BufferData = (delegate* unmanaged<uint, IntPtr, void*, uint, void>)P("glBufferData");
        VertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)P("glVertexAttribPointer");
        EnableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glEnableVertexAttribArray");
        DrawArrays = (delegate* unmanaged<uint, int, int, void>)P("glDrawArrays");
        Scissor = (delegate* unmanaged<int, int, int, int, void>)P("glScissor");
        CreateShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
        ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
        CompileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
        DeleteShader = (delegate* unmanaged<uint, void>)P("glDeleteShader");
        ShaderBinary = (delegate* unmanaged<int, uint*, uint, void*, int, void>)P("glShaderBinary");
        SpecializeShader = (delegate* unmanaged<uint, byte*, uint, uint*, uint*, void>)P("glSpecializeShader");
        GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
        GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
        GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
        GetAttachedShaders = (delegate* unmanaged<uint, int, int*, uint*, void>)P("glGetAttachedShaders");
        GetShaderSource = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderSource");
        TexStorage3D = (delegate* unmanaged<uint, int, uint, int, int, int, void>)P("glTexStorage3D");
        TextureView = (delegate* unmanaged<uint, uint, uint, uint, uint, uint, uint, uint, void>)P("glTextureView");
        GenSamplers = (delegate* unmanaged<int, uint*, void>)P("glGenSamplers");
        ClearBufferfv = (delegate* unmanaged<uint, int, float*, void>)P("glClearBufferfv");
        BindSampler = (delegate* unmanaged<uint, uint, void>)P("glBindSampler");
        SamplerParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glSamplerParameteri");
        SamplerParameterf = (delegate* unmanaged<uint, uint, float, void>)P("glSamplerParameterf");
        GetUniformfv = (delegate* unmanaged<uint, int, float*, void>)P("glGetUniformfv");
        GetUniformiv = (delegate* unmanaged<uint, int, int*, void>)P("glGetUniformiv");
        GetTextureSubImage = (delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, int, void*, void>)P("glGetTextureSubImage");
        CreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        AttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        LinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        UniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
        Uniform2fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform2fv");
        Uniform2iv = (delegate* unmanaged<int, int, int*, void>)P("glUniform2iv");
        Uniform3iv = (delegate* unmanaged<int, int, int*, void>)P("glUniform3iv");
        Uniform4iv = (delegate* unmanaged<int, int, int*, void>)P("glUniform4iv");
        UniformMatrix2fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix2fv");
        UniformMatrix3fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix3fv");
        GetActiveUniform = (delegate* unmanaged<uint, uint, int, int*, int*, uint*, byte*, void>)P("glGetActiveUniform");
        BindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)P("glBindAttribLocation");
        GetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetProgramInfoLog");
        BufferSubData = (delegate* unmanaged<uint, IntPtr, IntPtr, void*, void>)P("glBufferSubData");
        ClearBufferSubData = (delegate* unmanaged<uint, uint, IntPtr, IntPtr, uint, uint, void*, void>)P("glClearBufferSubData");
        BindBufferBase = (delegate* unmanaged<uint, uint, uint, void>)P("glBindBufferBase");
        GetUniformBlockIndex = (delegate* unmanaged<uint, byte*, uint>)P("glGetUniformBlockIndex");
        UniformBlockBinding = (delegate* unmanaged<uint, uint, uint, void>)P("glUniformBlockBinding");
        Uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");
        Uniform1f = (delegate* unmanaged<int, float, void>)P("glUniform1f");
        Uniform2f = (delegate* unmanaged<int, float, float, void>)P("glUniform2f");
        Uniform3f = (delegate* unmanaged<int, float, float, float, void>)P("glUniform3f");
        Uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)P("glUniform4f");
        Uniform1fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform1fv");
        Uniform1iv = (delegate* unmanaged<int, int, int*, void>)P("glUniform1iv");
        Uniform3fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform3fv");
        Uniform4fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform4fv");
        GenFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
        DeleteFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteFramebuffers");
        BindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer");
        FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)P("glFramebufferTexture2D");
        FramebufferTextureLayer = (delegate* unmanaged<uint, uint, uint, int, int, void>)P("glFramebufferTextureLayer");
        FramebufferTexture = (delegate* unmanaged<uint, uint, uint, int, void>)P("glFramebufferTexture");
        TexImage3D = (delegate* unmanaged<uint, int, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage3D");
        CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)P("glCheckFramebufferStatus");
        BlitFramebuffer =
            (delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void>)P("glBlitFramebuffer");
        GenerateMipmap = (delegate* unmanaged<uint, void>)P("glGenerateMipmap");
        TextureBarrier = (delegate* unmanaged<void>)P("glTextureBarrier");
        ClearColor = (delegate* unmanaged<float, float, float, float, void>)P("glClearColor");
        DrawBuffer = (delegate* unmanaged<uint, void>)P("glDrawBuffer");
        ReadBuffer = (delegate* unmanaged<uint, void>)P("glReadBuffer");
        ReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)P("glReadPixels");
        DepthFunc = (delegate* unmanaged<uint, void>)P("glDepthFunc");
        Clear = (delegate* unmanaged<uint, void>)P("glClear");
        DepthMask = (delegate* unmanaged<byte, void>)P("glDepthMask");
        PolygonOffset = (delegate* unmanaged<float, float, void>)P("glPolygonOffset");
        ColorMask = (delegate* unmanaged<byte, byte, byte, byte, void>)P("glColorMask");
        ClearDepth = (delegate* unmanaged<double, void>)P("glClearDepth");
        DepthRange = (delegate* unmanaged<double, double, void>)P("glDepthRange");
        GetFloatv = (delegate* unmanaged<uint, float*, void>)P("glGetFloatv");
        Viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
        DeleteTextures = (delegate* unmanaged<int, uint*, void>)P("glDeleteTextures");
        CopyImageSubData = (delegate* unmanaged<uint, uint, int, int, int, int, uint, uint, int, int, int, int, int, int, int, void>)P("glCopyImageSubData");
        TexStorage2D = (delegate* unmanaged<uint, int, uint, int, int, void>)P("glTexStorage2D");
        CompressedTexSubImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, int, void*, void>)P("glCompressedTexSubImage2D");
        MultiDrawElementsBaseVertex = (delegate* unmanaged<uint, int*, uint, void**, int, int*, void>)P("glMultiDrawElementsBaseVertex");
        GetVertexAttribiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetVertexAttribiv");
        GetVertexAttribPointerv = (delegate* unmanaged<uint, uint, void**, void>)P("glGetVertexAttribPointerv");
        GetFramebufferAttachmentParameteriv = (delegate* unmanaged<uint, uint, uint, int*, void>)P("glGetFramebufferAttachmentParameteriv");
        FramebufferRenderbuffer = (delegate* unmanaged<uint, uint, uint, uint, void>)P("glFramebufferRenderbuffer");
        BindRenderbuffer = (delegate* unmanaged<uint, uint, void>)P("glBindRenderbuffer");
        BindTextureUnit = (delegate* unmanaged<uint, uint, void>)P("glBindTextureUnit");
        GetTextureHandleARB = (delegate* unmanaged<uint, ulong>)P("glGetTextureHandleARB");
        MakeTextureHandleResidentARB = (delegate* unmanaged<ulong, void>)P("glMakeTextureHandleResidentARB");
        MakeTextureHandleNonResidentARB = (delegate* unmanaged<ulong, void>)P("glMakeTextureHandleNonResidentARB");
        GetStringi = (delegate* unmanaged<uint, uint, byte*>)P("glGetStringi");
        DeleteVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glDeleteVertexArrays");
        DeleteProgram = (delegate* unmanaged<uint, void>)P("glDeleteProgram");
        DispatchCompute = (delegate* unmanaged<uint, uint, uint, void>)P("glDispatchCompute");
        MemoryBarrier = (delegate* unmanaged<uint, void>)P("glMemoryBarrier");
        GetBufferSubData = (delegate* unmanaged<uint, IntPtr, IntPtr, void*, void>)P("glGetBufferSubData");
        GenQueries = (delegate* unmanaged<int, uint*, void>)P("glGenQueries");
        BeginQuery = (delegate* unmanaged<uint, uint, void>)P("glBeginQuery");
        EndQuery = (delegate* unmanaged<uint, void>)P("glEndQuery");
        QueryCounter = (delegate* unmanaged<uint, uint, void>)P("glQueryCounter");
        GetQueryObjectiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetQueryObjectiv");
        GetQueryObjectui64v = (delegate* unmanaged<uint, uint, ulong*, void>)P("glGetQueryObjectui64v");
        Uniform3i = (delegate* unmanaged<int, int, int, int, void>)P("glUniform3i");
        BufferStorage = (delegate* unmanaged<uint, IntPtr, void*, uint, void>)P("glBufferStorage");
        MapBufferRange = (delegate* unmanaged<uint, IntPtr, IntPtr, uint, void*>)P("glMapBufferRange");
        UnmapBuffer = (delegate* unmanaged<uint, byte>)P("glUnmapBuffer");
        MultiDrawElementsIndirect = (delegate* unmanaged<uint, uint, void*, int, int, void>)P("glMultiDrawElementsIndirect");
        MultiDrawElementsIndirectCount = (delegate* unmanaged<uint, uint, void*, IntPtr, int, int, void>)P("glMultiDrawElementsIndirectCount");
        // ...and the pre-4.6 spelling, which is the same function under the extension that promoted into core.
        if (MultiDrawElementsIndirectCount == null)
            MultiDrawElementsIndirectCount = (delegate* unmanaged<uint, uint, void*, IntPtr, int, int, void>)P("glMultiDrawElementsIndirectCountARB");
        VertexAttribDivisor = (delegate* unmanaged<uint, uint, void>)P("glVertexAttribDivisor");
        DrawArraysInstancedBaseInstance =
            (delegate* unmanaged<uint, int, int, int, uint, void>)P("glDrawArraysInstancedBaseInstance");
        FenceSync = (delegate* unmanaged<uint, uint, IntPtr>)P("glFenceSync");
        ClientWaitSync = (delegate* unmanaged<IntPtr, uint, ulong, uint>)P("glClientWaitSync");
        DeleteSync = (delegate* unmanaged<IntPtr, void>)P("glDeleteSync");

        // FBO procs are NOT in this gate: text/chat rendering must not regress if a client lacks them.
        // SceneInputs null-checks GenFramebuffers/FramebufferTexture2D itself before using the FBO path.
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
