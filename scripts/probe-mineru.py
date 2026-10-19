# -*- coding: utf-8 -*-
"""Probe the real MinerU installation for the US2 contract (T025/T027/T036).

Read-only: runs `mineru version`, `parse --help`, `server status`. Does not parse
any real script material and does not change global MinerU configuration.

Usage: python scripts/probe-mineru.py [--out docs/evidence/us2]
"""
import json
import os
import subprocess
import sys

EXE = os.path.join(os.environ.get("USERPROFILE", ""), ".local", "bin", "mineru.exe")


def run(args, timeout=120):
    """Run mineru with MERINU_* overrides cleared except our own MINERU_HOME."""
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith("MINERU_")}
    app_home = os.path.join(os.environ.get("LOCALAPPDATA", "."), "LocalDm", "mineru-home")
    env["MINERU_HOME"] = app_home
    try:
        proc = subprocess.run(
            [EXE] + args,
            capture_output=True,
            timeout=timeout,
            env=env,
            encoding="utf-8",
            errors="replace",
        )
        return {"args": args, "exitCode": proc.returncode,
                "stdout": (proc.stdout or "")[:4000], "stderr": (proc.stderr or "")[:2000]}
    except Exception as exc:  # noqa: BLE001 - probe reports everything
        return {"args": args, "error": "%s: %s" % (type(exc).__name__, exc)}


def main():
    out_dir = "docs/evidence/us2"
    if "--out" in sys.argv:
        out_dir = sys.argv[sys.argv.index("--out") + 1]
    os.makedirs(out_dir, exist_ok=True)

    result = {
        "executablePresent": os.path.exists(EXE),
        "executable": EXE if os.path.exists(EXE) else None,
        "appOwnedHome": os.path.join(os.environ.get("LOCALAPPDATA", "."), "LocalDm", "mineru-home"),
        "version": run(["version"]),
        "parseHelp": run(["parse", "--help"]),
        "serverStatus": run(["server", "status"]),
    }

    path = os.path.join(out_dir, "mineru-probe.json")
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(result, fh, ensure_ascii=False, indent=2)
    print("wrote", path)
    print("version exit:", result["version"].get("exitCode"))
    print("server status exit:", result["serverStatus"].get("exitCode"))
    print(result["serverStatus"].get("stdout", "")[:600])
    print(result["serverStatus"].get("stderr", "")[:600])


if __name__ == "__main__":
    main()
