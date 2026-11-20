import 'dart:convert';
import 'dart:io';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:path_provider/path_provider.dart';

import 'protocol/crypto.dart';

/// A Windows host this phone has paired with. Public keys only; nothing secret.
class PairedHost {
  final String id;
  String name;
  final String edPk;
  final String xPk;
  String? signalingUrl;
  List<String> lanEndpoints;
  List<String> permissions;
  int? lastConnected;
  String? resumeSid;
  String? resumeToken;

  PairedHost({required this.id, required this.name, required this.edPk, required this.xPk, this.signalingUrl,
      this.lanEndpoints = const [], this.permissions = const [], this.lastConnected, this.resumeSid, this.resumeToken});

  bool can(String p) => permissions.contains(p);

  Map<String, dynamic> toJson() => {
        'id': id, 'name': name, 'edPk': edPk, 'xPk': xPk, 'signalingUrl': signalingUrl, 'lan': lanEndpoints,
        'permissions': permissions, 'lastConnected': lastConnected, 'resumeSid': resumeSid, 'resumeToken': resumeToken,
      };

  static PairedHost fromJson(Map<String, dynamic> j) => PairedHost(
        id: j['id'], name: j['name'], edPk: j['edPk'], xPk: j['xPk'], signalingUrl: j['signalingUrl'],
        lanEndpoints: List<String>.from(j['lan'] ?? const []), permissions: List<String>.from(j['permissions'] ?? const []),
        lastConnected: j['lastConnected'], resumeSid: j['resumeSid'], resumeToken: j['resumeToken'],
      );
}

/// Keys live in the Keychain (iOS) / Android Keystore-backed EncryptedSharedPreferences.
class SecureStore {
  static const _storage = FlutterSecureStorage(
    iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock_this_device),
  );

  static Future<DeviceKeys> loadOrCreateKeys() async {
    final existing = await _storage.read(key: 'device_keys_v1');
    if (existing != null) return DeviceKeys.importSecret(fromB64u(existing));
    final keys = await DeviceKeys.generate();
    await _storage.write(key: 'device_keys_v1', value: b64u(await keys.exportSecret()));
    return keys;
  }

  static Future<List<PairedHost>> loadHosts() async {
    final raw = await _storage.read(key: 'hosts_v1');
    if (raw == null) return [];
    return (jsonDecode(raw) as List).map((e) => PairedHost.fromJson(e as Map<String, dynamic>)).toList();
  }

  static Future<void> saveHosts(List<PairedHost> hosts) =>
      _storage.write(key: 'hosts_v1', value: jsonEncode(hosts.map((h) => h.toJson()).toList()));

  /// Wipes identity and hosts (Settings → Reset). A new identity means re-pairing.
  static Future<void> wipe() => _storage.deleteAll();
}

/// Chat history stays on the phone (app-private storage); never uploaded anywhere.
class ChatStore {
  static Future<Directory> _dir() async {
    final d = Directory('${(await getApplicationSupportDirectory()).path}/chats');
    if (!d.existsSync()) d.createSync(recursive: true);
    return d;
  }

  static Future<List<Map<String, dynamic>>> list() async {
    final d = await _dir();
    final out = <Map<String, dynamic>>[];
    for (final f in d.listSync().whereType<File>().where((f) => f.path.endsWith('.json'))) {
      try {
        out.add(jsonDecode(await f.readAsString()) as Map<String, dynamic>);
      } catch (_) {/* skip corrupt file */}
    }
    out.sort((a, b) => (b['updated'] as int).compareTo(a['updated'] as int));
    return out;
  }

  static Future<void> save(Map<String, dynamic> chat) async {
    final d = await _dir();
    chat['updated'] = DateTime.now().millisecondsSinceEpoch;
    final f = File('${d.path}/${chat['id']}.json');
    final tmp = File('${f.path}.tmp');
    await tmp.writeAsString(jsonEncode(chat));
    await tmp.rename(f.path);
  }

  static Future<void> delete(String id) async {
    final f = File('${(await _dir()).path}/$id.json');
    if (f.existsSync()) await f.delete();
  }
}
