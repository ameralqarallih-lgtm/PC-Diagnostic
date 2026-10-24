"""Where the character pipeline finds things. Each can be set in the environment:

- KITGEN: the generation kit (its .venv runs these scripts; its models are read, never changed): hf (Animagine XL 4.0,
  Wan 2.2 I2V A14B GGUF + Lightning LoRA), models\\rife426 (RIFE 4.26). Default: kit\\ beside these scripts in a
  repository; installed with PC Diagnostic, %LOCALAPPDATA%\\PcDiagnostic\\characters\\kit. setup.py fills it.
- CHARGEN_OUT: the outputs (default: artifacts\\chargen in this repo, git ignores it; installed with PC Diagnostic, where
  it cannot write beside itself: %LOCALAPPDATA%\\PcDiagnostic\\characters\\generator-out).
- FFMPEG: ffmpeg.exe (default: the one on PATH).
- GPU_LOCK: the folder of an optional shared GPU lock (a gpu_lock.py there, for PCs whose GPU other jobs share;
  default: %USERPROFILE%\\.gpu; without one, nothing is waited for).
"""
import os
import shutil
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
_LOCAL = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "PcDiagnostic" / "characters"
KITGEN = Path(os.environ.get("KITGEN") or (Path(__file__).resolve().parent / "kit" if (_REPO / ".git").exists() else _LOCAL / "kit"))
os.environ.setdefault("HF_HOME", str(KITGEN / "hf"))
OUT = Path(os.environ.get("CHARGEN_OUT") or (_REPO / "artifacts" / "chargen" if (_REPO / ".git").exists()
                                              else _LOCAL / "generator-out"))
# The steps it runs (pack.mts among them) write to the same place.
os.environ["CHARGEN_OUT"] = str(OUT)
FF = os.environ.get("FFMPEG") or shutil.which("ffmpeg") or "ffmpeg"
GPU_LOCK = Path(os.environ.get("GPU_LOCK", Path.home() / ".gpu"))
