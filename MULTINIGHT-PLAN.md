# Multi-Night Stacking — Implementation Plan

All decisions in this document are final and ready for implementation.
No further design discussion needed.

---

## 1. Fork Setup

- **GUID**: Keep original (`10bc1716-54af-425e-b307-c0ca1ce10600`) — same as stock,
  NINA treats them as the same plugin; can swap back to stock via plugin manager reinstall
- **AssemblyDescription**: Prefix with `**CUSTOM FORK**`
- **Working directory**: User points fork at a separate directory from stock Live Stack
  to prevent stock (if ever reinstalled temporarily) from overwriting multi-night FITS files
- **Deploy script**: Same pattern as TS fork — closes NINA, removes stock Live Stack DLL,
  builds, deploys

---

## 2. New Setting: Multi-Night Mode

Add to plugin options (`Options.xaml` / settings):

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `MultiNightMode` | bool | false | Enable multi-night stack resumption |
| `PlatesolveThresholdArcmin` | double | 5.0 | Max pointing offset to allow resume (arcmin) |
| `AffineResidualThresholdPixels` | double | 3.0 | Max per-frame alignment RMS error (pixels) |
| `PlatesolveRetryFrames` | int | 3 | Frames to attempt plate solve before giving up |

Multi-night mode is **set once and stays on** — no per-session prompting.

---

## 3. Sidecar JSON Schema

One `.json` file lives alongside each FITS stack file:
- Monochrome: `{WorkingDirectory}/stacks/{Target}-{Filter}.json`
- OSC channels: `{WorkingDirectory}/stacks/{Target}-R_OSC.json` etc.

```json
{
  "target": "M101",
  "filter": "H",
  "totalFrames": 47,
  "totalExposureSeconds": 28200,
  "sessions": [
    {
      "date": "2026-04-01",
      "frames": 22,
      "exposureSeconds": 13200,
      "rejectedFrames": 1,
      "rejectionReasons": ["layer2_affine_residual"]
    },
    {
      "date": "2026-04-07",
      "frames": 25,
      "exposureSeconds": 15000,
      "rejectedFrames": 0,
      "rejectionReasons": []
    }
  ],
  "referenceWcs": {
    "ra": 210.8023,
    "dec": 54.3490,
    "rotation": 15.3,
    "pixelScale": 0.65,
    "solvedAt": "2026-04-01T03:12:00Z"
  },
  "referenceStars": [
    { "x": 412.3, "y": 198.7 },
    { "x": 891.1, "y": 445.2 }
  ],
  "dimensions": {
    "width": 3552,
    "height": 3552,
    "binning": 1
  },
  "createdUtc": "2026-04-01T03:10:00Z",
  "lastUpdatedUtc": "2026-04-07T04:15:00Z"
}
```

**Key points:**
- `referenceStars` persists the star list used for alignment — loaded on resume instead of
  re-detecting from the stack (faster and deterministic)
- `referenceWcs` stores the plate solve result used for Layer 1 validation
- `sessions` array tracks per-night contribution for Night Summary stats
- Rejected frames are logged with reason for transparency; never silently discarded
- `dimensions` used for compatibility check on resume

**Sidecar trust rule**: `IMGCOUNT` from FITS header is authoritative for frame count.
If sidecar `totalFrames` disagrees with FITS `IMGCOUNT`, log a warning and trust the
FITS header. Update sidecar to match.

---

## 4. Resume Logic

### 4a. On session start for a (Target, Filter) pair

```
IF multi-night mode OFF:
    → proceed as stock (fresh stack, overwrite FITS)

IF multi-night mode ON:
    check if FITS file exists at expected path

    IF no FITS file:
        → fresh start (normal stock behavior)
        → create sidecar on first frame

    IF FITS file exists:
        → load FITS pixels into bag.Stack
        → read IMGCOUNT from FITS header → bag.ImageCount
        → load sidecar if exists (create skeleton if missing)
        → check dimensions match sidecar (width, height, binning)
            IF mismatch: abort resume, warn user with specifics, fresh start
        → log: "Resuming {Target}-{Filter}: {n} frames from {lastUpdatedUtc}"
        → proceed to Layer 1 validation
```

### 4b. Dimension/compatibility check on resume

Read from FITS header or sidecar. If current session dimensions differ:
- Log warning: "Stack dimensions mismatch: stored {W}x{H} bin{B}, current {W}x{H} bin{B} — starting fresh"
- Do NOT stack incompatible frames. Reset to fresh start.
- Archive existing FITS (rename to `{Target}-{Filter}-archived-{date}.fits`)

Gain/offset changes: **warn only**, do not block. Log the discrepancy in the sidecar.

### 4c. Alignment reference on resume

After loading the FITS stack:
- If sidecar has `referenceStars`: load them directly as the alignment reference
- If no sidecar / no referenceStars: run star detection on the loaded FITS stack
  (higher SNR than individual frames → better detection than using a single frame)
- Store detected stars as alignment reference for this session

---

## 5. Layer 1 — Plate Solve Validation (per-session, not per-frame)

Runs once when resuming, on the first incoming frame.

### WCS source hierarchy (in order):

1. **WCS already in incoming frame's FITS header** (`CRVAL1`/`CRVAL2`/rotation) —
   free, zero cost, used when user already plate-solves every frame
2. **No WCS in header, solver configured** — trigger plate solve on first new frame
   via NINA's `IPlateSolverFactory` mediator (hint-assisted, ~2s typical)
3. **No solver configured** — skip Layer 1 entirely, log warning, proceed to Layer 2 only

### Validation:

```
Compute angular separation between incoming frame WCS and sidecar referenceWcs
IF separation > PlatesolveThresholdArcmin:
    abort resume
    log: "Pointing offset {X} arcmin exceeds threshold {Y} arcmin — starting fresh"
    warn user in UI
    offer fresh start
ELSE:
    store WCS in sidecar if not already present (first-ever solve for this stack)
    proceed with stacking
```

### Solve failure handling:

If plate solve fails (poor conditions, short exposure, partial cloud):
- Retry on next `PlatesolveRetryFrames` frames (default 3)
- If still no solve after retries: skip Layer 1 for this session, log warning, proceed
- Do NOT abort the resume due to a failed solve — fall back gracefully

---

## 6. Layer 2 — Per-Frame Affine Residual Check

Runs on every incoming frame (resumed and fresh sessions alike).

After the affine transformation is computed from star triangle matching:
- Calculate RMS residual of star position errors post-transform
- If RMS > `AffineResidualThresholdPixels`: **reject frame**
  - Log: "Frame rejected: affine residual {X}px exceeds threshold {Y}px"
  - Increment rejected frame counter in sidecar
  - Record rejection reason: `"layer2_affine_residual"`
  - Continue processing next frame — do not abort session

This extends the existing alignment code in `ImageTransformer.cs`. The transform
is already computed; the residual check is an additive step before `SequentialStack` is called.

---

## 7. Existing Quality Gates (unchanged from stock)

These continue to run as before, upstream of both Layer 1 and 2:
- Minimum star count (8 stars, existing)
- HFR threshold (existing per-filter setting)

Rejections from these gates are also logged to the sidecar with their reasons.

---

## 8. OSC / Color Composite Resume

OSC stacks produce three FITS files: `{Target}-R_OSC.fits`, `{Target}-G_OSC.fits`,
`{Target}-B_OSC.fits`.

**Resume rule**: all three must be present to resume. If any is missing:
- Log warning: "OSC resume aborted: {missing file} not found — starting fresh"
- Fresh start for all three channels (atomic)
- Do not resume two channels and start a third fresh — that produces a broken composite

Since all three channels are produced from the same input frame in the same processing
call, frame counts will always be in sync under normal operation. No special sync
checking needed.

---

## 9. Sidecar Update Cadence

The sidecar is updated:
- **On session start (resume)**: load existing, update `lastUpdatedUtc`, add new session entry
- **After each accepted frame**: increment `totalFrames`, `totalExposureSeconds`,
  update current session entry
- **After each rejected frame**: increment `rejectedFrames`, append rejection reason
- **On first plate solve**: write `referenceWcs`
- **On first frame of fresh stack**: write `dimensions`, `referenceStars`, `createdUtc`

Write is atomic: write to temp file → rename (same pattern as existing FITS writes).

---

## 10. Reset / Archive

Add a **"Reset Stack"** button per tab in the Live Stack UI.

Behavior:
- Rename existing FITS to `{Target}-{Filter}-archived-{YYYY-MM-DD}.fits`
- Delete sidecar JSON
- Reset in-memory tab to fresh state (ImageCount = 0, Stack = null)
- Log: "Stack reset: archived to {filename}"

**Never silently delete** — always archive. User can manually delete archives if desired.

---

## 11. Logging

All multi-night events use a consistent log prefix for easy grepping: `[MultiNight]`

Key log lines:
```
[MultiNight] Resuming M101-H: 47 frames, 7.8h total (last session: 2026-04-07)
[MultiNight] Layer 1 passed: pointing offset 1.2 arcmin (threshold 5.0)
[MultiNight] Layer 1 skipped: no plate solver configured
[MultiNight] Layer 1 failed: pointing offset 8.3 arcmin — starting fresh
[MultiNight] Layer 1 solve failed on frame 1, retrying (2 attempts remaining)
[MultiNight] Frame rejected: affine residual 4.1px exceeds threshold 3.0px
[MultiNight] Stack reset: archived to M101-H-archived-2026-04-07.fits
[MultiNight] Dimension mismatch on resume: stored 3552x3552 bin1, current 1776x1776 bin2
[MultiNight] OSC resume aborted: M101-G_OSC.fits not found — starting fresh
[MultiNight] Sidecar IMGCOUNT mismatch: FITS=47, sidecar=45 — trusting FITS header
```

---

## 12. Night Summary Integration

**No new report sections needed.** The existing thumbnail display already renders
whatever is in the FITS stack file — it automatically shows the accumulated multi-night
stack with no code changes.

**Only change needed**: stats displayed under each thumbnail.

Current behavior: frame count and integration time come from the in-memory session
broadcast (single-night counts).

New behavior:
```
IF sidecar JSON exists alongside FITS file:
    display totalFrames and totalExposureSeconds from sidecar
    (true multi-night accumulated totals)
ELSE:
    fall back to current broadcast-based counts
    (stock Live Stack compatibility, unchanged)
```

This is a small change in the NS report generation code, in the Live Stack section
of the session report. Fully backwards compatible — single-night users see no change.

All filter stacks for a target are shown in the report regardless of whether data
was added that night (existing behavior, unchanged).

---

## 13. Implementation Order

Work in this sequence — each phase is independently shippable:

### Phase 1 — Fork scaffold
- Fork setup: GUID comment, AssemblyDescription, deploy script
- Add settings: `MultiNightMode`, `PlatesolveThresholdArcmin`,
  `AffineResidualThresholdPixels`, `PlatesolveRetryFrames`
- Stub out sidecar read/write class (no-op initially)

### Phase 2 — Resume accumulator
- On session start: detect existing FITS, load pixels + IMGCOUNT
- Dimension compatibility check
- Reference star loading (from sidecar or re-detected from loaded stack)
- Log all resume events with `[MultiNight]` prefix
- **Shippable**: multi-night accumulation works, no alignment validation yet

### Phase 3 — Layer 2 affine residual check
- Extend `ImageTransformer.cs` / stacking path to compute post-transform RMS residual
- Reject frame + log if residual exceeds threshold
- Write rejections to sidecar

### Phase 4 — Layer 1 plate solve validation
- WCS source hierarchy: header → solve first frame → skip
- Angular separation check against stored `referenceWcs`
- Retry logic on solve failure
- Abort resume + offer fresh start on threshold exceeded
- **Shippable**: full two-layer validation in place

### Phase 5 — Reset/archive UI
- Per-tab "Reset Stack" button
- Archive rename behavior

### Phase 6 — Night Summary integration
- Sidecar stats in report (totalFrames, totalExposureSeconds)
- Fallback to broadcast counts when no sidecar

---

## 14. What This Is Not

To keep scope clear:

- **Not a science-grade integration tool.** These stacks are for visual progress
  tracking and Night Summary thumbnails. Users do their own rigorous integration
  in PixInsight/Siril/etc. from raw frames.
- **Not sigma clipping or multi-pass rejection.** Running mean is sufficient.
  Bad frames that slip through quality gates have negligible visual impact at scale.
- **Not a restack-from-scratch tool.** No frame library management, no re-processing
  from saved calibrated lights.
- **Not a multi-user or networked feature.** Single machine, single working directory.
