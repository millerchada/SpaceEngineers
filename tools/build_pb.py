#!/usr/bin/env python
"""Build a Space Engineers PB deployment artifact from a maintained source file.

    python build_pb.py IO_Production_Manager_v2.4.5.cs

Writes <name>.min.cs, which is what you paste into the programmable block. The
input file stays the file of record: full comments, readable indentation.

WHY: the PB source ceiling is 100,000 characters. Comments and indentation cost
~14,000 of them and carry no runtime meaning, but deleting them from the source
would destroy hard-won documentation (see CHANGELOG.md). So we keep both: a
documented source for humans, a stripped artifact for the game.

WHAT IT REMOVES
  - // line comments (string-aware: a // inside a string literal is preserved)
  - leading indentation
  - trailing whitespace
  - runs of spaces/tabs OUTSIDE string literals, collapsed to one
  - lines that become empty

WHAT IT DELIBERATELY KEEPS
  - newlines. Removing them would save ~2,000 more characters but collapse the
    file to one line, making in-game compile errors unlocatable. Line structure
    is worth more than the characters. NOTE: in-game error line numbers refer to
    the .min.cs (plus the PB's own ~32-line generated preamble), not the source.

SAFETY: this transform is only valid because the source contains no verbatim
(@"...") strings, whose leading whitespace IS significant. The checks below
assert that, and verify every string literal survives byte-identical. If a check
fails the artifact is NOT written.
"""
import subprocess
import sys

import minify_names
import io
import os


STRUCTURAL = '{}()[];'


def here_dir():
    return os.path.dirname(os.path.abspath(__file__))


def tighten(code):
    """Remove the spaces that provably cannot be separating two tokens.

    scan() collapses runs of whitespace to ONE space but never removes that space, so the
    artifact carries a space in `if (x) {` , `int i = 0;` , `a + b` and thousands more where
    the lexer does not need one. Measured on v2.4.40 that is 6,517 characters - more than the
    newlines this tool deliberately keeps, and it costs no readability anywhere, because the
    SOURCE is untouched.

    THE SAFETY RULE, and it is the whole argument: two adjacent tokens can only merge into a
    different token if BOTH sides are word characters (`int x` -> `intx`) or BOTH sides are
    punctuation (`a - -1` -> `a--1`, `/` `/` -> `//`). So a space is removed only when exactly
    ONE side is a word character and the other is punctuation - a case in which no C# token
    pair can possibly join. Both-word and both-punctuation spaces are kept, conservatively,
    even though many of those would also be safe.

    String and char literals are copied verbatim; nothing inside them is examined or altered.
    The transform is idempotent, so the artifact remains stable under a second pass, and
    build_pb re-verifies literals, code structure and a real compile afterwards.
    """
    out = []
    i, n = 0, len(code)

    def isword(ch):
        return ch.isalnum() or ch == '_'

    while i < n:
        c = code[i]
        if c == '"' or c == "'":
            j = i + 1
            while j < n:
                if code[j] == chr(92):
                    j += 2
                    continue
                if code[j] == c:
                    j += 1
                    break
                j += 1
            out.append(code[i:j])
            i = j
            continue
        if c == ' ':
            prev = out[-1][-1] if (out and out[-1]) else chr(10)
            nxt = code[i + 1] if i + 1 < n else chr(10)
            if isword(prev) == isword(nxt):
                out.append(c)          # both words, or both punctuation: keep it
            i += 1
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def scan(src):
    """Return (stripped_text, string_literals, structural_counts).

    structural_counts tallies {}()[]; seen in CODE context only - never inside a
    string literal or a comment. Comparing that between the source and the
    rebuilt artifact is a real structural-equivalence check; comparing raw
    character counts is NOT, because comments contain brackets of their own.
    """
    out = []
    literals = []
    struct = dict((ch, 0) for ch in STRUCTURAL)
    i, n = 0, len(src)
    line = []                      # current output line, chars
    pending_space = False          # collapse runs of whitespace outside strings

    def flush_line():
        text = ''.join(line).rstrip()
        if text:
            out.append(text)
        del line[:]

    while i < n:
        c = src[i]

        if c == '\n':
            flush_line()
            pending_space = False
            i += 1
            continue

        # comment start (only outside strings, which is where we are here)
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            while i < n and src[i] != '\n':
                i += 1
            continue

        if c == '/' and i + 1 < n and src[i + 1] == '*':
            raise SystemExit('ERROR: block comment found; this tool only handles // comments')

        # string or char literal: copy verbatim, honouring escapes
        if c == '"' or c == "'":
            if c == '"' and i > 0 and src[i - 1] == '@':
                raise SystemExit('ERROR: verbatim @"..." string found; indentation is '
                                 'significant inside it and stripping would corrupt it')
            quote = c
            lit = [c]
            if pending_space and line:
                line.append(' ')
            pending_space = False
            i += 1
            while i < n:
                ch = src[i]
                lit.append(ch)
                if ch == '\\':                 # escape: consume the next char too
                    if i + 1 < n:
                        lit.append(src[i + 1])
                        i += 2
                        continue
                if ch == quote:
                    i += 1
                    break
                i += 1
            text = ''.join(lit)
            if quote == '"':
                literals.append(text)
            line.append(text)
            continue

        if c == ' ' or c == '\t':
            if line:                           # never emit leading indentation
                pending_space = True
            i += 1
            continue

        if pending_space:
            line.append(' ')
            pending_space = False
        if c in struct:
            struct[c] += 1
        line.append(c)
        i += 1

    flush_line()
    return '\n'.join(out) + '\n', literals, struct


def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    src_path = sys.argv[1]
    # ENCODING IS PINNED. Sources contain non-ASCII (em dashes in comments) and were
    # written as UTF-8, but the locale default here is cp1252 - so an unpinned read
    # decodes them differently from check_pb.py, which always reads UTF-8. Today that is
    # harmless only because comments never reach the artifact; pin both so it stays that
    # way rather than depending on it.
    with io.open(src_path, 'r', encoding='utf-8') as f:
        src = f.read()

    out, src_lits, src_struct = scan(src)

    # --- identifier shortening -------------------------------------------------
    # Renames only OUR private fields and method names, never a dotted member and never
    # anything inside a string literal. minify_names verifies the transform is a bijection by
    # applying the inverse mapping and requiring the original back byte for byte; the literal
    # and structure checks below then re-verify against the ORIGINAL source, and check_pb.py
    # compiles the result. See minify_names.py for why each exclusion exists.
    renamed, mapping, mreport = minify_names.minify(
        out, stub_path=os.path.join(here_dir(), 'se_stubs.cs'))
    out = renamed

    # --- space tightening ------------------------------------------------------
    # A preprocessor directive is line-oriented and would be corrupted by anything that moves
    # its tokens around. There are none in this project and there is no reason to add one, but
    # a transform that would silently break them must say so rather than find out in the game.
    for ln in out.split(chr(10)):
        if ln.lstrip().startswith('#'):
            raise SystemExit('ERROR: preprocessor directive found (%r); tighten() is not safe '
                             'for line-oriented directives' % ln[:40])
    before_tighten = len(out)
    out = tighten(out)
    tightened = before_tighten - len(out)

    # --- verification: refuse to write a suspect artifact ---------------------
    problems = []

    # Re-scan the ARTIFACT. Its string literals and its code-context structure
    # must match the source's exactly: that proves no quote was mangled and no
    # code was dropped. (Comparing RAW character counts would fail spuriously,
    # because comments contain brackets and semicolons of their own.)
    reout, out_lits, out_struct = scan(out)
    if src_lits != out_lits:
        problems.append('string literals changed: %d -> %d'
                        % (len(src_lits), len(out_lits)))
        for a, b in zip(src_lits, out_lits):
            if a != b:
                problems.append('  first difference: %r vs %r' % (a[:60], b[:60]))
                break
    for ch in STRUCTURAL:
        if src_struct[ch] != out_struct[ch]:
            problems.append("code-context '%s' changed: %d -> %d"
                            % (ch, src_struct[ch], out_struct[ch]))
    if reout != out:
        problems.append('artifact is not stable under a second pass')

    print('source   %7d chars' % len(src))
    print('artifact %7d chars   (saved %d, %.1f%%)'
          % (len(out), len(src) - len(out), 100.0 * (len(src) - len(out)) / len(src)))
    print('string literals preserved: %d' % len(out_lits))
    print('code structure: ' + '  '.join('%s=%d' % (c, src_struct[c]) for c in STRUCTURAL))
    print('identifiers shortened: %d fields + %d methods, saved %d chars'
          % (mreport['fields'], mreport['methods'], mreport['saved']))
    print('own-type members shortened: %d names, saved %d chars'
          % (mreport['members'], mreport['member_saved']))
    print('spaces tightened: saved %d chars' % tightened)
    print('PB ceiling headroom: %d chars' % (100000 - len(out)))

    if problems:
        print('\nFAILED - artifact NOT written:')
        for p in problems:
            print('  - ' + p)
        return 1

    base, ext = os.path.splitext(src_path)
    dst = base + '.min' + ext
    with io.open(dst, 'w', encoding='utf-8', newline='') as f:
        f.write(out)

    # --- COMPILE GATE ----------------------------------------------------------
    # The ARTIFACT is what gets pasted, so the artifact is what must compile - not merely the
    # source it came from. This also catches a bad rename, which is exactly how the QueueLoad /
    # ServiceLoadouts collision in minify_names was found. Without this the transform would be
    # "verified" only in the sense that brace counts matched, which is the weaker claim that
    # let two compile errors reach the game.
    here = os.path.dirname(os.path.abspath(__file__))
    check = os.path.join(here, 'check_pb.py')
    if os.path.exists(check):
        r = subprocess.run([sys.executable, check, dst], capture_output=True, text=True)
        if r.returncode != 0:
            os.remove(dst)
            print('\nFAILED - artifact did NOT compile; it has been deleted:')
            print(r.stdout + r.stderr)
            return 1
        print('compile gate: artifact compiles clean')
    else:
        print('WARNING: check_pb.py not found - artifact NOT compile-checked')

    print('\nverification passed -> %s' % dst)
    if len(out) >= 100000:
        print('WARNING: artifact still exceeds the 100,000-character PB ceiling')
    return 0


if __name__ == '__main__':
    sys.exit(main())
