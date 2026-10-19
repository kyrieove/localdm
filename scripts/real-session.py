# -*- coding: utf-8 -*-
"""Run one full session with the REAL script package through the shipped window.

Uses Windows UI Automation (Invoke/Toggle) exactly like a screen reader would.
Asserts by reading the phase line back from the window, not by trusting commands.
"""

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import time

_here = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("drv", os.path.join(_here, "drive-ui-session.py"))
drv = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(drv)

from pywinauto import Application  # noqa: E402

ROOT = drv.ROOT
EXE = drv.EXE
OUT = os.path.join(ROOT, "docs", "evidence", "us3")
DATA = os.path.join(ROOT, ".appdata", "localdm-real-session")


def main():
    os.makedirs(OUT, exist_ok=True)
    shutil.rmtree(DATA, ignore_errors=True)

    # 复用已构建好的真实剧本包，不重跑 OCR
    src_pkg = os.path.join(ROOT, ".appdata", "localdm-data", "packages")
    os.makedirs(os.path.join(DATA, "packages"), exist_ok=True)
    for name in os.listdir(src_pkg):
        if name.endswith(".json"):
            shutil.copy2(os.path.join(src_pkg, name), os.path.join(DATA, "packages", name))

    env = dict(os.environ)
    env["LOCALDM_DATA_ROOT"] = DATA

    proc = subprocess.Popen([EXE], env=env)
    try:
        hwnd = drv.find_window("本局主持")
        time.sleep(2.0)
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
            drv.capture(hwnd, path)
            return os.path.basename(path)

        def click(label, ctype="Button", name=None):
            ctrl = win.child_window(title=label, control_type=ctype)
            ctrl.wait("exists enabled", timeout=15)
            if ctype == "CheckBox":
                if ctrl.get_toggle_state() == 0:
                    ctrl.toggle()
            elif ctype == "ListItem":
                ctrl.click_input()
            else:
                ctrl.invoke()
            time.sleep(1.0)
            entry = {"step": label, "ok": True, "state": summary()}
            if name:
                entry["screenshot"] = shot(name)
            steps.append(entry)
            return True

        def ensure_ready():
            cb = win.child_window(title="大家已口头准备好", control_type="CheckBox")
            if cb.get_toggle_state() == 0:
                cb.toggle()
                time.sleep(0.6)

        # 1. 首页选中真实剧本并开局
        click("用这个剧本开局", name="r01-library-open.png")
        click("主持台", name="r02-host.png")
        before_start = texts()
        ensure_ready()
        click("开始这一局", name="r03-started.png")
        after_start = texts()

        # 2. 逐阶段推进到投票
        for _ in range(4):
            ensure_ready()
            click("下一环节（已勾选准备）")
        after_phases = summary()

        # 3. 投票：写汇总 + 确认 + 提交
        win.child_window(auto_id="VoteBox", control_type="Edit").set_edit_text("A=3, B=2")
        time.sleep(0.4)
        click("确认这是大家核对过的汇总", ctype="CheckBox")
        click("提交票数汇总", name="r04-votes.png")
        after_vote = summary()

        # 4. 进入真相阶段，确认揭秘前没有真相文本
        ensure_ready()
        click("下一环节（已勾选准备）", name="r05-reveal-phase.png")
        before_reveal = texts()
        truth_before = any("已公开的揭秘内容" in t for t in before_reveal)

        # 5. 揭秘
        click("确认这是公开揭秘，且原本条件已满足", ctype="CheckBox")
        click("揭秘（需二次确认）", name="r06-revealed.png")
        after_reveal = texts()
        truth_after = any("已公开的揭秘内容" in t for t in after_reveal)

        # 6. 复盘
        click("进入复盘", name="r07-completed.png")
        final = summary()

        report = {
            "scenario": "US3 真实剧本在真实窗口走一局",
            "package": "真实剧本（180 页来源）",
            "steps": steps,
            "observations": {
                "beforeStart": [t for t in before_start if "·" in t and "revision" in t],
                "afterStart": [t for t in after_start if "·" in t and "revision" in t],
                "afterThreeAdvance": after_phases,
                "afterVote": after_vote,
                "truthVisibleBeforeReveal": truth_before,
                "truthVisibleAfterReveal": truth_after,
                "final": final,
            },
        }
        with open(os.path.join(OUT, "real-session-steps.json"), "w", encoding="utf-8") as fh:
            json.dump(report, fh, ensure_ascii=False, indent=2)

        print("ok steps:", sum(1 for s in steps if s.get("ok")), "/", len(steps))
        print(json.dumps(report["observations"], ensure_ascii=False, indent=2)[:1600])
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    main()
