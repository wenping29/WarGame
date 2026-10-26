import struct, zlib

def write_geotiff(path, data, bits, sample_format, west, north, res_x, res_y, nodata=None, tiled=False):
    h, w = len(data), len(data[0])
    if bits == 16:
        raw = bytearray()
        for r in range(h):
            prev = 0
            for c in range(w):
                v = data[r][c]
                delta = v - prev
                if delta > 32767: delta -= 65536
                elif delta < -32768: delta += 65536
                raw += struct.pack('<h', delta)
                prev = v
    else:
        raw = bytearray()
        for r in range(h):
            prev = 0
            for c in range(w):
                v = data[r][c] & 0xFF
                delta = (v - prev) & 0xFF
                raw.append(delta)
                prev = v
    comp = zlib.compress(bytes(raw), 6)

    transform = [res_x, 0, 0, 0,  0, -res_y, 0, 0,  0, 0, 0, 0,  west, north, 0, 1]
    tags = []
    tags.append((256, 4, 1, w))
    tags.append((257, 4, 1, h))
    tags.append((258, 3, 1, bits))
    tags.append((259, 3, 1, 8))
    tags.append((262, 3, 1, 1))
    if not tiled:
        tags.append((273, 4, 1, 0))
        tags.append((277, 3, 1, 1))
        tags.append((278, 4, 1, h))
        tags.append((279, 4, 1, 0))
    else:
        tags.append((322, 4, 1, w))
        tags.append((323, 4, 1, h))
        tags.append((324, 4, 1, 0))
        tags.append((325, 4, 1, 0))
    tags.append((317, 3, 1, 2))
    tags.append((339, 3, 1, sample_format))
    if nodata is not None:
        tags.append((42113, 2, len(str(nodata)+'\0'), 0))
    tags.append((34264, 12, 16, 0))
    tags.sort(key=lambda t: t[0])

    ifd_pos = 8
    data_start = ifd_pos + 2 + len(tags)*12 + 4
    blob_items = {}
    if nodata is not None:
        blob_items[42113] = (str(nodata) + '\0').encode('ascii')
    blob_items[34264] = b''.join(struct.pack('<d', v) for v in transform)
    off = data_start
    blob_offsets = {}
    for t, b in blob_items.items():
        blob_offsets[t] = off
        off += len(b)
        if off % 2 == 1: off += 1
    strip_off = off
    off += len(comp)

    ifd_entries = bytearray()
    for (tag, typ, cnt, val) in tags:
        if tag in blob_offsets:
            v = blob_offsets[tag]
        elif tag in (273, 279, 324, 325):
            v = strip_off if tag in (273, 324) else len(comp)
        else:
            v = val
        if typ in (2, 12) or (tag in (273, 279, 324, 325)):
            ifd_entries += struct.pack('<HHII', tag, typ, cnt, v)
        else:
            entry = struct.pack('<HHI', tag, typ, cnt)
            if typ == 3:
                entry += struct.pack('<H', v) + b'\x00\x00'
            else:
                entry += struct.pack('<I', v)
            ifd_entries += entry

    out = bytearray()
    out += b'II' + struct.pack('<H', 42)
    out += struct.pack('<I', ifd_pos)
    out += struct.pack('<H', len(tags))
    out += ifd_entries
    out += struct.pack('<I', 0)
    for t, b in blob_items.items():
        while len(out) < blob_offsets[t]:
            out += b'\x00'
        out += b
        if len(out) % 2 == 1: out += b'\x00'
    while len(out) < strip_off:
        out += b'\x00'
    out += comp
    with open(path, 'wb') as f:
        f.write(out)
    print(f"wrote {path}: {w}x{h} bits={bits} sf={sample_format} tiled={tiled} nodata={nodata} -> {len(out)} bytes")

w, h = 8, 6
data = [[(r*10 + c*3) for c in range(w)] for r in range(h)]
data[0][0] = -32768
data[5][7] = -32768
write_geotiff('test_int16.tif', data, 16, 2, west=116.0, north=40.0, res_x=0.01, res_y=0.01, nodata=-32768, tiled=False)

w2, h2 = 4, 4
data2 = [[(r*4 + c) % 11 for c in range(w2)] for r in range(h2)]
write_geotiff('test_uint8.tif', data2, 8, 1, west=-180.0, north=90.0, res_x=0.5, res_y=0.5, nodata=255, tiled=False)

w3, h3 = 4, 4
data3 = [[(r*5 + c*2) for c in range(w3)] for r in range(h3)]
write_geotiff('test_tiled.tif', data3, 16, 2, west=0.0, north=1.0, res_x=0.25, res_y=0.25, nodata=-32768, tiled=True)
