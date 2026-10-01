"""Brain registry -- named multi-brain support.

A *brain* is a named set of blocks. Each block carries its own data source
(HTTP URL or local DDN path), its finest voxel size, its anatomical omeToRas
affine (reassembly into shared RAS mm), and optionally a label-map (segmentation)
source. Some brains have one block, some many; some have label maps, some don't.

On disk: one JSON per brain in `server/brains/<name>.json`. Schema:

    {
      "name": "Brain_580_whole",
      "brain": "Stroke_1",              # source anatomical brain id (optional)
      "space": "RAS-mm",
      "description": "...",
      "blocks": [
        {
          "id": "580",
          "source": "http://host/zarr_files/580_ALL_3d.zarr"  # or a local path
          "voxel_um": [20.0, 8.0, 8.0],  # finest z,y,x microns
          "omeToRas": [[...],[...],[...]],# 3x4 anatomical affine, or null
          "label_map": {                 # optional
            "source": "http://.../580_labels.zarr",  # or local path
            "name": "regions",
            "lut": null                  # optional id->{color,name} map or URL
          }
        }
      ]
    }

Source transport is auto-detected per block: a value starting with http:// or
https:// uses HttpZarrBrainSource; anything else is treated as a local path and
uses LocalZarrBrainSource (see docs/performance-plan.md Item 1).
"""
from __future__ import annotations

import json
import os
import re
from typing import Dict, List, Optional

from datasource.http_zarr import HttpZarrBrainSource
from datasource.local_zarr import LocalZarrBrainSource

HERE = os.path.dirname(os.path.abspath(__file__))
BRAINS_DIR = os.path.join(HERE, "brains")


def _safe_name(name: str) -> str:
    """Filesystem-safe brain name (the registry key / file stem)."""
    s = re.sub(r"[^A-Za-z0-9_.-]+", "_", name).strip("_")
    if not s:
        raise ValueError(f"invalid brain name {name!r}")
    return s


def is_local(source: str) -> bool:
    return not source.lower().startswith(("http://", "https://"))


def make_source(block: dict):
    """Build the right data source for a block spec (HTTP vs local DDN)."""
    src = block["source"]
    voxel = block.get("voxel_um")
    voxel = tuple(voxel) if voxel else None
    if is_local(src):
        return LocalZarrBrainSource(src, voxel_um_finest=voxel)
    return HttpZarrBrainSource(src, voxel_um_finest=voxel)


# ---- registry I/O -----------------------------------------------------------
def ensure_dir() -> None:
    os.makedirs(BRAINS_DIR, exist_ok=True)


def brain_path(name: str) -> str:
    return os.path.join(BRAINS_DIR, f"{_safe_name(name)}.json")


def list_brains() -> List[str]:
    if not os.path.isdir(BRAINS_DIR):
        return []
    return sorted(f[:-5] for f in os.listdir(BRAINS_DIR) if f.endswith(".json"))


def load_brain(name: str) -> dict:
    with open(brain_path(name)) as f:
        d = json.load(f)
    d.setdefault("name", _safe_name(name))
    d.setdefault("blocks", [])
    return d


def save_brain(brain_def: dict) -> str:
    """Write a brain definition; returns its (safe) name."""
    ensure_dir()
    name = _safe_name(brain_def["name"])
    brain_def = {**brain_def, "name": name}
    tmp = brain_path(name) + ".tmp"
    with open(tmp, "w") as f:
        json.dump(brain_def, f, indent=2)
    os.replace(tmp, brain_path(name))
    return name


def exists(name: str) -> bool:
    return os.path.exists(brain_path(name))


def bootstrap_brain(name: str, roots: List[str], block_meta: Dict[str, dict],
                    voxel_fallback, brain_label: Optional[str] = None,
                    description: str = "") -> dict:
    """Synthesize a brain definition from the server's current config
    (the hardcoded ZARR_ROOTS + histology_blocks.json meta). This is what
    'save current brain as <name>' captures: each block's source + anatomical
    omeToRas + finest voxel size (transforms: anatomical only)."""
    blocks = []
    for url in roots:
        base = url.rstrip("/").split("/")[-1]
        m = re.match(r"(\d+)", base)
        bid = m.group(1) if m else re.sub(r"[^A-Za-z0-9]+", "_", base).strip("_")
        meta = block_meta.get(bid, {})
        ip = meta.get("inPlaneScaleMeters", 8e-6) * 1e6 if meta else None
        voxel = ([meta["zScaleMeters"] * 1e6, ip, ip]
                 if meta and "zScaleMeters" in meta else list(voxel_fallback))
        blocks.append({
            "id": bid,
            "source": url,
            "voxel_um": voxel,
            "omeToRas": meta.get("omeToRas"),
            "label_map": None,
        })
    return {
        "name": _safe_name(name),
        "brain": brain_label,
        "space": "RAS-mm",
        "description": description or f"Captured from server defaults ({len(blocks)} blocks).",
        "blocks": blocks,
    }
