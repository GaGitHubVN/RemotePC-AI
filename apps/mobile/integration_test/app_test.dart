// End-to-end UI test on an iOS Simulator / Android emulator against tools/RemotePC.TestHost (real RemotePC.Core:
// pairing, E2E envelopes, LAN signaling, WebRTC video + data channels, RPC, file transfer).
//
// The harness (.github/workflows/mobile-e2e.yml) starts the test host, passes its pairing URI with
// --dart-define=PAIR_URI=..., and takes a native screenshot (xcrun simctl io … screenshot) whenever this test prints
// "SHOT:<name>". Native views (UITabBar, SwiftUI glass) cannot be tapped from a Flutter test, so the test drives the
// same functions those buttons call; everything Flutter-drawn is tapped normally.
import 'dart:async';
import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:path_provider/path_provider.dart';
import 'package:remotepc_ai/app_state.dart';
import 'package:remotepc_ai/core/host_link.dart';
import 'package:remotepc_ai/main.dart' as app;
import 'package:remotepc_ai/screens/home.dart';
import 'package:remotepc_ai/screens/settings.dart';
import 'package:remotepc_ai/ui/platform_ui.dart';
import 'package:shared_preferences/shared_preferences.dart';

const pairUri = String.fromEnvironment('PAIR_URI');
const pairHttp = String.fromEnvironment('PAIR_HTTP');

/// A fresh single-use offer from the test host (offers expire after 5 minutes).
Future<String> freshPairUri() async {
  if (pairHttp.isEmpty) return pairUri;
  final c = HttpClient();
  try {
    final r = await (await c.getUrl(Uri.parse(pairHttp))).close();
    return await r.transform(const SystemEncoding().decoder).join();
  } finally {
    c.close();
  }
}
const expectStyle = String.fromEnvironment('EXPECT_STYLE');

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  binding.framePolicy = LiveTestWidgetsFlutterBindingFramePolicy.fullyLive;

  Future<void> settle(WidgetTester t, [int ms = 1500]) async {
    final end = DateTime.now().add(Duration(milliseconds: ms));
    while (DateTime.now().isBefore(end)) {
      await t.pump(const Duration(milliseconds: 50));
    }
  }

  Future<void> shot(WidgetTester t, String name) async {
    await settle(t, 1200);
    // The test host takes the native simulator screenshot synchronously (xcrun simctl io … screenshot).
    final c = HttpClient()..connectionTimeout = const Duration(seconds: 10);
    try {
      // A screenshot is evidence, not part of the flow under test: never let it hang or fail the test.
      final r = await (await c.getUrl(Uri.parse('${pairHttp}shot/$name'))).close().timeout(const Duration(seconds: 45));
      // ignore: avoid_print
      print('SHOT:$name → ${await r.transform(const SystemEncoding().decoder).join().timeout(const Duration(seconds: 10))}');
    } catch (e) {
      // ignore: avoid_print
      print('SHOT:$name failed: $e');
    } finally {
      c.close();
    }
  }

  Future<void> waitFor(WidgetTester t, bool Function() cond, String what, {int seconds = 40, String Function()? detail}) async {
    final end = DateTime.now().add(Duration(seconds: seconds));
    while (!cond()) {
      if (DateTime.now().isAfter(end)) fail('timed out waiting for $what${detail == null ? '' : ' [${detail()}]'}');
      await t.pump(const Duration(milliseconds: 100));
    }
  }

  testWidgets('pair, connect, remote video, input, chat, files, settings', (t) async {
    expect(pairUri.isNotEmpty || pairHttp.isNotEmpty, isTrue, reason: 'run with --dart-define=PAIR_HTTP=… or PAIR_URI=…');
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('onboarded', true);
    await prefs.setString('language', 'vi');

    await app.main();
    await waitFor(t, () => AppState.instance.ready, 'app init');
    // ignore: avoid_print
    print('UI style: ${PlatformUi.style.name} (OS major ${PlatformUi.osMajor})');
    if (expectStyle.isNotEmpty) expect(PlatformUi.style.name, expectStyle, reason: 'wrong UI branch for this OS');
    final s = AppState.instance;
    for (final h in [...s.hosts]) {
      await s.removeHost(h);
    }
    await shot(t, '01-home-empty');

    // ---- pairing (same code path as the QR scanner; the test host auto-approves) ----
    final inv = PairingInvite.parseQr(await freshPairUri());
    String? verify;
    final host = await HostLink.pair(s.keys, inv, 'Simulator', s.platformName, (c) => verify = c);
    expect(verify, hasLength(6));
    await s.addHost(host);
    await shot(t, '02-home-paired');

    // ---- "Điều khiển màn hình" → remote desktop over WebRTC ----
    final homeCtx = t.element(find.byType(HomeScreen, skipOffstage: false));
    openRemote(homeCtx, host);
    final link = s.link(host);
    await waitFor(t, () => link.isConnected, 'WebRTC connected (state=${link.state.name} error=${link.error})', seconds: 120);
    await waitFor(t, () => link.stats.framesDecoded > 5, 'decoded video frames', seconds: 60, detail: () => '${link.stats}');
    // ignore: avoid_print
    print('video: ${link.stats.width}x${link.stats.height} decoded=${link.stats.framesDecoded} codec=${link.stats.codec} path=${link.stats.path}');
    await shot(t, '03-remote');
    // Flutter-drawn video surface: a real tap goes through the gesture code → input data channel → host log "INPUT button left".
    await t.tapAt(t.getCenter(find.byType(app.RemotePcApp)));
    await settle(t, 800);
    final ping = await link.call('ping', {'t': 1});
    expect(ping, isA<Map<String, dynamic>>());

    // ---- chat (RPC ai.status; no local model on CI) ----
    Navigator.of(t.element(find.byType(app.RootShell, skipOffstage: false)), rootNavigator: true).pop();
    await settle(t);
    openChat(t.element(find.byType(HomeScreen, skipOffstage: false)), host);
    await settle(t, 2500);
    await t.enterText(find.byType(EditableText).first, 'Xin chào máy tính 👋');
    await shot(t, '04-chat');
    Navigator.of(t.element(find.byType(app.RootShell, skipOffstage: false)), rootNavigator: true).pop();
    await settle(t);

    // ---- files (fs.roots / fs.list / download with SHA-256 check / upload) ----
    openFiles(t.element(find.byType(HomeScreen, skipOffstage: false)), host);
    await waitFor(t, () => find.text('Documents').evaluate().isNotEmpty, 'fs.roots listing');
    await shot(t, '05-files-roots');
    await t.tap(find.text('Documents'));
    await waitFor(t, () => find.text('Ghi chú.txt').evaluate().isNotEmpty, 'fs.list of Documents');
    await shot(t, '06-files-documents');
    final f = await link.download('Documents/report.bin');
    expect(await f.length(), 300 * 1024);
    final tmp = File('${(await getTemporaryDirectory()).path}/upload-test.txt')..writeAsStringSync('upload from simulator ✓');
    final remote = await link.upload(tmp, folder: 'Received');
    expect(remote, contains('upload-test'));

    // ---- tabs ----
    if (PlatformUi.isGlass && NativeTabBar.active) {
      NativeTabBar.select(3); // native UITabBarController path
    } else {
      await t.tap(find.text('Cài đặt').last);
    }
    await waitFor(t, () => find.byType(SettingsScreen).evaluate().isNotEmpty, 'settings tab');
    await shot(t, '07-settings');
    if (PlatformUi.isGlass && NativeTabBar.active) {
      NativeTabBar.select(1);
    } else {
      await t.tap(find.text('AI').last);
    }
    await shot(t, '08-ai-tab');

    // ---- dark mode through the app's own Theme setting (also switches native chrome) ----
    s.themeMode = 'dark';
    await s.savePrefs();
    await PlatformUi.setThemeMode('dark');
    if (PlatformUi.isGlass && NativeTabBar.active) {
      NativeTabBar.select(0);
    } else {
      await t.tap(find.text('Máy tính').last);
    }
    await shot(t, '09-dark-home');
    openRemote(t.element(find.byType(HomeScreen, skipOffstage: false)), host);
    await shot(t, '10-dark-remote');
    Navigator.of(t.element(find.byType(app.RootShell, skipOffstage: false)), rootNavigator: true).pop();
    await settle(t);
    s.themeMode = 'system';
    await s.savePrefs();

    await link.disconnect();
    await settle(t);
  });
}
