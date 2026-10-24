"""A character's clips with Wan 2.2 I2V A14B (Apache 2.0), set up as kitgen does: its two 14B experts quantized (GGUF
Q6_K) and streamed to the GPU a few blocks at a time, the Lightning 4-step LoRA (no guidance). From the chosen still,
placed on a plain white frame with room to move, one 3 s clip (49 frames at Wan's 16 fps) per action, the camera still.

  python clips.py NAME STILL.png ACTION[@SEED] [...] [-- NAME STILL.png ACTION ...]   (one load for them all: it takes
  about 25 minutes, then each clip about 2)
  -> F:\\Development\\PC Diagnostic\\artifacts\\chargen\\NAME\\ACTION\\0000.png ... (and ACTION.mp4 to look at)"""
import json
import os
import subprocess
import sys
import time
from pathlib import Path

from settings import FF, KITGEN, OUT  # noqa: E402  (HF_HOME set there)
os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")
import numpy as np  # noqa: E402
import torch  # noqa: E402
from huggingface_hub import hf_hub_download  # noqa: E402
from PIL import Image  # noqa: E402

from gpuslot import below_normal, gpu_slot  # noqa: E402

MID = "Wan-AI/Wan2.2-I2V-A14B-Diffusers"
FRAMES, FPS = 49, 16

BASE = ("A cute chibi anime character on a plain white background, clean cel-shaded anime style, crisp outlines. The camera is completely still, "
        "fixed framing, no zoom, no pan. The character stays in the same place in the frame. Smooth, natural animation, the character looks "
        "exactly the same throughout.")
ACTIONS = {
    "idle": "The character stands in place, breathing gently, blinking, its head tilting a little, its tail or hair swaying softly.",
    "walk": "The character walks in place on the spot facing right, a steady walking cycle: legs stepping, body bobbing gently, never moving across the frame.",
    "run": "The character runs in place on the spot facing right, a quick running cycle: legs pumping, body leaning forward, never moving across the frame.",
    "jump": "The character crouches, jumps straight up happily and lands softly in exactly the same place.",
    # Dragged on the screen (PC Diagnostic): held up, then dropped and landing.
    "held": "The character is lifted up and held in the air by an unseen hand: it dangles, its legs and arms swinging gently, looking surprised, staying in the middle of the frame.",
    "land": "The character drops down from just above and lands on its feet in exactly the same place, its knees bending to soften the landing, then it stands up straight.",
    "play": "The character plays happily in place: it hops, wiggles and bounces with joy.",
    "dance": "The character dances happily in place, bouncing to a beat, swaying left and right.",
    "sit": "The character sits down on the spot and stays sitting, breathing calmly and blinking.",
    "sleep": "The character lies down on the spot, curls up and falls asleep, eyes closed, breathing slowly.",
}
# Four-legged characters: their gaits said the way an animal moves (the person's wording gave a kitten that hardly
# stepped).
ANIMALS = {"kitten", "puppy", "bunny", "corgi"}
ANIMAL_ACTIONS = {
    "walk": "The animal walks in place on the spot facing right: it lifts and puts down each paw in turn, a clear four-legged walking cycle, "
            "its body bobbing and its tail swaying, never moving across the frame.",
    "run": "The animal trots quickly in place on the spot facing right: a bouncy four-legged running cycle, paws lifting high, ears and tail "
           "bouncing, never moving across the frame.",
}
NEG = ("camera movement, zoom, pan, camera shake, cut, scene change, background change, colored background, shadow, multiple characters, "
       "extra limbs, deformed, distorted face, melting, blurry, low quality, text, watermark, subtitles")


def pipeline():
    from diffusers import AutoencoderKLWan, GGUFQuantizationConfig, UniPCMultistepScheduler, WanImageToVideoPipeline, WanTransformer3DModel
    from diffusers.hooks import apply_group_offloading

    q = GGUFQuantizationConfig(compute_dtype=torch.bfloat16)
    hi = hf_hub_download("QuantStack/Wan2.2-I2V-A14B-GGUF", "HighNoise/Wan2.2-I2V-A14B-HighNoise-Q6_K.gguf")
    lo = hf_hub_download("QuantStack/Wan2.2-I2V-A14B-GGUF", "LowNoise/Wan2.2-I2V-A14B-LowNoise-Q6_K.gguf")
    t1 = WanTransformer3DModel.from_single_file(hi, quantization_config=q, config=MID, subfolder="transformer", torch_dtype=torch.bfloat16)
    t2 = WanTransformer3DModel.from_single_file(lo, quantization_config=q, config=MID, subfolder="transformer_2", torch_dtype=torch.bfloat16)
    vae = AutoencoderKLWan.from_pretrained(MID, subfolder="vae", torch_dtype=torch.float32)
    pipe = WanImageToVideoPipeline.from_pretrained(MID, transformer=t1, transformer_2=t2, vae=vae, torch_dtype=torch.bfloat16)
    pipe.scheduler = UniPCMultistepScheduler.from_config(pipe.scheduler.config, flow_shift=5.0)
    lh = hf_hub_download("lightx2v/Wan2.2-Lightning", "Wan2.2-I2V-A14B-4steps-lora-rank64-Seko-V1/high_noise_model.safetensors")
    ll = hf_hub_download("lightx2v/Wan2.2-Lightning", "Wan2.2-I2V-A14B-4steps-lora-rank64-Seko-V1/low_noise_model.safetensors")
    pipe.load_lora_weights(lh, adapter_name="lightning")
    pipe.load_lora_weights(ll, adapter_name="lightning_2", load_into_transformer_2=True)
    cuda, cpu = torch.device("cuda"), torch.device("cpu")
    from gpuslot import free_gb

    if free_gb() >= 16:
        # Room on the GPU for one whole expert (about 12 GB): each expert moved there whole while it is used, so the
        # GPU does the work and the CPU only hands over two models per clip.
        pipe.enable_model_cpu_offload()
        pipe.vae.enable_tiling()
        print("offload: one expert on the GPU at a time", flush=True)
        return pipe
    # Little room (others on the GPU): the experts streamed a few blocks at a time. This keeps the CPU busy copying
    # weights to the GPU at every step.
    for m in (pipe.transformer, pipe.transformer_2):
        m.enable_group_offload(onload_device=cuda, offload_device=cpu, offload_type="block_level", num_blocks_per_group=4, use_stream=True, low_cpu_mem_usage=True)
    apply_group_offloading(pipe.text_encoder, onload_device=cuda, offload_device=cpu, offload_type="block_level", num_blocks_per_group=4)
    print("offload: blocks streamed (the GPU is shared)", flush=True)
    pipe.vae.to(cuda)
    pipe.vae.enable_tiling()
    return pipe


# The CPU's threads kept few: with weights on the move they would otherwise spin on every core.
torch.set_num_threads(min(6, os.cpu_count() or 6))


def stage(still: Path) -> Image.Image:
    """The still on a plain white frame of Wan's 480p size (upright for a person, wide for an animal), the character
    about 62 % of its height, its feet at 86 %: room above to jump, to the sides to move."""
    # Cut out with the anime matting model (specks and the still's own off-white left behind), then on pure white.
    from rembg import new_session, remove

    source = Image.open(still)
    if source.mode == "RGBA" and np.asarray(source)[..., 3].min() < 250:
        # Already cut out (a character's own first frame): used as it is.
        cut = source
    else:
        cut = remove(source.convert("RGB"), session=new_session("isnet-anime"), post_process_mask=True)
    alpha = np.asarray(cut)[..., 3]
    ink = np.where(alpha > 32)
    top, bottom, left, right = ink[0].min(), ink[0].max() + 1, ink[1].min(), ink[1].max() + 1
    white = Image.new("RGBA", cut.size, "white")
    white.alpha_composite(cut)
    char = white.convert("RGB").crop((left, top, right, bottom))
    upright = char.height >= char.width
    w, h = (480, 832) if upright else (832, 480)
    k = min(0.62 * h / char.height, 0.8 * w / char.width)
    char = char.resize((max(1, round(char.width * k)), max(1, round(char.height * k))), Image.LANCZOS)
    frame = Image.new("RGB", (w, h), "white")
    frame.paste(char, ((w - char.width) // 2, round(0.86 * h) - char.height))
    return frame


def main():
    # Characters separated by "--": each its name, its still and its actions.
    groups, current = [], []
    for arg in sys.argv[1:] + ["--"]:
        if arg == "--":
            if current:
                groups.append((current[0], Path(current[1]), current[2:]))
            current = []
        else:
            current.append(arg)
    below_normal()
    staged = []
    for name, still, wanted in groups:
        start = stage(still)
        folder = OUT / name
        folder.mkdir(parents=True, exist_ok=True)
        start.save(folder / "stage.png")
        staged.append((name, start, wanted))
    count = sum(len(w) for _, _, w in staged)
    with gpu_slot(f"Wan 2.2 I2V A14B: {', '.join(n for n, _, _ in staged)} clips ({count}) for PC Diagnostic", minutes=30 + 3 * count, need_gb=10):
        t0 = time.time()
        pipe = pipeline()
        print(f"loaded in {time.time() - t0:.0f} s", flush=True)
        for name, start, wanted in staged:
            folder = OUT / name
            for item in wanted:
                action, _, seed = item.partition("@")
                t0 = time.time()
                animal = name in ANIMALS or name in os.environ.get("CHARGEN_ANIMALS", "").split(",")
                said = ANIMAL_ACTIONS.get(action) if animal else None
                # A new animation's own wording (animate.py), else the action's.
                own = json.loads(os.environ.get("CHARGEN_PROMPTS", "{}")).get(action)
                frames = pipe(image=start, prompt=f"{BASE} {own or said or ACTIONS[action]}", negative_prompt=NEG, height=start.height, width=start.width, num_frames=FRAMES,
                              num_inference_steps=4, guidance_scale=1.0, guidance_scale_2=1.0, generator=torch.Generator("cpu").manual_seed(int(seed or 7)),
                              output_type="np").frames[0]
                out = folder / action
                out.mkdir(exist_ok=True)
                for f in out.glob("*.png"):
                    f.unlink()
                for i, f in enumerate(frames):
                    Image.fromarray((np.clip(f, 0, 1) * 255).astype(np.uint8)).save(out / f"{i:04d}.png")
                subprocess.run([FF, "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", str(out / "%04d.png"), "-pix_fmt", "yuv420p", str(folder / f"{action}.mp4")], check=True)
                print(f"{name} {action}: {len(frames)} frames in {time.time() - t0:.0f} s", flush=True)
        del pipe
        torch.cuda.empty_cache()


if __name__ == "__main__":
    main()
