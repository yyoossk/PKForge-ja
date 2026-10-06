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
adb shell am start -W -n "$act" || adb shell monkey -p "$pkg" -c android.intent.category.LAUNCHER 1
alive=0
for i in $(seq 1 12); do
  sleep 10
  adb exec-out screencap -p > "$out/screen-$i.png" || true
  if adb shell pidof "$pkg" >/dev/null 2>&1; then alive=$((alive + 1)); else break; fi
done
adb logcat -d > "$out"/logcat-all.txt || true
grep -iE "FATAL|AndroidRuntime|monodroid|mono-rt|MissingMethod|TypeLoad|PKForgeJa|Unhandled" "$out"/logcat-all.txt > "$out"/logcat.txt || true
tail -80 "$out"/logcat.txt
if [[ $alive -lt 6 ]]; then
  echo "アプリが起動後に終了しました（$alive 回生存確認）"
  exit 1
fi
echo "起動テスト OK（${alive}0 秒以上動作）"
