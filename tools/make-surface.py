"""Pre-render the prototype's alpha ramp with deterministic pixel dithering.

The shared 512x512 texture avoids broad 8-bit gradient steps and has no timer.
"""
from pathlib import Path
from PIL import Image

size = 512
image = Image.new('RGBA', (size, size))
pixels = image.load()
for y in range(size):
    alpha = 255 * (.065 + (.026 - .065) * y / (size - 1))
    lower = int(alpha)
    fraction = alpha - lower
    for x in range(size):
        value = ((x + 1) * 374761393 + (y + 1) * 668265263) & 0xffffffff
        value = ((value ^ (value >> 13)) * 1274126177) & 0xffffffff
        threshold = ((value ^ (value >> 16)) & 0xffff) / 65536
        pixels[x, y] = (255, 255, 255, lower + (threshold < fraction))
target = Path(__file__).resolve().parents[1] / 'src/Neo.App/Assets/surface-soft.png'
image.save(target, optimize=True)
print(f'{target}: {target.stat().st_size} bytes')
