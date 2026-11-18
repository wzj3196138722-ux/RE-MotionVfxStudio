"""RE4R (EFX version 3539837): native_core bound to the re4.exe profile, plus the engine state the
RE4 particle code reads. Experiments and re4host.py import this module as `rn`.
"""
from __future__ import annotations

from games import re4 as _profile
from native_core import *  # noqa: F401,F403  (the harness API: ExeImage, call, faults, typed access)
from native_core import _RESOLVED_DLLS, _k32  # noqa: F401  (scan_loops.py, older scripts)
from native_core import IMAGE_BASE, ExeImage, f32, set_f32, set_u8, set_u32, set_u64, u32
from native_core import load as _load

BUILD = _profile.BUILD
DEFAULT_EXE = BUILD.default_exe
KNOWN_SIZE = BUILD.size
KNOWN_TIMESTAMP = BUILD.timestamp


def load(path=None) -> ExeImage:
    return _load(BUILD, path)


# -- engine state the effect code reads ---------------------------------------------------------
# qword [0x14d62f8f0] = the effect system settings object (0xee0 bytes, ctor 3cdf250, created at
# startup by 7896840..78969d0). It is a .bss pointer, so it is null in a freshly mapped image.
# Bytes the particle code reads: +0xeb0 (type update after collision), +0xeb2 (LegacyMode2018).
# The ctor zeroes both; startup then copies them from a config block (7896afe: eb0 = cfg+0x51d,
# 7896b12: eb2 = cfg+0x530).
SETTINGS_PTR_RVA = 0xD62F8F0
SETTINGS_SIZE = 0xEE0


def install_settings(img: ExeImage, eb0: int = 0, eb2: int = 0) -> int:
    """Point the settings global at a zeroed fake object and set the two flag bytes."""
    obj = img.alloc(SETTINGS_SIZE)
    set_u64(IMAGE_BASE + SETTINGS_PTR_RVA, obj)
    set_u8(obj + 0xEB0, eb0)
    set_u8(obj + 0xEB2, eb2)
    return obj


class Emitter:
    """The pieces of an emitter instance E and of the per-tick ctx the per-particle functions read.

    owner = ctx + 0x118 (what the samplers get in r8): owner+8 = E, owner+0x10 = dt seconds
    (ctx+0x128), owner+0x14 = N frames (ctx+0x12c), owner+0x18 = tick flags (ctx+0x130).
    E+0x110 = seed, E+0x120 = state, E+0x140 = particle id counter."""

    def __init__(self, img: ExeImage, seed: int = 0, state: int = 1, dt: float = 1 / 60):
        self.img = img
        self.E = img.alloc(0x400)
        self.ctx = img.alloc(0x600)
        self.owner = self.ctx + 0x118
        set_u64(self.owner + 8, self.E)
        self.seed, self.state, self.dt = seed, state, dt

    seed = property(lambda s: u32(s.E + 0x110), lambda s, v: set_u32(s.E + 0x110, v))
    state = property(lambda s: u32(s.E + 0x120), lambda s, v: set_u32(s.E + 0x120, v))
    dt = property(lambda s: f32(s.owner + 0x10), lambda s, v: set_f32(s.owner + 0x10, v))
    flags = property(lambda s: u32(s.owner + 0x18), lambda s, v: set_u32(s.owner + 0x18, v))

    def particle(self, pid: int) -> int:
        """A particle record (0x140 bytes): [p] flags, +4 flags2, +0x18 id, +0x58 age (frames)."""
        p = self.img.alloc(0x140)
        set_u32(p + 0x18, pid)
        return p
