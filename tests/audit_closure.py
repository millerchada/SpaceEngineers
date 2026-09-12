"""Recursive production-graph closure audit.

    python tests/audit_closure.py IO_Production_Manager/IO_Production_Manager_vX.Y.Z.cs

WHY THIS EXISTS. "45 stock-configurable products / 45 recipes / 0 pending" proved every stock
ROOT had a recipe. It said nothing about the dependencies beneath those roots. A root with a
recipe whose ingredient has no recipe is still unbuildable - the planner reports RawShortage on
the ingredient and the root sits Blocked forever. SyntheticFabric exposed the gap; Gunpowder is
a second, independent instance.

It walks every ingredient of every active recipe, transitively, and classifies each LEAF - an
ingredient with no active recipe - as:

  TERMINAL_RAW                intentionally outside production control. Refinery and ore-
                              processing output. IOPM manages no refining by design, so these
                              are supposed to be leaves.
  MANUFACTURED_MISSING_RECIPE known or observed craftable, but no Recipe implemented. This is
                              a real hole in the catalog.
  UNKNOWN                     insufficient evidence to classify either way. Must be resolved by
                              observation, never by assumption.

HAVING AN ItemDef DOES NOT MAKE SOMETHING TERMINAL. That was the mistake this audit exists to
prevent: Gunpowder carries an Ingot TypeId and would pass any "is it an ingot" heuristic, yet
it is crafted in a Munitions Factory. TypeId describes where an item is SORTED, not whether it
can be MADE. Classification therefore reads positive evidence - blueprint knowledge, a known
crafting machine, a changelog observation - and falls back to UNKNOWN rather than guessing.
"""
import io
import os
import re
import sys

# Refinery / ore-processing output. IOPM manages no refining by design (README: "Hard
# constraints"), so these are leaves ON PURPOSE. Listed explicitly rather than inferred from
# TypeId, because TypeId is not evidence of craftability - see the module docstring.
TERMINAL_RAW = {
    'IronIngot', 'NickelIngot', 'CobaltIngot', 'CopperIngot', 'GoldIngot', 'AluminumIngot',
    'TitaniumIngot', 'SilverIngot', 'SiliconWafer', 'Carbon', 'Sulfur', 'LithiumPaste',
    'TantalumIngot', 'PlatinumIngot', 'PotassiumNitrate', 'Gravel',
}
TERMINAL_EVIDENCE = {
    'Gravel': 'Ingot/Stone - rock-crusher output of the ore chain; refining is out of scope',
}

# Positive evidence that something IS craftable, from live observation recorded in the repo or
# supplied by the operator. Blueprint-table membership is separate and read from the source.
CRAFT_EVIDENCE = {
    'Gunpowder': 'live: crafted by Munitions Factory (Large); IO reuses vanilla Ingot/Magnesium',
}


def parse(path):
    raw = io.open(path, encoding='utf-8').read()
    noc = chr(10).join(l for l in raw.split(chr(10)) if not l.strip().startswith('//'))
    j = re.sub(r'"\s*\+\s*' + chr(10) + r'?\s*"', '', noc)

    items = {}
    configurable = set()
    for m in re.finditer(r'AddItemGroup\("([^"]+)",\s*"([^"]*)",\s*(true|false)\)', j):
        tid, body, cfg = m.group(1), m.group(2), m.group(3)
        for e in body.split(','):
            if not e:
                continue
            a, _, sub = e.partition('=')
            items[a] = tid + '/' + (sub or a)
            if cfg == 'true':
                configurable.add(a)
    for m in re.finditer(r'MarkStockConfigurable\("(\w+)"\)', j):
        configurable.add(m.group(1))

    # Mirror BOTH sources of _aliases at runtime:
    #   1. AddItem registers the bare SubtypeId as an alias of its ItemDef
    #      (_aliases[subtype] = alias, first declaration wins), and
    #   2. AddAliasGroup adds explicit spellings.
    # Missing (1) is why the first version of this audit read SyntheticFabric as UNKNOWN: the
    # blueprint is declared "Fabric=Fabric", and only the subtype registration connects the
    # name Fabric to the alias SyntheticFabric.
    aliases = {}
    for a, ident in items.items():
        sub = ident.split('/')[-1]
        if sub not in aliases:
            aliases[sub] = a
    for m in re.finditer(r'AddAliasGroup\("([^"]*)"\)', j):
        for e in m.group(1).split(','):
            if e:
                a, _, t = e.partition('=')
                aliases[a] = t

    blueprints = {}
    for m in re.finditer(r'AddBlueprintGroup\("([^"]*)"\)', j):
        for e in m.group(1).split(','):
            if e:
                a, _, b = e.partition('=')
                blueprints[a] = b

    recipes = {}
    for m in re.finditer(r'"([A-Za-z]+)\|([0-9.]+)\|([^|"]*)\|([^"]*)"', j):
        ings = []
        for ing in m.group(4).split(','):
            if ing:
                n, _, q = ing.partition(':')
                ings.append((n, q))
        recipes[m.group(1)] = {'out': m.group(2), 'machine': m.group(3), 'ings': ings}
    # AddBlueprintGroup keys through Canon() at runtime (AddPairsTo canonKey:true), so a
    # blueprint declared as "Fabric=Fabric" is stored under the ALIAS SyntheticFabric once that
    # ItemDef exists. Replicate that here or the audit under-classifies: SyntheticFabric read
    # as UNKNOWN on the first run when it is really MANUFACTURED_MISSING_RECIPE.
    canon_bp = {}
    for a, b in blueprints.items():
        key = a if a in items else aliases.get(a, a)
        canon_bp.setdefault(key, b)
    return items, configurable, aliases, canon_bp, recipes


def canon(name, items, aliases):
    if name in items:
        return name
    if name in aliases:
        return aliases[name]
    return name


def main():
    path = sys.argv[1]
    items, configurable, aliases, blueprints, recipes = parse(path)

    # transitive closure from every stock-configurable root
    referenced = {}
    seen = set()
    stack = [r for r in sorted(configurable)]
    while stack:
        a = stack.pop()
        if a in seen:
            continue
        seen.add(a)
        r = recipes.get(a)
        if not r:
            continue
        for nm, qty in r['ings']:
            c = canon(nm, items, aliases)
            referenced.setdefault(c, set()).add(a)
            stack.append(c)

    leaves = sorted(a for a in referenced if a not in recipes)

    print('=' * 108)
    print('PRODUCTION-GRAPH CLOSURE - %s' % os.path.basename(path))
    print('roots (stock-configurable) %d | active recipes %d | distinct dependencies %d | '
          'LEAVES %d' % (len(configurable), len(recipes), len(referenced), len(leaves)))
    print('=' * 108)
    hdr = '%-17s %-42s %-22s %-4s %s' % ('Alias', 'Physical Identity', 'Referenced By',
                                         'Rec', 'Classification')
    print(hdr)
    print('-' * 108)

    buckets = {'TERMINAL_RAW': [], 'MANUFACTURED_MISSING_RECIPE': [], 'UNKNOWN': []}
    for a in leaves:
        ident = items.get(a, '** NO ItemDef **')
        by = ', '.join(sorted(referenced[a]))
        if len(by) > 21:
            by = by[:18] + '...'
        if a in CRAFT_EVIDENCE:
            cls, ev = 'MANUFACTURED_MISSING_RECIPE', CRAFT_EVIDENCE[a]
        elif a in blueprints:
            cls, ev = ('MANUFACTURED_MISSING_RECIPE',
                       'blueprint id %s exists in the knowledge table' % blueprints[a])
        elif a in TERMINAL_RAW:
            cls, ev = 'TERMINAL_RAW', TERMINAL_EVIDENCE.get(
                a, 'refinery output; refining out of IOPM scope by design')
        else:
            cls, ev = 'UNKNOWN', 'no blueprint id, no craft observation, not a known raw'
        buckets[cls].append((a, ident, sorted(referenced[a]), ev))
        print('%-17s %-42s %-22s %-4s %s' % (a, ident, by, 'no', cls))

    for name in ('MANUFACTURED_MISSING_RECIPE', 'UNKNOWN', 'TERMINAL_RAW'):
        rows = buckets[name]
        print()
        print('%s (%d)' % (name, len(rows)))
        if not rows:
            print('  (none)')
        for a, ident, by, ev in rows:
            print('  %-17s %-42s' % (a, ident))
            print('  %-17s referenced by: %s' % ('', ', '.join(by)))
            print('  %-17s evidence: %s' % ('', ev))

    print()
    holes = len(buckets['MANUFACTURED_MISSING_RECIPE']) + len(buckets['UNKNOWN'])
    if holes:
        print('CLOSURE INCOMPLETE - %d leaf/leaves are not intentional terminals' % holes)
        return 1
    print('CLOSURE COMPLETE - every leaf is an intentional terminal raw material')
    return 0


if __name__ == '__main__':
    sys.exit(main())
