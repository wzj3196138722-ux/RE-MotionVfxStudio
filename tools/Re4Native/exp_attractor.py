"""Attractor (RE4 layout) on re4.exe: which word is strength, reversal scale, reversal radius and resistance.

    python -I -X utf8 -u exp_attractor.py

Two readings of the 8 payload words: re4.exe's text parser and AttractorExpression slots give
  Flags | AttractPosition xyz | ForceScale | ReversalForceScale | ReversalDistance | ForceResist
(EFXAttributeAttractorRE4); the older shared struct read +16..+24 as one Vector3 "AttractPositionLocal"
(a local target offset) and +28 as ForceStatic. Each variant patches the Attractor of a temporary copy of a
corpus CPU Billboard3D entry (Flags 0, AttractPosition (0, 3, 0)) and runs the emitter natively for 30 ticks.
Measured: mean particle height and mean speed against the ForceScale = 0 run.

  pull     ForceScale 20                                 particles move up toward the target
  rev_y    as pull, +20 = 5 (ReversalDistance 0)         same as pull: with a radius of 0 the scale is unused;
                                                         a Vector3 offset would move the target in y instead
  reverse  as pull, ReversalDistance 100                 everything is inside the radius: pushed down
  reverse3 as reverse, ReversalForceScale 3              pushed down about three times as hard
  resist   as pull, ForceResist 5                        slower than pull
"""
import json
import os
import struct
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
STM = r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm"
SRC = STM + r"\_anotherorder\vfx\effecteditor\efd_stage\efd_st60\efd_st60_850\efd_0015_st60_850_0001.efx.3539837"
OFF = 913
ATTRACTOR = 192
TARGET = (0.0, 3.0, 0.0)
VARIANTS = {
    'off': (0, 1, 0, 0),
    'pull': (20, 1, 0, 0),
    'rev_y': (20, 5, 0, 0),
    'reverse': (20, 1, 100, 0),
    'reverse3': (20, 3, 100, 0),
    'resist': (20, 1, 0, 5),
}


def payload_offset(img, path):
    """File offset of the Attractor payload (after its UniqueID) in the entry at OFF."""
    import emitter as em
    e = em.Entry(img, path, OFF)
    pos = OFF + 16
    for a in e.attrs:
        pos += 4                       # type id
        if a['type'] == ATTRACTOR:
            return pos + 4             # skip UniqueID
        pos += a['bytes']
    raise RuntimeError('no Attractor in the entry')


def run(name):
    import emitter as em
    import re4native as rn
    img = rn.load()
    em.install_runtime(img)
    data = bytearray(open(SRC, 'rb').read())
    p = payload_offset(img, SRC)
    force, rev_scale, rev_dist, resist = VARIANTS[name]
    struct.pack_into('<I3f4f', data, p, 0, *TARGET, force, rev_scale, rev_dist, resist)
    path = os.path.join(tempfile.gettempdir(), 're4native-attractor', f'{name}.efx.3539837')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, 'wb').write(data)
    m = em.Emitter(img, em.Entry(img, path, OFF))
    m.play()
    for _ in range(30):
        m.tick(1 / 60)
    st = [s for s in m.state()]
    n = len(st)
    y = sum(s['pos'][1] for s in st) / n
    v = sum(sum(c * c for c in s['vel']) ** 0.5 for s in st) / n
    return dict(n=n, y=y, speed=v)


def main():
    if len(sys.argv) > 2 and sys.argv[1] == 'run':
        print('RESULT ' + json.dumps(run(sys.argv[2])))
        return
    res = {}
    for name in VARIANTS:
        p = subprocess.run([sys.executable, '-I', '-X', 'utf8', os.path.abspath(__file__), 'run', name],
                           capture_output=True, text=True, encoding='utf-8', errors='replace')
        line = next((l for l in p.stdout.splitlines() if l.startswith('RESULT ')), None)
        if not line:
            print(name, 'FAILED', p.stderr[-800:])
            return
        res[name] = json.loads(line[7:])
    o = res['off']
    for k, r in res.items():
        print(json.dumps(dict(variant=k, particles=r['n'], dy=round(r['y'] - o['y'], 4), speed=round(r['speed'], 4))))
    ok = (res['pull']['y'] > o['y'] and abs(res['rev_y']['y'] - res['pull']['y']) < 1e-6
          and res['reverse']['y'] < o['y'] and (o['y'] - res['reverse3']['y']) > 2 * (o['y'] - res['reverse']['y'])
          and res['resist']['speed'] < res['pull']['speed'])
    print('RESULT ' + json.dumps(dict(exp='attractor', status='PASS' if ok else 'FAIL')))


if __name__ == '__main__':
    main()
