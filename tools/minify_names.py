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
import io
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


# Members of BCL / SE types that one of OUR classes might also happen to declare. Renaming a
# name in this set risks rewriting `list.Count` or `sb.Append`, and while the compile gate
# would catch that, a transform should not lean on the last line of defence for a case it can
# simply refuse up front.
BCL_MEMBERS = set("""Count Length Add Remove Clear Contains Item Key Value Keys Values Sort
ToString Equals GetHashCode GetType Substring IndexOf Trim Split Join Replace StartsWith
EndsWith Parse TryParse TryGetValue ContainsKey Append AppendLine Max Min Round Abs Floor
Ceiling Action Func Task Enabled Position Status Mode Target""".split())

_DECL = re.compile(r'\bpublic\s+(?:readonly\s+)?[A-Za-z_][\w<>,\[\]\.\s]*?\s'
                   r'([A-Za-z_]\w*)\s*(?=[;,=({])')


def api_surface(stub_path):
    """Every member and type name se_stubs.cs declares - the API we must never rewrite."""
    try:
        stub = io.open(stub_path, encoding='utf-8').read()
    except IOError:
        return set()
    names = set()
    for m in re.finditer(r'\b([A-Za-z_]\w*)\s*(?:\(|\{\s*get|\{\s*set|;)', stub):
        names.add(m.group(1))
    for m in re.finditer(r'\b(?:class|struct|interface|enum)\s+([A-Za-z_]\w*)', stub):
        names.add(m.group(1))
    return names


def owned_members(code, api_names):
    """Names declared as members inside OUR OWN class/struct bodies in the artifact.

    THE OWNERSHIP RULE, which is the entire point of this function. A name may be renamed only
    if it is declared inside a type THIS FILE defines, and is not part of any API surface we
    compile against. The artifact contains nothing but IOPM code - the SE interfaces live in
    se_stubs.cs and the BCL is external - so "declared in a type body here" really does mean
    "ours". `api_names` is every name se_stubs.cs declares; a collision disqualifies the name
    outright, which today removes Name, Type, Amount and EntityId.

    Anything missed is simply not renamed, costing characters and nothing else. Anything
    wrongly included is renamed consistently EVERYWHERE, so it either remains a valid program
    or fails to compile - and build_pb.py compiles the artifact and deletes it on failure.
    There is no path from a mistake here to a silently different program: generated names exist
    nowhere else in the file, so a rename that lands on a framework member can only produce
    CS1061 or CS0246, never a working call to something else.
    """
    members = set()
    for m in re.finditer(r'\b(?:class|struct)\s+([A-Za-z_]\w*)\s*\{', code):
        i, depth = m.end(), 1
        while i < len(code) and depth:
            if code[i] == '{':
                depth += 1
            elif code[i] == '}':
                depth -= 1
            i += 1
        body = code[m.end():i - 1]
        for d in _DECL.finditer(body):
            members.add(d.group(1))
        for d in re.finditer(r',\s*([A-Za-z_]\w*)\s*(?=[,;=])', body):
            members.add(d.group(1))
    return set(t for t in members
               if t not in KEYWORDS and t not in NEVER and t not in BCL_MEMBERS
               and t not in api_names and len(t) > 2)


def apply_map(code, mapping, dotted_too=False):
    """Rewrite occurrences of mapped identifiers. Never touches string literals.

    dotted_too=False is the ORIGINAL conservative pass: bare occurrences only, so a framework
    member reached through a dot can never be rewritten. dotted_too=True is used only for names
    that passed owned_members(), where the dotted occurrence is exactly what we are after.
    """
    out, last = [], 0
    for s, e, t, dotted in _tokens_outside_strings(code):
        if (dotted and not dotted_too) or t not in mapping:
            continue
        out.append(code[last:s])
        out.append(mapping[t])
        last = e
    out.append(code[last:])
    return ''.join(out)


def minify(code, stub_path=None):
    """Return (renamed_code, mapping, report). Raises AssertionError if a round trip fails."""
    fields, methods, present = collect_targets(code)
    targets = fields | methods
    mapping = build_map(targets, present)
    renamed = apply_map(code, mapping)

    # --- pass 2: members of OUR OWN types -----------------------------------
    # A SEPARATE mapping with its own round trip, so the two passes are measured and reasoned
    # about independently and a failure names which one broke.
    api = api_surface(stub_path) if stub_path else set()
    mem_targets = owned_members(renamed, api) - set(mapping.values())
    mem_map = build_map(mem_targets, set(present) | set(mapping.values()))
    before_members = len(renamed)
    renamed2 = apply_map(renamed, mem_map, dotted_too=True)
    mem_inverse = {v: k for k, v in mem_map.items()}
    assert len(mem_inverse) == len(mem_map), 'member mapping is not injective'
    if apply_map(renamed2, mem_inverse, dotted_too=True) != renamed:
        raise AssertionError('member round trip diverged')
    member_saved = before_members - len(renamed2)

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
        'members': len(mem_targets),
        'member_saved': member_saved,
    }
    mapping.update(mem_map)
    return renamed2, mapping, report
