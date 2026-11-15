"""Build a real EFX emitter with re4.exe's own code and tick it.

  install_runtime(img)   allocator entry patch, settings object, .bss singleton stubs, static TLS shim
  read_entry(img, path, off)   attributes of one root entry: per attribute the type's factory
                         (alloc + ctor), then the type's loader (vtable slot 5) reading the file bytes
                         through a stream object; the engine decides how many bytes each attribute takes
  Emitter(img, entry)    E placement-constructed by 3d06440 (attribute blocks built by slot 15)
  Emitter.tick(dt)       the tick driver 3d09690 (state machine, N accumulator, then 3d08480)

Every pointer the engine reads from outside the emitter (update struct, the owner object, the
effect-level objects at E+8 / E+0x10, singletons) is a zeroed buffer unless noted.
"""
import ctypes
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import factory  # noqa: E402
import re4native as rn  # noqa: E402

B = rn.IMAGE_BASE
ARENA_BASE = 0x5F00_0000_0000
ARENA_SIZE = 1 << 28
_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_k32.VirtualAlloc.restype = ctypes.c_void_p
_k32.VirtualAlloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint32, ctypes.c_uint32]
_state = {}

# allocator entry points (3a85cc0 = jmp 3ab29d0 (size); 3a85cd0 = jmp 3acc670 (ptr); see README)
ALLOC = 0x3AB29D0
FREE = 0x3ACC670


def _jmp(target):
    return b"\x48\xB8" + struct.pack("<Q", target) + b"\xFF\xE0"


def install_allocator(img):
    """3ab29d0(size) -> 16-aligned bump block from a zeroed arena; 3acc670(ptr) -> no-op."""
    if "cursor" in _state:
        return
    if _k32.VirtualAlloc(ARENA_BASE, ARENA_SIZE, 0x3000, 0x04) != ARENA_BASE:
        raise OSError("cannot commit the arena")
    code = _k32.VirtualAlloc(None, 0x1000, 0x3000, 0x40)
    cursor = code + 0x800
    ctypes.c_uint64.from_address(cursor).value = ARENA_BASE
    stub = (b"\x48\x83\xC1\x0F"                                  # add rcx, 15
            b"\x48\x83\xE1\xF0"                                  # and rcx, -16
            b"\x48\x89\xC8"                                      # mov rax, rcx
            b"\x49\xB9" + struct.pack("<Q", cursor) +            # mov r9, cursor
            b"\xF0\x49\x0F\xC1\x01"                              # lock xadd [r9], rax
            b"\xC3")                                             # ret (rax = old cursor)
    ctypes.memmove(code, stub, len(stub))
    ctypes.memmove(B + ALLOC, _jmp(code), 12)
    ctypes.memmove(B + FREE, b"\xC3", 1)
    _state.update(cursor=cursor, code=code)


def arena_used():
    return ctypes.c_uint64.from_address(_state["cursor"]).value - ARENA_BASE


# .bss singletons the emitter loop reaches (scan_loops.py); each gets a zeroed object
SINGLETONS = {
    0xD62F9F0: 0x4000,   # byte +0x3bb9 gates E+0x124 = [update+0x30]+0x1b8 (3d08656)
    0xD62F8E0: 0x4000,   # collision world (PtCollision / PtColliderAction)
    0xD62F9B0: 0x1000,   # mesh render manager: TypeMesh's builder asks it for an instance (3d2eae0; call stubbed)
    0xD62FA38: 0x5000,   # engine object: PtLife events (3d3d110); +0x4340 -> float +0x18 scales spawn counts (3d154f7)
}


def install_singletons(img):
    out = {}
    for rva, size in SINGLETONS.items():
        obj = img.alloc(size)
        rn.set_u64(B + rva, obj)
        out[rva] = obj
    q = img.alloc(0x100)                      # [d62fa38]+0x4340 -> +0x18: spawn-count scale, 1.0 here
    rn.set_f32(q + 0x18, 1.0)
    rn.set_u64(out[0xD62FA38] + 0x4340, q)
    return out


def install_tls(img):
    """Static TLS for re4.exe on this thread: a block holding the TLS template, placed in a new
    ThreadLocalStoragePointer array at an index past the ones the loader filled, and _tls_index
    set to it. gs:[0x58] is replaced for the calling thread only."""
    tls = B + img.layout["tls_rva"]
    start, end, index_addr = rn.u64(tls), rn.u64(tls + 8), rn.u64(tls + 16)
    block = img.alloc(0x1000)
    ctypes.memmove(block, start, end - start)
    code = _k32.VirtualAlloc(None, 0x1000, 0x3000, 0x40)
    ctypes.memmove(code, bytes.fromhex("65488B042530000000C3"), 10)   # mov rax, gs:[0x30] ; ret
    teb = ctypes.CFUNCTYPE(ctypes.c_uint64)(code)()
    old = rn.u64(teb + 0x58)
    arr = img.alloc(8 * 128)
    n = 0
    if old:                                  # keep the existing modules' slots (stop at the first 0)
        while n < 64 and rn.u64(old + 8 * n):
            rn.set_u64(arr + 8 * n, rn.u64(old + 8 * n))
            n += 1
    idx = 100
    rn.set_u64(arr + 8 * idx, block)
    rn.set_u64(teb + 0x58, arr)
    import atexit
    atexit.register(lambda: rn.set_u64(teb + 0x58, old))   # the loader frees its own array at thread exit
    rn.set_u32(index_addr, idx)
    return dict(index=idx, block=block, copied_slots=n)


STUB_FN = ctypes.CFUNCTYPE(ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64)
CALLS = []          # (stub name, rcx, rdx, r8, r9, note) of every stubbed call


def stub(img, rva, name, answer=None):
    """Send the function at rva to a Python callback that records the call and returns
    answer(rcx, rdx, r8, r9) (default 0). The original code never runs."""
    def cb(rcx, rdx, r8, r9):
        note = None
        try:
            ret = answer(rcx, rdx, r8, r9) if answer else 0
            if isinstance(ret, tuple):
                ret, note = ret
        except Exception as e:      # a Python error must not hand native code garbage
            ret, note = 0, f"stub error {e}"
        CALLS.append((name, rcx, rdx, r8, r9, note))
        return ret or 0
    f = img.keep(STUB_FN(cb))
    ctypes.memmove(B + rva, _jmp(ctypes.cast(f, ctypes.c_void_p).value), 12)


def _wstr(a, n=260):
    try:
        return ctypes.wstring_at(a, n).split("\0")[0] if a else None
    except OSError:
        return None


# engine services the EFX code calls outside the emitter
SERVICES = {
    # resource lookup (rcx = manager, rdx = type info, r8 = wide path) -> resource handle; here a
    # zeroed stand-in object per path (RESOURCES), so nothing of the file behind the path is loaded
    0x39EF530: ("resource lookup", lambda c, d, p, q: (_resource(_wstr(p)), _wstr(p))),
    # render-primitive pool (d89a730, bitmask over 0xee0-byte slots) -> one slot for a type's block
    # (Billboard3D builder 3d18663 stores it at block+0x38); here a zeroed slot
    0x4748B80: ("render slot alloc", lambda c, d, p, q: _IMG[0].alloc(0xEE0)),
    # mesh render-instance pool (TypeMesh builder 3d2eaec stores it at block+0xf8); here a zeroed instance
    0x4659170: ("mesh instance alloc", lambda c, d, p, q: _IMG[0].alloc(0x4000)),
    # mesh instance setup from the .mesh / .mdf2 resources (TypeMesh builder 3d2eb26 / 3d2eb34 / 3d2eb49):
    # render side, needs the real mesh resource; skipped
    0x46565C0: ("mesh instance setup A", None),
    0x4658830: ("mesh instance setup B", None),
    0x4656200: ("mesh instance setup C", None),
    # collision filter lookups by name (rcx = [d62f928] CollisionFilter, rdx = wide name), used by the
    # PtCollision / PtColliderAction installs: layer index 0, mask bit 0 (1 << 0)
    0x43D8570: ("collision layer by name", lambda c, d, p, q: (0, _wstr(d))),
    0x43D8800: ("collision mask bit by name", lambda c, d, p, q: (0, _wstr(d))),
    # joint lookup by name on the parent's transform (ParentOptions BoneName): no parent -> not found
    0x3D00F80: ("joint lookup", lambda c, d, p, q: 0),
}
RESOURCES = {}
_IMG = []


# The .uvs files the corpus entries name are not in RE4_EXTRACT, so a .uvs stand-in gets this
# synthetic shape: UVS_SHAPE[0] sequences of UVS_SHAPE[1] patterns each (no UV rectangles: the
# accessor 495a6b0 then returns its default rectangle at d62f4a8).
UVS_SHAPE = [4, 16]


def _uvs(img, obj):
    nseq, npat = UVS_SHAPE
    hdr = img.alloc(0x40)                       # +0x50 -> file header: +8 sequence count
    rn.set_u32(hdr, 0x5556532E)
    rn.set_u32(hdr + 8, nseq)
    rn.set_u32(hdr + 12, nseq * npat)
    rn.set_u64(obj + 0x50, hdr)
    table = img.alloc(16 * nseq)                # +0x70 -> per sequence {SequenceBlock*, patterns*}
    for i in range(nseq):
        blk = img.alloc(16)
        rn.set_u32(blk, npat)
        rn.set_u64(table + 16 * i, blk)
        rn.set_u64(table + 16 * i + 8, img.alloc(16 * npat))
    rn.set_u64(obj + 0x70, table)


def _resource(path):
    if path not in RESOURCES:
        RESOURCES[path] = _IMG[0].alloc(0x1000)
        if path and path.lower().endswith(".uvs"):
            _uvs(_IMG[0], RESOURCES[path])
        if path and path.lower().endswith(".mesh"):   # +0x168 -> mesh data (TypeMesh slot 23, 3d306ca); empty
            rn.set_u64(RESOURCES[path] + 0x168, _IMG[0].alloc(0x1000))
        rn.set_u32(RESOURCES[path] + 0x28, 0x10000)   # reference count: release (39f73f0) never reaches 0
    return RESOURCES[path]


def install_runtime(img, eb0=0, eb2=0, global_seed=None, collision=False, ptlife=False):
    install_allocator(img)
    _IMG[:] = [img]
    for rva, (name, fn) in SERVICES.items():
        stub(img, rva, name, fn)
    settings = rn.install_settings(img, eb0=eb0, eb2=eb2)
    singles = install_singletons(img)
    tls = install_tls(img)
    # critical section of the engine's thread-index table (3b0b350, reached through the memory-tag scope
    # 3ab52b0 that some loaders open); a static initializer sets it up in the game
    _k32.InitializeCriticalSection(ctypes.c_void_p(B + 0xD78B380))
    if global_seed is not None:              # the engine's global generator (d6a3150..5c), zero in a fresh image
        for i, v in enumerate(global_seed):
            rn.set_u32(B + 0xD6A3150 + 4 * i, v)
    import world
    if collision:
        world.install_collision(img)        # replaces the zeroed d62f8e0 with the flat-ground query pool
    if ptlife:
        world.install_ptlife(img)
    return dict(settings=settings, singletons=singles, tls=tls)


READ_FN = ctypes.CFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t)


class Stream:
    def __init__(self, img, data, pos):
        self.data, self.pos = data, pos
        self.cb = img.keep(READ_FN(self._read))
        self.vtable = img.alloc(0x100)
        for i in range(32):
            rn.set_u64(self.vtable + 8 * i, ctypes.cast(self.cb, ctypes.c_void_p).value)
        self.obj = img.alloc(0x80)
        rn.set_u64(self.obj, self.vtable)
        self.handle = img.alloc(16)
        rn.set_u64(self.handle, self.obj)

    def _read(self, this, buf, size):
        chunk = self.data[self.pos:self.pos + size]
        ctypes.memmove(buf, chunk, len(chunk))
        self.pos += len(chunk)
        return len(chunk)


_TABLE = {}


def _native_table(img):
    if not _TABLE:
        _TABLE.update(factory.native_table(img))
    return _TABLE


class Entry:
    """A root entry: header (index, nameHash, entryAssignment, attribute count) then
    [type id][UniqueID][payload] per attribute."""

    def __init__(self, img, path, off, drop_types=(), header=4):
        """drop_types: attribute type ids that are read (their bytes consumed) but left out of the
        entry, e.g. (1,) runs the entry without its FixRandomGenerator."""
        data = open(path, "rb").read()
        if header == 4:     # root entry: index, nameHash, entryAssignment, attribute count
            self.index, self.name_hash, self.assign, self.count = struct.unpack_from("<4I", data, off)
        else:               # Action: actionUnkn0, nameHash, attribute count
            self.index, self.name_hash, self.count = struct.unpack_from("<3I", data, off)
            self.assign = None
        self.obj = img.alloc(0x200)          # entry object the emitter ctor reads (+0xc count, +0x10 list)
        # loaders get a context whose +8+8*type_id holds the attribute of that type already loaded
        # (ScaleAnim reads +0x88 = Spawn -> MaxParticles, UVSequence +0xb0 / +0xc0 / +0x200 / +0x838)
        self.loadctx = img.alloc(0x1000)
        import world
        rn.set_u64(self.loadctx, world.make_file(img))   # [0] = file: Action table for ActionIndex / linkedAction
        self.attrs = []
        st = Stream(img, data, off + 4 * header)
        tbl = _native_table(img)
        prev = None
        for i in range(self.count):
            tid = struct.unpack_from("<I", data, st.pos)[0]
            st.pos += 4
            if tid not in tbl:
                raise RuntimeError(f"attribute {i}: type id {tid} has no factory in factory.native_table()")
            t = tbl[tid]
            attr = img.call(img.function(t["factory"], ctypes.c_void_p))
            vt = rn.u64(attr) - B
            loader = factory.slot(vt, 5)
            p0 = st.pos
            ok = img.call(img.function(loader, ctypes.c_uint8, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p),
                          attr, st.handle, self.loadctx)
            self.attrs.append(dict(type=tid, attr=attr, vtable=vt, loader=loader, bytes=st.pos - p0, ok=ok,
                                   uid=struct.unpack_from("<I", data, p0)[0]))
            if not ok:
                raise RuntimeError(f"loader {loader:#x} of type {tid} returned 0 at file offset {p0:#x}")
            if tid == 28 and not rn.u64(attr + 0xC8):   # TypeMesh: material handle (from the .mdf2) read by slot 23 (3d309ec)
                rn.set_u64(attr + 0xC8, img.alloc(0x100))
            if tid in drop_types:
                self.attrs[-1]["dropped"] = True
                continue
            if prev:
                rn.set_u64(prev + 0x10, attr)
            else:
                rn.set_u64(self.obj + 0x10, attr)
            prev = attr
            rn.set_u64(self.loadctx + 8 + 8 * tid, attr)
        rn.set_u32(self.obj + 0xC, sum(not a.get("dropped") for a in self.attrs))
        # +0x34 bit 0 -> E+0x168 bit 0 (3d06551): the 3D tick loop 3d08480 instead of the 2D one 3d079c0
        rn.set_u32(self.obj + 0x34, 1 if any(a['type'] == 9 for a in self.attrs) else 0)
        self.end = st.pos


class Emitter:
    def __init__(self, img, entry, e_size=1 << 20):
        self.img = img
        self.entry = entry
        self.E = img.alloc(e_size)
        rn.set_u64(self.E, entry.obj)          # E[0] = entry (3dfc900, 3d08480 read the attribute list through it)
        self.element = img.alloc(0x1000)     # -> E+8  (reads +0x6c flags, +0x90/+0x94)
        self.effect = img.alloc(0x1000)      # -> E+0x10 (reads +0x1c4; +0x10 -> parent object, +0x20 its transform)
        parent = img.alloc(0x1000)
        rn.set_u64(self.effect + 0x10, parent)
        rn.set_u64(parent + 0x20, img.alloc(0x1000))   # the parent's transform (no joints, no parent: +0x60 = 0)
        args = img.alloc(0x40)
        rn.set_u64(args + 0x10, self.E)
        rn.set_u64(args + 0x18, self.element)
        rn.set_u64(args + 0x20, self.effect)
        img.call(img.function(0x3D06440, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p), entry.obj, args)
        self.owner = img.alloc(0x1000)       # rcx of 3d09690 / 3d08480 (stored at ctx+0x118)
        self.U = img.alloc(0x200)            # update struct (rdx of 3d09690)
        rn.set_u64(self.U + 0x0, img.alloc(0x400))
        rn.set_u64(self.U + 0x8, self.E)
        for off in (0x10, 0x18, 0x20, 0x28):
            rn.set_u64(self.U + off, img.alloc(0x1000))
        # U+0x20 -> ctx+0x100: +1 byte gates 3dfa810; float +8 is the effect's LOD factor q that Spawn
        # multiplies its counts by at (re)start (3d15b1d -> 3d154d0); 1.0 = full counts
        rn.set_f32(rn.u64(self.U + 0x20) + 8, 1.0)
        rn.set_u64(self.U + 0x30, self.effect)
        self.set_world(((1, 0, 0, 0), (0, 1, 0, 0), (0, 0, 1, 0), (0, 0, 0, 1)))
        self.drv = img.function(0x3D09690, ctypes.c_uint8, ctypes.c_void_p, ctypes.c_void_p)

    def set_world(self, rows):
        """U+0x40..0x7f: the effect's world matrix (rows: X, Y, Z axes, translation), copied into ctx by 3d08480."""
        for r, row in enumerate(rows):
            for c, v in enumerate(row):
                rn.set_f32(self.U + 0x40 + 16 * r + 4 * c, v)

    @property
    def max_particles(self):
        return rn.u32(self.E + 0xFC)

    def play(self):
        rn.set_u32(self.E + 0x170, (rn.u32(self.E + 0x170) & ~7) | 1)

    def tick(self, dt):
        rn.set_f32(self.U + 0x80, dt)
        return self.img.call(self.drv, self.owner, self.U)

    def particles(self):
        base = rn.u64(self.E + 0x190)
        return [base + 0x140 * i for i in range(rn.u32(self.E + 0x100))]

    def block_of(self, type_id):
        """The runtime block of an attribute type (E+0x288[i], i = position in the linked list)."""
        live = [a for a in self.entry.attrs if not a.get("dropped")]
        for i, a in enumerate(live):
            if a["type"] == type_id:
                return rn.u64(self.E + 0x288 + 8 * i)
        return None

    def side(self, slot):
        """Per-particle side data (E+0x198 + slot * E+0x148); attribute records sit at block+8."""
        return rn.u64(self.E + 0x198) + slot * rn.u32(self.E + 0x148)

    def state(self):
        """One frame of particle state, slot order. Field offsets: README / native-pilot.md."""
        f, u = rn.f32, rn.u32
        out = []
        life = self.block_of(91)
        for i, p in enumerate(self.particles()):
            w = u(p)
            if not w & 3:
                continue
            rec = dict(slot=i, id=u(p + 0x18), flags=w, flags2=u(p + 4), phase=(u(p + 4) >> 3) & 0xF,
                       life_alpha=(w >> 2) & 0xFF, uv_seq=(w >> 10) & 0xFF, uv_frame=(w >> 18) & 0xFF,
                       age=f(p + 0x58), pos=[f(p + 0x28 + 4 * k) for k in range(3)],
                       vel=[f(p + 0x5C + 4 * k) for k in range(3)], coef=f(p + 0x74), accel=f(p + 0x78),
                       gvel=f(p + 0x7C), size=[f(p + 0x80 + 4 * k) for k in range(4)], rot=f(p + 0x9C),
                       rgba=u(p + 0x4C), blink=f(p + 0x50))
            if life:
                r = self.side(i) + u(life + 8)
                rec["life_rec"] = list(struct.unpack("<8h", self.img.read(r, 16)))
            out.append(rec)
        return out

    def emitter_state(self):
        return dict(state=rn.u32(self.E + 0x170) & 7, e170=rn.u32(self.E + 0x170), e168=rn.u32(self.E + 0x168),
                    seed=rn.u32(self.E + 0x110), rng_state=rn.u32(self.E + 0x120), counter=rn.u32(self.E + 0x140),
                    acc=rn.f32(self.E + 0x10C), time=rn.f32(self.E + 0x108))
