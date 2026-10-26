"""Dump GeoTIFF geo tags raw values (独立于被测的 C# GeoBand，用于判定约定)。"""
import struct, sys

def dump(path):
    with open(path, 'rb') as f:
        head = f.read(8)
        bo = head[:2]
        le = bo == b'II'
        endian = '<' if le else '>'
        magic = struct.unpack(endian + 'H', head[2:4])[0]
        ifd = struct.unpack(endian + 'I', head[4:8])[0]
        print(f"{path}: byteorder={bo} magic={magic} firstIFD={ifd}")

        def rd(fmt, n):
            return struct.unpack(endian + fmt, f.read(n))[0]

        f.seek(ifd)
        n = rd('H', 2)
        print(f"  entries={n}   (endian={'LE' if le else 'BE'})")
        for i in range(n):
            tag, typ, cnt = rd('H', 2), rd('H', 2), rd('I', 4)
            raw = f.read(4)
            name = {256: 'ImageWidth', 257: 'ImageLength', 258: 'BitsPerSample',
                    259: 'Compression', 262: 'Photometric', 317: 'Predictor',
                    339: 'SampleFormat', 322: 'TileWidth', 323: 'TileLength',
                    33550: 'ModelPixelScale', 33922: 'ModelTiepoint',
                    34264: 'ModelTransformation', 42113: 'GDAL_NODATA'}.get(tag, '')
            extra = ''
            if tag in (33550, 33922, 34264):
                if typ == 12 and cnt * 8 > 4:
                    off = struct.unpack(endian + 'I', raw)[0]
                    save = f.tell()
                    f.seek(off)
                    vals = struct.unpack(endian + 'd' * cnt, f.read(8 * cnt))
                    f.seek(save)
                    extra = f" @off={off} vals={[round(v, 6) for v in vals]}"
                else:
                    extra = f" inline raw={raw.hex()}"
            elif tag == 42113:
                c = cnt if cnt * 1 <= 4 else 4
                off = struct.unpack(endian + 'I', raw)[0] if cnt > 4 else None
                if off is None:
                    extra = f" inline='{raw[:cnt].decode('ascii', 'replace')}'"
                else:
                    save = f.tell(); f.seek(off)
                    extra = f" @off={off} '{f.read(cnt).decode('ascii', 'replace').strip()}'"
                    f.seek(save)
            print(f"  [{i:2}] tag={tag:<6} type={typ:<2} count={cnt:<4} {name:<20}{extra}")

for p in sys.argv[1:]:
    dump(p)
    print()
