"""Emulate MonsterHunterWilds.exe functions under unicorn (image mapped from the file sections).

The anti-tamper code in .trace reads KUSER_SHARED_DATA and the PEB; both are provided (KUSER copied
from this process, PEB a zeroed block with ImageBaseAddress). AVX instructions unicorn rejects are
stepped by avx_step. Functions are patched with int3 handlers via Emu.patch(rva, fn).
"""
import collections
import ctypes
import struct

import capstone
import pefile
import unicorn
from capstone import x86 as cx
from unicorn import *
from unicorn.x86_const import *

import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from wk import EXES
EXE = EXES['wilds']
B = 0x140000000
STACK, STK = 0x7f0000000, 0x200000
HEAP, HSZ = 0x800000000, 0x8000000
RET = 0x7ff000000
ALLOC = 0xb020830   # alloc(size): mov edx,0x10; jmp f5b0

_cs = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
_cs.detail = True
INT3 = bytes([0xCC])
HLT = bytes([0xF4])


class Emu:
    def __init__(s):
        d = open(EXE, 'rb').read()
        p = pefile.PE(data=d, fast_load=True)
        s.size = p.OPTIONAL_HEADER.SizeOfImage
        u = s.u = Uc(UC_ARCH_X86, UC_MODE_64)
        u.mem_map(B, s.size)
        u.mem_write(B, d[:0x1000])
        for x in p.sections:
            n = min(x.SizeOfRawData, x.Misc_VirtualSize or x.SizeOfRawData)
            if n:
                u.mem_write(B + x.VirtualAddress, d[x.PointerToRawData:x.PointerToRawData + n])
        u.mem_map(STACK, STK)
        u.mem_map(HEAP, HSZ)
        u.mem_map(RET, 0x1000)
        u.mem_write(RET, HLT)
        u.mem_map(0x7ffe0000, 0x1000)
        u.mem_write(0x7ffe0000, ctypes.string_at(0x7ffe0000, 0x1000))
        s.teb = HEAP + HSZ - 0x10000
        u.msr_write(0xC0000101, s.teb)                       # gs base
        u.mem_write(s.teb + 0x30, struct.pack('<Q', s.teb))
        u.mem_write(s.teb + 0x58, struct.pack('<Q', s.teb + 0x1000))   # TLS array
        for i in range(64):
            u.mem_write(s.teb + 0x1000 + 8 * i, struct.pack('<Q', s.teb + 0x2000 + 0x100 * i))
        s.hp = HEAP + 0x1000
        s.patches = {}
        peb = s.malloc(0x1000)
        u.mem_write(s.teb + 0x60, struct.pack('<Q', peb))
        u.mem_write(peb + 0x10, struct.pack('<Q', B))
        s.faults = []
        s.blocks = None
        u.hook_add(UC_HOOK_INTR, s._intr)
        u.hook_add(UC_HOOK_MEM_INVALID, s._bad)
        s.patch(ALLOC, lambda e, u: e.zalloc(u.reg_read(UC_X86_REG_RCX)))

    def malloc(s, n):
        a = (s.hp + 15) & ~15
        s.hp = a + n
        return a

    def zalloc(s, n):
        a = s.malloc(n)
        s.u.mem_write(a, bytes(n))
        return a

    def patch(s, rva, fn):
        s.patches[rva] = fn
        s.u.mem_write(B + rva, INT3)

    def _intr(s, u, intno, ud):
        rip = u.reg_read(UC_X86_REG_RIP) - 1
        if rip - B in s.patches:
            v = s.patches[rip - B](s, u)
            if v is not None:
                u.reg_write(UC_X86_REG_RAX, v)
            rsp = u.reg_read(UC_X86_REG_RSP)
            u.reg_write(UC_X86_REG_RIP, s.q(rsp))
            u.reg_write(UC_X86_REG_RSP, rsp + 8)
        else:
            s.faults.append(('int', intno, rip))
            u.emu_stop()

    def _bad(s, u, acc, addr, size, val, ud):
        s.faults.append(('mem', acc, addr, u.reg_read(UC_X86_REG_RIP)))
        return False

    def trace_on(s, n=40):
        s.blocks = collections.deque(maxlen=n)
        s.u.hook_add(UC_HOOK_BLOCK, lambda u, a, sz, ud: s.blocks.append(a - B))

    def call(s, rva, args=(), count=200_000_000):
        u = s.u
        rsp = STACK + STK - 0x1000
        u.mem_write(rsp, struct.pack('<Q', RET))
        for r, v in zip([UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9], args):
            u.reg_write(r, v)
        u.reg_write(UC_X86_REG_RSP, rsp)
        s.faults = []
        pc = B + rva
        s.avx = 0
        while True:
            try:
                u.emu_start(pc, RET, count=count)
                return u.reg_read(UC_X86_REG_RAX)
            except UcError as e:
                rip = u.reg_read(UC_X86_REG_RIP)
                if e.errno == UC_ERR_INSN_INVALID and _safe_avx(u, rip):
                    s.avx += 1
                    pc = u.reg_read(UC_X86_REG_RIP)
                    continue
                try:
                    x = next(_cs.disasm(bytes(u.mem_read(rip, 15)), rip), None)
                except UcError:
                    x = None
                return ('err', str(e), hex(rip - B), x and (x.mnemonic + ' ' + x.op_str), s.faults)

    def q(s, a): return struct.unpack('<Q', s.u.mem_read(a, 8))[0]
    def d(s, a): return struct.unpack('<I', s.u.mem_read(a, 4))[0]


import unicorn.x86_const as _x86c


def _reg(name): return getattr(_x86c, 'UC_X86_REG_' + name.upper())


def _ea(u, op, x):
    m = op.mem
    a = m.disp
    if m.base:
        a += x.address + x.size if m.base == cx.X86_REG_RIP else u.reg_read(_reg(x.reg_name(m.base)))
    if m.index:
        a += u.reg_read(_reg(x.reg_name(m.index))) * m.scale
    return a & 0xffffffffffffffff


def _rd(u, x, op, n):
    if op.type == cx.X86_OP_REG:
        return u.reg_read(_reg(x.reg_name(op.reg))) & ((1 << (8 * n)) - 1)
    return int.from_bytes(u.mem_read(_ea(u, op, x), n), 'little')


def _wr(u, x, op, v, n):
    if op.type == cx.X86_OP_REG:
        name = x.reg_name(op.reg)
        u.reg_write(_reg('y' + name[1:] if name.startswith('xmm') else name), v)   # VEX zero-extends
    else:
        u.mem_write(_ea(u, op, x), v.to_bytes(n, 'little'))


def avx_step(u, rip):
    x = next(_cs.disasm(bytes(u.mem_read(rip, 15)), rip), None)
    if x is None:
        return False
    m, ops = x.mnemonic, x.operands

    def width(op):
        if op.type == cx.X86_OP_REG:
            n = x.reg_name(op.reg)
            return 32 if n.startswith('y') else 16 if n.startswith('x') else 8
        return op.size

    full = lambda n: (1 << 8 * n) - 1
    if m == 'vzeroupper':
        for i in range(16):
            u.reg_write(_reg('ymm%d' % i), u.reg_read(_reg('ymm%d' % i)) & full(16))
    elif m in ('vmovups', 'vmovaps', 'vmovdqu', 'vmovdqa', 'vmovdqu64', 'vmovdqu32', 'vmovupd', 'vmovapd') and len(ops) == 2:
        n = width(ops[0]) if ops[0].type == cx.X86_OP_REG else width(ops[1])
        _wr(u, x, ops[0], _rd(u, x, ops[1], n), n)
    elif m in ('vxorps', 'vpxor', 'vxorpd', 'vandps', 'vpand', 'vorps', 'vpor', 'vandnps', 'vpandn') and len(ops) == 3:
        n = width(ops[0])
        a, b = _rd(u, x, ops[1], n), _rd(u, x, ops[2], n)
        r = {'vxorps': a ^ b, 'vpxor': a ^ b, 'vxorpd': a ^ b, 'vandps': a & b, 'vpand': a & b,
             'vorps': a | b, 'vpor': a | b}.get(m)
        if r is None:
            r = (~a & full(n)) & b
        _wr(u, x, ops[0], r, n)
    elif m in ('vcmptrueps', 'vpcmpeqd', 'vpcmpeqb', 'vpcmpeqq') and len(ops) == 3 \
            and ops[2].type == cx.X86_OP_REG and ops[1].reg == ops[2].reg:
        n = width(ops[0])
        _wr(u, x, ops[0], full(n), n)
    elif m == 'vbroadcastss':
        n = width(ops[0])
        v = _rd(u, x, ops[1], 4)
        _wr(u, x, ops[0], sum(v << (32 * i) for i in range(n // 4)), n)
    elif m in ('vinsertf128', 'vinserti128'):
        a, b, k = _rd(u, x, ops[1], 32), _rd(u, x, ops[2], 16), ops[3].imm & 1
        _wr(u, x, ops[0], ((a & ~(full(16) << 128 * k)) | (b << 128 * k)) & full(32), 32)
    elif m in ('vextractf128', 'vextracti128'):
        a, k = _rd(u, x, ops[1], 32), ops[2].imm & 1
        _wr(u, x, ops[0], (a >> 128 * k) & full(16), 16)
    else:
        return False
    u.reg_write(UC_X86_REG_RIP, rip + x.size)
    return True


def _safe_avx(u, rip):
    try:
        return avx_step(u, rip)
    except Exception:
        return False
