#!/usr/bin/env python3
"""CX-374: offline replay of inc_stars' 'bluish wisp' term
    col += NoiseSimplex2D(1.2*artUv) * vec3(0.2,0,0.2)*0.01 * vPosWorld.z/100
for one snapped camera, correlated against the measured (R-G)/lum chroma map of the same frame.
The matrices below are the ones a one-off uniform dump read off the hosted sky program for the confirming
snap (2026-09-21, 15 Hammer 00:15, camera at (8.3, 76.7, 3.2)); the chroma .npy is (R-G)/lum over sky pixels
of that snap's *_sky.pfm. Result: r = 0.912 at the clock's own rotation -- the measurement behind CX-374's verdict."""
import sys, numpy as np
from PIL import Image

def simplex2d(v):   # transcription of inc_random.shd NoiseSimplex2D, vectorised over (...,2)
    C = np.array([0.211324865405187, 0.366025403784439, -0.577350269189626, 0.024390243902439])
    i = np.floor(v + (v[...,0]+v[...,1])[...,None]*C[1])
    x0 = v - i + (i[...,0]+i[...,1])[...,None]*C[0]
    i1 = np.where((x0[...,0] > x0[...,1])[...,None], np.array([1.,0.]), np.array([0.,1.]))
    x12 = np.concatenate([x0, x0], -1) + np.array([C[0],C[0],C[2],C[2]])
    x12[...,:2] -= i1
    i = np.mod(i, 289.0)
    def permute(x): return np.mod(((x*34.0)+1.0)*x, 289.0)
    p = permute(permute(i[...,1:2] + np.stack([np.zeros_like(i1[...,1]), i1[...,1], np.ones_like(i1[...,1])],-1))
                + i[...,0:1] + np.stack([np.zeros_like(i1[...,0]), i1[...,0], np.ones_like(i1[...,0])],-1))
    m = np.maximum(0.5 - np.stack([(x0*x0).sum(-1), (x12[...,:2]**2).sum(-1), (x12[...,2:]**2).sum(-1)],-1), 0.0)
    m = m*m; m = m*m
    x = 2.0*np.modf(p*C[3])[0] - 1.0
    h = np.abs(x) - 0.5
    ox = np.floor(x + 0.5)
    a0 = x - ox
    m *= 1.79284291400159 - 0.85373472095314*(a0*a0 + h*h)
    g = np.stack([a0[...,0]*x0[...,0] + h[...,0]*x0[...,1],
                  a0[...,1]*x12[...,0] + h[...,1]*x12[...,1],
                  a0[...,2]*x12[...,2] + h[...,2]*x12[...,3]], -1)
    return 130.0*(m*g).sum(-1)

def model(proj, vinv, w, h, theta, step=4):
    P = np.array(proj).reshape(4,4).T; V = np.array(vinv).reshape(4,4).T   # column-major -> row-major
    ys, xs = np.mgrid[0:h:step, 0:w:step]
    ndc_x = (xs+0.5)/w*2-1; ndc_y = 1-(ys+0.5)/h*2
    d = np.stack([ndc_x/P[0,0], ndc_y/P[1,1], -np.ones_like(ndc_x)], -1)
    dirw = d @ V[:3,:3].T
    dirw /= np.linalg.norm(dirw, axis=-1, keepdims=True)
    lat = 0.6109; pole = np.array([0.0, np.cos(lat), np.sin(lat)])
    c, s = np.cos(theta), np.sin(theta)
    ds = dirw*c + np.cross(pole, dirw)*s + pole[None,None,:]*(dirw @ pole)[...,None]*(1-c)
    art = np.stack([(np.arctan2(ds[...,1], ds[...,0])/6.2831853 + 0.5)*10.0,
                    np.arcsin(np.clip(ds[...,2],-1,1))/3.1415926*5.0], -1)
    n = simplex2d(1.2*art)
    # convergence point: where the celestial pole projects (screen px)
    pw = pole @ np.linalg.inv(V[:3,:3]).T   # world -> view
    return n, dirw[...,2], pw

if __name__ == '__main__':
    proj = [1.308648,0,0,0, 0,2.4142134,0,0, 0,0,-1.0001999,-1, 0,0,-0.20002,0]
    vinv = [-0.3016794,-0.9534093,-1.4901156E-08,-0, -0.8682278,0.27472624,0.4131658,0, -0.3939161,0.12464366,-0.91065574,-0, 8.310109,76.722435,3.1943767,1]
    chroma = np.load(sys.argv[1]); h, w = chroma.shape
    step = 4
    meas = chroma[::step, ::step]
    ok = np.isfinite(meas)
    month, day, hour = 1, 15, 0.25
    theta0 = 2*np.pi*(hour/24 + ((month-1)*30 + (day-1))/360)
    best = None
    for theta in np.linspace(0, 2*np.pi, 361):
        n, dz, _ = model(proj, vinv, w, h, theta, step)
        sel = ok & (dz > 0)
        r = np.corrcoef(n[sel], meas[sel])[0,1]
        if best is None or r > best[0]: best = (r, theta)
    n, dz, pw = model(proj, vinv, w, h, best[1], step)
    r0 = np.corrcoef(n[ok&(dz>0)], meas[ok&(dz>0)])[0,1]
    print(f"clock theta {theta0:.3f} rad; best corr {best[0]:.3f} at theta {best[1]:.3f} rad")
    # the celestial pole in view space -> screen px (may be off-screen)
    P = np.array(proj).reshape(4,4).T
    if pw[2] < 0:
        sx = (pw[0]/-pw[2]*P[0,0]+1)/2*w; sy = (1-pw[1]/-pw[2]*P[1,1])/2*h
        print(f"celestial pole projects to screen ({sx:.0f}, {sy:.0f}) px  [frame {w}x{h}]")
    Vv = np.array(vinv).reshape(4,4).T
    zen = np.array([0,0,1.0]) @ np.linalg.inv(Vv[:3,:3]).T
    sx = (zen[0]/-zen[2]*P[0,0]+1)/2*w; sy = (1-zen[1]/-zen[2]*P[1,1])/2*h
    print(f"zenith projects to screen ({sx:.0f}, {sy:.0f}) px")
    img = np.clip((n+1)/2, 0, 1); img[~(dz>0)] = 0.5
    Image.fromarray((img*255).astype(np.uint8)).save('/tmp/x_model.png')
    mm = np.nan_to_num((meas - np.nanpercentile(meas,2))/(np.nanpercentile(meas,98)-np.nanpercentile(meas,2)), nan=0.5)
    Image.fromarray((np.clip(mm,0,1)*255).astype(np.uint8)).save('/tmp/x_meas.png')
