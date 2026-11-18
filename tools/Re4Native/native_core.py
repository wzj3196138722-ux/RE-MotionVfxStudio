"""Map a RE Engine exe at its image base in this process and call its functions.

This file knows nothing about any one game. The build an experiment expects (file size, PE time
stamp, default path) comes from a profile in games/; re4native.py and mhwildsnative.py bind a
profile and add the engine state their game's code reads.

The headers and sections are copied to 0x140000000 unchanged, so no relocation is applied and every
static address is live. Nothing of the exe runs at load: no entry point, no TLS callbacks, no CRT
initialisers. Imports from kernel32 / the MSVC runtime / the UCRT are bound with LoadLibrary +
GetProcAddress; every other import (d3d, Steam, sockets, ...) points at its own 16-byte slot in a
reserved no-access page, so a call into one raises UnresolvedImportCalled with the import's name.

Faults: a vectored exception handler records the faulting instruction and an exe-only backtrace
(the exe's .pdata is registered with RtlAddFunctionTable), and call() turns ctypes' OSError into
NativeFault. A fast fail (0xC0000409, stack cookie or __fastfail) cannot be caught: it ends the
process. Run every experiment in a child process (run_all.py does).

Only one image can be mapped per process: load() maps once and then returns the same object.
"""
from __future__ import annotations

import ctypes
import os
import re
import struct
from pathlib import Path

from games import GameBuild

IMAGE_BASE = 0x140000000

_RESOLVED_DLLS = {"kernel32.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll",
                  "concrt140.dll"}
_TRAP_SLOT = 16

MEM_COMMIT, MEM_RESERVE = 0x1000, 0x2000
PAGE_NOACCESS, PAGE_READWRITE, PAGE_EXECUTE_READWRITE = 0x01, 0x04, 0x40

_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_k32.VirtualAlloc.restype = ctypes.c_void_p
_k32.VirtualAlloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint32, ctypes.c_uint32]
_k32.LoadLibraryW.restype = ctypes.c_void_p
_k32.LoadLibraryW.argtypes = [ctypes.c_wchar_p]
_k32.GetProcAddress.restype = ctypes.c_void_p
_k32.GetProcAddress.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
_k32.RtlAddFunctionTable.restype = ctypes.c_bool
_k32.RtlAddFunctionTable.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint64]

_IMAGE: "ExeImage | None" = None


class WrongBuild(RuntimeError):
    """The file is not the exe build whose addresses this folder uses."""


class UnresolvedImportCalled(RuntimeError):
    """Native code called an import that the harness deliberately left unresolved."""


class NativeFault(RuntimeError):
    """An access violation inside native code: faulting instruction (RVA), the address it touched,
    and the return addresses inside the exe on the stack at that moment (RVAs, innermost first)."""

    def __init__(self, rip: int, access: str, address: int, backtrace: tuple[int, ...] = (), kind: str = "access violation"):
        self.rip, self.access, self.address, self.backtrace, self.kind = rip, access, address, backtrace, kind
        chain = " <- ".join(_fmt(f) for f in backtrace[:10])
        detail = f": {access} {address:#x}" if kind == "access violation" else ""
        super().__init__(f"{kind} at {_fmt(rip)}{detail}" + (f" [{chain}]" if chain else ""))


def _fmt(addr: int) -> str:
    if IMAGE_BASE <= addr < _IMAGE_SPAN[1]:
        return f"{addr - IMAGE_BASE:x}"
    return f"{addr:#x}"


# A vectored exception handler sees the access violation before ctypes' SEH frame turns it into
# OSError, with the faulting instruction that OSError's message lacks.
_VEH = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p)
_k32.AddVectoredExceptionHandler.restype = ctypes.c_void_p
_k32.AddVectoredExceptionHandler.argtypes = [ctypes.c_uint32, _VEH]
_k32.RemoveVectoredExceptionHandler.argtypes = [ctypes.c_void_p]
_ACCESS = {0: "read", 1: "write", 8: "execute"}
_last_fault: list = [None]
# Exceptions reported as NativeFault with the faulting instruction and backtrace.
_FAULT_KINDS = {0xC0000005: "access violation", 0xC0000094: "integer divide by zero"}
_k32.RtlLookupFunctionEntry.restype = ctypes.c_void_p
_k32.RtlLookupFunctionEntry.argtypes = [ctypes.c_uint64, ctypes.POINTER(ctypes.c_uint64), ctypes.c_void_p]
_k32.RtlVirtualUnwind.restype = ctypes.c_void_p
_k32.RtlVirtualUnwind.argtypes = [ctypes.c_uint32, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_void_p,
                                  ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p),
                                  ctypes.POINTER(ctypes.c_uint64), ctypes.c_void_p]
_CONTEXT_SIZE, _CTX_RSP, _CTX_RIP = 0x4D0, 0x98, 0xF8   # x64 CONTEXT
_ctx_buf = (ctypes.c_ubyte * (_CONTEXT_SIZE + 16))()
_IMAGE_SPAN = [IMAGE_BASE, IMAGE_BASE]   # end set when an image is mapped


def _exe_backtrace(context: int, limit: int = 32) -> tuple[int, ...]:
    """Unwind a copy of the faulting CONTEXT through the exe's frames (its table is registered).
    A frame outside the exe (a trap slot) is unwound as a leaf: its return address is at rsp."""
    ctx = ctypes.addressof(_ctx_buf) + (-ctypes.addressof(_ctx_buf)) % 16
    ctypes.memmove(ctx, context, _CONTEXT_SIZE)
    frames = []
    base, handler_data, establisher = ctypes.c_uint64(), ctypes.c_void_p(), ctypes.c_uint64()
    seen_exe = False
    for i in range(limit):
        rip = ctypes.c_uint64.from_address(ctx + _CTX_RIP).value
        inside = _IMAGE_SPAN[0] <= rip < _IMAGE_SPAN[1]
        if not inside and seen_exe:
            break                                  # left the exe again: back in ctypes / Python
        if inside:
            seen_exe = True
            frames.append(rip)
        elif i > 0 and i > 8:
            break
        # system DLL frames (a fault inside kernel32 / ntdll called by the exe) unwind through their
        # own registered tables; only exe frames are reported
        entry = _k32.RtlLookupFunctionEntry(rip, ctypes.byref(base), None)
        if entry:
            _k32.RtlVirtualUnwind(0, base.value, rip, entry, ctx, ctypes.byref(handler_data),
                                  ctypes.byref(establisher), None)
        else:  # leaf (or a trap slot): the return address is on top of the stack
            rsp = ctypes.c_uint64.from_address(ctx + _CTX_RSP)
            ctypes.c_uint64.from_address(ctx + _CTX_RIP).value = ctypes.c_uint64.from_address(rsp.value).value
            rsp.value += 8
    return tuple(frames)


def _on_exception(pointers):
    record = ctypes.c_void_p.from_address(pointers).value       # EXCEPTION_POINTERS.ExceptionRecord
    context = ctypes.c_void_p.from_address(pointers + 8).value  # EXCEPTION_POINTERS.ContextRecord
    code = ctypes.c_uint32.from_address(record).value
    if code in _FAULT_KINDS:
        try:
            frames = _exe_backtrace(context)
        except Exception:
            frames = ()
        _last_fault[0] = (ctypes.c_void_p.from_address(record + 16).value or 0,  # ExceptionAddress
                          ctypes.c_size_t.from_address(record + 32).value,       # Information[0]
                          ctypes.c_size_t.from_address(record + 40).value,       # Information[1]
                          frames, _FAULT_KINDS[code])
    return 0  # EXCEPTION_CONTINUE_SEARCH: ctypes still handles it


_veh_callback = _VEH(_on_exception)


def _pe_layout(header: bytes) -> dict:
    pe = struct.unpack_from("<I", header, 0x3C)[0]
    if header[:2] != b"MZ" or header[pe:pe + 4] != b"PE\0\0":
        raise WrongBuild("not a PE file")
    nsec = struct.unpack_from("<H", header, pe + 6)[0]
    timestamp = struct.unpack_from("<I", header, pe + 8)[0]
    optsz = struct.unpack_from("<H", header, pe + 20)[0]
    opt = pe + 24
    dd = opt + 112
    sections = []
    for i in range(nsec):
        name, vsize, va, rawsz, rawptr = struct.unpack_from("<8sIIII", header, opt + optsz + 40 * i)
        sections.append((name.rstrip(b"\0").decode(errors="replace"), vsize, va, rawsz, rawptr))
    return dict(timestamp=timestamp,
                base=struct.unpack_from("<Q", header, opt + 24)[0],
                size_of_image=struct.unpack_from("<I", header, opt + 56)[0],
                size_of_headers=struct.unpack_from("<I", header, opt + 60)[0],
                import_rva=struct.unpack_from("<I", header, dd + 8)[0],
                exception_rva=struct.unpack_from("<I", header, dd + 3 * 8)[0],
                exception_size=struct.unpack_from("<I", header, dd + 3 * 8 + 4)[0],
                tls_rva=struct.unpack_from("<I", header, dd + 9 * 8)[0],
                sections=sections)


def check_build(build: GameBuild, path) -> dict:
    """Raise WrongBuild unless `path` is the build `build` was analysed on; return its PE layout."""
    path = Path(path)
    size = path.stat().st_size
    with open(path, "rb") as f:
        header = f.read(4096)
    layout = _pe_layout(header)
    if size != build.size or layout["timestamp"] != build.timestamp or layout["base"] != IMAGE_BASE:
        raise WrongBuild(f"{path}: size {size}, timestamp {layout['timestamp']:#x}, base "
                         f"{layout['base']:#x}; expected {build.name} {build.size}, "
                         f"{build.timestamp:#x}, {IMAGE_BASE:#x}")
    return layout


def load(build: GameBuild, path=None) -> "ExeImage":
    """Map the exe once per process (later calls return the same image). The file is only read.
    A second call for another game raises: the image base is fixed, so only one exe fits."""
    global _IMAGE
    if _IMAGE is None:
        _IMAGE = ExeImage(build, build.exe_path(path), None)
    elif _IMAGE.build.name != build.name:
        raise RuntimeError(f"{_IMAGE.build.name} is already mapped in this process; "
                           f"run {build.name} in its own process")
    return _IMAGE


class ExeImage:
    def __init__(self, build: GameBuild, path: Path, layout: dict | None):
        self.build = build
        self.path = path
        self.layout = layout = layout or check_build(build, path)
        size = layout["size_of_image"]
        _IMAGE_SPAN[1] = IMAGE_BASE + size
        got = _k32.VirtualAlloc(IMAGE_BASE, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE)
        if got != IMAGE_BASE:
            raise OSError(f"cannot map {size:#x} bytes at {IMAGE_BASE:#x} "
                          f"(got {got}, error {ctypes.get_last_error()})")
        with open(path, "rb") as f:
            ctypes.memmove(IMAGE_BASE, f.read(layout["size_of_headers"]), layout["size_of_headers"])
            for _name, vsize, va, rawsz, rawptr in layout["sections"]:
                n = min(rawsz, vsize) if vsize else rawsz
                if n:
                    f.seek(rawptr)
                    ctypes.memmove(IMAGE_BASE + va, f.read(n), n)
        self._size = size
        self._imports: dict[tuple[str, str], int] = {}
        self._slots: dict[tuple[str, str], int] = {}
        self.traps: list[str] = []
        self._bind_imports(layout["import_rva"])
        entries = layout["exception_size"] // 12
        if not _k32.RtlAddFunctionTable(IMAGE_BASE + layout["exception_rva"], entries, IMAGE_BASE):
            raise OSError("RtlAddFunctionTable failed")
        self._keep: list = []   # buffers and callbacks native code may still point at

    # -- imports ----------------------------------------------------------------------------
    def _bind_imports(self, import_rva: int) -> None:
        entries = []
        i = 0
        while True:
            oft, _ts, _fw, name_rva, ft = struct.unpack("<IIIII", self.read(IMAGE_BASE + import_rva + 20 * i, 20))
            if not name_rva:
                break
            dll = ctypes.string_at(IMAGE_BASE + name_rva).decode()
            j = 0
            while True:
                thunk = struct.unpack("<Q", self.read(IMAGE_BASE + (oft or ft) + 8 * j, 8))[0]
                if not thunk:
                    break
                sym = f"#{thunk & 0xFFFF}" if thunk >> 63 else ctypes.string_at(IMAGE_BASE + (thunk & 0x7FFFFFFF) + 2).decode()
                entries.append((dll, sym, IMAGE_BASE + ft + 8 * j))
                j += 1
            i += 1
        trap_size = max(0x1000, len(entries) * _TRAP_SLOT)
        self._trap_base = _k32.VirtualAlloc(None, trap_size, MEM_RESERVE, PAGE_NOACCESS)
        self._trap_end = self._trap_base + trap_size
        modules: dict[str, int | None] = {}
        self.bound = 0
        for dll, sym, iat in entries:
            key = dll.lower()
            target = None
            if key in _RESOLVED_DLLS or key.startswith("api-ms-win-crt-"):
                if key not in modules:
                    modules[key] = _k32.LoadLibraryW(dll)
                if modules[key]:
                    proc = int(sym[1:]) if sym.startswith("#") else sym.encode()
                    target = _k32.GetProcAddress(modules[key], proc)
            if target:
                self.bound += 1
            else:
                target = self._trap_base + len(self.traps) * _TRAP_SLOT
                self.traps.append(f"{dll}!{sym}")
            ctypes.memmove(iat, struct.pack("<Q", target), 8)
            self._imports[(key, sym)] = target
            self._slots[(key, sym)] = iat

    def import_slot(self, dll: str, name: str) -> int:
        return self._slots[(dll.lower(), name)]

    def trap_address(self, name: str) -> int:
        return self._trap_base + self.traps.index(name) * _TRAP_SLOT

    @property
    def size(self) -> int:
        return self._size

    # -- memory -----------------------------------------------------------------------------
    @staticmethod
    def read(address: int, size: int) -> bytes:
        return ctypes.string_at(address, size)

    def alloc(self, size: int, fill: int = 0) -> int:
        """A zeroed (or byte-filled), 16-aligned buffer that lives as long as the image."""
        buf = ctypes.create_string_buffer(bytes([fill]) * (size + 16))
        self._keep.append(buf)
        a = ctypes.addressof(buf)
        return a + (-a) % 16

    def keep(self, obj):
        self._keep.append(obj)
        return obj

    # -- calls ------------------------------------------------------------------------------
    @staticmethod
    def function(rva: int, restype, *argtypes):
        """A ctypes callable for the native function at IMAGE_BASE + rva (Windows x64 ABI)."""
        return ctypes.CFUNCTYPE(restype, *argtypes)(IMAGE_BASE + rva)

    def call(self, fn, *args):
        """Call `fn`. An access violation becomes NativeFault (instruction + address + backtrace),
        or UnresolvedImportCalled when the code jumped into the trap page of an unbound import."""
        _last_fault[0] = None
        handler = _k32.AddVectoredExceptionHandler(1, _veh_callback)
        try:
            return fn(*args)
        except OSError as e:
            if not re.search(r"access violation|divide by zero", str(e)) or _last_fault[0] is None:
                raise
            rip, kind, address, frames, fault = _last_fault[0]
            if self._trap_base <= rip < self._trap_end:
                raise UnresolvedImportCalled(self.traps[(rip - self._trap_base) // _TRAP_SLOT]
                                             + (" from " + " <- ".join(_fmt(f) for f in frames[:6]) if frames else "")) from None
            raise NativeFault(rip, _ACCESS.get(kind, str(kind)), address, frames, fault) from None
        finally:
            _k32.RemoveVectoredExceptionHandler(handler)


# -- typed memory access ------------------------------------------------------------------------
def f32(a: int) -> float: return ctypes.c_float.from_address(a).value
def u32(a: int) -> int: return ctypes.c_uint32.from_address(a).value
def u64(a: int) -> int: return ctypes.c_uint64.from_address(a).value
def u8(a: int) -> int: return ctypes.c_uint8.from_address(a).value
def set_f32(a: int, v: float) -> None: ctypes.c_float.from_address(a).value = v
def set_u32(a: int, v: int) -> None: ctypes.c_uint32.from_address(a).value = v & 0xFFFFFFFF
def set_u64(a: int, v: int) -> None: ctypes.c_uint64.from_address(a).value = v
def set_u8(a: int, v: int) -> None: ctypes.c_uint8.from_address(a).value = v
def f32bits(a: int) -> int: return u32(a)


# -- patching -----------------------------------------------------------------------------------
def jump_to(target: int) -> bytes:
    """12 bytes: mov rax, target; jmp rax."""
    return bytes.fromhex("48 B8") + struct.pack("<Q", target) + bytes.fromhex("FF E0")


def patch_jump(rva: int, target: int) -> None:
    """Overwrite the start of the function at `rva` with a jump to `target`."""
    ctypes.memmove(IMAGE_BASE + rva, jump_to(target), 12)


def install_bump_allocator(img: ExeImage, rvas, arena_size: int = 0x10000000) -> int:
    """Replace the engine allocator entries at `rvas` (each called as alloc(size, ...)) with one
    lock-free bump allocator over a fresh arena; nothing is freed. Returns the arena address."""
    arena = _k32.VirtualAlloc(None, arena_size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)
    code = _k32.VirtualAlloc(None, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE)
    if not arena or not code:
        raise OSError(f"VirtualAlloc failed (error {ctypes.get_last_error()})")
    cursor = code + 0x800
    ctypes.c_uint64.from_address(cursor).value = arena
    stub = (bytes.fromhex("48 83 C1 0F")          # add rcx, 15
            + bytes.fromhex("48 83 E1 F0")        # and rcx, -16
            + bytes.fromhex("48 89 C8")           # mov rax, rcx
            + bytes.fromhex("49 B9") + struct.pack("<Q", cursor)   # mov r9, cursor
            + bytes.fromhex("F0 49 0F C1 01")     # lock xadd [r9], rax: rax = old cursor
            + bytes.fromhex("C3"))                # ret
    ctypes.memmove(code, stub, len(stub))
    for rva in rvas:
        patch_jump(rva, code)
    return arena


def install_global(img: ExeImage, rva: int, size: int) -> int:
    """Point the .bss pointer at `rva` (null in a freshly mapped image) at a zeroed object."""
    obj = img.alloc(size)
    set_u64(IMAGE_BASE + rva, obj)
    return obj


def ucrt_powf():
    """The CRT powf the exe imports (api-ms-win-crt-math -> ucrtbase), for bit-exact references."""
    f = ctypes.CDLL("ucrtbase").powf
    f.restype, f.argtypes = ctypes.c_float, [ctypes.c_float, ctypes.c_float]
    return f


def ucrt_floorf():
    f = ctypes.CDLL("ucrtbase").floorf
    f.restype, f.argtypes = ctypes.c_float, [ctypes.c_float]
    return f
