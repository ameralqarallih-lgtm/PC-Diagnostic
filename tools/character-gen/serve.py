"""The character generator service: PC Diagnostic's "internal" generator (Settings > Characters > Generator), the
pipeline of this folder behind a small HTTP API. Any other service that answers the same API can be used as an
"external" generator.

  %PY% serve.py [--host 127.0.0.1] [--port 7865] [--key SECRET]      (%PY%: kitgen's Python, see README.md)

The API (JSON; with --key, every call needs "Authorization: Bearer SECRET"):
  GET  /v1/health                    {"name", "version": 1, "actions": [...], "busy": bool}
  POST /v1/stills                    {"prompt", "kind": "person"|"animal", "count": 1-4}       -> {"job"}
  POST /v1/characters                {"still": file id, "name", "kind", "actions": [...], "faces": 1|-1} -> {"job"}
  POST /v1/files                     a character's .char (the raw bytes, at most 32 MB)                -> {"file"}
  POST /v1/animations                {"character": file id, "name": an animation id, "prompt", "seed"?} -> {"job"}
                                     a new animation for that character: the job's "character" is its extra .char;
                                     or {"items": [{"character", "name", "prompt", "seed"?}, ...]} (at most 64): every
                                     clip with one load of Wan; the job's "characters" are their files in that order
                                     (null for one that failed) and "errors" why
  GET  /v1/jobs/ID                   {"state": "queued"|"running"|"done"|"failed", "step", "progress": 0-1,
                                      "error", "stills": [file ids], "character": file id}
  GET  /v1/files/ID                  the file (a still's PNG, or the character's .char)
  POST /v1/stop                      stops the generator (a job under way is given up)

Jobs run one at a time, each holding the shared GPU for its goal (PROTOCOL.md) while it runs.
"""
import argparse
import json
import os
import queue
import random
import re
import subprocess
import sys
import threading
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from settings import OUT  # noqa: E402
import run  # noqa: E402

ACTIONS = ["idle", "walk", "run", "jump", "play", "dance", "sit", "sleep"]
QUALITY_PERSON = "solo, chibi, super deformed, big head, full body, standing, from side, facing right, open eyes, big eyes"
QUALITY_ANIMAL = "no humans, solo, chibi, full body, standing, from side, facing right, cute, big eyes"
JOBS: dict[str, dict] = {}
FILES: dict[str, Path] = {}
WORK: "queue.Queue[tuple[str, dict]]" = queue.Queue()
BLOCKED = re.compile(r"[^\w\s,()'-]", re.UNICODE)
# A new animation's id, as the app checks it.
ANIMATION = re.compile(r"^[a-z][a-z0-9-]{1,23}$")


def file_id(path: Path) -> str:
    fid = uuid.uuid4().hex[:16]
    FILES[fid] = path
    return fid


def steps(job: dict, label: str, args: list[str], total: int = 0, env: dict | None = None):
    """Runs one step, its progress counted from the clips it reports."""
    job["step"] = label
    done = 0
    # Its output read as UTF-8 (progress bars and models print characters the Windows code page cannot read).
    proc = subprocess.Popen([sys.executable, *args] if args[0].endswith(".py") else args, cwd=HERE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, encoding="utf-8", errors="replace", env={**os.environ, "PYTHONIOENCODING": "utf-8", "PYTHONUTF8": "1", **(env or {})})
    tail = []
    for line in proc.stdout:
        # A progress bar rewrites its line: its last state.
        text = line.rstrip().split("\r")[-1].strip()
        tail = (tail + [text])[-20:]
        if text:
            job["log"] = (job.get("log", []) + [f"{label}: {text}"])[-40:]
        if total and ("frames in" in line or ": finished" in line or re.match(r"^\w+ \d+$", line.strip())):
            done += 1
            job["progress"] = min(0.99, done / total)
    if proc.wait() != 0:
        raise RuntimeError(f"{label} failed: " + " | ".join(tail[-4:]))


def work():
    while True:
        jid, body = WORK.get()
        job = JOBS[jid]
        job["state"] = "running"
        try:
            animal = body.get("kind") == "animal"
            name = f"gen-{jid}"
            if body["type"] == "animation":
                # New animations for characters that exist (animate.py --batch): each clip alone, at its character's
                # height, every clip with one load of Wan.
                items = body.get("items") or [{"character": body["character"], "name": body["name"], "prompt": body["prompt"], "seed": body.get("seed", 7)}]
                spec = OUT / "batches" / f"anim-{jid}.json"
                spec.parent.mkdir(parents=True, exist_ok=True)
                spec.write_text(json.dumps({"items": [{"char": str(FILES[i["character"]]), "action": i["name"], "prompt": BLOCKED.sub("", i["prompt"])[:300].strip(),
                                                       "seed": int(i.get("seed", 7))} for i in items]}), encoding="utf-8")
                names = sorted({i["name"] for i in items})
                run.reserve(f"animations {', '.join(names)} for {len(items)} characters' clips ({jid})")
                try:
                    # Progress: each clip Wan makes, then each one finished.
                    steps(job, "Animating", ["animate.py", "--batch", str(spec)], total=2 * len(items))
                finally:
                    run.lock("release", run.OWNER, f"animations {', '.join(names)} made ({jid})")
                done = json.loads(spec.read_text(encoding="utf-8"))["items"]
                job["characters"] = [file_id(Path(d["out"])) if d.get("out") else None for d in done]
                job["errors"] = [d.get("error") for d in done]
                job["character"] = job["characters"][0]
            elif body["type"] == "stills":
                prompt = ", ".join([QUALITY_ANIMAL if animal else QUALITY_PERSON, BLOCKED.sub("", body["prompt"])[:400], "white background, simple background"])
                count = max(1, min(4, int(body.get("count", 4))))
                first = random.randint(100, 99999)
                run.reserve(f"character stills for a user's prompt ({jid})")
                try:
                    steps(job, "Drawing the character", ["still.py", name, "--prompt", prompt, "--size", "1152x896" if animal else "832x1216",
                                                         "--seeds", str(count), "--first", str(first)], total=count)
                finally:
                    run.lock("release", run.OWNER, f"stills made ({jid})")
                job["stills"] = [file_id(OUT / name / f"still-{s}.png") for s in range(first, first + count)]
            else:
                still = FILES[body["still"]]
                actions = [a for a in body.get("actions", ACTIONS) if a in ACTIONS] or ["idle"]
                if "idle" not in actions:
                    actions.insert(0, "idle")
                title = BLOCKED.sub("", body.get("name", "Character"))[:40].strip() or "Character"
                env = {"CHARGEN_ANIMALS": name} if animal else {}
                run.reserve(f"a character animated for a user ({jid}: {len(actions)} clips)")
                try:
                    steps(job, "Animating", ["clips.py", name, str(still), *actions], total=len(actions), env=env)
                finally:
                    run.lock("release", run.OWNER, f"character animated ({jid})")
                steps(job, "Making the loops", ["finish.py", name, *actions, "--faces", str(int(body.get("faces", 1))), "--title", title,
                                                "--tall", "200", "--fps", "20"])
                steps(job, "Packing", ["node", "pack.mts", name])
                steps(job, "Packing", ["loops.py", str(OUT / name / f"{name}.char")])
                job["character"] = file_id(OUT / name / f"{name}.char")
            job.update(state="done", progress=1.0, step="Done")
        except Exception as ex:  # noqa: BLE001 - reported to the app
            job.update(state="failed", error=str(ex)[:500])


class Handler(BaseHTTPRequestHandler):
    key = None

    def _send(self, code: int, value=None, data: bytes | None = None, kind="application/json"):
        body = data if data is not None else json.dumps(value).encode()
        self.send_response(code)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _allowed(self) -> bool:
        if self.key and self.headers.get("Authorization") != f"Bearer {self.key}":
            self._send(401, {"error": "a key is needed"})
            return False
        return True

    def _file(self) -> bytes:
        """An uploaded file: the body by its Content-Length (at most 32 MB)."""
        return self.rfile.read(min(int(self.headers.get("Content-Length", 0)), 32 * 1024 * 1024))

    def _body(self) -> bytes:
        """The request's body: by its Content-Length, or sent in chunks (at most 64 KB)."""
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            data = b""
            while len(data) <= 65536:
                size = int(self.rfile.readline().split(b";")[0].strip() or b"0", 16)
                if size == 0:
                    self.rfile.readline()
                    break
                data += self.rfile.read(size)
                self.rfile.readline()
            return data
        return self.rfile.read(min(int(self.headers.get("Content-Length", 0)), 65536))

    def do_GET(self):  # noqa: N802
        if not self._allowed():
            return
        parts = self.path.strip("/").split("/")
        if parts == ["v1", "health"]:
            return self._send(200, {"name": "PC Diagnostic character generator", "version": 1, "actions": ACTIONS, "animations": True,
                                    "busy": any(j["state"] in ("queued", "running") for j in JOBS.values())})
        if len(parts) == 3 and parts[:2] == ["v1", "jobs"] and parts[2] in JOBS:
            return self._send(200, JOBS[parts[2]])
        if len(parts) == 3 and parts[:2] == ["v1", "files"] and parts[2] in FILES and FILES[parts[2]].exists():
            p = FILES[parts[2]]
            return self._send(200, data=p.read_bytes(), kind="image/png" if p.suffix == ".png" else "application/octet-stream")
        self._send(404, {"error": "not found"})

    def do_POST(self):  # noqa: N802
        if not self._allowed():
            return
        if self.path == "/v1/files":
            data = self._file()
            if data[:4] != b"PCDC":
                return self._send(400, {"error": "a character file (.char) is expected"})
            folder = OUT / "uploads"
            folder.mkdir(parents=True, exist_ok=True)
            path = folder / f"{uuid.uuid4().hex[:16]}.char"
            path.write_bytes(data)
            return self._send(201, {"file": file_id(path)})
        try:
            body = json.loads(self._body() or b"{}")
        except ValueError:
            return self._send(400, {"error": "JSON expected"})
        if self.path == "/v1/stop":
            self._send(202, {"stopping": True})
            # Off the request's thread: shutting down waits for the request to end.
            threading.Thread(target=self.server.shutdown, daemon=True).start()
            return
        kind = {"/v1/stills": "stills", "/v1/characters": "character", "/v1/animations": "animation"}.get(self.path)
        if kind == "animation":
            items = body.get("items") or [body]
            if len(items) > 64 or any(i.get("character") not in FILES or not ANIMATION.match(str(i.get("name", ""))) or not str(i.get("prompt", "")).strip() for i in items):
                return self._send(400, {"error": "each needs an uploaded character, an animation id (2-24 lowercase letters, digits or dashes) and a prompt (at most 64)"})
        if not kind or (kind == "stills" and not str(body.get("prompt", "")).strip()) or (kind == "character" and body.get("still") not in FILES):
            return self._send(400, {"error": "a prompt (stills) or a still from this generator (characters) is needed"})
        jid = uuid.uuid4().hex[:12]
        JOBS[jid] = {"id": jid, "state": "queued", "step": "Waiting for the GPU", "progress": 0, "error": None, "stills": [], "character": None, "characters": [], "errors": []}
        WORK.put((jid, {**body, "type": kind}))
        self._send(202, {"job": jid})

    def log_message(self, *args):  # quiet
        pass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=7865)
    ap.add_argument("--key", default=os.environ.get("CHARGEN_KEY"))
    args = ap.parse_args()
    Handler.key = args.key
    threading.Thread(target=work, daemon=True).start()
    print(f"PC Diagnostic character generator on http://{args.host}:{args.port} ({'key required' if args.key else 'no key'})", flush=True)
    ThreadingHTTPServer((args.host, args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
