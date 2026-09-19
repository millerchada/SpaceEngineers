"""Generate IO Power Control's built-in power catalog from the Industrial Overhaul mod files.

Tier 3 of the potential-demand model needs a rated load for blocks that publish nothing usable
through DetailedInfo - pistons, doors, medical rooms, programmable blocks and every other small
consumer. On a real base those are the majority of blocks by count, and while each is tiny, an
unknown block is excluded from the model AND drags the coverage figure down until the operator
stops trusting it.

Those ratings are not a guess: they are declared in the mod's own .sbc definitions. This reads
them and emits the CATALOG[] array. Nothing here invents a number; if a block does not declare
one, it does not get an entry, and the script keeps reporting it as unknown.

    python IO_Power_Control/tools/extract_io_catalog.py <ModData> [<BaseGameData> ...]

Give the BASE GAME Data directory as well and its blocks are read first, with the mod's
definitions overriding them - which is the same order the game loads them in. Without it, the
catalog covers only blocks Industrial Overhaul actually redefines, and vanilla blocks that
publish nothing through DetailedInfo (doors, medical rooms, programmable blocks) stay unknown.

    python IO_Power_Control/tools/extract_io_catalog.py \
        "D:/SteamLibrary/.../244850/2344068716/Data" \
        "D:/SteamLibrary/steamapps/common/SpaceEngineers/Content/Data"

Writes the C# array to stdout and to IO_Power_Control/docs/io_power_catalog.txt for review.

WHAT THIS CANNOT REACH. Measured 2026-09-19: adding the base game's own Data directory to a
scan of Industrial Overhaul yields only FIVE extra subtypes. Many vanilla blocks - sliding
hatch doors, medical rooms, programmable blocks among them - declare no power tag in any .sbc
at all, because their consumption is hardcoded in the game's C# and applied through a resource
sink component at runtime. In-game tools that read the live component (BuildInfo) can show
those figures; definition files cannot. Those blocks stay unknown here by necessity, and the
only route to a number for them is a measurement typed into [PowerControl.Catalog].

TAGS READ, and why:
  RequiredPowerInput            the block's rated draw. The figure Potential wants.
  OperationalPowerConsumption   production blocks: draw while actually working.
  PowerConsumptionMoving        doors and similar: the MOVING figure, not the idle one, because
                                Potential asks what the block COULD draw.
  RequiredIdlePower             only used when nothing better is declared.

Where a subtype declares several, the LARGEST is taken - again because the question is the
ceiling, not the resting state.
"""
import io
import os
import re
import sys

TAGS = ("RequiredPowerInput", "OperationalPowerConsumption", "PowerConsumptionMoving",
        "RequiredIdlePower")
# Any of these that exist under a given root are scanned. The mod keeps its block files in
# three folders; the base game keeps them in one.
SUBDIRS = ("Cubeblocks_IO", "CubeBlocks_Vanilla", "CubeBlocks_DLC", "CubeBlocks")

SUB_RX = re.compile(r"<SubtypeId>([^<]*)</SubtypeId>")
TAG_RX = re.compile(r"<(%s)>\s*([0-9.eE+-]+)\s*</\1>" % "|".join(TAGS))


def scan(path):
    """Walk each block definition and pair its SubtypeId with the largest power tag inside it.

    The .sbc files are flat XML and a Definition is delimited by </Definition>, so the parse is
    a split rather than a tree walk - deliberately, because a malformed or unfamiliar file
    should yield fewer entries, never wrong ones attributed to the wrong block.
    """
    out = {}
    for sub in SUBDIRS:
        d = os.path.join(path, sub)
        if not os.path.isdir(d):
            continue
        for name in sorted(os.listdir(d)):
            if not name.lower().endswith(".sbc"):
                continue
            text = io.open(os.path.join(d, name), encoding="utf-8", errors="replace").read()
            for block in text.split("</Definition>"):
                subs = SUB_RX.findall(block)
                if not subs:
                    continue
                # The FIRST SubtypeId in a definition is the block's own; later ones are
                # component references, mount points and build models.
                subtype = subs[0].strip()
                if not subtype:
                    continue
                best = 0.0
                for m in TAG_RX.finditer(block):
                    try:
                        v = float(m.group(2))
                    except ValueError:
                        continue
                    if v > best:
                        best = v
                if best <= 0:
                    continue
                if subtype not in out or best > out[subtype]:
                    out[subtype] = best
    return out


def fmt(v):
    s = ("%.6f" % v).rstrip("0").rstrip(".")
    return s if s else "0"


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    # LAST root wins on a clash, so pass the base game AFTER the mod and it would override it -
    # wrong way round. Roots are therefore applied in reverse: later arguments are the weaker
    # source, matching the natural reading of "the mod, then the game underneath it".
    data = {}
    for root in reversed(sys.argv[1:]):
        found = scan(root)
        sys.stderr.write("%5d from %s\n" % (len(found), root))
        data.update(found)
    if not data:
        print("no power figures found - is that a Data directory?")
        return 2
    rows = ['"%s=%s"' % (k, fmt(v)) for k, v in sorted(data.items())]
    lines, cur = [], ""
    for r in rows:
        add = (r + ", ")
        if len(cur) + len(add) > 96:
            lines.append(cur.rstrip())
            cur = ""
        cur += add
    if cur:
        lines.append(cur.rstrip().rstrip(","))
    body = "\n".join(lines)
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    docs = os.path.join(here, "docs")
    if not os.path.isdir(docs):
        os.makedirs(docs)
    io.open(os.path.join(docs, "io_power_catalog.txt"), "w", encoding="utf-8",
            newline="").write(body + "\n")
    sys.stdout.write(body + "\n")
    sys.stderr.write("\n%d subtypes with a declared power figure\n" % len(data))
    return 0


if __name__ == "__main__":
    sys.exit(main())
