"""Safety suite for the artifact TRANSFORM - the identifier passes and space tightening.

    python IO_Production_Manager/tests/tests_minify.py \
           IO_Production_Manager/IO_Production_Manager_vX.Y.Z.cs

build_pb.py already proves a lot about a built artifact: string literals survive byte-identical,
code-context {}()[]; counts match, the output is stable under a second pass, and the artifact
compiles. What it cannot prove on its own is that the transform is safe *by construction* -
that it refuses the things it should refuse rather than happening not to hit them today.

So this suite is mostly NEGATIVE CONTROLS. It feeds the transform inputs that a broken version
would mangle and requires it to leave them alone. The one that matters most:

    a framework member must be unrenameable EVEN IF one of our own classes declares the
    same name

because that is the single assumption the owned-member pass rests on, and "we happen not to
have a field called Count" is luck, not a guarantee.
"""
import io
import os
import re
import sys

PROJ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = os.path.dirname(PROJ)
sys.path.insert(0, os.path.join(REPO, 'tools'))

import build_pb          # noqa: E402
import minify_names      # noqa: E402

STUBS = os.path.join(REPO, 'tools', 'se_stubs.cs')


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else None
    if not target:
        print(__doc__)
        return 2
    src = io.open(target, encoding='utf-8').read()
    stripped, src_lits, src_struct = build_pb.scan(src)

    fails = []
    n = [0]

    def check(name, ok, detail=''):
        n[0] += 1
        print('  %-58s %s' % (name, 'PASS' if ok else 'FAIL'))
        if not ok:
            fails.append(name + ((' - ' + detail) if detail else ''))

    api = minify_names.api_surface(STUBS)

    print('-- the ownership rule refuses the API surface')
    check('se_stubs.cs yields a non-trivial API surface', len(api) > 80, str(len(api)))
    for m in ('CustomName', 'CustomData', 'EntityId', 'Enabled', 'IsWorking', 'GetInventory',
              'AddQueueItem', 'SendMessage', 'UseAntenna', 'BroadcastTarget', 'TryGet',
              'CurrentVolume', 'MaxVolume', 'Components'):
        check('API member refused: %s' % m, m in api or m in minify_names.BCL_MEMBERS)

    print('-- NEGATIVE CONTROL: our own class declaring an API name must not unlock it')
    # This is the case that luck currently hides: IOPM has no field called CustomName. Inject
    # one and require the pass to still refuse the name, which proves the exclusion is doing
    # the work rather than the absence of a collision.
    poisoned = stripped + chr(10) + 'class QzPoison { public string CustomName; public int Count; public long EntityId; }' + chr(10)
    owned = minify_names.owned_members(poisoned, api)
    for m in ('CustomName', 'EntityId'):
        check('declared-by-us but API-owned, still refused: %s' % m, m not in owned)
    check('declared-by-us but BCL-owned, still refused: Count', 'Count' not in owned)

    print('-- NEGATIVE CONTROL: only DIRECT member declarations are owned')
    # The ownership rule says a rename target must be a MEMBER DECLARATION of a type this file
    # defines. A regex sweeping the whole class body satisfies that only while our helper types
    # stay implementation-free; the moment one grows a method, its parameters and locals start
    # looking like members. This is that class, written out in full so the parser has to earn
    # the distinction structurally rather than by luck.
    helper = (
        'class QzHelper {' + chr(10) +
        'public int RealMember, SecondMember;' + chr(10) +
        'public Dictionary<string, LQ> Table = new Dictionary<string, LQ>(SCI);' + chr(10) +
        'public int Sat, Short, NoOp;' + chr(10) +
        'public bool Derived { get { return Inner.Count == 1; } }' + chr(10) +
        'public void Test(int parameter) {' + chr(10) +
        'int localA = 1, localB = 2;' + chr(10) +
        'Foo(localA, localB);' + chr(10) +
        '}' + chr(10) + '}')
    own = minify_names.owned_members(helper, api)
    for want in ('RealMember', 'SecondMember', 'Sat', 'NoOp', 'Table', 'Test', 'Derived'):
        check('direct member declaration is owned: %s' % want, want in own, str(sorted(own)))
    for bad, why in (('parameter', 'a method parameter'),
                     ('localA', 'a local'),
                     ('localB', 'a second local in the same statement'),
                     ('Foo', 'a called method'),
                     ('Inner', 'an identifier inside a property body')):
        check('NOT owned - %s: %s' % (why, bad), bad not in own, str(sorted(own)))
    check('multi-field declarations are still found whole',
          {'Sat', 'Short', 'NoOp'} <= own or 'Short' in minify_names.BCL_MEMBERS,
          str(sorted(own)))

    print('-- NEGATIVE CONTROL: no framework member is renamed in the real artifact')
    renamed, mapping, report = minify_names.minify(stripped, stub_path=STUBS)
    for m in ('CustomName', 'CustomData', 'EntityId', 'IsWorking', 'AddQueueItem',
              'TryGetValue', 'Count', 'Append', 'GetInventory', 'SendMessage', 'Components',
              'IsSameConstructAs', 'CurrentVolume', 'MaxVolume', 'Enabled'):
        check('not in the rename map: %s' % m, m not in mapping)
    # ... and any API name the SOURCE actually uses must still be physically present in the
    # output, i.e. not rewritten by some other route. Conditioned on the source so the suite
    # stays version-agnostic: SendMessage and GetBlockWithId arrive in 2.4.40, and a check that
    # silently passes on an older file because the name was never there proves nothing.
    for m in ('CustomName', 'AddQueueItem', 'SendMessage', 'GetBlockWithId', 'TryGet',
              'IsSameConstructAs', 'GetBlockWithName', 'CustomData'):
        if m in stripped:
            check('used by the source, still in the artifact: %s' % m, m in renamed)

    print('-- the transform is a bijection and is deterministic')
    again, mapping2, _ = minify_names.minify(stripped, stub_path=STUBS)
    check('two runs produce identical output', again == renamed)
    check('two runs produce the identical mapping', mapping2 == mapping)
    check('generated names collide with nothing pre-existing',
          not (set(mapping.values()) & set(mapping.keys())))
    check('the mapping is injective', len(set(mapping.values())) == len(mapping))

    print('-- space tightening cannot merge two tokens')
    T = build_pb.tighten
    cases = [
        ('int x = 0;', 'int x=0;', 'a declaration keeps its one required space'),
        ('a - -1', 'a- -1', 'minus minus is never joined into --'),
        ('a + +1', 'a+ +1', 'plus plus is never joined into ++'),
        ('x / / y', 'x/ /y', 'two slashes are never joined into a comment'),
        ('return new List<int>();', 'return new List<int>();', 'keywords keep their spaces'),
        ('if (a) { b(); }', 'if(a) {b(); }', 'word/punct spaces go, punct/punct stay'),
        ('foreach (var kv in d) x += kv.Value;', 'foreach(var kv in d)x+=kv.Value;',
         'a realistic statement'),
    ]
    for src_s, want, why in cases:
        check(why, T(src_s) == want, '%r -> %r, wanted %r' % (src_s, T(src_s), want))
    check('tightening is idempotent', T(T(stripped)) == T(stripped))
    # The three cases above are all the CONSERVATIVE branch: `) {`, `; }` and `= "` are
    # punctuation on both sides, so the space stays even though it is provably removable.
    # Roughly 1,500 characters are left on the table by that choice, deliberately.
    check('punctuation-to-punctuation spaces are deliberately kept',
          T('a ) { b') == 'a) {b')

    print('-- string literals are inert under every pass')
    lit = 'string s = "  a  +  b  "; string t = " int x ";'
    check('spaces inside a literal survive tightening',
          T(lit) == 'string s= "  a  +  b  ";string t= " int x ";', repr(T(lit)))
    _, lits_after, struct_after = build_pb.scan(T(renamed))
    check('literals identical after rename + tighten', lits_after == src_lits,
          '%d vs %d' % (len(lits_after), len(src_lits)))
    check('code structure identical after rename + tighten',
          all(struct_after[c] == src_struct[c] for c in build_pb.STRUCTURAL))

    print('-- the member pass actually did something')
    check('owned members were found and renamed', report['members'] > 20, str(report['members']))
    check('and it saved characters', report['member_saved'] > 500, str(report['member_saved']))

    print()
    if fails:
        print('%d of %d TRANSFORM CHECKS FAILED' % (len(fails), n[0]))
        for f in fails:
            print('  - ' + f)
        return 1
    print('ALL %d TRANSFORM CHECKS PASSED' % n[0])
    return 0


if __name__ == '__main__':
    sys.exit(main())
