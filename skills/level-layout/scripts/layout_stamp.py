#!/usr/bin/env python3
"""Stamp blocks of layout entries (road-kit recipes, or any piece group) into a layout JSON.

A block is a list of entries [guid, x, y, rot, name, w, h]: the layout anchor (min corner of the
footprint), the quarter turns (clockwise, as in the layout), a label, and the footprint in cells as
placed. Blocks come from a recipes file ({"recipes": [{"name", "size": [W, H], "pieces": [...]}]},
e.g. NK's docs/road-recipes.json) or are built in code with `entry()`.

Turning a block keeps every piece's own turn consistent: a piece at rot r becomes rot r + turns, and
its anchor moves so that its footprint lands where the turned block puts it. Pure stdlib; the
apply step (apply_layout) stays the authority on footprints, so check the grid report after stamping.

Library use:
    from layout_stamp import Layout, load_recipe, crop, turn, shift
    lay = Layout.load("Level.layout.json")           # or Layout.new(40, 40)
    block = load_recipe("docs/road-recipes.json", "four-way junction")
    lay.add_decor(shift(turn(block, 1, 18, 18), 11, 11), prefix="jn")
    lay.save("Level.layout.json")

Command line (one stamp per call):
    layout_stamp.py LAYOUT.json --recipes RECIPES.json --recipe NAME --at X Y
                    [--crop X0 Y0 X1 Y1] [--turns N] [--prefix ID] [--size W H]
"""
import argparse
import json
import sys


def entry(guid, x, y, rot, name, w, h):
    """One block entry; w x h are the cells as placed (already turned by rot)."""
    return [guid, int(x), int(y), int(rot) % 4, name, int(w), int(h)]


def load_recipe(path, name):
    """The named recipe's entries and its size (W, H)."""
    with open(path) as f:
        recipes = json.load(f)["recipes"]
    for recipe in recipes:
        if recipe["name"] == name:
            return [entry(*p) for p in recipe["pieces"]], tuple(recipe["size"])
    raise KeyError(f"no recipe '{name}' in {path}; have: {', '.join(r['name'] for r in recipes)}")


def crop(block, x0, y0, x1, y1):
    """Entries whose footprint lies inside cells x0..x1, y0..y1 (inclusive), moved so x0, y0 is 0, 0."""
    out = []
    for g, x, y, r, n, w, h in block:
        if x >= x0 and y >= y0 and x + w - 1 <= x1 and y + h - 1 <= y1:
            out.append(entry(g, x - x0, y - y0, r, n, w, h))
    return out


def turn(block, turns, width, height):
    """Turns a width x height block clockwise by quarter turns (seen from above, +y up)."""
    out = [list(e) for e in block]
    for _ in range(turns % 4):
        # Clockwise: (x, y) -> (y, W - 1 - x); a footprint's new min corner is (y, W - x - w).
        out = [entry(g, y, width - x - w, r + 1, n, h, w) for g, x, y, r, n, w, h in out]
        width, height = height, width
    return out


def shift(block, dx, dy):
    return [entry(g, x + dx, y + dy, r, n, w, h) for g, x, y, r, n, w, h in block]


def cells(e):
    _, x, y, _, _, w, h = e
    return [(x + i, y + j) for i in range(w) for j in range(h)]


class Layout:
    """A layout document plus the cells its decor covers, so stamps can't stack ground tiles."""

    def __init__(self, doc):
        self.doc = doc
        self.ground = {}
        self.sizes = {}

    @classmethod
    def new(cls, width, height):
        return cls({"v": 1, "size": [width, height], "pieces": [], "deploy": [], "enemies": [], "decor": []})

    @classmethod
    def load(cls, path):
        with open(path) as f:
            return cls(json.load(f))

    def save(self, path):
        with open(path, "w") as f:
            json.dump(self.doc, f, separators=(",", ":"))
            f.write("\n")

    def _ids(self):
        return {e[0] for key in ("pieces", "decor", "enemies") for e in self.doc.get(key, [])}

    def add_decor(self, block, prefix, replace=False, clip=True):
        """
        Adds entries as decor with ids prefix0, prefix1, .... A cell already covered by an earlier stamp is an
        error unless replace=True (then the earlier tile covering it is removed). With clip, entries reaching off
        the board are skipped. Returns the number added.
        """
        width, height = self.doc["size"]
        ids = self._ids()
        added = 0
        for e in block:
            footprint = cells(e)
            if clip and any(not (0 <= cx < width and 0 <= cy < height) for cx, cy in footprint):
                continue
            hits = {self.ground[c] for c in footprint if c in self.ground}
            if hits and not replace:
                raise ValueError(f"{prefix}: {e[4]} at {e[1]},{e[2]} overlaps {', '.join(sorted(hits))}")
            for old in hits:
                self.remove(old)
            i = 0
            while f"{prefix}{i}" in ids:
                i += 1
            local_id = f"{prefix}{i}"
            ids.add(local_id)
            self.doc["decor"].append([local_id, e[0], e[1], e[2], e[3]])
            for c in footprint:
                self.ground[c] = local_id
            added += 1
        return added

    def remove(self, local_id):
        self.doc["decor"] = [d for d in self.doc["decor"] if d[0] != local_id]
        for c in [c for c, owner in self.ground.items() if owner == local_id]:
            del self.ground[c]

    def free(self, x0, y0, x1, y1):
        """True when no stamped tile covers cells x0..x1, y0..y1."""
        return all((x, y) not in self.ground for x in range(x0, x1 + 1) for y in range(y0, y1 + 1))


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("layout", help="layout JSON to stamp into (created with --size if missing)")
    p.add_argument("--recipes", required=True)
    p.add_argument("--recipe", required=True)
    p.add_argument("--at", nargs=2, type=int, required=True, metavar=("X", "Y"))
    p.add_argument("--crop", nargs=4, type=int, metavar=("X0", "Y0", "X1", "Y1"))
    p.add_argument("--turns", type=int, default=0)
    p.add_argument("--prefix", default="t")
    p.add_argument("--size", nargs=2, type=int, metavar=("W", "H"))
    p.add_argument("--replace", action="store_true", help="replace tiles already on the stamped cells")
    a = p.parse_args(argv)
    try:
        lay = Layout.load(a.layout)
    except FileNotFoundError:
        if not a.size:
            p.error(f"{a.layout} does not exist; give --size W H to start one")
        lay = Layout.new(*a.size)
    # Existing decor isn't sized here; stamps only check against what this run adds.
    block, (w, h) = load_recipe(a.recipes, a.recipe)
    if a.crop:
        x0, y0, x1, y1 = a.crop
        block, w, h = crop(block, x0, y0, x1, y1), x1 - x0 + 1, y1 - y0 + 1
    block = shift(turn(block, a.turns, w, h), *a.at)
    n = lay.add_decor(block, a.prefix, replace=a.replace)
    lay.save(a.layout)
    print(f"stamped {n} tiles of '{a.recipe}' at {a.at[0]},{a.at[1]} (turns {a.turns}) into {a.layout}")


if __name__ == "__main__":
    sys.exit(main())
