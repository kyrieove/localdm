# -*- coding: utf-8 -*-
"""OCR a real script page with the built-in Windows.Media.Ocr engine and report quality.

No model download, no admin rights, no third-party OCR package: only the OS engine
plus tiny WinRT bindings. Prints timing and the recognized text so quality can be judged.
"""

import asyncio
import os
import sys
import time

import winrt.windows.media.ocr as ocr
import winrt.windows.graphics.imaging as imaging
import winrt.windows.storage as storage


async def ocr_image(path: str) -> tuple[str, float]:
    started = time.time()
    file = await storage.StorageFile.get_file_from_path_async(os.path.abspath(path))
    stream = await file.open_async(storage.FileAccessMode.READ)
    decoder = await imaging.BitmapDecoder.create_async(stream)
    bitmap = await decoder.get_software_bitmap_async()

    engine = ocr.OcrEngine.try_create_from_language(
        next(l for l in ocr.OcrEngine.available_recognizer_languages
             if l.language_tag.startswith("zh"))
    )
    result = await engine.recognize_async(bitmap)
    return result.text, time.time() - started


def main():
    if len(sys.argv) < 2:
        raise SystemExit("usage: probe-windows-ocr.py <image.jpg>")

    path = sys.argv[1]
    text, elapsed = asyncio.run(ocr_image(path))
    print(f"image: {os.path.basename(path)} | {os.path.getsize(path)} bytes")
    print(f"engine: Windows.Media.Ocr (zh-Hans-CN) | {elapsed:.2f}s")
    print(f"chars: {len(text)}")
    print("--- text ---")
    print(text)


if __name__ == "__main__":
    main()
