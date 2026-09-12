"""Machine-coverage audit: has IOPM modelled everything the mod actually exposes?

    python tests/audit_machine_coverage.py IO_Production_Manager/IO_Production_Manager_vX.Y.Z.cs

THE THIRD AUDIT, and it answers a question the other two structurally cannot:

    audit_catalog.py   are the products and recipes IOPM KNOWS internally consistent?
    audit_closure.py   do the recipe graphs IOPM KNOWS terminate at an intentional boundary?
    this one           does IOPM know everything the MOD exposes?

A product absent from IOPM entirely passes both other audits silently: it is not a stock root,
so catalog integrity never looks at it, and no known recipe references it, so it never appears
as a leaf in the closure graph. Absence is invisible to any audit that starts from what we
already have. This one starts from the OUTSIDE - live evidence captured per machine in
machine_evidence/*.json - and compares inward.

CLASSIFICATION
  MANAGED               active Recipe in IOPM. Ingredients are compared against the evidence
                        and any mismatch is reported, because a recipe that exists but is
                        wrong is worse than one that is missing.
  KNOWN_IDENTITY_ONLY   IOPM can resolve the item (ItemDef or loadout alias) but has no Recipe.
  MISSING_FROM_IOPM     absent entirely - no ItemDef, no alias, no blueprint.
  INTENTIONAL_EXCLUDE   deliberately out of scope. ONLY from the explicit list below. Never
                        inferred from an item being a tool, weapon, bottle or ammo.
  UNKNOWN               present in some form but the physical identity cannot be pinned down.

Coverage is REPORTED, never fatal. A gap here is a scope decision for a human, not a defect
that should block an unrelated fix from shipping.
"""
import glob
import io
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from audit_closure import parse  # noqa: E402  same source model, one place to keep correct

# Deliberate scope exclusions. EMPTY ON PURPOSE - nothing has been excluded yet. An entry here
# is a recorded decision with a reason, never a category-based assumption.
INTENTIONAL_EXCLUDE = {}


def load_evidence():
    root = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                        'machine_evidence')
    out = []
    for f in sorted(glob.glob(os.path.join(root, '*.json'))):
        out.append(json.load(io.open(f, encoding='utf-8')))
    return out


def main():
    path = sys.argv[1]
    items, configurable, aliases, blueprints, recipes = parse(path)
    src = io.open(path, encoding='utf-8').read()
    noc = chr(10).join(l for l in src.split(chr(10)) if not l.strip().startswith('//'))
    j = re.sub(r'"\s*\+\s*' + chr(10) + r'?\s*"', '', noc)
    loadalias = {}
    for m in re.finditer(r'AddLoadAlias\("([^"]+)",\s*"([^"]*)"\)', j):
        tid, body = m.group(1), m.group(2)
        for e in body.split(','):
            if e:
                a, _, sub = e.partition('=')
                loadalias[a] = tid + '/' + (sub or a)
    load_by_type = {v: k for k, v in loadalias.items()}

    machines = load_evidence()
    if not machines:
        print('no machine evidence found under machine_evidence/')
        return 2

    totals = {'MANAGED': 0, 'KNOWN_IDENTITY_ONLY': 0, 'MISSING_FROM_IOPM': 0,
              'INTENTIONAL_EXCLUDE': 0, 'UNKNOWN': 0}
    mismatches, unresolved, producer_gaps = [], [], []

    for ev in machines:
        print('=' * 116)
        print('MACHINE COVERAGE - %s (%s)' % (ev['machine'], ev['mod']))
        print('enumeration: %s | observed recipes: %d | categories: %s'
              % (ev['enumeration'], ev['observed_recipes'],
                 ', '.join('%s %d' % (k, v) for k, v in ev['observed_categories'].items())))
        print('provenance: %s' % ev['provenance'])
        print('=' * 116)
        per = {'MANAGED': 0, 'KNOWN_IDENTITY_ONLY': 0, 'MISSING_FROM_IOPM': 0,
               'INTENTIONAL_EXCLUDE': 0, 'UNKNOWN': 0}

        for r in ev['recipes']:
            disp = r['display']
            hint = r.get('alias_hint')
            ident_hint = r.get('identity_hint')

            # resolve the alias IOPM would use, without trusting the display name
            alias = None
            if hint in items:
                alias = hint
            elif hint in aliases:
                alias = aliases[hint]
            elif ident_hint and ident_hint in load_by_type:
                alias = load_by_type[ident_hint]
            elif ident_hint:
                for a, t in items.items():
                    if t == ident_hint:
                        alias = a
                        break

            has_item = alias in items if alias else False
            # Blueprint knowledge can exist WITHOUT an ItemDef - AcidPowerCell is the live
            # case. Look it up by the evidence's own hint too, or the audit reports "nothing
            # known" about something the repo demonstrably knows a blueprint id for.
            has_bp = bool((alias and alias in blueprints) or (hint and hint in blueprints))
            bp_id = blueprints.get(alias) or (blueprints.get(hint) if hint else None)
            has_recipe = alias in recipes if alias else False
            stockcfg = alias in configurable if alias else False
            # BlueprintReverseMap iterates _items, so only an ItemDef-backed alias WITH a
            # blueprint can attribute a manually queued job. A loadout alias cannot.
            attributable = bool(has_item and has_bp)
            ident = items.get(alias) if has_item else (
                loadalias.get(alias) if alias in loadalias else ident_hint)

            match = 'n/a'
            if has_recipe:
                live = {k: float(v) for k, v in r['ingredients'].items()}
                have = {n: float(q) for n, q in recipes[alias]['ings']}
                have = {(n if n in items else aliases.get(n, n)): q for n, q in have.items()}
                livec = {(n if n in items else aliases.get(n, n)): q for n, q in live.items()}
                match = 'YES' if livec == have else 'NO'
                if match == 'NO':
                    mismatches.append((disp, livec, have))

            if alias in INTENTIONAL_EXCLUDE:
                cls = 'INTENTIONAL_EXCLUDE'
            elif has_recipe:
                cls = 'MANAGED'
            elif alias and (has_item or alias in loadalias):
                cls = 'KNOWN_IDENTITY_ONLY'
            elif has_bp:
                # Partial knowledge: a blueprint id, but no physical identity. A BLUEPRINT ID
                # IS NOT AN IDENTITY - it says how to queue a job, never what the resulting
                # item IS - so this is neither "known" nor "absent".
                cls = 'UNKNOWN'
            else:
                cls = 'MISSING_FROM_IOPM'

            # PRODUCER COVERAGE, reported separately from identity coverage. A recipe can be
            # correct and still name a machine this item was never observed on; the token is
            # only a preference, so this is information, not a fault.
            if has_recipe:
                tok = recipes[alias]['machine']
                names_this = any(t.strip().lower() == ev['machine_token_in_iopm'].lower()
                                 for t in tok.split(';'))
                if not names_this:
                    producer_gaps.append((ev['machine'], disp, tok))

            if r.get('identity_confidence', '').startswith(('CONFLICT', 'UNRESOLVED')):
                unresolved.append((disp, r['identity_confidence']))

            totals[cls] += 1
            per[cls] += 1
            print()
            print('  %-28s [%s]' % (disp, r['category']))
            print('    canonical alias      : %s' % (alias or '-- none --'))
            print('    physical identity    : %s' % (ident or '-- unresolved --'))
            print('    blueprint identity   : %s' % (bp_id or '-- none --'))
            print('    ItemDef / blueprint  : %-3s / %-3s' % ('yes' if has_item else 'no',
                                                              'yes' if has_bp else 'no'))
            print('    active Recipe        : %s' % ('yes' if has_recipe else 'no'))
            print('    StockConfigurable    : %s' % ('yes' if stockcfg else 'no'))
            print('    manual-queue attrib. : %s' % ('yes' if attributable else 'no'))
            if has_recipe:
                print('    iopm machine token   : %s' % recipes[alias]['machine'])
            print('    ingredients match    : %s' % match)
            print('    CLASSIFICATION       : %s' % cls)
            if r.get('notes'):
                print('    notes                : %s' % r['notes'])
            if r.get('identity_confidence'):
                print('    identity confidence  : %s' % r['identity_confidence'])

        print()
        print('  -- %s subtotal (%d outputs): %s' % (
            ev['machine'], sum(per.values()),
            '  '.join('%s %d' % (k, v) for k, v in per.items() if v)))

    print()
    print('=' * 116)
    print('SUMMARY (all machines audited)')
    for k in ('MANAGED', 'KNOWN_IDENTITY_ONLY', 'MISSING_FROM_IOPM', 'INTENTIONAL_EXCLUDE',
              'UNKNOWN'):
        print('  %-22s %d' % (k, totals[k]))
    print()
    if mismatches:
        print('  INGREDIENT MISMATCHES (%d) - a wrong recipe is worse than a missing one'
              % len(mismatches))
        for d, live, have in mismatches:
            print('    %s' % d)
            print('      live : %s' % live)
            print('      iopm : %s' % have)
    else:
        print('  ingredient mismatches: none')
    print()
    if producer_gaps:
        print('  PRODUCER GAPS (%d) - IOPM has a recipe whose machine token does not name the'
              % len(producer_gaps))
        print('  machine the output was observed on. Token is a PREFERENCE only; eligibility')
        print('  comes from CanUseBlueprint, so this is information, not a defect.')
        for m, d, tok in producer_gaps:
            print('    %-28s observed on %-20s token "%s"' % (d, m, tok))
    else:
        print('  producer gaps: none - every modelled recipe names the machine it was seen on')
    if unresolved:
        print()
        print('  UNRESOLVED / CONFLICTING IDENTITIES (%d)' % len(unresolved))
        for d, why in unresolved:
            print('    %-28s %s' % (d, why))
    print()
    print('  Machines audited: %s' % ', '.join(
        '%s (%s)' % (m['machine'], m['enumeration']) for m in machines))
    print('  GLOBAL IO MACHINE COVERAGE IS NOT ESTABLISHED - only the machines listed above')
    print('  have been enumerated. Every other production block is UNAUDITED, not clean.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
