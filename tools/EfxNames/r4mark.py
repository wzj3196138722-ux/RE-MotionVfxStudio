"""RE4 field names -> file offsets, by running re4.exe natively (read-only, nothing written outside memory).

    python -I -X utf8 r4mark.py <TypeName> [<TypeName> ...]     -> r4m_<TypeName>.json, one child process per type

Per type (vtable from re4_parsers.json, payloads from ../raw_re4.json):
  parser  slot 18 (this, block, &name, &value): for every name string, a zeroed block, value "0.375" (and pair forms);
          the words the call writes are that name's block offsets.
  loader  slot 5 (attr, stream, ctx) on UniqueID + corpus payload, then builder slot 15 (attr, block, ctx):
          each 4-byte payload word in turn is replaced by a marker; where the marker shows up in the block gives
          file offset -> block offset. Other block words that change are counted as converted / flag reads.
"""
import sys, ctypes, json, os, struct, subprocess

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from paths import OUT as H, RE4NATIVE, REPO
RN = RE4NATIVE
BLK = 0x1000


def child(kind):
    sys.path.insert(0, RN)
    import re4native as rn
    import emitter as em
    img = rn.load()
    em.install_runtime(img)
    B = rn.IMAGE_BASE
    # GPU type builders register with renderer managers that are not set up here (null manager objects):
    # 45ecd40 (register; 1 = done) locks [rcx+0x10]; 4657b10 is reached from the GpuMesh builder and answers 0.
    em.stub(img, 0x45ECD40, "renderer manager register", lambda *a: 1)
    em.stub(img, 0x4657B10, "gpu mesh resource")
    parsers = json.load(open(os.path.join(H, 're4_parsers.json')))
    tid, info = next((k, v) for k, v in parsers.items() if v['kind'] == kind)
    vt = int(info['vt'], 16)
    native = json.load(open(os.path.join(H, 're4_native_vt.json')))[tid]
    slot = lambda k: rn.u64(B + vt + 8 * k) - B
    fac = img.function(native['factory'], ctypes.c_void_p)
    load = img.function(slot(5), ctypes.c_uint8, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p)
    build = img.function(slot(15), ctypes.c_uint64, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p)
    parse = img.function(slot(18), ctypes.c_uint8, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p)
    ctx = img.alloc(0x1000)
    import world
    rn.set_u64(ctx, world.make_file(img))
    blk = img.alloc(BLK)
    # loaders size per-particle tables from the Spawn already loaded (ctx + 8 + 8 * 16 -> MaxParticles):
    # give them a real one (first corpus Spawn payload)
    raw_all = json.load(open(os.path.join(H, 'raw_re4.json')))
    sp_tid = next(k for k, v in parsers.items() if v['kind'] == 'Spawn')
    sp_vt = int(parsers[sp_tid]['vt'], 16)
    sp = img.call(img.function(native[sp_tid]['factory'] if sp_tid == tid else
                               json.load(open(os.path.join(H, 're4_native_vt.json')))[sp_tid]['factory'], ctypes.c_void_p))
    sp_load = img.function(rn.u64(B + sp_vt + 8 * 5) - B, ctypes.c_uint8, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p)
    sps = rn.ReadStream(img, b'\x11\x22\x33\x44' + bytes.fromhex(next(iter(raw_all['Spawn']))), 0)
    assert img.call(sp_load, sp, sps.handle, ctx)
    rn.set_u64(ctx + 8 + 8 * int(sp_tid), sp)

    def rstr(s):
        b = s.encode() + b'\0'
        buf = img.alloc(len(b) + 16)
        ctypes.memmove(buf, b, len(b))
        o = img.alloc(0x20)
        rn.set_u64(o, buf)
        rn.set_u32(o + 0x18, len(s))
        rn.set_u32(o + 0x1c, max(0x18, len(b)))
        return o

    out = dict(kind=kind, id=int(tid), vt=hex(vt), parser={}, words={}, runs=0)
    # parser: name -> block offsets written
    attr = img.call(fac)
    def probe(name, val):
        ctypes.memset(blk, 0, BLK)
        try:
            ok = img.call(parse, attr, blk, rstr(name), rstr(val))
        except Exception:
            ok = None
        return ok, ctypes.string_at(blk, BLK)

    # a name's value slots are the words that differ between two different values; words written the same
    # either way (a "was given" flag, say) are kept apart as side writes
    VALS = [('0.375', '0.625'), ('0.375,0.625', '0.125,0.875'), ('0.375 0.625', '0.125 0.875'),
            ('0.375,0.625,0.875', '0.125,0.25,0.75'), ('0.375 0.625 0.875', '0.125 0.25 0.75'), ('3', '5'),
            ('1', '0')]
    out['parser_side'] = {}
    for name in info.get('strs_all') or info['strs']:
        best = None
        for v1, v2 in VALS:
            ok1, b1 = probe(name, v1)
            ok2, b2 = probe(name, v2)
            if not (ok1 and ok2):
                continue
            slots = [o for o in range(0, BLK, 4) if b1[o:o + 4] != b2[o:o + 4]]
            side = [o for o in range(0, BLK, 4) if b1[o:o + 4] == b2[o:o + 4] and b1[o:o + 4] != bytes(4)]
            if best is None or len(slots) > len(best[0]):
                best = (slots, side)
        if best and best[0]:
            out['parser'][name] = best[0]
            if best[1]:
                out['parser_side'][name] = best[1]
    if os.environ.get('R4PARSER_ONLY'):
        old = json.load(open(os.path.join(H, f'r4m_{kind}.json')))
        old['parser'], old['parser_side'] = out['parser'], out['parser_side']
        json.dump(old, open(os.path.join(H, f'r4m_{kind}.json'), 'w'), indent=0)
        print('RESULT', kind, 'names', len(out['parser']), 'parser only')
        return
    # loader + builder markers
    payloads = [bytes.fromhex(h) for h in json.load(open(os.path.join(H, 'raw_re4.json'))).get(kind, {})]

    ATTR_MODE = os.environ.get('R4MODE') == 'attr'
    attr2blk = {}
    if ATTR_MODE:
        # GPU types: the builder registers renderer resources that are not set up here, so the
        # attribute -> block copy is read statically from the builder (fmap.builder_map)
        sys.path.insert(0, H)
        import wk, fmap
        Rp = wk.get('re4')
        for blkoff, attroff in fmap.builder_map(Rp, slot(15)).items():
            attr2blk.setdefault(attroff, []).append(blkoff)
        out['mode'] = 'attr+static builder'
    cursor = em._state["cursor"]
    mark = rn.u64(cursor)

    def run(p):
        # every run starts from the same arena position, so repeated loaders do not exhaust the arena
        try:
            a = img.call(fac)
            st = rn.ReadStream(img, b'\x11\x22\x33\x44' + p, 0)
            if not img.call(load, a, st.handle, ctx):
                return None, st.pos
            if kind == 'TypeMesh' and not rn.u64(a + 0xC8):   # material handle from the .mdf2 (see emitter.Entry)
                rn.set_u64(a + 0xC8, img.alloc(0x100))
            if ATTR_MODE:   # builder not run: the attribute object itself, mapped to the block statically below
                return ctypes.string_at(a, BLK), st.pos
            ctypes.memset(blk, 0, BLK)
            img.call(build, a, blk, ctx)
            return ctypes.string_at(blk, BLK), st.pos
        finally:
            end = min(rn.u64(cursor), em.ARENA_BASE + em.ARENA_SIZE)
            if end > mark:
                ctypes.memset(mark, 0, end - mark)
            rn.set_u64(cursor, mark)

    for p in payloads[:6]:
        base, used = run(p)
        if base is None:
            out.setdefault('load_fail', 0)
            out['load_fail'] += 1
            continue
        out['runs'] += 1
        out['payload'] = len(p)
        out['consumed'] = used
        for k in range(0, len(p) - 3, 4):
            m = struct.pack('<f', 1000.0 + k + 0.25)
            q = bytearray(p)
            q[k:k + 4] = m
            try:
                b, u = run(bytes(q))
            except Exception:
                b, u = None, -1
            w = out['words'].setdefault(str(k), dict(blocks={}, changed=0, fail=0))
            if b is None or u != used:
                w['fail'] += 1
                continue
            hits = [o for o in range(0, BLK - 3) if b[o:o + 4] == m]
            if ATTR_MODE:
                w.setdefault('attr', {})
                for o in hits:
                    w['attr'][hex(o)] = w['attr'].get(hex(o), 0) + 1
                hits = [blkoff for o in hits for blkoff in attr2blk.get(o, [])]
            for o in hits:
                w['blocks'][hex(o)] = w['blocks'].get(hex(o), 0) + 1
            if not hits and b != base:
                w['changed'] += 1
                for o in range(0, BLK, 4):
                    if b[o:o + 4] != base[o:o + 4]:
                        cb = w.setdefault('changed_blocks', {})
                        cb[hex(o)] = cb.get(hex(o), 0) + 1
    json.dump(out, open(os.path.join(H, f'r4m_{kind}.json'), 'w'), indent=0)
    print('RESULT', kind, 'names', len(out['parser']), 'runs', out['runs'], 'words', len(out['words']))


if __name__ == '__main__':
    if sys.argv[1] == 'child':
        child(sys.argv[2])
        sys.exit(0)
    for kind in sys.argv[1:]:
        p = subprocess.run([sys.executable, '-I', '-X', 'utf8', os.path.abspath(__file__), 'child', kind],
                           capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=900)
        line = [l for l in p.stdout.splitlines() if l.startswith('RESULT')]
        print(line[-1] if line else f'{kind}: FAILED rc={p.returncode} {p.stderr.strip()[-600:]}')
