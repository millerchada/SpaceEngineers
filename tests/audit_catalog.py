"""Catalog-integrity audit for an IOPM source.

    python tests/audit_catalog.py IO_Production_Manager/IO_Production_Manager_vX.Y.Z.cs

Checks the invariants that hold the item catalog together and that no compiler can see:

  - every recipe ingredient resolves to a known ItemDef or alias
  - every recipe output is itself an ItemDef
  - raw physical identity -> alias is ONE-TO-ONE. Two aliases for one TypeId/SubtypeId makes
    _onHand crediting and ScanQueues attribution depend on declaration order.
  - no alias is declared twice with different identities
  - stock-configurable product count, managed recipe count, recipe-pending set
  - a loadout alias that duplicates an ItemDef identity (redundant, and a second name for the
    same thing)
"""
import io
import os
import re
import sys


def strip_comments(s):
    return chr(10).join(l for l in s.split(chr(10)) if not l.strip().startswith('//'))


def join_concat(s):
    return re.sub(r'"\s*\+\s*' + chr(10) + r'?\s*"', '', s)


def parse(path):
    raw = io.open(path, encoding='utf-8').read()
    j = join_concat(strip_comments(raw))

    items = {}          # alias -> raw type
    order = []
    configurable = set()
    for m in re.finditer(r'AddItemGroup\("([^"]+)",\s*"([^"]*)",\s*(true|false)\)', j):
        tid, body, cfg = m.group(1), m.group(2), m.group(3)
        for e in body.split(','):
            if not e:
                continue
            alias, _, sub = e.partition('=')
            sub = sub or alias
            items[alias] = tid + '/' + sub
            order.append(alias)
            if cfg == 'true':
                configurable.add(alias)
    for m in re.finditer(r'MarkStockConfigurable\("(\w+)"\)', j):
        configurable.add(m.group(1))

    aliases = {}
    for m in re.finditer(r'AddAliasGroup\("([^"]*)"\)', j):
        for e in m.group(1).split(','):
            if e:
                a, _, t = e.partition('=')
                aliases[a] = t

    loadalias = {}
    for m in re.finditer(r'AddLoadAlias\("([^"]+)",\s*"([^"]*)"\)', j):
        tid, body = m.group(1), m.group(2)
        for e in body.split(','):
            if e:
                a, _, sub = e.partition('=')
                loadalias[a] = tid + '/' + (sub or a)

    recipes = []
    for m in re.finditer(r'"([A-Za-z]+)\|([0-9.]+)\|([^|"]*)\|([^"]*)"', j):
        recipes.append((m.group(1), m.group(2), m.group(3), m.group(4)))
    return items, order, configurable, aliases, loadalias, recipes


FAIL = []


def check(label, cond, detail=''):
    print('  %-52s %s' % (label, 'PASS' if cond else '*** FAIL ***'))
    if detail:
        print('       ' + detail)
    if not cond:
        FAIL.append(label)


def main():
    path = sys.argv[1]
    items, order, configurable, aliases, loadalias, recipes = parse(path)
    print('=' * 72)
    print('CATALOG INTEGRITY - %s' % os.path.basename(path))
    print('  ItemDefs %d | stock-configurable %d | aliases %d | loadout aliases %d | recipes %d'
          % (len(items), len(configurable), len(aliases), len(loadalias), len(recipes)))
    print()

    names = [r[0] for r in recipes]
    check('no duplicate recipe alias', len(names) == len(set(names)),
          ', '.join(sorted(n for n in set(names) if names.count(n) > 1)))

    bad_out = [n for n in names if n not in items]
    check('every recipe output is an ItemDef', not bad_out, ', '.join(bad_out))

    bad_in = []
    for r in recipes:
        for ing in r[3].split(','):
            nm = ing.split(':')[0]
            if nm and nm not in items and nm not in aliases:
                bad_in.append('%s needs %s' % (r[0], nm))
    check('every ingredient resolves to ItemDef/alias', not bad_in, '; '.join(bad_in))

    # ONE-TO-ONE raw identity -> alias
    by_type = {}
    for a in order:
        by_type.setdefault(items[a], []).append(a)
    dup = {t: v for t, v in by_type.items() if len(v) > 1}
    check('raw identity -> alias is one-to-one', not dup,
          '; '.join('%s <- %s' % (t, ', '.join(v)) for t, v in dup.items()))

    dupdecl = [a for a in set(order) if order.count(a) > 1]
    check('no alias declared twice', not dupdecl, ', '.join(dupdecl))

    # A loadout alias whose NAME IS THE BARE SUBTYPE of an identity an ItemDef already owns is
    # dead weight: AddItem already registers the bare SubtypeId as an alias, so the name
    # resolves at priority A without this table. ALTERNATE SPELLINGS are a different thing and
    # are deliberately allowed - RadioCommComponent, ReactorComponent, ThrustComponent,
    # GravityGenComponent and DetectorComponent are names a GOAT loadout might be written with
    # that cannot be derived from the subtype, so they earn their place.
    # Neither kind affects raw -> alias attribution: AddLoadAlias does not populate _typeAlias.
    # That invariant is the one checked above, and it is the one that actually matters.
    itemtypes = set(items.values())
    redundant = {a: t for a, t in loadalias.items()
                 if t in itemtypes and a not in items and t.split('/')[-1] == a}
    check('no loadout alias duplicates a bare ItemDef subtype', not redundant,
          '; '.join('%s -> %s' % (a, t) for a, t in redundant.items()))

    pending = sorted(a for a in configurable if a not in names)
    print()
    print('  stock-configurable products : %d' % len(configurable))
    print('  managed recipes             : %d' % len(recipes))
    print('  recipe-pending products     : %d %s'
          % (len(pending), ('- ' + ', '.join(pending)) if pending else ''))
    print()
    if FAIL:
        print('CATALOG AUDIT FAILED (%d)' % len(FAIL))
        return 1
    print('CATALOG AUDIT PASSED')
    return 0


if __name__ == '__main__':
    sys.exit(main())
