# sam-segment.py — one-shot SAM point segmentation for the studio's "click to select an object" mask.
# Usage:  python sam-segment.py <image> <x> <y> <out-mask.png> <checkpoint> [model_type]
# Loads Segment-Anything, sets the image, predicts from a single positive click point, and writes the
# best-scoring mask as a white-on-black PNG (the studio's inpaint-mask convention). Standard SamPredictor API.
import sys
import numpy as np
from PIL import Image
from segment_anything import sam_model_registry, SamPredictor

img_path, x, y, out_path, ckpt = sys.argv[1:6]
model_type = sys.argv[6] if len(sys.argv) > 6 else "vit_b"

sam = sam_model_registry[model_type](checkpoint=ckpt)
predictor = SamPredictor(sam)

image = np.array(Image.open(img_path).convert("RGB"))
predictor.set_image(image)

masks, scores, _ = predictor.predict(
    point_coords=np.array([[int(float(x)), int(float(y))]]),
    point_labels=np.array([1]),          # 1 = a positive (include) click
    multimask_output=True,
)
best = masks[int(np.argmax(scores))]      # highest-confidence mask
Image.fromarray((best * 255).astype(np.uint8)).save(out_path)
print(out_path)
