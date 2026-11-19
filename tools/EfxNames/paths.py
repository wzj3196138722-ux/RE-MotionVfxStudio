"""Shared paths. Intermediate data lives in OUT (EFXNAMES_OUT, default tools/EfxNames/out, ignored by git)."""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
RE4NATIVE = os.path.join(REPO, 'tools', 'Re4Native')
OUT = os.environ.get('EFXNAMES_OUT', os.path.join(HERE, 'out'))
os.makedirs(OUT, exist_ok=True)
