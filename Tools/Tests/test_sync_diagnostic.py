import importlib.util
import math
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location(
    "analyze_sync_diagnostic", Path(__file__).parents[1] / "analyze_sync_diagnostic.py")
diagnostic = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diagnostic)


class StopwatchTimingTests(unittest.TestCase):
    def test_crossing_uses_irregular_presentation_times(self):
        samples = [(2.0, -0.05, 1.78), (2.2, 0.15, 1.78)]
        self.assertAlmostEqual(diagnostic.mesh_beats(samples)[0], 2.05)

    def test_parked_first_beep_is_not_a_measurable_crossing(self):
        samples = [(0.0, 0.0, 1.78), (0.5, 0.0, 1.78), (0.6, 0.7, 1.6)]
        self.assertEqual(diagnostic.mesh_beats(samples), [])

    def test_six_oclock_crossing_is_excluded(self):
        samples = [(1.0, -0.1, -1.78), (1.1, 0.1, -1.78)]
        self.assertEqual(diagnostic.mesh_beats(samples), [])

    def test_audio_onset_angle_uses_interpolated_vertices(self):
        samples = [(1.0, -0.1, 1.78), (1.1, 0.1, 1.78)]
        self.assertAlmostEqual(diagnostic.hand_angle_at_time(samples, 1.05), 0.0)
        self.assertAlmostEqual(diagnostic.hand_angle_at_time(samples, 1.025),
                               math.degrees(math.atan2(-0.05, 1.78)))


if __name__ == "__main__":
    unittest.main()
