// Early-Z microbench for the OIT append block. Build + run (NVIDIA EGL):
//   gcc -O2 tools/earlyz/bench.c -o /tmp/ezbench -lEGL -lGL && __EGL_VENDOR_LIBRARY_FILENAMES=/usr/share/glvnd/egl_vendor.d/10_nvidia.json /tmp/ezbench 20
// Expect the "with dead CxOitOut SSBO block" variant several times slower: the atomic disables early-Z even down a dead branch.
// Early-Z microbench: prepass depth, then LAYERS full-screen quads at the same depth (LEQUAL, no depth write)
// with a heavy fragment shader. Variant 1 splices a dead (uOit==0) SSBO-append block shaped like CxOitOut.
#include <EGL/egl.h>
#include <EGL/eglext.h>
#define GL_GLEXT_PROTOTYPES
#include <GL/gl.h>
#include <GL/glext.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
static const char* VS = "#version 460 core\nlayout(location=0) in vec2 p; uniform float z; void main(){gl_Position=vec4(p,z,1);}";
static const char* OIT =
"layout(location = 1) out float oReveal;\n"
"uniform int uOit; uniform sampler2D uOitDepth;\n"
"layout(std430, binding = 22) buffer H { uint uOitCount; uint uOitHead[]; };\n"
"layout(std430, binding = 23) writeonly buffer N { uvec4 uOitNode[]; };\n"
"uniform ivec2 uOitDim; uniform float uOitBias; uniform int uOitGob;\n"
"vec4 CxOitOut(vec4 c){ if (uOit == 0) { oReveal = 1.0; return c; }\n"
" float a = clamp(c.a,0.,1.); ivec2 px = ivec2(gl_FragCoord.xy);\n"
" if (uOit == 2){ if (gl_FragCoord.z - uOitBias > texelFetch(uOitDepth, px, 0).r) discard;\n"
"  uint i = atomicAdd(uOitCount, 1u) + 1u; if (i <= uint(uOitDim.y)) { uint prev = atomicExchange(uOitHead[px.y*uOitDim.x+px.x], i);\n"
"  uint key = (uint(clamp(gl_FragCoord.z,0.,1.)*16777215.0) << 8) | uint(uOitGob);\n"
"  uOitNode[i] = uvec4(prev, key, packHalf2x16(c.rg), packHalf2x16(vec2(c.b,a))); } }\n"
" float w = clamp(pow(min(1.0,a*10.0)+0.01,3.0)*1e8*pow(1.0-gl_FragCoord.z*0.9,3.0),1e-2,3e3); oReveal = a; return vec4(c.rgb*a,a)*w; }\n";
static const char* PLAIN = "layout(location = 1) out float oReveal;\nvec4 CxOitOut(vec4 c){ oReveal = 1.0; return c; }\n";
static const char* BODY =
"layout(location=0) out vec4 frag; uniform float uAlphaCut;\n"
"void main(){ vec3 acc = vec3(0); vec2 uv = gl_FragCoord.xy*0.001;\n"
" for (int i=0;i<64;i++){ acc += vec3(sin(uv.x*float(i)+acc.y), cos(uv.y*float(i)-acc.x), sin(acc.x*acc.y)); }\n"
" if (acc.x + 10.0 < uAlphaCut) discard;\n"   // a cutout-shaped discard, as the real lit stage has
" frag = CxOitOut(vec4(acc*0.01, 1.0)); }\n";
static GLuint sh(GLenum t, const char* a, const char* b, const char* c){ GLuint s=glCreateShader(t); const char* src[3]={a,b,c}; glShaderSource(s,3,src,0); glCompileShader(s); GLint ok; glGetShaderiv(s,GL_COMPILE_STATUS,&ok); if(!ok){char l[4096]; glGetShaderInfoLog(s,4096,0,l); fprintf(stderr,"%s\n",l); exit(1);} return s; }
static GLuint prog(const char* fsMid){ GLuint p=glCreateProgram(); glAttachShader(p,sh(GL_VERTEX_SHADER,VS,"","")); glAttachShader(p,sh(GL_FRAGMENT_SHADER,"#version 460 core\n",fsMid,BODY)); glLinkProgram(p); GLint ok; glGetProgramiv(p,GL_LINK_STATUS,&ok); if(!ok){char l[4096]; glGetProgramInfoLog(p,4096,0,l); fprintf(stderr,"%s\n",l); exit(1);} return p; }
int main(int argc,char**argv){
  int W=1920,H=1080,LAYERS=argc>1?atoi(argv[1]):20;
  PFNEGLQUERYDEVICESEXTPROC qd=(void*)eglGetProcAddress("eglQueryDevicesEXT"); PFNEGLGETPLATFORMDISPLAYEXTPROC gpd=(void*)eglGetProcAddress("eglGetPlatformDisplayEXT");
  EGLDeviceEXT devs[8]; EGLint nd=0; qd(8,devs,&nd); EGLDisplay d=gpd(EGL_PLATFORM_DEVICE_EXT,devs[0],0); eglInitialize(d,0,0); eglBindAPI(EGL_OPENGL_API);
  EGLint ca[]={EGL_SURFACE_TYPE,EGL_PBUFFER_BIT,EGL_RENDERABLE_TYPE,EGL_OPENGL_BIT,EGL_NONE}; EGLConfig cfg; EGLint n; eglChooseConfig(d,ca,&cfg,1,&n);
  EGLint cx[]={EGL_CONTEXT_MAJOR_VERSION,4,EGL_CONTEXT_MINOR_VERSION,6,EGL_CONTEXT_OPENGL_PROFILE_MASK,EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT,EGL_NONE};
  EGLContext c=eglCreateContext(d,cfg,EGL_NO_CONTEXT,cx); EGLint pa[]={EGL_WIDTH,8,EGL_HEIGHT,8,EGL_NONE}; EGLSurface sf=eglCreatePbufferSurface(d,cfg,pa); if(!eglMakeCurrent(d,sf,sf,c)){fprintf(stderr,"no ctx\n");return 1;}
  printf("%s\n",glGetString(GL_RENDERER));
  GLuint fbo,col,rev,dep; glGenFramebuffers(1,&fbo); glBindFramebuffer(GL_FRAMEBUFFER,fbo);
  glGenTextures(1,&col); glBindTexture(GL_TEXTURE_2D,col); glTexStorage2D(GL_TEXTURE_2D,1,GL_RGBA16F,W,H); glFramebufferTexture2D(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,col,0);
  glGenTextures(1,&rev); glBindTexture(GL_TEXTURE_2D,rev); glTexStorage2D(GL_TEXTURE_2D,1,GL_R16F,W,H); glFramebufferTexture2D(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT1,GL_TEXTURE_2D,rev,0);
  glGenTextures(1,&dep); glBindTexture(GL_TEXTURE_2D,dep); glTexStorage2D(GL_TEXTURE_2D,1,GL_DEPTH_COMPONENT32F,W,H); glFramebufferTexture2D(GL_FRAMEBUFFER,GL_DEPTH_ATTACHMENT,GL_TEXTURE_2D,dep,0);
  GLenum bufs[2]={GL_COLOR_ATTACHMENT0,GL_COLOR_ATTACHMENT1}; glDrawBuffers(2,bufs);
  if(glCheckFramebufferStatus(GL_FRAMEBUFFER)!=GL_FRAMEBUFFER_COMPLETE){fprintf(stderr,"fbo\n");return 1;}
  GLuint ssbo[2]; glGenBuffers(2,ssbo); for(int i=0;i<2;i++){glBindBuffer(GL_SHADER_STORAGE_BUFFER,ssbo[i]); glBufferData(GL_SHADER_STORAGE_BUFFER,1<<20,0,GL_DYNAMIC_DRAW); glBindBufferBase(GL_SHADER_STORAGE_BUFFER,22+i,ssbo[i]);}
  float q[]={-1,-1,1,-1,-1,1,1,1}; GLuint vao,vbo; glGenVertexArrays(1,&vao); glBindVertexArray(vao); glGenBuffers(1,&vbo); glBindBuffer(GL_ARRAY_BUFFER,vbo); glBufferData(GL_ARRAY_BUFFER,sizeof q,q,GL_STATIC_DRAW); glEnableVertexAttribArray(0); glVertexAttribPointer(0,2,GL_FLOAT,0,0,0);
  glViewport(0,0,W,H); glEnable(GL_DEPTH_TEST);
  const char* names[2]={"plain (no SSBO code)","with dead CxOitOut SSBO block"}; const char* mids[2]={PLAIN,OIT};
  for(int v=0;v<2;v++){
    GLuint p=prog(mids[v]); glUseProgram(p); glUniform1i(glGetUniformLocation(p,"uOit"),0); glUniform1f(glGetUniformLocation(p,"uAlphaCut"),-1.0f);
    GLuint qry; glGenQueries(1,&qry);
    for(int rep=0;rep<3;rep++){
      glDepthMask(1); glDepthFunc(GL_LESS); glClearDepth(1.0); glClear(GL_DEPTH_BUFFER_BIT|GL_COLOR_BUFFER_BIT);
      // prepass: depth 0.5 everywhere (cheap: colour masked)
      glColorMask(0,0,0,0); glUniform1f(glGetUniformLocation(p,"z"),0.5f); glDrawArrays(GL_TRIANGLE_STRIP,0,4); glColorMask(1,1,1,1);
      glDepthMask(0); glDepthFunc(GL_LEQUAL);
      glBeginQuery(GL_TIME_ELAPSED,qry);
      glUniform1f(glGetUniformLocation(p,"z"),0.5f); glDrawArrays(GL_TRIANGLE_STRIP,0,4);       // the visible layer: shades once
      glUniform1f(glGetUniformLocation(p,"z"),0.6f); for(int i=0;i<LAYERS;i++) glDrawArrays(GL_TRIANGLE_STRIP,0,4); // behind: early-Z should reject all
      glEndQuery(GL_TIME_ELAPSED); GLuint64 ns; glGetQueryObjectui64v(qry,GL_QUERY_RESULT,&ns);
      printf("%-36s rep %d: %.2f ms (1 visible + %d occluded layers)\n",names[v],rep,ns/1e6,LAYERS);
    }
  }
  return 0; }
