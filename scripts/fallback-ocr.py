# -*- coding: utf-8 -*-
"""Fallback OCR for pages MinerU flash returns no text for.

Runs RapidOCR (ONNX, local) on a single image and prints the recognized text on stdout.
Used only as a fallback: it is fast but its reading order is unreliable on
vertical / rotated layouts, so the app labels these spans with the fallback name.

Usage: python scripts/fallback-ocr.py --image <path>
"""

import argparse
import sys


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", required=True)
    args = parser.parse_args()

    import logging

    from rapidocr import RapidOCR

    logging.getLogger("RapidOCR").setLevel(logging.ERROR)

    engine = RapidOCR()
    result = engine(args.image)

    txts = getattr(result, "txts", None)
    if txts is None and isinstance(result, tuple):
        txts = [item[1] for item in (result[0] or [])]

    lines = [t.strip() for t in (txts or []) if t and t.strip()]
    if not lines:
        return 3

    sys.stdout.write("\n".join(lines))
    return 0


if __name__ == "__main__":
    sys.exit(main())
