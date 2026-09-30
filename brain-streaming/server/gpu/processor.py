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

    # ---- merged-block-volume bake (color+opacity) + occupancy --------------
    # Baked once on the A100 so the SRD client shader just composites RGBA
    # instead of recomputing tissue/density/colour every ray step. RGB is the
    # colour (NOT premultiplied by dt); A is opacity, so the client's runtime
    # _Density knob still works. Matches the look of BrainRaymarch.shader:
    #   tissue = 1 - gray ; opacity = mask * tissue ; colour = lerp(blue, white).
    _COL_LO = (0.55, 0.62, 0.78)   # low-tissue tint (bluish)
    _COL_HI = (0.98, 0.98, 0.98)   # high-tissue tint (white)

    def bake_rgba(self, a: np.ndarray, device: Optional[int] = None) -> np.ndarray:
        """(z,y,x,2)=(gray ch0, mask ch3) -> (z,y,x,4) uint8 RGBA (colour+opacity)."""
        if self.available and device is not None:
            with _cp.cuda.Device(int(device) % self.device_count):
                return self._bake_rgba_impl(a)
        return self._bake_rgba_impl(a)

    def _bake_rgba_impl(self, a) -> np.ndarray:
        xp, g = self._xp(a)
        gray = g[..., 0].astype(xp.float32) / 255.0
        mask = g[..., 1].astype(xp.float32) / 255.0
        tissue = 1.0 - gray
        opacity = mask * tissue
        dz, dy, dx = tissue.shape
        out = xp.empty((dz, dy, dx, 4), dtype=xp.float32)
        for i in range(3):
            out[..., i] = (self._COL_LO[i] + (self._COL_HI[i] - self._COL_LO[i]) * tissue) * 255.0
        out[..., 3] = opacity * 255.0
        return self._to_numpy(xp.clip(out, 0, 255).astype(xp.uint8))

    def bake_rgba_rgb(self, a: np.ndarray, device: Optional[int] = None,
                      bg: float = 250.0) -> np.ndarray:
        """(z,y,x,3)=fused RGB colour -> (z,y,x,4) uint8 RGBA (colour+opacity).

        For the hb02 fused volume the background is WHITE (fill 255), so tissue is
        wherever the colour departs from white: opacity = 1 - min(r,g,b), and the
        RGB is kept as-is. `bg` is the near-white floor below which a voxel counts
        as fully background (fully transparent)."""
        if self.available and device is not None:
            with _cp.cuda.Device(int(device) % self.device_count):
                return self._bake_rgba_rgb_impl(a, bg)
        return self._bake_rgba_rgb_impl(a, bg)

    def _bake_rgba_rgb_impl(self, a, bg) -> np.ndarray:
        xp, g = self._xp(a)
        rgb = g[..., :3].astype(xp.float32)
        mn = rgb.min(axis=-1)                         # 255 on white background
        tissue = xp.clip((bg - mn) / bg, 0.0, 1.0)    # 0 on white, ->1 as it darkens/colours
        dz, dy, dx = tissue.shape
        out = xp.empty((dz, dy, dx, 4), dtype=xp.float32)
        out[..., :3] = rgb
        out[..., 3] = tissue * 255.0
        return self._to_numpy(xp.clip(out, 0, 255).astype(xp.uint8))

    # ---- segmentation label colouring ------------------------------------
    _LABEL_LUT = None                              # cached (256,3) uint8 colour table

    @classmethod
    def label_lut(cls) -> np.ndarray:
        """256-entry RGB colour table for uint8 label ids. id 0 = black (its alpha
        is 0 anyway); the rest are spread by the golden angle so adjacent region
        ids get well-separated hues. Deterministic, so colours are stable."""
        if cls._LABEL_LUT is not None:
            return cls._LABEL_LUT
        lut = np.zeros((256, 3), np.uint8)
        for i in range(1, 256):
            h = (i * 0.61803398875) % 1.0          # golden-angle hue
            s, v = 0.62, 1.0
            k = int(h * 6.0); f = h * 6.0 - k
            p, q, t = v * (1 - s), v * (1 - s * f), v * (1 - s * (1 - f))
            r, g, b = [(v, t, p), (q, v, p), (p, v, t),
                       (p, q, v), (t, p, v), (v, p, q)][k % 6]
            lut[i] = (int(r * 255), int(g * 255), int(b * 255))
        cls._LABEL_LUT = lut
        return lut

    def bake_labels_rgba(self, a: np.ndarray, device: Optional[int] = None) -> np.ndarray:
        """(z,y,x,1) uint8 label ids -> (z,y,x,4) uint8 RGBA. rgb = LUT[id];
        alpha = 255 where id>0 else 0 (background transparent). CPU (label volumes
        are small); ignores `device`."""
        ids = a[..., 0].astype(np.uint8)
        lut = self.label_lut()
        rgb = lut[ids]                             # (z,y,x,3)
        alpha = np.where(ids > 0, 255, 0).astype(np.uint8)[..., None]
        return np.ascontiguousarray(np.concatenate([rgb, alpha], axis=-1), np.uint8)

    def maxpool_occupancy(self, rgba: np.ndarray, block: int = 16,
                          device: Optional[int] = None) -> np.ndarray:
        """Max of the alpha channel over block^3 cells -> (oz,oy,ox,1) uint8.

        A tiny volume the client shader samples to leap over empty space. Dims
        are ceil(dim/block); the volume is zero-padded up to a multiple of block.
        """
        if self.available and device is not None:
            with _cp.cuda.Device(int(device) % self.device_count):
                return self._maxpool_impl(rgba, block)
        return self._maxpool_impl(rgba, block)

    def _maxpool_impl(self, rgba, block) -> np.ndarray:
        xp, g = self._xp(rgba)
        alpha = g[..., 3]
        dz, dy, dx = alpha.shape
        oz, oy, ox = (-(-dz // block), -(-dy // block), -(-dx // block))  # ceil
        pad = xp.zeros((oz * block, oy * block, ox * block), dtype=alpha.dtype)
        pad[:dz, :dy, :dx] = alpha
        red = pad.reshape(oz, block, oy, block, ox, block).max(axis=(1, 3, 5))
        return self._to_numpy(red.astype(xp.uint8)[..., None])
