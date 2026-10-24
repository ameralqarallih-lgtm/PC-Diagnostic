"""Character stills with Animagine XL 4.0 (CreativeML Open RAIL++-M: its pictures may be used commercially): original
characters only (no named ones), full body on a plain white background, a few seeds each to choose from.

  python still.py NAME [NAME ...] [--seeds 4]      (kitgen's venv: torch, diffusers)
  -> F:\\Development\\PC Diagnostic\\artifacts\\chargen\\NAME\\still-SEED.png"""
import argparse
import os
import sys
from pathlib import Path

from settings import FF, KITGEN, OUT  # noqa: E402  (HF_HOME set there)
import torch  # noqa: E402

from gpuslot import below_normal, gpu_slot  # noqa: E402

QUALITY = "masterpiece, high score, great score, absurdres"
NEGATIVE = ("lowres, bad anatomy, bad hands, text, error, missing finger, extra digits, fewer digits, cropped, worst quality, low quality, low score, "
            "bad score, average score, signature, watermark, username, blurry, multiple views, out of frame, cut off, shadow, gradient background, scenery, "
            "closed eyes, colored background, grey background, stars, sparkles, floating objects, multiple animals, 2cats, 3cats, 2dogs, 3dogs, multiple dogs, multiple cats")

# Original characters: who they are, and the picture's size (Animagine's own sizes).
CHARACTERS = {
    "hana": ("1girl, solo, chibi, super deformed, big head, full body, standing, from side, facing right, open eyes, big eyes, happy, short pink hair, bob cut, star hair ornament, "
             "oversized white hoodie, cat ear hood, pleated skirt, white sneakers, white background, simple background", (832, 1216)),
    "ren": ("1boy, solo, chibi, super deformed, big head, full body, standing, from side, facing right, open eyes, grin, spiky black hair, blue eyes, headphones around neck, "
            "black jacket, red hoodie, cargo shorts, sneakers, white background, simple background", (832, 1216)),
    "kitten": ("no humans, solo, cat, kitten, orange tabby cat, chibi, full body, standing, from side, facing right, cute, big green eyes, "
               "fluffy, white background, simple background", (1152, 896)),
    "puppy": ("no humans, solo, dog, shiba inu, puppy, chibi, full body, standing, from side, facing right, cute, happy, tongue out, "
              "red collar, white background, simple background", (1152, 896)),
    "yuki": ("1girl, solo, chibi, super deformed, big head, full body, standing, from side, facing right, open eyes, big eyes, gentle smile, "
             "long light blue hair, white beret, earmuffs, white winter coat, fur trim, red scarf, brown boots, white background, simple background", (832, 1216)),
    "akari": ("1girl, solo, chibi, super deformed, big head, full body, standing, from side, facing right, open eyes, big eyes, happy, fox girl, "
              "fox ears, fluffy fox tail, orange hair, short twintails, yellow hoodie, denim shorts, sneakers, white background, simple background", (832, 1216)),
    "bunny": ("no humans, solo, rabbit, bunny, white rabbit, chibi, full body, standing, from side, facing right, cute, big eyes, long ears, fluffy, "
              "pink nose, white background, simple background", (1152, 896)),
    "corgi": ("no humans, solo, dog, corgi, puppy, chibi, full body, standing, from side, facing right, cute, happy, tongue out, blue collar, "
              "white background, simple background", (1152, 896)),
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("names", nargs="+")
    ap.add_argument("--seeds", type=int, default=4)
    ap.add_argument("--first", type=int, default=1)
    ap.add_argument("--tag", default="")
    ap.add_argument("--prompt", help="a character of one's own (the generator service): its tags, for the single NAME")
    ap.add_argument("--size", default="832x1216", help="with --prompt: WxH (832x1216 a person, 1152x896 an animal)")
    args = ap.parse_args()
    if args.prompt:
        w, h = (int(v) for v in args.size.split("x"))
        CHARACTERS[args.names[0]] = (args.prompt, (w, h))
    below_normal()
    from diffusers import EulerAncestralDiscreteScheduler, StableDiffusionXLPipeline

    with gpu_slot("Animagine XL 4.0: character stills for PC Diagnostic", minutes=5 + 0.5 * len(args.names) * args.seeds, need_gb=8):
        pipe = StableDiffusionXLPipeline.from_pretrained("cagliostrolab/animagine-xl-4.0", torch_dtype=torch.float16, use_safetensors=True)
        pipe.scheduler = EulerAncestralDiscreteScheduler.from_config(pipe.scheduler.config)
        pipe.to("cuda")
        for name in args.names:
            prompt, (w, h) = CHARACTERS[name]
            folder = OUT / name
            folder.mkdir(parents=True, exist_ok=True)
            for seed in range(args.first, args.first + args.seeds):
                image = pipe(f"{prompt}, {QUALITY}", negative_prompt=NEGATIVE, width=w, height=h, num_inference_steps=28, guidance_scale=5,
                             generator=torch.Generator("cuda").manual_seed(seed)).images[0]
                image.save(folder / f"still{args.tag}-{seed}.png")
                print(name, seed, flush=True)
        del pipe
        torch.cuda.empty_cache()


if __name__ == "__main__":
    main()
