# -*- coding: utf-8 -*-
"""Drive the real LocalDm window through one deterministic demo session.

WPF exposes no child HWNDs, so controls are located/read through Windows UI Automation
and activated through UIA Invoke/Toggle patterns (what a screen reader would use).
Every step re-reads the current state so a stale checkbox value cannot cancel a click.
"""

import ctypes
import json
import os
import shutil
import struct
import subprocess
import time
import zlib
import ctypes.wintypes as wt

from pywinauto import Application  # noqa: E402

_here = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(_here, ".."))
OUT = os.path.join(ROOT, "docs", "evidence", "us3")
EXE = os.path.join(ROOT, "src", "LocalDm.App", "bin", "Debug", "net10.0-windows", "LocalDm.exe")

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
SRCCOPY = 0x00CC0020
PW_RENDERFULLCONTENT = 0x00000002


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
        ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


def find_window(title_part, timeout=60):
    end = time.time() + timeout
    while time.time() < end:
        found = []

        @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
        def cb(hwnd, _):
            length = user32.GetWindowTextLengthW(hwnd)
            if length:
                buf = ctypes.create_unicode_buffer(length + 1)
                user32.GetWindowTextW(hwnd, buf, length + 1)
                if title_part in buf.value and user32.IsWindowVisible(hwnd):
                    found.append(hwnd)
            return True

        user32.EnumWindows(cb, 0)
        if found:
            return found[0]
        time.sleep(0.4)
    return None


def capture(hwnd, path):
    """PrintWindow capture: WPF has no child HWNDs, so plain BitBlt would miss the content."""
    rect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w, h = rect.right - rect.left, rect.bottom - rect.top
    hdc = user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    if not user32.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT):
        gdi32.BitBlt(mem, 0, 0, w, h, hdc, 0, 0, SRCCOPY)

    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(hwnd, hdc)

    stride = w * 4
    rows = []
    for y in range(h):
        row = buf.raw[y * stride:(y + 1) * stride]
        rgb = bytearray(w * 3)
        for x in range(w):
            rgb[x * 3] = row[x * 4 + 2]
            rgb[x * 3 + 1] = row[x * 4 + 1]
            rgb[x * 3 + 2] = row[x * 4]
        rows.append(b"\x00" + bytes(rgb))
    raw = b"".join(rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 6))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as fh:
        fh.write(png)


def main():
    os.makedirs(OUT, exist_ok=True)
    data = os.path.join(OUT, ".probe-data")
    shutil.rmtree(data, ignore_errors=True)
    env = dict(os.environ)
    env["LOCALDM_DATA_ROOT"] = data

    proc = subprocess.Popen([EXE], env=env)
    try:
        hwnd = find_window("本局主持")
        time.sleep(1.5)
        app = Application(backend="uia").connect(handle=hwnd, timeout=20)
        win = app.window(handle=hwnd)
        steps = []

        def texts():
            out = []
            for c in win.descendants(control_type="Text"):
                try:
                    out.append(c.window_text())
                except Exception:
                    pass
            return out

        def summary():
            for t in texts():
                if "·" in t and "revision" in t:
                    return t
            return ""

        def shot(name):
            path = os.path.join(OUT, name)
            capture(hwnd, path)
            return os.path.basename(path)

        def invoke(label, ctype="Button", name=None):
            ctrl = win.child_window(title=label, control_type=ctype)
            ctrl.wait("exists enabled", timeout=15)
            if ctype == "CheckBox":
                if ctrl.get_toggle_state() == 0:
                    ctrl.toggle()
            else:
                ctrl.invoke()
            time.sleep(1.0)
            entry = {"step": label, "ok": True, "summary": summary()}
            if name:
                entry["screenshot"] = shot(name)
            steps.append(entry)
            return True

        def ensure_ready():
            cb = win.child_window(title="大家已口头准备好", control_type="CheckBox")
            if cb.get_toggle_state() == 0:
                cb.toggle()
                time.sleep(0.6)

        invoke("主持台", name="00-host-page.png")
        invoke("装载演示剧本", name="01-loaded.png")
        after_load = texts()

        # 未勾选「大家已准备好」：开始必须被拒绝，状态保持 Prepared
        invoke("开始这一局", name="02-start-without-groupready.png")
        after_blocked = texts()
        steps[-1]["rejectedAsExpected"] = "准备" in " ".join(texts()[-1:])

        ensure_ready()
        invoke("开始这一局", name="03-started.png")
        after_start = texts()

        ensure_ready()
        invoke("下一环节（已勾选准备）", name="04-search.png")
        invoke("发放一个材料编号", name="05-material.png")

        ensure_ready()
        invoke("下一环节（已勾选准备）", name="06-discuss.png")
        ensure_ready()
        invoke("下一环节（已勾选准备）", name="07-vote-page.png")

        typed = False
        try:
            win.child_window(auto_id="VoteBox", control_type="Edit").set_edit_text("A=3, B=2")
            typed = True
            time.sleep(0.5)
        except Exception as exc:  # noqa: BLE001
            steps.append({"step": "type votes", "ok": False, "error": str(exc)[:120]})
        steps.append({"step": "type votes", "ok": typed})

        invoke("确认这是大家核对过的汇总", ctype="CheckBox")
        invoke("提交票数汇总", name="08-votes.png")

        ensure_ready()
        invoke("下一环节（已勾选准备）", name="09-reveal-page.png")
        before_reveal = texts()
        invoke("确认这是公开揭秘，且原本条件已满足", ctype="CheckBox")
        invoke("揭秘（需二次确认）", name="10-revealed.png")
        after_reveal = texts()
        invoke("进入复盘", name="11-completed.png")
        final_texts = texts()

        report = {
            "scenario": "US3 真实窗口完整局（演示剧本；UI Automation Invoke/Toggle 驱动）",
            "automationBackend": "Windows UI Automation (pywinauto uia)",
            "exe": os.path.relpath(EXE, ROOT),
            "dataRoot": os.path.relpath(data, ROOT),
            "steps": steps,
            "observations": {
                "afterLoad": after_load,
                "afterStartWithoutGroupReady": after_blocked,
                "afterStart": after_start,
                "beforeReveal": before_reveal,
                "afterReveal": after_reveal,
                "final": final_texts,
            },
        }
        with open(os.path.join(OUT, "ui-session-steps.json"), "w", encoding="utf-8") as fh:
            json.dump(report, fh, ensure_ascii=False, indent=2)
        print("ok steps:", sum(1 for s in steps if s.get("ok")), "/", len(steps))
        print("final summary:", summary())
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    main()
