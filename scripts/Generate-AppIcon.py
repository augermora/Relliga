"""Rebuild the checked-in ICO from Relliga's code-native mark; Python stdlib only.
The geometry mirrors src/Relliga.Desktop/Assets/Relliga.svg. Not required to publish.
"""
from pathlib import Path
import struct
import zlib


def curve(a, b, c, d):
    return [tuple((1-t)**3*a[k] + 3*(1-t)**2*t*b[k] + 3*(1-t)*t*t*c[k] + t**3*d[k]
                  for k in (0, 1)) for t in (i/24 for i in range(1, 25))]


polygon = [(76, 187), (76, 73), (108, 73), (108, 90)]
polygon += curve((108, 90), (121, 73), (139, 68), (165, 73))
polygon += [(165, 107)]
polygon += curve((165, 107), (133, 101), (110, 117), (110, 150))
polygon += [(110, 187)]


def inside(x, y):
    result = False
    previous = polygon[-1]
    for current in polygon:
        if (current[1] > y) != (previous[1] > y) and x < (previous[0]-current[0])*(y-current[1])/(previous[1]-current[1])+current[0]:
            result = not result
        previous = current
    return result


def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind+data))


def png(size):
    rows = bytearray()
    for y in range(size):
        rows.append(0)
        for x in range(size):
            samples = []
            for dx, dy in ((.25, .25), (.75, .25), (.25, .75), (.75, .75)):
                px, py = (x+dx)*256/size, (y+dy)*256/size
                cx, cy = min(200, max(56, px)), min(200, max(56, py))
                if (px-cx)**2 + (py-cy)**2 > 56**2:
                    samples.append((0, 0, 0, 0))
                else:
                    samples.append((16, 27, 46, 255) if inside(px, py) else (169, 199, 255, 255))
            alpha = sum(s[3] for s in samples)
            rgb = [round(sum(s[k]*s[3] for s in samples)/alpha) if alpha else 0 for k in range(3)]
            rows.extend((*rgb, round(alpha/4)))
    header = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', header) + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')


sizes = (16, 32, 48, 64, 256)
images = [png(size) for size in sizes]
offset = 6 + 16*len(images)
entries = bytearray()
for size, content in zip(sizes, images):
    entries.extend(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(content), offset))
    offset += len(content)
target = Path(__file__).resolve().parents[1] / 'src/Relliga.Desktop/Assets/Relliga.ico'
target.write_bytes(struct.pack('<HHH', 0, 1, len(images)) + entries + b''.join(images))
print(target)
