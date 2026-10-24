"""Sets how each clip of a character file repeats, so repeating it never jumps back to its start:

  - a pose whose end does not lead back to its start: its opening move plays once, then sitting its calmest six frames
    play forwards and back ("loop", "end", "bounce"), and asleep its calmest frame is held ("loop" = "end");
  - a clip done in place (idle, play, dance) whose end does not lead back to its start: forwards and back;
  - a walk or run whose end does not lead back to its start: the stretch of it that repeats most seamlessly (the frame
    that would follow its end most like its first), its opening played once ("loop", "end");
  - a clip that already loops: as it is.

  %PY% loops.py FILE.char [FILE.char ...] [--dry]      (%PY%: kitgen's Python, see README.md)
"""
import io
import json
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image

POSES = {"sit", "sleep"}
CYCLES = {"walk", "run"}
IN_PLACE = {"idle", "play", "dance", "held"}


def small(png: bytes) -> np.ndarray:
    im = Image.open(io.BytesIO(png)).convert("RGBA").resize((64, 64), Image.BILINEAR)
    a = np.asarray(im, dtype=np.float32) / 255
    return np.concatenate([a[..., :3] * a[..., 3:], a[..., 3:]], axis=2)


def diff(x: np.ndarray, y: np.ndarray) -> float:
    return float(np.abs(x - y).mean())


def repeat_of(action: str, frames: list[np.ndarray]) -> dict:
    """How a clip repeats: {} as it is, or loop/end/bounce."""
    k = len(frames)
    if k < 8:
        return {}
    steps = [diff(frames[i], frames[i + 1]) for i in range(k - 1)]
    step = max(float(np.median(steps)), 1e-6)
    end = k - 1
    # A last frame unlike the one before it (made to lead back to the start) is left out of the repeating.
    if steps[-1] > 3 * max(float(np.median(steps[k // 2:-1])), 1e-6):
        end = k - 2
    seam = diff(frames[end], frames[0]) / step
    if action in CYCLES:
        # The stretch [start, last] whose next frame would most look like its first: a seamless step cycle.
        k_min = max(8, int(k * 0.45))
        best = (diff(frames[k - 1], frames[0]), 0, k - 1)
        for start in range(0, k // 3 + 1):
            for last in range(start + k_min - 1, k - 1):
                cost = diff(frames[last + 1], frames[start])
                if cost < best[0]:
                    best = (cost, start, last)
        cost, start, last = best
        # Worth it when it joins clearly better than the whole clip does.
        if (start, last) != (0, k - 1) and cost < 0.7 * diff(frames[k - 1], frames[0]):
            return {"loop": start, "end": last} if start else {"end": last}
        return {}
    if seam <= 3:
        return {}
    if action == "sleep":
        # Asleep it lies still: its calmest frame after lying down, held (its zZ moves); a sleep still moving, repeated
        # forwards and back, looked restless.
        calm = min(range(k // 3, end + 1), key=lambda i: steps[i - 1] + (steps[i] if i < k - 1 else steps[i - 1]))
        return {"loop": calm, "end": calm}
    if action in POSES:
        # Its calmest six frames after sitting down, forwards and back (breathing).
        windows = [(float(np.mean(steps[s:s + 5])), s) for s in range(k // 3, end - 4)]
        if windows:
            _, start = min(windows)
            return {"loop": start, "end": start + 5, "bounce": True}
    if action in IN_PLACE or action in POSES:
        return {"end": end, "bounce": True} if end < k - 1 else {"bounce": True}
    return {}


def main() -> None:
    dry = "--dry" in sys.argv
    for name in [a for a in sys.argv[1:] if not a.startswith("--")]:
        path = Path(name)
        b = path.read_bytes()
        n = struct.unpack_from("<I", b, 6)[0]
        m = json.loads(b[10:10 + n])
        at = 10 + n
        for c in m["clips"]:
            frames = []
            for size in c["frames"]:
                frames.append(small(b[at:at + size]))
                at += size
            for key in ("loop", "end", "bounce"):
                c.pop(key, None)
            c.update(repeat_of(c["action"], frames))
            if any(key in c for key in ("loop", "end", "bounce")):
                print(path.stem, c["action"], {key: c[key] for key in ("loop", "end", "bounce") if key in c}, "of", len(frames))
        if not dry:
            text = json.dumps(m, separators=(",", ":")).encode()
            path.write_bytes(b[:6] + struct.pack("<I", len(text)) + text + b[10 + n:])


if __name__ == "__main__":
    main()
