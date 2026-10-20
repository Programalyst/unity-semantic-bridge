import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(__file__))
from layout_stamp import Layout, cells, crop, entry, shift, turn  # noqa: E402

G = "0" * 32


class TurnTests(unittest.TestCase):
    def test_quarter_turn_moves_footprint_clockwise(self):
        # A 2x1 piece at the bottom-left of a 4x3 block ends at the top-left of the turned 3x4 block.
        block = [entry(G, 0, 0, 0, "a", 2, 1)]
        (g, x, y, r, n, w, h), = turn(block, 1, 4, 3)
        self.assertEqual((x, y, r, w, h), (0, 2, 1, 1, 2))

    def test_cells_follow_a_clockwise_point_rotation(self):
        block = [entry(G, 1, 0, 3, "a", 2, 3)]
        before = set(cells(block[0]))
        after = set(cells(turn(block, 1, 4, 5)[0]))
        self.assertEqual(after, {(y, 4 - 1 - x) for x, y in before})

    def test_four_turns_round_trip(self):
        block = [entry(G, 1, 2, 1, "a", 2, 3), entry(G, 0, 0, 2, "b", 1, 1)]
        self.assertEqual(turn(block, 4, 5, 6), block)
        self.assertEqual(turn(turn(block, 1, 5, 6), 3, 6, 5), block)


class CropShiftTests(unittest.TestCase):
    def test_crop_keeps_whole_pieces_and_rebases(self):
        block = [entry(G, 2, 2, 0, "in", 2, 2), entry(G, 3, 0, 0, "cut", 2, 3)]
        self.assertEqual(crop(block, 2, 2, 5, 5), [entry(G, 0, 0, 0, "in", 2, 2)])

    def test_shift(self):
        self.assertEqual(shift([entry(G, 1, 1, 0, "a", 1, 1)], 3, 4)[0][1:3], [4, 5])


class LayoutTests(unittest.TestCase):
    def test_overlap_is_an_error_unless_replacing(self):
        lay = Layout.new(10, 10)
        lay.add_decor([entry(G, 0, 0, 0, "road", 2, 2)], "r")
        with self.assertRaises(ValueError):
            lay.add_decor([entry(G, 1, 1, 0, "walk", 2, 2)], "w")
        lay.add_decor([entry(G, 1, 1, 0, "walk", 2, 2)], "w", replace=True)
        self.assertEqual([d[0] for d in lay.doc["decor"]], ["w0"])

    def test_off_board_entries_are_clipped(self):
        lay = Layout.new(4, 4)
        n = lay.add_decor([entry(G, 3, 0, 0, "edge", 2, 2), entry(G, 0, 0, 0, "ok", 2, 2)], "t")
        self.assertEqual(n, 1)
        self.assertEqual(lay.doc["decor"][0][:3], ["t0", G, 0])

    def test_ids_do_not_collide_with_existing_ones(self):
        lay = Layout.new(6, 6)
        lay.doc["pieces"].append(["t0", G, 4, 4, 0])
        lay.add_decor([entry(G, 0, 0, 0, "a", 1, 1)], "t")
        self.assertEqual(lay.doc["decor"][0][0], "t1")


if __name__ == "__main__":
    unittest.main()
