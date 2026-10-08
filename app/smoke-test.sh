#!/usr/bin/env bash
# PKForge Dex をエミュレーターに入れて、言語ごとに起動・画面撮影する
#   app/smoke-test.sh APK PACKAGE OUTDIR
set -u
apk="$1"
pkg="$2"
out="$3"
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
    for k in 1 2 3 4 5 6; do adb shell input tap 960 1010; sleep 2; done
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
    # 設定メニュー（Start ボタン相当 = 設定カード）。BUTTON_START で開く
    adb shell input keyevent KEYCODE_BUTTON_START; sleep 4
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

echo "起動できた言語: $ok / 9"
[[ $ok -ge 8 ]] || exit 1
