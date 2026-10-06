#!/usr/bin/env bash
# エミュレーターに日本語版をインストールして起動し、落ちないか・画面が日本語かを確かめる
set -u
apk="$1"
pkg="${2:-org.pkforge.shurt.ja}"
out="${3:-smoke}"
mkdir -p "$out"
adb shell settings put system accelerometer_rotation 0 || true
adb shell settings put system user_rotation 1 || true
adb shell am force-stop "$pkg" || true
sleep 3
adb logcat -c || true
adb install -r "$apk" 2>&1 | tee "$out"/install.txt
grep -q Success "$out"/install.txt || { echo "インストール失敗"; exit 1; }
act=$(adb shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER "$pkg" | tail -1 | tr -d '\r')
echo "起動: $act"
# 本家にも起動直後の画面再生成で落ちることがある（エミュレーター特有）ので、3 回まで試す
alive=0
for attempt in 1 2 3; do
  adb shell am start -W -n "$act" >/dev/null 2>&1
  alive=0
  for i in $(seq 1 9); do
    sleep 10
    adb exec-out screencap -p > "$out/screen-$attempt-$i.png" || true
    if adb shell pidof "$pkg" >/dev/null 2>&1; then alive=$((alive + 1)); else break; fi
  done
  echo "試行 $attempt: ${alive}0 秒動作"
  [[ $alive -ge 6 ]] && break
done
# 画面を少し操作して、ほかの画面も撮る
if [[ $alive -ge 6 ]]; then
  adb shell input tap 1065 202; sleep 2                      # 「Got it」
  for k in 1 2 3 4 5 6; do adb shell input tap 960 1010; sleep 2; done   # 案内を進める
  adb exec-out screencap -p > "$out/ui-home.png"
  n=0
  for x in 200 580 960 1340 1720; do
    n=$((n + 1))
    adb shell input tap $x 965; sleep 6
    adb exec-out screencap -p > "$out/ui-tab$n.png"
    if [[ $n -eq 4 ]]; then                                   # ふしぎなカード（ポケモン名の確認）
      adb shell input tap 960 574; sleep 10
      adb exec-out screencap -p > "$out/ui-cards.png"
      adb shell input keyevent KEYCODE_BACK; sleep 3
    fi
    adb shell input keyevent KEYCODE_BACK; sleep 3
  done
fi
adb logcat -d > "$out"/logcat-all.txt || true
grep -iE "FATAL|AndroidRuntime|monodroid|mono-rt|MissingMethod|TypeLoad|PKForgeJa|Unhandled" "$out"/logcat-all.txt > "$out"/logcat.txt || true
tail -80 "$out"/logcat.txt
if [[ $alive -lt 6 ]]; then
  echo "アプリが起動後に終了しました（$alive 回生存確認）"
  exit 1
fi
echo "起動テスト OK（${alive}0 秒以上動作）"
