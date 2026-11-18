"""MH Wilds (EFX version 5571972): native_core bound to the MonsterHunterWilds.exe profile.

What the Wilds EFX code needs that a freshly mapped image lacks:
- the engine allocator: alloc(size) at b020830 (mov edx, 0x10; jmp f5b0) and f5b0 itself reach the
  engine heap, which is never created here; both are replaced by a bump allocator at load.
- engine singletons some builders read (Spawn, EmitterShape3D): install_global(rva, size) per
  pointer, found per builder from its NativeFault address.

Addresses: docs/development/WILDS-EFX-VS-RE4.zh-CN.md (factory table 14bf2000, step tables 14bf66e8,
emitter registration 386e3e0, list sort 1dec20).
"""
from __future__ import annotations

import ctypes

from games import mhwilds as _profile
from native_core import *  # noqa: F401,F403
from native_core import ExeImage, install_bump_allocator
from native_core import load as _load

BUILD = _profile.BUILD
DEFAULT_EXE = BUILD.default_exe

ALLOC_RVAS = (0xB020830, 0xF5B0)
FACTORY_TABLE_RVA = 0x14BF2000     # std::function x 282, index = attribute type id
STEP_TABLE_RVA = 0x14BF66E8        # bases: A3D 0, B3D 119, A2D 230, B2D 281

_arena: list[int] = []


def load(path=None) -> ExeImage:
    img = _load(BUILD, path)
    if not _arena:
        _arena.append(install_bump_allocator(img, ALLOC_RVAS))
    return img


def call(img: ExeImage, rva: int, *args, res=ctypes.c_uint64):
    """Call the function at `rva` with integer/pointer arguments."""
    return img.call(img.function(rva, res, *[ctypes.c_uint64] * len(args)), *args)
