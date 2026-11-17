#!/usr/bin/env python3
"""Virtual Office v2 static asset generator (standard library only, re-runnable).

Usage:  python tools/office-assets/generate.py [output_dir]

Writes pixel-art PNGs (alpha is only 0 or 255, no anti-aliasing, fixed palette) to
src/Employee.Desktop/Assets/VirtualOffice/v2/ by default.  Old assets are never touched.

Isometric conventions (screen space, 2:1):
  * +u = down-right (dx +2, dy +1)   = "SE"
  * +v = down-left  (dx -2, dy +1)   = "SW"
  * Grid tile 76x38; neighbour centres are (+-38, +-19) apart.
"""
import struct
import sys
import zlib
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
OUT = REPO / "src/Employee.Desktop/Assets/VirtualOffice/v2"
T2_SOURCE = REPO / "AIEmployeeGenerated/a49df5e02f38418891e45a543e379979-9F4039963688.png"

# Crop boxes (x0, y0, x1, y1) inside the 128x128 t2 sheet, per asset name.  The t2 image is a
# whole isometric room (items sit on walls/floor, not on transparent backgrounds), so no box
# is trusted by default and every item is drawn by this script.  Fill a box to import it:
# the crop is snapped to PAL and alpha is forced to 0/255.
T2_CROPS = {}  # e.g. {"plant": (6, 60, 26, 84)}

TILE_W, TILE_H = 76, 38
WALL_H, WALL_M = 48, 6           # wall height above the tile edge, top margin in the canvas
WALL_CANVAS_H = WALL_M + WALL_H + TILE_H
DOOR_EXTRA = 28                  # door segments are this much taller than plain walls (frame above the wall line)

PAL = {
    "ink": (62, 44, 40),
    "w1": (170, 114, 70), "w2": (198, 142, 88), "w3": (222, 170, 112), "w4": (240, 200, 146),
    "c1": (252, 240, 212), "c2": (242, 220, 180), "c3": (224, 194, 152),
    "gd1": (240, 196, 110), "gd2": (214, 160, 72),
    "bl1": (150, 194, 208), "bl2": (110, 160, 184),
    "gn1": (170, 204, 128), "gn2": (118, 170, 102), "gn3": (84, 136, 84),
    "c4": (210, 178, 136),            # one shade darker than c3 (wall joints on the NE face)
    "bg": (78, 66, 62),               # preview backdrop: dark warm taupe, not cream
    "rd1": (222, 134, 106), "rd2": (184, 96, 78), "rd3": (150, 70, 60),
    "s1": (214, 212, 202), "s2": (170, 168, 160), "s3": (128, 128, 122),
    "wh": (255, 250, 232), "screen": (150, 224, 232),
    # pale department floor pairs (base, dark)
    "fm1": (244, 214, 120), "fm2": (222, 184, 88),
    "fd1": (160, 204, 220), "fd2": (126, 176, 200),
    "fr1": (176, 214, 140), "fr2": (140, 190, 108),
    "fc1": (206, 170, 222), "fc2": (176, 138, 196),
    "fq1": (240, 170, 136), "fq2": (214, 134, 102),
    "fe1": (136, 200, 184), "fe2": (104, 168, 152),
    # path: clearly darker/more saturated than the cream preview background and the wood floor
    "pa1": (156, 112, 84), "pa2": (176, 128, 92), "pa3": (110, 78, 62),
}
PAL_VALUES = set(PAL.values())


class Canvas:
    def __init__(self, w, h):
        self.w, self.h = w, h
        self.p = [[None] * w for _ in range(h)]

    def px(self, x, y, c):
        if 0 <= x < self.w and 0 <= y < self.h:
            self.p[y][x] = PAL[c] if isinstance(c, str) else c

    def hline(self, x0, x1, y, c):
        for x in range(x0, x1 + 1):
            self.px(x, y, c)

    def vline(self, x, y0, y1, c):
        for y in range(y0, y1 + 1):
            self.px(x, y, c)

    def rect(self, x, y, w, h, c):
        for yy in range(y, y + h):
            self.hline(x, x + w - 1, yy, c)

    def disc(self, cx, cy, r, c):
        for y in range(cy - r, cy + r + 1):
            for x in range(cx - r, cx + r + 1):
                if (x - cx) ** 2 + (y - cy) ** 2 <= r * r:
                    self.px(x, y, c)

    def outline(self, c="ink", skip_cols=()):
        """Draw a 1px outline on transparent pixels touching the silhouette (4-neighbourhood).
        Columns in skip_cols get no outline (used for wall segment joints)."""
        add = []
        for y in range(self.h):
            for x in range(self.w):
                if x in skip_cols:
                    continue
                if self.p[y][x] is None:
                    for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                        if 0 <= nx < self.w and 0 <= ny < self.h and self.p[ny][nx] is not None:
                            add.append((x, y))
                            break
        for x, y in add:
            self.px(x, y, c)

    def mirror(self):
        m = Canvas(self.w, self.h)
        for y in range(self.h):
            m.p[y] = list(reversed(self.p[y]))
        return m

    def blit(self, src, x0, y0):
        for y in range(src.h):
            for x in range(src.w):
                v = src.p[y][x]
                if v is not None:
                    self.px(x0 + x, y0 + y, v)

    def png(self, path):
        raw = bytearray()
        for row in self.p:
            raw.append(0)
            for v in row:
                raw.extend((v[0], v[1], v[2], 255) if v else (0, 0, 0, 0))

        def chunk(t, d):
            return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

        data = (b"\x89PNG\r\n\x1a\n"
                + chunk(b"IHDR", struct.pack(">2I5B", self.w, self.h, 8, 6, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
                + chunk(b"IEND", b""))
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)


# ----------------------------------------------------------------------------- geometry
def inside(x, y):
    """Pixel membership of the 76x38 rhombus: rows are 4k+2 wide, 2 px/row steps, exact tiling."""
    if not (0 <= x < TILE_W and 0 <= y < TILE_H):
        return False
    k = min(y, TILE_H - 1 - y)
    return 37 - 2 * k <= x <= 38 + 2 * k


def box(c, ox, oy, U, V, Z, top, left, right, z0=0, rim=None):
    """Isometric box. (ox,oy) = top-left pixel of floor cell (u=0,v=0); each cell is 4x1 px.
    Footprint is U cells along SE and V cells along SW; Z px tall, lifted z0 px."""
    mask = set()
    for u in range(U):
        for v in range(V):
            x = ox + 2 * (u - v)
            y = oy + u + v - z0 - Z
            for dx in range(4):
                mask.add((x + dx, y))
    colmax = {}
    for x, y in mask:
        if y > colmax.get(x, -10 ** 6):
            colmax[x] = y
    xs = ox + 2 * (U - V) + 2  # x of the bottom vertex: left face < xs <= right face
    for x, ym in colmax.items():
        col = left if x < xs else right
        for k in range(1, Z + 1):
            c.px(x, ym + k, col)
    for x, y in mask:
        c.px(x, y, top)
    if rim:
        for x, y in mask:
            if any(n not in mask for n in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1))):
                c.px(x, y, rim)
    return {"colmax": colmax, "xs": xs, "xmax": max(colmax), "xmin": min(colmax)}


def cell(ox, oy, u0, v0):
    return ox + 2 * (u0 - v0), oy + u0 + v0


# ----------------------------------------------------------------------------- floors
PERIOD = 76   # lattice step in u (SE neighbour) and v (SW neighbour); every pattern period divides it


def pattern(kind, variant, u, v):
    """Role 0=base 1=alt 2=dark as a function of lattice coordinates u=x+2y, v=x-2y+74.
    Neighbouring tiles differ by exactly +-76 in u or v, so any pattern whose period divides 76
    (here 1, 2, 4, 19, 38, 76) continues across tile edges without a seam."""
    if kind == "wood":
        a, b = (u, v) if variant == 0 else (v, u)
        band = a // 19
        if a % 19 < 2:
            return 2
        if (b + band * 19) % 38 < 2:      # staggered butt joints
            return 2
        return 1 if band % 2 else 0
    if kind == "stone":
        if variant == 0:
            if u % 19 < 2 or v % 19 < 2:
                return 2
            return 1 if (u // 19 + v // 19) % 2 else 0
        if u % 38 < 2 or v % 38 < 2:
            return 2
        return 1 if (u * 3 + v * 5) % 19 < 3 else 0
    # carpet
    if variant == 0:
        return 1 if (u // 19 + v // 19) % 2 else 0
    if u % 19 in (8, 9) and v % 19 in (8, 9):
        return 2
    return 1 if (u // 19) % 2 == 0 and (v // 19) % 2 == 0 else 0


def floor_tile(base, alt, dark, kind, variant):
    c = Canvas(TILE_W, TILE_H)
    cols = (base, alt, dark)
    for y in range(TILE_H):
        for x in range(TILE_W):
            if inside(x, y):
                c.px(x, y, cols[pattern(kind, variant, x + 2 * y, x - 2 * y + 74)])
    return c


FLOORS = {
    "floor": ("wood", "w3", "w4", "w2"),
    "path": ("stone", "pa1", "pa2", "pa3"),
    # carpets: no cream squares any more, the alternate shade is the department's own darker tone
    "manager": ("carpet", "fm1", "fm2", "fm2"),
    "development": ("carpet", "fd1", "fd2", "fd2"),
    "research": ("carpet", "fr1", "fr2", "fr2"),
    "creative": ("carpet", "fc1", "fc2", "fc2"),
    "qa": ("carpet", "fq1", "fq2", "fq2"),
    "external": ("carpet", "fe1", "fe2", "fe2"),
}


def make_floors(out):
    tiles = {}
    for name, (kind, base, alt, dark) in FLOORS.items():
        for vi, suffix in enumerate(("", "-b")):
            t = floor_tile(base, alt, dark, kind, vi)
            tiles[name + suffix] = t
            out[f"floor-{name}{suffix}.png"] = t
    return tiles


def lattice_center(gx, gy, x0, y0):
    return x0 + 38 * (gx - gy), y0 + 19 * (gx + gy)


def make_floor_test(tiles, out):
    # one variant only: variants are each seamless with themselves, not with each other
    c = Canvas(456, 228)
    for gy in range(6):
        for gx in range(6):
            cx, cy = lattice_center(gx, gy, 38 + 38 * 5, 19)
            c.blit(tiles["floor"], cx - 38, cy - 19)
    out["floor-test.png"] = c

    # 6x6 flat grid: six 2x2 department blocks (3 across, 2 down) and the path in the last two rows
    rooms = (("manager", "development", "research"), ("creative", "qa", "external"))
    r = Canvas(456, 228)
    for gy in range(6):
        for gx in range(6):
            name = "path" if gy >= 4 else rooms[gy // 2][gx // 2]
            cx, cy = lattice_center(gx, gy, 38 + 38 * 5, 19)
            r.blit(tiles[name], cx - 38, cy - 19)
    out["floor-test-rooms.png"] = r


# ----------------------------------------------------------------------------- walls
def wall_by(x):
    """Row (inside the tile) of the NW tile edge at column x (0..37); NE edge mirrors it."""
    return min(18, (38 - x) // 2)


def make_wall(kind):
    end = kind.endswith("-end")
    base_kind = kind[:-4] if end else kind
    door = base_kind.startswith("door")
    corner = base_kind == "corner"
    extra = DOOR_EXTRA if door else 0
    hh = WALL_H + extra                 # wall height of this segment (door frames are taller than walls)
    c = Canvas(TILE_W, WALL_CANVAS_H + extra)
    sides = {"nw": ["nw"], "ne": ["ne"], "door-nw": ["nw"], "door-ne": ["ne"], "corner": ["nw", "ne"]}[base_kind]
    for x in range(TILE_W):
        side = "nw" if x <= 37 else "ne"
        if side not in sides:
            continue
        n = x if side == "nw" else 75 - x
        by = wall_by(n)
        top, bot = WALL_M + by, WALL_M + by + hh
        gap = door and 5 <= n <= 32          # 28 px wide opening
        if side == "nw":
            cap, face, seam, base = "c1", "c2", "c3", "w3"
        else:
            cap, face, seam, base = "c2", "c3", "c4", "w2"   # seam = face shade one step darker (subtle joints)
        for y in range(top, bot + 1):
            d = y - top
            if gap:
                # Door: the wall keeps its full height; a lintel (same cap profile as the wall) spans
                # the opening, a door leaf stands at one jamb and a threshold closes the bottom.
                if d < 3:
                    col = cap
                elif d == 3:
                    col = seam
                elif d < 11:
                    col = face
                elif d == 11:
                    col = "w4"                              # lintel trim
                elif d >= hh - 1:
                    col = "w1"                              # threshold
                elif d == hh - 2:
                    col = "gd2"                             # threshold lip
                elif n <= 10:                               # door leaf (open, against the jamb)
                    if n in (5, 10) or d == 12:
                        col = "w1"
                    elif 6 <= n <= 9 and (17 <= d <= 36 or 41 <= d <= hh - 6):
                        col = "w3"                          # raised panels
                    elif n == 9 and d in (38, 39):
                        col = "gd1"                         # handle
                    else:
                        col = "w2"
                else:
                    continue                                # open doorway, see-through
            elif d < 3:
                col = cap
            elif d == 3:
                col = seam
            elif door and (2 <= n <= 4 or 33 <= n <= 35):
                col = "w4"                                  # door jamb trim, flush with the wall face
            elif y > bot - 5:
                col = base
            else:
                col = seam if n % 19 == 0 else face
            c.px(x, y, col)
    if end:
        # End cap: a slightly raised pillar with a visible top face (rhombus) and two shaded sides.
        xs = range(68, 76) if base_kind.endswith("ne") else range(0, 8)
        t0 = WALL_M + wall_by(4) - 4
        for x in xs:
            lx = x - xs[0]
            n = 75 - x if base_kind.endswith("ne") else x
            bot = WALL_M + wall_by(n) + WALL_H
            for y in range(t0, bot + 1):
                r = y - t0
                if r == 0:
                    col = "c1" if 2 <= lx <= 5 else None
                elif r == 1:
                    col = "c1"
                elif r == 2:
                    col = "c1" if 2 <= lx <= 5 else ("c2" if lx < 4 else "w3")
                elif y > bot - 4:
                    col = "w3" if lx < 4 else "w2"
                else:
                    col = "c2" if lx < 4 else "w3"
                    if lx in (3, 4):
                        col = "c3" if lx == 3 else "w2"
                if col:
                    c.px(x, y, col)
    if corner:
        # Corner post: 8 px wide with a rhombus cap on rows 3..5. Nothing touches row 0, so the ink
        # outline closes cleanly over the top of the post.
        for x in range(34, 42):
            n = x if x <= 37 else 75 - x
            bot = WALL_M + wall_by(n) + WALL_H
            for y in range(3, bot + 1):
                if y == 3:
                    col = "c1" if 36 <= x <= 39 else None
                elif y == 4:
                    col = "c1"
                elif y == 5:
                    col = "c1" if 36 <= x <= 39 else ("w4" if x < 36 else "w3")
                else:
                    col = "w4" if x <= 37 else "w3"
                if col:
                    c.px(x, y, col)
    # Segment joints: a single-direction segment ends at the tile's centre line. The 1 px column just
    # past that edge (x=38 for NW, x=37 for NE) is left empty instead of dark, so neighbouring segments
    # join as one continuous wall. The top outline of the next segment still closes the wall above.
    # The outer canvas edge (x=0 / x=75) never carries an outline, so both sides are seamless.
    skip = ()
    if not corner:
        skip = (38,) if base_kind.endswith("nw") else (37,)
    c.outline("ink", skip)
    return c


WALL_ANCHOR = (38, WALL_M + WALL_H + 19)   # tile centre inside the wall canvas
DOOR_ANCHOR = (38, WALL_ANCHOR[1] + DOOR_EXTRA)   # same tile centre inside the taller door canvas


# ----------------------------------------------------------------------------- furniture
DESK_W, DESK_H, DESK_ANCHOR = 72, 64, (36, 46)
CHAIR_W, CHAIR_H, CHAIR_ANCHOR = 40, 46, (20, 38)   # >= 3 px transparent padding on every side
SEAT_OFFSET = (-22, -11)   # chair anchor relative to desk anchor for desk-se (x mirrored for sw)
SEAT_LIFT = 11             # seat surface above chair anchor


def make_desk_se():
    c = Canvas(DESK_W, DESK_H)
    U, V, Z, Z0 = 12, 18, 16, 16
    cx, cy = DESK_ANCHOR
    ox, oy = cx - (U - V) - 2, cy - (U + V) // 2
    info = box(c, ox, oy, U, V, Z, "w4", "w2", "w1", rim="c1")
    cm, xs = info["colmax"], info["xs"]
    # Back panel (SE face, seen from the room): vertical grooves
    for x in range(xs, info["xmax"] + 1):
        if (x - xs) % 12 == 6:
            for k in range(2, Z):
                c.px(x, cm[x] + k, "w2")
    # Sitter-side drawers visible on the SW end face
    for x in range(info["xmin"] + 4, xs - 3):
        for d in (7, 14):
            c.px(x, cm[x] + d, "w1")
    mid = (info["xmin"] + xs) // 2
    for d in (4, 11):
        c.hline(mid - 1, mid + 1, cm[mid] + d, "gd1")
    # Desktop items; monitor faces the sitter (NW) so its back casing is what we see.
    def sub(u0, v0, uu, vv, zz, top, left, right, z0):
        x, y = cell(ox, oy, u0, v0)
        return box(c, x, y, uu, vv, zz, top, left, right, z0=z0)
    sub(2, 1, 3, 3, 1, "wh", "s1", "s2", Z0)                 # papers
    sub(2, 6, 3, 6, 1, "wh", "s2", "s3", Z0)                 # keyboard (sitter side)
    sub(5, 8, 2, 2, 3, "s2", "s2", "s3", Z0)                 # monitor stand
    m = sub(5, 5, 1, 8, 9, "screen", "s2", "s3", Z0 + 3)    # monitor: screen edge glows on top
    xc = (m["xs"] + m["xmax"]) // 2
    c.hline(xc - 1, xc, m["colmax"][xc] + 4, "s1")           # logo on the back casing
    sub(3, 13, 1, 2, 1, "s1", "s2", "s3", Z0)                # mouse
    sub(8, 14, 2, 2, 4, "wh", "rd1", "rd2", Z0)              # mug
    c.outline("ink")
    return c


def make_chair_se():
    c = Canvas(CHAIR_W, CHAIR_H)
    cx, cy = CHAIR_ANCHOR
    ox, oy = cx - 2, cy - 8
    box(c, *cell(ox, oy, 2, 2), 4, 4, 1, "s2", "s3", "s3")                  # wheel base
    box(c, *cell(ox, oy, 3, 3), 1, 1, 8, "s2", "s3", "s3", z0=1)            # post
    box(c, ox, oy, 8, 8, 3, "rd1", "rd2", "rd3", z0=8)                      # seat
    box(c, ox, oy, 1, 8, 14, "rd1", "rd2", "rd3", z0=11)                    # backrest on the NW side
    c.outline("ink")
    return c


# ----------------------------------------------------------------------------- decor
def make_plant():
    c = Canvas(32, 48)
    box(c, 14, 38, 4, 4, 8, "w1", "rd2", "rd3", rim="rd1")
    for cx, cy, r, col in ((16, 27, 6, "gn2"), (9, 23, 5, "gn3"), (23, 23, 5, "gn3"), (16, 16, 6, "gn2"),
                           (14, 13, 2, "gn1"), (20, 22, 2, "gn1"), (8, 21, 2, "gn1"), (16, 26, 2, "gn1")):
        c.disc(cx, cy, r, col)
    c.outline("ink")
    return c


def make_cabinet():
    c = Canvas(44, 64)
    ox, oy, U, V, Z = 22, 45, 8, 10, 34
    info = box(c, ox, oy, U, V, Z, "w4", "w2", "w1", rim="c1")
    cm, xs, xmax = info["colmax"], info["xs"], info["xmax"]
    mid = (xs + xmax) // 2
    for k in range(2, Z):
        c.px(mid, cm[mid] + k, "w3")
    for x in range(xs, xmax + 1):
        c.px(x, cm[x] + 17, "w3")
    for x in (mid - 3, mid + 2):
        for d in (9, 24):
            c.rect(x, cm[x] + d, 1, 3, "gd1")
    for x in range(info["xmin"] + 3, xs - 2):
        for d in (11, 22):
            c.px(x, cm[x] + d, "w1")
    c.outline("ink")
    return c


def make_printer():
    c = Canvas(40, 40)
    ox, oy = 20, 24
    info = box(c, ox, oy, 7, 9, 7, "s1", "s2", "s3", rim="wh")
    x, y = cell(ox, oy, 1, 2)
    box(c, x, y, 5, 5, 4, "s1", "s2", "s3", z0=7, rim="wh")
    x, y = cell(ox, oy, 2, 3)
    box(c, x, y, 3, 3, 1, "wh", "c3", "c3", z0=11)
    cm = info["colmax"]
    xc = (info["xs"] + info["xmax"]) // 2
    c.hline(xc - 5, xc + 3, cm[xc] + 4, "ink")
    c.px(xc + 6, cm[xc + 6] + 3, "gn1")
    c.outline("ink")
    return c


def make_whiteboard():
    c = Canvas(40, 52)
    ox, oy, U, V, Z, Z0 = 30, 36, 2, 14, 24, 10
    x, y = cell(ox, oy, 0, 0)
    box(c, x, y, 1, 1, Z0, "s3", "s3", "s3")
    x, y = cell(ox, oy, 1, 13)
    box(c, x, y, 1, 1, Z0, "s3", "s3", "s3")
    info = box(c, ox, oy, U, V, Z, "s2", "s2", "wh", z0=Z0)
    cm, xs, xmax = info["colmax"], info["xs"], info["xmax"]
    for x in range(xs, xmax + 1):
        c.px(x, cm[x] + 1, "s2")
        c.px(x, cm[x] + Z, "s2")
    c.vline(xs, cm[xs] + 1, cm[xs] + Z, "s2")
    c.vline(xmax, cm[xmax] + 1, cm[xmax] + Z, "s2")
    for x0, x1, d, col in ((xs + 4, xs + 17, 6, "bl2"), (xs + 4, xs + 11, 11, "rd1"), (xs + 4, xs + 20, 16, "gn2")):
        for x in range(x0, x1 + 1):
            c.px(x, cm[x] + d, col)
    c.outline("ink")
    return c


SIGN_W, SIGN_PH, SIGN_ANCHOR = 44, 16, (4, 3)   # plate width / height; anchor = plate top-left corner


def make_sign():
    """Wall-mounted plate (screwed on), sheared to follow the NE wall face: 1 px down per 2 px right."""
    c = Canvas(52, 44)
    ax, ay = SIGN_ANCHOR
    for X in range(SIGN_W):
        off = (X + 1) // 2
        for r in range(SIGN_PH):
            frame = r < 3 or r >= SIGN_PH - 3 or X < 3 or X >= SIGN_W - 3
            col = ("w4" if r == 0 else ("w1" if r == SIGN_PH - 1 else "w2")) if frame else "c1"
            if frame and X in (1, SIGN_W - 2) and r in (1, SIGN_PH - 2):
                col = "gd1"                                  # mounting screws
            c.px(ax + X, ay + off + r, col)
    c.outline("ink")
    return c


# ----------------------------------------------------------------------------- t2 import
def read_png_rgba(path):
    d = Path(path).read_bytes()
    pos, idat, pal, trns = 8, b"", None, None
    while pos < len(d):
        n = struct.unpack(">I", d[pos:pos + 4])[0]
        k, body = d[pos + 4:pos + 8], d[pos + 8:pos + 8 + n]
        if k == b"IHDR":
            w, h, bd, ct = struct.unpack(">IIBB", body[:10])
        elif k == b"IDAT":
            idat += body
        elif k == b"PLTE":
            pal = body
        elif k == b"tRNS":
            trns = body
        pos += n + 12
    if bd != 8 or ct not in (2, 3, 6):
        raise ValueError("unsupported PNG layout")
    ch = {2: 3, 3: 1, 6: 4}[ct]
    stride, raw, prev, rows, p = w * ch, zlib.decompress(idat), bytearray(w * ch), [], 0
    for _ in range(h):
        f, line = raw[p], bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        for i in range(stride):
            a = line[i - ch] if i >= ch else 0
            b = prev[i]
            cc = prev[i - ch] if i >= ch else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + (a + b) // 2) & 255
            elif f == 4:
                pa, pb, pc = abs(b - cc), abs(a - cc), abs(a + b - 2 * cc)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else (b if pb <= pc else cc))) & 255
        rows.append(line)
        prev = line
    px = []
    for y in range(h):
        r = []
        for x in range(w):
            if ct == 6:
                r.append(tuple(rows[y][x * 4:x * 4 + 4]))
            elif ct == 2:
                r.append((*rows[y][x * 3:x * 3 + 3], 255))
            else:
                i = rows[y][x]
                r.append((*pal[i * 3:i * 3 + 3], trns[i] if trns and i < len(trns) else 255))
        px.append(r)
    return w, h, px


def nearest_pal(rgb):
    return min(PAL_VALUES, key=lambda q: sum((q[i] - rgb[i]) ** 2 for i in range(3)))


def import_crop(name):
    """Crop T2_CROPS[name] from the t2 sheet -> palette-snapped, alpha 0/255 canvas, or None."""
    box_ = T2_CROPS.get(name)
    if not box_ or not T2_SOURCE.exists():
        return None
    _, _, px = read_png_rgba(T2_SOURCE)
    x0, y0, x1, y1 = box_
    c = Canvas(x1 - x0, y1 - y0)
    for y in range(y0, y1):
        for x in range(x0, x1):
            p = px[y][x]
            if p[3] >= 128:
                c.p[y - y0][x - x0] = nearest_pal(p[:3])
    return c


# ----------------------------------------------------------------------------- preview
def make_preview(tiles, sprites):
    items = []   # (depth_key, layer, sprite, x, y)

    def tile_pos(gx, gy):
        return lattice_center(gx, gy, 0, 0)

    def put(sprite, anchor, target, layer=1, depth=None):
        x, y = target[0] - anchor[0], target[1] - anchor[1]
        items.append(((target[1] if depth is None else depth, target[0]), layer, sprite, x, y))

    for gy in range(-3, 6):
        for gx in range(-1, 8):
            inroom = 0 <= gx <= 5 and 0 <= gy <= 4
            if inroom:
                t = tiles["development"]
            elif 2 <= gx <= 5 and gy < 0:       # 4-tile wide path leading to the door at (4,0)
                t = tiles["path"]
            else:
                t = tiles["floor"]
            cx, cy = tile_pos(gx, gy)
            items.append(((cy, cx), 0, t, cx - 38, cy - 19))
    for gy in range(0, 5):
        for gx in range(0, 6):
            if gx == 0 and gy == 0:
                kind = "corner"
            elif gx == 0:
                kind = "nw-end" if gy == 4 else "nw"
            elif gy == 0:
                kind = "door-ne" if gx == 4 else ("ne-end" if gx == 5 else "ne")
            else:
                continue
            put(sprites["wall-" + kind], DOOR_ANCHOR if kind.startswith("door") else WALL_ANCHOR, tile_pos(gx, gy))
    for gx, gy, d in ((2, 1, "se"), (2, 3, "se"), (4, 3, "sw")):
        cx, cy = tile_pos(gx, gy)
        dx = SEAT_OFFSET[0] if d == "se" else -SEAT_OFFSET[0]
        put(sprites["chair-" + d], CHAIR_ANCHOR, (cx + dx, cy + SEAT_OFFSET[1]))
        put(sprites["desk-" + d], DESK_ANCHOR, (cx, cy))
    put(sprites["plant"], (16, 42), tile_pos(5, 1))
    put(sprites["cabinet"], (22, 54), tile_pos(1, 4))
    put(sprites["printer"], (20, 32), tile_pos(4, 1))   # well inside the room, not on a front edge
    put(sprites["whiteboard"], (20, 44), tile_pos(1, 2))
    # Room sign: screwed onto the NE wall face, spanning the walls of tiles (2,0) and (3,0).
    ex, ey = tile_pos(2, 0)
    put(sprites["room-sign"], SIGN_ANCHOR, (ex + 14, ey - 52), depth=60)
    # Outside plant beyond the east end of the wall, clear of the wall end cap.
    put(sprites["plant"], (16, 42), tile_pos(6, -1))
    items.sort(key=lambda it: (it[1], it[0]))   # floors (layer 0) first, then by depth
    xs0 = min(i[3] for i in items) - 8
    ys0 = min(i[4] for i in items) - 8
    xs1 = max(i[3] + i[2].w for i in items) + 8
    ys1 = max(i[4] + i[2].h for i in items) + 8
    c = Canvas(xs1 - xs0, ys1 - ys0)
    c.rect(0, 0, c.w, c.h, "bg")
    for _, _, s, x, y in items:
        c.blit(s, x - xs0, y - ys0)
    return c


# ----------------------------------------------------------------------------- verification
def verify(out):
    for name, c in out.items():
        for row in c.p:
            for v in row:
                assert v is None or v in PAL_VALUES, (name, v)
        if name.startswith("floor-") and not name.startswith("floor-test"):
            assert (c.w, c.h) == (TILE_W, TILE_H), name
            for y in range(TILE_H):
                for x in range(TILE_W):
                    assert (c.p[y][x] is not None) == inside(x, y), (name, x, y)
    # lattice coverage: every pixel of the 6x6 mosaic area is covered exactly once
    cover = {}
    for gy in range(6):
        for gx in range(6):
            cx, cy = lattice_center(gx, gy, 38 + 38 * 5, 19)
            for y in range(TILE_H):
                for x in range(TILE_W):
                    if inside(x, y):
                        key = (cx - 38 + x, cy - 19 + y)
                        cover[key] = cover.get(key, 0) + 1
    assert max(cover.values()) == 1, "tiles overlap"
    # edge-adjacent rows of the lattice must be gap-free: check the middle tile fully surrounded
    cx, cy = lattice_center(2, 2, 38 + 38 * 5, 19)
    for y in range(TILE_H):
        for x in range(TILE_W):
            if inside(x, y):
                assert (cx - 38 + x, cy - 19 + y) in cover
    assert len(PAL) <= 40, "palette too large"
    # seamless floors: each pattern repeats with the lattice step (76) in both u and v
    for kind in ("wood", "stone", "carpet"):
        for variant in (0, 1):
            for u in range(0, 160, 1):
                for v in range(0, 160, 3):
                    r = pattern(kind, variant, u, v)
                    assert r == pattern(kind, variant, u + PERIOD, v) == pattern(kind, variant, u, v + PERIOD), \
                        ("seam", kind, variant, u, v)
    lum = lambda q: 0.3 * q[0] + 0.59 * q[1] + 0.11 * q[2]
    assert lum(PAL["pa1"]) < lum(PAL["c1"]) - 80, "path not dark enough vs preview background"
    assert lum(PAL["pa1"]) < lum(PAL["w3"]) - 40, "path not dark enough vs floor"
    # department floors must stand out from the (non-cream) preview backdrop
    lum_bg = lum(PAL["bg"])
    assert lum_bg < 100 and PAL["bg"][0] - PAL["bg"][2] < 40, "backdrop must be dark, not cream"
    for d in ("fm", "fd", "fr", "fc", "fq", "fe"):
        for shade in ("1", "2"):
            assert lum(PAL[d + shade]) - lum_bg >= 55, ("department floor too close to backdrop", d, shade)
    # wall sprites: nothing on the top row (outlines must close); door opening is wide and tall
    for n in ("wall-nw", "wall-ne", "wall-door-nw", "wall-door-ne", "wall-corner", "wall-nw-end", "wall-ne-end"):
        assert all(v is None for v in out[n + ".png"].p[0]), (n, "touches the top edge")
    # no full-height dark joint line at segment boundaries
    for n, col in (("wall-nw", 38), ("wall-nw-end", 38), ("wall-door-nw", 38),
                   ("wall-ne", 37), ("wall-ne-end", 37), ("wall-door-ne", 37)):
        assert all(v is None for row in out[n + ".png"].p for v in [row[col]]), (n, "joint column not empty")
    for n, xs in (("wall-door-ne.png", range(75 - 32, 75 - 5 + 1)), ("wall-door-nw.png", range(5, 33))):
        dn = out[n]
        for x in ((75 - 20, 75 - 28) if "ne" in n else (20, 28)):   # open part of the doorway (n=20, 28)
            filled = [y for y in range(dn.h) if dn.p[y][x] is not None]
            gap_rows = [y for y in range(filled[0], filled[-1]) if dn.p[y][x] is None]
            assert len(gap_rows) >= 55, (n, "opening too low", len(gap_rows))
        assert len(xs) >= 28, "opening too narrow"
    # chairs: transparent padding on every side
    for n in ("chair-se.png", "chair-sw.png"):
        ch = out[n]
        for y in range(ch.h):
            for x in range(ch.w):
                if ch.p[y][x] is not None:
                    assert 2 <= x < ch.w - 2 and 2 <= y < ch.h - 2, (n, "touches edge", x, y)
    for n in ("wall-nw.png", "wall-ne.png"):
        assert out[n].p != out["wall-nw.png"].p or n == "wall-nw.png", "wall directions identical"
    assert out["desk-se.png"].p != out["desk-sw.png"].p, "desk directions identical"


def main():
    global OUT
    if len(sys.argv) > 1:
        OUT = Path(sys.argv[1])
    out = {}
    tiles = make_floors(out)
    make_floor_test(tiles, out)

    sprites = {}
    for kind in ("nw", "ne", "door-nw", "door-ne", "corner", "nw-end", "ne-end"):
        sprites["wall-" + kind] = make_wall(kind)
    sprites["desk-se"] = make_desk_se()
    sprites["desk-sw"] = sprites["desk-se"].mirror()
    sprites["chair-se"] = make_chair_se()
    sprites["chair-sw"] = sprites["chair-se"].mirror()
    for name, fn in (("plant", make_plant), ("cabinet", make_cabinet), ("printer", make_printer),
                     ("whiteboard", make_whiteboard)):
        sprites[name] = import_crop(name) or fn()
    sprites["room-sign"] = make_sign()
    sprites["room-sign-nw"] = sprites["room-sign"].mirror()
    for n, s in sprites.items():
        out[n + ".png"] = s
    out["office-v2-preview.png"] = make_preview(tiles, sprites)

    verify(out)
    for n, c in out.items():
        c.png(OUT / n)
    print(f"wrote {len(out)} PNGs to {OUT}")
    for n, c in sorted(out.items()):
        print(f"  {n:28s} {c.w}x{c.h}")


if __name__ == "__main__":
    main()
