# Materials for Alloy: the `.mtr` reference

One `.mtr` works for every player. The stock parameters are what vanilla draws. Alloy draws those
too and also reads a set of extra `Cx` parameters that vanilla ignores. You can leave an existing
material alone and it will look better under Alloy anyway, or you can add the `Cx` lines when you
want metal that looks like metal, lacquer, velvet or glowing runes. Design rationale:
[ADR 0026](../adr/0026-modern-material-model.md).

## What Alloy does with a stock material

Nothing you need to change. A spec map (`texture2`) plus a roughness map (`texture3`) is the
workflow to use, and it works the same in vanilla and Alloy. `Specularity`, `Roughness`,
`Metallicness`, `CustomSpecularColor` and the stock texture slots mean exactly what they mean in
vanilla: a roughness of 0.3 gives the same highlight shape in both. Alloy adds light the engine
drops. Rough highlights keep their energy, reflections brighten at grazing angles and fade inside
crevices, shading takes its direction from the surroundings, and highlights no longer sparkle at a
distance.

Metal reflects the scene. The engine treats a spec-map pixel as metal from about s = 0.2 up, fully
metal from s = 0.53 (`metal = 3s - 0.6`), and Alloy reflects its surroundings on those pixels in
proportion, whether or not the material names an environment map. Keep matte surfaces at or below
s = 0.2 and they stay matte. To make metal shinier without making it a mirror, raise its spec value
and control the blur with a roughness map.

A model with no `.mtr`, or one that sets no `Specularity` and binds no spec or roughness map, is
drawn matte in Alloy: fully rough, never metal, and no brighter at low angles than head-on. An environment map on such a model makes it only
mildly glossy, not the mirror vanilla generates. To make it shiny, give it a material.

## The optional Alloy view

The `Cx` parameters below are for people who work in Substance, Blender or another glTF exporter and
want to drop that output in as it is. Nobody has to use them.

These are ordinary `parameter` lines. Vanilla parses them, prints one "failed to link material custom
parameter" line per name when the material loads, and never uses them. Names are case-insensitive.

| line | range | default | what it does |
|---|---|---|---|
| `parameter float CxMetallic 1.0` | 0–1 | 0 | Physical metalness. At 1 there is no diffuse: the colour comes from reflection, tinted by the diffuse texture. |
| `parameter float CxRoughness 0.4` | 0–1 | the stock roughness | Roughness in **glTF/Substance units** (perceptual). `CxRoughness 0.5` equals stock `Roughness 0.25`. |
| `parameter int CxOrm 1` | 0/1 | 0 | `texture11` is a glTF-packed map: **R** occlusion, **G** roughness, **B** metalness. |
| `parameter float CxClearcoat 1.0` | 0–1 | 0 | A clear, glossy layer over the surface: varnish, lacquer, polished stone, wet paint. |
| `parameter float CxClearcoatRoughness 0.15` | 0–1 | 0.15 | The coat's roughness (perceptual). |
| `parameter float CxSheenColor 0.6 0.5 0.7` | 0–1 each | black = off | A soft rim at grazing angles, the look of velvet, silk and wool. Linear colour. |
| `parameter float CxSheenRoughness 0.5` | 0–1 | 0.5 | How wide the sheen rim spreads. |
| `parameter float CxEmissiveStrength 8` | 0–100 | 1 | Multiplies the self-illumination map (`texture5`) so it blooms. |

A material that declares `CxMetallic`, `CxRoughness` or a `CxOrm` map is **physical**. In Alloy its
Cx values win over the stock ones, and it reflects the scene whether or not it has an environment
map. The stock values stay as the vanilla fallback, so set both.

Any roughness below about 0.14 draws as 0.14. A perfect mirror would overflow the HDR target in
direct sunlight.

How the values combine follows glTF: a factor multiplies its channel of the ORM map, a factor with no
map is used as is, a map with no factor is used as is. Two deliberate differences from glTF: no metal
anywhere means a dielectric (glTF would default to full metal), and no roughness anywhere keeps the
stock roughness.

## Example: a steel blade with a leather grip, from a Substance/Blender glTF export

```
customshaderVS vslit_nm
customshaderFS fslit_nm
parameter float Specularity 0.6
parameter float Roughness 0.2
parameter float Metallicness 0.8
parameter int   CxOrm 1
texture0 w_sword_d
texture1 w_sword_n
texture2 w_sword_s
texture11 w_sword_orm
```

Vanilla draws the blade from `Specularity`/`Roughness`/`Metallicness` and the specular map. Alloy
reads metalness and roughness per texel from `w_sword_orm`: the blade is metal and the grip is
leather in one texture, with ambient occlusion from its R channel.

## Example: a velvet cloak and a varnished table

```
parameter float CxSheenColor 0.45 0.1 0.15
parameter float CxSheenRoughness 0.6
```

```
parameter float CxClearcoat 0.8
parameter float CxClearcoatRoughness 0.15
```

## Player settings

`light.brdf` (Modern materials) turns all of this on and off. Off is the stock shading exactly, the
Cx lines included. `light.ambientdir` sets how strongly ambient light takes direction from the scene,
and `light.specaa` turns highlight smoothing on and off. With chat commands on, `/light brdf 0` and
`/light brdf 1` give a quick side-by-side.
