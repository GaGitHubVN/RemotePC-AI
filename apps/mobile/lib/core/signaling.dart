import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:web_socket_channel/io.dart';

import 'protocol/crypto.dart';

enum SignalState { disconnected, connecting, online }

/// Authenticated connection to a signaling endpoint (public wss:// server or the host's LAN ws:// endpoint).
/// PROTOCOL.md §2. The transport is untrusted: envelopes are end-to-end encrypted.
class SignalingConnection {
  final Uri url;
  final DeviceKeys keys;
  IOWebSocketChannel? _ch;
  StreamSubscription? _sub;
  final _envelopes = StreamController<Envelope>.broadcast();
  final _errors = StreamController<String>.broadcast();
  SignalState state = SignalState.disconnected;
  Completer<Map<String, dynamic>>? _turn;
  Timer? _ping;
  DateTime? _pingSent;
  Duration? rtt;

  SignalingConnection(this.url, this.keys) {
    final privateHost = _isPrivate(url.host);
    if (url.scheme != 'wss' && !(url.scheme == 'ws' && privateHost)) {
      throw ArgumentError('Internet signaling must use wss://');
    }
  }

  Stream<Envelope> get envelopes => _envelopes.stream;
  Stream<String> get errors => _errors.stream;

  static bool _isPrivate(String host) {
    final ip = InternetAddress.tryParse(host);
    if (ip == null) return false;
    if (ip.isLoopback || ip.isLinkLocal) return true;
    final b = ip.rawAddress;
    return ip.type == InternetAddressType.IPv4 &&
        (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
  }

  /// Connects and authenticates. Throws on failure (caller decides on retry/backoff).
  Future<void> connect({Duration timeout = const Duration(seconds: 6)}) async {
    state = SignalState.connecting;
    final ws = await WebSocket.connect(url.toString()).timeout(timeout);
    ws.pingInterval = const Duration(seconds: 20);
    _ch = IOWebSocketChannel(ws);
    final first = Completer<void>();
    _sub = _ch!.stream.listen((raw) async {
      if (raw is! String || raw.length > 65536) return;
      final m = jsonDecode(raw) as Map<String, dynamic>;
      switch (m['t']) {
        case 'challenge':
          final role = 'client';
          final sig = await keys.sign(utf8.encode('remotepc-signal-v1\n${m['nonce']}\n${keys.deviceId}\n$role'));
          _send({'t': 'hello', 'v': 1, 'role': role, 'id': keys.deviceId, 'edPk': b64u(keys.edPk), 'sig': b64u(sig)});
        case 'welcome':
          state = SignalState.online;
          if (!first.isCompleted) first.complete();
          _ping = Timer.periodic(const Duration(seconds: 15), (_) {
            _pingSent = DateTime.now();
            _send({'t': 'ping'});
          });
        case 'pong':
          if (_pingSent != null) rtt = DateTime.now().difference(_pingSent!);
        case 'relay':
          final env = Envelope.fromJson(m['env'] as Map<String, dynamic>);
          if (env.from == m['from']) _envelopes.add(env);
        case 'turn':
          _turn?.complete(m);
        case 'error':
          _errors.add('${m['code']}');
      }
    }, onDone: () {
      state = SignalState.disconnected;
      _ping?.cancel();
      if (!first.isCompleted) first.completeError(StateError('connection closed during authentication'));
      _errors.add('closed');
    }, onError: (Object e) {
      if (!first.isCompleted) first.completeError(e);
    });
    await first.future.timeout(timeout);
  }

  void _send(Map<String, dynamic> m) => _ch?.sink.add(jsonEncode(m));

  bool send(Envelope env) {
    if (state != SignalState.online) return false;
    _send({'t': 'relay', 'to': env.to, 'env': env.toJson()});
    return true;
  }

  Future<List<Map<String, dynamic>>> turnServers() async {
    if (state != SignalState.online) return [];
    _turn = Completer();
    _send({'t': 'turn'});
    try {
      final m = await _turn!.future.timeout(const Duration(seconds: 3));
      return List<Map<String, dynamic>>.from(m['iceServers'] ?? const []);
    } on TimeoutException {
      return [];
    }
  }

  Future<void> close() async {
    _ping?.cancel();
    await _sub?.cancel();
    await _ch?.sink.close();
    state = SignalState.disconnected;
  }
}

/// LAN discovery (PROTOCOL.md §6). On iOS, UDP broadcast needs Apple's multicast entitlement; without it this
/// returns nothing and the app relies on the LAN addresses carried in the QR code or manual entry.
class LanDiscovery {
  static Future<List<Map<String, dynamic>>> discover({Duration wait = const Duration(seconds: 2)}) async {
    final found = <String, Map<String, dynamic>>{};
    RawDatagramSocket? sock;
    try {
      sock = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
      sock.broadcastEnabled = true;
      sock.listen((ev) {
        if (ev != RawSocketEvent.read) return;
        final d = sock!.receive();
        if (d == null || d.data.length > 2048) return;
        try {
          final m = jsonDecode(utf8.decode(d.data)) as Map<String, dynamic>;
          if (m['t'] == 'host' && m['v'] == 1 && m['id'] is String) {
            m['address'] = d.address.address;
            found[m['id']] = m;
          }
        } catch (_) {}
      });
      final probe = utf8.encode('{"t":"discover","v":1}');
      for (var i = 0; i < 3; i++) {
        sock.send(probe, InternetAddress('255.255.255.255'), 45821);
        await Future<void>.delayed(wait ~/ 3);
      }
    } catch (_) {
      // permission or entitlement missing: discovery unavailable
    } finally {
      sock?.close();
    }
    return found.values.toList();
  }
}
