#!/usr/bin/env python3
"""Measure, validate, and preview rectangular regions on a raster button map."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import sys
from collections.abc import Iterable, Sequence
from pathlib import Path
from typing import Any

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError as error:  # pragma: no cover - exercised only without the dependency
    raise SystemExit("region_tool.py requires Pillow: python -m pip install Pillow") from error


Rect = tuple[float, float, float, float]
ImageSize = tuple[int, int]


def reject_json_constant(value: str) -> None:
    """Reject JSON extensions such as NaN and Infinity."""
    raise ValueError(f"manifest contains a non-finite number: {value}")


def reject_duplicate_object_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    """Build a JSON object while rejecting repeated source keys."""
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON object key: {key!r}")
        result[key] = value
    return result


def sha256_file(path: Path) -> str:
    """Return the lowercase SHA-256 digest for a file."""
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def flatten_onto_white(image: Image.Image) -> Image.Image:
    """Return an opaque RGBA copy with transparent pixels composited over white."""
    rgba = image.convert("RGBA")
    white_background = Image.new("RGBA", rgba.size, (255, 255, 255, 255))
    return Image.alpha_composite(white_background, rgba)


def parse_crop(value: str) -> tuple[int, int, int, int]:
    """Parse an exclusive x1,y1,x2,y2 crop specification."""
    try:
        parts = tuple(int(part.strip()) for part in value.split(","))
    except ValueError as error:
        raise argparse.ArgumentTypeError("crop must contain four integers: x1,y1,x2,y2") from error
    if len(parts) != 4:
        raise argparse.ArgumentTypeError("crop must contain four integers: x1,y1,x2,y2")
    x1, y1, x2, y2 = parts
    if x2 <= x1 or y2 <= y1:
        raise argparse.ArgumentTypeError("crop right and bottom must exceed left and top")
    return parts


def group_runs(candidates: Sequence[tuple[int, int]], span: int) -> list[dict[str, Any]]:
    """Group adjacent candidate coordinates and retain the darkest member of each run."""
    if not candidates:
        return []
    groups: list[list[tuple[int, int]]] = [[candidates[0]]]
    for coordinate, count in candidates[1:]:
        if coordinate == groups[-1][-1][0] + 1:
            groups[-1].append((coordinate, count))
        else:
            groups.append([(coordinate, count)])

    runs: list[dict[str, Any]] = []
    for group in groups:
        peak_coordinate, peak_count = max(group, key=lambda item: item[1])
        runs.append(
            {
                "start": group[0][0],
                "end": group[-1][0],
                "peak": peak_coordinate,
                "peak_dark_pixels": peak_count,
                "peak_fraction": round(peak_count / span, 4),
            }
        )
    return runs


def scan_lines(
    image_path: Path,
    crop: tuple[int, int, int, int] | None,
    threshold: int,
    min_row_fraction: float,
    min_column_fraction: float,
) -> dict[str, Any]:
    """Return likely long dark horizontal and vertical line runs within a crop."""
    with Image.open(image_path) as opened:
        gray = flatten_onto_white(opened).convert("L")
        width, height = gray.size
        bounds = crop or (0, 0, width, height)
        x1, y1, x2, y2 = bounds
        if x1 < 0 or y1 < 0 or x2 > width or y2 > height:
            raise ValueError(f"crop {bounds} exceeds image size {width}x{height}")
        pixels = gray.load()

        row_span = x2 - x1
        column_span = y2 - y1
        rows = []
        for y in range(y1, y2):
            count = sum(1 for x in range(x1, x2) if pixels[x, y] <= threshold)
            if count / row_span >= min_row_fraction:
                rows.append((y, count))

        columns = []
        for x in range(x1, x2):
            count = sum(1 for y in range(y1, y2) if pixels[x, y] <= threshold)
            if count / column_span >= min_column_fraction:
                columns.append((x, count))

    return {
        "image": str(image_path),
        "image_size": [width, height],
        "crop": list(bounds),
        "threshold": threshold,
        "horizontal_runs": group_runs(rows, row_span),
        "vertical_runs": group_runs(columns, column_span),
    }


def load_manifest(path: Path) -> tuple[ImageSize, str | None, list[int], dict[int, Rect]]:
    """Load and type-check a button-region manifest."""
    data = json.loads(
        path.read_text(encoding="utf-8"),
        parse_constant=reject_json_constant,
        object_pairs_hook=reject_duplicate_object_keys,
    )
    if not isinstance(data, dict):
        raise ValueError("manifest must be a JSON object")

    image_size_raw = data.get("image_size")
    if (
        not isinstance(image_size_raw, list)
        or len(image_size_raw) != 2
        or not all(isinstance(value, int) and not isinstance(value, bool) and value > 0 for value in image_size_raw)
    ):
        raise ValueError("image_size must be [width, height] using positive integers")
    image_size = (image_size_raw[0], image_size_raw[1])

    image_sha256_raw = data.get("image_sha256")
    if image_sha256_raw is not None and (
        not isinstance(image_sha256_raw, str)
        or len(image_sha256_raw) != 64
        or any(character not in "0123456789abcdefABCDEF" for character in image_sha256_raw)
    ):
        raise ValueError("image_sha256 must be a 64-character hexadecimal string")
    image_sha256 = image_sha256_raw.lower() if image_sha256_raw is not None else None

    expected_raw = data.get("expected_buttons")
    regions_raw = data.get("regions")
    if not isinstance(expected_raw, list) or not all(
        isinstance(button, int) and not isinstance(button, bool) for button in expected_raw
    ):
        raise ValueError("expected_buttons must be an array of integers")
    if not isinstance(regions_raw, dict):
        raise ValueError("regions must be an object keyed by button number")

    regions: dict[int, Rect] = {}
    for raw_button, raw_rect in regions_raw.items():
        try:
            button = int(raw_button)
        except (TypeError, ValueError) as error:
            raise ValueError(f"invalid region key: {raw_button!r}") from error
        if button in regions:
            raise ValueError(f"duplicate region key: {raw_button!r} also identifies button {button}")
        if not isinstance(raw_rect, list) or len(raw_rect) != 4:
            raise ValueError(f"button {button} region must be [x, y, width, height]")
        coordinates: list[float] = []
        for value in raw_rect:
            if not isinstance(value, (int, float)) or isinstance(value, bool):
                raise ValueError(f"button {button} region must be [x, y, width, height]")
            try:
                coordinate = float(value)
            except OverflowError as error:
                raise ValueError(f"button {button} region contains a non-finite number") from error
            if not math.isfinite(coordinate):
                raise ValueError(f"button {button} region contains a non-finite number")
            coordinates.append(coordinate)
        regions[button] = tuple(coordinates)  # type: ignore[assignment]
    return image_size, image_sha256, expected_raw, regions


def validate_regions(image_path: Path, manifest_path: Path) -> dict[str, Any]:
    """Check expected coverage, uniqueness, dimensions, and image bounds."""
    expected_image_size, expected_image_sha256, expected, regions = load_manifest(manifest_path)
    with Image.open(image_path) as image:
        width, height = image.size

    errors: list[str] = []
    if expected_image_size != (width, height):
        errors.append(
            f"manifest image_size {expected_image_size[0]}x{expected_image_size[1]} "
            f"does not match image {width}x{height}"
        )
    if expected_image_sha256 is not None:
        actual_image_sha256 = sha256_file(image_path)
        if expected_image_sha256 != actual_image_sha256:
            errors.append(
                f"manifest image_sha256 {expected_image_sha256} does not match image {actual_image_sha256}"
            )
    if len(expected) != len(set(expected)):
        errors.append("expected_buttons contains duplicates")
    expected_set = set(expected)
    region_set = set(regions)
    missing = sorted(expected_set - region_set)
    unexpected = sorted(region_set - expected_set)
    if missing:
        errors.append(f"missing regions: {missing}")
    if unexpected:
        errors.append(f"unexpected regions: {unexpected}")

    rectangle_owners: dict[Rect, int] = {}
    for button, rectangle in sorted(regions.items()):
        x, y, region_width, region_height = rectangle
        if region_width <= 0 or region_height <= 0:
            errors.append(f"button {button} has a non-positive region: {rectangle}")
        if x < 0 or y < 0 or x + region_width > width or y + region_height > height:
            errors.append(f"button {button} exceeds {width}x{height}: {rectangle}")
        if rectangle in rectangle_owners:
            errors.append(f"buttons {rectangle_owners[rectangle]} and {button} share region {rectangle}")
        else:
            rectangle_owners[rectangle] = button

    return {
        "valid": not errors,
        "image_size": [width, height],
        "expected_count": len(expected_set),
        "region_count": len(regions),
        "errors": errors,
    }


def paths_refer_to_same_file(left: Path, right: Path) -> bool:
    """Compare paths safely even when an output is a symlink or hard link."""
    if os.path.normcase(str(left.resolve())) == os.path.normcase(str(right.resolve())):
        return True
    try:
        return left.samefile(right)
    except OSError:
        return False


def validate_output_path(image_path: Path, manifest_path: Path, output_path: Path, force: bool) -> None:
    """Prevent previews from replacing either input or an existing output by accident."""
    for source_path, source_name in ((image_path, "source image"), (manifest_path, "manifest")):
        if paths_refer_to_same_file(output_path, source_path):
            raise ValueError(f"preview output must differ from the {source_name}: {source_path}")
    if output_path.exists() and not force:
        raise ValueError(f"preview output already exists: {output_path}; pass --force to replace it")


def preview_regions(image_path: Path, manifest_path: Path, output_path: Path, force: bool = False) -> dict[str, Any]:
    """Render translucent numbered regions over a copy of the source image."""
    validate_output_path(image_path, manifest_path, output_path, force)
    validation = validate_regions(image_path, manifest_path)
    if not validation["valid"]:
        raise ValueError("manifest is invalid; run validate before preview")
    _, _, _, regions = load_manifest(manifest_path)

    with Image.open(image_path) as opened:
        base = opened.convert("RGBA")
    overlay = Image.new("RGBA", base.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay, "RGBA")
    font = ImageFont.load_default()
    palette = [(239, 68, 68), (37, 99, 235), (22, 163, 74), (202, 138, 4), (147, 51, 234)]

    for index, (button, rectangle) in enumerate(sorted(regions.items())):
        x, y, width, height = rectangle
        left = int(round(x))
        top = int(round(y))
        right = int(round(x + width)) - 1
        bottom = int(round(y + height)) - 1
        red, green, blue = palette[index % len(palette)]
        draw.rectangle(
            (left, top, right, bottom),
            fill=(red, green, blue, 52),
            outline=(red, green, blue, 255),
            width=2,
        )
        label = str(button)
        label_box = draw.textbbox((0, 0), label, font=font)
        label_width = label_box[2] - label_box[0]
        label_height = label_box[3] - label_box[1]
        badge = (left + 3, top + 3, left + label_width + 9, top + label_height + 7)
        draw.rectangle(badge, fill=(red, green, blue, 235))
        draw.text((left + 6, top + 4), label, fill=(255, 255, 255, 255), font=font)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    annotated = Image.alpha_composite(base, overlay)
    flatten_onto_white(annotated).convert("RGB").save(output_path)
    return {"output": str(output_path), "region_count": len(regions), "image_size": list(base.size)}


def bounded_fraction(value: str) -> float:
    """Parse a fraction in the inclusive range from zero to one."""
    try:
        fraction = float(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("value must be a number from 0 to 1") from error
    if not 0 <= fraction <= 1:
        raise argparse.ArgumentTypeError("value must be a number from 0 to 1")
    return fraction


def build_parser() -> argparse.ArgumentParser:
    """Build the command-line parser."""
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    scan = subparsers.add_parser("scan", help="find likely grid-line runs")
    scan.add_argument("image", type=Path)
    scan.add_argument("--crop", type=parse_crop)
    scan.add_argument("--threshold", type=int, default=210, choices=range(256), metavar="0-255")
    scan.add_argument("--min-row-fraction", type=bounded_fraction, default=0.65)
    scan.add_argument("--min-column-fraction", type=bounded_fraction, default=0.55)

    validate = subparsers.add_parser("validate", help="validate a region manifest")
    validate.add_argument("image", type=Path)
    validate.add_argument("manifest", type=Path)

    preview = subparsers.add_parser("preview", help="render a numbered region preview")
    preview.add_argument("image", type=Path)
    preview.add_argument("manifest", type=Path)
    preview.add_argument("output", type=Path)
    preview.add_argument("--force", action="store_true", help="replace an existing preview output")
    return parser


def write_result(result: dict[str, Any]) -> None:
    """Write deterministic JSON output for agents and humans."""
    json.dump(result, sys.stdout, indent=2)
    sys.stdout.write("\n")


def main(argv: Iterable[str] | None = None) -> int:
    """Run the selected region-tool operation and return a process exit code."""
    args = build_parser().parse_args(list(argv) if argv is not None else None)
    try:
        if args.command == "scan":
            result = scan_lines(
                args.image,
                args.crop,
                args.threshold,
                args.min_row_fraction,
                args.min_column_fraction,
            )
        elif args.command == "validate":
            result = validate_regions(args.image, args.manifest)
            write_result(result)
            return 0 if result["valid"] else 1
        else:
            result = preview_regions(args.image, args.manifest, args.output, args.force)
        write_result(result)
        return 0
    except (OSError, ValueError, json.JSONDecodeError) as error:
        write_result({"error": str(error)})
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
