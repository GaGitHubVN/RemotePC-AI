import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'package:crypto/crypto.dart' as hash;
import 'package:flutter/foundation.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';
import 'package:path_provider/path_provider.dart';

import 'protocol/crypto.dart';
import 'signaling.dart';
import 'storage.dart';

enum LinkState { idle, connectingTransport, openingSession, negotiating, connected, reconnecting, failed, closed }

class RpcError implements Exception {
  final String code, message;
  RpcError(this.code, this.message);
  @override
  String toString() => message;
}

class StreamStatsSnapshot {
  double fps = 0, kbps = 0, rttMs = 0, jitterMs = 0, decodeMs = 0, jitterBufferMs = 0;
  int packetsLost = 0, framesDecoded = 0, width = 0, height = 0;
  String path = '—'; // direct-lan | direct-stun | relay
  String codec = '';
}

/// Pairing data scanned from the host's QR code (PROTOCOL.md §4).
class PairingInvite {
  final String hostId, edPk, xPk, hostName;
  final Uint8List secret;
  final String? signalingUrl;
  final List<String> lan;
  final bool isCode;
  PairingInvite({required this.hostId, required this.edPk, required this.xPk, required this.hostName, required this.secret,
      this.signalingUrl, this.lan = const [], this.isCode = false});

  static PairingInvite parseQr(String text) {
    final u = Uri.parse(text);
    if (u.scheme != 'remotepc' || u.host != 'pair' || u.queryParameters['v'] != '1') {
      throw const FormatException('Not a RemotePC AI pairing code');
    }
    final q = u.queryParameters;
    final exp = int.tryParse(q['exp'] ?? '') ?? 0;
    if (exp != 0 && exp < DateTime.now().millisecondsSinceEpoch) throw const FormatException('This pairing code has expired');
    final h = q['h']!, e = q['e']!, x = q['x']!;
    if (deviceIdOf(fromB64u(e)) != h) throw const FormatException('Pairing code is inconsistent');
    final sig = q['sig'];
    if (sig != null && !sig.startsWith('wss://')) throw const FormatException('Signaling URL must be wss://');
    return PairingInvite(hostId: h, edPk: e, xPk: x, hostName: q['n'] ?? 'PC', secret: fromB64u(q['s']!), signalingUrl: sig,
        lan: (q['lan'] ?? '').split(',').where((s) => s.isNotEmpty).toList());
  }

  /// One-time code + a host found by LAN discovery (or typed address).
  static PairingInvite fromCode(Map<String, dynamic> discovered, String code) => PairingInvite(
        hostId: discovered['id'], edPk: discovered['edPk'], xPk: discovered['xPk'], hostName: discovered['name'] ?? 'PC',
        secret: utf8Bytes(code), lan: ['${discovered['address']}:${discovered['ws']}'], isCode: true);
}

/// Connection to one paired Windows host.
class HostLink extends ChangeNotifier {
  final DeviceKeys keys;
  final PairedHost host;
  final _guard = ReplayGuard();
  final _seq = SeqCounter();
  Uint8List? _key;
  SignalingConnection? _sig;
  StreamSubscription? _envSub;
  RTCPeerConnection? _pc;
  RTCDataChannel? _ctl, _mv, _in, _file;
  final _pending = <int, Completer<Map<String, dynamic>>>{};
  final _events = StreamController<Map<String, dynamic>>.broadcast();
  final _fileFrames = StreamController<Uint8List>.broadcast();
  final _sessionWaiters = <String, Completer<Map<String, dynamic>>>{};
  int _rpcId = 0;
  int _moveSeq = 0;
  Timer? _statsTimer, _retryTimer;
  int _attempt = 0;
  bool _userClosed = false;

  LinkState state = LinkState.idle;
  String? error;
  String? sid;
  String transport = '';
  MediaStream? remoteStream;
  List<Map<String, dynamic>> displays = [];
  int currentDisplay = 0;
  final stats = StreamStatsSnapshot();
  double? inputRttMs;

  HostLink(this.keys, this.host);

  Stream<Map<String, dynamic>> get events => _events.stream;
  bool get isConnected => state == LinkState.connected;

  void _set(LinkState s, [String? err]) {
    state = s;
    error = err;
    notifyListeners();
  }

  // ---------------- transport ----------------

  Future<Uint8List> _envKey() async => _key ??= await envelopeKey(keys, fromB64u(host.xPk), host.id);

  /// LAN endpoints first (fast, works offline), then the Internet signaling server.
  static Future<(SignalingConnection, String)> openTransport(DeviceKeys keys, List<String> lan, String? internet) async {
    final attempts = <Future<(SignalingConnection, String)>>[];
    for (final ep in lan) {
      attempts.add(() async {
        final c = SignalingConnection(Uri.parse('ws://$ep/signal'), keys);
        await c.connect(timeout: const Duration(seconds: 3));
        return (c, 'LAN $ep');
      }());
    }
    if (attempts.isNotEmpty) {
      try {
        return await _firstSuccess(attempts);
      } catch (_) {/* fall through to Internet */}
    }
    if (internet != null && internet.isNotEmpty) {
      final c = SignalingConnection(Uri.parse(internet), keys);
      await c.connect(timeout: const Duration(seconds: 8));
      return (c, 'Internet');
    }
    throw StateError('PC not reachable: no LAN endpoint answered and no Internet signaling server is configured.');
  }

  static Future<T> _firstSuccess<T>(List<Future<T>> fs) {
    final c = Completer<T>();
    var failures = 0;
    for (final f in fs) {
      f.then((v) {
        if (!c.isCompleted) c.complete(v);
      }, onError: (Object e) {
        if (++failures == fs.length && !c.isCompleted) c.completeError(e);
      });
    }
    return c.future;
  }

  Future<void> _send(String typ, Object payload, {String sid = ''}) async {
    final env = await sealEnvelope(await _envKey(),
        Envelope(typ: typ, from: keys.deviceId, to: host.id, sid: sid, seq: _seq.next(), ts: DateTime.now().millisecondsSinceEpoch, n: ''), payload);
    if (!(_sig?.send(env) ?? false)) throw StateError('not connected to the PC');
  }

  Future<void> _onEnvelope(Envelope env) async {
    if (env.to != keys.deviceId || env.from != host.id) return;
    final now = DateTime.now().millisecondsSinceEpoch;
    if (_guard.check(env, now) != null) return;
    final Map<String, dynamic> p;
    try {
      p = await openEnvelope(await _envKey(), env) as Map<String, dynamic>;
    } catch (_) {
      return; // forged or corrupted
    }
    _guard.accept(env, now);
    switch (env.typ) {
      case 'session.accept':
      case 'session.reject':
        _sessionWaiters.remove(p['reqId'])?.complete({...p, '_typ': env.typ});
      case 'webrtc.answer':
        if (env.sid == sid) await _pc?.setRemoteDescription(RTCSessionDescription(p['sdp'] as String, 'answer'));
      case 'webrtc.ice':
        if (env.sid == sid && (p['candidate'] as String?)?.isNotEmpty == true) {
          await _pc?.addCandidate(RTCIceCandidate(p['candidate'], p['sdpMid'], p['sdpMLineIndex']));
        }
      case 'session.close':
        if (env.sid == sid) {
          final reason = p['reason'] as String? ?? 'closed';
          await _teardownPeer();
          if (reason == 'revoked') {
            _set(LinkState.failed, 'This phone was removed on the PC. Pair again to reconnect.');
          } else if (reason == 'replaced') {
            // another connection from this phone replaced us (e.g. after a network switch) — nothing to do
          } else {
            _set(LinkState.closed, reason);
          }
        }
    }
  }

  // ---------------- pairing ----------------

  /// Sends a pair.request and waits for the owner's decision on the PC.
  /// [onVerifyCode] receives the 6-digit code the user must compare with the PC screen.
  static Future<PairedHost> pair(DeviceKeys keys, PairingInvite inv, String deviceName, String platform,
      void Function(String code) onVerifyCode) async {
    final (sig, _) = await openTransport(keys, inv.lan, inv.signalingUrl);
    try {
      final pairNonce = b64u(randomBytes(16));
      final transcript = pairTranscript(hostId: inv.hostId, clientId: keys.deviceId, clientEdPk: b64u(keys.edPk),
          clientXPk: b64u(keys.xPk), hostEdPk: inv.edPk, hostXPk: inv.xPk, pairNonce: pairNonce);
      onVerifyCode(verifyCode(transcript));
      final key = await envelopeKey(keys, fromB64u(inv.xPk), inv.hostId);
      final env = await sealEnvelope(key, Envelope(typ: 'pair.request', from: keys.deviceId, to: inv.hostId, seq: SeqCounter().next(),
          ts: DateTime.now().millisecondsSinceEpoch, n: '', kx: {'edPk': b64u(keys.edPk), 'xPk': b64u(keys.xPk)}), {
        'pairNonce': pairNonce, 'proof': b64u(await pairProof(inv.secret, transcript)),
        'deviceName': deviceName, 'platform': platform, 'appVersion': '0.1.0',
      });
      final result = Completer<Map<String, dynamic>>();
      final sub = sig.envelopes.listen((e) async {
        if (e.typ != 'pair.result' || e.from != inv.hostId) return;
        try {
          result.complete(await openEnvelope(key, e) as Map<String, dynamic>);
        } catch (_) {}
      });
      final errSub = sig.errors.listen((e) {
        if (e == 'peer_offline' && !result.isCompleted) result.completeError(StateError('The PC is offline.'));
      });
      if (!sig.send(env)) throw StateError('could not reach the PC');
      final r = await result.future.timeout(const Duration(minutes: 3), onTimeout: () => {'ok': false, 'reason': 'timeout'});
      await sub.cancel();
      await errSub.cancel();
      if (r['ok'] != true) {
        throw StateError(switch (r['reason']) {
          'expired' => 'The pairing code expired. Create a new one on the PC.',
          'bad_proof' => 'Wrong code.',
          'locked' => 'Too many wrong attempts. Create a new code on the PC.',
          'rejected' => 'The PC owner rejected this phone.',
          _ => 'Pairing failed (${r['reason']}).',
        });
      }
      return PairedHost(id: inv.hostId, name: r['hostName'] ?? inv.hostName, edPk: inv.edPk, xPk: inv.xPk,
          signalingUrl: inv.signalingUrl, lanEndpoints: inv.lan, permissions: List<String>.from(r['permissions'] ?? const []));
    } finally {
      await sig.close();
    }
  }

  // ---------------- session ----------------

  Future<void> connect() async {
    _userClosed = false;
    _retryTimer?.cancel();
    try {
      _set(LinkState.connectingTransport);
      await _sig?.close();
      await _envSub?.cancel();
      final (sig, how) = await openTransport(keys, host.lanEndpoints, host.signalingUrl);
      _sig = sig;
      transport = how;
      _envSub = sig.envelopes.listen(_onEnvelope);
      sig.errors.listen((e) {
        if (e == 'closed' && !_userClosed && state != LinkState.closed) _scheduleReconnect('transport closed');
      });

      _set(LinkState.openingSession);
      final reqId = b64u(randomBytes(6));
      final waiter = Completer<Map<String, dynamic>>();
      _sessionWaiters[reqId] = waiter;
      await _send('session.open', {
        'reqId': reqId,
        if (host.resumeSid != null) 'resume': {'sid': host.resumeSid, 'token': host.resumeToken},
        'client': {'codecs': ['H264']},
      });
      final acc = await waiter.future.timeout(const Duration(seconds: 15));
      if (acc['_typ'] == 'session.reject') {
        throw StateError(switch (acc['reason']) {
          'locked' => 'Remote access is locked on the PC.',
          'busy' => 'The PC reached its maximum number of sessions.',
          _ => 'The PC rejected the session (${acc['reason']}).',
        });
      }
      sid = acc['sid'];
      host.resumeSid = sid;
      host.resumeToken = acc['token'];
      host.permissions = List<String>.from(acc['permissions'] ?? const []);
      displays = List<Map<String, dynamic>>.from(acc['displays'] ?? const []);
      await _startPeer(List<Map<String, dynamic>>.from(acc['iceServers'] ?? const []));
      _attempt = 0;
    } catch (e) {
      _set(LinkState.failed, e.toString().replaceFirst('Bad state: ', ''));
      if (!_userClosed) _scheduleReconnect(error ?? 'failed');
    }
  }

  void _scheduleReconnect(String why) {
    if (_userClosed || (error?.contains('removed on the PC') ?? false)) return;
    _retryTimer?.cancel();
    final delay = Duration(milliseconds: (min(30000, 1000 * pow(2, min(_attempt, 5)).toInt()) * (0.8 + Random().nextDouble() * 0.4)).toInt());
    _attempt++;
    _set(LinkState.reconnecting, '$why — retrying in ${delay.inSeconds}s');
    _retryTimer = Timer(delay, connect);
  }

  /// "Retry now" button.
  void retryNow() {
    _retryTimer?.cancel();
    connect();
  }

  Future<void> _startPeer(List<Map<String, dynamic>> iceServers) async {
    await _teardownPeer();
    _set(LinkState.negotiating);
    final pc = await createPeerConnection({
      'iceServers': iceServers.isEmpty ? [{'urls': 'stun:stun.l.google.com:19302'}] : iceServers,
      'sdpSemantics': 'unified-plan',
      'bundlePolicy': 'max-bundle',
    });
    _pc = pc;
    await pc.addTransceiver(kind: RTCRtpMediaType.RTCRtpMediaTypeVideo, init: RTCRtpTransceiverInit(direction: TransceiverDirection.RecvOnly));
    pc.onTrack = (e) {
      if (e.streams.isNotEmpty) {
        remoteStream = e.streams.first;
        notifyListeners();
      }
    };
    pc.onIceCandidate = (c) {
      if (c.candidate != null) _send('webrtc.ice', {'candidate': c.candidate, 'sdpMid': c.sdpMid, 'sdpMLineIndex': c.sdpMLineIndex}, sid: sid!).catchError((_) {});
    };
    pc.onConnectionState = (s) {
      if (s == RTCPeerConnectionState.RTCPeerConnectionStateConnected) {
        _set(LinkState.connected);
        host.lastConnected = DateTime.now().millisecondsSinceEpoch;
        _statsTimer?.cancel();
        _statsTimer = Timer.periodic(const Duration(seconds: 1), (_) => _collectStats());
        _measureRtt();
      } else if (s == RTCPeerConnectionState.RTCPeerConnectionStateFailed || s == RTCPeerConnectionState.RTCPeerConnectionStateDisconnected) {
        // Network switch (Wi-Fi ↔ cellular) or path loss: resume with a fresh peer connection; queued input is dropped.
        if (!_userClosed) _scheduleReconnect('connection lost');
      }
    };
    _ctl = await pc.createDataChannel('ctl', RTCDataChannelInit()..ordered = true);
    _mv = await pc.createDataChannel('mv', RTCDataChannelInit()
      ..ordered = false
      ..maxRetransmits = 0);
    _in = await pc.createDataChannel('in', RTCDataChannelInit()..ordered = true);
    _file = await pc.createDataChannel('file', RTCDataChannelInit()..ordered = true);
    _ctl!.onMessage = (m) {
      if (m.isBinary) return;
      final o = jsonDecode(m.text) as Map<String, dynamic>;
      if (o['ev'] != null) {
        _events.add(o);
      } else if (o['id'] != null) {
        _pending.remove(o['id'])?.complete(o);
      }
    };
    _file!.onMessage = (m) {
      if (m.isBinary) _fileFrames.add(m.binary);
    };
    final offer = await pc.createOffer({'offerToReceiveVideo': true});
    await pc.setLocalDescription(offer);
    await _send('webrtc.offer', {'sdp': offer.sdp}, sid: sid!);
  }

  Future<void> _teardownPeer() async {
    _statsTimer?.cancel();
    for (final c in _pending.values) {
      if (!c.isCompleted) c.completeError(RpcError('disconnected', 'Disconnected from the PC'));
    }
    _pending.clear();
    await _pc?.close();
    _pc = null;
    _ctl = _mv = _in = _file = null;
    remoteStream = null;
  }

  Future<void> disconnect() async {
    _userClosed = true;
    _retryTimer?.cancel();
    if (sid != null) await _send('session.close', {'reason': 'client_closed'}, sid: sid!).catchError((_) {});
    await _teardownPeer();
    await _sig?.close();
    _set(LinkState.closed);
  }

  // ---------------- RPC ----------------

  Future<Map<String, dynamic>> call(String method, [Map<String, dynamic>? params, Duration timeout = const Duration(seconds: 30)]) async {
    final dc = _ctl;
    if (dc == null || dc.state != RTCDataChannelState.RTCDataChannelOpen) throw RpcError('disconnected', 'Not connected to the PC');
    final id = ++_rpcId;
    final c = Completer<Map<String, dynamic>>();
    _pending[id] = c;
    dc.send(RTCDataChannelMessage(jsonEncode({'id': id, 'm': method, 'p': params})));
    final r = await c.future.timeout(timeout, onTimeout: () {
      _pending.remove(id);
      throw RpcError('timeout', 'The PC did not answer in time');
    });
    if (r['e'] != null) throw RpcError(r['e']['code'] ?? 'error', r['e']['msg'] ?? 'error');
    final result = r['r'];
    if (result is List) return {'_list': result}; // array results (agent.list, fs.roots, fs.search)
    return (result as Map<String, dynamic>?) ?? {};
  }

  /// Calls a streaming method (e.g. ai.chat). [onEvent] receives events correlated with this request.
  Future<Map<String, dynamic>> callStreaming(String method, Map<String, dynamic> params, void Function(Map<String, dynamic> ev) onEvent,
      {Duration timeout = const Duration(minutes: 10)}) async {
    final id = _rpcId + 1;
    final sub = events.where((e) => e['id'] == id).listen(onEvent);
    try {
      return await call(method, params, timeout);
    } finally {
      await sub.cancel();
    }
  }

  void cancelCall(int id) => _ctl?.send(RTCDataChannelMessage(jsonEncode({'id': 0, 'm': 'rpc.cancel', 'p': {'id': id}})));
  int get lastRpcId => _rpcId;

  // ---------------- input ----------------

  void sendMove(double x, double y) {
    final dc = _mv;
    if (dc?.state == RTCDataChannelState.RTCDataChannelOpen) dc!.send(RTCDataChannelMessage(jsonEncode({'x': x, 'y': y, 'q': ++_moveSeq})));
  }

  void sendInput(Map<String, dynamic> m) {
    final dc = _in;
    if (dc?.state == RTCDataChannelState.RTCDataChannelOpen) dc!.send(RTCDataChannelMessage(jsonEncode(m)));
  }

  Future<void> _measureRtt() async {
    try {
      final sw = Stopwatch()..start();
      await call('ping', {'t': DateTime.now().millisecondsSinceEpoch}, const Duration(seconds: 5));
      inputRttMs = sw.elapsedMicroseconds / 1000;
      await call('diag.rtt', {'ms': inputRttMs});
      notifyListeners();
    } catch (_) {}
  }

  int _lastBytes = 0;
  DateTime _lastStatsAt = DateTime.now();

  Future<void> _collectStats() async {
    final pc = _pc;
    if (pc == null) return;
    if (DateTime.now().second % 5 == 0) _measureRtt();
    try {
      final reports = await pc.getStats();
      String? selectedPair;
      final candidates = <String, Map<dynamic, dynamic>>{};
      for (final r in reports) {
        final v = r.values;
        switch (r.type) {
          case 'inbound-rtp':
            if (v['kind'] != 'video' && v['mediaType'] != 'video') break;
            stats.fps = (v['framesPerSecond'] as num?)?.toDouble() ?? stats.fps;
            stats.packetsLost = (v['packetsLost'] as num?)?.toInt() ?? 0;
            stats.jitterMs = ((v['jitter'] as num?)?.toDouble() ?? 0) * 1000;
            stats.framesDecoded = (v['framesDecoded'] as num?)?.toInt() ?? 0;
            stats.width = (v['frameWidth'] as num?)?.toInt() ?? stats.width;
            stats.height = (v['frameHeight'] as num?)?.toInt() ?? stats.height;
            final decodeTotal = (v['totalDecodeTime'] as num?)?.toDouble();
            if (decodeTotal != null && stats.framesDecoded > 0) stats.decodeMs = decodeTotal * 1000 / stats.framesDecoded;
            final jb = (v['jitterBufferDelay'] as num?)?.toDouble();
            final jbCount = (v['jitterBufferEmittedCount'] as num?)?.toDouble();
            if (jb != null && jbCount != null && jbCount > 0) stats.jitterBufferMs = jb * 1000 / jbCount;
            final bytes = (v['bytesReceived'] as num?)?.toInt() ?? 0;
            final now = DateTime.now();
            final dt = now.difference(_lastStatsAt).inMilliseconds;
            if (dt > 0 && _lastBytes > 0) stats.kbps = (bytes - _lastBytes) * 8 / dt;
            _lastBytes = bytes;
            _lastStatsAt = now;
          case 'transport':
            selectedPair = v['selectedCandidatePairId'] as String?;
          case 'candidate-pair':
            if (v['state'] == 'succeeded' && (v['nominated'] == true || selectedPair == null)) {
              selectedPair ??= r.id;
              stats.rttMs = ((v['currentRoundTripTime'] as num?)?.toDouble() ?? 0) * 1000;
              candidates['pair'] = v;
            }
          case 'local-candidate':
          case 'remote-candidate':
            candidates[r.id] = v;
          case 'codec':
            if ((v['mimeType'] as String?)?.startsWith('video') == true) stats.codec = (v['mimeType'] as String).split('/').last;
        }
      }
      final pair = candidates['pair'];
      if (pair != null) {
        final local = candidates[pair['localCandidateId']];
        final remote = candidates[pair['remoteCandidateId']];
        final types = {local?['candidateType'], remote?['candidateType']};
        stats.path = types.contains('relay') ? 'relay (TURN)' : types.contains('srflx') || types.contains('prflx') ? 'direct (STUN)' : 'direct (LAN)';
      }
      notifyListeners();
    } catch (_) {}
  }

  // ---------------- files ----------------

  /// Uploads a file in 60 KiB chunks with SHA-256 verification and resume support.
  Future<String> upload(File file, {String? folder, void Function(int done, int total)? progress}) async {
    final size = await file.length();
    final digest = await hash.sha256.bind(file.openRead()).first;
    final name = file.uri.pathSegments.last;
    final begin = await call('file.begin', {'name': name, 'size': size, 'sha256': digest.toString(), if (folder != null) 'folder': folder});
    final id = begin['id'] as String;
    var offset = (begin['offset'] as num).toInt();
    final idBytes = _hex(id);
    final raf = await file.open();
    try {
      await raf.setPosition(offset);
      while (offset < size) {
        final dc = _file;
        if (dc == null || dc.state != RTCDataChannelState.RTCDataChannelOpen) throw RpcError('disconnected', 'Connection lost; upload can be resumed');
        while ((await dc.getBufferedAmount()) > 256 * 1024) {
          await Future<void>.delayed(const Duration(milliseconds: 5));
        }
        final chunk = await raf.read(60 * 1024);
        final frame = Uint8List(24 + chunk.length)
          ..setRange(0, 16, idBytes)
          ..buffer.asByteData().setInt64(16, offset)
          ..setRange(24, 24 + chunk.length, chunk);
        dc.send(RTCDataChannelMessage.fromBinary(frame));
        offset += chunk.length;
        progress?.call(offset, size);
      }
    } finally {
      await raf.close();
    }
    for (var i = 0; i < 400; i++) {
      final st = await call('file.status', {'id': id});
      if ((st['offset'] as num).toInt() >= size) break;
      await Future<void>.delayed(const Duration(milliseconds: 50));
    }
    final end = await call('file.end', {'id': id}, const Duration(minutes: 5));
    return end['path'] as String;
  }

  /// Downloads a file from the PC into the app's download folder and verifies SHA-256.
  Future<File> download(String path, {void Function(int done, int total)? progress}) async {
    final info = await call('file.download', {'path': path});
    final id = info['id'] as String;
    final size = (info['size'] as num).toInt();
    final dir = Directory('${(await getApplicationDocumentsDirectory()).path}/RemotePC');
    if (!dir.existsSync()) dir.createSync(recursive: true);
    final safe = (info['name'] as String).replaceAll(RegExp(r'[\\/:*?"<>|]'), '_');
    var out = File('${dir.path}/$safe');
    for (var i = 2; out.existsSync(); i++) {
      final dot = safe.lastIndexOf('.');
      out = File('${dir.path}/${dot > 0 ? '${safe.substring(0, dot)} ($i)${safe.substring(dot)}' : '$safe ($i)'}');
    }
    final part = File('${out.path}.part');
    final sink = part.openWrite();
    final idBytes = _hex(id);
    var received = 0;
    final done = Completer<void>();
    final sub = _fileFrames.stream.listen((f) {
      if (f.length < 24 || !listEquals(f.sublist(0, 16), idBytes)) return;
      sink.add(f.sublist(24));
      received += f.length - 24;
      progress?.call(received, size);
      if (received >= size && !done.isCompleted) done.complete();
    });
    final failSub = events.where((e) => e['ev'] == 'file.failed' && e['p']?['id'] == id).listen((e) {
      if (!done.isCompleted) done.completeError(RpcError('failed', e['p']['error'] ?? 'download failed'));
    });
    try {
      if (size > 0) await done.future.timeout(const Duration(hours: 2));
    } finally {
      await sub.cancel();
      await failSub.cancel();
      await sink.close();
    }
    final actual = (await hash.sha256.bind(part.openRead()).first).toString();
    if (actual != info['sha256']) {
      await part.delete();
      throw RpcError('checksum_mismatch', 'Checksum mismatch; the download was discarded');
    }
    return part.rename(out.path);
  }

  static Uint8List _hex(String h) => Uint8List.fromList(List.generate(h.length ~/ 2, (i) => int.parse(h.substring(i * 2, i * 2 + 2), radix: 16)));

  @override
  void dispose() {
    _statsTimer?.cancel();
    _retryTimer?.cancel();
    _events.close();
    _fileFrames.close();
    super.dispose();
  }
}
