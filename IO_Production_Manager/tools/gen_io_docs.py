"""Generate <project>/docs/industrial-overhaul/v1.7.7/ from evidence/machines/*.json.

    python IO_Production_Manager/tools/gen_io_docs.py

The docs are GENERATED, never hand-edited, so they cannot drift from the evidence they
describe. Edit the JSON; re-run this. A hand-edited doc would become a second source of truth,
which is the failure this whole audit stack exists to prevent.
"""
import glob
import io
import json
import os

# PROJ is the IOPM project root; evidence and generated docs both live inside it.
PROJ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EV = os.path.join(PROJ, 'evidence', 'machines')
DOC = os.path.join(PROJ, 'docs', 'industrial-overhaul', 'v1.7.7')


def load():
    return [json.load(io.open(f, encoding='utf-8'))
            for f in sorted(glob.glob(os.path.join(EV, '*.json')))]


def qty(v):
    return ('%g' % v)


def main():
    os.makedirs(DOC, exist_ok=True)
    ms = load()
    complete = [m for m in ms if m['enumeration'] == 'COMPLETE']
    total = sum(len(m['recipes']) for m in ms)

    # ---------------------------------------------------------------- README
    w = ['# Industrial Overhaul v1.7.7 — live production evidence', '',
         'GENERATED from `evidence/machines/*.json` by `tools/gen_io_docs.py`.',
         '**Do not hand-edit.** Edit the evidence JSON and re-run the generator — a',
         'hand-edited doc becomes a second source of truth, which is the failure this whole',
         'audit stack exists to prevent.', '',
         '| File | Contents |', '|---|---|',
         '| `production-machines.md` | Every enumerated machine and its full recipe list |',
         '| `production-catalog.md` | Every observed output, with IOPM reconciliation state |',
         '| `production-dependency-notes.md` | Unresolved identities, conflicts, architecture gaps |',
         '', '## Coverage roster', '',
         '| Machine | Enumeration | Entries |', '|---|---|---:|']
    for m in ms:
        w.append('| %s | %s | %d |' % (m['machine'], m['enumeration'], len(m['recipes'])))
    w += ['| **Total** | | **%d** |' % total, '',
          'Scope: non-refinery production blocks currently built on the grid. Refinery and',
          'ore-processing machines are outside this pass, and **every IO production block not',
          'listed above is UNAUDITED, which is not the same as clean.**', '']
    io.open(os.path.join(DOC, 'README.md'), 'w', encoding='utf-8', newline='').write(
        chr(10).join(w))

    # ----------------------------------------------------- production-machines
    w = ['# Production machines — IO v1.7.7', '',
         'GENERATED from `evidence/machines/*.json`. Do not hand-edit.', '',
         'Build times are reproduced verbatim as displayed, including ranges.', '']
    for m in ms:
        w.append('## %s' % m['machine'])
        w.append('')
        if m['enumeration'] != 'COMPLETE':
            w += ['**%s** — %s' % (m['enumeration'],
                                   m.get('deferred_reason', 'no reason recorded')), '']
            continue
        w.append('IOPM machine token: `%s` · %d entries · %s'
                 % (m['machine_token_in_iopm'], len(m['recipes']),
                    ', '.join('%s %d' % (k, v) for k, v in m['observed_categories'].items())))
        w.append('')
        cats = []
        for r in m['recipes']:
            if r['category'] not in cats:
                cats.append(r['category'])
        for c in cats:
            w += ['### %s' % c, '', '| Output | Build time | Inputs | Notes |',
                  '|---|---|---|---|']
            for r in m['recipes']:
                if r['category'] != c:
                    continue
                ings = '; '.join('%s `%s`' % (k, qty(v)) for k, v in r['ingredients'].items())
                n = []
                if r.get('output_qty'):
                    n.append('**output x%s**' % r['output_qty'])
                if r.get('magazine_capacity'):
                    n.append('capacity %s' % r['magazine_capacity'])
                if r.get('not_craftable_inputs'):
                    n.append('Not Craftable: %s' % ', '.join(r['not_craftable_inputs']))
                if r.get('recipe_shape') == 'MULTI_OUTPUT':
                    n.append('**MULTI-OUTPUT**: ' + '; '.join(
                        '%s x%s' % (o['display'], o['qty']) for o in r['outputs']))
                if r.get('notes'):
                    n.append(r['notes'])
                w.append('| %s | `%s` | %s | %s |'
                         % (r['display'], r.get('build_time_displayed', '—'), ings,
                            ' · '.join(n)))
            w.append('')
    io.open(os.path.join(DOC, 'production-machines.md'), 'w', encoding='utf-8',
            newline='').write(chr(10).join(w))

    # ------------------------------------------------------ production-catalog
    w = ['# Production catalog — every observed output', '',
         'GENERATED from `evidence/machines/*.json`. Do not hand-edit.', '',
         'One row per observed output per machine. An output appearing on several machines',
         'appears several times — that is a fact about the mod, not duplication.', '',
         '| Output | Machine | Category | Canonical alias | Physical identity |',
         '|---|---|---|---|---|']
    for m in ms:
        for r in m['recipes']:
            w.append('| %s | %s | %s | %s | %s |'
                     % (r['display'], m['machine'], r['category'],
                        '`%s`' % r['alias_hint'] if r.get('alias_hint') else '—',
                        '`%s`' % r['identity_hint'] if r.get('identity_hint')
                        else '**unresolved**'))
    w += ['', 'Reconciliation against IOPM (ItemDefs, recipes, StockConfigurable,',
          'manual-queue attribution) is produced live by',
          '`python tests/audit_machine_coverage.py <source.cs>` (from the project root)',
          'rather than frozen here,',
          'so it cannot go stale against the script.', '']
    io.open(os.path.join(DOC, 'production-catalog.md'), 'w', encoding='utf-8',
            newline='').write(chr(10).join(w))

    # --------------------------------------------- production-dependency-notes
    w = ['# Dependency notes, unresolved identities and architecture gaps', '',
         'GENERATED from `evidence/machines/*.json`. Do not hand-edit.', '',
         '## Multi-output recipes — architecture gap', '']
    any_multi = False
    for m in ms:
        for r in m['recipes']:
            if r.get('recipe_shape') != 'MULTI_OUTPUT':
                continue
            any_multi = True
            w += ['### %s — %s' % (r['display'], m['machine']), '',
                  'Inputs: ' + '; '.join('%s `%s`' % (k, qty(v))
                                         for k, v in r['ingredients'].items()), '',
                  '| Output | Qty | Identity |', '|---|---:|---|']
            for o in r['outputs']:
                w.append('| %s | %s | %s |' % (o['display'], o['qty'],
                                               o.get('identity_hint') or '**unresolved**'))
            w += ['', r.get('notes', ''), '']
    if not any_multi:
        w += ['None observed.', '']
    w += ['IOPM models one blueprint → one product. Representing a coproduct recipe needs',
          'multi-output accounting **and** a planning policy that does not re-run the recipe',
          'because only one of its outputs was counted against demand. Until both exist,',
          'these are recorded and deliberately not modelled.', '',
          '## Unresolved and conflicting identities', '',
          '| Output | Machine | Status |', '|---|---|---|']
    for m in ms:
        for r in m['recipes']:
            c = r.get('identity_confidence')
            if c and c.startswith(('UNRESOLVED', 'CONFLICT')):
                w.append('| %s | %s | %s |' % (r['display'], m['machine'], c))
    w += ['', '**Nothing above was resolved by inference.** A physical subtype is never',
          'derived from a display name, and a capacity shown in the UI is not proof of a',
          'different subtype.', '']
    io.open(os.path.join(DOC, 'production-dependency-notes.md'), 'w', encoding='utf-8',
            newline='').write(chr(10).join(w))
    print('generated %d machines, %d entries -> docs/industrial-overhaul/v1.7.7/'
          % (len(ms), total))


if __name__ == '__main__':
    main()
