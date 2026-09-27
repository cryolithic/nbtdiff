#!/usr/bin/env python3
"""Regenerates the app icon from the 16x16 pixel map below. Standard library only.

    python3 tools/icon/make_icon.py

Writes src/NbtDiff.App/Assets/nbtdiff.ico (16-256 px) and nbtdiff-{16..256}.png.
Every size is the same 16x16 map scaled by a whole number, so pixels stay square.
"""
import struct
import zlib
from pathlib import Path

# 5x5 chunks of 2x2 px with 1 px gutters, the region view in miniature.
# e empty, g same, x different, L left only, R right only.
CHUNKS = ["egeee", "xxLeg", "xReee", "eeexe", "geRRe"]

PALETTE = {
    ".": (0, 0, 0, 0),
    "k": (0x16, 0x18, 0x1D, 255),  # tile
    "e": (0x2B, 0x30, 0x3A, 255),
    "g": (0x3C, 0x8A, 0x4E, 255),
    "x": (0xEF, 0x4B, 0x4B, 255),
    "L": (0x3D, 0x8F, 0xE6, 255),
    "R": (0xA0, 0x60, 0xE8, 255),
}

SIZES = [16, 32, 48, 64, 128, 256]
OUT = Path(__file__).resolve().parents[2] / "src" / "NbtDiff.App" / "Assets"


def pixel_map():
    rows = []
    for y in range(16):
        if y % 3 == 0:
            rows.append(".kkkkkkkkkkkkkk." if y in (0, 15) else "k" * 16)
        else:
            rows.append("k" + "k".join(c * 2 for c in CHUNKS[y // 3]) + "k")
    assert all(len(r) == 16 for r in rows)
    return rows


def png(rows, size):
    scale = size // 16
    raw = bytearray()
    for r in rows:
        line = b"".join(bytes(PALETTE[c]) * scale for c in r)
        for _ in range(scale):
            raw += b"\x00" + line

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def ico(images):
    # PNG-compressed entries: supported by Windows Vista and later for every size.
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return header + entries + blobs


def main():
    rows = pixel_map()
    OUT.mkdir(parents=True, exist_ok=True)
    images = [(s, png(rows, s)) for s in SIZES]
    for s, data in images:
        (OUT / f"nbtdiff-{s}.png").write_bytes(data)
    (OUT / "nbtdiff.ico").write_bytes(ico(images))


if __name__ == "__main__":
    main()
