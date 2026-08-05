from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

from PIL import Image, ImageDraw


MODULE_PATH = Path(__file__).parents[1] / "scripts" / "region_tool.py"
MODULE_SPEC = importlib.util.spec_from_file_location("region_tool", MODULE_PATH)
if MODULE_SPEC is None or MODULE_SPEC.loader is None:
    raise RuntimeError(f"Unable to load {MODULE_PATH}")
region_tool = importlib.util.module_from_spec(MODULE_SPEC)
MODULE_SPEC.loader.exec_module(region_tool)


class RegionToolTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary_directory.name)
        self.image_path = self.root / "controller.png"
        self.manifest_path = self.root / "regions.json"
        with Image.new("RGB", (40, 30), "white") as image:
            image.save(self.image_path)
        self.write_manifest()

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def write_manifest(self, **overrides: object) -> None:
        manifest: dict[str, object] = {
            "image_size": [40, 30],
            "expected_buttons": [1, 2],
            "regions": {
                "1": [2, 3, 10, 8],
                "2": [20, 12, 12, 10],
            },
        }
        manifest.update(overrides)
        self.manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    def test_validate_accepts_matching_manifest_and_optional_hash(self) -> None:
        digest = hashlib.sha256(self.image_path.read_bytes()).hexdigest()
        self.write_manifest(image_sha256=digest)

        result = region_tool.validate_regions(self.image_path, self.manifest_path)

        self.assertTrue(result["valid"])
        self.assertEqual([], result["errors"])

    def test_validate_reports_image_size_and_hash_mismatches(self) -> None:
        self.write_manifest(image_size=[41, 30], image_sha256="0" * 64)

        result = region_tool.validate_regions(self.image_path, self.manifest_path)

        self.assertFalse(result["valid"])
        self.assertTrue(any("image_size" in error for error in result["errors"]))
        self.assertTrue(any("image_sha256" in error for error in result["errors"]))

    def test_manifest_rejects_non_finite_coordinates(self) -> None:
        self.manifest_path.write_text(
            '{"image_size":[40,30],"expected_buttons":[1],"regions":{"1":[NaN,2,3,4]}}',
            encoding="utf-8",
        )

        with self.assertRaisesRegex(ValueError, "non-finite"):
            region_tool.load_manifest(self.manifest_path)

    def test_manifest_rejects_boolean_button_numbers(self) -> None:
        self.write_manifest(expected_buttons=[True], regions={"1": [2, 3, 10, 8]})

        with self.assertRaisesRegex(ValueError, "array of integers"):
            region_tool.load_manifest(self.manifest_path)

    def test_non_object_manifest_returns_a_structured_cli_error(self) -> None:
        self.manifest_path.write_text("[]", encoding="utf-8")
        output = io.StringIO()

        with redirect_stdout(output):
            exit_code = region_tool.main(["validate", str(self.image_path), str(self.manifest_path)])

        self.assertEqual(2, exit_code)
        self.assertEqual("manifest must be a JSON object", json.loads(output.getvalue())["error"])

    def test_preview_refuses_to_overwrite_either_input(self) -> None:
        image_before = self.image_path.read_bytes()
        manifest_before = self.manifest_path.read_bytes()

        with self.assertRaisesRegex(ValueError, "source image"):
            region_tool.preview_regions(self.image_path, self.manifest_path, self.image_path, force=True)
        with self.assertRaisesRegex(ValueError, "manifest"):
            region_tool.preview_regions(self.image_path, self.manifest_path, self.manifest_path, force=True)

        self.assertEqual(image_before, self.image_path.read_bytes())
        self.assertEqual(manifest_before, self.manifest_path.read_bytes())

    def test_preview_requires_force_to_replace_an_existing_output(self) -> None:
        output_path = self.root / "preview.png"
        output_path.write_bytes(b"existing preview")
        first_output = io.StringIO()

        with redirect_stdout(first_output):
            first_exit_code = region_tool.main(
                ["preview", str(self.image_path), str(self.manifest_path), str(output_path)]
            )

        self.assertEqual(2, first_exit_code)
        self.assertIn("--force", json.loads(first_output.getvalue())["error"])

        forced_output = io.StringIO()
        with redirect_stdout(forced_output):
            forced_exit_code = region_tool.main(
                ["preview", str(self.image_path), str(self.manifest_path), str(output_path), "--force"]
            )

        self.assertEqual(0, forced_exit_code)
        self.assertEqual(str(output_path), json.loads(forced_output.getvalue())["output"])
        with Image.open(output_path) as preview:
            self.assertEqual((40, 30), preview.size)

    def test_scan_finds_dark_horizontal_and_vertical_runs(self) -> None:
        with Image.new("RGB", (40, 30), "white") as image:
            draw = ImageDraw.Draw(image)
            draw.line((0, 10, 39, 10), fill="black")
            draw.line((20, 0, 20, 29), fill="black")
            image.save(self.image_path)

        result = region_tool.scan_lines(
            self.image_path,
            crop=None,
            threshold=10,
            min_row_fraction=0.9,
            min_column_fraction=0.9,
        )

        self.assertEqual(10, result["horizontal_runs"][0]["peak"])
        self.assertEqual(20, result["vertical_runs"][0]["peak"])


if __name__ == "__main__":
    unittest.main()
