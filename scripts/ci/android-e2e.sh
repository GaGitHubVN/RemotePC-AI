#!/usr/bin/env bash
# Runs inside reactivecircus/android-emulator-runner (emulator booted, adb connected). CI only.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SHOTS="$ROOT/shots"
mkdir -p "$SHOTS"
# The app reaches the test host on 127.0.0.1 inside the emulator; forward those ports to the runner.
adb reverse tcp:45900 tcp:45900
adb reverse tcp:45901 tcp:45901
adb shell settings put global window_animation_scale 0 || true
adb logcat -c || true
adb logcat -v time > "$SHOTS/logcat.txt" 2>&1 &
LOGCAT=$!
URI=$(cat /tmp/pair.txt)
cd "$ROOT/apps/mobile"
code=0
# Hard cap so a hung emulator fails fast with logs instead of eating the job timeout.
timeout --kill-after=30s 25m flutter test integration_test/app_test.dart -v \
  --dart-define=PAIR_URI="$URI" --dart-define=PAIR_HTTP=http://127.0.0.1:45901/ --dart-define=EXPECT_STYLE=material \
  > "$SHOTS/flutter-test.log" 2>&1 || code=$?
grep -v '^\[' "$SHOTS/flutter-test.log" | tail -80 || true
kill $LOGCAT 2>/dev/null || true
adb exec-out screencap -p > "$SHOTS/zz-final.png" || true
exit $code
