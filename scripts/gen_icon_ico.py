# -*- coding: utf-8 -*-
"""
从 icons/icon_eframe.png 生成多尺寸 icon.ico，用于 NSIS 安装包图标。

输入: icons/icon_eframe.png
输出: icons/icon.ico （包含 16/32/48/64/128/256 多尺寸）
"""
import sys
from pathlib import Path
from PIL import Image

# 项目根目录（脚本位于 scripts/ 下）
ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "icons" / "icon_eframe.png"
DST = ROOT / "icons" / "icon.ico"

if not SRC.exists():
    print(f"[ERROR] 源文件不存在: {SRC}", file=sys.stderr)
    sys.exit(1)

# NSIS / Windows Shell 推荐的图标尺寸
SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]

img = Image.open(SRC).convert("RGBA")
print(f"[INFO] 源 PNG: {SRC} ({img.size[0]}x{img.size[1]})")

# Pillow 的 save 会自动为每个尺寸生成一帧
img.save(DST, format="ICO", sizes=SIZES)
print(f"[OK]   已生成 ICO: {DST}")

# 验证生成的 ICO 包含的尺寸
with Image.open(DST) as ico:
    print(f"[INFO] ICO 尺寸列表: {ico.info.get('sizes', 'unknown')}")
