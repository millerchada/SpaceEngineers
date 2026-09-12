"""Shorten OUR OWN identifiers in the deployment artifact. Behaviour-preserving by construction.

Identifiers are 47% of the artifact's characters, and the bulk of what we control is our own
private fields and method names. Shortening them reclaims PB ceiling without touching a single
line of logic - no expression is rewritten, no branch merged, nothing inlined. The readable
names live on in the Git source, which is the only place anyone reads them.

WHAT IS RENAMED
  - private fields: every identifier beginning with '_'. In this codebase that prefix is used
    exclusively for class-level fields.
  - our own methods: names declared at column 0 in the stripped artifact.

WHAT IS NOT, AND WHY
  - Anything preceded by '.'. This is the rule that makes the pass safe. Our method `Get` and
    MyIni's `ini.Get` share a name; renaming by bare token would silently rewrite the API call.
    Our own members are always called bare, framework members always dotted, so the dot is a
    reliable discriminator.
  - Anything inside a string literal. Diagnostics text, [Stock] keys, item aliases and GOAT
    tokens are DATA - rewriting one would change behaviour, not just spelling.
  - C# keywords, `Program`, `Main`, `Save`, and class/struct member names (which are reached
    through a dot anyway).
  - Locals. They are the riskiest to rename (shadowing can rebind rather than error) and the
    least valuable, so they are left alone.

VERIFICATION - the reason this is trustworthy rather than merely plausible
  1. Applying the INVERSE mapping to the renamed text must reproduce the original BYTE FOR
     BYTE. That proves the transform is a bijection over tokens and that nothing outside the
     mapping was touched. A rename that clobbered a literal, ate a character, or collided two
     names could not survive this.
  2. Generated names are checked against every identifier already present, so a rename can
     never capture an existing name.
  3. build_pb.py re-verifies string literals and code structure afterwards, and check_pb.py
     compiles the result. A rename that produced invalid C# cannot reach the artifact.
"""
import re

KEYWORDS = set('''abstract as base bool break byte case catch char checked class const continue
decimal default delegate do double else enum event explicit extern false finally fixed float for
foreach get goto if implicit in int interface internal is lock long namespace new null object
operator out override params private protected public readonly ref return sbyte sealed set short
sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked
unsafe ushort using value virtual void volatile while var yield partial where'''.split())

NEVER = {'Program', 'Main', 'Save'}

# Alphabet for generated names. Two chars gives 52*63 combinations - far more than needed.
_A = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ'


def _tokens_outside_strings(code):
    """Yield (start, end, text) for identifier tokens that are NOT inside a string or char
    literal, along with whether the token is preceded by a '.'."""
    i, n = 0, len(code)
    while i < n:
        c = code[i]
        if c == '"':
            i += 1
            while i < n:
                if code[i] == chr(92):
                    i += 2
                    continue
                if code[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        if c == "'":
            i += 1
            while i < n:
                if code[i] == chr(92):
                    i += 2
                    continue
                if code[i] == "'":
                    i += 1
                    break
                i += 1
            continue
        if c.isalpha() or c == '_':
            j = i
            while j < n and (code[j].isalnum() or code[j] == '_'):
                j += 1
            k = i - 1
            while k >= 0 and code[k] in ' \t':
                k -= 1
            dotted = k >= 0 and code[k] == '.'
            yield i, j, code[i:j], dotted
            i = j
            continue
        i += 1


def collect_targets(code):
    """Return the set of identifiers we own and may safely rename."""
    present = set()
    ever_dotted = set()
    for _, _, t, dotted in _tokens_outside_strings(code):
        present.add(t)
        if dotted:
            ever_dotted.add(t)

    # EXCLUDE ANY NAME THAT IS EVER REACHED THROUGH A DOT. This is the rule that keeps the
    # pass honest, and it was added because the compiler caught the pass getting it wrong:
    # QueueLoad and ServiceLoadouts are each BOTH one of our method names AND a field on a
    # class (MachineChoice.QueueLoad, Config.ServiceLoadouts). The dotted USES were correctly
    # skipped, but the bare DECLARATION `public double QueueLoad;` was renamed - desyncing the
    # two and producing CS1061. If a name is ever dotted anywhere in the file it belongs to a
    # type's surface, so leave every occurrence of it alone. This costs a little saving (our
    # own `Get` is excluded because MyIni's `ini.Get` is dotted) and buys correctness.
    fields = set(t for t in present
                 if t.startswith('_') and len(t) > 2 and t not in ever_dotted)

    # Method declarations sit at column 0 in the stripped artifact and end the line with '{'.
    methods = set()
    for line in code.split(chr(10)):
        if not line or line[0] in ' \t' or not line.rstrip().endswith('{'):
            continue
        m = re.match(r'^[A-Za-z_][A-Za-z0-9_<>,\[\]\. ]*?\s([A-Za-z_][A-Za-z0-9_]*)\s*\(', line)
        if m:
            name = m.group(1)
            if (name not in KEYWORDS and name not in NEVER and len(name) > 2
                    and name not in ever_dotted):
                methods.add(name)
    return fields, methods, present


def build_map(targets, present):
    """Deterministic short names that collide with nothing already in the file."""
    mapping, used, idx = {}, set(present), 0

    def nxt():
        nonlocal idx
        while True:
            n = len(_A)
            cand = 'Q' + _A[idx % n] + (('' if idx < n else _A[(idx // n) % n]))
            idx += 1
            if cand not in used:
                used.add(cand)
                return cand

    for t in sorted(targets, key=lambda x: (-len(x), x)):
        mapping[t] = nxt()
    return mapping


def apply_map(code, mapping):
    """Rewrite bare (non-dotted, non-string) occurrences of mapped identifiers."""
    out, last = [], 0
    for s, e, t, dotted in _tokens_outside_strings(code):
        if dotted or t not in mapping:
            continue
        out.append(code[last:s])
        out.append(mapping[t])
        last = e
    out.append(code[last:])
    return ''.join(out)


def minify(code):
    """Return (renamed_code, mapping, report). Raises AssertionError if the round trip fails."""
    fields, methods, present = collect_targets(code)
    targets = fields | methods
    mapping = build_map(targets, present)
    renamed = apply_map(code, mapping)

    # --- the proof: inverse must reproduce the original exactly -------------
    inverse = {v: k for k, v in mapping.items()}
    assert len(inverse) == len(mapping), 'mapping is not injective'
    restored = apply_map(renamed, inverse)
    if restored != code:
        for i, (x, y) in enumerate(zip(restored, code)):
            if x != y:
                raise AssertionError('round trip diverged at %d: %r vs %r'
                                     % (i, restored[i - 40:i + 40], code[i - 40:i + 40]))
        raise AssertionError('round trip length mismatch: %d vs %d' % (len(restored), len(code)))

    report = {
        'fields': len(fields),
        'methods': len(methods),
        'saved': len(code) - len(renamed),
    }
    return renamed, mapping, report
