"""Build a real EFX emitter with MonsterHunterWilds.exe's own code and tick it (the Wilds counterpart of
emitter.py; addresses and what they match in re4.exe are listed below).

  install_runtime(img)   singletons, helper stubs and static TLS (the allocator is replaced by mhwildsnative.load)
  Entry(img, path, off)  attributes of one root entry: the type's factory (alloc + ctor), then its loader
                         (vtable slot 5) reading the file through a stream; descriptor X = the attribute chain
  Emitter(img, entry)    step lists registered by 1dec20, E placement-constructed by aaba300
  Emitter.tick(dt)       the tick driver 1c1c0 (state machine, N accumulator, then the 3D loop aaba8e0)

| Wilds | RE4 | role |
|---|---|---|
| 1c1c0 (X, U) | 3d09690 (owner, U) | tick driver. rcx is the descriptor X: the loop reads the step lists at X+0x50 / +0x58 |
| aaba300 (X, args) | 3d06440 | emitter constructor: args+0x10 = E, +0x18 element, +0x20 effect |
| aaba8e0 / aabba90 | 3d08480 / 3d079c0 | 3D / 2D loop, chosen by E+0xf0 bit 0 (RE4 E+0x168) |
| 1dec20 (X) | (none: fixed calls) | registers the step lists, then sorts them |
| vtable slot 5 (attr, stream, ctx) | slot 5 | loader; the stream starts at the UniqueID (after type id and declared size) |
| 614a0 (mgr, kind, path) | 39ef530 | resource lookup; the resource's reference count is at +0x28 in both |
| 1e1de0 | 4748b80 | render slot allocation, stored at block+0x38 by the Type builders |

E offsets: state +0x100 (RE4 +0x170), particle capacity +0x84 (from Spawn attr+0x3c), particle slots
+0x120 (RE4 +0x190) with stride 0x150 (RE4 0x140), attribute blocks +0x2c8 (RE4 +0x288), E.seed +0x98
(RE4 +0x110) and E.state +0xa8 (RE4 +0x120), read by the seeding function b0510f0 (RE4 3d09990),
claimed-particle counter +0xc8, per-particle side data at [E+0x128] with stride E+0xd0.

Particle slot (RE4 offset in brackets): [p] flags, [p+4] flags2 (phase = bits 3..6), +0x18 id, +0x28 previous
position, +0x34 position (+0x28), +0x60 life fraction, +0x64 age (+0x58), +0x68 velocity (+0x5c), +0x80 SpeedCoef
(+0x74), +0x84 gravity accel (+0x78), +0x88 gravity velocity (+0x7c), +0x8c size slots U X Y Z (+0x80),
+0xa8 rotation X Y Z (+0x9c), +0x40 Noise offset (+0x34), +0x4c displacement when Velocity3D Flags bit 2 is set (+0x40), +0x58 colour
RGBA8 (+0x4c), +0x5c Blink factor (+0x50). UV sequence and frame sit in [p] bits 10..17 / 18..25 as in RE4.
"""
import ctypes
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mhwildsnative as wn  # noqa: E402

B = wn.IMAGE_BASE

# .bss singletons the loaders, builders and the loop read; each gets a zeroed object
SINGLETONS = {
    0x147FEF60: 0x10000,   # +0x1aa9 gates EffectShader's loader (ad746ff); +0x1aa1 byte picks the loop's full alive-step range
    0x147CA268: 0x10000,   # graphics settings: +0x4c97 (FadeByDepth loader adb3265), +0x5520 config object (Spawn builder)
    0x147CA238: 0x10000,   # +0x88 read by the EmitterShape3D builder
    0x147E5330: 0x10000,   # resource manager passed to the resource lookup 614a0 (stubbed)
    0x1495B650: 0x10000,   # resource type passed to 614a0
    0x14C26D78: 0x10000,   # per-material counters, lock inc'd by EffectShader's loader (adf3c50)
    0x147E5348: 0x10000,   # +0x42dc read by Spawn's second pass (b0338d9)
}

# byte patches (the marker experiments used the same two)
PATCHES = {
    0xB073170: bytes([0xB8, 0, 0, 0x80, 0x3F, 0x66, 0x0F, 0x6E, 0xC0, 0xC3]),   # -> 1.0f (scale the Spawn builder reads)
    0xB04FB00: bytes([0x31, 0xC0, 0xC3]),                                       # -> 0 (lookup the EmitterShape3D builder makes)
}

# effect+0x1c4 bit 20: ParentOptions with a parent joint that cannot be resolved does not request the
# loop's reset branch (b02d305). The harness has no skeleton, so every joint lookup fails.
EFFECT_KEEP_WITHOUT_JOINT = 1 << 20

UVS_SHAPE = [4, 16]   # synthetic UVS: 4 sequences of 16 patterns, no UV rectangles
THREAD_TABLE_LOCK = 0x14905870   # CRITICAL_SECTION in .bss, set up by a static initializer in the game

STUB_FN = ctypes.CFUNCTYPE(ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64)
CALLS = []
RESOURCES = {}
_IMG = []


def stub(img, rva, name, answer=None):
    """Send the function at rva to a Python callback; it records the call and returns answer(...) or 0."""
    def cb(rcx, rdx, r8, r9):
        note = name
        try:
            ret = answer(rcx, rdx, r8, r9) if answer else 0
        except Exception as e:      # a Python error must not hand native code garbage
            ret, note = 0, f"{name} (stub error {e})"
        CALLS.append((note, rcx, rdx, r8, r9))
        return ret or 0
    f = img.keep(STUB_FN(cb))
    wn.patch_jump(rva, ctypes.cast(f, ctypes.c_void_p).value)


def _wstr(a, n=260):
    return ctypes.wstring_at(a, n).split(chr(0))[0] if a else None


def _uvs(img, obj):
    """The UVS fields the UVSequence step reads: +0x58 header (+8 sequence count), +0x78 per-sequence
    {SequenceBlock*, patterns*}. RE4's layout moved by 8 bytes."""
    counts = [UVS_SHAPE[1]] * UVS_SHAPE[0]
    hdr = img.alloc(0x40)
    wn.set_u32(hdr, 0x5556532E)
    wn.set_u32(hdr + 8, len(counts))
    wn.set_u32(hdr + 12, sum(counts))
    wn.set_u64(obj + 0x58, hdr)
    table = img.alloc(16 * len(counts))
    for i, npat in enumerate(counts):
        blk = img.alloc(16)
        wn.set_u32(blk, npat)
        wn.set_u64(table + 16 * i, blk)
        wn.set_u64(table + 16 * i + 8, img.alloc(16 * npat))
    wn.set_u64(obj + 0x78, table)


def _resource(mgr, kind, path, _):
    """614a0(manager, type, path): the path is a length-prefixed (0x40) or plain UTF-16 string."""
    s = _wstr(path + (2 if wn.u32(path) & 0xFFFF == 0x40 else 0))
    if s not in RESOURCES:
        img = _IMG[0]
        RESOURCES[s] = img.alloc(0x1000)
        if s and s.lower().endswith(".uvs"):
            _uvs(img, RESOURCES[s])
        wn.set_u32(RESOURCES[s] + 0x28, 0x10000)   # reference count: release b006090 never frees it
    return RESOURCES[s]


def install_runtime(img):
    _IMG[:] = [img]
    out = {}
    for rva, size in SINGLETONS.items():
        out[rva] = wn.install_global(img, rva, size)
    for rva, code in PATCHES.items():
        ctypes.memmove(B + rva, code, len(code))
    stub(img, 0x614A0, "resource lookup", _resource)
    stub(img, 0x1E1DE0, "render slot alloc", lambda *a: img.alloc(0x2000))
    tls = wn.install_static_tls(img)
    # critical section of the engine's thread-index table (17871dc0, reached through b006400 from the memory-tag
    # scope 0x2100 that the equidistant-cylinder table builder b4e4a60 opens); RE4's is d78b380 in emitter.py
    ctypes.WinDLL("kernel32").InitializeCriticalSection(ctypes.c_void_p(B + THREAD_TABLE_LOCK))
    return dict(singletons=out, tls=tls)


# -- factories ------------------------------------------------------------------------------------
# The static initializer aab6b5b.. writes the 282 std::function objects of the factory table 14bf2000
# (index = attribute type id). Each object's vtable +0x10 is its call slot: mov ecx, size; call b020830
# (alloc); jmp ctor. The initializer never runs here, so the table is read from its code.
FACTORY_INIT_RVA = 0xAAB6B5B
_FACTORIES = {}


def factories(img):
    """type id -> factory call slot RVA."""
    if _FACTORIES:
        return _FACTORIES
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    code = ctypes.string_at(B + FACTORY_INIT_RVA, 0x10000)
    lam = None
    arr, span = wn.FACTORY_TABLE_RVA, 0x40 * 400
    for x in md.disasm(code, FACTORY_INIT_RVA):
        if x.mnemonic == "ret":
            break
        if "[rip" not in x.op_str:
            continue
        disp = x.op_str.split("rip")[1].split("]")[0].replace(" ", "")
        t = x.address + x.size + int(disp, 16)
        if x.mnemonic == "lea" and x.op_str.startswith("rax"):
            lam = t
        elif x.mnemonic == "mov" and x.op_str.startswith("qword ptr [rip") and x.op_str.endswith("rax") \
                and lam is not None and arr <= t < arr + span and (t - arr) % 0x40 == 0 and not arr <= lam < arr + span:
            _FACTORIES[(t - arr) // 0x40] = wn.u64(B + lam + 0x10) - B
    return _FACTORIES


# Factories that crash natively: their constructors are in the anti-tamper section (.trace).
# 31 = TypeMeshClip ends in a fast fail (0xC0000409), so it ends the process.
CRASHING_FACTORIES = {24, 31, 62, 157, 207, 239, 251, 261, 278, 280}


class Entry:
    """A root entry: header (index, nameHash, entryAssignment, attribute count) then per attribute
    [type id][declared size][UniqueID + payload (declared size bytes)]."""

    def __init__(self, img, path, off, drop_types=()):
        data = open(path, "rb").read()
        self.index, self.name_hash, self.assign, self.count = struct.unpack_from("<4I", data, off)
        self.X = img.alloc(0x400)                 # descriptor: +0xc count, +0x10 chain, +0x34 3D, +0x50.. step lists
        self.loadctx = img.alloc(0x1000)          # +8+8*type id = attribute of that type already loaded
        self.attrs = []
        fac = factories(img)
        pos, prev = off + 16, None
        for i in range(self.count):
            tid, size = struct.unpack_from("<2I", data, pos)
            if tid in CRASHING_FACTORIES or tid not in fac:
                raise RuntimeError(f"attribute {i}: type {tid} has no native factory")
            attr = img.call(img.function(fac[tid], ctypes.c_uint64))
            if not attr:
                raise RuntimeError(f"attribute {i}: factory of type {tid} returned null")
            vt = wn.u64(attr) - B
            loader = wn.u64(B + vt + 8 * 5) - B
            st = wn.ReadStream(img, data, pos + 8)
            ok = img.call(img.function(loader, ctypes.c_uint8, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64),
                          attr, st.handle, self.loadctx)
            if not ok or st.pos != pos + 8 + size:
                raise RuntimeError(f"loader {loader:#x} of type {tid}: ok {ok}, read {st.pos - pos - 8} of {size} bytes")
            wn.set_u64(self.loadctx + 8 + 8 * tid, attr)
            pos += 8 + size
            if tid in drop_types:
                continue
            wn.set_u64((prev + 0x10) if prev else (self.X + 0x10), attr)
            prev = attr
            self.attrs.append(dict(type=tid, attr=attr, vtable=vt, loader=loader, bytes=size))
        wn.set_u32(self.X + 0xC, len(self.attrs))
        self.end = pos

    def set_3d(self, three_d):
        """X+0x34 == 1 selects the 3D step numbers (and the 3D loop through E+0xf0 bit 0)."""
        wn.set_u32(self.X + 0x34, 1 if three_d else 0)

    def register_steps(self, img):
        img.call(img.function(0x1DEC20, None, ctypes.c_uint64), self.X)
        return self.step_lists()

    def step_lists(self):
        X = self.X
        def lst(p, n):
            return [wn.u32(wn.u64(X + p) + 4 * i) for i in range(wn.u32(X + n))] if wn.u64(X + p) else []
        return lst(0x50, 0x58), lst(0x60, 0x68)


class Emitter:
    def __init__(self, img, entry, e_size=1 << 20):
        self.img, self.entry = img, entry
        self.E = img.alloc(e_size)
        self.element = img.alloc(0x1000)
        self.effect = img.alloc(0x1000)
        self.parent = img.alloc(0x1000)
        self.args = img.alloc(0x40)
        self.U = img.alloc(0x200)
        wn.set_u64(self.effect + 0x10, self.parent)
        wn.set_u64(self.parent + 0x20, img.alloc(0x1000))
        wn.set_u32(self.effect + 0x1C4, EFFECT_KEEP_WITHOUT_JOINT)
        wn.set_u64(self.E, entry.X)
        wn.set_u64(self.args + 0x10, self.E)
        wn.set_u64(self.args + 0x18, self.element)
        wn.set_u64(self.args + 0x20, self.effect)
        img.call(img.function(0xAABA300, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64), entry.X, self.args)
        wn.set_u64(self.U + 8, self.E)
        for off, size in ((0, 0x400), (0x10, 0x1000), (0x18, 0x1000), (0x20, 0x1000), (0x28, 0x1000)):
            wn.set_u64(self.U + off, img.alloc(size))
        wn.set_u64(wn.u64(self.U + 0x18) + 8, img.alloc(0x40))
        wn.set_f32(wn.u64(self.U + 0x20) + 8, 1.0)
        wn.set_u64(self.U + 0x30, self.effect)
        self.set_world(((1, 0, 0, 0), (0, 1, 0, 0), (0, 0, 1, 0), (0, 0, 0, 1)))
        self.drv = img.function(0x1C1C0, ctypes.c_uint8, ctypes.c_uint64, ctypes.c_uint64)

    def set_world(self, rows):
        ctypes.memmove(self.U + 0x40, struct.pack("<16f", *(v for row in rows for v in row)), 64)

    @property
    def capacity(self):
        return wn.u32(self.E + 0x84)

    def play(self):
        wn.set_u32(self.E + 0x100, (wn.u32(self.E + 0x100) & ~7) | 1)

    def tick(self, dt):
        wn.set_f32(self.U + 0x80, dt)
        return self.img.call(self.drv, self.entry.X, self.U)

    def particles(self):
        base = wn.u64(self.E + 0x120)
        return [base + 0x150 * i for i in range(self.capacity)] if base else []

    def alive(self):
        """Slots whose [p] has bit 0 (alive) set."""
        return [p for p in self.particles() if wn.u32(p) & 1]

    def state(self, life_type=94):
        """Live particles of this tick, in the field names exp_emitter.check() reads."""
        f, u = wn.f32, wn.u32
        life = self.block_of(life_type)
        out = []
        for i, p in enumerate(self.particles()):
            w = u(p)
            if not w & 3:
                continue
            rec = dict(slot=i, id=u(p + 0x18), flags=w, flags2=u(p + 4), phase=(u(p + 4) >> 3) & 0xF,
                       life_alpha=(w >> 2) & 0xFF, uv_seq=(w >> 10) & 0xFF, uv_frame=(w >> 18) & 0xFF,
                       age=f(p + 0x64), life_t=f(p + 0x60), pos=[f(p + 0x34 + 4 * k) for k in range(3)],
                       vel=[f(p + 0x68 + 4 * k) for k in range(3)], coef=f(p + 0x80), accel=f(p + 0x84),
                       gvel=f(p + 0x88), size=[f(p + 0x8C + 4 * k) for k in range(4)], rot=f(p + 0xA8),
                       disp=[f(p + 0x4C + 4 * k) for k in range(3)], rgba=u(p + 0x58), blink=f(p + 0x5C),
                       noise=[f(p + 0x40 + 4 * k) for k in range(3)])
            if life:   # Life record: remain, A, A+K, T (as RE4)
                r = wn.u64(self.E + 0x128) + i * u(self.E + 0xD0) + u(life + 8)
                rec["life_rec"] = list(struct.unpack("<8h", self.img.read(r, 16)))
            out.append(rec)
        return out

    def emitter_state(self):
        return dict(state=wn.u32(self.E + 0x100) & 7, seed=wn.u32(self.E + 0x98), rng_state=wn.u32(self.E + 0xA8),
                    counter=wn.u32(self.E + 0xC8))

    def block_of(self, type_id):
        for i, a in enumerate(self.entry.attrs):
            if a["type"] == type_id:
                return wn.u64(self.E + 0x2C8 + 8 * i)
        return None
