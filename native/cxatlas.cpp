// The P/Invoke surface over the vendored xatlas (native/xatlas/, MIT): one mesh in, one charted mesh out.
// Built into cx_managed.so / version.dll beside funchook (DirectPInvoke "cxatlas"); the C# side is
// managed/Alloy/MeshChart.cs. Single-threaded on purpose (XA_MULTITHREADED=0): the charter runs one mesh
// per worker call and the result has to be byte-identical across runs, which xatlas's own thread pool is not.
#include "xatlas/xatlas.h"
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

// Static link (DirectPInvoke): plain C linkage, nothing exported from the shipped image.
#define CX_EXPORT extern "C"

// Mirrors CX.Alloy.MeshChart.ChartOut field for field.
struct CxChartResult
{
    void* atlas;             // opaque; cx_chart_free takes it
    const float* uv;         // 2 per output vertex, atlas TEXEL units
    const uint32_t* xref;    // output vertex -> input vertex
    const uint32_t* index;   // output index run (indexes output vertices), indexCount long
    uint32_t vertexCount, indexCount, chartCount, width, height;
};

// 0 ok; 1 bad input; 2 xatlas AddMesh refused; 3 a face ended up in no chart (uncharted triangle);
// 4 the output overflowed 16-bit indexing (Alloy draws everything as GL_UNSIGNED_SHORT).
CX_EXPORT int cx_chart(const float* pos, const float* nrm, const float* uv, uint32_t vertCount,
                       const uint16_t* idx, uint32_t idxCount, float texelsPerUnit, uint32_t padding,
                       CxChartResult* out)
{
    if (!out) return 1;
    memset(out, 0, sizeof(*out));
    if (!pos || !idx || vertCount == 0 || idxCount < 3 || idxCount % 3 != 0) return 1;
    xatlas::Atlas* atlas = xatlas::Create();
    xatlas::MeshDecl decl;
    decl.vertexCount = vertCount;
    decl.vertexPositionData = pos; decl.vertexPositionStride = 12;
    if (nrm) { decl.vertexNormalData = nrm; decl.vertexNormalStride = 12; }
    if (uv) { decl.vertexUvData = uv; decl.vertexUvStride = 8; }
    decl.indexData = idx; decl.indexCount = idxCount; decl.indexFormat = xatlas::IndexFormat::UInt16;
    if (xatlas::AddMesh(atlas, decl) != xatlas::AddMeshError::Success) { xatlas::Destroy(atlas); return 2; }
    xatlas::ChartOptions co;
    xatlas::PackOptions po;
    po.texelsPerUnit = texelsPerUnit;
    po.padding = padding;
    po.bilinear = true;
    po.blockAlign = false;
    po.bruteForce = false;
    xatlas::Generate(atlas, co, po);
    if (atlas->meshCount != 1 || atlas->width == 0 || atlas->height == 0) { xatlas::Destroy(atlas); return 3; }
    const xatlas::Mesh& m = atlas->meshes[0];
    if (m.vertexCount > 65535) { xatlas::Destroy(atlas); return 4; }
    for (uint32_t i = 0; i < m.vertexCount; i++)
        if (m.vertexArray[i].atlasIndex < 0) { xatlas::Destroy(atlas); return 3; }
    // malloc, not new: with XA_MULTITHREADED=0 xatlas needs no libstdc++ and neither does this file.
    float* uvs = (float*)malloc(sizeof(float) * m.vertexCount * 2);
    uint32_t* xref = (uint32_t*)malloc(sizeof(uint32_t) * m.vertexCount);
    for (uint32_t i = 0; i < m.vertexCount; i++)
    {
        uvs[i * 2] = m.vertexArray[i].uv[0]; uvs[i * 2 + 1] = m.vertexArray[i].uv[1];
        xref[i] = m.vertexArray[i].xref;
    }
    out->atlas = atlas; out->uv = uvs; out->xref = xref; out->index = m.indexArray;
    out->vertexCount = m.vertexCount; out->indexCount = m.indexCount; out->chartCount = m.chartCount;
    out->width = atlas->width; out->height = atlas->height;
    return 0;
}

CX_EXPORT void cx_chart_free(CxChartResult* r)
{
    if (!r) return;
    free((void*)r->uv); free((void*)r->xref);
    if (r->atlas) xatlas::Destroy((xatlas::Atlas*)r->atlas);
    memset(r, 0, sizeof(*r));
}
