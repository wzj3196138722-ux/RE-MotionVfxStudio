"""Spawn ParticleInterval on re4.exe: the slot groups it makes.

    python -I -X utf8 -u exp_particle_interval.py

The loader 3d15720 keeps q = MaxParticles / ParticleInterval (1 when ParticleInterval is 0) at attribute +0x2c; the
emitter constructor 3d06440 copies it to E+0x104; the slot claim 3d15e60 reads it. With q > 1 the particle pool is
ParticleInterval groups of q consecutive slots: only a group's first slot (slot % q == 0) is claimed from the batch;
the next slot of a group is claimed once the slot before it is alive and past its spawn tick, so a group fills one
slot per tick. Every RE4 corpus Spawn has ParticleInterval = MaxParticles (q = 1, no groups).

The test overwrites q (attribute +0x2c) on a TypeMesh entry with MaxParticles 10 and compares the alive slots of the
first ticks with that rule, for q = 1, 2, 5.
"""
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
STM = r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm"
PATH = STM + r"\_anotherorder\vfx\effecteditor\efd_character\efd_ao_chc0\efd_0015_ao_chc0_damage_1000_dead_crushe_0000.efx.3539837"
OFF = 7043            # Spawn MaxParticles 10, ParticleInterval 10, SpawnNum 10, one batch
SPAWN = 16
TICKS = 6


def run(q):
    import emitter as em
    import re4native as rn
    img = rn.load()
    em.install_runtime(img)
    e = em.Entry(img, PATH, OFF)
    sp = next(a for a in e.attrs if a["type"] == SPAWN)
    before = rn.u32(sp["attr"] + 0x2C)
    rn.set_u32(sp["attr"] + 0x2C, q)
    m = em.Emitter(img, e)
    m.play()
    rows = []
    for _ in range(TICKS):
        m.tick(1 / 60)
        rows.append(sorted(p["slot"] for p in m.state()))
    return dict(before=before, max=m.max_particles, alive=rows)


def expected(q, cap, ticks, first_tick):
    """Slots alive per tick: group heads at the first batch, then one more slot per group per tick."""
    out = []
    for t in range(ticks):
        k = t - first_tick + 1                   # ticks since the batch, the batch tick being 1
        if k < 1:
            out.append([])
            continue
        out.append(sorted(s for s in range(cap) if s % q < min(k, q)) if q > 1 else list(range(cap)))
    return out


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "run":
        print("RESULT " + json.dumps(run(int(sys.argv[2]))))
        return
    res = {}
    ok = True
    for q in (1, 2, 5):
        p = subprocess.run([sys.executable, "-I", "-X", "utf8", os.path.abspath(__file__), "run", str(q)],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        line = next((l for l in p.stdout.splitlines() if l.startswith("RESULT ")), None)
        r = json.loads(line[7:])
        first = next(i for i, a in enumerate(r["alive"]) if a)
        exp = expected(q, r["max"], TICKS, first)
        r["match"] = r["alive"] == exp
        ok &= r["match"] and r["before"] == 1
        res[q] = r
        print(json.dumps(dict(q=q, quotient_in_file=r["before"], alive=r["alive"], match=r["match"])))
    print("RESULT " + json.dumps(dict(exp="particle_interval", status="PASS" if ok else "FAIL")))


if __name__ == "__main__":
    main()
