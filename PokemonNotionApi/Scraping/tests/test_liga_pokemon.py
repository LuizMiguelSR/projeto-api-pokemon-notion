import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "liga_pokemon", Path(__file__).resolve().parents[1] / "liga_pokemon.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class Page:
    def __init__(self, states):
        self.states = iter(states)
        self.waits = 0

    def evaluate(self, script, **kwargs):
        assert kwargs["isolated_context"] is False
        state = next(self.states)
        if isinstance(state, Exception):
            raise state
        return state

    def wait_for_timeout(self, milliseconds):
        assert milliseconds == 500
        self.waits += 1


class CardWaitTests(unittest.TestCase):
    def test_ready_card_does_not_wait(self):
        page = Page([True])
        self.assertTrue(bridge.wait_for_card_data(page, 2000))
        self.assertEqual(page.waits, 0)

    def test_intermediate_page_waits_for_prices(self):
        page = Page([False, False, True])
        self.assertTrue(bridge.wait_for_card_data(page, 2000))
        self.assertEqual(page.waits, 2)

    def test_persistent_challenge_stops_at_limit(self):
        page = Page([False, False])
        self.assertFalse(bridge.wait_for_card_data(page, 1000))
        self.assertEqual(page.waits, 2)

    def test_navigation_during_challenge_is_retried(self):
        from patchright.sync_api import Error
        page = Page([Error("Execution context was destroyed"), True])
        self.assertTrue(bridge.wait_for_card_data(page, 2000))
        self.assertEqual(page.waits, 1)


if __name__ == "__main__":
    unittest.main()
