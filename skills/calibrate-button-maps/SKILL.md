---
name: calibrate-button-maps
description: Measure, audit, repair, and verify rectangular label or touch regions over raster controller maps. Use when creating a custom joystick, throttle, button-box, touchscreen, or other hardware map; when a button label is missing, shifted, or drawn in the wrong cell; or when adding completeness and rendering tests so future controls cannot silently fall back or become misaligned.
---

# Calibrate Button Maps

Build the region catalog from the template's native pixels, then prove the catalog and rendered result agree. Preserve the source image and write previews separately.

## Workflow

1. Locate the exact raster asset used at runtime and record its native width and height. Do not measure a scaled UI screenshot.
2. Identify the complete set of buttons printed on the template. Keep custom logical controls that have no printed cell outside this set and document their fallback behavior.
3. Inspect the original image at native resolution. Crop tightly around each table or irregular cell before measuring lines.
4. Use `scripts/region_tool.py scan` to find likely horizontal and vertical border runs. Treat its output as measurement evidence; photographs and leader lines can produce extra candidates.
5. Define each region as `[x, y, width, height]`, with right and bottom coordinates exclusive. Choose one consistent border inset convention. Measure irregular rows individually instead of forcing a uniform row height.
6. Save the expected button set and regions in a JSON manifest:

   ```json
   {
     "image_size": [1180, 748],
     "expected_buttons": [1, 2, 3],
     "regions": {
       "1": [120, 80, 210, 24],
       "2": [120, 104, 210, 24],
       "3": [500, 220, 180, 30]
     }
   }
   ```

7. Validate the manifest, render a numbered preview, and inspect that preview at native resolution before changing application code.
8. Port the reviewed regions into the application. Keep the manifest when the project benefits from a data-driven source of truth; otherwise preserve representative exact coordinates in tests.
9. Add tests for:
   - the exact native template size;
   - every printed button appearing exactly once;
   - positive, unique, in-bounds rectangles;
   - exact coordinates for repaired or irregular cells;
   - isolated render checks for adjacent cells, so one overlay cannot make its neighbor's test pass;
   - intentional fallback behavior for logical controls without printed cells.

## Utility

The script requires Python and Pillow. If Pillow is unavailable, report that dependency and use an existing project image library when practical.

```powershell
python scripts/region_tool.py scan controller.png --crop 850,495,1145,568
python scripts/region_tool.py validate controller.png regions.json
python scripts/region_tool.py preview controller.png regions.json region-preview.png
python -m unittest discover tests
```

Adjust `--threshold`, `--min-row-fraction`, and `--min-column-fraction` when the grid is gray, antialiased, or broken by artwork. Repeat scans with smaller crops before lowering thresholds enough to admit unrelated image features.

The manifest's `image_size` is required. `image_sha256` is optional and pins the regions to one exact revision of the source image. Preview refuses to replace either input or an existing output; pass `--force` only when intentionally replacing a previously generated preview.

## Acceptance

Finish only after the manifest validates, every expected control is visible in the preview, repaired cells remain inside their printed boundaries, automated tests pass, and the runtime view has been checked when hardware or a live application is available.
