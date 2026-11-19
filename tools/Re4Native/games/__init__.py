"""Per-game exe builds for the native harness.

A profile names the one build its addresses were read from. The harness maps only that build:
another file size or PE time stamp raises WrongBuild instead of calling stale addresses.
"""
from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class GameBuild:
    name: str            # the id GameProfile.cs uses for the game
    efx_version: int     # EFX file version suffix of this game
    default_exe: str
    exe_env: str         # environment variable that overrides default_exe
    size: int            # exe file size in bytes
    timestamp: int       # PE header TimeDateStamp

    def exe_path(self, path=None) -> Path:
        return Path(path or os.environ.get(self.exe_env, self.default_exe)).expanduser()


def get(name: str) -> GameBuild:
    from . import mhwilds, re4
    builds = {b.name: b for b in (re4.BUILD, mhwilds.BUILD)}
    if name not in builds:
        raise KeyError(f"no native profile for {name!r}; known: {', '.join(builds)}")
    return builds[name]
