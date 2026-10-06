"""ログの要点を GitHub Actions の注釈として出す（ログ本体を取得できない環境でも読めるように）。"""
import pathlib
import re
import sys

def emit(title, lines):
    text = '\n'.join(lines)[-12000:]
    text = text.replace('%', '%25').replace('\r', '').replace('\n', '%0A')
    print(f'::notice title={title}::{text}')

root = pathlib.Path(sys.argv[1])
for log in sorted(root.glob('*.log')):
    lines = log.read_text(encoding='utf-8', errors='replace').splitlines()
    errors = []
    for i, l in enumerate(lines):
        if re.search(r'FATAL EXCEPTION|JavaProxyThrowable:|UNHANDLED', l):
            errors.extend(lines[i:i + 8])
        elif re.search(r'error|エラー|失敗|Exception:', l, re.I) and not re.search(r'^\S+ \S+\s+\d+\s+\d+ E AndroidRuntime: \tat ', l):
            errors.append(l)
    emit(log.name, (errors[:60] + ['---- 末尾 ----'] + lines[-60:]) if errors else lines[-80:])
