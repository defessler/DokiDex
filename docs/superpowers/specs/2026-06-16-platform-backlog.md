<!-- Living platform-feature backlog. Round 2 = Leonardo/Krea/Ideogram/Runway/Civitai/Tensor/SeaArt. Round 3 = Midjourney + Pika/Kling + Recraft/Playground, double-validated (UX-VALUE dedup + local-feasibility lens). All 8 platforms merged, deduped, prioritized. -->

# DokiDex — Prioritized Feature Backlog (deduped, locally-feasible only)

Each item is judged on two lenses: **adoption value** (`whyAdopt`/`capability`) and **local feasibility** (single-GPU 32GB + mutually-exclusive llm/media groups, no cloud, no social). Kept = both lenses positive, or one decisively strong. Round-3 items that merely restate a round-2 row were folded in as a strengthened "From" source rather than duplicated. Cloud/social/model-unavailable items are in DROPPED.

Platforms: Leonardo, Krea, Ideogram, Runway, Civitai/Tensor/SeaArt (round 2); Midjourney, Pika+Kling, Recraft+Playground (round 3).

---

## Composer (prompt box)

| Feature | From | Capability | Priority | Local note |
|---|---|---|---|---|
| **Named @-references in prompt** (uploads/styles/chars get a name, `@Hero` autocompletes inline; reusable shelf). Extend each ref with **per-ref weight + overall strength + blendable refs** (`@A::2 @B::1`) and a stable id; **single subject-slot ("Omni") with one resemblance dial** lives on this shelf. | Runway + Midjourney (`--sref`/`--oref`) + Pika/Kling (Ingredients) + Recraft (Custom Style) | reference + prompt tooling, subject/style consistency, self-documenting | **P1** | Token → stored image path → IP-Adapter/style-ref param at submit; per-ref weight = adapter scale; blend = multiple IP-Adapter refs; subject slot optionally backed by FaceID/InstantID. One reference-bundle subsystem powers the whole shelf. Highest-leverage prompt primitive. |
| **Wildcards / dynamic-prompt randomizer** (`__pose__` drawn from local .txt per seed) | Civitai | controlled variation per batch | **P1** | SwarmUI wildcard syntax; expand pre-seed, record **resolved** prompt to sidecar. |
| **Steerable rewriter** (free-text "how to rewrite" instruction + re-run) | Leonardo | directable prompt tooling | **P1** | Add instruction field to existing LLM rewriter template — no new infra; critical for uncensored tone steering. |
| **Conversational iterate-by-instruction** (one-card chat thread: "make it night", "add a red scarf" — carries forward subject/style/seed; rewriter emits a prompt *diff*, resubmits) | Midjourney | stateful delta-editing loop | **P1** | Builds on the steerable rewriter: llm group takes [prior expanded prompt + card sidecar + instruction] → revised prompt, resubmit with carried seed/refs. One llm/media swap hop per turn. No voice. |
| **Magic-Prompt tri-state Off/On/Auto + store original AND enhanced** | Ideogram | rewriter transparency/auditability | **P1** | Sidecar holds expanded prompt; add original + per-job mode. Essential when rewrite breaks literal text. |
| **Describe (image→editable prompt / reverse-prompt)** | Ideogram + Recraft (prompt extraction) | reverse-prompt bootstrap | **P2** | Reuse llm group as VLM/interrogator captioner → drops straight into composer & feeds the rewriter; honors llm/media exclusion. |
| **Draft↔Final toggle + one-click Enhance-from-card** (same prompt → Lightning/LCM/Turbo low-step fast path; "Enhance" re-POSTs the stored sidecar seed+prompt at the full profile; draft vs final cards visibly distinguished) | Civitai + Midjourney (Draft Mode) | cheap explore / expensive commit economy | **P1** | Scheduler/steps/res swap on the shipped Z-Image Turbo path; Enhance = resubmit resolved seed at Base. A deliberate two-speed GPU economy — compounds with every exploratory action. |
| **Aesthetic dials: Stylize / Weird / Variety** (named abstract sliders for aesthetic opinion, controlled novelty, batch spread) — *Raw already shipped* | Midjourney | friendly creative controls vs raw CFG | **P2** | Stylize → aesthetic guidance shaping; Variety → seed/CFG jitter across batch; Weird → conditioning perturbation (weakest — approximate or drop if poor). Never expose raw CFG. |
| **Style chips (stackable look modifiers)** distinct from recipe chips | Leonardo | fast aesthetic dial | **P2** | Named bundles of +/- prompt fragments applied at submit; stack on any recipe. |
| **Color palette control** (preset + custom hex swatches, savable; **inspiration-palette shuffle** for ideation) | Ideogram + Recraft | non-textual styling | **P2** | Swatches into conditioning / color-grade; front-end picker. (Deterministic recolor pass lives in Edit canvas.) |

## Generation card (controls + post actions)

| Feature | From | Capability | Priority | Local note |
|---|---|---|---|---|
| **Per-reference ControlNet stacking ("Control Traits")** (each ref/trait: own type Pose/Edge/Depth + per-trait weight slider; style refs get separate Influence axis; auto-run the right preprocessor per uploaded ref) | Leonardo + Playground (Control Traits) | structural conditioning | **P1** | SwarmUI stacks ControlNet/IP-Adapter units w/ per-unit weight; auto-preprocess per trait; **cap unit count for 32GB VRAM**. |
| **LoRA blending mixer** (multi-select, per-LoRA sliders, combine-range hints) | Leonardo | LoRA/style blend | **P1** | Native multi-LoRA weights; UI reads local LoRA metadata/thumbs. Replaces `<lora:x:0.7>` fiddling. |
| **Training-free character reference** (1 image → auto face/hair ID map, editable preserve-mask) | Ideogram | recurring-character consistency | **P1** | InstantID/PuLID/IP-Adapter-FaceID + local face/hair parse; no training. |
| **Per-card downstream action menu** (Face Fix, Hi-res Fix, tiered Upscale 1.5–3x) | Civitai | refine-from-result | **P1** | Each action = preset img2img/upscale taking card image + sidecar params; chains finished image as input. |
| **Image Set / series object** (N cells share one locked style + palette + AR + detail level, but **each cell has its own prompt**; reroll **one cell** without touching the rest; also applies to **audio sets** — foley/music variants) | Recraft (Image Set) | consistent-series iteration | **P1** | N independent SwarmUI jobs sharing one style/LoRA/palette token; each cell stores its own seed sidecar so "reroll this one" resubmits only that cell. The missing primitive for emote/icon/turnaround/foley series. Composes with async cards. |
| **CSV/spreadsheet batch generation** (≤500 rows, per-row params, queued overnight) | Ideogram | batch/queue | **P2** | Parse server-side, enqueue respecting llm/media groups, stream to existing cards. |
| **Parallel multi-model compare** (one input → N models → side-by-side grid) | Krea | empirical model selection | **P2** | Queue fan-out across checkpoints (sequential on single GPU), compare grid. |
| **Output verification (opt-in VLM prompt-adherence triage)** (after a job, llm-group VLM checks output vs prompt — key nouns present, OCR requested text — badges card pass/fail + reason; optional auto-reroll) | Playground (recursive verification) | iteration/QA, batch triage | **P2** | Per-job toggle; run with llm group after media releases GPU (respects exclusivity). Batch the check at end-of-run to amortize the swap cost. |
| **Fix Seed across a series** (sequence-level consistency toggle) | Runway | consistency control | **P3** | Sidecar resolves seeds; lock-for-run toggle is trivial. |
| **Named conditioning sliders** (label raw weights as "Likeness"/"Motion"/"Strength") | Runway | accessibility | **P3** | UI relabel of existing numeric params. |
| **Seamless tile/texture toggle** | Ideogram | specialized output mode | **P3** | ComfyUI circular/tiling padding → single checkbox. |
| **Denoise zone labels + sane defaults** (subtle/balanced/reinvent on img2img) | Civitai | sane defaults | **P3** | Default value + labeled zones; no provenance floor. |

## Edit canvas

| Feature | From | Capability | Priority | Local note |
|---|---|---|---|---|
| **Smart Layers — click-to-decompose a flat image into editable object/text layers** (promptable segmentation auto-masks every element on click; move/scale/rotate; delete-and-inpaint-fill; double-click **text to rewrite in place**) | Playground (Smart Layers) | precision post-gen editing | **P1** | Strict superset of paint-your-own-mask inpaint: a SAM/CLIPSeg/YOLO-class segmenter turns the *already-planned* inpaint engine into surgical no-reroll editing. (Same segmenter also powers bg-removal + motion auto-segment.) Text path = OCR-detect → inpaint glyphs → re-typeset (the "Extract Text" flow). |
| **Stage multiple (heterogeneous) edits → apply in ONE generation** (queue: remove this, swap that text, recolor here, move that — then one job; clean before/after) | Krea + Playground | multi-output efficiency | **P1** | Generalizes multi-region annotation to *mixed* edit types. Homogeneous masks ride one regional pass; heterogeneous ops ride a minimal sequenced pass behind one click. One card = one batched edit set — cheaper per VRAM setup. |
| **Multi-region annotation edit** (N labeled boxes, per-region prompt, one batched pass; badge sidebar) | Krea | regional inpaint batched | **P1** | SwarmUI regional/segmented prompting; collapses N round-trips into one. (Subsumed by "Stage multiple edits" once that ships.) |
| **Mixed editing mode — inpaint + outpaint + sketch on one surface with a single strength dial** (mask to replace, drag canvas edge to extend, or draw a rough sketch to guide; one "edit strength" = denoise) | Playground (Mixed) + Leonardo | low-friction unified editing | **P1** | Inpaint/outpaint share the masked-diffusion path; sketch = scribble/canny ControlNet on the region; one strength dial = denoise. Unifies the previously-separate inpaint/outpaint tools and adds sketch-to-image input on the canvas DokiDex already has. |
| **Retexture / Restyle — structure-locked whole-image restyle** (keep geometry/composition, apply a brand-new style/prompt; the third primitive between re-roll and region-inpaint) | Midjourney | variation flow | **P1** | Extract a structure map (canny/depth/lineart) from source, regenerate with new prompt/style at moderate denoise conditioned on that map. Needs a net-new "creativity" knob on the canvas. |
| **Inpaint strength slider + erase-before-inpaint + 60/40 outpaint guidance** | Leonardo | inpaint/outpaint quality | **P1** | Denoise on masked img2img native; erase + overlap tiling are canvas behaviors. (Folds into Mixed mode.) |
| **Drag-the-border outpaint + on-canvas aspect-ratio change** (grab frame edge to extend in any direction; change AR directly on the canvas; combinable with brush in fewer submits) | Midjourney | direct-manipulation reframing | **P1** | Outpaint/resize are SwarmUI ops; the work is front-end frame handles that compute new bounds+mask. Start one-op-per-submit; batch later via "Stage multiple edits". |
| **Rich mask toolkit** (invert + load-previous-mask + rect/freeform/brush-size/eraser; **scroll/right-drag brush size, Shift to flip erase↔restore, mask-area→creativity mapping**) | Ideogram + Midjourney (vary-region ergonomics) | mask UX speed | **P2** | Client-side canvas + store last mask bitmap; larger mask → higher inpaint denoise/creativity (sets correct expectation). |
| **Color recolor — deterministic LAB palette remap** (Swatches = one color at a time; Spectrum = shift groups of similar hues; non-destructive) | Recraft | reliable post-gen color identity | **P2** | Nearest-palette-color mapping in LAB over the output — **needs no model**, fully deterministic (the high-value local part vs flaky gen-time palette adherence). |
| **Per-region reference image drop** (scope an image-ref to one region) | Krea | visual region control | **P2** | IP-Adapter conditioning within a mask region. |
| **Resizable generation window = pixel-density control** (tight context window → sharper region) | Ideogram | inpaint sharpness | **P2** | Crop window, generate at window res, composite back. |
| **Stacked non-destructive edits + per-session history strip** | Krea | safe iterative editing | **P2** | Client-side stack of intermediate versions + op; editor-scoped undo, distinct from Library lineage. |
| **Reference preserve/release mask** (same mask scopes how much of a ref is honored) | Ideogram | reference granularity | **P2** | Mask multiplied into adapter conditioning region. |
| **Replace/Remove background** (subject-aware cutout → transparent PNG, or prompt-replace w/ relight) | Ideogram | bg workflow + alpha | **P2** | RMBG/SAM segment (shared with Smart Layers) + mask-conditioned gen; PNG alpha export. |
| **Drag-to-move object + auto gap-fill** | Krea | direct spatial edit | **P3** | Auto-mask object (Smart Layers segmenter), composite at target, inpaint vacated region. |
| **Upscale: dual Resemblance/Detail (or Creativity/Texture) sliders + face-aware block + split-view compare + guiding prompt** | Ideogram + Krea + Leonardo | creative upscale controls | **P1** | Map to denoise/CFG + separate face-restore pass over SUPIR/Ultimate SD Upscale; split-view = front-end wipe. |
| **Named enhancer/upscale presets** (Default/Flat-Sharp/Strong/Reinterpretation) | Krea | upscale presets | **P3** | Saved param bundles; extends recipe-chip pattern to enhance stage. |

## Live / real-time surface

| Feature | From | Capability | Priority | Local note |
|---|---|---|---|---|
| **Realtime Gen scratchpad** (continuous as-you-type re-render on turbo/low-step; promote a frame to a full job) | Leonardo + Krea | front-of-session iteration | **P1** | Turbo checkpoint @1–4 steps, debounce keystrokes, stream over SignalR; runs in media slot. |
| **Sketch-to-image canvas w/ single AI-strength dial** (draw → live render; structure-faithful↔prompt-creative) | Leonardo + Krea | spatial/compositional input | **P2** | Scribble ControlNet + turbo; raster canvas as control image, debounce strokes. (Shares the Mixed-mode sketch path.) |
| **Screen-region mirroring as live input** (capture Blender/Krita/Figma region → live restyle) | Krea | local-first input source | **P3** | OS screen capture → img2img/ControlNet; uniquely local. |
| **Webcam as live restyle input** | Krea | input source | **P3** | Local webcam → debounced img2img loop. |

## Video controls

| Feature | From | Capability | Priority | Local note |
|---|---|---|---|---|
| **Start + optional End keyframe conditioning + Loop** (set start, optional end; model interpolates between; Loop = start==end; AR follows start frame) | SeaArt + Krea + Leonardo + Runway + Midjourney + Pika/Kling | shot direction | **P1** | Wan **FLF2V** first/last-frame workflow; **gate the end-slot to the FLF2V variant** (disable when model lacks it). Highest-leverage single video control. |
| **Per-axis signed camera sliders + structured camera presets** (H/V/Zoom/Pan/Tilt/Roll + combined, −10…+10; named moves + "master shot" macros crane/orbit/bullet-time; intensity) | SeaArt + Runway + Leonardo + Kling (camera controls) | deterministic cinematography | **P1** | **Compiles to prompt tokens** (and/or motion conditioning) — zero new model. Where a motion node exists, also feed conditioning; else prompt-only. Attacks the worst part of local video. |
| **Frame-interpolation / Smooth-Video / Super-Slow-Mo** (target fps, slow-mo multiplier, fix-dup-frames) | Krea + Leonardo + Runway | video post-processing | **P1** | RIFE/FILM as workflow node; one-click post action on any video card. |
| **Keyframe storyboard strip** (2–5 ordered images as keys; each *segment* gets its own prompt + duration; drag/paste/pick-from-library to populate) | Pika (Pikaframes) + Kling | multi-shot composition, predictable iteration | **P2** | N chained FLF2V i2v segments stitched via ffmpeg; last generated frame feeds next start for continuity. **Gated on FLF2V**; per-segment bounded by Wan ~5s. Each segment is its own reproducible sidecar. |
| **Extend/continue clip from final frame** (append ~4–5s from the last frame; **steering prompt optional** — blank = continue momentum, set = redirect; repeatable, **cap depth** for drift) | Krea + Midjourney + Pika/Kling | longer sequences | **P2** | Last frame → start via native i2v, concat via ffmpeg. Implement once (Pika "extend-with-optional-prompt" and MJ "segment extend" are the same op). |
| **Motion-intensity dial (Low/High) + seamless-loop toggle + look presets** | Krea + Midjourney | motion/loop control | **P2** | Existing video-model params; binary Low/High motion prevents the "everything jitters" failure mode; presets = saved bundles. |
| **Reusable Ingredients / Elements library (single-subject v1)** (named reference images — character/outfit/object/setting — dropped into generations as slots to lock identity across clips) | Pika (Scene Ingredients) + Kling (Multi-Image Ref) | subject consistency, asset reuse | **P2** | Slots + JSON sidecars (aligns with Library); identity via IP-Adapter/FaceID. **Multi-subject compositing is unreliable — ship single-subject first.** |
| **Motion Brush** (per-region masks, drawn directional trajectory per region, ~4-axis vectors, ~6 brushes, **static/anchor brush** to pin still regions; optional auto-segment snap) | Runway + Kling (motion brush) | per-region motion | **P3** | Reuses inpaint/Smart-Layers canvas for masks + arrows; static brush is a cheap stabilizer. Conditioning gated to a motion-control model/ControlNet; degrade gracefully to prompt phrasing ("subject moves left") when absent. |
| **One-pass sound-on clip orchestration** (video → auto foley + music keyed to same prompt/duration) | SeaArt | multi-output orchestration | **P2** | Chain video→foley→music sequentially (respects GPU group exclusion); skip in-model lip-sync. |
| **Identity-lock toggle for video** (face/ID-ref pass across motion) | SeaArt | character consistency | **P3** | IP-Adapter-FaceID/PuLID into video workflow; feasible only if node exists. |
| **Reference-video motion transfer + local Motion Gallery** | SeaArt | reusable motion presets | **P3** | Gallery/upload UI is trivial-local; transfer needs pose-driven video node in catalog. |

## Model + Workflow manager

| Feature | From | Capability | Priority | Local note |
|---|---|---|---|---|
| **Workflow→form publishing** (tag which node fields are user-editable; render minimal panel) | Tensor.art + Krea + Runway | workflow-as-app UX | **P1** | Metadata layer (exposed inputs + types/labels/defaults) + form renderer over Comfy/SwarmUI API graph. Drop only the public-link sharing. |
| **Custom Style authoring + Test-it live preview** (build a reusable style from 1–5 reference images each with a weight; embed a default style-prompt so daily prompts stay short; **two interpretation modes** essentials [color/texture] vs composition [+layout/structure]; scratch "Generate test image" while tuning, then Save) | Recraft (Custom Style) + Midjourney (Moodboards) | user-authored style library, no training | **P1** | A style = saved bundle {1–5 refs + per-ref IP-Adapter weights + appended prompt fragment + mode flag}. Essentials = IP-Adapter only; composition = IP-Adapter + structural ControlNet. **One bundle subsystem** shared with the @-reference shelf. Lightweight tier needs no training. |
| **Moodboard → train a personal style (heavier tier) + switchable style profiles** (define an aesthetic from a folder of images; multiple personal-style profiles, set default or switch per generation) | Midjourney (Moodboards) | personalization, producer-side | **P2** | Lightweight tier = the multi-image style bundle above (no train). Heavier tier = kick a local LoRA/textual-inversion job from the image set (fits the training feature below). Switchable profiles = pick the active bundle. |
| **Style discovery: random-style shuffle + lock-style-across-prompts** (a "random style" button injects a fresh saved style/LoRA each refresh for serendipity; a lock toggle persists the chosen style across subsequent prompts) | Midjourney (Style Explorer) | ideation, consistency | **P2** | Random = render the user's prompt under a randomly-chosen saved style token (cheap via Draft mode); lock = persist that token. (Preference-driven convergence is covered by Exploration Mode below.) |
| **In-app LoRA/style training** (N images → Medium/Large tier, trigger word) | Krea | producer-side capability | **P2** | kohya/sd-scripts on 32GB in media slot; tier = steps/rank/resolution preset. Backs the "train from moodboard" tier. |
| **Node-lite flow canvas** (typed color-coded ports, design-time validation, run-all, fan-out) | Krea + Runway | multi-step pipeline authoring | **P2** | Orchestration over the capability catalog; start linear ("recipes") before full graph. |
| **Task-scoped "Apps"** (verb-first one-input tools: Remove…, Restyle…, Extend…) | Runway | progressive disclosure | **P3** | Front-end of workflow→form; saved workflow + minimal form. |

## Library / organization

| Feature | From | Capability | Priority | Local note |
|---|---|---|---|---|
| **Saved searches + typed filters + timeline scrubber + bulk actions + generate-into-folder** (saved search = stored query that auto-files past AND future generations; filter by model/version/AR/output-type; date timeline bar; multi-select favorite/trash/move; direct new generations into a chosen folder) | Midjourney | self-maintaining organization | **P1** | Query over the existing sidecar index (prompt text, model, AR, type, date, seed); saved search = re-evaluated stored query; timeline = sort by mtime; bulk = local file moves/flags. DokiDex is well-positioned — the sidecars are already the index. |
| **Image-ranking keyboard triage** (number keys to keep/skip/favorite across a grid; wired to favorite/trash/remix/upscale) | Midjourney | fast curation, keyboard flow | **P2** | Pure front-end over existing favorite/trash/sidecar-rating actions; review a finished batch keyboard-only. Free win for a heavy single user. |
| **Sessions / spatial variation-tree board** (generations land by parent_id as a pannable tree) | Runway + Ideogram | iteration legibility | **P2** | Client-side canvas over sidecar records; parent_id links form tree. Distinct from flat Library. |
| **Batch edit: one instruction across many items** (select N → one edit → queued) | Runway | multi-output editing | **P2** | Queue same edit recipe over selected items sequentially on the one GPU. |
| **Auto AR-match from reference + prefill-edit-with-source-model** | Leonardo | sensible defaults / anti-drift | **P2** | AR from source file; source model id in sidecar → one-click edit-with-original prevents style drift. |
| **Pin/star tools to quick-access bar** | Runway | navigation QoL | **P3** | localStorage preference. |
| **Full keyboard-first canvas map** (single-key tools B/X/V/C/R/T/I/G/S/F, space-pan, scroll-zoom; **fit-to-selection, fit-to-project, 100% zoom, hide-all-UI `\`**, brush size `[`/`]`, Enter=generate) | Krea + Recraft + Midjourney | keyboard flow | **P3** | Pure keybindings + viewport math; one consolidated keyboard map across cards, Edit canvas, and Library. Extends shipped Ctrl+Enter. |

## New cross-cutting surface

| Feature | From | Capability | Priority | Local note |
|---|---|---|---|---|
| **Layout-first bounding-box composer** (drag boxes on a 0–1000 grid; per-box literal text + hex colors; auto-arrange helper for text → regional/segment prompt; ControlNet-style placement hint) | Ideogram + Recraft (controllable layout) | deterministic composition + in-image text | **P1** | React canvas overlay → SwarmUI `region()` binds each box's prompt to its area; Ideogram 4.0 nf4 fits 32GB. **In-image text needs a text-strong model.** The single most differentiating local-studio feature. |
| **Exploration Mode (diverge → pick → graded refine)** (one fuzzy prompt → ~8 distinct visual *directions*; pick one; refine via a 5-step "similarity" ladder from "a little" to "extremely" similar) | Recraft (Exploration Mode) | cold-start ideation | **P1** | Diverge = one batch, varied seeds + mild prompt/style jitter; the 5 similarity levels = a fixed img2img-denoise ladder (~0.2/0.35/0.5/0.65/0.8) and/or IP-Adapter weight against the chosen image. Fully local; solves the "I don't know what I want yet" problem that remix/vary doesn't. |
| **Prompt-free Remix (visual-similarity re-roll)** (select an image, generate nearby alternates with **no text prompt**; a single similarity slider controls drift; doubles as aspect-ratio retarget) | Recraft (Remix) | lowest-effort iteration | **P2** | Make the planned **Vary** action prompt-free: img2img/IP-Adapter from source, similarity = denoise/IP weight; AR retarget = same conditioning into a new ratio (or outpaint). Removing the prompt removes the main source of drift. |
| **One-click effect-preset gallery** (visual thumbnail menu of pre-tuned transforms — squish/inflate/melt/explode, plus video macros bullet-time/whip-pan; subject auto-detect; click = polished result, no prompt-craft) | Pika (Pikaffects) | discoverable guided exploration | **P2** | Each effect = a stored prompt + param (+ optional workflow) bundle — the recipe-bundle mechanism already planned, packaged as a thumbnail gallery; user-extensible JSON. Subject auto-detect reuses the shared segmenter. (Presentation/scope extension of recipe chips, not a new engine.) |

---

## DROPPED (with reason)

- **Video region edit / object-swap (video inpaint, Pika Modify Region / Pikaswaps)** — no native Wan VACE class in the catalog; would require a base64-source custom workflow (the known I2V dead-end). Re-qualifies on a native VACE class. Local substitute today: extract a frame → image-inpaint → re-run i2v.
- **Lip-sync as a post step (Kling/Pika)** — no cataloged lip-sync/S2V model (Wan2.2-S2V / Wav2Lip uncataloged); needs a base64-ref custom workflow. Re-qualifies only on a native S2V class. Ship silent video + auto foley/music instead. (Also dropped in round 2 from SeaArt.)
- **Non-destructive timeline + layer compositor for clips (Pika 2.5 Studio)** — largest scope of any mined item, overlaps the Sessions board, and clutters a single-clip studio. Defer; if ever built, start minimal as ordered trim+concat of Library clips.
- **Mockup compositing + AI Blend relight (Recraft)** — the value-add "AI Blend" relight model is uncataloged, and CMYK/print export is off-purpose for this studio. Plain blend/tile/mask is generic image-editing, not worth a dedicated surface. Defer until a relight model is cataloged.
- **Shareable/portable style codes (Midjourney `--sref` codes as social tokens)** — the *shareable* aspect is social/cloud; kept only the local analog (weightable/blendable saved style bundles with a local hash id, folded into the @-reference shelf + Custom Style).
- **Expose workflow as public link / API-trigger app** (Krea/Tensor.art) — cloud/social sharing; kept only the local form-panel projection.
- **Krea Explore curated/popular aesthetics feed** — cloud-sourced gallery; the only adoptable remnant (a local seeded built-in style strip) folds into saved styles / random-style shuffle.
- **In-model lip-synced dialogue / talking-head** (SeaArt) — same blocker as Kling/Pika lip-sync above.
- **Virtual-light relight rig, virtual-camera/lens sim, colorize-with-hints** (Krea) — each needs a relight/lens/colorize edit model not in the catalog; rig UIs are local but produce nothing without the model. Re-qualifies as P3 once cataloged.
- **Act-Two expressiveness (literal)** (Runway) — needs an audio-driven performance model; kept only the generalized "named strength slider" pattern.
- **Saved Styles "Recents/Explore" browsing as a feature** (Ideogram) — overlaps recipe chips + Model manager; the new bit (image-reference style presets) collapses into the @-reference shelf + Custom Style.
- **Infinite spatial board with z-order as primary Library** (Ideogram) — overlaps Runway Sessions (kept that); a free z-order collage is redundant for a single-user studio.

---

## TOP 5 HIGHEST-LEVERAGE ADDITIONS (across all 8 platforms)

1. **Draft↔Final toggle + one-click Enhance-from-card** (Midjourney + Civitai) — a deliberate two-speed economy of the single 32GB GPU: explore cheaply at ~10x on the shipped Turbo path, then spend full compute only to Enhance keepers (re-POST the stored seed+prompt at Base). It compounds with *every* exploratory feature below — each becomes nearly free. No new model.
2. **Layout-first bounding-box composer** (Ideogram + Recraft) — turns composition + in-image text from a dice-roll into deterministic direct manipulation; the one capability that most differentiates a serious local studio. Rides SwarmUI regional/`region()` prompting.
3. **Smart Layers — click-to-decompose into editable object/text layers** (Playground) — a strict superset of the planned paint-your-own-mask canvas: a local SAM/CLIPSeg-class segmenter turns the *already-planned* inpaint engine into surgical, no-reroll editing (and the same segmenter powers bg-removal and motion auto-segment). Highest-leverage upgrade to an existing plan.
4. **Exploration Mode (diverge → 5-step similarity ladder)** (Recraft) — fixes the cold-start "I don't know what I want yet" problem that remix/vary never addressed; a thin preset layer over varied seeds + a fixed img2img-denoise ladder, fully local.
5. **Start + optional End keyframe conditioning + per-axis camera controls** (SeaArt/Krea/Midjourney/Kling) — directly fixes the unsteerable-motion problem that is the worst part of local video; camera controls compile to prompt tokens (zero new model), end-frame rides the Wan FLF2V variant (gated where unsupported).
