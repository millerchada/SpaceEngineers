# Faithful port of CanonicalizeStock from v2.4.31, run against the live Custom Data
# plus adversarial cases. Mirrors the C# line for line so the test proves the shipped logic.
import io

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


def is_known(n):
    return canon(n) in KNOWN


def canonicalize_stock(stock_keys, text):
    """stock_keys: list of (name, raw_value) as MyIni would return them."""
    if text is None:
        return text
    if not stock_keys:
        return text
    names, val, ord_ = [], {}, {}
    for n, v in stock_keys:
        if n is None or n in val:
            continue
        val[n] = v
        c = canon(n)
        ord_[n] = c if is_known(n) else n
        names.append(n)
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
    """Model MyIni.GetKeys/Get over the [Stock] section."""
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
    """Model EnsureStockAliasesPresent: add absent StockConfigurable items at 0."""
    present = set(canon(k).lower() for k, _ in keys)
    out = list(keys)
    for item in sorted(KNOWN):
        if item.lower() not in present:
            out.append((item, '0'))
    return out


def cycle(text):
    return canonicalize_stock(seed(parse_stock(text)), text)


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


LIVE = io.open(r'C:\Users\Chad\AppData\Local\Temp\claude'
               r'\C--PROJECTS-SpaceEngineers\d933f9d9-41d7-49af-98ce-364bb4fa83c6'
               r'\scratchpad\live_cd.txt', encoding='utf-8').read()

print('=' * 62)
print('PASS 1 on the live Custom Data')
p1 = cycle(LIVE)
p2 = cycle(p1)
p3 = cycle(p2)
print('  pass1 == pass2 (idempotent):', p1 == p2)
print('  pass2 == pass3             :', p2 == p3)

before = dict(parse_stock(LIVE))
after = dict(parse_stock(p1))
print('  keys before/after          : %d -> %d' % (len(before), len(after)))
lost = [k for k in before if k not in after]
print('  keys LOST (must be none)   :', lost or 'none')
changed = [(k, before[k], after[k]) for k in before if before[k] != after[k]]
print('  values CHANGED (must be none):', changed or 'none')
added = sorted(k for k in after if k not in before)
print('  keys ADDED                 :', added or 'none')

order = [k for k, _ in parse_stock(p1)]
print('  sorted by canonical alias  :',
      order == sorted(order, key=lambda x: (canon(x).lower(), x.lower())))

for sec in ['General', 'Sorting', 'Production', 'Display']:
    same = section(LIVE, sec) == section(p1, sec)
    print('  [%s] untouched%s: %s' % (sec, ' ' * (11 - len(sec)), same))

print()
print('=' * 62)
print('ADVERSARIAL: unknown user keys, an alias key, odd values, CRLF')
ADV = LIVE.replace('[Stock]\n', '[Stock]\nZzUserThing=42\nAaaCustom=7\nComputer=500\n'
                                'Uranium=0.5\nWeirdValue=1e3\n')
a1 = cycle(ADV)
a2 = cycle(a1)
print('  idempotent                 :', a1 == a2)
ab, aa = dict(parse_stock(ADV)), dict(parse_stock(a1))
print('  unknown keys survive       :',
      all(k in aa for k in ['ZzUserThing', 'AaaCustom', 'WeirdValue']))
print('  alias key kept its spelling:', 'Computer' in aa and 'Computer=500' in a1)
print('  no duplicate BasicComputer :', 'BasicComputer' not in aa)
print('  raw values preserved       :',
      aa.get('Uranium') == '0.5' and aa.get('WeirdValue') == '1e3'
      and aa.get('ZzUserThing') == '42')
ord_adv = [k for k, _ in parse_stock(a1)]
print('  Computer sorts as BasicComputer:',
      ord_adv.index('Computer') < ord_adv.index('Ceramic'))
print('  unknown keys interleaved   :',
      ord_adv.index('AaaCustom') == 0 and ord_adv[-1] == 'ZzUserThing')

CRLF = ADV.replace('\n', '\r\n')
c1 = cycle(CRLF)
print('  CRLF input idempotent      :', c1 == cycle(c1))
print('  CRLF preserved             :', '\r\n' in c1 and '\n\n' not in c1.replace('\r\n', '\n\n\n'))

print()
print('first 12 ordered keys:', ord_adv[:12])
