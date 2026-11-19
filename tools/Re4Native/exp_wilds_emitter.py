"""Wilds emitter scenarios: load a corpus root entry, register its steps, construct E, tick it.

    python -I -X utf8 -u exp_wilds_emitter.py                     the built-in entries, one child each
    python -I -X utf8 -u exp_wilds_emitter.py run <path> <offset> [ticks]
    python -I -X utf8 -u exp_wilds_emitter.py sweep <entries.json> [count]

entries.json: a list of {"path", "start", "types"} (start = file offset of the entry header). The
built-in entries are under MHWILDS_EXTRACT (default: the extracted natives\\STM\\Art\\VFX folder).
Reports, per entry: step lists, capacity, alive slots per tick, which steps ran, and the fault if any.
"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.environ.get("MHWILDS_EXTRACT",
                      r"E:\Program\Steam\steamapps\common\MonsterHunterWilds\MHWILDS_EXTRACT\natives\STM\Art\VFX")
BUILTIN = [   # (relative path, entry offset): Billboard3D entries whose attribute types all load natively
    (r"EffectEditor\Common\slinger\11_pl_slinger_055_jimen.efx.5571972", 15735),
    (r"EffectEditor\Common\slinger\11_pl_slinger_053.efx.5571972", 6635),
    (r"EffectEditor\Common\slinger\11_pl_slinger_012.efx.5571972", 906),
    (r"EffectEditor\Enemy\em0002\11_em0002_00_800.efx.5571972", 173),
]
TRANSFORM3D = 10   # Wilds type id of Transform3D: its presence makes an entry 3D (as RE4's type 9)


def run(path, off, ticks=30):
    import ctypes
    sys.path.insert(0, HERE)
    import mhwildsnative as wn
    import mhwilds_emitter as me
    img = wn.load()
    me.install_runtime(img)
    out = dict(exp="wilds_emitter", entry=f"{os.path.basename(path)}@{off}")
    try:
        entry = me.Entry(img, path, off)
        out["types"] = [a["type"] for a in entry.attrs]
        entry.set_3d(TRANSFORM3D in out["types"])
        A, Bl = entry.register_steps(img)
        out["steps_A"], out["steps_B"] = A, Bl
        # count step calls: wrap every step table entry with a counting trampoline
        hits = {}
        T = wn.IMAGE_BASE + wn.STEP_TABLE_RVA
        F4 = ctypes.CFUNCTYPE(ctypes.c_uint64, *[ctypes.c_uint64] * 4)
        for k in sorted(set(A) | set(Bl)):
            orig = wn.u64(T + 8 * k)
            if not orig:
                continue
            def wrap(k=k, fn=F4(orig)):
                def cb(a, b, c, d):
                    hits[k] = hits.get(k, 0) + 1
                    return fn(a, b, c, d)
                return cb
            w = img.keep(F4(wrap()))
            wn.set_u64(T + 8 * k, ctypes.cast(w, ctypes.c_void_p).value)
        em = me.Emitter(img, entry)
        out["capacity"] = em.capacity
        em.play()
        alive = []
        for _ in range(ticks):
            em.tick(1 / 60)
            alive.append(len(em.alive()))
        out["alive"] = alive
        out["steps_run"] = dict(sorted(hits.items()))
        out["status"] = "RAN" if max(alive or [0]) else "RAN_EMPTY"
    except Exception as e:
        out["status"] = "FAULT"
        out["error"] = str(e)[:300]
    print("RESULT " + json.dumps(out))
    return out


def child(path, off, ticks):
    p = subprocess.run([sys.executable, "-I", "-X", "utf8", "-u", os.path.abspath(__file__), "run", path, str(off), str(ticks)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    for line in p.stdout.splitlines():
        if line.startswith("RESULT "):
            return json.loads(line[7:])
    return dict(exp="wilds_emitter", entry=f"{os.path.basename(path)}@{off}", status="CRASH",
                exit=hex(p.returncode & 0xFFFFFFFF), tail=(p.stdout + p.stderr)[-300:])


def main():
    a = sys.argv[1:]
    if a and a[0] == "run":
        run(a[1], int(a[2]), int(a[3]) if len(a) > 3 else 30)
        return
    if a and a[0] == "sweep":
        rows = json.load(open(a[1], encoding="utf-8"))[: int(a[2]) if len(a) > 2 else None]
        counts = {}
        for x in rows:
            r = child(x["path"], x["start"], 30)
            counts[r["status"]] = counts.get(r["status"], 0) + 1
            print(json.dumps(r, ensure_ascii=False))
        print("RESULT " + json.dumps(dict(exp="wilds_emitter_sweep", counts=counts)))
        return
    for rel, off in BUILTIN:
        r = child(os.path.join(ROOT, rel), off, 30)
        print("RESULT " + json.dumps(r, ensure_ascii=False))


if __name__ == "__main__":
    main()
