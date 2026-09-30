"""GpuProcessor -- optional A100 (CuPy) acceleration for per-brick processing.

Design rule: the core streaming prototype MUST work without a GPU. This class
degrades to NumPy when CuPy is unavailable, so every method returns the same
NumPy result either way -- only faster on GPU.

What it does (all measured ~1-4 ms/brick warm on an A100):
  * resample_xy_max : integer block-downsample in x/y so no brick texture
    exceeds a cap (lets fine levels stream at a uniform, GPU-friendly size).
  * gradient_mag    : |grad| of a channel (edge/opacity enhancement).
  * enhance_contrast: stretch the grayscale channel for crisper tissue.

GPU cost is negligible next to the ~170 ms HTTP fetch, so this is "free" quality.
"""
from __future__ import annotations

import math
import os
from typing import Optional

import numpy as np

try:
    import cupy as _cp
    _CUPY = True
except Exception:  # noqa
    _cp = None
    _CUPY = False


class GpuProcessor:
    def __init__(self, enabled: bool = True, device: int = 0):
        self.device = device
        self.available = bool(_CUPY and enabled and self._probe())
        self.backend = "cupy" if self.available else "numpy"

    def _probe(self) -> bool:
        try:
            with _cp.cuda.Device(self.device):
                _cp.zeros(1, dtype=_cp.uint8) + 1   # forces context + a JIT kernel
            return True
        except Exception:  # noqa
            return False

    def info(self) -> dict:
        d = {"backend": self.backend, "available": self.available}
        if self.available:
            try:
                d["device_count"] = _cp.cuda.runtime.getDeviceCount()
                free, total = _cp.cuda.runtime.memGetInfo()
                d["device"] = self.device
                d["mem_free_gb"] = round(free / 1e9, 1)
                d["mem_total_gb"] = round(total / 1e9, 1)
            except Exception:  # noqa
                pass
        return d

    # ---- helpers ------------------------------------------------------------
    def _xp(self, a):
        if self.available:
            return _cp, _cp.asarray(a)
        return np, a

    @staticmethod
    def _to_numpy(x):
        return _cp.asnumpy(x) if (_CUPY and isinstance(x, _cp.ndarray)) else np.asarray(x)

    def warmup(self, factors=(2, 4, 6, 8), cap: int = 4) -> None:
        """Pre-compile the downsample kernels (nvrtc JIT can take ~20 s the first
        time) so no user request pays that cost. Cheap once cached to disk."""
        if not self.available:
            return
        for f in factors:
            try:
                self.resample_xy_max(np.zeros((2, f * cap, f * cap, 2), np.uint8), cap)
            except Exception:  # noqa
                pass

    @property
    def device_count(self) -> int:
        if not self.available:
            return 0
        try:
            return _cp.cuda.runtime.getDeviceCount()
        except Exception:  # noqa
            return 1

    # ---- ops (array is (z,y,x,c) uint8) ------------------------------------
    def resample_xy_max(self, a: np.ndarray, max_xy: int, device: Optional[int] = None) -> np.ndarray:
        """Block-mean downsample x,y so max(dy,dx) <= max_xy. z,c untouched.

        `device` selects which of the 8 A100s runs it (round-robined by callers)
        so concurrent requests spread across GPUs instead of hammering GPU 0.
        """
        dz, dy, dx, dc = a.shape
        f = max(1, math.ceil(max(dy, dx) / max_xy))
        if f == 1:
            return a
        if self.available and device is not None:
            with _cp.cuda.Device(int(device) % self.device_count):
                return self._resample_impl(a, f, dz, dy, dx, dc)
        return self._resample_impl(a, f, dz, dy, dx, dc)

    def _resample_impl(self, a, f, dz, dy, dx, dc) -> np.ndarray:
        xp, g = self._xp(a)
        cy, cx = (dy // f) * f, (dx // f) * f
        g = g[:, :cy, :cx, :].astype(xp.float32)
        g = g.reshape(dz, cy // f, f, cx // f, f, dc).mean(axis=(2, 4))
        return self._to_numpy(g.astype(xp.uint8))

    def gradient_mag(self, a: np.ndarray, channel: int = 0) -> np.ndarray:
        """uint8 gradient magnitude of one channel -> (z,y,x,1)."""
        xp, g = self._xp(a)
        f = g[..., channel].astype(xp.float32)
        gz, gy, gx = xp.gradient(f)
        m = xp.sqrt(gz * gz + gy * gy + gx * gx)
        mx = float(m.max()) if m.size else 1.0
        m = (m / mx * 255.0) if mx > 0 else m
        out = m.astype(xp.uint8)[..., None]
        return self._to_numpy(out)

    def enhance_contrast(self, a: np.ndarray, channel: int = 0,
                         lo: float = 0.02, hi: float = 0.98) -> np.ndarray:
        """Percentile-stretch one channel in place (returns a copy)."""
        xp, g = self._xp(a.copy())
        f = g[..., channel].astype(xp.float32)
        a_lo, a_hi = xp.quantile(f, lo), xp.quantile(f, hi)
        if float(a_hi - a_lo) > 1e-3:
            f = xp.clip((f - a_lo) / (a_hi - a_lo), 0, 1) * 255.0
            g[..., channel] = f.astype(xp.uint8)
        return self._to_numpy(g)
