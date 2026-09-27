"""Tileable night facade for distant placeholder buildings: DXT1 diffuse + spec (+ a flat normal map).

For MHO's envbaseshaderv3 (e.g. madripoor_hitown_storefront_a_mat): diffuse1 = colour; specemissivereflectheight1
= R specular, G emissive (emissive = diffuse * G * EmissiveMult when UseEmissive/UseDiffuseInEmissive are on, as on
storefront_a_mat), B reflection; normal1 = tangent-space normal (flat here). All DXT1, like the stock textures.

usage: python make_facade.py outdir [--size 1024] [--bay 128] [--lit 0.3] [--glow 150] [--seed 7]
  one texel = one world unit when the wall UV repeat (--wall-uv) equals --size
"""
import struct, sys, random, os

args = sys.argv[1:]
OUT = args[0]
def opt(name, default):
    return type(default)(args[args.index(name) + 1]) if name in args else default
SIZE = opt('--size', 1024)
BAY = opt('--bay', 128)          # floor height and bay width in texels
LIT = opt('--lit', 0.3)          # share of lit windows
GLOW = opt('--glow', 150)        # emissive strength of lit windows (0-255)
SEED = opt('--seed', 7)
rnd = random.Random(SEED)

WALL, BAND = (58, 56, 62), (74, 72, 78)
GLASS = (22, 30, 44)
LIGHTS = [(255, 196, 110), (255, 214, 150), (190, 215, 255), (210, 150, 80)]
n = SIZE // BAY
lit = {(i, j): rnd.choice(LIGHTS) for i in range(n) for j in range(n) if rnd.random() < LIT}
noise = [rnd.randint(-6, 6) for _ in range(SIZE * SIZE)]

def texel(x, y):
    """(diffuse, spec) at texel x, y (y down; image top = higher up the building)."""
    bx, by = x % BAY, y % BAY
    cell = (x // BAY, y // BAY)
    if by < 10:                                            # floor band
        d, s = BAND, (20, 0, 0)
    elif 18 <= bx < BAY - 18 and 28 <= by < BAY - 22:      # window
        if cell in lit:
            c = lit[cell]; k = 0.85 + 0.15 * (by - 28) / (BAY - 50)
            d, s = tuple(min(255, int(v * k)) for v in c), (60, GLOW, 20)
        else:
            k = 1 + 0.5 * (by - 28) / (BAY - 50)
            d, s = tuple(int(v * k) for v in GLASS), (90, 0, 60)
    elif (16 <= bx < BAY - 16 and 26 <= by < BAY - 20):    # window frame
        d, s = (40, 40, 44), (30, 0, 0)
    else:
        d, s = WALL, (15, 0, 0)
    e = noise[y * SIZE + x]
    return tuple(max(0, min(255, v + e)) for v in d), s

def rgb565(c): return ((c[0] * 31 + 127) // 255) << 11 | ((c[1] * 63 + 127) // 255) << 5 | ((c[2] * 31 + 127) // 255)
def unpack(v): return ((v >> 11) * 255 // 31, ((v >> 5) & 63) * 255 // 63, (v & 31) * 255 // 31)

def dxt1(pixels, w, h):
    """Simple DXT1 (4-colour mode): endpoints = the block's darkest/brightest texels by luma."""
    out = bytearray()
    for by in range(0, h, 4):
        for bx in range(0, w, 4):
            block = [pixels[(by + i // 4) * w + bx + i % 4] for i in range(16)]
            lum = [p[0] * 3 + p[1] * 6 + p[2] for p in block]
            c0, c1 = rgb565(block[lum.index(max(lum))]), rgb565(block[lum.index(min(lum))])
            if c0 < c1: c0, c1 = c1, c0
            if c0 == c1:
                out += struct.pack('<HHI', c0, c1, 0); continue
            p0, p1 = unpack(c0), unpack(c1)
            pal = [p0, p1, tuple((2 * a + b) // 3 for a, b in zip(p0, p1)), tuple((a + 2 * b) // 3 for a, b in zip(p0, p1))]
            bits = 0
            for i, p in enumerate(block):
                k = min(range(4), key=lambda j: sum((p[t] - pal[j][t]) ** 2 for t in range(3)))
                bits |= k << (2 * i)
            out += struct.pack('<HHI', c0, c1, bits)
    return out

def write_dds(path, pixels, w, h):
    data = dxt1(pixels, w, h)
    hdr = bytearray(128)
    struct.pack_into('<4sIIIIIII', hdr, 0, b'DDS ', 124, 0x81007, h, w, len(data), 0, 1)
    struct.pack_into('<II4s', hdr, 76, 32, 4, b'DXT1')
    struct.pack_into('<I', hdr, 108, 0x1000)
    open(path, 'wb').write(bytes(hdr) + data)

os.makedirs(OUT, exist_ok=True)
tex = [texel(x, y) for y in range(SIZE) for x in range(SIZE)]
write_dds(os.path.join(OUT, 'ht_facade_diff.dds'), [t[0] for t in tex], SIZE, SIZE)
write_dds(os.path.join(OUT, 'ht_facade_spec.dds'), [t[1] for t in tex], SIZE, SIZE)
write_dds(os.path.join(OUT, 'ht_flat_nrml.dds'), [(128, 128, 255)] * 256, 16, 16)
try:
    from PIL import Image
    im = Image.new('RGB', (SIZE, SIZE)); im.putdata([t[0] for t in tex]); im.save(os.path.join(OUT, 'ht_facade_diff_preview.png'))
except ImportError:
    pass
print(f"{OUT}: {SIZE}x{SIZE} facade, {n}x{n} windows of {BAY}, {len(lit)} lit ({LIT:.0%}), glow {GLOW}; flat normal 16x16")
