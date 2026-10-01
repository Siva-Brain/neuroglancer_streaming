"""GpuProcessor -- optional A100 (CuPy) acceleration for per-brick processing.

Design rule: the core streaming prototype MUST work without a GPU. This class
degrades to NumPy when CuPy is unavailable, so every method returns the same
NumPy result either way -- only faster on GPU.

What it does (all measured ~1-4 ms/brick warm on an A100):
  * resample_xy_max : integer block-downsample in x/y so no brick texture
    exceeds a cap (lets fine levels stream at a uniform, GPU-friendly size).
  * gradient_mag    : |grad| of a channel (edge/opacity enhancement).
  * enhance_contrast: stretch the grayscale channel for crisper tissue.
  * sample_window / glass_pack : Nissl see-through DVR preprocessing --
    per-block intensity normalization, light 3D denoise, and a precomputed
    gradient channel so shading has no brick-border seams.

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

    # ---- Nissl "glass-brain" preprocessing ---------------------------------
    def sample_window(self, a: np.ndarray, channel: int = 0,
                      lo: float = 0.02, hi: float = 0.98) -> Tuple[float, float]:
        """Return the (lo,hi) raw-intensity percentile window of one channel.

        Computed once per block from a coarse volume; serialize() then maps this
        window to 0..255 so every block shares one brightness range (kills the
        inter-block seams in the reassembled brain)."""
        xp, g = self._xp(a)
        f = g[..., channel].astype(xp.float32)
        if f.size == 0:
            return (0.0, 255.0)
        a_lo, a_hi = float(xp.quantile(f, lo)), float(xp.quantile(f, hi))
        if a_hi - a_lo < 1e-3:
            return (0.0, 255.0)
        return (a_lo, a_hi)

    @staticmethod
    def _smooth_axis(xp, f, ax):
        """Separable [1,2,1]/4 blur along one axis (edge-padded)."""
        pad = [(0, 0)] * f.ndim
        pad[ax] = (1, 1)
        g = xp.pad(f, pad, mode="edge")
        lo = [slice(None)] * f.ndim; lo[ax] = slice(0, -2)
        mid = [slice(None)] * f.ndim; mid[ax] = slice(1, -1)
        hi = [slice(None)] * f.ndim; hi[ax] = slice(2, None)
        return 0.25 * g[tuple(lo)] + 0.5 * g[tuple(mid)] + 0.25 * g[tuple(hi)]

    def glass_pack(self, a: np.ndarray, gray_idx: int = 0, mask_idx: int = 1,
                   lohi: Optional[Tuple[float, float]] = None,
                   smooth_axes=(0, 1, 2), device: Optional[int] = None) -> np.ndarray:
        """Pack a Nissl brick for the see-through DVR as (z,y,x,3) uint8:
            ch0 = normalized grayscale  (per-block window -> 0..255)
            ch1 = tissue mask           (passed through)
            ch2 = gradient magnitude    (precomputed shading, no brick seams)

        The gray channel is lightly 3D-smoothed first (suppresses the z-lamination
        striping typical of serial Nissl sections) and the gradient is taken on
        that smoothed, normalized field so the client never calls grad() across
        brick borders."""
        if self.available and device is not None:
            with _cp.cuda.Device(int(device) % self.device_count):
                return self._glass_impl(a, gray_idx, mask_idx, lohi, smooth_axes)
        return self._glass_impl(a, gray_idx, mask_idx, lohi, smooth_axes)

    def _glass_impl(self, a, gray_idx, mask_idx, lohi, smooth_axes) -> np.ndarray:
        xp, g = self._xp(a)
        gray = g[..., gray_idx].astype(xp.float32)
        mask = g[..., mask_idx].astype(xp.float32) if g.shape[-1] > mask_idx \
            else xp.full(gray.shape, 255.0, xp.float32)
        if lohi is not None and (lohi[1] - lohi[0]) > 1e-3:
            gray = xp.clip((gray - lohi[0]) / (lohi[1] - lohi[0]), 0.0, 1.0) * 255.0
        sm = gray
        for ax in smooth_axes:
            sm = self._smooth_axis(xp, sm, ax)
        gz, gy, gx = xp.gradient(sm)
        gm = xp.sqrt(gz * gz + gy * gy + gx * gx)
        mx = float(gm.max()) if gm.size else 0.0
        grad = (gm / mx * 255.0) if mx > 0 else gm
        out = xp.stack([xp.clip(sm, 0, 255), mask, xp.clip(grad, 0, 255)],
                       axis=-1).astype(xp.uint8)
        return self._to_numpy(out)
