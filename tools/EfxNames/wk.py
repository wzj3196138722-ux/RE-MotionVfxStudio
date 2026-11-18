"""Static Wilds/RE4 exe toolkit with cached indices (numpy). Reads only."""
import os, struct, re, functools, bisect
import numpy as np
import pefile, capstone

EXES = {'re4': os.environ.get('RE4_EXE') or r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe",
        'wilds': os.environ.get('MHWILDS_EXE') or r"E:\Data\Github\RE-MotionVfxStudio\sample\Wilds\MonsterHunterWilds.exe"}
from paths import OUT
CACHE = os.path.join(OUT, 'cache')
os.makedirs(CACHE, exist_ok=True)
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = False


class PE:
    def __init__(s, g):
        s.g = g
        s.d = open(EXES[g], 'rb').read()
        p = pefile.PE(data=s.d, fast_load=True)
        s.base = p.OPTIONAL_HEADER.ImageBase
        s.secs = [(x.Name.rstrip(b'\0').decode(errors='replace'), x.VirtualAddress, max(x.Misc_VirtualSize, x.SizeOfRawData),
                   x.PointerToRawData, x.SizeOfRawData, x.Characteristics) for x in p.sections]
        ed = p.OPTIONAL_HEADER.DATA_DIRECTORY[3]
        s.pdata = (ed.VirtualAddress, ed.Size)
        s.u8 = np.frombuffer(s.d, np.uint8)

    def sec(s, name):
        return next(x for x in s.secs if x[0] == name)

    def off2rva(s, o):
        for n, va, vs, ro, rs, ch in s.secs:
            if ro <= o < ro + rs: return va + o - ro

    def rva2off(s, r):
        for n, va, vs, ro, rs, ch in s.secs:
            if va <= r < va + rs: return ro + r - va

    def q(s, r): return struct.unpack_from('<Q', s.d, s.rva2off(r))[0]
    def i32(s, r): return struct.unpack_from('<i', s.d, s.rva2off(r))[0]
    def u32(s, r): return struct.unpack_from('<I', s.d, s.rva2off(r))[0]
    def f32(s, r): return struct.unpack_from('<f', s.d, s.rva2off(r))[0]

    def execsecs(s): return [x for x in s.secs if x[5] & 0x20000000]

    def secname(s, r):
        for n, va, vs, ro, rs, ch in s.secs:
            if va <= r < va + vs: return n

    @functools.cached_property
    def funcs(s):
        o = s.rva2off(s.pdata[0]); n = s.pdata[1] // 12
        a = np.frombuffer(s.d, '<u4', n * 3, o).reshape(-1, 3)
        b = a[:, 0].astype(np.int64); e = a[:, 1].astype(np.int64)
        i = np.argsort(b)
        return b[i], e[i]

    def func_of(s, r):
        b, e = s.funcs
        i = int(np.searchsorted(b, r, side='right')) - 1
        return (int(b[i]), int(e[i])) if i >= 0 and b[i] <= r < e[i] else None

    def fstart(s, x):
        f = s.func_of(x)
        if f: return f[0]
        o = s.rva2off(x); win = s.d[max(0, o - 0x8000):o]
        i = win.rfind(b'\xcc\xcc')
        if i < 0: return None
        j = i + 2
        while j < len(win) and win[j] == 0xcc: j += 1
        return x - (len(win) - j)

    # ---- indices
    def _cached(s, name, fn):
        p = os.path.join(CACHE, f'{s.g}_{name}.npz')
        if os.path.exists(p):
            z = np.load(p); return tuple(z[k] for k in sorted(z.files))
        r = fn(); np.savez(p, **{f'a{i}': x for i, x in enumerate(r)}); return r

    @functools.cached_property
    def branches(s):
        """(op, site, target) for E8/E9 rel32 in exec sections."""
        def build():
            ops = []; sites = []; tg = []
            for n, va, vs, ro, rs, ch in s.execsecs():
                a = s.u8[ro:ro + rs]; L = len(a) - 5
                op = a[:L]
                m = np.nonzero((op == 0xE8) | (op == 0xE9))[0]
                d = (a[m + 1].astype(np.int64) | (a[m + 2].astype(np.int64) << 8) | (a[m + 3].astype(np.int64) << 16) | (a[m + 4].astype(np.int64) << 24))
                d = np.where(d >= 2 ** 31, d - 2 ** 32, d)
                site = va + m
                ops.append(op[m]); sites.append(site); tg.append(site + 5 + d)
            return np.concatenate(ops), np.concatenate(sites), np.concatenate(tg)
        return s._cached('br', build)

    def callers(s, t, op=None):
        o, si, tg = s.branches
        m = tg == t
        if op is not None: m &= o == op
        return [int(x) for x in si[m]]

    @functools.cached_property
    def riprefs(s):
        """(site, target, insn_len) for rip-relative modrm forms with REX 48/4C and opcode 8D/8B/89/..., and plain 8B/8D/89 without REX."""
        def build():
            sites = []; tg = []
            for n, va, vs, ro, rs, ch in s.execsecs():
                a = s.u8[ro:ro + rs]; L = len(a) - 8
                modrm = a[2:L + 2]
                rex = (a[:L] & 0xF0) == 0x40
                opc = a[1:L + 1]
                okop = np.isin(opc, np.array([0x8D, 0x8B, 0x89, 0x63, 0x3B, 0x39, 0x03, 0x2B], np.uint8))
                okm = (modrm & 0xC7) == 0x05
                m = np.nonzero(rex & okop & okm)[0]
                d = (a[m + 3].astype(np.int64) | (a[m + 4].astype(np.int64) << 8) | (a[m + 5].astype(np.int64) << 16) | (a[m + 6].astype(np.int64) << 24))
                d = np.where(d >= 2 ** 31, d - 2 ** 32, d)
                sites.append(va + m); tg.append(va + m + 7 + d)
            return np.concatenate(sites), np.concatenate(tg)
        return s._cached('rip', build)

    def xrefs(s, t):
        si, tg = s.riprefs
        return [int(x) for x in si[tg == t]]

    def qrefs(s, t):
        """Offsets (rva) of 8-byte absolute pointers to rva t in non-exec sections."""
        pat = struct.pack('<Q', s.base + t)
        out = []
        for m in re.finditer(re.escape(pat), s.d):
            r = s.off2rva(m.start())
            if r is not None: out.append(r)
        return out

    def strs(s, txt):
        res = []
        for pat, k in ((b'\0' + txt.encode() + b'\0', 'a'), (b'\0\0' + txt.encode('utf-16le') + b'\0\0', 'w')):
            for m in re.finditer(re.escape(pat), s.d):
                r = s.off2rva(m.start() + (1 if k == 'a' else 2))
                if r: res.append((r, k))
        return res

    def dis(s, r, n=40, stop=True):
        o = s.rva2off(r); xs = []
        for x in md.disasm(s.d[o:o + n * 15], r):
            xs.append(x)
            if len(xs) >= n or (stop and x.mnemonic in ('ret', 'int3')): break
        return xs

    def disf(s, f):
        b, e = s.func_of(f) or (f, None)
        if e is None:
            return s.dis(f, 4000)
        o = s.rva2off(b)
        return list(md.disasm(s.d[o:o + (e - b)], b))

    def riptarget(s, x):
        m = re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', x.op_str)
        if m: return x.address + x.size + int(m.group(2), 16) * (1 if m.group(1) == '+' else -1)

    def cstr(s, r, lim=80):
        o = s.rva2off(r)
        if o is None: return None
        e = s.d.find(b'\0', o, o + lim); b = s.d[o:e]
        if e > o and len(b) >= 2 and all(32 <= c < 127 for c in b): return b.decode()
        # utf16
        b2 = s.d[o:o + lim * 2]
        try:
            t = b2.decode('utf-16le').split('\0')[0]
            if len(t) >= 2 and all(32 <= ord(c) < 127 for c in t): return 'w:' + t
        except Exception:
            pass
        return None

    def pr(s, xs):
        for x in xs:
            t = s.riptarget(x)
            extra = ''
            if t is not None:
                st = s.cstr(t)
                extra = f'  ; {t:#x}' + (f' "{st}"' if st else '')
            print(f'  {x.address:#x} {x.mnemonic} {x.op_str}{extra}')


@functools.lru_cache(None)
def get(g): return PE(g)
