"""Finishes a character's clips (from clips.py) for the app:
- each made a seamless loop at 24 fps: the stretch of the clip that comes back nearest to where it started (or its
  settled end, for sitting and sleeping), its frames and the seam filled in by RIFE 4.26 (MIT);
- cut out with the anime matting model (rembg isnet-anime), the white background unmixed from the edges;
- its drift across the frame taken out (a walk in place that slides), its own sway kept for dancing and playing;
- all clips scaled alike (the idle clip standing TALL pixels) and laid on one spot for the feet;
written as raw RGBA frames and a manifest for pack.mts, which makes the ".char".

  python finish.py NAME ACTION [...] [--tall 240] [--fps 24]"""
import argparse
import importlib.util
import json
import os
import sys
import time
from pathlib import Path

from settings import FF, KITGEN, OUT  # noqa: E402  (HF_HOME set there)
import cv2  # noqa: E402
import numpy as np  # noqa: E402
import torch  # noqa: E402
from PIL import Image  # noqa: E402

from gpuslot import below_normal, free_gb  # noqa: E402

RIFE = KITGEN / "models" / "rife426"
# How each action loops: a cycle found in the clip, the whole clip (it ends as it began), or its settled end.
LOOP = {"idle": "cycle", "walk": "cycle", "run": "cycle", "dance": "cycle", "play": "cycle", "jump": "whole", "sit": "tail", "sleep": "tail", "held": "cycle", "land": "whole"}
# Actions whose sway across the frame is their own (kept); the others are held on one spot.
SWAYS = {"dance", "play"}


def rife():
    src = (RIFE / "IFNet_HDv3.py").read_text(encoding="utf-8").replace("from model.warplayer import warp", "from rife_warp import warp")
    sys.path.insert(0, str(RIFE))
    try:
        mod = importlib.util.module_from_spec(importlib.util.spec_from_loader("rife_ifnet", loader=None))
        exec(compile(src, str(RIFE / "IFNet_HDv3.py"), "exec"), mod.__dict__)  # noqa: S102 - the model's own architecture file
    finally:
        sys.path.remove(str(RIFE))
    net = mod.IFNet()
    sd = torch.load(RIFE / "flownet.pkl", map_location="cpu", weights_only=True)
    net.load_state_dict({k.replace("module.", ""): v for k, v in sd.items()}, strict=False)
    return net.eval().half().cuda()


def between(net, a: np.ndarray, b: np.ndarray, t: float) -> np.ndarray:
    """The frame a share t of the way from a to b (RIFE)."""
    h, w = a.shape[:2]
    ph, pw = (64 - h % 64) % 64, (64 - w % 64) % 64
    ta, tb = ((torch.from_numpy(f.copy()).cuda().permute(2, 0, 1).half() / 255.0)[None] for f in (a, b))
    ta, tb = (torch.nn.functional.pad(x, (0, pw, 0, ph), mode="replicate") for x in (ta, tb))
    with torch.inference_mode():
        _, _, merged = net(torch.cat((ta, tb), 1), timestep=t, scale_list=[16, 8, 4, 2, 1])
    return (merged[-1][0, :, :h, :w].clamp(0, 1) * 255).round().byte().permute(1, 2, 0).cpu().numpy()


def small(f: np.ndarray) -> np.ndarray:
    return cv2.resize(cv2.cvtColor(f, cv2.COLOR_RGB2GRAY), (64, 112), interpolation=cv2.INTER_AREA).astype(np.float32)


def segment(frames: list[np.ndarray], how: str) -> list[np.ndarray]:
    """The frames that loop: a cycle (from i to just before j, frame j nearly frame i), the whole clip, or its end."""
    n = len(frames)
    if how == "whole":
        return frames
    s = [small(f) for f in frames]
    if how == "tail":
        motion = [float(np.abs(s[k] - s[k - 1]).mean()) for k in range(1, n)]
        calm = float(np.median(motion[-16:])) * 1.5 + 0.2
        start = n - 20
        while start > n // 3 and motion[start - 2] <= calm:
            start -= 1
        return frames[start:]
    best = None
    for i in range(0, 10):
        for j in range(i + 18, n):
            d = float(np.abs(s[i] - s[j]).mean()) - 0.004 * (j - i)
            if best is None or d < best[0]:
                best = (d, i, j)
    _, i, j = best
    return frames[i:j]


def resample(net, loop: list[np.ndarray], fps_in: float, fps_out: float) -> list[np.ndarray]:
    """The loop at another rate, going round: every frame between two is RIFE's (the last to the first too)."""
    n = len(loop)
    m = max(2, round(n * fps_out / fps_in))
    out = []
    for k in range(m):
        t = k * n / m
        i = int(t)
        frac = t - i
        out.append(loop[i] if frac < 1e-3 else between(net, loop[i], loop[(i + 1) % n], frac))
    return out


_SESSION = None


def cutout(f: np.ndarray) -> np.ndarray:
    """RGBA: the anime matting model's matte, the white background unmixed from the edges."""
    global _SESSION
    from rembg import new_session, remove

    if _SESSION is None:
        _SESSION = new_session("isnet-anime")
    a = remove(f, session=_SESSION, post_process_mask=False, only_mask=True).astype(np.float32) / 255.0
    a = np.where(a < 0.06, 0, a)
    # Only the character: specks and bits floating apart from it (dust in the still, petals) left out. Parts are kept
    # when they are large (over 2 % of the largest) or close to it (within 6 pixels: a tail tip, a lock of hair).
    solid = (a > 0.25).astype(np.uint8)
    count, labels, stats, _ = cv2.connectedComponentsWithStats(solid, connectivity=8)
    if count > 2:
        sizes = stats[1:, cv2.CC_STAT_AREA]
        main = 1 + int(np.argmax(sizes))
        near = cv2.dilate((labels == main).astype(np.uint8), np.ones((13, 13), np.uint8)) > 0
        keep = np.zeros(count, bool)
        keep[main] = True
        for c in range(1, count):
            if c != main and (stats[c, cv2.CC_STAT_AREA] >= 0.02 * sizes.max() or near[labels == c].any()):
                keep[c] = True
        body = cv2.dilate(keep[labels].astype(np.uint8), np.ones((5, 5), np.uint8)) > 0
        a = np.where(body, a, 0)
    rgb = f.astype(np.float32)
    safe = np.maximum(a, 0.06)[..., None]
    rgb = np.where(a[..., None] > 0, (rgb - (1 - safe) * 255.0) / safe, rgb)
    return np.dstack([np.clip(rgb, 0, 255), a * 255]).round().astype(np.uint8)


def center_x(rgba: np.ndarray) -> float:
    cols = rgba[..., 3].astype(np.float32).sum(axis=0)
    return float((cols * np.arange(len(cols))).sum() / max(1.0, cols.sum()))


def bbox(rgba: np.ndarray, at: int = 32):
    ys, xs = np.where(rgba[..., 3] > at)
    return (xs.min(), ys.min(), xs.max() + 1, ys.max() + 1) if len(xs) else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("name")
    ap.add_argument("actions", nargs="+")
    ap.add_argument("--tall", type=int, default=240)
    ap.add_argument("--fps", type=float, default=24)
    ap.add_argument("--faces", type=int, default=1, choices=[1, -1], help="the way the character looks: 1 right, -1 left")
    ap.add_argument("--title", default=None, help="its name as shown (default: NAME)")
    args = ap.parse_args()
    below_normal()
    folder = OUT / args.name
    clips = {}
    # RIFE on a few dozen small frames is light work (under a minute, under 2 GB): no lock, only room on the GPU.
    while free_gb() < 2.5:
        print("[finish] waiting for 2.5 GB free on the GPU", flush=True)
        time.sleep(20)
    if True:
        net = rife()
        for action in args.actions:
            frames = [np.asarray(Image.open(p).convert("RGB")) for p in sorted((folder / action).glob("*.png"))]
            loop = segment(frames, LOOP.get(action, "cycle"))
            clips[action] = resample(net, loop, 16, args.fps)
            print(action, len(frames), "->", len(loop), "->", len(clips[action]), flush=True)
        del net
        torch.cuda.empty_cache()

    # Cut out, held on one spot (the idle clip's middle), then all scaled alike.
    cut = {a: [cutout(f) for f in fs] for a, fs in clips.items()}
    idle = cut.get("idle") or next(iter(cut.values()))
    spot = float(np.median([center_x(f) for f in idle]))
    for action, frames in cut.items():
        if action in SWAYS:
            continue
        for k, f in enumerate(frames):
            dx = int(round(spot - center_x(f)))
            frames[k] = np.roll(f, dx, axis=1) if dx else f
    boxes = [bbox(f) for f in idle if bbox(f)]
    standing = float(np.median([b[3] - b[1] for b in boxes]))
    feet = float(np.median([b[3] for b in boxes]))
    k = args.tall / standing
    every = [b for fs in cut.values() for f in fs if (b := bbox(f, 8))]
    left, top, right, bottom = min(b[0] for b in every), min(b[1] for b in every), max(b[2] for b in every), max(b[3] for b in every)
    pad = 3
    left, top = max(0, left - pad), max(0, top - pad)
    w, h = round((right + pad - left) * k), round((bottom + pad - top) * k)
    # The top of its head: the middle of its topmost ink in the idle clip's first frame.
    f0 = idle[0]
    rows = np.where(f0[..., 3].max(axis=1) > 128)[0]
    head_x = float(np.where(f0[rows[0]:rows[0] + 6, :, 3].max(axis=0) > 128)[0].mean())
    raw = folder / "pack"
    raw.mkdir(exist_ok=True)
    for p in raw.glob("*.rgba"):
        p.unlink()
    manifest = {"name": args.title or args.name.capitalize(), "width": w, "height": h, "anchor": [round((spot - left) * k, 2), round((feet - top) * k, 2)],
                "head": [round((head_x - left) * k, 2), round((rows[0] - top) * k, 2)], "tall": args.tall, "faces": args.faces, "clips": []}
    for action, frames in cut.items():
        names = []
        for i, f in enumerate(frames):
            crop = Image.fromarray(f[top:top + round(h / k), left:left + round(w / k)], "RGBA").resize((w, h), Image.LANCZOS)
            name = f"{action}-{i:04d}.rgba"
            (raw / name).write_bytes(np.asarray(crop).tobytes())
            names.append(name)
            if i == 0:
                crop.save(folder / f"cut-{action}.png")
        manifest["clips"].append({"action": action, "fps": round(args.fps), "frames": names})
    (raw / "manifest.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
    print("ready", manifest["width"], "x", manifest["height"], {c["action"]: len(c["frames"]) for c in manifest["clips"]})


if __name__ == "__main__":
    main()
