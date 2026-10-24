# Character generation

The characters that come with PC Diagnostic are original characters, generated and animated with local models. Each becomes one character file (`.char`: transparent PNG frames for each action, the format the editor and the recorder play; see `CharacterPack.cs`).

## The models

They live in the generation kit (`kit\\` beside these scripts by default; `setup.py` downloads them there), read and never changed; see `settings.py`.

- **Animagine XL 4.0** (CreativeML Open RAIL++-M, so its pictures may be used commercially): the character's still. Prompts name original characters only, never characters from existing series.
- **Wan 2.2 I2V A14B** (Apache 2.0): the animation, one 3 s clip per action from the still, the camera still. Its two experts are GGUF Q6_K, streamed to the GPU a block at a time, with the Lightning 4-step LoRA.
- **RIFE 4.26** (MIT): each clip as a seamless 24 fps loop.
- **rembg isnet-anime** (MIT): the cut-out. Specks floating apart from the character are left out.

## The GPU

An NVIDIA GPU with CUDA is needed (the shipped characters were made on an RTX 4090; Wan 2.2 streams its experts, so less memory works, more slowly). `gpuslot.py` waits while there is too little free GPU memory, and, where the PC has a shared GPU lock (`GPU_LOCK`, see `settings.py`), for its turn.

To run PC Diagnostic itself (and see the characters on the screen), use `.\tools\run-pcdiag.ps1` from the repo root.

## Run it (one command per step)

In PowerShell, from `tools\character-gen`:

```powershell
$PY = ".\kit\.venv\Scripts\python.exe"   # or any Python with the packages: python setup.py check

# 1. Candidate stills (4 seeds each) to choose from: artifacts\chargen\NAME\still-SEED.png
& $PY run.py stills yuki akari --seeds 4

# 2. A character from its chosen still: every clip, its loops and its .char (artifacts\chargen\NAME\NAME.char).
#    Several characters at once share one Wan load: separate them with --.
& $PY run.py make yuki --still ..\..\artifacts\chargen\yuki\still-21.png --title Yuki `
               -- bunny --still ..\..\artifacts\chargen\bunny\still-22.png --title Mochi

# Redo one clip with another seed (walk@12), then repack from the clips already made (no Wan):
& $PY run.py make kitten --still ..\..\artifacts\chargen\kitten\still-4.png --actions walk@12,run@12 --faces -1
& $PY run.py pack kitten --faces -1 --title Mikan
```

`run.py` waits for its turn on the shared GPU, holds it for the step's goal and releases it at the end. Options:
- `--actions`: idle, walk, run, jump, play, dance, sit, sleep (default: all but dance).
- `--faces -1`: the drawing looks left.
- `--title`: the name shown.
- `--tall` and `--fps`: the size and rate of the frames (default 200 px and 20 fps).

## Making a character (the steps run.py takes)

The steps below use kitgen's Python: `%KITGEN%\.venv\Scripts\python.exe`.

1. **Stills.** Add the character to `CHARACTERS` in `still.py`: a prompt (full body, from the side, facing right, a white background) and a size. Then run `python still.py NAME --seeds 4` and choose the best still.
2. **Clips.** Run `python clips.py NAME STILL.png idle walk run jump play sit sleep [-- OTHER STILL2.png ...]`.
   - One run loads Wan once (about 1–25 min, depending on what is already in memory); each clip then takes about a minute.
   - Use `ACTION@SEED` for another try at a clip.
   - Four-legged characters (`ANIMALS`) get their own wording for walking and running.
3. **Loops.** Run `python finish.py NAME idle walk ... [--faces -1] [--tall 200] [--fps 20]`. It:
   - makes the loops (RIFE);
   - cuts the character out;
   - holds the walk in place (its slide across the frame taken out);
   - scales every clip alike, the idle clip standing `--tall` pixels;
   - lays them on one spot for the feet.

   Use `--faces -1` when the drawing looks left.
4. **Pack.** Run `node pack.mts NAME`: 256 colours with a palette for each clip, giving `artifacts\chargen\NAME\NAME.char`.
5. **Ship it.** Copy the `.char` into `src\PcDiag.Core\Overlay\Characters` and add its id to `OverlayCharacters.ShippedIds`.

Outputs go to `artifacts\chargen`, which git ignores; only the finished `.char` files are committed.
