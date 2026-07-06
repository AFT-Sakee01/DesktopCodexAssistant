#!/usr/bin/env python
"""Compare render sample PNG trees.

Usage:
    python _validation/Compare-RenderSamples.py <dirA> <dirB> [--threshold 0.001]

The tool recursively matches PNG files by relative path, requires identical file
sets and image sizes, and reports the ratio of pixels whose RGBA value differs.
It prints RESULT: PASS and exits 0 only when every matched image is within the
threshold. Any missing file, size mismatch, load error, or over-threshold diff
prints RESULT: FAIL and exits 1.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image


def collect_pngs(root: Path) -> dict[str, Path]:
    files: dict[str, Path] = {}
    for path in sorted(root.rglob("*.png")):
        if path.is_file():
            key = path.relative_to(root).as_posix()
            files[key] = path
    return files


def diff_ratio(left_path: Path, right_path: Path) -> tuple[float, str | None]:
    try:
        with Image.open(left_path) as left_image, Image.open(right_path) as right_image:
            left = left_image.convert("RGBA")
            right = right_image.convert("RGBA")
            if left.size != right.size:
                return 1.0, f"size mismatch {left.size} != {right.size}"

            left_pixels = left.tobytes()
            right_pixels = right.tobytes()
            total_pixels = left.size[0] * left.size[1]
            if total_pixels == 0:
                return 0.0, None

            different = 0
            for offset in range(0, len(left_pixels), 4):
                if left_pixels[offset:offset + 4] != right_pixels[offset:offset + 4]:
                    different += 1
            return different / float(total_pixels), None
    except Exception as exc:  # noqa: BLE001 - CLI should report load failures as comparison failures.
        return 1.0, f"load error: {exc}"


def main() -> int:
    parser = argparse.ArgumentParser(description="Compare render sample PNG directories.")
    parser.add_argument("dir_a", type=Path)
    parser.add_argument("dir_b", type=Path)
    parser.add_argument("--threshold", type=float, default=0.001)
    args = parser.parse_args()

    dir_a = args.dir_a
    dir_b = args.dir_b
    if not dir_a.is_dir():
        print(f"ERROR: not a directory: {dir_a}")
        print("RESULT: FAIL")
        return 1
    if not dir_b.is_dir():
        print(f"ERROR: not a directory: {dir_b}")
        print("RESULT: FAIL")
        return 1

    left_files = collect_pngs(dir_a)
    right_files = collect_pngs(dir_b)
    fail = False

    missing_right = sorted(set(left_files) - set(right_files))
    missing_left = sorted(set(right_files) - set(left_files))
    for name in missing_right:
        print(f"MISSING in {dir_b}: {name}")
        fail = True
    for name in missing_left:
        print(f"MISSING in {dir_a}: {name}")
        fail = True

    for name in sorted(set(left_files) & set(right_files)):
        ratio, detail = diff_ratio(left_files[name], right_files[name])
        if detail is not None:
            print(f"{name} diff={ratio:.6f} {detail}")
            fail = True
            continue
        print(f"{name} diff={ratio:.6f}")
        if ratio > args.threshold:
            fail = True

    print("RESULT: FAIL" if fail else "RESULT: PASS")
    return 1 if fail else 0


if __name__ == "__main__":
    sys.exit(main())
