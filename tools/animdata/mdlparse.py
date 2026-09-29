#!/usr/bin/env python3
"""ASCII MDL geometry + animation parser, and forward kinematics over an animation.

Conventions verified against NeverBlender's writer and the stock data:
  * animation `positionkey`/`orientationkey` values are ABSOLUTE LOCAL transforms, not deltas
    (a node with no key keeps its geometry rest transform)
  * orientation is axis-angle (ax ay az radians); a bare `orientation`/`position` line inside an
    animation node is a constant (one key at t=0)
  * no `*bezierkey` controller exists anywhere in the stock or SoW humanoid chains (grepped),
    so every controller here is linear / slerp
  * model units are metres
"""
import math, re

NUMRE = re.compile(r"^[-+0-9.eE]+$")


def _f(t):
    return float(t)


class Node:
    __slots__ = ("type", "name", "parent", "position", "orientation", "poskeys", "orikeys", "scalekeys")

    def __init__(self, type_, name):
        self.type = type_
        self.name = name
        self.parent = None
        self.position = (0.0, 0.0, 0.0)
        self.orientation = (0.0, 0.0, 0.0, 0.0)   # axis-angle
        self.poskeys = []     # [(t, x, y, z)]
        self.orikeys = []     # [(t, ax, ay, az, angle)]
        self.scalekeys = []


class Anim:
    __slots__ = ("name", "length", "transtime", "animroot", "events", "nodes")

    def __init__(self, name):
        self.name = name
        self.length = 0.0
        self.transtime = 0.0
        self.animroot = ""
        self.events = []      # [(t, name)]
        self.nodes = {}       # lowername -> Node


class Model:
    def __init__(self, name):
        self.name = name
        self.supermodel = ""
        self.animationscale = 1.0
        self.classification = ""
        self.nodes = {}       # lowername -> Node  (geometry / rest pose)
        self.order = []
        self.anims = {}       # lowername -> Anim
        self.animorder = []


def _keylist(lines, i, ncols):
    """Read an indented key block. Returns (rows, next_index). Handles the optional count token."""
    rows = []
    while i < len(lines):
        tok = lines[i]
        if not tok:
            i += 1
            continue
        low = tok[0].lower()
        if low == "endlist":
            return rows, i + 1
        if not NUMRE.match(tok[0]):
            return rows, i           # implicit end (next keyword)
        if len(tok) < ncols:
            return rows, i
        rows.append(tuple(_f(x) for x in tok[:ncols]))
        i += 1
    return rows, i


def parse(text):
    """Parse ASCII MDL text into a Model."""
    lines = []
    for raw in text.splitlines():
        s = raw.split("#", 1)[0].strip()
        if s:
            lines.append(s.split())
    m = Model("")
    i = 0
    cur = None          # current Node
    curanim = None
    ingeom = False
    while i < len(lines):
        tok = lines[i]
        k = tok[0].lower()
        if k == "newmodel":
            m.name = tok[1].lower()
        elif k == "setsupermodel":
            m.supermodel = tok[2].lower() if len(tok) > 2 else ""
        elif k == "setanimationscale":
            m.animationscale = _f(tok[1])
        elif k == "classification":
            m.classification = tok[1] if len(tok) > 1 else ""
        elif k == "beginmodelgeom":
            ingeom = True
        elif k == "endmodelgeom":
            ingeom = False
        elif k == "newanim":
            curanim = Anim(tok[1].lower())
            m.anims[curanim.name] = curanim
            m.animorder.append(curanim.name)
        elif k == "doneanim":
            curanim = None
        elif k == "length" and curanim is not None and cur is None:
            curanim.length = _f(tok[1])
        elif k == "transtime" and curanim is not None:
            curanim.transtime = _f(tok[1])
        elif k == "animroot" and curanim is not None:
            curanim.animroot = tok[1].lower()
        elif k == "event" and curanim is not None:
            curanim.events.append((_f(tok[1]), tok[2]))
        elif k == "node":
            cur = Node(tok[1].lower(), tok[2])
            if curanim is not None:
                curanim.nodes[cur.name.lower()] = cur
            else:
                m.nodes[cur.name.lower()] = cur
                m.order.append(cur.name.lower())
        elif k == "endnode":
            cur = None
        elif cur is not None:
            if k == "parent":
                cur.parent = tok[1].lower() if tok[1].lower() != "null" else None
            elif k == "position" and len(tok) >= 4:
                cur.position = tuple(_f(x) for x in tok[1:4])
                if curanim is not None:
                    cur.poskeys = [(0.0,) + cur.position]
            elif k == "orientation" and len(tok) >= 5:
                cur.orientation = tuple(_f(x) for x in tok[1:5])
                if curanim is not None:
                    cur.orikeys = [(0.0,) + cur.orientation]
            elif k in ("positionkey", "positionbezierkey"):
                rows, i2 = _keylist(lines, i + 1, 4)
                cur.poskeys = rows
                i = i2
                continue
            elif k in ("orientationkey", "orientationbezierkey"):
                rows, i2 = _keylist(lines, i + 1, 5)
                cur.orikeys = rows
                i = i2
                continue
            elif k in ("scalekey", "scalebezierkey"):
                rows, i2 = _keylist(lines, i + 1, 2)
                cur.scalekeys = rows
                i = i2
                continue
        i += 1
    return m


# ---------- math ----------

def aa_to_quat(ax, ay, az, ang):
    n = math.sqrt(ax * ax + ay * ay + az * az)
    if n < 1e-9 or abs(ang) < 1e-12:
        return (1.0, 0.0, 0.0, 0.0)
    s = math.sin(ang / 2.0) / n
    return (math.cos(ang / 2.0), ax * s, ay * s, az * s)


def qmul(a, b):
    w1, x1, y1, z1 = a
    w2, x2, y2, z2 = b
    return (w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2,
            w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2,
            w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2,
            w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2)


def qrot(q, v):
    w, x, y, z = q
    vx, vy, vz = v
    # t = 2 * (q_vec x v)
    tx = 2 * (y * vz - z * vy)
    ty = 2 * (z * vx - x * vz)
    tz = 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))


def slerp(a, b, t):
    d = sum(x * y for x, y in zip(a, b))
    if d < 0.0:
        b = tuple(-x for x in b)
        d = -d
    if d > 0.9995:
        r = tuple(a[i] + t * (b[i] - a[i]) for i in range(4))
    else:
        th0 = math.acos(max(-1.0, min(1.0, d)))
        th = th0 * t
        s0 = math.sin(th0)
        r = tuple((math.sin(th0 - th) / s0) * a[i] + (math.sin(th) / s0) * b[i] for i in range(4))
    n = math.sqrt(sum(x * x for x in r)) or 1.0
    return tuple(x / n for x in r)


def sample_pos(keys, t):
    if not keys:
        return None
    if len(keys) == 1 or t <= keys[0][0]:
        return keys[0][1:4]
    if t >= keys[-1][0]:
        return keys[-1][1:4]
    for j in range(len(keys) - 1):
        t0, t1 = keys[j][0], keys[j + 1][0]
        if t0 <= t <= t1:
            f = 0.0 if t1 == t0 else (t - t0) / (t1 - t0)
            return tuple(keys[j][1 + c] + f * (keys[j + 1][1 + c] - keys[j][1 + c]) for c in range(3))
    return keys[-1][1:4]


def sample_ori(keys, t):
    if not keys:
        return None
    if len(keys) == 1 or t <= keys[0][0]:
        return aa_to_quat(*keys[0][1:5])
    if t >= keys[-1][0]:
        return aa_to_quat(*keys[-1][1:5])
    for j in range(len(keys) - 1):
        t0, t1 = keys[j][0], keys[j + 1][0]
        if t0 <= t <= t1:
            f = 0.0 if t1 == t0 else (t - t0) / (t1 - t0)
            return slerp(aa_to_quat(*keys[j][1:5]), aa_to_quat(*keys[j + 1][1:5]), f)
    return aa_to_quat(*keys[-1][1:5])


# ---------- forward kinematics ----------

def pose(geom_model, anim, t, extra_geom=None):
    """World (model-space) transform of every geometry node at animation time t.

    geom_model: the Model whose geometry nodes define the rest skeleton (e.g. pmh0 merged with a_ba)
    anim:       an Anim (its nodes override the rest local transform)
    returns {lowername: (position(3), quat(4))}
    """
    nodes = geom_model.nodes
    out = {}

    def resolve(name):
        if name in out:
            return out[name]
        n = nodes[name]
        an = anim.nodes.get(name)
        p = n.position
        q = aa_to_quat(*n.orientation)
        if an is not None:
            sp = sample_pos(an.poskeys, t)
            if sp is not None:
                p = sp
            sq = sample_ori(an.orikeys, t)
            if sq is not None:
                q = sq
        par = n.parent
        if par is None or par not in nodes or par == name:
            out[name] = (p, q)
        else:
            pp, pq = resolve(par)
            wp = tuple(pp[i] + qrot(pq, p)[i] for i in range(3))
            out[name] = (wp, qmul(pq, q))
        return out[name]

    for nm in nodes:
        resolve(nm)
    return out


def merge_skeleton(models):
    """Rest skeleton: first model that defines a node wins; parents preserved.

    The engine composes the appearance model (pmh0) with its supermodel's bones; taking the
    appearance model first and filling in from the supermodel chain reproduces that.
    """
    m = Model("merged")
    for src in models:
        for nm in src.order:
            if nm not in m.nodes:
                m.nodes[nm] = src.nodes[nm]
                m.order.append(nm)
    return m
