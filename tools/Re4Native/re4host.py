"""P0 RE4 render-host service. stdout is exclusively JSON-lines; see PROTOCOL.md."""
import argparse
import ctypes
import hashlib
import json
import math
import mmap
import os
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import emitter as em
import re4native as rn

HEADER = 256
SLOT_HEADER = 64
ENTRY_SIZE = 160
STRIDE = 0x140
VERSION = 3
# Drawable Type attribute -> companion attributes allowed with it (Expression / Clip).
DRAWABLE = {21: {22}, 23: {24}, 63: {64, 65}, 31: set(), 32: {58}, 34: {60}, 70: set()}
# Ribbon types whose engine points are exported, all as slot * points-per-particle records of 0x54 bytes plus
# 0x68-byte state records whose +0x18 holds the valid point count:
# - RibbonFollow 31, RibbonLength 32: type block +0x38 points, +0x40 states (0x68 bytes) copied as they are.
# - RibbonFixEnd 34: +0x40 states are 0x70 bytes (+0x08 point pointer, +0x18 live count); re-packed.
# - PolygonTrail 70: built from the engine's vertex grid (see trail_columns).
RIBBON_POOLS = {31, 32, 34, 70}
POINT_STRIDE, RIBBON_STATE_STRIDE, FIXEND_STATE_STRIDE = 0x54, 0x68, 0x70
TRAIL_VERTEX_STRIDE = 48
IDENTITY = (1., 0., 0., 0., 0., 1., 0., 0., 0., 0., 1., 0., 0., 0., 0., 1.)


class Snapshot:
    """Quiescent, same-thread snapshot. P0 supports the existing non-world bb fixtures.

    Keep ctypes allocations alive; restore all of them, not just the bump arena.
    Never reuse a faulted image. Python resource caches are restored as well.
    """
    def __init__(self, img):
        self.img = img
        self.regions = []
        with open(os.environ.get('RE4_EXE', rn.DEFAULT_EXE), 'rb') as f:
            hdr = f.read(4096)
        pe = struct.unpack_from('<I', hdr, 0x3c)[0]
        nsec, optsz = struct.unpack_from('<H', hdr, pe + 6)[0], struct.unpack_from('<H', hdr, pe + 20)[0]
        for i in range(nsec):
            off = pe + 24 + optsz + i * 40
            vsize, va, raw = struct.unpack_from('<III', hdr, off + 8)
            flags = struct.unpack_from('<I', hdr, off + 36)[0]
            if flags & 0x80000000:
                self.regions.append((rn.IMAGE_BASE + va, ctypes.string_at(rn.IMAGE_BASE + va, max(vsize, raw))))
        self.used = em.arena_used()
        self.regions.append((em.ARENA_BASE, ctypes.string_at(em.ARENA_BASE, self.used)))
        self.keep_count = len(img._keep)
        for obj in img._keep:
            if isinstance(obj, ctypes.Array):
                self.regions.append((ctypes.addressof(obj), bytes(obj)))
        self.resources = dict(em.RESOURCES)
        self.bytes = sum(len(b) for _, b in self.regions)

    def restore(self):
        # Clear allocations made after the checkpoint before rewinding the bump cursor.
        used = em.arena_used()
        if used > self.used:
            ctypes.memset(em.ARENA_BASE + self.used, 0, used - self.used)
        for ptr, data in self.regions:
            ctypes.memmove(ptr, data, len(data))
        rn.set_u64(em._state['cursor'], em.ARENA_BASE + self.used)
        del self.img._keep[self.keep_count:]
        em.RESOURCES.clear()
        em.RESOURCES.update(self.resources)
        em.CALLS.clear()


class RecoveredError(RuntimeError):
    """A non-native error during advancement after which the load snapshot was restored."""


class Host:
    def __init__(self, args):
        self.args = args
        self.img = None
        self.emitters = []
        self.mapping = None
        self.tick = 0
        self.elapsed = 0.
        self.dts = []
        self.worlds = []
        self.active_entry = None
        self.world_written = {}

    def load(self, cmd):
        """First load maps the exe, installs the runtime and records the base snapshot (no emitter
        yet). Every later load restores that base instead of restarting the process, so a new
        document costs only the engine's entry parse, emitter build and the load snapshot."""
        start = time.perf_counter()
        reused = self.img is not None
        base_ms = 0.
        if self.mapping is not None:        # the client binds a fresh mapping after every load
            self.mapping.close()
            self.mapping = None
        if not reused:
            self.img = rn.load(self.args.exe)
            # PtCollision / PtColliderAction query the physics world: a flat ground at y = 0 answers them
            # (without it the query ring has no capacity and the first query divides by zero, 3ce336c).
            import world
            world.RECORD[0] = False
            em.install_runtime(self.img, collision=True)
            em._native_table(self.img)      # runs every factory once; cached type table is static data
            self.base = Snapshot(self.img)
        else:
            base_start = time.perf_counter()
            self.emitters, self.meta = [], []
            if hasattr(self, 'snapshot'):
                del self.snapshot
            self.base.restore()
            self.world_written = {}
            base_ms = (time.perf_counter() - base_start) * 1000
        em.UVS_COUNTS.clear()
        em.UVS_SHAPE[:] = [4, 16]
        # The engine's global generator (d6a3150..5c) is zero in a fresh image; the base snapshot
        # holds that zero state, so a given seed is written the same way on first and later loads.
        seed = cmd.get('global_seed') or [0, 0, 0, 0]
        if len(seed) != 4:
            raise ValueError('global_seed must have 4 values')
        for i, v in enumerate(seed):
            rn.set_u32(rn.IMAGE_BASE + 0xD6A3150 + 4 * i, int(v) & 0xFFFFFFFF)
        for path, counts in cmd.get('uvs_resources', {}).items():
            if not counts or len(counts) > 256 or any(not isinstance(n, int) or not 1 <= n <= 256 for n in counts):
                raise ValueError('UVS sequence sizes must be in 1..256')
            em.UVS_COUNTS[path.replace('\\', '/').lower()] = counts
        specs = cmd.get('entries')
        if not specs:
            from exp_emitter import ENTRIES, STM
            scene = ENTRIES[cmd.get('fixture', 'bb2')]
            path = scene['path']
            if self.args.resources:
                path = str(Path(self.args.resources) / Path(path).relative_to(STM))
            specs = [dict(path=path, offset=scene['off'], drop_types=cmd.get('drop_types', []))]
            em.UVS_SHAPE[:] = scene.get('uvs', (4, 16))
        if not 1 <= len(specs) <= 64:
            raise ValueError('expected 1..64 entries')
        self.meta = []
        data_offset = SLOT_HEADER + ENTRY_SIZE * len(specs)
        skipped = []
        for i, spec in enumerate(specs):
            self.active_entry = i
            try:
                stress = int(spec.get('stress_particles', 0))
                if not 0 <= stress <= 10000:
                    raise ValueError('stress_particles must be 0..10000')
                entry = em.Entry(self.img, spec['path'], int(spec['offset']), drop_types=spec.get('drop_types', ()), spawn_override=stress)
                # Drawable CPU types: Billboard2D 21, Billboard3D 23, Polygon 63, each with its own
                # Expression / Clip companions, which only rewrite the type block.
                types = [a['type'] for a in entry.attrs if 21 <= a['type'] <= 76]
                primary = [t for t in types if t in DRAWABLE]
                if len(primary) != 1 or not set(types) <= {primary[0]} | DRAWABLE[primary[0]]:
                    raise ValueError(f'requires one drawable Type attribute (Billboard2D, Billboard3D, Polygon, RibbonFollow, RibbonLength, RibbonFixEnd, PolygonTrail) with its companions; found {types}')
                type_id = primary[0]
                spawn = next(a for a in entry.attrs if a['type'] == 16)
                capacity = rn.u32(spawn['attr'] + 0x28)
                if capacity > 10000:
                    raise ValueError('MaxParticles exceeds P0 safety limit 10000')
                # Size the placement buffer conservatively: side data and attribute blocks also live here.
                e = em.Emitter(self.img, entry, e_size=max(1 << 20, capacity * 8192 + (1 << 20)))
                e.play()
                n, side = e.max_particles, rn.u32(e.E + 0x148)
                pbytes, sbytes = n * STRIDE, n * side
                meta = dict(entry=entry.index, type=type_id, capacity=n, stride=STRIDE,
                            side_stride=side, particles=data_offset, particle_bytes=pbytes,
                            side=data_offset + pbytes, side_bytes=sbytes, block=data_offset + pbytes + sbytes, block_bytes=0x2f8,
                            strip=0, strip_bytes=0, strip_points=0, strip_state=0, strip_state_bytes=0)
                data_offset += pbytes + sbytes + 0x2f8
                if type_id in RIBBON_POOLS:
                    # Points per particle come from the file ((ShapeDivision-1)*SplineDivision + ShapeDivision);
                    # the state records are only filled on the first update, after this snapshot.
                    per = int(spec.get('ribbon_points', 0))
                    if not 2 <= per <= 1000 * 1000:
                        raise ValueError('ribbon entries need ribbon_points (points per particle)')
                    if type_id == 70:
                        # PolygonTrail: type block +0x308 = vertex grid, +0x310 vertices per particle (the column count
                        # at +0x33c is written only by the first update, so the file's column count is used).
                        vertices = rn.u32(e.block_of(70) + 0x310)
                        if vertices % per or vertices // per < 2:
                            raise ValueError(f'PolygonTrail grid of {vertices} vertices does not split into {per} columns')
                        meta.update(trail_vertices=vertices)
                    meta.update(strip=data_offset, strip_bytes=n * per * POINT_STRIDE, strip_points=per)
                    data_offset += meta['strip_bytes']
                    meta.update(strip_state=data_offset, strip_state_bytes=n * RIBBON_STATE_STRIDE)
                    data_offset += meta['strip_state_bytes']
                self.emitters.append(e)
                self.meta.append(meta)
            except (rn.NativeFault, rn.UnresolvedImportCalled):
                raise  # the image may be damaged: fatal
            except Exception as ex:
                # A Python-level refusal (unsupported attribute, missing factory, bad value) leaves the
                # image intact: skip this entry and keep loading the rest. Its arena allocations stay
                # until the next base-snapshot restore.
                skipped.append(dict(entry=i, kind=type(ex).__name__, message=str(ex)))
        if not self.emitters:
            raise ValueError('no entry could be built: ' + '; '.join(f"{s['entry']}: {s['message']}" for s in skipped))
        self.active_entry = None
        self.slot_bytes = (data_offset + 63) & ~63
        self.track_capacity = 36000
        self.input_offset = HEADER + 3 * self.slot_bytes
        self.mapping_bytes = self.input_offset + self.track_capacity * 68
        self.snapshot = Snapshot(self.img)
        self.world_written = {}
        self.set_track([1 / 60] * 3600, [])
        return dict(entries=self.meta, slot_bytes=self.slot_bytes, mapping_bytes=self.mapping_bytes,
                    input_offset=self.input_offset, track_capacity=self.track_capacity,
                    snapshot_bytes=self.snapshot.bytes, load_ms=(time.perf_counter() - start) * 1000,
                    reused=reused, base_restore_ms=base_ms, skipped=skipped)

    def set_track(self, dts, worlds):
        if not dts or any(not math.isfinite(v) or v <= 0 or v > 1 for v in dts):
            raise ValueError('dt must be finite and in (0, 1] seconds')
        if worlds and (len(worlds) != len(dts) or any(len(w) != 16 or any(not math.isfinite(x) for x in w) for w in worlds)):
            raise ValueError('matrix track must contain one finite 4x4 matrix per tick')
        self.dts = list(struct.unpack('<%df' % len(dts), struct.pack('<%df' % len(dts), *dts)))
        self.worlds = worlds
        if hasattr(self, 'snapshot'):
            self.snapshot.restore()
            self.world_written = {}
        self.tick, self.elapsed = 0, 0.

    def bind(self, cmd):
        self.mapping = mmap.mmap(-1, self.mapping_bytes, tagname=cmd['name'])
        self.mapping[:HEADER] = bytes(HEADER)
        struct.pack_into('<4s7I', self.mapping, 0, b'R4FX', VERSION, rn.KNOWN_TIMESTAMP, 3,
                         self.slot_bytes, self.input_offset, self.track_capacity, self.mapping_bytes)
        return dict(bound=True)

    def track(self, cmd):
        n = int(cmd['length'])
        if not 1 <= n <= self.track_capacity:
            raise ValueError('invalid track length')
        dts = struct.unpack_from('<%df' % n, self.mapping, self.input_offset)
        worlds = []
        if cmd.get('matrices', False):
            worlds = [struct.unpack_from('<16f', self.mapping, self.input_offset + self.track_capacity * 4 + i * 64) for i in range(n)]
        self.set_track(dts, worlds)
        return dict(length=n)

    @staticmethod
    def fixend_points(block, meta, out):
        # RibbonFixEnd: state +0x08 -> that particle's points, +0x18 live count c (1 at spawn, capped at N).
        n, per = meta['capacity'], meta['strip_points']
        ctypes.memset(out + meta['strip_state'], 0, meta['strip_state_bytes'])
        states = rn.u64(block + 0x40)
        if not states:
            return
        for i in range(n):
            st = states + FIXEND_STATE_STRIDE * i
            ptr, count = rn.u64(st + 8), rn.u32(st + 0x18)
            if not ptr:
                continue
            ctypes.memmove(out + meta['strip'] + i * per * POINT_STRIDE, ptr, per * POINT_STRIDE)
            struct.pack_into('<I', (ctypes.c_char * 4).from_address(out + meta['strip_state'] + i * RIBBON_STATE_STRIDE + 0x18), 0, min(count, per))

    @staticmethod
    def trail_columns(block, meta, out):
        # PolygonTrail: the engine's vertex grid (+0x308, slot * vertices, column-major with `rows` vertices per
        # column; vertex +0x00 world position, +0x0c row / (rows-1), +0x10 U along the trail, +0x14 RGBA8).
        # Rows between the first and the last are linear (RUNTIME 4.3.9), so each column is exported as one point
        # record: +0x00 base (row 0), +0x18 tip (last row), +0x40 U, +0x44 colour, +0x48 = 1. The live column
        # count is not stored per particle; the last live column is the first with U = 1.0.
        n, columns, vertices = meta['capacity'], meta['strip_points'], meta['trail_vertices']
        rows = vertices // columns
        grid = rn.u64(block + 0x308)
        ctypes.memset(out + meta['strip_state'], 0, meta['strip_state_bytes'])
        if not grid:
            return
        points = (ctypes.c_char * meta['strip_bytes']).from_address(out + meta['strip'])
        states = (ctypes.c_char * meta['strip_state_bytes']).from_address(out + meta['strip_state'])
        for i in range(n):
            raw = ctypes.string_at(grid + i * vertices * TRAIL_VERTEX_STRIDE, vertices * TRAIL_VERTEX_STRIDE)
            live = 0
            for c in range(columns):
                b, t = c * rows * TRAIL_VERTEX_STRIDE, (c * rows + rows - 1) * TRAIL_VERTEX_STRIDE
                at = (i * columns + c) * POINT_STRIDE
                u = struct.unpack_from('<f', raw, b + 0x10)[0]
                points[at:at + 12] = raw[b:b + 12]
                points[at + 0x18:at + 0x24] = raw[t:t + 12]
                struct.pack_into('<fIf', points, at + 0x40, u, struct.unpack_from('<I', raw, b + 0x14)[0], 1.0)
                if u == 1.0 and not live:
                    live = c + 1
            struct.pack_into('<I', states, i * RIBBON_STATE_STRIDE + 0x18, live if live >= 2 else 0)

    def frame(self, cmd):
        self.active_entry = None
        target, slot = int(cmd['tick']), int(cmd['slot'])
        if not 0 <= target <= len(self.dts) or slot not in range(3) or self.mapping is None:
            raise ValueError('frame target/slot is invalid or mapping is unbound')
        start = time.perf_counter()
        restore_ms = 0.
        if target < self.tick:
            self.snapshot.restore()
            self.world_written = {}
            self.tick, self.elapsed = 0, 0.
            restore_ms = (time.perf_counter() - start) * 1000
        sim_start = time.perf_counter()
        try:
            while self.tick < target:
                dt = self.dts[self.tick]
                matrix = self.worlds[self.tick] if self.worlds else IDENTITY
                rows = [matrix[i:i + 4] for i in range(0, 16, 4)]
                for i, e in enumerate(self.emitters):
                    self.active_entry = i
                    # U+0x40 is only written here: skip the write while the matrix is unchanged.
                    # A snapshot restore rewinds U as well, so the cache is cleared with it.
                    if self.world_written.get(i) != matrix:
                        e.set_world(rows)
                        self.world_written[i] = matrix
                    e.tick(dt)
                self.tick += 1
                self.elapsed += dt
                em.CALLS.clear()  # service logging must not grow for the lifetime of playback
        except rn.NativeFault:
            raise  # fatal: the faulted image is never reused
        except Exception as ex:
            # Some emitters of this tick may have advanced and others not. Rewind to the load
            # snapshot so the next request replays from tick 0; the error itself stays non-fatal.
            self.snapshot.restore()
            self.world_written = {}
            self.tick, self.elapsed = 0, 0.
            raise RecoveredError(f'{type(ex).__name__}: {ex}; state rewound to tick 0') from ex
        self.active_entry = None
        sim_ms = (time.perf_counter() - sim_start) * 1000
        base = HEADER + slot * self.slot_bytes
        dt = self.dts[target - 1] if target else 0.
        struct.pack_into('<IIdfI', self.mapping, base, target, len(self.emitters), self.elapsed, dt, 0)
        hashes, alive_counts = [], []
        # memmove directly into the mapping: no Python per-particle decoding on the hot path.
        address = ctypes.addressof(ctypes.c_char.from_buffer(self.mapping))
        for i, (e, meta) in enumerate(zip(self.emitters, self.meta)):
            pos = base + SLOT_HEADER + i * ENTRY_SIZE
            flags = [rn.u32(e.E + off) for off in (0x168, 0x16c, 0x170, 0x174)]
            struct.pack_into('<16I', self.mapping, pos, meta['entry'], meta['type'], meta['capacity'], STRIDE,
                             meta['side_stride'], rn.u32(e.E + 0x100), *flags, meta['particles'], meta['particle_bytes'],
                             meta['side'], meta['side_bytes'], meta['block'], meta['block_bytes'])
            matrix = self.worlds[target - 1] if self.worlds and target else IDENTITY
            struct.pack_into('<16f', self.mapping, pos + 64, *matrix)
            for key, ptr in [('particles', rn.u64(e.E + 0x190)), ('side', rn.u64(e.E + 0x198))]:
                length = meta['particle_bytes' if key == 'particles' else 'side_bytes']
                if length:
                    ctypes.memmove(address + base + meta[key], ptr, length)
            block = e.block_of(meta['type'])
            ctypes.memmove(address + base + meta['block'], block, meta['block_bytes'])
            struct.pack_into('<8I', self.mapping, pos + 128, meta['strip'], meta['strip_bytes'], POINT_STRIDE if meta['strip_bytes'] else 0,
                             meta['strip_points'], meta['strip_state'], meta['strip_state_bytes'],
                             RIBBON_STATE_STRIDE if meta['strip_state_bytes'] else 0, 0)
            if meta['strip_bytes']:
                if meta['type'] == 70:
                    self.trail_columns(block, meta, address + base)
                elif meta['type'] == 34:
                    self.fixend_points(block, meta, address + base)
                else:
                    points, states = rn.u64(block + 0x38), rn.u64(block + 0x40)
                    if points and states:
                        ctypes.memmove(address + base + meta['strip'], points, meta['strip_bytes'])
                        ctypes.memmove(address + base + meta['strip_state'], states, meta['strip_state_bytes'])
                    else:
                        ctypes.memset(address + base + meta['strip_state'], 0, meta['strip_state_bytes'])
            if cmd.get('diagnostics'):
                raw = self.mapping[base + meta['particles']:base + meta['particles'] + meta['particle_bytes']]
                hashes.append(hashlib.sha256(raw).hexdigest())
                alive_counts.append(sum(bool(struct.unpack_from('<I', raw, k)[0] & 1) for k in range(0, len(raw), STRIDE)))
        return dict(tick=target, slot=slot, sim_ms=sim_ms, restore_ms=restore_ms,
                    total_ms=(time.perf_counter() - start) * 1000, hashes=hashes, alive=alive_counts)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', default=os.environ.get('RE4_EXE', rn.DEFAULT_EXE))
    parser.add_argument('--resources', help='optional natives/stm root for built-in fixtures')
    parser.add_argument('--allow-test-faults', action='store_true')
    args = parser.parse_args()
    os.environ['RE4_EXE'] = args.exe
    host = Host(args)
    for line in sys.stdin:
        cmd = {}
        fatal = False
        try:
            cmd = json.loads(line)
            name = cmd['command']
            if name == 'shutdown':
                print(json.dumps(dict(id=cmd.get('id'), ok=True)), flush=True)
                break
            if name == 'test_fault' and args.allow_test_faults:
                if cmd.get('exit'):
                    os._exit(73)
                # Use the same bad-owner seeder call as exp_map.py, so the fault has an exe RVA.
                owner = host.img.alloc(32)
                rn.set_u64(owner + 8, 0x10)
                fn = host.img.function(0x3D09990, ctypes.c_void_p, ctypes.c_void_p,
                                       ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32)
                host.img.call(fn, host.img.alloc(16), 0x27, owner, 0)
                raise RuntimeError('test fault unexpectedly returned')
            elif name in ('load', 'bind', 'track', 'frame'):
                result = getattr(host, name)(cmd)
            else:
                raise ValueError('unknown command or disabled test fault')
            reply = dict(id=cmd.get('id'), ok=True, **result)
        except Exception as ex:
            # A failed snapshot restore, or any error while advancing that could not be rewound,
            # leaves state unknown: end the process instead of answering further frames.
            fatal = (isinstance(ex, (rn.NativeFault, rn.UnresolvedImportCalled)) or cmd.get('command') == 'load'
                     or (cmd.get('command') == 'frame' and host.active_entry is not None and not isinstance(ex, RecoveredError)))
            error = dict(kind=type(ex).__name__, message=str(ex), entry=host.active_entry)
            if isinstance(ex, rn.NativeFault):
                error.update(rva=hex(ex.rip - rn.IMAGE_BASE), backtrace=[hex(v - rn.IMAGE_BASE) for v in ex.backtrace])
            reply = dict(id=cmd.get('id'), ok=False, fatal=fatal, error=error)
        print(json.dumps(reply), flush=True)
        if fatal:
            break
    if host.mapping:
        host.mapping.close()


if __name__ == '__main__':
    main()

