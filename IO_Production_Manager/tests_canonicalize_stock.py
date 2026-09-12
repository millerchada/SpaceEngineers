# Faithful port of DetectStockCollisions + CanonicalizeStock from IOPM v2.4.32, run against
# the live Custom Data plus adversarial inputs. Mirrors the C# closely enough that passing here
# is real evidence about the shipped logic. Run: python tests_canonicalize_stock.py
import io
import sys

# --- model of IOPM's Canon(): alias -> canonical, and the known-item set ----
ALIASES = {
    'computer': 'BasicComputer', 'detector': 'SensorCluster',
    'bulletproofglass': 'Glass', 'interiorplate': 'AluminumPlate',
    'largetube': 'LargeSteelTube', 'smalltube': 'SmallSteelTube',
    'medical': 'MedicalComponent', 'powercell': 'LithiumPowerCell',
    'constructioncomp': 'Construction',
}
KNOWN = set("""SteelPlate AluminumPlate TitaniumPlate CopperWire GoldWire LargeSteelTube
SmallSteelTube Construction MetalGrid Electromagnet Motor BasicComputer AdvancedComputer
SensorCluster Display Thermocouple Ceramic Glass HeatingElement Lightbulb MedicalComponent
LithiumPowerCell Plastic Rubber Superconductor GravityGenerator Thrust LaserEmitter ArmorGlass
ElectronMatrix FSSolarCell QuantumComputer Reactor SuperMagnet TokamakBlanket Concrete
Cryocooler ArmoredPlate Capacitor Explosives Girder RadioCommunication SolarCell Canvas
Polymer""".split())


def canon(n):
    return ALIASES.get(n.lower(), n)


def known(n):
    return canon(n) in KNOWN


def detect_collisions(stock_keys):
    """Port of DetectStockCollisions. Returns (collide_set, messages)."""
    collide, msgs, groups, order = set(), [], {}, []
    for n, _ in stock_keys:
        if n is None or not known(n):
            continue
        c = canon(n)
        if c.lower() not in groups:
            groups[c.lower()] = (c, [])
            order.append(c.lower())
        groups[c.lower()][1].append(n)
    order.sort(key=lambda k: groups[k][0].lower())
    for k in order:
        c, g = groups[k]
        if len(g) < 2:
            continue
        g.sort(key=lambda x: x.lower())
        collide.add(k)
        msgs.append('[Stock] alias collision on %s: %s all mean %s' % (c, ', '.join(g), c))
    return collide, msgs


def canonicalize_stock(stock_keys, text, collide):
    """Port of CanonicalizeStock."""
    if text is None or not stock_keys:
        return text
    names, val, ord_ = [], {}, {}
    for n, raw in stock_keys:
        if n is None:
            continue
        c = canon(n)
        k = known(n)
        name = c if (k and c.lower() not in collide) else n
        if name.lower() in (x.lower() for x in val):
            continue
        val[name] = raw
        ord_[name] = c if k else name
        names.append(name)
    names.sort(key=lambda x: (ord_[x].lower(), x.lower()))
    nl = '\r\n' if '\r\n' in text else '\n'
    lines = [l.rstrip('\r') for l in text.split('\n')]
    start, end = -1, len(lines)
    for i, l in enumerate(lines):
        t = l.strip()
        if start < 0:
            if t.lower() == '[stock]':
                start = i
            continue
        if t.startswith('['):
            end = i
            break
    if start < 0:
        return text
    out = lines[:start + 1]
    out += ['%s=%s' % (n, val[n]) for n in names]
    if end < len(lines):
        out.append('')
    out += lines[end:]
    return nl.join(out)


def parse_stock(text):
    keys, inside = [], False
    for l in text.split('\n'):
        t = l.rstrip('\r').strip()
        if t.lower() == '[stock]':
            inside = True
            continue
        if inside and t.startswith('['):
            break
        if inside and '=' in t:
            k, _, v = t.partition('=')
            keys.append((k.strip(), v.strip()))
    return keys


def seed(keys):
    """Port of EnsureStockAliasesPresent."""
    present = set(canon(k).lower() for k, _ in keys)
    out = list(keys)
    for item in sorted(KNOWN):
        if item.lower() not in present:
            out.append((item, '0'))
    return out


def cycle(text):
    keys = seed(parse_stock(text))
    collide, msgs = detect_collisions(keys)
    return canonicalize_stock(keys, text, collide), msgs


def section(text, name):
    grab, out = False, []
    for l in text.split('\n'):
        t = l.rstrip('\r').strip()
        if t.lower() == '[' + name.lower() + ']':
            grab = True
            out.append(t)
            continue
        if grab and t.startswith('['):
            break
        if grab:
            out.append(t)
    return '\n'.join(out)


FAILURES = []


def check(label, cond):
    print('  %-46s %s' % (label, 'PASS' if cond else '*** FAIL ***'))
    if not cond:
        FAILURES.append(label)


LIVE = io.open('live_custom_data.txt', encoding='utf-8').read()

print('=' * 68)
print('LIVE CONFIG (45 canonical keys) - must be a no-op today')
p1, m1 = cycle(LIVE)
p2, _ = cycle(p1)
p3, _ = cycle(p2)
before, after = dict(parse_stock(LIVE)), dict(parse_stock(p1))
check('idempotent (pass1 == pass2 == pass3)', p1 == p2 == p3)
check('no collisions reported', m1 == [])
check('key count unchanged 45 -> 45', len(before) == len(after) == 45)
check('no key lost', not [k for k in before if k not in after])
check('no value changed', not [k for k in before if before[k] != after[k]])
check('ZERO VISIBLE DIFF vs v2.4.31 ordering',
      [k for k, _ in parse_stock(p1)] == sorted(before, key=lambda x: (canon(x).lower(), x.lower())))
for sec in ['General', 'Sorting', 'Production', 'Display']:
    check('[%s] untouched' % sec, section(LIVE, sec) == section(p1, sec))

print()
print('=' * 68)
print('RE-SPELLING: an alias key must be rewritten to its canonical name')
RS = LIVE.replace('BasicComputer=1000\n', '').replace('[Stock]\n', '[Stock]\nComputer=500\n')
r1, rm = cycle(RS)
r2, _ = cycle(r1)
ks = dict(parse_stock(r1))
check('idempotent', r1 == r2)
check('no collision (only one key means BasicComputer)', rm == [])
check('renamed Computer -> BasicComputer', 'BasicComputer' in ks and 'Computer' not in ks)
check('value carried across exactly', ks.get('BasicComputer') == '500')
check('sorted position correct',
      [k for k, _ in parse_stock(r1)].index('BasicComputer')
      < [k for k, _ in parse_stock(r1)].index('Canvas'))

print()
print('=' * 68)
print('COLLISION: two keys meaning the same item - report, never discard')
CO = LIVE.replace('[Stock]\n', '[Stock]\nComputer=500\n')  # BasicComputer=1000 still present
c1, cm = cycle(CO)
c2, cm2 = cycle(c1)
ks = dict(parse_stock(c1))
check('idempotent', c1 == c2)
check('collision reported', len(cm) == 1 and 'BasicComputer' in cm[0])
check('collision still reported next cycle (stable)', cm == cm2)
check('BOTH keys survive', ks.get('BasicComputer') == '1000' and ks.get('Computer') == '500')
check('neither renamed', 'Computer' in ks and 'BasicComputer' in ks)
check('no value lost', len(parse_stock(c1)) == 46)

print()
print('=' * 68)
print('UNKNOWN KEYS / VALUE FIDELITY / LINE ENDINGS')
ADV = LIVE.replace('[Stock]\n', '[Stock]\nZzUserThing=42\nAaaCustom=7\nUranium=0.5\nWeird=1e3\n')
a1, _ = cycle(ADV)
a2, _ = cycle(a1)
ka = dict(parse_stock(a1))
ordA = [k for k, _ in parse_stock(a1)]
check('idempotent', a1 == a2)
check('unknown keys survive', all(k in ka for k in ['ZzUserThing', 'AaaCustom', 'Weird']))
check('unknown keys not renamed', ka.get('Uranium') == '0.5')
check('raw values preserved verbatim',
      ka.get('Weird') == '1e3' and ka.get('ZzUserThing') == '42')
check('unknown keys interleave alphabetically',
      ordA[0] == 'AaaCustom' and ordA[-1] == 'ZzUserThing')
CRLF = ADV.replace('\n', '\r\n')
cr1, _ = cycle(CRLF)
cr2, _ = cycle(cr1)
check('CRLF idempotent', cr1 == cr2)
check('every newline stays CRLF', '\n' not in cr1.replace('\r\n', '') and '\r\n' in cr1)
check('LF input stays LF', '\r' not in a1)

print()
print('=' * 68)
print('STRUCTURAL EDGE CASES')
MIN = LIVE.split('[Stock]')[0] + '[Stock]\nSteelPlate=10000\n\n[IOPM.Status]\nVersion=x\n'
m1s, _ = cycle(MIN)
m2s, _ = cycle(m1s)
check('sparse section idempotent', m1s == m2s)
check('sparse section seeded to 45', len(parse_stock(m1s)) == 45)
check('existing value kept while seeding', dict(parse_stock(m1s))['SteelPlate'] == '10000')
LAST = LIVE.split('[IOPM.Status]')[0].rstrip() + '\n'
l1, _ = cycle(LAST)
check('[Stock] as final section idempotent', l1 == cycle(l1)[0])

print()
if FAILURES:
    print('FAILED: %d' % len(FAILURES))
    for f in FAILURES:
        print('  - ' + f)
    sys.exit(1)
print('ALL CHECKS PASSED')
