"""The character generator's prerequisites, checked one by one and set up when asked (PC Diagnostic's
"Character generator" section runs it with the generator's Python):

  %PY% setup.py check                 each step as JSON: {"steps": [{"id", "title", "ok", "warn", "detail", "actions"}]}
  %PY% setup.py packages              installs the Python packages (PyTorch with CUDA, diffusers, rembg...)
  %PY% setup.py models                downloads the models into the kit's hf folder (about 45 GB)
  %PY% setup.py cutout                downloads the cut-out model (isnet-anime, about 170 MB)
  %PY% setup.py ffmpeg                installs ffmpeg with winget

The kit is the folder whose hf\\ holds the models and models\\rife426\\ RIFE 4.26 (KITGEN, see settings.py).
"""
import importlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from settings import KITGEN  # noqa: E402  (sets HF_HOME to the kit's hf folder)

PACKAGES = ["torch", "diffusers", "transformers", "accelerate", "huggingface_hub", "gguf", "rembg", "onnxruntime", "cv2", "numpy", "PIL", "peft"]
PIP_NAMES = {"cv2": "opencv-python", "PIL": "pillow", "onnxruntime": "onnxruntime-gpu"}
# What the pipeline reads (still.py, clips.py): a repository and the files it needs from it (None: all of it).
MODELS = [
    ("cagliostrolab/animagine-xl-4.0", None, "Animagine XL 4.0 (stills), about 7 GB"),
    ("Wan-AI/Wan2.2-I2V-A14B-Diffusers", ["model_index.json", "vae/*", "text_encoder/*", "tokenizer/*", "image_encoder/*", "image_processor/*", "scheduler/*", "transformer/config.json", "transformer_2/config.json"], "Wan 2.2 I2V A14B: its VAE and text encoder, about 12 GB"),
    ("QuantStack/Wan2.2-I2V-A14B-GGUF", ["HighNoise/Wan2.2-I2V-A14B-HighNoise-Q6_K.gguf", "LowNoise/Wan2.2-I2V-A14B-LowNoise-Q6_K.gguf"], "Wan 2.2 I2V A14B GGUF Q6 (animation), about 24 GB"),
    ("lightx2v/Wan2.2-Lightning", ["Wan2.2-I2V-A14B-4steps-lora-rank64-Seko-V1/high_noise_model.safetensors", "Wan2.2-I2V-A14B-4steps-lora-rank64-Seko-V1/low_noise_model.safetensors"], "Wan 2.2 Lightning LoRA (4 steps), about 1.2 GB"),
]
RIFE = KITGEN / "models" / "rife426"
# Where rembg keeps it: its home (U2NET_HOME, ~/.rembg or the older ~/.u2net), flat or in models\<name>\.
HOMES = ([Path(os.environ["U2NET_HOME"])] if "U2NET_HOME" in os.environ else []) + [Path.home() / ".rembg", Path.home() / ".u2net"]
CUTOUTS = [home / sub for home in HOMES for sub in (Path("models") / "isnet-anime" / "isnet-anime.onnx", Path("isnet-anime.onnx"))]
CUTOUT = next((c for c in CUTOUTS if c.exists()), CUTOUTS[0])


def cached(repo: str) -> Path:
    return Path(os.environ["HF_HOME"]) / "hub" / f"models--{repo.replace('/', '--')}"


def step(id_, title, ok, detail, actions=(), warn=False):
    return {"id": id_, "title": title, "ok": bool(ok), "warn": bool(warn), "detail": detail, "actions": list(actions)}


def check():
    steps = [step("python", "Python", sys.version_info >= (3, 10), f"Python {sys.version.split()[0]} at {sys.executable}", ["python"])]

    missing = [p for p in PACKAGES if importlib.util.find_spec(p) is None]
    steps.append(step("packages", "Python packages", not missing, "All installed." if not missing else "Missing: " + ", ".join(PIP_NAMES.get(m, m) for m in missing), ["packages"]))

    gpu, cuda = "PyTorch is not installed.", False
    if "torch" not in missing:
        try:
            import torch

            cuda = torch.cuda.is_available()
            if cuda:
                p = torch.cuda.get_device_properties(0)
                gb = p.total_memory / 2**30
                gpu = f"{p.name}, {gb:.0f} GB (CUDA {torch.version.cuda})"
                steps.append(step("gpu", "NVIDIA GPU with CUDA", True, gpu + ("" if gb >= 15 else ": 16 GB or more is needed for the animation"), warn=gb < 15))
            else:
                steps.append(step("gpu", "NVIDIA GPU with CUDA", False, f"PyTorch {torch.__version__} has no CUDA: install the packages again (the CUDA build)", ["packages"]))
        except Exception as ex:  # noqa: BLE001 - reported
            steps.append(step("gpu", "NVIDIA GPU with CUDA", False, f"PyTorch could not start: {ex}", ["packages"]))
    else:
        steps.append(step("gpu", "NVIDIA GPU with CUDA", False, gpu, ["packages"]))

    absent = [label for repo, _, label in MODELS if not cached(repo).exists()]
    steps.append(step("models", "Models", not absent, f"In {Path(os.environ['HF_HOME'])}." if not absent else "Not downloaded: " + "; ".join(absent), ["models", "kit"]))

    rife = (RIFE / "IFNet_HDv3.py").exists() and (RIFE / "flownet.pkl").exists() and (RIFE / "rife_warp.py").exists()
    steps.append(step("rife", "RIFE 4.26 (smooth loops)", rife, f"In {RIFE}." if rife else f"Not in {RIFE}: use a kit that has it (models\\rife426: IFNet_HDv3.py, flownet.pkl, rife_warp.py)", ["kit"]))

    steps.append(step("cutout", "Cut-out model (isnet-anime)", CUTOUT.exists(), f"In {CUTOUT}." if CUTOUT.exists() else "Not downloaded yet (it is also fetched the first time a character is made).", ["cutout"]))

    ff = os.environ.get("FFMPEG") or shutil.which("ffmpeg")
    steps.append(step("ffmpeg", "ffmpeg", bool(ff), ff or "Not found on PATH.", ["ffmpeg"]))
    # Node.js packs the frames into a character (pack.mts, TypeScript run as it is: 22.18 or later).
    node = shutil.which("node")
    version = None
    if node:
        try:
            version = subprocess.run([node, "--version"], capture_output=True, text=True, timeout=20).stdout.strip()
        except (OSError, subprocess.SubprocessError):
            version = None
    parts = tuple(int(x) for x in version.lstrip("v").split(".")[:2]) if version and version.lstrip("v").split(".")[0].isdigit() else ()
    ok = len(parts) == 2 and parts >= (22, 18)
    steps.append(step("node", "Node.js (packs each character)", ok,
                      f"Node.js {version} at {node}" if ok else (f"Node.js {version} is too old: 22.18 or later is needed" if version else "Not found on PATH."), ["node"]))
    print(json.dumps({"kit": str(KITGEN), "steps": steps}))


def run(args):
    print("> " + " ".join(args), flush=True)
    return subprocess.call(args)


def packages():
    # PyTorch's CUDA build first (the plain one has no CUDA on Windows), then the rest.
    code = run([sys.executable, "-m", "pip", "install", "--upgrade", "torch", "--index-url", "https://download.pytorch.org/whl/cu128"])
    rest = [PIP_NAMES.get(p, p) for p in PACKAGES if p != "torch"]
    return code or run([sys.executable, "-m", "pip", "install", "--upgrade", *rest])


def models():
    from huggingface_hub import snapshot_download

    for repo, files, label in MODELS:
        print(f"Downloading {label} ({repo})...", flush=True)
        snapshot_download(repo, allow_patterns=files)
    print("Models ready.", flush=True)
    return 0


def cutout():
    from rembg import new_session

    new_session("isnet-anime")
    print(f"Cut-out model ready in {CUTOUT}.", flush=True)
    return 0


def node():
    if not shutil.which("winget"):
        print("winget is not on this PC: install Node.js (LTS) from https://nodejs.org and put it on PATH.", flush=True)
        return 1
    return run(["winget", "install", "--id", "OpenJS.NodeJS.LTS", "-e", "--accept-source-agreements", "--accept-package-agreements"])


def ffmpeg():
    if not shutil.which("winget"):
        print("winget is not on this PC: install ffmpeg from https://www.gyan.dev/ffmpeg/builds/ and put it on PATH (or set FFMPEG).", flush=True)
        return 1
    return run(["winget", "install", "--id", "Gyan.FFmpeg", "-e", "--accept-source-agreements", "--accept-package-agreements"])


if __name__ == "__main__":
    what = sys.argv[1] if len(sys.argv) > 1 else "check"
    actions = {"check": lambda: check() or 0, "packages": packages, "models": models, "cutout": cutout, "ffmpeg": ffmpeg, "node": node}
    if what not in actions:
        sys.exit("check, packages, models, cutout, ffmpeg or node")
    sys.exit(actions[what]())
