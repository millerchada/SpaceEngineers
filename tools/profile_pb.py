"""Where do the artifact's characters actually live?

    python tools/profile_pb.py IO_Production_Manager/IO_Production_Manager_vX.Y.Z.cs

Reclamation work without a profile is guesswork, and guesswork here has a bad record: the
obvious-looking targets (comments, indentation) were already taken years ago, and the
remaining ones are not where intuition puts them. This measures the built artifact and splits
every character into exactly one bucket, so the buckets sum to the file size and a claimed
saving can be checked against a category that actually exists.

Buckets, in code context only (string contents are their own bucket):

  string literals   every character between quotes, quotes included. DATA - item aliases,
                    [Stock] keys, diagnostic text. Untouchable by any safe transform.
  identifiers       names. What minify_names.py works on; the report splits them into the
                    ones it renamed (already short) and the ones it refused.
  keywords          C# reserved words.
  numbers           numeric literals.
  newlines          one per line. build_pb.py keeps these deliberately for error locating.
  punctuation       {}()[];,.<>+-*/=!&|?:% and friends.
  spaces            the single spaces build_pb.py could not collapse further.
"""
import io
import os
import re
import subprocess
import sys

KEYWORDS = set('''abstract as base bool break byte case catch char checked class const continue
decimal default delegate do double else enum event explicit extern false finally fixed float
for foreach get goto if implicit in int interface internal is lock long namespace new null
object operator out override params private protected public readonly ref return sbyte sealed
set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong
unchecked unsafe ushort using value virtual void volatile while var yield partial where'''.split())


def profile(code):
    b = dict(strings=0, identifiers=0, keywords=0, numbers=0, newlines=0,
             punctuation=0, spaces=0)
    idents = {}
    i, n = 0, len(code)
    while i < n:
        c = code[i]
        if c == '\n':
            b['newlines'] += 1
            i += 1
        elif c == '"' or c == "'":
            q, j = c, i + 1
            while j < n:
                if code[j] == chr(92):
                    j += 2
                    continue
                if code[j] == q:
                    j += 1
                    break
                j += 1
            b['strings'] += j - i
            i = j
        elif c.isalpha() or c == '_':
            j = i
            while j < n and (code[j].isalnum() or code[j] == '_'):
                j += 1
            tok = code[i:j]
            if tok in KEYWORDS:
                b['keywords'] += j - i
            else:
                b['identifiers'] += j - i
                idents[tok] = idents.get(tok, 0) + 1
            i = j
        elif c.isdigit():
            j = i
            while j < n and (code[j].isdigit() or code[j] in '.eEfdmxXabcdefABCDEF'):
                j += 1
            b['numbers'] += j - i
            i = j
        elif c == ' ' or c == '\t':
            b['spaces'] += 1
            i += 1
        else:
            b['punctuation'] += 1
            i += 1
    return b, idents


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    here = os.path.dirname(os.path.abspath(__file__))
    src = sys.argv[1]
    art = os.path.splitext(src)[0] + '.min' + os.path.splitext(src)[1]
    if not os.path.exists(art):
        r = subprocess.run([sys.executable, os.path.join(here, 'build_pb.py'), src],
                           capture_output=True, text=True)
        if not os.path.exists(art):
            print('could not build the artifact:')
            print(r.stdout + r.stderr)
            return 2
    code = io.open(art, encoding='utf-8', newline='').read()
    b, idents = profile(code)
    total = sum(b.values())
    print('artifact: %s' % os.path.basename(art))
    print('measured: %d chars   (file is %d - %s)'
          % (total, len(code), 'MATCH' if total == len(code) else 'MISMATCH, buckets are wrong'))
    print()
    print('%-16s %9s %7s' % ('bucket', 'chars', 'share'))
    print('-' * 34)
    for k in sorted(b, key=lambda x: -b[x]):
        print('%-16s %9d %6.1f%%' % (k, b[k], 100.0 * b[k] / total))
    print()

    # Identifiers, split by whether the existing minifier already reached them. A name of two
    # characters starting with Q is one it generated.
    short = sum(l * c for l, c in
                ((len(t), n) for t, n in idents.items() if re.match(r'^Q[A-Za-z]?$', t)))
    longn = b['identifiers'] - short
    print('identifiers already shortened by minify_names : %d chars' % short)
    print('identifiers it left alone                     : %d chars' % longn)
    print()
    print('20 most expensive identifiers it left alone (chars = length x uses):')
    rank = sorted(((len(t) * c, t, c) for t, c in idents.items()
                   if not re.match(r'^Q[A-Za-z]?$', t)), reverse=True)
    for cost, tok, uses in rank[:20]:
        print('  %6d  %-34s x%d' % (cost, tok, uses))
    print()
    lines = code.count('\n')
    print('newline budget: %d lines. Packing to an 200-char line width would leave roughly'
          % lines)
    packed = 0
    width = 0
    for ln in code.split('\n'):
        if width and width + len(ln) + 1 > 200:
            packed += 1
            width = 0
        width += len(ln) + 1
    print('                %d lines, recovering about %d chars.' % (packed + 1, lines - packed - 1))
    return 0


if __name__ == '__main__':
    sys.exit(main())
