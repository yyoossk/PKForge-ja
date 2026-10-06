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
    errors = [l for l in lines if re.search(r'error|エラー|失敗|FATAL|Exception|NG', l, re.I)]
    emit(log.name, (errors[:60] + ['---- 末尾 ----'] + lines[-60:]) if errors else lines[-80:])
