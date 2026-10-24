"""Bakes the props chosen from props.py's drawings into the app's props file (a clip per prop, one frame each, in the
characters' format), src/PcDiag.Core/Overlay/Characters/props.char.

Each prop is scaled to its size in reactions.json (a share of the character's height: the file's `tall` stands for
the character) and stands on the frames' anchor, its bottom in the middle; where it is held (grip), where it goes and
how it moves are drawn in code from reactions.json.

  python bake_props.py      (kitgen's venv: PIL)"""
import json
import re
import subprocess
from pathlib import Path

from PIL import Image

from settings import OUT  # noqa: E402

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
TABLE = REPO / "web" / "src" / "features" / "overlay" / "characters" / "reactions.json"
TARGET = REPO / "src" / "PcDiag.Core" / "Overlay" / "Characters" / "props.char"

# The drawing chosen for each prop (props.py's seed), from the contact sheet of 19 Oct 2026.
PICKS = {"battery": 2, "boxes": 4, "coffee": 2, "cone": 4, "desk-fan": 3, "ice-pack": 4, "popcorn": 4, "siren": 1,
         "thermometer": 3, "toolbox": 4, "trophy": 2, "umbrella": 2, "extinguisher": 1, "hose": 1}

# Those drawn facing left, turned to face right as the file does (what they spray goes the way they face).
FLIPPED = {"hose"}

# The character's height in the file's pixels.
TALL = 400


def main():
    props = json.loads(TABLE.read_text(encoding="utf-8"))["props"]
    drawn = {}
    for p in props:
        image = Image.open(OUT / "props" / p["id"] / f"{PICKS[p['id']]}-cut.png").convert("RGBA")
        if p["id"] in FLIPPED:
            image = image.transpose(Image.FLIP_LEFT_RIGHT)
        h = round(p["size"] * TALL)
        drawn[p["id"]] = image.resize((max(1, round(image.width * h / image.height)), h), Image.LANCZOS)
    width = 2 * max((im.width + 1) // 2 for im in drawn.values()) + 4
    # As tall as the character at least (the format's rule: `tall` within the frame).
    height = max(TALL, max(im.height for im in drawn.values())) + 2
    work = OUT / "props" / "pack"
    work.mkdir(parents=True, exist_ok=True)
    clips = []
    for p in props:
        im = drawn[p["id"]]
        canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        canvas.paste(im, ((width - im.width) // 2, height - im.height), im)
        name = f"{p['id']}.rgba"
        (work / name).write_bytes(canvas.tobytes())
        clips.append({"action": p["id"], "fps": 1, "frames": [name], "mirror": p.get("mirror", False)})
    manifest = {"name": "Props", "width": width, "height": height, "anchor": [width / 2, height], "head": [width / 2, height - TALL],
                "tall": TALL, "faces": 1, "clips": clips}
    (work / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    # Each prop's width against the character (where it stands beside it is worked out from it).
    text = TABLE.read_text(encoding="utf-8")
    for p in props:
        width_share = round(drawn[p["id"]].width / TALL, 3)
        line = next(l for l in text.splitlines() if f'"id": "{p["id"]}"' in l)
        changed = re.sub(r', "width": [0-9.]+', "", line).replace(f', "grip": ', f', "width": {width_share}, "grip": ', 1)
        text = text.replace(line, changed)
    TABLE.write_text(text, encoding="utf-8", newline="")
    subprocess.run(["node", "pack-props.mts", str(work), str(TARGET)], cwd=HERE, check=True)


if __name__ == "__main__":
    main()
