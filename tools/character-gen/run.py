"""One command for the whole character pipeline. It waits for the GPU (gpuslot.py), runs the steps
and releases the GPU at the end. Run it with kitgen's Python (settings.py says where kitgen is):

  set PY=kit\\.venv\\Scripts\\python.exe

  %PY% run.py stills NAME [NAME ...] [--seeds 4] [--first 1] [--tag=-x]
      candidate stills (artifacts\\chargen\\NAME\\still*-SEED.png) to choose from

  %PY% run.py make NAME --still PATH [--actions idle,walk,run,jump,play,sit,sleep] [--faces -1] [--title Name]
                    [--tall 200] [--fps 20] [-- NAME2 --still PATH2 ...]
      clips (Wan 2.2, one load for every character listed), loops, cut-out and the .char
      (artifacts\\chargen\\NAME\\NAME.char)

  %PY% run.py pack NAME [--faces -1] [--title Name] [--tall 200] [--fps 20] [--actions ...]
      only the loops and the .char again, from clips already made (no Wan)

  %PY% run.py animate-batch SPEC.json
      many new animations' clips with one load of Wan (animate.py --batch: each item's file or error written back)

  %PY% run.py animate WORK CHARFILE ACTION --prompt "..." [--seed 7] [--fps 20] [--loop cycle]
      a new animation for a character that exists, from its own first idle frame: WORK\\WORK.char, the clip alone
"""
import argparse
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from settings import GPU_LOCK  # noqa: E402

OWNER = "pc-diagnostic-d9"
ALL = "idle,walk,run,jump,play,sit,sleep"


def lock(*args: str) -> str:
    return subprocess.run([sys.executable, str(GPU_LOCK / "gpu_lock.py"), *args], capture_output=True, text=True).stdout


def reserve(goal: str):
    """Waits for this agent's turn (first in the queue, the GPU free), then holds it for the goal."""
    import time

    while True:
        out = lock("reserve", OWNER, goal)
        if out.startswith("reserved"):
            print(f"[run] GPU reserved for: {goal}", flush=True)
            return
        print(f"[run] waiting for the GPU: {out.strip()}", flush=True)
        time.sleep(30)


def step(*args: str):
    print("[run]", " ".join(args), flush=True)
    subprocess.run([sys.executable, *args], cwd=HERE, check=True)


def finish_and_pack(c):
    extra = ["--faces", str(c.faces), "--tall", str(c.tall), "--fps", str(c.fps)] + (["--title", c.title] if c.title else [])
    # Every clip the character has (a redone clip and the others alike), in the usual order.
    from settings import OUT

    made = [a for a in ("idle", "walk", "run", "jump", "play", "dance", "sit", "sleep", "held", "land") if (OUT / c.name / a).is_dir()]
    step("finish.py", c.name, *made, *extra)
    subprocess.run(["node", "pack.mts", c.name], cwd=HERE, check=True)
    # How each clip repeats (a pose settles instead of starting over).
    subprocess.run([sys.executable, "loops.py", str(OUT / c.name / f"{c.name}.char")], cwd=HERE, check=True)


def character_args(argv):
    ap = argparse.ArgumentParser(prog="run.py make|pack")
    ap.add_argument("name")
    ap.add_argument("--still")
    ap.add_argument("--actions", default=ALL)
    ap.add_argument("--faces", type=int, default=1, choices=[1, -1])
    ap.add_argument("--title")
    ap.add_argument("--tall", type=int, default=200)
    ap.add_argument("--fps", type=int, default=20)
    return ap.parse_args(argv)


def main():
    if len(sys.argv) < 3 or sys.argv[1] not in ("stills", "make", "pack", "animate", "animate-batch"):
        print(__doc__)
        sys.exit(2)
    command, rest = sys.argv[1], sys.argv[2:]
    if command == "animate-batch":
        # Many new animations' clips (animate.py --batch SPEC): one load of Wan for them all.
        spec = rest[0]
        reserve(f"PC Diagnostic animations made in one batch ({Path(spec).stem})")
        try:
            step("animate.py", "--batch", spec)
        finally:
            print(lock("release", OWNER, f"animations made ({Path(spec).stem})"), flush=True)
        return
    if command == "animate":
        from settings import OUT

        work, action = rest[0], rest[2]
        reserve(f"PC Diagnostic animation {action} made for {work}")
        try:
            step("animate.py", *rest)
            subprocess.run(["node", "pack.mts", work], cwd=HERE, check=True)
            subprocess.run([sys.executable, "loops.py", str(OUT / work / f"{work}.char")], cwd=HERE, check=True)
        finally:
            print(lock("release", OWNER, f"animation {action} made for {work}"), flush=True)
        return
    if command == "stills":
        names = [a for a in rest if not a.startswith("-")]
        reserve(f"PC Diagnostic character stills: {', '.join(names)}")
        try:
            step("still.py", *rest)
        finally:
            lock("release", OWNER, f"stills made: {', '.join(names)}")
        return
    groups, cur = [], []
    for a in rest + ["--"]:
        if a == "--":
            if cur:
                groups.append(character_args(cur))
            cur = []
        else:
            cur.append(a)
    if command == "pack":
        for c in groups:
            finish_and_pack(c)
        return
    names = ", ".join(c.name for c in groups)
    reserve(f"PC Diagnostic characters made: {names}")
    try:
        clip_args = []
        for c in groups:
            clip_args += (["--"] if clip_args else []) + [c.name, c.still, *c.actions.split(",")]
        step("clips.py", *clip_args)
        for c in groups:
            finish_and_pack(c)
    finally:
        print(lock("release", OWNER, f"characters made: {names}"), flush=True)


if __name__ == "__main__":
    main()
