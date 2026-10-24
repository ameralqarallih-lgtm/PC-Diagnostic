"""Makes a new animation for a character that already exists (PC Diagnostic's "Animations": one prompt, every
character), from the character's own first idle frame:

  %PY% animate.py WORK CHARFILE ACTION --prompt "The character waves ..." [--seed 7] [--fps 20] [--loop cycle]

- WORK: its work folder (artifacts\\chargen\\WORK); the result is WORK\\WORK.char.
- The clip is made by Wan 2.2 from the character's first idle frame on white (clips.py, the prompt in CHARGEN_PROMPTS),
  looped by RIFE, cut out (finish.py's steps), and packed alone as ACTION at the character's own standing height: an
  extra file the app plays with the character (no idle clip of its own; the frames sized for this clip).
- run.py animate reserves the shared GPU for it (PROTOCOL.md); clips.py holds it while Wan runs.
"""
import argparse
import io
import json
import os
import struct
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from settings import OUT  # noqa: E402

# An action's name: a few lowercase letters, digits and dashes (as the app checks it).
NAME = __import__("re").compile(r"^[a-z][a-z0-9-]{1,23}$")


def read_char(path: Path):
    """A character file's manifest and its clips' frames (PNG bytes), by action."""
    b = path.read_bytes()
    if b[:4] != b"PCDC":
        raise SystemExit(f"{path} is not a character file")
    n = struct.unpack_from("<I", b, 6)[0]
    m = json.loads(b[10:10 + n])
    at, frames = 10 + n, {}
    for c in m["clips"]:
        frames[c["action"]] = []
        for size in c["frames"]:
            frames[c["action"]].append(b[at:at + size])
            at += size
    return m, frames


def bbox(rgba: np.ndarray, at: int = 32):
    ys, xs = np.where(rgba[..., 3] > at)
    return (xs.min(), ys.min(), xs.max() + 1, ys.max() + 1) if len(xs) else None


def stage_char(work: str, char: Path):
    """Its work folder, with its first idle frame (cut out already: the clip starts from it); its manifest and that frame's box."""
    folder = OUT / work
    folder.mkdir(parents=True, exist_ok=True)
    m, packed = read_char(char)
    first = Image.open(io.BytesIO(packed["idle"][0])).convert("RGBA")
    first.save(folder / "from-char.png")
    return folder, m, bbox(np.asarray(first))


def finish_clip(net, folder: Path, action: str, m: dict, pack_box, fps: int, loop_how: str):
    """A clip made by Wan (folder/action/*.png): looped (RIFE), cut out, held on one spot, and laid out for pack.mts at
    the character's own height (as tall in its first frame as its idle frame stands in its file, its feet on the same
    spot, its head where the character's is; the frames sized for this clip)."""
    import finish

    frames = [np.asarray(Image.open(p).convert("RGB")) for p in sorted((folder / action).glob("*.png"))]
    loop = finish.resample(net, finish.segment(frames, loop_how), 16, fps)
    cut = [finish.cutout(f) for f in loop]
    start = next((b for f in cut if (b := bbox(f))), None)
    if start is None or pack_box is None:
        raise SystemExit("the character could not be found in the clip")
    spot = finish.center_x(cut[0])
    for k, f in enumerate(cut):
        dx = int(round(spot - finish.center_x(f)))
        cut[k] = np.roll(f, dx, axis=1) if dx else f
    k = (pack_box[3] - pack_box[1]) / max(1, start[3] - start[1])
    feet = float(start[3])
    every = [b for f in cut if (b := bbox(f, 8))]
    pad = 3
    left, top = max(0, min(b[0] for b in every) - pad), max(0, min(b[1] for b in every) - pad)
    right, bottom = min(cut[0].shape[1], max(b[2] for b in every) + pad), min(cut[0].shape[0], max(b[3] for b in every) + pad)
    w, h = max(1, round((right - left) * k)), max(1, round((bottom - top) * k))
    scale = min(1.0, 1024 / max(w, h))
    w, h, k = max(1, round(w * scale)), max(1, round(h * scale)), k * scale
    anchor = [round((spot - left) * k, 2), round((feet - top) * k, 2)]
    head = m.get("head") or [m["anchor"][0], m["anchor"][1] - m["tall"]]
    head = [min(w, max(0, round(anchor[0] + (head[0] - m["anchor"][0]) * scale, 2))), min(h, max(0, round(anchor[1] + (head[1] - m["anchor"][1]) * scale, 2)))]
    raw = folder / "pack"
    raw.mkdir(exist_ok=True)
    for p in raw.glob("*"):
        p.unlink()
    names = []
    for i, f in enumerate(cut):
        crop = Image.fromarray(f[top:bottom, left:right], "RGBA").resize((w, h), Image.LANCZOS)
        (raw / f"{action}-{i:04d}.rgba").write_bytes(np.asarray(crop).tobytes())
        names.append(f"{action}-{i:04d}.rgba")
        if i == 0:
            crop.save(folder / f"cut-{action}.png")
    manifest = {"name": m["name"], "width": w, "height": h, "anchor": anchor, "head": head, "tall": round(m["tall"] * scale, 2), "faces": m["faces"], "extra": True,
                "clips": [{"action": action, "fps": fps, "frames": names}]}
    (raw / "manifest.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
    print("ready", w, "x", h, action, len(names), flush=True)


def pack(work: str, out: Path):
    """The work folder's laid-out clip packed (pack.mts), its repeat set (loops.py), moved to out."""
    subprocess.run(["node", "pack.mts", work], cwd=HERE, check=True)
    made = OUT / work / f"{work}.char"
    subprocess.run([sys.executable, "loops.py", str(made)], cwd=HERE, check=True)
    made.replace(out)


def batch(spec_path: Path, fps: int, loop_how: str):
    """Many clips with one load of Wan: the spec's items ({"char", "action", "prompt", "seed"?}), each packed alone;
    each item's "out" (its file) or "error" written back into the spec."""
    import torch
    import finish

    spec = json.loads(spec_path.read_text(encoding="utf-8"))
    items = spec["items"]
    prompts, groups, staged, works = {}, {}, {}, {}
    for i, item in enumerate(items):
        if not NAME.match(item["action"]):
            item["error"] = "an action's name is 2-24 lowercase letters, digits or dashes"
            continue
        prompts[item["action"]] = item["prompt"]
        char = Path(item["char"])
        work = works.setdefault(str(char), f"{spec_path.stem}-c{len(works)}")
        if work not in staged:
            staged[work] = stage_char(work, char)
        groups.setdefault(work, []).append(f"{item['action']}@{int(item.get('seed', 7))}")
        item["work"] = work
    # 1. Every clip, Wan loaded once (clips.py takes several characters, each with its actions).
    args = []
    for work, actions in groups.items():
        args += (["--"] if args else []) + [work, str(OUT / work / "from-char.png"), *dict.fromkeys(actions)]
    if args:
        env = dict(os.environ, CHARGEN_PROMPTS=json.dumps(prompts))
        subprocess.run([sys.executable, "clips.py", *args], cwd=HERE, check=True, env=env)
    # 2. Each looped, cut out and packed alone (RIFE loaded once).
    net = finish.rife()
    try:
        for item in items:
            if "work" not in item:
                continue
            folder, m, pack_box = staged[item["work"]]
            try:
                finish_clip(net, folder, item["action"], m, pack_box, fps, loop_how)
                out = folder / f"{item['action']}.char"
                pack(item["work"], out)
                item["out"] = str(out)
            except (SystemExit, subprocess.CalledProcessError, OSError, ValueError) as ex:
                item["error"] = str(ex)[:300]
            print(f"{item['work']} {item['action']}: finished", flush=True)
    finally:
        del net
        torch.cuda.empty_cache()
    spec_path.write_text(json.dumps(spec, indent=1), encoding="utf-8")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("work", nargs="?")
    ap.add_argument("char", nargs="?")
    ap.add_argument("action", nargs="?")
    ap.add_argument("--prompt")
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--fps", type=int, default=20)
    ap.add_argument("--loop", default="cycle", choices=["cycle", "whole", "tail"])
    ap.add_argument("--no-clip", action="store_true", help="use the clip's frames already in the work folder (a retry after Wan)")
    ap.add_argument("--batch", help="a spec file of many clips, made with one load of Wan (see batch())")
    args = ap.parse_args()
    if args.batch:
        batch(Path(args.batch), args.fps, args.loop)
        return
    if not (args.work and args.char and args.action and args.prompt):
        raise SystemExit("WORK CHARFILE ACTION --prompt, or --batch SPEC")
    if not NAME.match(args.action):
        raise SystemExit("an action's name is 2-24 lowercase letters, digits or dashes")
    folder, m, pack_box = stage_char(args.work, Path(args.char))
    if not args.no_clip:
        env = dict(os.environ, CHARGEN_PROMPTS=json.dumps({args.action: args.prompt}))
        subprocess.run([sys.executable, "clips.py", args.work, str(folder / "from-char.png"), f"{args.action}@{args.seed}"], cwd=HERE, check=True, env=env)
    import torch
    import finish

    net = finish.rife()
    try:
        finish_clip(net, folder, args.action, m, pack_box, args.fps, args.loop)
    finally:
        del net
        torch.cuda.empty_cache()


if __name__ == "__main__":
    main()
