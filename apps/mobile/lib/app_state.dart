import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'core/host_link.dart';
import 'core/protocol/crypto.dart';
import 'core/storage.dart';

/// App-wide state: identity, paired PCs and their live connections.
class AppState extends ChangeNotifier {
  static final AppState instance = AppState._();
  AppState._();

  late DeviceKeys keys;
  List<PairedHost> hosts = [];
  final Map<String, HostLink> _links = {};
  bool ready = false;
  bool onboarded = false;
  String language = 'vi';
  String themeMode = 'system';
  bool reduceMotion = false;
  bool trackpadMode = true;
  double pointerSensitivity = 1.4;
  bool autoClipboard = false;
  /// Show each video frame the moment it is decoded (WebRTC playout delay 0). Lowest latency, but on the iOS 26
  /// simulator no frame was decoded at all with it, so iOS keeps it off by default. Read once at startup.
  bool zeroDelay = Platform.isAndroid;

  Future<void> init() async {
    keys = await SecureStore.loadOrCreateKeys();
    hosts = await SecureStore.loadHosts();
    final p = await SharedPreferences.getInstance();
    onboarded = p.getBool('onboarded') ?? false;
    language = p.getString('language') ?? (Platform.localeName.startsWith('vi') ? 'vi' : 'en');
    themeMode = p.getString('theme') ?? 'system';
    reduceMotion = p.getBool('reduceMotion') ?? false;
    trackpadMode = p.getBool('trackpad') ?? true;
    pointerSensitivity = p.getDouble('sensitivity') ?? 1.4;
    autoClipboard = p.getBool('autoClipboard') ?? false;
    zeroDelay = p.getBool('zeroDelay') ?? Platform.isAndroid;
    ready = true;
    notifyListeners();
  }

  Future<void> savePrefs() async {
    final p = await SharedPreferences.getInstance();
    await p.setBool('onboarded', onboarded);
    await p.setString('language', language);
    await p.setString('theme', themeMode);
    await p.setBool('reduceMotion', reduceMotion);
    await p.setBool('trackpad', trackpadMode);
    await p.setDouble('sensitivity', pointerSensitivity);
    await p.setBool('autoClipboard', autoClipboard);
    await p.setBool('zeroDelay', zeroDelay);
    notifyListeners();
  }

  HostLink link(PairedHost h) => _links.putIfAbsent(h.id, () => HostLink(keys, h)..addListener(() => saveHosts()));

  Future<void> addHost(PairedHost h) async {
    hosts.removeWhere((x) => x.id == h.id);
    hosts.add(h);
    await saveHosts();
  }

  Future<void> removeHost(PairedHost h) async {
    await _links.remove(h.id)?.disconnect();
    hosts.removeWhere((x) => x.id == h.id);
    await saveHosts();
  }

  Future<void> saveHosts() async {
    await SecureStore.saveHosts(hosts);
    notifyListeners();
  }

  Future<void> resetIdentity() async {
    for (final l in _links.values) {
      await l.disconnect();
    }
    _links.clear();
    await SecureStore.wipe();
    keys = await SecureStore.loadOrCreateKeys();
    hosts = [];
    notifyListeners();
  }

  String get platformName => Platform.isIOS ? 'ios' : Platform.isAndroid ? 'android' : Platform.operatingSystem;
  String get deviceName => Platform.isIOS ? 'iPhone' : 'Android ${Platform.operatingSystemVersion.split(' ').first}';
}
