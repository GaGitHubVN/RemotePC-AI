# ADR-0007 — iOS builds on GitHub Actions

## Decision
- Workflow runs on a pinned macOS runner image (`macos-26`) and selects Xcode explicitly by listing
  `/Applications/Xcode_*.app` and choosing the newest ≥ 26 (needed for the iOS 26 SDK / Liquid Glass APIs).
  The job fails loudly if no suitable Xcode is installed instead of silently using an older SDK.
- Unsigned device build: `flutter build ios --release --no-codesign` → `build/ios/iphoneos/Runner.app`
  (arm64 device binary). The workflow verifies with `lipo -info`, `otool -l` (LC_BUILD_VERSION platform IOS,
  minos 18.0), `plutil` on Info.plist, and lists embedded frameworks, then zips `Payload/Runner.app` into
  `RemotePC-AI-unsigned.ipa`.
- A simulator build is never packaged as an IPA.
- An optional signed job runs only when signing secrets exist (`IOS_P12_BASE64`, `IOS_P12_PASSWORD`,
  `IOS_PROFILE_BASE64`, `IOS_TEAM_ID`).

## Consequences
An unsigned IPA cannot be installed until re-signed (by the user's own signing tool). Frameworks inside
`Frameworks/` (Flutter.framework, WebRTC.framework, plugins) must be re-signed too; the user's tool must
support deep signing. Entitlements needed: none beyond defaults in v1 (no push, no App Groups).
