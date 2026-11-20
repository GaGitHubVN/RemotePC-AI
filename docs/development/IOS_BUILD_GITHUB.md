# Building the iOS IPA on GitHub Actions (no Mac needed)

The workflow `.github/workflows/build-ios.yml` runs on a GitHub-hosted **macOS 26** runner, selects **Xcode ≥ 26**
explicitly (it fails loudly if the image has none — needed for the iOS 26 SDK / Liquid Glass APIs), builds an
**unsigned device build**, validates it and packages `RemotePC-AI-iOS-unsigned.ipa`.

## 1. Create the repository and push

```powershell
cd D:\RemotePC-AI
gh repo create RemotePC-AI --private --source . --remote origin
git push -u origin main
```

(Or create an empty private repo on github.com and `git remote add origin https://github.com/<you>/RemotePC-AI.git`.)

## 2. Repository settings

- Settings → Actions → General → *Allow all actions* (or allow `actions/*`, `subosito/flutter-action`, `softprops/action-gh-release`).
- Workflow permissions: **Read repository contents** (the release job requests `contents: write` itself).
- macOS minutes on private repos are billed at a higher rate than Linux; check your plan.

## 3. Run the build

```powershell
gh workflow run "Build iOS IPA"
gh run watch                       # follow logs
gh run download --name RemotePC-AI-iOS-unsigned -D releases/ios
```

Tags `v*` also trigger it (and `release.yml` attaches the IPA to a GitHub pre-release).

## 4. What the workflow verifies

| Check | Command | Expectation |
|---|---|---|
| Xcode / SDK | `xcodebuild -version`, `xcrun --sdk iphoneos --show-sdk-version` | Xcode ≥ 26, iOS SDK ≥ 26 |
| Device (not simulator) | `otool -l … LC_BUILD_VERSION` | `platform 2` (iOS), **not** 7 (simulator) |
| Deployment target | same | `minos 18.0` |
| Architecture | `lipo -info Runner` | `arm64` |
| Info.plist | `plutil -p` | bundle id `ai.remotepc.app`, MinimumOSVersion 18.0, usage descriptions |
| Frameworks | `ls Runner.app/Frameworks` | Flutter.framework, WebRTC.framework, plugin frameworks |

A simulator `.app` is never renamed to `.ipa`. The IPA is the standard `Payload/Runner.app` zip.

## 5. Sign with your own tool

The IPA is **not installable until signed**. Your signing tool must:
- re-sign every framework in `Payload/Runner.app/Frameworks/` (deep signing), then the app;
- use a provisioning profile whose App ID matches `ai.remotepc.app` (or change the bundle id);
- entitlements: none beyond defaults are required. If your tool removes `get-task-allow` or similar that is fine.
  **Not used**: push notifications, App Groups, iCloud.

Local network: iOS shows the *Local Network* prompt on first LAN connection — allow it. UDP broadcast discovery requires
Apple's multicast entitlement, which the app does **not** request; on iPhone pair with the QR code (it carries the PC's
LAN addresses).

## 6. Optional signed build in CI

Add secrets `IOS_P12_BASE64`, `IOS_P12_PASSWORD`, `IOS_PROFILE_BASE64`, `IOS_TEAM_ID`. The `signed` job then runs
`flutter build ipa` with an ad-hoc export. Never commit certificates or profiles.

## 7. When the build fails — where to look

| Failure | Likely cause | Fix |
|---|---|---|
| "No Xcode >= 26 on this runner image" | runner image changed | update `runs-on` to the newest `macos-*` image listing Xcode 26 |
| Swift error on `glassEffect` / `GlassEffectContainer` | SDK < 26 selected | check the *Select Xcode* step output |
| CocoaPods / SPM resolution error | plugin without SPM support | Flutter falls back to CocoaPods automatically; check `pod` output in the log |
| `minos` not 18.0 | deployment target edited | `IPHONEOS_DEPLOYMENT_TARGET = 18.0` in `ios/Runner.xcodeproj/project.pbxproj` |
| Code signing error in the unsigned job | `--no-codesign` missing | keep `flutter build ios --release --no-codesign` |

## 8. Install check on iPhone

After signing: install, launch, allow camera (QR) and local network, pair. On iOS 26 the remote toolbar uses native
Liquid Glass; on iOS 18–25 a standard translucent bar is used (the glass API is never called there).
