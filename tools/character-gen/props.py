"""Props for the characters (things shown with them as they react: an ice pack on the head, a cone beside them) made
with Animagine XL 4.0 in the characters' style, one object alone on white, a few seeds each; then cut out (rembg's
isnet-general-use, made for objects), cropped to the object and scaled to at most 256 px on its longer side.

  python props.py [ID ...] [--seeds 4]      (kitgen's venv: torch, diffusers, rembg)
  -> OUT\\props\\ID\\SEED.png (as drawn) and SEED-cut.png (cut out)"""
import argparse
from pathlib import Path

from settings import OUT  # noqa: E402  (HF_HOME set there)
import torch  # noqa: E402

from gpuslot import below_normal, gpu_slot  # noqa: E402

STYLE = "no humans, still life, object focus, single object, centered, chibi style, anime coloring, cute, clean lineart, white background, simple background"
QUALITY = "masterpiece, high score, great score, absurdres"
NEGATIVE = ("1girl, 1boy, person, humans, hands, animal, text, letters, logo, watermark, signature, lowres, worst quality, low quality, blurry, "
            "multiple objects, cropped, out of frame, scenery, table, floor, shadow, gradient background, colored background")

# id: what it is (the prompt's subject). Their names, where they go and how they move are in reactions.json's props.
PROPS = {
    "ice-pack": "a blue ice pack, gel cold pack with ice cubes, frosty",
    "umbrella": "an open red umbrella, side view, curved handle",
    "siren": "a red rotating emergency siren light, police beacon, glowing red dome, small black base",
    "desk-fan": "a small white electric desk fan, round cage, side view, on a stand",
    "coffee": "a white coffee mug, hot coffee, steam",
    "trophy": "a shiny gold trophy cup, two handles, small base, star",
    "cone": "an orange traffic cone with white stripes",
    "toolbox": "a red metal toolbox, wrench and screwdriver sticking out",
    "boxes": "a pile of three cardboard boxes, stacked",
    "thermometer": "a big red glass thermometer, high temperature",
    "battery": "a green battery, cartoon battery, charge bars",
    "popcorn": "a red and white striped popcorn bucket, full of popcorn",
    "extinguisher": "a red fire extinguisher, side view, black hose and nozzle pointing right, handle on top",
    "hose": "a green garden hose spray nozzle gun, side view, pointing right, coiled green hose behind it",
}


def cut(image, session):
    """The object alone (cut out), cropped to it and at most 256 px on its longer side."""
    from rembg import remove
    from PIL import Image

    alone = remove(image, session=session, post_process_mask=True)
    box = alone.getchannel("A").point(lambda a: 255 if a > 24 else 0).getbbox()
    if box:
        alone = alone.crop(box)
    k = 256 / max(alone.size)
    if k < 1:
        alone = alone.resize((max(1, round(alone.width * k)), max(1, round(alone.height * k))), Image.LANCZOS)
    return alone


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ids", nargs="*")
    ap.add_argument("--seeds", type=int, default=4)
    ap.add_argument("--first", type=int, default=1)
    args = ap.parse_args()
    ids = args.ids or list(PROPS)
    below_normal()
    from diffusers import EulerAncestralDiscreteScheduler, StableDiffusionXLPipeline
    from rembg import new_session

    with gpu_slot("Animagine XL 4.0: props for PC Diagnostic's characters", minutes=5 + 0.3 * len(ids) * args.seeds, need_gb=8):
        pipe = StableDiffusionXLPipeline.from_pretrained("cagliostrolab/animagine-xl-4.0", torch_dtype=torch.float16, use_safetensors=True)
        pipe.scheduler = EulerAncestralDiscreteScheduler.from_config(pipe.scheduler.config)
        pipe.to("cuda")
        drawn = []
        for pid in ids:
            folder = OUT / "props" / pid
            folder.mkdir(parents=True, exist_ok=True)
            for seed in range(args.first, args.first + args.seeds):
                image = pipe(f"{PROPS[pid]}, {STYLE}, {QUALITY}", negative_prompt=NEGATIVE, width=1024, height=1024, num_inference_steps=28, guidance_scale=5,
                             generator=torch.Generator("cuda").manual_seed(seed)).images[0]
                image.save(folder / f"{seed}.png")
                drawn.append((folder, seed, image))
                print(pid, seed, flush=True)
        del pipe
        torch.cuda.empty_cache()
    session = new_session("isnet-general-use")
    for folder, seed, image in drawn:
        cut(image, session).save(folder / f"{seed}-cut.png")
    print("cut", len(drawn), flush=True)


if __name__ == "__main__":
    main()
