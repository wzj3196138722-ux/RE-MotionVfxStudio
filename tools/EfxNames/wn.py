"""Forwards to tools/Re4Native/mhwildsnative.py; load() maps the exe named by MHWILDS_EXE (wk.EXES['wilds'])."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from paths import RE4NATIVE  # noqa: E402

sys.path.insert(0, RE4NATIVE)
import mhwildsnative as _w  # noqa: E402
from mhwildsnative import *  # noqa: E402,F401,F403
from wk import EXES  # noqa: E402


def load(path=None):
    return _w.load(path or EXES['wilds'])
