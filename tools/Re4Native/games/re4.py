"""RE4R: the re4.exe build every RVA in re4native.py, emitter.py, world.py and factory.py was read from."""
from . import GameBuild

BUILD = GameBuild(
    name="re4",
    efx_version=3539837,
    default_exe=r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe",
    exe_env="RE4_EXE",
    size=233_689_576,
    timestamp=0x69A8F652,
)
