"""MH Wilds: the MonsterHunterWilds.exe build the addresses in mhwildsnative.py and
docs/development/WILDS-EFX-VS-RE4.zh-CN.md were read from."""
from . import GameBuild

BUILD = GameBuild(
    name="mhwilds",
    efx_version=5571972,
    # the game is not installed; a copy of its exe is kept in the main checkout (not tracked)
    default_exe=r"E:\Data\Github\RE-MotionVfxStudio\sample\Wilds\MonsterHunterWilds.exe",
    exe_env="MHWILDS_EXE",
    size=546_013_600,
    timestamp=0x6ACECA63,
)
