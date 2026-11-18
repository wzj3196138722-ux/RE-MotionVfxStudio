"""MH Wilds: the MonsterHunterWilds.exe build the addresses in mhwildsnative.py and
docs/development/WILDS-EFX-VS-RE4.zh-CN.md were read from."""
from . import GameBuild

BUILD = GameBuild(
    name="mhwilds",
    efx_version=5571972,
    default_exe=r"~\Downloads\MonsterHunterWilds.exe",   # the game is not installed; a copy of its exe
    exe_env="MHWILDS_EXE",
    size=546_013_600,
    timestamp=0x6ACECA63,
)
