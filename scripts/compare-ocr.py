# -*- coding: utf-8 -*-
"""Compare OCR backends on the same real script page and score them.

Backends:
  rapidocr     lightweight ONNX OCR, no model download beyond its own weights
  windowsocr   built-in Windows.Media.Ocr (zh-Hans-CN)
  mineru-flash MinerU 4.0.1 local flash tier (already installed on this machine)

Scoring is deliberately mechanical so the comparison is reproducible:
  - charCount, cjkCount
  - known tokens that must appear (character names, script title)
  - order check: whether the vertical body text comes out in reading order
Prints everything; writes JSON evidence.
"""

import importlib.util
import json
import os
import re
import subprocess
import sys
import time

_here = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(_here, ".."))
OUT = os.path.join(ROOT, "docs", "evidence", "us2", "ocr-comparison")

# One real script page (role cover). Vertical body text is the hard part.
# 通过命令行传入被测页，避免把真实剧本路径写进仓库
PAGE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "fixtures", "synthetic", "images", "02-clue.png")

# Tokens that any correct reading of this page must contain.
# 期望关键词由调用方给出；默认用合成样本的已知文字
EXPECTED = ["合成", "绞盘", "铭牌", "编号"]

# Substrings that indicate the reading order is actually right.
ORDER_MARKERS = ["女，二十二岁", "黑色的长发", "举止得体", "聪慧"]


def cjk_count(text: str) -> int:
    return len(re.findall(r"[\u4e00-\u9fff]", text))


def normalize(text: str) -> str:
    return re.sub(r"[\s\u3000]+", "", text)


def score(text: str) -> dict:
    flat = normalize(text)
    hits = [t for t in EXPECTED if normalize(t) in flat]
    order = [m for m in ORDER_MARKERS if normalize(m) in flat]
    return {
        "chars": len(text.strip()),
        "cjk": cjk_count(text),
        "expectedHits": hits,
        "expectedTotal": len(EXPECTED),
        "orderHits": order,
        "orderTotal": len(ORDER_MARKERS),
    }


def run_rapidocr(path: str) -> dict:
    from rapidocr import RapidOCR

    engine = RapidOCR()
    started = time.time()
    result = engine(path)
    elapsed = time.time() - started

    # RapidOCR 1.x returns an object with .txts
    texts = getattr(result, "txts", None)
    if texts is None and isinstance(result, tuple):
        texts = [item[1] for item in (result[0] or [])]
    text = "\n".join(texts or [])
    return {"backend": "rapidocr", "elapsed": round(elapsed, 2), "text": text, "score": score(text)}


def run_windows_ocr(path: str) -> dict:
    import asyncio

    import winrt.windows.media.ocr as ocr
    import winrt.windows.graphics.imaging as imaging
    import winrt.windows.storage as storage

    async def go():
        started = time.time()
        file = await storage.StorageFile.get_file_from_path_async(os.path.abspath(path))
        stream = await file.open_async(storage.FileAccessMode.READ)
        decoder = await imaging.BitmapDecoder.create_async(stream)
        bitmap = await decoder.get_software_bitmap_async()
        engine = ocr.OcrEngine.try_create_from_language(
            next(l for l in ocr.OcrEngine.available_recognizer_languages
                 if l.language_tag.startswith("zh")))
        result = await engine.recognize_async(bitmap)
        return result.text, time.time() - started

    text, elapsed = asyncio.run(go())
    return {"backend": "windowsocr", "elapsed": round(elapsed, 2), "text": text, "score": score(text)}


def run_mineru_flash(path: str, home: str, use_cache: bool) -> dict:
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith("MINERU_")}
    env["MINERU_HOME"] = home
    exe = os.path.join(os.environ.get("USERPROFILE", ""), ".local", "bin", "mineru.exe")
    started = time.time()
    proc = subprocess.run(
        [exe, "parse", os.path.abspath(path), "--tier", "flash", "--json"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, timeout=600)
    elapsed = time.time() - started
    if proc.returncode != 0:
        return {"backend": "mineru-flash", "elapsed": round(elapsed, 2), "text": "", "error": proc.stdout[:400]}
    payload = json.loads(proc.stdout)
    content = payload.get("content") or {}
    text = content.get("content") or ""
    text = re.sub(r"<!--.*?-->", "", text).strip()
    if not use_cache:
        pass
    return {
        "backend": "mineru-flash",
        "elapsed": round(elapsed, 2),
        "text": text,
        "score": score(text),
        "cacheHit": (payload.get("parse") or {}).get("cache_hit"),
        "ranges": [r.get("page_range") for r in (content.get("content_ranges") or [])],
    }


def main():
    os.makedirs(OUT, exist_ok=True)
    home = os.path.join(ROOT, ".appdata", "mineru-home")
    use_cache = "--no-cache" not in sys.argv

    results = []
    for name, fn in (
        ("rapidocr", lambda: run_rapidocr(PAGE)),
        ("windowsocr", lambda: run_windows_ocr(PAGE)),
        ("mineru-flash", lambda: run_mineru_flash(PAGE, home, use_cache)),
    ):
        try:
            results.append(fn())
        except Exception as exc:  # noqa: BLE001
            results.append({"backend": name, "error": f"{type(exc).__name__}: {exc}"})

    report = {"page": os.path.relpath(PAGE, ROOT), "expected": EXPECTED, "results": results}
    with open(os.path.join(OUT, "comparison.json"), "w", encoding="utf-8") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)

    print(f"{'backend':14} {'sec':>6} {'chars':>6} {'cjk':>5} {'expect':>7} {'order':>6}")
    for r in results:
        if "error" in r:
            print(f"{r['backend']:14}  ERROR {r['error'][:60]}")
            continue
        s = r["score"]
        print(f"{r['backend']:14} {r['elapsed']:6.2f} {s['chars']:6} {s['cjk']:5} "
              f"{len(s['expectedHits'])}/{s['expectedTotal']:>4} {len(s['orderHits'])}/{s['orderTotal']}")

    for r in results:
        if "error" not in r:
            print(f"\n===== {r['backend']} =====")
            print(r["text"][:1200])


if __name__ == "__main__":
    main()
