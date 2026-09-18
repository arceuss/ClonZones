"""Export a GH3 PC 1.31 .fnt.xen bitmap font into the editable pair the mod loads:
<name>.png (straight-alpha RGBA atlas) and <name>.font.txt (plain-text metrics).

One-off preparation only; the mod never runs Python. Stdlib only.

Usage: python gh3_font_export.py <font.fnt.xen> <output_dir>

Container layout (all big-endian except the trailing DDS page), recovered from
gh3.exe sub_5F3510 and checked against the file bytes:
  +0   u32 yorigin (subtracted from every glyph's vertical origin)
  +4   i32 default pre-spacing      +8   i32 default post-spacing
  +12  f32 line height              +16  u32 glyph record array offset
  +20  u16[65536] char -> glyph index (0 = unmapped)
  +131164 f32 space width           +131232 u32 texture metadata offset
  glyph record (36 B): U0 V0 U1 V1 W H YOff f32, XOff Extra i32
  texture metadata (40 B) then a little-endian DDS (DXT2/3/5).
"""
import os
import struct
import sys
import zlib


def u16be(b, o): return struct.unpack('>H', b[o:o + 2])[0]
def u32be(b, o): return struct.unpack('>I', b[o:o + 4])[0]
def i32be(b, o): return struct.unpack('>i', b[o:o + 4])[0]
def f32be(b, o): return struct.unpack('>f', b[o:o + 4])[0]


def rgb565(c):
    return ((c >> 11) & 31) * 255 // 31, ((c >> 5) & 63) * 255 // 63, (c & 31) * 255 // 31


def palette(c0, c1):
    r0, g0, b0 = rgb565(c0)
    r1, g1, b1 = rgb565(c1)
    if c0 > c1:
        return [(r0, g0, b0), (r1, g1, b1),
                ((2 * r0 + r1) // 3, (2 * g0 + g1) // 3, (2 * b0 + b1) // 3),
                ((r0 + 2 * r1) // 3, (g0 + 2 * g1) // 3, (b0 + 2 * b1) // 3)]
    return [(r0, g0, b0), (r1, g1, b1), ((r0 + r1) // 2, (g0 + g1) // 2, (b0 + b1) // 2), (0, 0, 0)]


def decode_block(data, off, explicit_alpha):
    if explicit_alpha:
        alphas = [((data[off + i // 2] >> (0 if i % 2 == 0 else 4)) & 0xF) * 17 for i in range(16)]
    else:
        a0, a1 = data[off], data[off + 1]
        bits = int.from_bytes(data[off + 2:off + 8], 'little')
        if a0 > a1:
            apal = [a0, a1] + [((6 - i) * a0 + i * a1 + 3) // 7 for i in range(1, 7)]
        else:
            apal = [a0, a1] + [((4 - i) * a0 + i * a1 + 2) // 5 for i in range(1, 5)] + [0, 255]
        alphas = [apal[(bits >> (3 * i)) & 7] for i in range(16)]
    c0, c1, idx = struct.unpack('<HHI', data[off + 8:off + 16])
    pal = palette(c0, c1)
    return [pal[(idx >> (2 * i)) & 3] + (alphas[i],) for i in range(16)]


def decode_dds(dds):
    assert dds[:4] == b'DDS ', 'no DDS page'
    height, width = struct.unpack('<II', dds[12:20])
    fourcc = dds[84:88]
    explicit_alpha = fourcc in (b'DXT2', b'DXT3')
    if not explicit_alpha and fourcc not in (b'DXT4', b'DXT5'):
        raise ValueError('unsupported page format %r' % fourcc)
    data = dds[128:]
    img = bytearray(width * height * 4)
    bw, bh = width // 4, height // 4
    for by in range(bh):
        for bx in range(bw):
            px = decode_block(data, (by * bw + bx) * 16, explicit_alpha)
            for i, (r, g, b, a) in enumerate(px):
                o = ((by * 4 + i // 4) * width + bx * 4 + (i % 4)) * 4
                img[o:o + 4] = bytes((r, g, b, a))
    return width, height, img


def write_png(path, w, h, rgba):
    def chunk(tag, body):
        return struct.pack('>I', len(body)) + tag + body + struct.pack('>I', zlib.crc32(tag + body) & 0xFFFFFFFF)
    raw = b''.join(b'\x00' + bytes(rgba[y * w * 4:(y + 1) * w * 4]) for y in range(h))
    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')
    with open(path, 'wb') as f:
        f.write(png)


def char_token(code):
    if 33 <= code <= 126 and code != ord('#'):
        return chr(code)
    return 'U+%04X' % code


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    src, out_dir = sys.argv[1], sys.argv[2]
    name = os.path.basename(src).split('.')[0]
    d = open(src, 'rb').read()
    yorigin = u32be(d, 0)
    pre, post = i32be(d, 4), i32be(d, 8)
    line_height = f32be(d, 12)
    glyph_off = u32be(d, 16)
    space_width = f32be(d, 131164)
    tex_meta_off = u32be(d, 131232)
    assert (tex_meta_off - glyph_off) % 36 == 0, 'glyph table size'
    nglyph = (tex_meta_off - glyph_off) // 36
    w, h, img = decode_dds(d[tex_meta_off + 40:])
    glyphs = []
    for gi in range(nglyph):
        o = glyph_off + gi * 36
        u0, v0, u1, v1, gw, gh, yoff = (f32be(d, o + k * 4) for k in range(7))
        glyphs.append((u0 * w, v0 * h, gw, gh, yoff, i32be(d, o + 28), i32be(d, o + 32)))
    table = [u16be(d, 20 + c * 2) for c in range(65536)]

    os.makedirs(out_dir, exist_ok=True)
    write_png(os.path.join(out_dir, name + '.png'), w, h, img)
    lines = [
        '# GH3 bitmap font "%s" exported from %s' % (name, os.path.basename(src)),
        '# Edit freely. Pixel coordinates refer to %s.png with the origin at the top-left.' % name,
        '# glyph <char|U+XXXX> <x> <y> <width> <height> <yoff> [xoff] [extra]',
        '#   width   = advance and ink width in pixels; pen moves width + font_spacing per glyph',
        '#   yoff    = vertical offset: quad top = (lineheight - (height - yoff) - yorigin) * scale + 1.25',
        '#   xoff    = x bearing subtracted from the pen (int), extra = trailing advance (int)',
        'page %d %d' % (w, h),
        'lineheight %g' % line_height,
        'spacewidth %g' % space_width,
        'yorigin %d' % yorigin,
        'pre %d' % pre,
        'post %d' % post,
    ]
    mapped = 0
    for code, gi in enumerate(table):
        if gi == 0:
            continue
        x, y, gw, gh, yoff, xoff, extra = glyphs[gi]
        lines.append('glyph %s %g %g %g %g %g %d %d' % (char_token(code), x, y, gw, gh, yoff, xoff, extra))
        mapped += 1
    with open(os.path.join(out_dir, name + '.font.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    print('%s: %d glyph records, %d mapped chars, page %dx%d' % (name, nglyph, mapped, w, h))


if __name__ == '__main__':
    main()
