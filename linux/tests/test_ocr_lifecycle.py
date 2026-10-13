"""Shared Linux/macOS OCR cache lifecycle without native OCR dependencies."""

from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import v6_ocr


class OcrLifecycleTests(unittest.TestCase):
    def setUp(self):
        for name, value in (("_engine", None), ("_idle_timer", None), ("_generation", 0)):
            patcher = patch.object(v6_ocr, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        self.factory = patch.object(v6_ocr, "_create_engine").start()
        self.timer = patch.object(v6_ocr.threading, "Timer").start()
        self.addCleanup(patch.stopall)

    def test_load_reuse_release_and_reload(self):
        first, second = Mock(), Mock()
        self.factory.side_effect = [first, second]
        self.assertIsNone(v6_ocr._engine)
        v6_ocr._run_ocr("first")
        v6_ocr._run_ocr("second")
        self.assertEqual(self.factory.call_count, 1)
        self.assertEqual(first.call_count, 2)
        self.assertTrue(self.timer.return_value.daemon)
        generation = v6_ocr._generation
        v6_ocr._release_idle_engine(generation)
        self.assertIsNone(v6_ocr._engine)
        self.assertIsNone(v6_ocr._idle_timer)
        v6_ocr._run_ocr("third")
        self.assertIs(v6_ocr._engine, second)

    def test_canceled_callback_cannot_release_new_request(self):
        v6_ocr._run_ocr("first")
        stale_generation = v6_ocr._generation
        v6_ocr._run_ocr("second")
        self.timer.return_value.cancel.assert_called_once()
        v6_ocr._release_idle_engine(stale_generation)
        self.assertIs(v6_ocr._engine, self.factory.return_value)

    def test_request_owns_lock_and_failed_request_still_expires(self):
        def fail(*_args, **_kwargs):
            self.assertFalse(v6_ocr._engine_lock.acquire(blocking=False))
            raise ValueError("bad image")
        self.factory.return_value.side_effect = fail
        with self.assertRaises(ValueError):
            v6_ocr._run_ocr("invalid")
        self.timer.return_value.start.assert_called_once()
        v6_ocr._release_idle_engine(v6_ocr._generation)
        self.assertIsNone(v6_ocr._engine)

    def test_failed_initialization_can_retry_without_idle_timer(self):
        self.factory.side_effect = [RuntimeError("missing model"), Mock()]
        with self.assertRaises(RuntimeError):
            v6_ocr._run_ocr("first")
        self.timer.assert_not_called()
        v6_ocr._run_ocr("retry")
        self.assertIsNotNone(v6_ocr._engine)


if __name__ == "__main__":
    unittest.main()
