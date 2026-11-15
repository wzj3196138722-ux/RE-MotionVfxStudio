"""Run every experiment in its own child process and collect the RESULT lines.

    python -I -X utf8 -u run_all.py [out_dir] [exp_name ...]

out_dir defaults to %TEMP%\re4native.

A child that dies (fast fail 0xC0000409, unhandled access violation) is reported with its exit code;
the others still run. Each child's full output goes to <out_dir>/<exp>.txt.
"""
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
EXPERIMENTS = ["exp_map", "exp_scaleanim", "exp_blink", "exp_loader", "exp_rng", "exp_pipeline", "scan_loops"]


def run(name: str, out_dir: Path) -> dict:
    p = subprocess.run([sys.executable, "-I", "-X", "utf8", "-u", str(HERE / f"{name}.py")],
                       cwd=str(HERE), capture_output=True, text=True, encoding="utf-8", errors="replace")
    text = p.stdout + ("\n--- stderr ---\n" + p.stderr if p.stderr.strip() else "")
    (out_dir / f"{name}.txt").write_text(text, encoding="utf-8")
    results = [json.loads(l[7:]) for l in p.stdout.splitlines() if l.startswith("RESULT ")]
    if p.returncode != 0:
        results.append({"exp": name, "status": "CRASH", "exit": hex(p.returncode & 0xFFFFFFFF)})
    return {"exp": name, "exit": p.returncode, "results": results}


def main():
    args = sys.argv[1:]
    out_dir = Path(args.pop(0)) if args and not args[0].startswith("exp_") and not args[0].startswith("scan_") else Path(tempfile.gettempdir()) / "re4native"
    out_dir.mkdir(parents=True, exist_ok=True)
    names = args or EXPERIMENTS
    summary = [run(n, out_dir) for n in names]
    for s in summary:
        for r in s["results"]:
            print(json.dumps(r, ensure_ascii=False))
    (out_dir / "summary.json").write_text(json.dumps(summary, indent=1, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
