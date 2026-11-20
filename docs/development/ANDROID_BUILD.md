# Building the Android APK

## Toolchain (verified 2026-11-20)

- Flutter 3.50.1 stable / Dart 3.14 (`git clone -b stable https://github.com/flutter/flutter D:\dev\flutter`)
- Android SDK platform 36/37, build-tools 36, cmdline-tools, **NDK 28.2.13676358** (r28c)
- JDK 17+ (Android Studio's bundled JBR works): `$env:JAVA_HOME='C:\Program Files\Android\Android Studio\jbr'`

```powershell
cd apps/mobile
flutter pub get
flutter analyze --no-fatal-infos
flutter test
flutter build apk --release        # → build/app/outputs/flutter-apk/app-release.apk
```

Universal APK with `arm64-v8a`, `armeabi-v7a`, `x86_64`. Smaller per-ABI APKs: `flutter build apk --split-per-abi`.

## Release signing

Without a key the release APK is signed with the **debug key**: it installs for testing but is not suitable for stores and
updates must keep the same key. For a real key:

```powershell
keytool -genkeypair -v -keystore $HOME\remotepc-release.jks -keyalg RSA -keysize 4096 -validity 9125 -alias remotepc
```

`apps/mobile/android/key.properties` (git-ignored, never commit):

```
storeFile=C:/Users/you/remotepc-release.jks
storePassword=…
keyAlias=remotepc
keyPassword=…
```

CI uses GitHub Secrets instead: `ANDROID_KEYSTORE_BASE64` (`[Convert]::ToBase64String([IO.File]::ReadAllBytes('remotepc-release.jks'))`),
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`.

## Checks performed on the APK

```powershell
$bt="$env:LOCALAPPDATA\Android\Sdk\build-tools\36.0.0"
& "$bt\aapt2.exe" dump badging app-release.apk | Select-String "sdkVersion|native-code|uses-permission"
& "$bt\apksigner.bat" verify --print-certs app-release.apk
adb install -r app-release.apk
```

Permissions: INTERNET, ACCESS_NETWORK_STATE, ACCESS_WIFI_STATE, CHANGE_WIFI_MULTICAST_STATE (LAN discovery), CAMERA (QR).
`flutter_webrtc` additionally merges BLUETOOTH (≤ API 30) and MODIFY_AUDIO_SETTINGS. The app never requests the microphone.

## Known build issues on Windows

| Error | Fix |
|---|---|
| `Could not close incremental caches` (Kotlin) | project and Gradle cache on different drives — `kotlin.incremental=false` is set in `android/gradle.properties` |
| `NDK 28.2… not installed` | install r28c: `sdkmanager "ndk;28.2.13676358"` or unzip `android-ndk-r28c-windows.zip` into `Sdk\ndk\28.2.13676358` |
| `Removing unused resources requires unused code shrinking` | `isShrinkResources = false` while R8 is disabled (see TASKS.md) |
