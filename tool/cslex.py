"""Minimal C# lexer that finds string literals (regular, verbatim, interpolated,
raw, u8) and their interpolation holes, skipping comments and char literals.

Only what the localizer needs: exact source spans, so literals can be rewritten
in place without touching anything else.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field



@dataclass
class Lit:
    start: int                 # offset of first prefix char ($ / @ / ")
    end: int                   # offset just after closing quote (and u8 suffix)
    kind: str                  # regular | verbatim | raw
    interp: bool
    open_len: int              # length of prefix + opening quote(s)
    close_len: int             # length of closing quote(s) + suffix
    parts: list = field(default_factory=list)   # ('text', s, e) | ('hole', s, e)
    u8: bool = False
    children: list = field(default_factory=list)  # literals nested in holes
    line: int = 0

    def key(self, src: str) -> str:
        out, n = [], 0
        for kind, s, e in self.parts:
            if kind == 'text':
                out.append(src[s:e])
            else:
                out.append('{%d}' % n)
                n += 1
        return ''.join(out)

    def holes(self, src: str) -> list[str]:
        return [src[s:e] for k, s, e in self.parts if k == 'hole']

    def visible(self, src: str) -> str:
        return ''.join(src[s:e] for k, s, e in self.parts if k == 'text')


class LexError(Exception):
    pass


class Lexer:
    def __init__(self, src: str):
        self.src = src
        self.n = len(src)

    # ---- public -------------------------------------------------------
    def literals(self) -> list[Lit]:
        """Top-level literals (nested ones hang off .children)."""
        out: list[Lit] = []
        self._code(0, out, hole=False)
        line_starts = [0] + [m.end() for m in re.finditer('\n', self.src)]
        import bisect

        def number(l: Lit):
            l.line = bisect.bisect_right(line_starts, l.start)
            for c in l.children:
                number(c)
        for l in out:
            number(l)
        return out

    # ---- scanning -----------------------------------------------------
    def _code(self, i: int, out: list, hole: bool, braces: int = 1) -> int:
        """Scan code from i. In hole mode stop at the '}' closing the hole
        (returns its index) or at a top-level ':' format spec."""
        src, n = self.src, self.n
        depth = 0
        while i < n:
            c = src[i]
            if c == '/' and i + 1 < n and src[i + 1] == '/':
                j = src.find('\n', i)
                i = n if j < 0 else j
                continue
            if c == '/' and i + 1 < n and src[i + 1] == '*':
                j = src.find('*/', i + 2)
                if j < 0:
                    raise LexError('unterminated comment')
                i = j + 2
                continue
            if c == "'":
                i = self._char(i)
                continue
            if c in '$@"':
                lit = self._try_string(i)
                if lit is not None:
                    out.append(lit)
                    i = lit.end
                    continue
            if c == '#' and self._at_line_start(i):
                j = src.find('\n', i)
                i = n if j < 0 else j
                continue
            if hole:
                if c in '([{':
                    depth += 1
                elif c in ')]':
                    depth -= 1
                elif c == '}':
                    if depth == 0:
                        return i
                    depth -= 1
                elif c == ':' and depth == 0:
                    if i + 1 < n and src[i + 1] == ':':
                        i += 2
                        continue
                    # format spec until closing brace
                    j = src.find('}' * braces, i)
                    if j < 0:
                        raise LexError('unterminated format spec')
                    return j
            i += 1
        if hole:
            raise LexError('unterminated interpolation hole')
        return i

    def _at_line_start(self, i: int) -> bool:
        j = i - 1
        while j >= 0 and self.src[j] in ' \t':
            j -= 1
        return j < 0 or self.src[j] == '\n'

    def _char(self, i: int) -> int:
        src = self.src
        j = i + 1
        if j < self.n and src[j] == '\\':
            j += 2
        else:
            j += 1
        while j < self.n and src[j] != "'":
            if src[j] == '\n':      # not a char literal after all
                return i + 1
            j += 1
        return j + 1

    def _try_string(self, i: int) -> Lit | None:
        src = self.src
        # '@' / '$' must not continue an identifier
        if i > 0 and (src[i - 1].isalnum() or src[i - 1] == '_') and src[i] != '"':
            return None
        m = re.match(r'(\$+@?|@\$+|@)?(""")?', src[i:i + 8])
        prefix = m.group(1) or ''
        q = i + len(prefix)
        if q >= self.n or src[q] != '"':
            return None
        dollars = prefix.count('$')
        verbatim = '@' in prefix
        interp = dollars > 0
        if src.startswith('"""', q):
            return self._raw(i, q, dollars)
        lit = Lit(i, 0, 'verbatim' if verbatim else 'regular', interp,
                  len(prefix) + 1, 1)
        j = q + 1
        text_start = j
        while True:
            if j >= self.n:
                raise LexError('unterminated string at %d' % i)
            c = src[j]
            if verbatim:
                if c == '"':
                    if j + 1 < self.n and src[j + 1] == '"':
                        j += 2
                        continue
                    break
            else:
                if c == '\\':
                    j += 2
                    continue
                if c == '"':
                    break
                if c == '\n':
                    raise LexError('newline in string at %d' % i)
            if interp and c == '{':
                if src.startswith('{{', j):
                    j += 2
                    continue
                lit.parts.append(('text', text_start, j))
                hs = j + 1
                he = self._code(hs, lit.children, hole=True)
                lit.parts.append(('hole', hs, he))
                j = he + 1
                text_start = j
                continue
            if interp and c == '}' and src.startswith('}}', j):
                j += 2
                continue
            j += 1
        lit.parts.append(('text', text_start, j))
        end = j + 1
        if src.startswith('u8', end) or src.startswith('U8', end):
            lit.u8 = True
            end += 2
            lit.close_len += 2
        lit.end = end
        lit.parts = [p for p in lit.parts if not (p[0] == 'text' and p[1] == p[2])] or [('text', text_start, text_start)]
        return lit

    def _raw(self, i: int, q: int, dollars: int) -> Lit:
        src = self.src
        k = q
        while k < self.n and src[k] == '"':
            k += 1
        nq = k - q
        close = src.find('"' * nq, k)
        if close < 0:
            raise LexError('unterminated raw string')
        lit = Lit(i, close + nq, 'raw', dollars > 0, k - i, nq)
        lit.parts = [('text', k, close)]
        return lit
