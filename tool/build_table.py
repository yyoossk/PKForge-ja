#!/usr/bin/env python3
"""ja/strings.json（C# ソース表記のキー）から、アプリに埋め込む実行時翻訳テーブルを作る。

出力形式（UTF-8、1 行 1 件、タブ区切り）:
  E <原文> <訳文>                 完全一致
  T <原文テンプレート> <訳文テンプレート> <複数形の穴>   {0} {1} … が可変部分
文字列中の \\ \n \t \r はエスケープする。
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HOLE = re.compile(r'(?<!\{)\{(\d+)(?:,-?\d+)?(?::[^{}]*)?\}(?!\})')
NAMED = re.compile(r'(?<!\{)\{([A-Za-z_]\w*)\}(?!\})')


def unescape_cs(s: str) -> str:
    out, i = [], 0
    simple = {'n': '\n', 't': '\t', 'r': '\r', '0': '\0', '"': '"', "'": "'", '\\': '\\',
              'a': '\a', 'b': '\b', 'f': '\f', 'v': '\v'}
    while i < len(s):
        c = s[i]
        if c == '\\' and i + 1 < len(s):
            d = s[i + 1]
            if d in simple:
                out.append(simple[d]); i += 2; continue
            if d == 'u' and re.fullmatch(r'[0-9a-fA-F]{4}', s[i + 2:i + 6]):
                out.append(chr(int(s[i + 2:i + 6], 16))); i += 6; continue
            if d == 'x':
                m = re.match(r'[0-9a-fA-F]{1,4}', s[i + 2:])
                if m:
                    out.append(chr(int(m.group(0), 16))); i += 2 + len(m.group(0)); continue
        out.append(c)
        i += 1
    return ''.join(out)


def normalize(key: str, value: str):
    """穴を {0} {1}… にそろえ、エスケープを戻す。名前付きの {name} も穴として扱う。"""
    names: dict[str, int] = {}
    base = [int(m.group(1)) for m in HOLE.finditer(key)]
    nxt = (max(base) + 1) if base else 0
    for m in NAMED.finditer(key):
        if m.group(1) not in names:
            names[m.group(1)] = nxt
            nxt += 1

    def conv(s: str) -> str:
        s = HOLE.sub(lambda m: '\x01%s\x02' % m.group(1), s)
        s = NAMED.sub(lambda m: '\x01%d\x02' % names[m.group(1)] if m.group(1) in names else m.group(0), s)
        s = unescape_cs(s).replace('{{', '{').replace('}}', '}')
        return s.replace('\x01', '{').replace('\x02', '}')
    return conv(key), conv(value), nxt


def esc(s: str) -> str:
    return s.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t').replace('\r', '\\r')


def build(strings: dict) -> list[str]:
    lines, seen = [], set()
    for k, v in sorted(strings.items(), key=lambda kv: (-len(kv[0]), kv[0])):
        if not v or v == k:
            continue
        key, val, holes = normalize(k, v)
        if key == val or key in seen:
            continue
        seen.add(key)
        hole_ids = [int(x) for x in re.findall(r'\{(\d+)\}', key)]
        if not hole_ids:
            lines.append(f'E\t{esc(key)}\t{esc(val)}')
            continue
        # 原文の穴は 0,1,2… の順に並んでいる必要がある（並んでいなければ並べ替える）
        order = []
        for h in hole_ids:
            if h not in order:
                order.append(h)
        if len(order) != len(hole_ids):
            continue  # 同じ穴が 2 回出る原文は扱わない
        remap = {old: new for new, old in enumerate(order)}
        key = re.sub(r'\{(\d+)\}', lambda m: '{%d}' % remap[int(m.group(1))], key)
        val = re.sub(r'\{(\d+)\}', lambda m: '{%d}' % remap.get(int(m.group(1)), 99), val)
        if '{99}' in val:
            continue
        lits = re.split(r'\{\d+\}', key)
        if not any(re.search(r'[A-Za-z]', x) for x in lits):
            continue  # 「{0} · {1}」のような記号だけの型は訳す意味がない
        if any(x == '' for x in lits[1:-1]):
            continue  # 穴が連続すると境界が決まらない
        plural = []
        for i, h in enumerate(re.finditer(r'\{(\d+)\}', key)):
            before = key[h.start() - 1] if h.start() > 0 else ''
            after = key[h.end()] if h.end() < len(key) else ''
            if before.isalpha() and before.isascii() and not (after.isalpha() and after.isascii()):
                plural.append(str(i))
        lines.append(f'T\t{esc(key)}\t{esc(val)}\t{",".join(plural)}')
    return lines


def main():
    src = ROOT / 'ja' / 'strings.json'
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / 'windows' / 'Core' / 'ui-table.txt'
    strings = json.loads(src.read_text(encoding='utf-8'))
    lines = build(strings)
    out.write_text('\n'.join(lines) + '\n', encoding='utf-8')
    e = sum(1 for x in lines if x.startswith('E'))
    print(f'{out}: 完全一致 {e} 件 / テンプレート {len(lines) - e} 件')


if __name__ == '__main__':
    main()
