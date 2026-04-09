# Multi-Night Stacking — Testing Guide

## Prerequisites

1. Custom fork deployed and NINA restarted
2. Confirm you see `**CUSTOM FORK**` in the Live Stack plugin description (Options > Plugins)
3. A camera connected (real or NINA simulator)

## Setup

In **Options > Live Stack** (plugin settings page):

1. **Working directory**: Set to a dedicated folder (e.g., `C:\LiveStackData\custom`)
   - Use a DIFFERENT directory from stock Live Stack if you plan to swap back
2. **Autosave stacked Lights**: **ON** (required — this writes the FITS files that resume depends on)
3. **Enable multi-night mode**: **ON**
4. **Plate solve threshold**: 5.0 arcmin (default)
5. **Alignment residual threshold**: 3.0 px (default)
6. **Plate solve retry frames**: 3 (default)

---

## Test 1: Basic Stacking + Sidecar Creation (single session)

**Goal**: Verify stacking works normally and sidecar files are created.

1. Set up a sequence with a named target (e.g., "M101") and a filter
2. Start Live Stack (button or sequence instruction)
3. Capture 5-10 frames
4. Stop Live Stack

**Verify**:
- [ ] Stack tab shows correct frame count
- [ ] `{WorkingDirectory}/stacks/M101-{Filter}.fits` exists
- [ ] `{WorkingDirectory}/stacks/M101-{Filter}.json` exists (the sidecar)
- [ ] Open the JSON — should contain:
  - `totalFrames` matching the FITS frame count
  - `totalExposureSeconds` > 0
  - `sessions` array with one entry for today's date
  - `dimensions` with your camera's resolution and binning
  - `referenceStars` array (should have entries)
  - If you plate-solve every frame: `referenceWcs` with RA/Dec/rotation

**Log lines to look for** (NINA log, search for `[MultiNight]`):
```
[MultiNight] Stored reference WCS: RA=... Dec=... Rot=...
```

---

## Test 2: Resume from Previous Session (the core feature)

**Goal**: Verify that stopping and restarting resumes the stack.

1. After Test 1 completes, note the frame count (e.g., 10 frames)
2. Start Live Stack again
3. Capture 3-5 more frames with the SAME target name and filter

**Verify**:
- [ ] On first new frame, notification shows: `[MultiNight] Resumed M101-{Filter}: 10 frames`
- [ ] Tab frame count starts at 10 (or whatever you had) and increments from there
- [ ] Stack image shows the accumulated result, not just the new frames
- [ ] Sidecar JSON now has TWO entries in the `sessions` array
- [ ] `totalFrames` matches the new cumulative count

**Log lines to look for**:
```
[MultiNight] Found existing stack at ..., attempting resume
[MultiNight] Loaded N reference stars from sidecar
[MultiNight] Resuming M101-H: 10 frames, X.Xh total (last session: 2026-04-08)
[MultiNight] Layer 1 passed: pointing offset X.X arcmin (threshold 5.0)
```

---

## Test 3: Layer 1 — Plate Solve Validation

**Only applicable if you plate-solve every frame (center-after-drift, etc.)**

**Goal**: Verify that the plate solve check works on resume.

1. After Test 2, check the sidecar JSON for `referenceWcs` values
2. Start Live Stack and capture a frame on the same target

**Verify**:
- [ ] Log shows `Layer 1 passed: pointing offset X.X arcmin`
- [ ] The offset should be small (< 1 arcmin if pointing is consistent)

**To test Layer 1 rejection** (optional, harder to trigger naturally):
- You'd need to resume a stack and then point at a completely different part of the sky
  with the same target name — this would trigger the threshold

---

## Test 4: Layer 2 — Alignment Residual Check

**Goal**: Verify that badly aligned frames are rejected.

1. Temporarily set **Alignment residual threshold** to a very low value like **0.5 px**
2. Start Live Stack and capture a few frames

**Verify**:
- [ ] Some frames may be rejected with: `Frame rejected: affine residual X.Xpx exceeds threshold 0.5px`
- [ ] Rejected frames show a notification in NINA
- [ ] Sidecar JSON records rejections in the session's `rejectionReasons`

3. **Set the threshold back to 3.0 px** when done

---

## Test 5: Reset Stack (Archive)

**Goal**: Verify the reset button archives and starts fresh.

1. Have an existing stack with some frames (from previous tests)
2. In the Live Stack tab, click the **reset button** (loop/refresh icon, next to the save button)

**Verify**:
- [ ] Tab is removed from the UI
- [ ] In the stacks folder: original `M101-{Filter}.fits` is gone
- [ ] New file exists: `M101-{Filter}-archived-2026-04-08.fits`
- [ ] Archived sidecar JSON alongside it
- [ ] Next frame for the same target creates a fresh stack from scratch

**Log lines to look for**:
```
[MultiNight] Stack reset: archived to M101-H-archived-2026-04-08.fits
```

---

## Test 6: Dimension Mismatch

**Goal**: Verify that changing resolution/binning triggers a fresh start.

1. Have an existing stack from previous tests (e.g., at bin 1)
2. Change camera binning to 2x2 (or change ROI)
3. Start Live Stack and capture a frame

**Verify**:
- [ ] Log shows dimension mismatch warning
- [ ] Old stack is archived automatically
- [ ] Fresh stack starts with the new dimensions
- [ ] Sidecar reflects new dimensions

---

## Test 7: Multi-Night Mode OFF (backwards compatibility)

**Goal**: Verify that disabling multi-night mode reverts to stock behavior.

1. Set **Enable multi-night mode**: **OFF**
2. Start Live Stack and capture frames

**Verify**:
- [ ] No resume attempts — always starts fresh
- [ ] No sidecar JSON files created
- [ ] FITS files overwritten each session (stock behavior)
- [ ] No `[MultiNight]` log lines

---

## What to Watch For (Potential Issues)

- **Memory usage**: Loading a large FITS stack into memory on resume. Monitor NINA's
  memory footprint — a 3552x3552 float32 stack is ~48MB, should be fine.
- **First-frame latency on resume**: Loading the FITS file adds a few seconds to the
  first frame processing. Subsequent frames should be normal speed.
- **Sidecar corruption**: If NINA crashes mid-write, the atomic temp+rename should
  protect against this. The FITS IMGCOUNT header is the authoritative source.
- **Reference star quality**: If the loaded reference stars from the sidecar produce
  poor alignment, try resetting the stack and letting it re-detect from the first
  new frame.

---

## Quick Checklist for First Real Imaging Night

- [ ] Multi-night mode ON
- [ ] Autosave stacked Lights ON
- [ ] Working directory set and has disk space
- [ ] Target names in sequence match exactly what you'll use next session
- [ ] After first session: verify stacks/ folder has FITS + JSON files
- [ ] Check JSON contents look sane
