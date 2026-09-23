#define GL_GLEXT_PROTOTYPES
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GL/gl.h>
#include <GL/glext.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
// Cluster-build check: runs the real cx_lightbuild once over 6 lights reaching every froxel, w lanes mixing
// live and cold statics (255), both with and without /light bincap, and checks every froxel lists all 6,
// live ones first, with the live count packed in the high half. NVIDIA, then Mesa (llvmpipe):
//   gcc -O1 tools/lightbench/buildcheck.c -o /tmp/buildcheck -lEGL -lGL
//   __EGL_VENDOR_LIBRARY_FILENAMES=/usr/share/glvnd/egl_vendor.d/10_nvidia.json /tmp/buildcheck managed/Alloy/Shaders/cx_lightbuild.glsl
//   LB_MESA=1 LIBGL_ALWAYS_SOFTWARE=1 __EGL_VENDOR_LIBRARY_FILENAMES=/usr/share/glvnd/egl_vendor.d/50_mesa.json /tmp/buildcheck ...
int main(int argc,char**argv){
  PFNEGLQUERYDEVICESEXTPROC qd=(void*)eglGetProcAddress("eglQueryDevicesEXT"); PFNEGLGETPLATFORMDISPLAYEXTPROC gpd=(void*)eglGetProcAddress("eglGetPlatformDisplayEXT");
  EGLDeviceEXT devs[8]; EGLint nd=0; qd(8,devs,&nd); EGLDisplay d=getenv("LB_MESA")?gpd(0x31DD,EGL_DEFAULT_DISPLAY,0):gpd(EGL_PLATFORM_DEVICE_EXT,devs[0],0); eglInitialize(d,0,0); eglBindAPI(EGL_OPENGL_API);
  EGLint cx[]={EGL_CONTEXT_MAJOR_VERSION,4,EGL_CONTEXT_MINOR_VERSION,6,EGL_CONTEXT_OPENGL_PROFILE_MASK,EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT,EGL_NONE};
  EGLContext c=eglCreateContext(d,EGL_NO_CONFIG_KHR,EGL_NO_CONTEXT,cx); if(!eglMakeCurrent(d,EGL_NO_SURFACE,EGL_NO_SURFACE,c)){fprintf(stderr,"no ctx\n");return 1;}
  FILE*f=fopen(argv[1],"rb"); fseek(f,0,SEEK_END); long L=ftell(f); rewind(f); char*raw=malloc(L+1); fread(raw,1,L,f); raw[L]=0;
  char*src=malloc(L+64), *o=src;   // the build's tokens: $CAP$ 128, $SORT$ 32, $GROUP$ 8
  for(char*r=raw;*r;){ if(!strncmp(r,"$CAP$",5)){o+=sprintf(o,"128");r+=5;} else if(!strncmp(r,"$SORT$",6)){o+=sprintf(o,"32");r+=6;} else if(!strncmp(r,"$GROUP$",7)){o+=sprintf(o,"8");r+=7;} else *o++=*r++; }
  *o=0;
  GLuint p=glCreateShaderProgramv(GL_COMPUTE_SHADER,1,(const char**)&src); GLint ok; glGetProgramiv(p,GL_LINK_STATUS,&ok); if(!ok){char log[8192]; glGetProgramInfoLog(p,8192,0,log); puts(log); return 1;}
  glUseProgram(p);
  enum{GX=4,GY=2,GZ=2,NC=GX*GY*GZ,CAP=128,NLT=6};
  float lw[NLT]={255,3,255,-1,256+5,-2};   // cold, dyn cube, cold, dyn no cube, static w/ cube, ambient
  float lights[NLT*8]; for(int i=0;i<NLT;i++){ float*r=lights+i*8; r[0]=0;r[1]=0;r[2]=-5;r[3]=1000; r[4]=r[5]=r[6]=1; r[7]=lw[i]; }
  GLuint b[4]; glGenBuffers(4,b);
  glBindBuffer(GL_SHADER_STORAGE_BUFFER,b[0]); glBufferData(GL_SHADER_STORAGE_BUFFER,sizeof lights,lights,GL_STATIC_DRAW);
  glBindBuffer(GL_SHADER_STORAGE_BUFFER,b[1]); glBufferData(GL_SHADER_STORAGE_BUFFER,NC*4,0,GL_DYNAMIC_READ);
  glBindBuffer(GL_SHADER_STORAGE_BUFFER,b[2]); glBufferData(GL_SHADER_STORAGE_BUFFER,NC*CAP*4,0,GL_DYNAMIC_READ);
  glBindBuffer(GL_SHADER_STORAGE_BUFFER,b[3]); glBufferData(GL_SHADER_STORAGE_BUFFER,4,0,GL_DYNAMIC_READ);
  for(int i=0;i<4;i++) glBindBufferBase(GL_SHADER_STORAGE_BUFFER,i,b[i]);
  float I[16]={1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1};
  glUniformMatrix4fv(glGetUniformLocation(p,"uView"),1,0,I); glUniform2f(glGetUniformLocation(p,"uProjXY"),1,1);
  glUniform2f(glGetUniformLocation(p,"uZ"),0.1f,300); glUniform3i(glGetUniformLocation(p,"uGrid"),GX,GY,GZ);
  glUniform1i(glGetUniformLocation(p,"uLightCount"),NLT); glUniform1f(glGetUniformLocation(p,"uReach"),1);
  glUniform2f(glGetUniformLocation(p,"uAtten"),1,1);
  int fails=0;
  for(int cap=0; cap<=32; cap+=32){
    glUniform1i(glGetUniformLocation(p,"uBinCap"),cap);
    glDispatchCompute((NC+7)/8,1,1); glMemoryBarrier(GL_ALL_BARRIER_BITS);
    uint32_t cnt[NC], idx[NC*CAP];
    glBindBuffer(GL_SHADER_STORAGE_BUFFER,b[1]); glGetBufferSubData(GL_SHADER_STORAGE_BUFFER,0,sizeof cnt,cnt);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER,b[2]); glGetBufferSubData(GL_SHADER_STORAGE_BUFFER,0,sizeof idx,idx);
    for(int cl=0;cl<NC;cl++){
      uint32_t n=cnt[cl]&0xffff, live=cnt[cl]>>16; int seen=0;
      for(uint32_t k=0;k<n;k++){ uint32_t li=idx[cl*CAP+k]; seen|=1<<li; int cold=lw[li]==255; if(cold!=(k>=live)){ fails++; if(fails<6) printf("  froxel %d slot %u light %u cold %d live %u\n",cl,k,li,cold,live); } }
      if(n!=NLT || live!=4 || seen!=(1<<NLT)-1){ fails++; if(fails<4) printf("  froxel %d n=%u live=%u seen=%x\n",cl,n,live,seen); }
    }
    printf("bincap %d: froxel 0 n=%u live=%u list:",cap,cnt[0]&0xffff,cnt[0]>>16); for(uint32_t k=0;k<(cnt[0]&0xffff);k++) printf(" %u",idx[k]); puts("");
  }
  printf("%s (%s)\n", fails?"FAIL":"ok", glGetString(GL_RENDERER)); return fails!=0; }
