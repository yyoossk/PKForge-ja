#!/usr/bin/env bash
# PKForge Dex をエミュレーターに入れて、言語ごとに起動・画面撮影する
#   app/smoke-test.sh APK PACKAGE OUTDIR
set -u
apk="$1"
pkg="$2"
out="$3"
save="${4:-}"
mkdir -p "$out"
adb shell settings put system accelerometer_rotation 0 || true
adb shell settings put system user_rotation 1 || true
adb install -r "$apk" 2>&1 | tee "$out"/install.txt
grep -q Success "$out"/install.txt || { echo "インストール失敗"; exit 1; }
act=$(adb shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER "$pkg" | tail -1 | tr -d '\r')
echo "起動: $act"

ok=0
run_lang() {   # $1 = ロケール, $2 = 画面を巡回するか
  local loc="$1" tour="$2" dir="$out/$1"
  mkdir -p "$dir"
  adb shell am force-stop "$pkg" || true
  adb shell cmd locale set-app-locales "$pkg" --locales "$loc" || true
  sleep 2
  adb logcat -c || true
  local alive=0
  for attempt in 1 2; do
    adb shell am start -W -n "$act" >/dev/null 2>&1
    alive=0
    for i in $(seq 1 7); do
      sleep 8
      if adb shell pidof "$pkg" >/dev/null 2>&1; then alive=$((alive + 1)); else break; fi
    done
    [[ $alive -ge 5 ]] && break
  done
  adb exec-out screencap -p > "$dir/1-start.png" || true
  echo "$loc: ${alive} 回生存確認"
  if [[ $alive -ge 5 ]]; then
    ok=$((ok + 1))
    adb shell input tap 1065 202; sleep 2                                     # 初回案内を閉じる
    for k in 1 2 3 4 5 6 7 8; do adb shell input tap 960 1058; sleep 2; done  # 吹き出しを送る（下端のヒント行を押す）
    adb exec-out screencap -p > "$dir/2-home.png"
    if [[ "$tour" == yes ]]; then
      n=0
      for x in 200 580 960 1340 1720; do
        n=$((n + 1))
        adb shell input tap $x 965; sleep 6
        adb exec-out screencap -p > "$dir/3-tab$n.png"
        adb shell input keyevent KEYCODE_BACK; sleep 3
      done
    fi
    # 設定メニュー（右下の設定カード）
    adb shell input tap 1720 985; sleep 4
    adb exec-out screencap -p > "$dir/4-settings.png"
    adb shell input keyevent KEYCODE_BACK; sleep 2
  fi
  adb logcat -d > "$dir"/logcat-all.txt || true
  grep -iE "FATAL|AndroidRuntime|monodroid|mono-rt|MissingMethod|TypeLoad|Unhandled|PKForgeI18n" "$dir"/logcat-all.txt > "$dir"/logcat.txt || true
  tail -30 "$dir"/logcat.txt
}

run_lang en-US yes
run_lang ja-JP yes
for loc in zh-CN zh-TW ko-KR fr-FR de-DE es-ES it-IT; do run_lang "$loc" no; done

# ---- 画面サイズごとの表示確認（日本語・ドイツ語） ----
run_layout() {   # $1 名前, $2 解像度, $3 dpi, $4 ロケール
  local dir="$out/layout-$1-$4"
  mkdir -p "$dir"
  adb shell wm size "$2"; adb shell wm density "$3"
  adb shell settings put system accelerometer_rotation 0; adb shell settings put system user_rotation 0
  adb shell am force-stop "$pkg"; adb shell cmd locale set-app-locales "$pkg" --locales "$4" || true
  sleep 3
  adb shell am start -W -n "$act" >/dev/null 2>&1
  sleep 35
  adb exec-out screencap -p > "$dir/1-start.png"
  for k in 1 2 3 4 5 6 7 8; do adb shell input keyevent KEYCODE_BUTTON_A; sleep 1.5; done
  adb exec-out screencap -p > "$dir/2-home.png"
  adb shell input keyevent KEYCODE_BUTTON_START; sleep 4
  adb exec-out screencap -p > "$dir/3-settings.png"
  adb shell input keyevent KEYCODE_BUTTON_B; sleep 2
  adb shell input keyevent KEYCODE_DPAD_DOWN; sleep 1                   # カードの段へ
  local n=4
  for name in bank autopilot park events; do
    adb shell input keyevent KEYCODE_BUTTON_A; sleep 7
    adb exec-out screencap -p > "$dir/$n-$name.png"
    n=$((n + 1))
    adb shell input keyevent KEYCODE_BUTTON_B; sleep 3
    adb shell input keyevent KEYCODE_DPAD_RIGHT; sleep 1
  done
  adb shell pidof "$pkg" >/dev/null 2>&1 && echo "$1 $4: OK" || echo "$1 $4: 落ちた"
}
run_layout phone-portrait 1080x2400 420 ja-JP
run_layout phone-portrait 1080x2400 420 de-DE
run_layout phone-landscape 2400x1080 420 ja-JP
run_layout tablet-landscape 2560x1600 320 ja-JP
run_layout tablet-portrait 1600x2560 320 ja-JP
run_layout thor-1080p 1920x1080 280 fr-FR

# ---- セーブを開いたあとの画面（ボックス・メニュー・ステータス）を画面サイズごとに撮る ----
tap_text() {   # 画面上の文字（部分一致）を探して押す
  adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 || return 1
  adb pull /sdcard/ui.xml "$out/ui.xml" >/dev/null 2>&1 || return 1
  local xy
  xy=$(python3 - "$out/ui.xml" "$1" <<'PY'
import re, sys
x = open(sys.argv[1], encoding='utf-8', errors='ignore').read()
for m in re.finditer(r'<node [^>]*>', x):
    n = m.group(0)
    t = (re.search(r' text="([^"]*)"', n) or [None, ''])[1]
    d = (re.search(r'content-desc="([^"]*)"', n) or [None, ''])[1]
    if sys.argv[2] in t or sys.argv[2] in d:
        b = re.search(r'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"', n)
        if b:
            x1, y1, x2, y2 = map(int, b.groups())
            print((x1 + x2) // 2, (y1 + y2) // 2)
            break
PY
)
  [[ -n "$xy" ]] || return 1
  adb shell input tap $xy
}

save_tour() {   # $1 名前, $2 解像度, $3 dpi, $4 ロケール, $5 初回（ファイル選択で開く）
  local dir="$out/save-$1-$4"
  mkdir -p "$dir"
  adb shell wm size "$2"; adb shell wm density "$3"
  adb shell am force-stop "$pkg"; adb shell cmd locale set-app-locales "$pkg" --locales "$4" || true
  sleep 3
  adb shell am start -W -n "$act" >/dev/null 2>&1
  sleep 30
  adb exec-out screencap -p > "$dir/0-home.png"
  if [[ "$5" == yes ]]; then
    adb shell input keyevent KEYCODE_BUTTON_X; sleep 6                    # 「ファイル」= セーブファイルを開く
    adb exec-out screencap -p > "$dir/0-picker.png"
    if ! tap_text PKForgeTest; then
      tap_text "Show roots" || tap_text "ルートを表示" || true; sleep 2
      tap_text Downloads || tap_text ダウンロード || true; sleep 3
      tap_text PKForgeTest || echo "ファイル選択で見つからない"
    fi
    sleep 15
  else
    if ! tap_text テスト; then                                            # 棚のカートリッジ（主人公名）を押す
      adb shell input keyevent KEYCODE_DPAD_UP; sleep 1
      adb shell input keyevent KEYCODE_DPAD_LEFT; sleep 1
      adb shell input keyevent KEYCODE_BUTTON_A
    fi
    sleep 15
  fi
  adb exec-out screencap -p > "$dir/1-box.png"
  adb shell input keyevent KEYCODE_BUTTON_START; sleep 4                  # カーソルのポケモンのメニュー
  adb exec-out screencap -p > "$dir/2-menu.png"
  adb shell input keyevent KEYCODE_BUTTON_A; sleep 6                      # 先頭の項目（詳細など）
  adb exec-out screencap -p > "$dir/3-first-item.png"
  adb shell input keyevent KEYCODE_BUTTON_B; sleep 3
  adb shell input keyevent KEYCODE_BUTTON_X; sleep 4                      # ツール
  adb exec-out screencap -p > "$dir/4-tools.png"
  adb shell input keyevent KEYCODE_BUTTON_B; sleep 2
  adb shell input keyevent KEYCODE_BUTTON_Y; sleep 4                      # セーブデータ
  adb exec-out screencap -p > "$dir/5-savedata.png"
  adb shell input keyevent KEYCODE_BUTTON_B; sleep 2
  adb shell input keyevent KEYCODE_BUTTON_R1; sleep 3                     # 次のボックス
  adb exec-out screencap -p > "$dir/6-box2.png"
  adb shell pidof "$pkg" >/dev/null 2>&1 && echo "save $1 $4: OK" || echo "save $1 $4: 落ちた"
  adb shell input keyevent KEYCODE_BUTTON_B; sleep 2
}
if [[ -n "$save" && -f "$save" ]]; then
  adb push "$save" /sdcard/Download/PKForgeTest.sav
  adb shell am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file:///sdcard/Download/PKForgeTest.sav >/dev/null 2>&1 || true
  save_tour phone-portrait 1080x2400 420 ja-JP yes
  save_tour phone-portrait 1080x2400 420 de-DE yes
  save_tour phone-landscape 2400x1080 420 ja-JP yes
  save_tour tablet-landscape 2560x1600 320 ja-JP yes
  save_tour tablet-portrait 1600x2560 320 ko-KR yes
  save_tour thor-1080p 1920x1080 280 ja-JP yes
fi
adb shell wm size reset; adb shell wm density reset

echo "起動できた言語: $ok / 9"
[[ $ok -ge 8 ]] || exit 1
