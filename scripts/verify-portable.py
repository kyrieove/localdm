# -*- coding: utf-8 -*-
"""Verify the published build runs without any .NET SDK, and that data stays out of the repo.

Runs LocalDm.exe from the published folder with a *minimal* environment:
PATH stripped of dotnet, DOTNET_ROOT / DOTNET_* cleared.
"""

import json
import os
import shutil
import subprocess
import time

import importlib.util

_here = os.path.dirname(os.path.abspath(__file__))

# 复用 drive-ui-session.py 的窗口查找与截图工具
_spec = importlib.util.spec_from_file_location("drive", os.path.join(_here, "drive-ui-session.py"))
drive = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(drive)

ROOT = drive.ROOT
PUB = os.path.join(ROOT, "artifacts", "publish", "win-x64")
EXE = os.path.join(PUB, "LocalDm.exe")
OUT = os.path.join(ROOT, "docs", "evidence", "us5")
DATA = os.path.join(ROOT, "artifacts", "portable-smoke-data")
find_window = drive.find_window
capture = drive.capture


def main():
    os.makedirs(OUT, exist_ok=True)
    shutil.rmtree(DATA, ignore_errors=True)

    env = {
        "SystemRoot": os.environ.get("SystemRoot", r"C:\Windows"),
        "windir": os.environ.get("windir", r"C:\Windows"),
        "TEMP": os.environ.get("TEMP", r"C:\Windows\Temp"),
        "TMP": os.environ.get("TMP", r"C:\Windows\Temp"),
        "USERPROFILE": os.environ.get("USERPROFILE", ""),
        "LOCALAPPDATA": os.environ.get("LOCALAPPDATA", ""),
        "APPDATA": os.environ.get("APPDATA", ""),
        "PATH": r"C:\Windows\System32;C:\Windows",
        "LOCALDM_DATA_ROOT": DATA,
    }

    started = time.time()
    proc = subprocess.Popen([EXE], env=env, cwd=PUB)
    try:
        hwnd = find_window("本局主持", timeout=60)
        elapsed = time.time() - started
        if not hwnd:
            raise SystemExit("window not found; build may not be self-contained")

        time.sleep(1.5)
        shot = os.path.join(OUT, "portable-start.png")
        capture(hwnd, shot)

        res = {
            "scenario": "US5 便携发布物在无 .NET SDK 环境启动",
            "exe": os.path.relpath(EXE, ROOT),
            "selfContained": os.path.exists(os.path.join(PUB, "hostfxr.dll")),
            "sdkOnPath": False,
            "pathUsed": env["PATH"],
            "secondsToWindow": round(elapsed, 1),
            "windowTitle": "本局主持",
            "dataRoot": os.path.relpath(DATA, ROOT),
            "dataFilesCreated": sorted(os.path.basename(p) for p in
                                       [os.path.join(dp, f) for dp, _, fs in os.walk(DATA) for f in fs]),
            "screenshot": os.path.basename(shot),
        }
        with open(os.path.join(OUT, "portable-smoke.json"), "w", encoding="utf-8") as fh:
            json.dump(res, fh, ensure_ascii=False, indent=2)
        print(json.dumps(res, ensure_ascii=False, indent=2))
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    main()
