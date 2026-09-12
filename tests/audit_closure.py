"""Recursive production-graph closure audit.

    python tests/audit_closure.py IO_Production_Manager/IO_Production_Manager_vX.Y.Z.cs

WHY THIS EXISTS. "45 stock-configurable products / 45 recipes / 0 pending" proved every stock
ROOT had a recipe. It said nothing about the dependencies beneath those roots. A root whose
ingredient has no recipe is still unbuildable - the planner reports RawShortage on the
ingredient and the root sits Blocked forever. Canvas shipped with a recipe in v2.4.35 and was
unbuildable on arrival because SyntheticFabric had none. Counting roots cannot catch that.

It walks every ingredient of every active recipe transitively from the stock roots and
classifies each LEAF - an ingredient with no active recipe - as:

  TERMINAL_PROCESS_BOUNDARY    Intentionally not manufactured by IOPM in the current v2.4.x
                               scope. A POLICY statement about where this script stops, not a
                               claim about what the game can or cannot make.
  MANUFACTURED_MISSING_RECIPE  Craftable per positive evidence, but no Recipe implemented.
                               A real hole in the catalog.
  UNKNOWN                      Insufficient evidence to classify. Resolve by observation,
                               never by assumption.

=============================================================================================
TYPEID IS NEVER EVIDENCE HERE. THIS IS THE POINT OF THE WHOLE FILE.
=============================================================================================
An item's TypeId says where it is SORTED. It says nothing about whether it can be MADE, and
Industrial Overhaul's processing routes do not map cleanly from inventory category onto
manufacturing/refining semantics. Two live counter-examples, both from this project:

    Gunpowder   MyObjectBuilder_Ingot/Magnesium   MANUFACTURED - Munitions Factory (Large)
    Polymer     MyObjectBuilder_Ingot/Polymer     MANUFACTURED - blueprint SyntheticPolymer

Both carry an Ingot TypeId. Any "ingots are refinery output, therefore terminal" heuristic
would have declared both terminal and closed this audit on a false green.

Enforced STRUCTURALLY rather than by discipline: classify() takes only the alias. The physical
identity is never passed to it, so a TypeId-derived rule cannot be written there without
changing the signature - which should be treated as a red flag in review.
"""
import io
import os
import re
import sys

# =============================================================================================
# THE PROCESS BOUNDARY - an explicit, hand-maintained POLICY list.
#
# These are the materials IOPM deliberately does not manufacture in the v2.4.x scope.
# Membership is a decision recorded here, NOT a property derived from an item's TypeId, its
# inventory category, or which warehouse it lands in. Putting something on this list says the
# script is not meant to make it. It does not say the game cannot.
#
# To take an item OFF the list, give it a recipe. To put one ON, record the reason below.
# =============================================================================================
TERMINAL_PROCESS_BOUNDARY = {
    'IronIngot', 'NickelIngot', 'CobaltIngot', 'CopperIngot', 'GoldIngot', 'AluminumIngot',
    'TitaniumIngot', 'SilverIngot', 'SiliconWafer', 'Carbon', 'Sulfur', 'LithiumPaste',
    'TantalumIngot', 'PlatinumIngot', 'PotassiumNitrate', 'Gravel',
}
BOUNDARY_REASON = {
    'Gravel': 'ore-processing output (rock crusher); ore processing is outside v2.4.x scope',
}
BOUNDARY_DEFAULT = ('smelting/refining output; IOPM manages no refining by design '
                    '(README "Hard constraints"), so this is a leaf on purpose')

# Positive evidence that something IS craftable, from live observation recorded in the repo or
# supplied by the operator. Blueprint-table membership is separate, and read from the source.
CRAFT_EVIDENCE = {
    'Gunpowder': ('live: crafted by Munitions Factory (Large). Ingredient list NOT captured - '
                  'do not infer it'),
}


def parse(path):
    raw = io.open(path, encoding='utf-8').read()
    noc = chr(10).join(l for l in raw.split(chr(10)) if not l.strip().startswith('//'))
    j = re.sub(r'"\s*\+\s*' + chr(10) + r'?\s*"', '', noc)

    items, configurable = {}, set()
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

    # Mirror BOTH sources of _aliases at runtime: AddItem registers each ItemDef's bare
    # SubtypeId, and AddAliasGroup adds explicit spellings. Missing the first is why an early
    # version of this audit read SyntheticFabric as UNKNOWN - only the subtype registration
    # connects the name "Fabric" to the alias SyntheticFabric.
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
    # AddBlueprintGroup keys through Canon() at runtime (AddPairsTo canonKey:true), so a
    # blueprint declared "Fabric=Fabric" is stored under the alias SyntheticFabric.
    canon_bp = {}
    for a, b in blueprints.items():
        canon_bp.setdefault(a if a in items else aliases.get(a, a), b)

    recipes = {}
    for m in re.finditer(r'"([A-Za-z]+)\|([0-9.]+)\|([^|"]*)\|([^"]*)"', j):
        ings = [(n, q) for n, _, q in
                (i.partition(':') for i in m.group(4).split(',') if i)]
        recipes[m.group(1)] = {'out': m.group(2), 'machine': m.group(3), 'ings': ings}
    return items, configurable, aliases, canon_bp, recipes


def classify(alias, blueprints):
    """Classify a leaf.

    TAKES ONLY THE ALIAS. The physical identity is deliberately not a parameter, so no
    TypeId-derived rule can be written here. See the module docstring.
    """
    if alias in CRAFT_EVIDENCE:
        return 'MANUFACTURED_MISSING_RECIPE', CRAFT_EVIDENCE[alias]
    if alias in blueprints:
        return ('MANUFACTURED_MISSING_RECIPE',
                'blueprint id %s exists in the knowledge table' % blueprints[alias])
    if alias in TERMINAL_PROCESS_BOUNDARY:
        return 'TERMINAL_PROCESS_BOUNDARY', BOUNDARY_REASON.get(alias, BOUNDARY_DEFAULT)
    return 'UNKNOWN', 'no blueprint id, no craft observation, not on the process-boundary list'


def main():
    path = sys.argv[1]
    items, configurable, aliases, blueprints, recipes = parse(path)

    referenced, seen, stack = {}, set(), sorted(configurable)
    while stack:
        a = stack.pop()
        if a in seen:
            continue
        seen.add(a)
        r = recipes.get(a)
        if not r:
            continue
        for nm, _ in r['ings']:
            c = nm if nm in items else aliases.get(nm, nm)
            referenced.setdefault(c, set()).add(a)
            stack.append(c)

    leaves = sorted(a for a in referenced if a not in recipes)
    print('=' * 110)
    print('PRODUCTION-GRAPH CLOSURE - %s' % os.path.basename(path))
    print('roots %d | active recipes %d | distinct dependencies %d | LEAVES %d'
          % (len(configurable), len(recipes), len(referenced), len(leaves)))
    print('=' * 110)
    print('%-17s %-40s %-21s %-4s %s'
          % ('Alias', 'Physical Identity', 'Referenced By', 'Rec', 'Classification'))
    print('-' * 110)

    buckets = {'TERMINAL_PROCESS_BOUNDARY': [], 'MANUFACTURED_MISSING_RECIPE': [],
               'UNKNOWN': []}
    for a in leaves:
        cls, ev = classify(a, blueprints)      # identity deliberately not passed
        ident = items.get(a, '** NO ItemDef **')
        by = ', '.join(sorted(referenced[a]))
        buckets[cls].append((a, ident, sorted(referenced[a]), ev))
        print('%-17s %-40s %-21s %-4s %s'
              % (a, ident, (by[:18] + '...') if len(by) > 20 else by, 'no', cls))

    for name in ('MANUFACTURED_MISSING_RECIPE', 'UNKNOWN', 'TERMINAL_PROCESS_BOUNDARY'):
        rows = buckets[name]
        print()
        print('%s (%d)' % (name, len(rows)))
        if name == 'TERMINAL_PROCESS_BOUNDARY' and rows:
            print('  Intentionally not manufactured by IOPM in the current v2.4.x scope.')
            print('  Membership is explicit policy, never inferred from TypeId.')
        if not rows:
            print('  (none)')
        for a, ident, by, ev in rows:
            print('  %-17s %s' % (a, ident))
            print('  %-17s referenced by: %s' % ('', ', '.join(by)))
            print('  %-17s evidence: %s' % ('', ev))

    holes = len(buckets['MANUFACTURED_MISSING_RECIPE']) + len(buckets['UNKNOWN'])
    print()
    if holes:
        print('CLOSURE INCOMPLETE - %d leaf/leaves are not intentional process boundaries'
              % holes)
        return 1
    print('CLOSURE COMPLETE - every leaf is an explicit, intentional process boundary')
    return 0


if __name__ == '__main__':
    sys.exit(main())
