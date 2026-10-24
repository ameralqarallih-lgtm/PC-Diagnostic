"""A GPU slot for a heavy job: enough free GPU memory, and, where the PC has one, a shared lock that other GPU jobs take
too (a gpu_lock.py in GPU_LOCK, see settings.py; without it nothing else is waited for)."""
import contextlib
import subprocess
import sys
import time

from settings import GPU_LOCK  # noqa: E402

try:
    sys.path.insert(0, str(GPU_LOCK))
    from gpu_lock import gpu_lock  # noqa: E402
except ImportError:
    @contextlib.contextmanager
    def gpu_lock(owner: str, goal: str):  # noqa: ARG001 (no shared lock on this PC)
        yield

OWNER = "pc-diagnostic"


def free_gb() -> float:
    out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used,memory.total", "--format=csv,noheader,nounits"], capture_output=True, text=True, check=True).stdout
    used, total = (float(v) for v in out.strip().splitlines()[0].split(","))
    return (total - used) / 1024


@contextlib.contextmanager
def gpu_slot(goal: str, minutes: float | None = None, need_gb: float = 8):
    """Waits for a safe moment, then holds the GPU for a goal while the job works towards it (reserved by goal, never by
    time; `minutes` is ignored)."""
    while True:
        with gpu_lock(OWNER, goal=goal):
            if free_gb() >= need_gb:
                yield
                return
        print(f"[gpu_slot] waiting: {free_gb():.1f} GB free of the {need_gb} GB needed", file=sys.stderr, flush=True)
        time.sleep(30)


def below_normal():
    """This process at below-normal priority (the user may be playing)."""
    import ctypes

    ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)
