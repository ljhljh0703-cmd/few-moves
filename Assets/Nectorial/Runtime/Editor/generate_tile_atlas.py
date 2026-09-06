#!/usr/bin/env python3
"""Generate the original 8 x 16px placeholder tile atlas deterministically."""

import struct
import zlib
from pathlib import Path

SIZE = 16
COLORS = {
    "clear": (0, 0, 0, 0),
    "floor": (31, 45, 63, 255),
    "grid": (42, 59, 80, 255),
    "wall": (82, 98, 119, 255),
    "wall_hi": (112, 130, 153, 255),
    "player": (53, 153, 255, 255),
    "exit": (255, 207, 64, 255),
    "door": (194, 119, 58, 255),
    "switch": (98, 218, 151, 255),
    "guard": (239, 85, 101, 255),
    "intent": (239, 85, 101, 125),
}


def tile(fill):
    return [[COLORS[fill] for _ in range(SIZE)] for _ in range(SIZE)]


def circle(image, color, radius):
    for y in range(SIZE):
        for x in range(SIZE):
            if (x - 7.5) ** 2 + (y - 7.5) ** 2 <= radius ** 2:
                image[y][x] = COLORS[color]


def diamond(image, color, radius):
    for y in range(SIZE):
        for x in range(SIZE):
            if abs(x - 7.5) + abs(y - 7.5) <= radius:
                image[y][x] = COLORS[color]


tiles = []
floor = tile("floor")
for i in range(SIZE):
    floor[0][i] = COLORS["grid"]
    floor[i][0] = COLORS["grid"]
tiles.append(floor)

wall = tile("wall")
for y in range(2, SIZE, 5):
    for x in range(SIZE):
        wall[y][x] = COLORS["wall_hi"]
tiles.append(wall)

player = tile("clear")
circle(player, "player", 5.5)
tiles.append(player)

exit_tile = tile("clear")
diamond(exit_tile, "exit", 6)
tiles.append(exit_tile)

door = tile("clear")
for y in range(2, 15):
    for x in range(4, 12):
        door[y][x] = COLORS["door"]
tiles.append(door)

switch = tile("clear")
for y in range(5, 11):
    for x in range(3, 13):
        switch[y][x] = COLORS["switch"]
tiles.append(switch)

guard = tile("clear")
diamond(guard, "guard", 6)
for y in range(6, 9):
    guard[y][5] = COLORS["floor"]
    guard[y][10] = COLORS["floor"]
tiles.append(guard)

intent = tile("clear")
for i in range(2, 14):
    intent[2][i] = COLORS["intent"]
    intent[13][i] = COLORS["intent"]
    intent[i][2] = COLORS["intent"]
    intent[i][13] = COLORS["intent"]
tiles.append(intent)

width = SIZE * len(tiles)
raw = bytearray()
for y in range(SIZE):
    raw.append(0)
    for image in tiles:
        for pixel in image[y]:
            raw.extend(pixel)


def chunk(kind, payload):
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)


png = b"\x89PNG\r\n\x1a\n"
png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, SIZE, 8, 6, 0, 0, 0))
png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
png += chunk(b"IEND", b"")

output = Path(__file__).resolve().parents[2] / "Resources" / "Visuals" / "turn-escape-tiles.png"
output.write_bytes(png)
print(output)
