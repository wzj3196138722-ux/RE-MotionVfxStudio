"""Fake engine services for the emitter: a flat-ground physics query pool, the PtLife Action recorder,
and the EFX file's Action table.

Collision query pool ([d62f8e0], used by PtCollision 3d64580 and PtColliderAction 3d62430):
  submit ray     3ce34a0(pool, &from, &to, layer, mask) -> handle (int, -1 = pool full)
  submit sphere  3ce3330(pool, &shape vec4 = (d6edb20, d6edb24, d6edb28, radius), &from, &to, layer, mask) -> handle
  poll           slot = pool+0x18 + handle*0x28 (pool+8 = slot count): +0 kind (0 ray, 1 sphere),
                 +8 ray query / +0x10 sphere query, +0x24 result index;
                 43ccd10(query) = [[query+0x10]] slot 5 (+0x28): done?   43cccf0(query, index) = slot 2 (+0x10):
                 result* or 0 (no hit). Result: +0 hit position, +0x10 normal.
  The pool here answers every query at once (done = 1) against the plane y = GROUND[0] with normal +Y;
  the engine itself only polls a handle on the next tick, which gives its one-frame latency.

PtLife: 3d3d110(ctx, particle, status, slot) is hooked (the original still runs) and every call is
recorded; an Action instance that became active in the call is recorded with its 4x4 matrix. The per-
emitter Action tick 3d3e0a0 and the shared Action executor 3d122b0 / 3d0ee50 are replaced by recorders,
so no child Action runs and instances stay active.

Action table: loaders resolve ActionIndex through 3cfb400(file, i) = [file+0x58] + i*0x38 (count
[file+0x8c]); the loader context's [0] is the file. Here: ACTION_COUNT empty Actions (no attribute nodes).
"""
import ctypes
import struct

import re4native as rn

B = rn.IMAGE_BASE
GROUND = [0.0]
QUERIES = []          # (handle, kind, from, to, radius, hit) per submitted query
PTLIFE = []           # every 3d3d110 call: dict(ctx, particle, id, status, slot, fired, matrix)
ACTION_TICKS = []     # 3d3e0a0 calls (not run)
ACTION_COUNT = 64
_k = []
SLOTS = 1024


def _vec(a):
    return struct.unpack("<3f", ctypes.string_at(a, 12))


def _plane_hit(a, b, r):
    """Segment a->b (sphere radius r) against y = GROUND: first contact while moving down."""
    h = GROUND[0] + r
    if a[1] >= h > b[1]:
        t = (a[1] - h) / (a[1] - b[1])
        return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))
    return None


def install_collision(img):
    pool = img.alloc(0x100)
    slots = img.alloc(0x28 * SLOTS)
    rn.set_u32(pool + 8, SLOTS)
    rn.set_u64(pool + 0x18, slots)
    results = img.alloc(0x40 * SLOTS)
    done_t = ctypes.CFUNCTYPE(ctypes.c_uint32, ctypes.c_uint64)
    res_t = ctypes.CFUNCTYPE(ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint32)
    state = {}                                  # inner object -> handle

    def done(inner):
        return 1

    def result(inner, idx):
        h = state.get(inner)
        q = QUERIES[h["q"]] if h is not None else None
        return results + 0x40 * h["slot"] if q and q["hit"] else 0

    f_done, f_res = img.keep(done_t(done)), img.keep(res_t(result))
    vt = img.alloc(0x80)
    rn.set_u64(vt + 0x10, ctypes.cast(f_res, ctypes.c_void_p).value)
    rn.set_u64(vt + 0x28, ctypes.cast(f_done, ctypes.c_void_p).value)
    inners = []
    for i in range(SLOTS):
        inner = img.alloc(0x40)
        rn.set_u64(inner, vt)
        q = img.alloc(0x40)
        rn.set_u64(q + 0x10, inner)
        s = slots + 0x28 * i
        rn.set_u64(s + 8, q)
        rn.set_u64(s + 0x10, q)
        state[inner] = {"slot": i, "q": None}
        inners.append(inner)
    counter = [0]

    def submit(kind, pa, pb, radius, layer, mask):
        a, b = _vec(pa), _vec(pb)
        hit = _plane_hit(a, b, radius)
        h = counter[0] % SLOTS
        counter[0] += 1
        s = slots + 0x28 * h
        rn.set_u32(s, kind)
        rn.set_u32(s + 0x24, 0)
        state[inners[h]]["q"] = len(QUERIES)
        if hit:
            pt = (hit[0], GROUND[0], hit[2])
            ctypes.memmove(results + 0x40 * h, struct.pack("<3fI3fI", *pt, 0, 0.0, 1.0, 0.0, 0), 32)
        QUERIES.append(dict(handle=h, kind=kind, frm=a, to=b, radius=radius, layer=layer, mask=mask,
                            hit=list(hit) if hit else None))
        return h

    ray_t = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint32, ctypes.c_uint32)
    sph_t = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64,
                             ctypes.c_uint32, ctypes.c_uint32)
    f_ray = img.keep(ray_t(lambda p, a, b, layer, mask: submit(0, a, b, 0.0, layer, mask)))
    f_sph = img.keep(sph_t(lambda p, shape, a, b, layer, mask: submit(1, a, b, rn.f32(shape + 12), layer, mask)))
    from emitter import _jmp
    ctypes.memmove(B + 0x3CE34A0, _jmp(ctypes.cast(f_ray, ctypes.c_void_p).value), 12)
    ctypes.memmove(B + 0x3CE3330, _jmp(ctypes.cast(f_sph, ctypes.c_void_p).value), 12)
    rn.set_u64(B + 0xD62F8E0, pool)
    return pool


def install_ptlife(img):
    """Hook 3d3d110 (17-byte prologue: six pushes + sub rsp, imm32) and replace 3d3e0a0."""
    from emitter import _jmp, _k32
    entry, n = 0x3D3D110, 17
    orig = bytes(ctypes.string_at(B + entry, n))
    tramp = _k32.VirtualAlloc(None, 0x1000, 0x3000, 0x40)
    ctypes.memmove(tramp, orig + _jmp(B + entry + n), n + 12)
    fn_t = ctypes.CFUNCTYPE(None, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_int32, ctypes.c_uint32)
    game = fn_t(tramp)

    def instances(ctx):
        blk = rn.u64(ctx + 0x280)
        attr = rn.u64(blk)
        cnt = rn.u32(attr + 0x38)
        return [rn.u64(blk + 0x90 + 8 * i) for i in range(cnt)]

    def hook(ctx, p, status, slot):
        try:
            before = [(rn.u32(i + 8), rn.u32(i + 0xC)) for i in instances(ctx)]
        except Exception:
            before = []
        game(ctx, p, status, slot)
        rec = dict(id=rn.u32(p + 0x18), status=status, slot=slot, fired=None)
        try:
            inst = instances(ctx)
            for k, i in enumerate(inst):
                now = (rn.u32(i + 8), rn.u32(i + 0xC))
                if k >= len(before) or now != before[k] or (now[0] & 2 and not before[k][0] & 2):
                    rec["fired"] = dict(instance=k, flags=now[0],
                                        matrix=list(struct.unpack("<16f", ctypes.string_at(i + 0x10, 64))))
            attr = rn.u64(rn.u64(ctx + 0x280))
            rec["attr_status"] = struct.unpack("<i", ctypes.string_at(attr + 0x30, 4))[0]
        except Exception as e:
            rec["error"] = str(e)
        PTLIFE.append(rec)

    f = img.keep(fn_t(hook))
    ctypes.memmove(B + entry, _jmp(ctypes.cast(f, ctypes.c_void_p).value), 12)
    tick_t = ctypes.CFUNCTYPE(None, ctypes.c_uint64)
    t = img.keep(tick_t(lambda ctx: ACTION_TICKS.append(("ptlife", ctx))))
    ctypes.memmove(B + 0x3D3E0A0, _jmp(ctypes.cast(t, ctypes.c_void_p).value), 12)
    # the shared Action executor (PtColliderAction's emitter step 3d62230 calls it per active instance),
    # and its variant 3d0ee50: recorded, not run
    for rva, name in ((0x3D122B0, "action exec"), (0x3D0EE50, "action exec (alt)")):
        f2 = img.keep(tick_t(lambda a, n=name: ACTION_TICKS.append((n, a))))
        ctypes.memmove(B + rva, _jmp(ctypes.cast(f2, ctypes.c_void_p).value), 12)


def instance_matrices(ctx):
    blk = rn.u64(ctx + 0x280)
    if not blk:
        return []
    attr = rn.u64(blk)
    out = []
    for k in range(rn.u32(attr + 0x38)):
        i = rn.u64(blk + 0x90 + 8 * k)
        out.append(dict(instance=k, flags=rn.u32(i + 8),
                        matrix=list(struct.unpack("<16f", ctypes.string_at(i + 0x10, 64)))))
    return out


def make_file(img):
    """A stand-in EFX file object with ACTION_COUNT Actions. Each Action (0x38 bytes) has one attribute
    node, a FixRandomGenerator built by its factory with zero data, and a vtable whose slot 1 returns the
    summed block size of the nodes (the PtLife block-size function 3d3d700 sizes each instance record
    with it; the builder then appends each node's block) and whose other slots return 0.
    The real Actions of the corpus files hold e.g. type 151, whose loader reads an embedded EFX and needs
    the nested resource loader (3cfadc0) and the thread-local memory-tag scope 3ab52b0; they are not
    parsed here, and no child Action runs anyway."""
    from emitter import _k32, _native_table
    import factory
    code = _k32.VirtualAlloc(None, 0x1000, 0x3000, 0x40)
    ctypes.memmove(code, bytes.fromhex("31C0C3"), 3)          # xor eax, eax ; ret
    frg = _native_table(img)[1]
    node0 = img.call(img.function(frg["factory"], ctypes.c_void_p))
    size = img.call(img.function(factory.slot(frg["vtable"], 6), ctypes.c_uint64, ctypes.c_void_p), node0)
    ctypes.memmove(code + 0x10, bytes([0xB8]) + struct.pack("<I", size) + bytes([0xC3]), 6)   # mov eax, size ; ret
    vt = img.alloc(8 * 64)
    for i in range(64):
        rn.set_u64(vt + 8 * i, code)
    rn.set_u64(vt + 8, code + 0x10)
    f = img.alloc(0x200)
    acts = img.alloc(0x38 * ACTION_COUNT)
    for k in range(ACTION_COUNT):
        a = acts + 0x38 * k
        rn.set_u64(a, vt)
        rn.set_u32(a + 0xC, 1)
        rn.set_u64(a + 0x10, img.call(img.function(frg["factory"], ctypes.c_void_p)))
    rn.set_u64(f + 0x58, acts)
    rn.set_u32(f + 0x8C, ACTION_COUNT)
    return f


def instance_matrices_blk(blk):
    """Action instances of a PtLife block: [blk+0x90+8k], count attr+0x38 (attr = [blk])."""
    attr = rn.u64(blk)
    out = []
    for k in range(rn.u32(attr + 0x38)):
        i = rn.u64(blk + 0x90 + 8 * k)
        if i:
            out.append(dict(instance=k, flags=rn.u32(i + 8),
                            matrix=list(struct.unpack("<16f", ctypes.string_at(i + 0x10, 64)))))
    return out
