#!/usr/bin/env python3
"""PKForge 多言語版ビルダー（本家ソースを書き換える）

  apply  SRC   : UI 文字列を「実行時に言語で切り替わる式」に書き換え、ランタイムとコードパッチを入れる
  stats  SRC   : 各言語の訳の適用件数を表示

文字列リテラル "Open save" は
    ((string)(global::PKForgeI18n.L_App.I switch { 1 => "セーブを開く", 2 => "打开存档", ..., _ => "Open save" }))
に置き換わる。言語番号は LANGS の並び（0 = 英語 = 原文）。
switch 文の case "X": は  case string __pkfN when __pkfN == (上と同じ式):  に、
switch 式の "X" => は    string __pkfN when __pkfN == (…) =>            に置き換えるので、
表示と比較の両方が同じ訳になり、メニューの選択判定などが壊れない。

ビルドで書き換え箇所がコンパイルエラーになったら、そのキーを --exclude ファイルに足して
やり直せる（.github/workflows/app.yml が自動でやる）。
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pkforge_ja as P  # noqa: E402
from cslex import Lexer  # noqa: E402

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
I18N = REPO / 'i18n'

# 0 は英語（原文）。並びを変えると保存済みの設定と食い違うので末尾にだけ足すこと。
LANGS = ['en', 'ja', 'zh-Hans', 'zh-Hant', 'ko', 'fr', 'de', 'es', 'it']

# ファイルの所属プロジェクト → そのアセンブリに入れるランタイムクラス名
PROJECTS = {
    'src/PKForge.App/': 'L_App',
    'src/PKForge.Domain/': 'L_Domain',
    'src/PKForge.Engine/': 'L_Engine',
    'src/PKForge.Chrome/': 'L_Chrome',
}

_PATTERN_BEFORE = re.compile(r'\b(is|or|and|not)\s*$|[<>]=?\s*$')
_CASE_BEFORE = re.compile(r'\bcase\s*$')
_CASE_AFTER = re.compile(r'^\s*:(?!:)')
_ARM_AFTER = re.compile(r'^\s*=>')
_ARM_BEFORE = re.compile(r'(^|[{,(])\s*$')
_WHEN_AFTER = re.compile(r'^\s*(when\b|or\b|and\b)')


def dictionaries() -> dict[str, dict]:
    out = {'ja': P.load_json(REPO / 'ja' / 'strings.json', {})}
    for lang in LANGS[2:]:
        out[lang] = P.load_json(I18N / f'{lang}.json', {})
    return out


def full_context(src: str, lit):
    """前後の文脈（同じ行だけでなく直前の数行も見る：case が改行の後に来る書き方用）"""
    before, after = P.context(src, lit)
    return before, after


def site_kind(src: str, lit) -> str:
    """'expr' 普通の式 / 'case' case ラベル / 'arm' switch 式の腕 / 'pattern' その他の定数パターン / 'const' 定数文脈"""
    before, after = full_context(src, lit)
    if _CASE_BEFORE.search(before):
        return 'case' if _CASE_AFTER.search(after) else 'pattern'
    if _ARM_AFTER.search(after) and _ARM_BEFORE.search(before):
        # "X" => …  （ラムダの引数は文字列にならないので switch 式の腕）
        return 'arm'
    if _PATTERN_BEFORE.search(before) and not re.search(r'[=!]=\s*$', before):
        if re.search(r'\b(is|or|and|not)\s*$', before):
            return 'pattern'
    if _WHEN_AFTER.search(after) and (_CASE_BEFORE.search(before) or _ARM_BEFORE.search(before)):
        return 'pattern'
    # const 宣言・属性・既定値引数（文字列やコメントの中の ; { } ( ) に惑わされないよう、それらを消した版で見る）
    code = masked(src)
    stmt_start = max(code.rfind(';', 0, lit.start), code.rfind('{', 0, lit.start), code.rfind('}', 0, lit.start))
    stmt = code[stmt_start + 1:lit.start]
    if re.search(r'\bconst\b', stmt):
        return 'const'
    if re.search(r'^\s*\[\s*[\w.]+', before):
        return 'const'
    # 既定値引数: 「型 名前 = "…"」で、いちばん内側の開き括弧が (
    if re.search(r'[\w>?\]]\s+@?\w+\s*=\s*$', code[max(0, lit.start - 200):lit.start]):
        depth = 0
        for ch in reversed(code[:lit.start]):
            if ch in ')]}':
                depth += 1
            elif ch in '([{':
                if depth == 0:
                    if ch == '(':
                        return 'const'
                    break
                depth -= 1
            elif ch == ';' and depth == 0:
                break
    return 'expr'


_MASKED: dict[int, str] = {}


def masked(src: str) -> str:
    """文字列リテラルの中身とコメントを空白にした src（長さは同じ）"""
    key = id(src)
    if key in _MASKED:
        return _MASKED[key]
    chars = list(src)
    for lit in P.walk(Lexer(src).literals()):
        for k, s_, e_ in lit.parts:
            if k == 'text':
                for i in range(s_, e_):
                    if chars[i] != '\n':
                        chars[i] = ' '
    out = ''.join(chars)
    out = re.sub(r'//[^\n]*', lambda m: ' ' * len(m.group(0)), out)
    out = re.sub(r'/\*.*?\*/', lambda m: re.sub(r'[^\n]', ' ', m.group(0)), out, flags=re.S)
    _MASKED[key] = out
    return out


def project_class(rel: str) -> str | None:
    for prefix, cls in PROJECTS.items():
        if rel.startswith(prefix):
            return cls
    return None


class Builder:
    def __init__(self, root: Path, exclude: set[str]):
        self.root = root
        cfg = P.load_json(REPO / 'ja' / 'config.json', {})
        self.cfg = cfg
        dicts = dictionaries()
        keep = set(cfg.get('never_translate', [])) | exclude
        occ, tainted, parsed, partial = P.scan(root, cfg)
        tainted -= set(cfg.get('force_translate', []))
        # 定数パターン（is "X" / "A" or "B" など）に使われるキーは全言語で原文のまま
        for f, (src, bom, lits) in parsed.items():
            for lit in P.walk(lits):
                if lit.u8 or lit.kind == 'raw' or lit.interp:
                    continue
                k = site_kind(src, lit)
                if k in ('pattern', 'const'):
                    tainted.add(lit.key(src))
        self.occ, self.tainted, self.parsed, self.partial = occ, tainted, parsed, partial
        self.eff: dict[str, dict] = {}
        for lang, d in dicts.items():
            eff, _ = P.effective(d, occ, tainted, partial, keep)
            self.eff[lang] = eff
        self.counter = 0
        self.stats = {lang: 0 for lang in LANGS[1:]}
        self.sites = 0

    # ---- 1 リテラルの言語別テキスト ----
    def translations(self, src: str, lit) -> dict[int, str]:
        """言語番号 → 訳文（このリテラル自身に当たる訳だけ）"""
        if lit.u8 or lit.kind == 'raw':
            return {}
        if not P.is_candidate(lit, src) or P.classify(src, lit) in ('ext', 'skip'):
            return {}
        key = lit.key(src)
        out = {}
        for i, lang in enumerate(LANGS):
            if i == 0:
                continue
            t = self.eff[lang].get(key)
            if not t or t == key:
                continue
            if P.check_translation(key, t, lit.interp, lit.kind):
                continue
            out[i] = t
        return out

    def plan(self, src: str, lits) -> dict[int, dict[int, str]]:
        table = {}
        for lit in P.walk(lits):
            tr = self.translations(src, lit)
            if tr:
                table[id(lit)] = tr
        return table

    # ---- 描画 ----
    def render_lang(self, src: str, lit, plan, lang: int) -> str:
        """1 言語ぶんのリテラル（穴の中の入れ子も同じ言語で）"""
        tables = {k: v[lang] for k, v in plan.items() if lang in v}
        return P.render_literal(src, lit, tables)

    def render_multi(self, src: str, lit, plan, cls: str) -> str:
        tr = plan.get(id(lit))
        if not tr and not self._has_translated_child(lit, plan):
            return src[lit.start:lit.end]
        if not tr:
            # 自分は訳さないが、穴の中に訳す文字列がある → 穴だけ多言語化
            holes = [self.render_span_multi(src, s, e, lit.children, plan, cls) for k, s, e in lit.parts if k == 'hole']
            out = [src[lit.start:lit.start + lit.open_len]]
            n = 0
            for k, s, e in lit.parts:
                if k == 'text':
                    out.append(src[s:e])
                else:
                    out.append('{' + holes[n] + '}')
                    n += 1
            out.append(src[lit.end - lit.close_len:lit.end])
            return ''.join(out)
        return self.switch_expr(src, lit, plan, cls)

    def switch_expr(self, src: str, lit, plan, cls: str) -> str:
        langs = set()
        for l in P.walk([lit]):
            langs |= set(plan.get(id(l), {}))
        arms: dict[str, list[int]] = {}
        orig = self.render_lang(src, lit, plan, 0)
        for i in sorted(langs):
            text = self.render_lang(src, lit, plan, i)
            if text != orig:
                arms.setdefault(text, []).append(i)
        for i in plan.get(id(lit), {}):
            self.stats[LANGS[i]] += 1
        self.sites += 1
        if not arms:
            return orig
        parts = [f'{" or ".join(map(str, ids))} => {text}' for text, ids in arms.items()]
        parts.append(f'_ => {orig}')
        return f'((string)(global::PKForgeI18n.{cls}.I switch {{ {", ".join(parts)} }}))'

    def _has_translated_child(self, lit, plan) -> bool:
        return any(id(c) in plan or self._has_translated_child(c, plan) for c in lit.children)

    def render_span_multi(self, src: str, s: int, e: int, lits, plan, cls: str) -> str:
        out, pos = [], s
        for l in sorted(lits, key=lambda x: x.start):
            if l.start < s or l.end > e:
                continue
            out.append(src[pos:l.start])
            out.append(self.render_site(src, l, plan, cls))
            pos = l.end
        out.append(src[pos:e])
        return ''.join(out)

    def render_site(self, src: str, lit, plan, cls: str) -> str:
        if id(lit) not in plan:
            return self.render_multi(src, lit, plan, cls)
        kind = site_kind(src, lit)
        if kind == 'expr':
            return self.render_multi(src, lit, plan, cls)
        if kind in ('case', 'arm') and not lit.interp:
            expr = self.switch_expr(src, lit, plan, cls)
            self.counter += 1
            v = f'__pkf{self.counter}'
            return f'string {v} when {v} == {expr}'
        return src[lit.start:lit.end]

    # ---- 全体 ----
    def apply(self) -> list[tuple[Path, str, bool]]:
        out = []
        for f, (src, bom, lits) in self.parsed.items():
            rel = f.relative_to(self.root).as_posix()
            cls = project_class(rel)
            if cls is None:
                continue
            plan = self.plan(src, lits)
            if not plan:
                continue
            new = self.render_span_multi(src, 0, len(src), lits, plan, cls)
            try:
                Lexer(new).literals()
            except Exception as e:  # noqa: BLE001
                raise SystemExit(f'書き換え後の字句解析に失敗: {rel}: {e}')
            out.append((f, new, bom))
        return out


RUNTIME = '''// <auto-generated> PKForge 多言語版ランタイム（pkforge_i18n.py が追加） </auto-generated>
#nullable enable
namespace PKForgeI18n
{
    internal static class %(cls)s
    {
        private static int _i = -1;

        /// <summary>現在の表示言語の番号（0 = English）。起動時に LangSetup が AppContext に入れる。</summary>
        internal static int I
        {
            get
            {
                var i = _i;
                if (i >= 0) return i;
                if (global::System.AppContext.GetData("PKForgeI18n.Lang") is int set)
                {
                    _i = set;
                    return set;
                }
                return FromCulture(global::System.Globalization.CultureInfo.CurrentUICulture.Name);
            }
        }

        internal static int FromCulture(string name)
        {
            name = (name ?? "").ToLowerInvariant();
            if (name.StartsWith("ja")) return 1;
            if (name.StartsWith("zh")) return name.Contains("hant") || name.EndsWith("-tw") || name.EndsWith("-hk") || name.EndsWith("-mo") ? 3 : 2;
            if (name.StartsWith("ko")) return 4;
            if (name.StartsWith("fr")) return 5;
            if (name.StartsWith("de")) return 6;
            if (name.StartsWith("es")) return 7;
            if (name.StartsWith("it")) return 8;
            return 0;
        }
    }
}
'''


def write_runtime(root: Path):
    for prefix, cls in PROJECTS.items():
        d = root / prefix
        if d.exists():
            (d / 'PKForgeI18n.g.cs').write_text(RUNTIME % {'cls': cls}, encoding='utf-8')


def cmd_apply(args):
    root = Path(args.src)
    exclude = set(P.load_json(Path(args.exclude), [])) if args.exclude and Path(args.exclude).exists() else set()
    b = Builder(root, exclude)
    files = b.apply()
    sitemap = {}
    for f, new, bom in files:
        P.write(f, new, bom)
    write_runtime(root)
    # 追加のソース（言語選択画面など）
    for extra in (REPO / 'app' / 'src').rglob('*.cs'):
        dest = root / extra.relative_to(REPO / 'app' / 'src')
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(extra.read_text(encoding='utf-8'), encoding='utf-8')
    # アイコン・スプラッシュ（赤×黒の図鑑風）
    import shutil
    assets = REPO / 'app' / 'assets'
    for name, dest in (('pkforge.png', 'src/PKForge.App/Resources/AppIcon/pkforge.png'),
                       ('splash.png', 'src/PKForge.App/Resources/Splash/splash.png')):
        if (assets / name).exists() and (root / dest).exists():
            shutil.copyfile(assets / name, root / dest)
    cfg = P.load_json(REPO / 'app' / 'patches.json', {})
    msgs = P.apply_code_patches(root, cfg, {'repo': args.repo})
    print(f'書き換えたファイル {len(files)} / 書き換え箇所 {b.sites} / 除外キー {len(exclude)}')
    for lang, n in b.stats.items():
        print(f'  {lang}: {n} 箇所')
    for m in msgs:
        print(m)
    if args.summary:
        with open(args.summary, 'a', encoding='utf-8') as fh:
            fh.write(f'### 多言語化\n書き換え {b.sites} 箇所 / {len(files)} ファイル / 除外キー {len(exclude)}\n\n')
            fh.write('| 言語 | 訳が入った箇所 |\n|---|---|\n')
            for lang, n in b.stats.items():
                fh.write(f'| {lang} | {n} |\n')
            fh.write('\n' + '\n'.join(f'- {m}' for m in msgs) + '\n')


def cmd_untranslated(args):
    """本家に増えた文字列のうち、どれかの言語で訳が無いもの"""
    root = Path(args.src)
    b = Builder(root, set())
    todo = {}
    for key, where in sorted(b.occ.items()):
        if key in b.tainted or not any(c != 'skip' for _, _, c in where):
            continue
        missing = [lang for lang in LANGS[1:] if key not in dictionaries_cache()[lang]]
        if missing:
            todo[key] = missing
    P.save_json(Path(args.out), todo)
    print(f'未翻訳 {len(todo)} 件')


_DC = None


def dictionaries_cache():
    global _DC
    if _DC is None:
        _DC = dictionaries()
    return _DC


def cmd_blame(args):
    """コンパイルエラー（file(line,col)）が書き換え箇所なら、そのキーを除外リストに足す"""
    root = Path(args.src)
    log = Path(args.log).read_text(encoding='utf-8', errors='ignore')
    errs = set()
    for m in re.finditer(r'^(?:\s*\d+>)?\s*(/[^\s(]+\.cs)\((\d+),(\d+)\): error (CS\d+)', log, re.M):
        errs.add((m.group(1), int(m.group(2)), int(m.group(3)), m.group(4)))
    if not errs:
        print('C# のエラー位置が見つかりません')
        raise SystemExit(2)
    exclude = set(P.load_json(Path(args.exclude), [])) if Path(args.exclude).exists() else set()
    # 書き換え前のソースで同じ位置のリテラルを探す（行番号は書き換えで変わらない：改行を足さないため）
    orig_root = Path(args.orig)
    added = set()
    unknown = []
    for path, line, col, code in sorted(errs):
        try:
            rel = Path(path).resolve().relative_to(root.resolve())
        except ValueError:
            unknown.append((path, line, code))
            continue
        of = orig_root / rel
        if not of.exists():
            unknown.append((path, line, code))
            continue
        src, _ = P.read(of)
        lits = list(P.walk(Lexer(src).literals()))
        on_line = [l for l in lits if l.line == line or (src.count('\n', 0, l.start) + 1 <= line <= src.count('\n', 0, l.end) + 1)]
        keys = {l.key(src) for l in on_line if not l.u8 and l.kind != 'raw'}
        if not keys:
            unknown.append((path, line, code))
            continue
        added |= keys
    new = added - exclude
    exclude |= added
    P.save_json(Path(args.exclude), sorted(exclude))
    print(f'除外に追加 {len(new)} 件（合計 {len(exclude)}）')
    for k in sorted(new):
        print('  +', k[:120])
    if unknown:
        print('書き換えと無関係なエラー:')
        for u in unknown[:30]:
            print('  ', u)
    raise SystemExit(0 if new else 3)


LANG_NAMES = {
    'ja': ('日本語', 'ポケモン公式の日本語用語（せいかく, とくせい, もちもの, ボックス, てもち, 色違い, タマゴ, 個体値, 努力値, 図鑑, わざ）'),
    'zh-Hans': ('Simplified Chinese', 'official Pokémon terms: 宝可梦, 性格, 特性, 携带物品, 盒子, 异色, 蛋, 个体值, 努力值, 图鉴, 招式, 训练家, 存档'),
    'zh-Hant': ('Traditional Chinese', 'official Pokémon terms: 寶可夢, 性格, 特性, 攜帶物品, 盒子, 異色, 蛋, 個體值, 努力值, 圖鑑, 招式, 訓練家, 存檔'),
    'ko': ('Korean', 'official Pokémon terms: 포켓몬, 성격, 특성, 지닌 물건, 박스, 색이 다른, 알, 개체값, 노력치, 도감, 기술, 트레이너, 세이브'),
    'fr': ('French', 'official Pokémon terms: Nature, Talent, Objet tenu, Boîte, Équipe, Chromatique, Œuf, IV, EV, Pokédex, Capacité, Dresseur, Sauvegarde'),
    'de': ('German', 'official Pokémon terms: Wesen, Fähigkeit, Getragenes Item, Box, Team, Schillernd, Ei, IS, FP, Pokédex, Attacke, Trainer, Spielstand (du-form)'),
    'es': ('Spanish (Spain)', 'official Pokémon terms: Naturaleza, Habilidad, Objeto equipado, Caja, Equipo, Variocolor, Huevo, IV, EV, Pokédex, Movimiento, Entrenador, Partida'),
    'it': ('Italian', 'official Pokémon terms: Natura, Abilità, Strumento tenuto, Box, Squadra, Cromatico, Uovo, IV, EV, Pokédex, Mossa, Allenatore, Salvataggio'),
}


def cmd_translate(args):
    """未翻訳の文字列を Claude API で全言語に訳して辞書に足す（ANTHROPIC_API_KEY があるときだけ）"""
    import os
    import urllib.request
    api_key = os.environ.get('ANTHROPIC_API_KEY')
    root = Path(args.src)
    b = Builder(root, set())
    keys = [k for k, where in sorted(b.occ.items())
            if k not in b.tainted and any(c != 'skip' for _, _, c in where)]
    dicts = dictionaries()
    total_missing = {lang: [k for k in keys if k not in dicts[lang]] for lang in LANG_NAMES}
    print('未翻訳: ' + ', '.join(f'{l} {len(v)}' for l, v in total_missing.items()))
    if not api_key:
        print('ANTHROPIC_API_KEY が無いので自動翻訳はスキップ（未翻訳は英語のまま表示されます）')
        return
    for lang, missing in total_missing.items():
        if not missing:
            continue
        name, terms = LANG_NAMES[lang]
        added = 0
        for i in range(0, len(missing), 60):
            chunk = missing[i:i + 60]
            ref = {k: dicts['ja'].get(k, '') for k in chunk} if lang != 'ja' else {}
            prompt = (
                f'You localize the UI of PKForge, an Android Pokémon save editor, into {name}. '
                'Translate each English UI string in the JSON array (C# source form, escapes included) and return ONLY a JSON object '
                '{"source": "translation"}. Keep placeholders like {0} {1} {0:X4} {name} (order may change), keep escapes such as \\n, '
                'never use a bare double quote, keep leading/trailing spaces, keep PKForge/PKHeX/ALM/Showdown/QR/PID/HP as-is. '
                f'Use {terms}. Return non-text strings unchanged.\n\n'
                + json.dumps(chunk, ensure_ascii=False)
                + (('\n\nJapanese reference translations: ' + json.dumps(ref, ensure_ascii=False)) if any(ref.values()) else ''))
            body = json.dumps({'model': os.environ.get('PKJA_MODEL', 'claude-sonnet-5-5'), 'max_tokens': 16000,
                               'messages': [{'role': 'user', 'content': prompt}]}).encode()
            req = urllib.request.Request('https://api.anthropic.com/v1/messages', data=body, headers={
                'x-api-key': api_key, 'anthropic-version': '2023-06-01', 'content-type': 'application/json'})
            try:
                with urllib.request.urlopen(req, timeout=300) as r:
                    resp = json.load(r)
                text = ''.join(x.get('text', '') for x in resp['content'])
                result = json.loads(re.search(r'\{.*\}', text, re.S).group(0))
            except Exception as e:  # noqa: BLE001
                print(f'{lang}: 自動翻訳に失敗: {e}')
                continue
            for k in chunk:
                v = result.get(k)
                if not isinstance(v, str) or not v.strip():
                    continue
                interp = bool(re.search(r'(?<!\{)\{\d+\}(?!\})', k))
                if P.check_translation(k, v, interp, 'regular') is None or P.check_translation(k, v, False, 'regular') is None:
                    dicts[lang][k] = v
                    added += 1
        path = REPO / 'ja' / 'strings.json' if lang == 'ja' else I18N / f'{lang}.json'
        P.save_json(path, dicts[lang])
        print(f'{lang}: {added} 件追加')


def main():
    ap = argparse.ArgumentParser(description='PKForge 多言語版ビルダー')
    sub = ap.add_subparsers(required=True)
    p = sub.add_parser('apply')
    p.add_argument('src')
    p.add_argument('--repo', default='yyoossk/PKForge-ja')
    p.add_argument('--exclude', default=str(I18N / 'exclude.json'))
    p.add_argument('--summary')
    p.set_defaults(fn=cmd_apply)
    p = sub.add_parser('untranslated')
    p.add_argument('src')
    p.add_argument('--out', default='untranslated-i18n.json')
    p.set_defaults(fn=cmd_untranslated)
    p = sub.add_parser('translate')
    p.add_argument('src')
    p.set_defaults(fn=cmd_translate)
    p = sub.add_parser('blame')
    p.add_argument('src', help='書き換え後のソース')
    p.add_argument('orig', help='書き換え前のソース')
    p.add_argument('log')
    p.add_argument('--exclude', default=str(I18N / 'exclude.json'))
    p.set_defaults(fn=cmd_blame)
    args = ap.parse_args()
    args.fn(args)


if __name__ == '__main__':
    main()
