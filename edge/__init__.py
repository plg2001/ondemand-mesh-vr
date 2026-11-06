"""Edge node prototype: point cloud in, reconstructed mesh (.glb) out, over HTTP.

Same import pattern as the other entry points: ``models/`` is NOT a package (FastMesh ships its own
top-level ``models``), so ``models/`` and the repository root are put on ``sys.path`` and our code is
imported as ``mesh_wrappers.*``.
"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for _p in (ROOT, ROOT / "models"):
    if str(_p) not in sys.path:
        sys.path.insert(0, str(_p))
