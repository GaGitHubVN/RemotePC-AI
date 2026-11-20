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
URI=$(cat /tmp/pair.txt)
cd "$ROOT/apps/mobile"
flutter test integration_test/app_test.dart \
  --dart-define=PAIR_URI="$URI" --dart-define=PAIR_HTTP=http://127.0.0.1:45901/ --dart-define=EXPECT_STYLE=material
