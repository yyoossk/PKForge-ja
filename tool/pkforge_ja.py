#!/usr/bin/env python3
"""PKForge 日本語化パッチャー

  extract  : 本家ソースから UI 文字列を抽出して未翻訳一覧を作る
  apply    : 辞書 (ja/strings.json) とコードパッチ (ja/config.json) をソースに当てる
  translate: 未翻訳の文字列を Claude API で自動翻訳して辞書に追記する (任意)

辞書のキーは C# ソース上の文字列そのもの（エスケープもそのまま）。
補間文字列 $"..." は穴を {0} {1} … に置き換えたものがキー。
訳文でも {0} などをそのまま残せば、語順を入れ替えてもよい。
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys
import urllib.request
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cslex import Lexer, Lit  # noqa: E402

HERE = Path(__file__).resolve().parent
JA_DIR = HERE.parent / 'ja'

# ---------------------------------------------------------------------------
# 文字列の選別
# ---------------------------------------------------------------------------
_WORD = r"[^\W\d_]"
_SINGLE_OK = re.compile(rf"(?:[A-Z]{_WORD}*(?:['’-]{_WORD}+)*|[A-Z]{{1,4}}s?)[.!?:…]*")
_HAS_SPACE = re.compile(r"\s")
_ESC = re.compile(r"\\(?:u[0-9a-fA-F]{4}|x[0-9a-fA-F]{1,4}|.)")
_FMT = re.compile(r"(?<!\{)\{\d+(?:,-?\d+)?(?::[^}]*)?\}(?!\})")


def plain(text: str) -> str:
    """表示上の文字（エスケープ・書式指定を除いたもの）"""
    t = _ESC.sub(' ', text)
    t = _FMT.sub(' ', t)
    return t


def is_candidate(lit: Lit, src: str) -> bool:
    if lit.u8 or lit.kind == 'raw':
        return False
    vis = plain(lit.visible(src)).strip()
    if len(re.findall(r'[A-Za-z]', vis)) < 2:
        return False
    if re.search(r'://|\.(png|jpg|json|ttf|zip|apk|tsv|gz|txt|bin|sav|pk\d|xml|html)\b', vis, re.I):
        return False
    if _HAS_SPACE.search(vis):
        # コードっぽいもの（書式文字列・正規表現）を除外
        if re.fullmatch(r'[\w\s.:/\\\-]+', vis) and re.search(r'(yyyy|MM|dd|HH|mm|ss)', vis):
            return False
        if re.search(r'[\\^$]\w|\(\?', lit.visible(src)) and not re.search(r'[a-z]{3,} [a-z]{3,}', vis):
            return False
        return True
    return bool(_SINGLE_OK.fullmatch(vis))


# ---------------------------------------------------------------------------
# 文脈判定（比較・キー用途の文字列は翻訳しない）
# ---------------------------------------------------------------------------
# 外部データ（設定キー・JSON・ファイル名・辞書キーなど）として使われる → 全箇所で翻訳禁止
_EXT_BEFORE = [
    re.compile(r'\b(nameof|OpenAppPackageFileAsync|FromFile|FromResource|Combine|GetStrings|'
               r'Parse|TryParse|IsMatch|Match|Matches|Regex|GetProperty|TryGetProperty|TryGetValue|'
               r'GetString|GetInt32|GetBoolean|WriteString|WritePropertyName|WriteNumber|WriteBoolean|'
               r'Remove|GoToAsync|RegisterRoute|Get|Set|ContainsKey|GetEnvironmentVariable|ParseAdd|'
               r'Replace|Split|Trim|TrimStart|TrimEnd|Headers\.Add|GetManifestResourceStream|'
               r'Preferences\.\w+|SecureStorage\.\w+|JsonPropertyName)\s*\(\s*$'),
    re.compile(r'[\w)\]]\s*\[\s*$'),                       # indexer
    re.compile(r'^\s*\[\s*[\w.]+\s*\('),                     # attribute
    re.compile(r'\b\w*(Key|Keys|Url|Uri|Path|FileName|File|Route|Id|Prefix|Suffix|Extension|Folder|Directory|'
               r'Channel|Action|ActionName|Scheme|Host|Mime|ContentType|Tag|Magic|Header)\s*=\s*$'),
]
_EXT_AFTER = [
    re.compile(r'^\s*\]\s*='),                              # ["key"] = value
]
# UI の選択肢と比較している可能性が高い → 定義側と同じ訳にそろえる（外部データと一致する場合のみ禁止）
_CMP_BEFORE = [
    re.compile(r'\bcase\s*$'),
    re.compile(r'(==|!=)\s*$'),
    re.compile(r'\b(is|or|and|not)\s*$'),
    re.compile(r'\b(Equals|StartsWith|EndsWith|Contains|IndexOf|LastIndexOf)\s*\(\s*$'),
]
_CMP_AFTER = [
    re.compile(r'^\s*(==|!=)'),
    re.compile(r'^\s*=>'),                                  # switch pattern
    re.compile(r'^\s*\.(Equals|StartsWith|EndsWith|Contains)\('),
]
_SKIP_BEFORE = [
    re.compile(r'\b(Debug|Trace|Log|Logger|Console|Diagnostics|App)\.(\w+\.)?(Write\w*|Log\w*|Trace|Fail|Assert)\(\s*$'),
    re.compile(r'\b(LogInformation|LogWarning|LogError|LogDebug)\(\s*$'),
]


def context(src: str, lit: Lit) -> tuple[str, str]:
    ls = src.rfind('\n', 0, lit.start) + 1
    le = src.find('\n', lit.end)
    le = len(src) if le < 0 else le
    return src[ls:lit.start], src[lit.end:le]


def classify(src: str, lit: Lit) -> str:
    """'ok' 表示用 / 'cmp' 比較 / 'ext' 外部キー / 'skip' ログなど"""
    before, after = context(src, lit)
    for rx in _EXT_AFTER:
        if rx.search(after):
            return 'ext'
    for rx in _EXT_BEFORE:
        if rx.search(before):
            return 'ext'
    if re.search(r'\bcase\s*$', before):
        return 'cmp'
    for rx in _CMP_AFTER:
        if rx.search(after):
            return 'cmp'
    m = re.search(r'\b(StartsWith|EndsWith|Contains|IndexOf|LastIndexOf)\s*\(\s*$', before)
    if m:
        return {'StartsWith': 'pre', 'EndsWith': 'suf'}.get(m.group(1), 'sub')
    for rx in _CMP_BEFORE:
        if rx.search(before):
            return 'cmp'
    for rx in _SKIP_BEFORE:
        if rx.search(before):
            return 'skip'
    return 'ok'


def data_words(root: Path) -> set[str]:
    """同梱データや PKHeX の英語テキストに含まれる語（比較相手が外部データかもしれないもの）"""
    import gzip
    words: set[str] = set()
    for f in glob.glob(str(root / 'external/PKHeX/PKHeX.Core/Resources/text/**/*_en*.txt'), recursive=True):
        for line in Path(f).read_text(encoding='utf-8', errors='ignore').splitlines():
            words.add(line.strip())
            if '\t' in line:
                words.update(x.strip() for x in line.split('\t'))
    for f in glob.glob(str(root / 'src/**/Resources/**/*'), recursive=True):
        fp = Path(f)
        if not fp.is_file() or fp.suffix.lower() in ('.png', '.ttf', '.jpg', '.webp'):
            continue
        data = fp.read_bytes()
        if fp.suffix == '.gz':
            try:
                data = gzip.decompress(data)
            except OSError:
                continue
        for run in re.findall(rb'[\x20-\x7e]{3,}', data):
            t = run.decode('ascii')
            for piece in re.split(r'[\t,;|"]', t):
                piece = piece.strip()
                if piece:
                    words.add(piece)
                    words.add(piece[1:])
    return words


# ---------------------------------------------------------------------------
# ソース走査
# ---------------------------------------------------------------------------
def source_files(root: Path, cfg: dict) -> list[Path]:
    files: set[Path] = set()
    for pat in cfg['include']:
        files.update(Path(p) for p in glob.glob(str(root / pat), recursive=True))
    out = []
    for f in sorted(files):
        rel = f.relative_to(root).as_posix()
        if any(Path(rel).match(x) or glob.fnmatch.fnmatch(rel, x) for x in cfg['exclude']):
            continue
        if '/obj/' in rel or '/bin/' in rel:
            continue
        out.append(f)
    return out


def read(f: Path) -> tuple[str, bool]:
    raw = f.read_bytes()
    bom = raw.startswith(b'\xef\xbb\xbf')
    return raw.decode('utf-8-sig'), bom


def write(f: Path, text: str, bom: bool):
    f.write_bytes((b'\xef\xbb\xbf' if bom else b'') + text.encode('utf-8'))


def walk(lits):
    for l in lits:
        yield l
        yield from walk(l.children)


def scan(root: Path, cfg: dict):
    """全文字列を走査し、キー→出現箇所 と 翻訳禁止キー集合 を返す"""
    occ: dict[str, list] = defaultdict(list)
    ctx: dict[str, set] = defaultdict(set)
    parsed = {}
    for f in source_files(root, cfg):
        src, bom = read(f)
        lits = Lexer(src).literals()
        parsed[f] = (src, bom, lits)
        rel = f.relative_to(root).as_posix()
        for lit in walk(lits):
            if lit.u8 or lit.kind == 'raw':
                continue
            key = lit.key(src)
            c = classify(src, lit)
            ctx[key].add(c)
            if is_candidate(lit, src):
                occ[key].append((rel, lit.line, c))
    words = data_words(root)
    tainted: set[str] = set()
    partial: dict[str, set] = defaultdict(set)
    for f, (src, bom, lits) in parsed.items():
        rel = f.relative_to(root).as_posix()
        for lit in walk(lits):
            if lit.u8 or lit.kind == 'raw':
                continue
            c = classify(src, lit)
            if c in ('pre', 'suf', 'sub'):
                partial[lit.key(src)].add((c, rel))
    for key, cs in ctx.items():
        if 'ext' in cs:
            tainted.add(key)
        elif cs & {'cmp', 'pre', 'suf', 'sub'} and ('ok' not in cs or plain(key).strip() in words or key in words):
            tainted.add(key)
    return occ, tainted, parsed, partial


def effective(strings: dict, occ: dict, tainted: set, partial: dict, keep: set) -> tuple[dict, list]:
    """実際に適用する訳。StartsWith などの部分一致で比較される文字列は、
    それを含む他の文字列の訳とも整合しない限り英語のまま残す。"""
    eff = {k: v for k, v in strings.items() if k in occ and k not in tainted and k not in keep and v and v != k}
    dropped = []
    changed = True
    while changed:
        changed = False
        for x, uses in partial.items():
            tx = eff.get(x, x)
            modes = {m for m, _ in uses}
            files = {f for _, f in uses}
            for k in occ:
                if k == x or not any(r in files for r, _, _ in occ[k]):
                    continue
                kl, xl = k.lower(), x.lower()
                rel = (('pre' in modes and kl.startswith(xl)) or ('suf' in modes and kl.endswith(xl))
                       or ('sub' in modes and xl in kl))
                if not rel:
                    continue
                tk = eff.get(k, k)
                tkl, txl = tk.lower(), tx.lower()
                ok = (('pre' in modes and kl.startswith(xl) and tkl.startswith(txl))
                      or ('suf' in modes and kl.endswith(xl) and tkl.endswith(txl))
                      or ('sub' in modes and xl in kl and txl in tkl))
                if not ok:
                    for z in (x, k):
                        if z in eff:
                            del eff[z]
                            dropped.append(z)
                            changed = True
    return eff, dropped


# ---------------------------------------------------------------------------
# 訳文の検証
# ---------------------------------------------------------------------------
def check_translation(key: str, ja: str, interp: bool, kind: str) -> str | None:
    if kind == 'verbatim':
        if re.search(r'(?<!")"(?!")', ja.replace('""', '')):
            return '引用符 " は "" と書いてください'
    else:
        if re.search(r'(?<!\\)"', ja.replace('\\\\', '')):
            return '引用符 " は \\" と書くか「」を使ってください'
        if '\n' in ja or '\r' in ja:
            return '改行は \\n と書いてください'
        if re.search(r'\\(?![nrt0"\'\\abfv]|u[0-9a-fA-F]{4}|x[0-9a-fA-F])', ja):
            return '不正なエスケープ'
    if interp:
        want = sorted(re.findall(r'(?<!\{)\{(\d+)\}(?!\})', key))
        got = sorted(re.findall(r'(?<!\{)\{(\d+)\}(?!\})', ja))
        if want != got:
            return f'プレースホルダが一致しません（必要: {", ".join("{"+w+"}" for w in want)}）'
        stripped = re.sub(r'\{\d+\}', '', ja).replace('{{', '').replace('}}', '')
        if '{' in stripped or '}' in stripped:
            return '{ } は {{ }} と書いてください'
    else:
        if sorted(_FMT.findall(key)) != sorted(_FMT.findall(ja)):
            return '書式プレースホルダ {0} などが一致しません'
    if sorted(re.findall(r'(?<!\{)\{[A-Za-z_]\w*\}(?!\})', key)) != sorted(re.findall(r'(?<!\{)\{[A-Za-z_]\w*\}(?!\})', ja)):
        return '{name} などの差し込み記号が一致しません'
    return None


# ---------------------------------------------------------------------------
# 書き換え
# ---------------------------------------------------------------------------
_PLURAL = re.compile(r'^\s*\(?[^?"]*\?\s*"(e?s)?"\s*:\s*"(e?s)?"\s*\)?\s*$')


def render_literal(src: str, lit: Lit, table: dict[int, str]) -> str:
    """lit を訳文（あれば）で再構成。穴の中の入れ子文字列も再帰的に処理する。
    穴 (hole) の範囲には書式指定 (:X8 など) も含まれる。"""
    holes = [render_span(src, s, e, lit.children, table) for k, s, e in lit.parts if k == 'hole']
    new = table.get(id(lit))
    out = [src[lit.start:lit.start + lit.open_len]]
    if new is None:
        n = 0
        for k, s, e in lit.parts:
            if k == 'text':
                out.append(src[s:e])
            else:
                out.append('{' + holes[n] + '}')
                n += 1
    elif lit.interp:
        pos = 0
        for m in re.finditer(r'(?<!\{)\{(\d+)\}(?!\})', new):
            out.append(new[pos:m.start()])
            hole = holes[int(m.group(1))]
            if not _PLURAL.match(hole):      # 英語の複数形語尾 (? "" : "s") は日本語では捨てる
                out.append('{' + hole + '}')
            pos = m.end()
        out.append(new[pos:])
    else:
        out.append(new)
    out.append(src[lit.end - lit.close_len:lit.end])
    return ''.join(out)


def render_span(src: str, s: int, e: int, lits: list[Lit], table) -> str:
    out, pos = [], s
    for l in sorted(lits, key=lambda x: x.start):
        if l.start < s or l.end > e:
            continue
        out.append(src[pos:l.start])
        out.append(render_literal(src, l, table))
        pos = l.end
    out.append(src[pos:e])
    return ''.join(out)


# ---------------------------------------------------------------------------
# コードパッチ
# ---------------------------------------------------------------------------
def apply_code_patches(root: Path, cfg: dict, variables: dict) -> list[str]:
    msgs = []
    for p in cfg.get('code_patches', []):
        if not p.get('enabled', True):
            continue
        files = sorted(set(glob.glob(str(root / p['file']), recursive=True)))
        total = 0
        repl = p['replace']
        for k, v in variables.items():
            repl = repl.replace('${' + k + '}', v)
        for f in files:
            f = Path(f)
            text, bom = read(f)
            new, n = re.subn(p['find'], repl, text, flags=re.M)
            if n:
                write(f, new, bom)
                total += n
        status = 'OK' if total >= p.get('min', 1) else 'NG'
        msgs.append(f'[{status}] {p["name"]}: {total} 箇所')
        if status == 'NG' and p.get('required'):
            raise SystemExit(f'必須パッチ「{p["name"]}」が当たりませんでした（本家の構造が変わった可能性）')
    return msgs


# ---------------------------------------------------------------------------
# コマンド
# ---------------------------------------------------------------------------
def load_json(path: Path, default):
    if path.exists():
        return json.loads(path.read_text(encoding='utf-8'))
    return default


def save_json(path: Path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=1, sort_keys=True) + '\n', encoding='utf-8')


def _prepare(root: Path):
    cfg = load_json(JA_DIR / 'config.json', {})
    strings = load_json(JA_DIR / 'strings.json', {})
    keep = set(cfg.get('never_translate', []))
    force = set(cfg.get('force_translate', []))
    occ, tainted, parsed, partial = scan(root, cfg)
    tainted -= force
    eff, dropped = effective(strings, occ, tainted, partial, keep)
    return cfg, strings, keep, occ, tainted, parsed, eff, dropped


def cmd_extract(args):
    root = Path(args.src)
    cfg, strings, keep, occ, tainted, parsed, eff, dropped = _prepare(root)
    todo = {}
    for key, where in sorted(occ.items()):
        if key in tainted or key in keep or key in strings:
            continue
        if not any(c != 'skip' for _, _, c in where):
            continue
        todo[key] = [f'{r}:{l}' for r, l, _ in where[:3]]
    save_json(Path(args.out), todo)
    used = sum(1 for k in occ if k in eff)
    msg = (f'候補 {len(occ)} 件 / 適用される訳 {used} 件 / 未翻訳 {len(todo)} 件 / '
           f'比較・キー用途で除外 {len(tainted & set(occ))} 件 / 部分一致の整合性で除外 {len(set(dropped))} 件')
    print(msg)
    if dropped:
        print('部分一致の整合性で英語のまま:', ', '.join(sorted(set(dropped))[:50]))
    if args.github_summary and os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as fh:
            fh.write(f'### 日本語化\n{msg}\n\n')
            if todo:
                fh.write('<details><summary>未翻訳の文字列</summary>\n\n')
                for k in list(todo)[:300]:
                    fh.write(f'- `{k[:150]}`\n')
                fh.write('\n</details>\n')


def cmd_apply(args):
    root = Path(args.src)
    cfg, strings, keep, occ, tainted, parsed, eff, dropped = _prepare(root)
    bad = {}
    pending = []
    replaced = 0
    for f, (src, bom, lits) in parsed.items():
        table: dict[int, str] = {}
        for lit in walk(lits):
            if lit.u8 or lit.kind == 'raw':
                continue
            key = lit.key(src)
            ja = eff.get(key)
            if not ja or not is_candidate(lit, src) or classify(src, lit) in ('ext', 'skip'):
                continue
            err = check_translation(key, ja, lit.interp, lit.kind)
            if err:
                bad[key] = (ja, err)
                continue
            table[id(lit)] = ja
        if not table:
            continue
        new = render_span(src, 0, len(src), lits, table)
        try:
            after = Lexer(new).literals()
        except Exception as e:  # noqa: BLE001
            raise SystemExit(f'書き換え後の字句解析に失敗: {f}: {e}')
        if len(after) != len(lits):
            raise SystemExit(f'書き換え後の字句解析が一致しません: {f}')
        pending.append((f, new, bom))
        replaced += len(table)
    for f, new, bom in pending:
        write(f, new, bom)
    changed_files = len(pending)

    variables = {'repo': args.repo}
    msgs = apply_code_patches(root, cfg, variables)
    print(f'文字列置換: {replaced} 箇所 / {changed_files} ファイル')
    for m in msgs:
        print(m)
    for key, (ja, err) in bad.items():
        print(f'[訳文エラー] {err}\n  原文: {key}\n  訳文: {ja}')
    if bad and args.strict:
        raise SystemExit(1)


def cmd_translate(args):
    api_key = os.environ.get('ANTHROPIC_API_KEY')
    if not api_key:
        print('ANTHROPIC_API_KEY が無いので自動翻訳はスキップします')
        return
    todo = load_json(Path(args.todo), {})
    if not todo:
        print('未翻訳はありません')
        return
    strings = load_json(JA_DIR / 'strings.json', {})
    glossary = (JA_DIR / 'glossary.md').read_text(encoding='utf-8') if (JA_DIR / 'glossary.md').exists() else ''
    examples = dict(list(sorted(strings.items(), key=lambda kv: len(kv[0])))[:0])
    keys = list(todo)
    added = 0
    for i in range(0, len(keys), 80):
        chunk = keys[i:i + 80]
        prompt = (
            'あなたはAndroid向けポケモンのセーブエディタ「PKForge」の日本語ローカライザーです。\n'
            '次のJSON配列の英語UI文字列（C#ソース上の表記、エスケープ込み）を自然な日本語UI文言に訳し、'
            '{"原文": "訳文"} のJSONオブジェクトだけを返してください。\n'
            'ルール: {0} {1} などのプレースホルダはすべてそのまま残す（語順の入れ替えは可）。'
            '\\n などのエスケープは保持。ダブルクォートは使わず「」を使う。'
            'ポケモン関連用語は日本の公式用語（例: Nature=せいかく, Ability=とくせい, Held item=もちもの, '
            'Box=ボックス, Party=てもち, Shiny=色違い, Egg=タマゴ, IVs=個体値, EVs=努力値, Bank=バンク）。'
            '固有名詞・略語(PKForge, PKHeX, ALM, Showdown, QR, IV, EV, PID, HP 等)は原則そのまま。'
            '翻訳すべきでない文字列（コード識別子など）は原文をそのまま返す。\n\n'
            + (f'用語集:\n{glossary}\n\n' if glossary else '')
            + json.dumps(chunk, ensure_ascii=False))
        body = json.dumps({
            'model': os.environ.get('PKJA_MODEL', 'claude-sonnet-5-5'),
            'max_tokens': 16000,
            'messages': [{'role': 'user', 'content': prompt}],
        }).encode()
        req = urllib.request.Request('https://api.anthropic.com/v1/messages', data=body, headers={
            'x-api-key': api_key, 'anthropic-version': '2023-06-01', 'content-type': 'application/json'})
        try:
            with urllib.request.urlopen(req, timeout=300) as r:
                resp = json.load(r)
            text = ''.join(b.get('text', '') for b in resp['content'])
            m = re.search(r'\{.*\}', text, re.S)
            result = json.loads(m.group(0))
        except Exception as e:  # noqa: BLE001
            print(f'自動翻訳に失敗: {e}')
            continue
        for k in chunk:
            v = result.get(k)
            if isinstance(v, str) and v.strip():
                interp = bool(re.search(r'(?<!\{)\{\d+\}(?!\})', k))
                if check_translation(k, v, interp, 'regular') is None or check_translation(k, v, False, 'regular') is None:
                    strings[k] = v
                    added += 1
    save_json(JA_DIR / 'strings.json', strings)
    print(f'自動翻訳で {added} 件追加しました')


def cmd_check(args):
    """翻訳ファイル（JSON）の訳文を検証する"""
    data = load_json(Path(args.file), {})
    ng = 0
    for k, v in data.items():
        if not isinstance(v, str):
            print(f'[NG] 文字列ではありません: {k}')
            ng += 1
            continue
        interp = bool(re.search(r'(?<!\{)\{\d+\}(?!\})', k))
        err = check_translation(k, v, False, 'regular')
        if err and interp:
            err = check_translation(k, v, True, 'regular')
        lead = re.match(r'\s*', k).group(0), re.search(r'\s*$', k).group(0)
        if not err and (not v.startswith(lead[0]) or not v.endswith(lead[1])):
            err = '先頭・末尾の空白を原文と同じにしてください'
        if err:
            ng += 1
            print(f'[NG] {err}\n  原文: {k}\n  訳文: {v}')
    if args.expect:
        want = set(load_json(Path(args.expect), {}))
        missing = want - set(data)
        if missing:
            ng += len(missing)
            print(f'[NG] 未訳 {len(missing)} 件: ' + ' | '.join(sorted(missing)[:20]))
    print(f'{len(data)} 件中 NG {ng} 件')
    raise SystemExit(1 if ng else 0)


def main():
    ap = argparse.ArgumentParser(description='PKForge 日本語化パッチャー')
    sub = ap.add_subparsers(required=True)
    p = sub.add_parser('extract')
    p.add_argument('src')
    p.add_argument('--out', default='untranslated.json')
    p.add_argument('--github-summary', action='store_true')
    p.set_defaults(fn=cmd_extract)
    p = sub.add_parser('apply')
    p.add_argument('src')
    p.add_argument('--repo', default=os.environ.get('GITHUB_REPOSITORY', 'Shurt/PKForge'))
    p.add_argument('--strict', action='store_true', help='訳文エラーがあれば失敗にする')
    p.set_defaults(fn=cmd_apply)
    p = sub.add_parser('check')
    p.add_argument('file')
    p.add_argument('--expect')
    p.set_defaults(fn=cmd_check)
    p = sub.add_parser('translate')
    p.add_argument('--todo', default='untranslated.json')
    p.set_defaults(fn=cmd_translate)
    args = ap.parse_args()
    args.fn(args)


if __name__ == '__main__':
    main()
