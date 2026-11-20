// RemotePC v1 protocol cryptography (packages/protocol/PROTOCOL.md), pure Dart via package:cryptography.
// Verified against packages/protocol/vectors/v1.json in test/protocol_test.dart.
import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:cryptography/cryptography.dart';
import 'package:crypto/crypto.dart' as hash;

String b64u(List<int> data) => base64Url.encode(data).replaceAll('=', '');

Uint8List fromB64u(String s) {
  if (!RegExp(r'^[A-Za-z0-9_-]*$').hasMatch(s)) throw const FormatException('invalid base64url');
  final pad = (4 - s.length % 4) % 4;
  if (pad == 3) throw const FormatException('invalid base64url length');
  return base64Url.decode(s + '=' * pad);
}

const _b32 = 'abcdefghijklmnopqrstuvwxyz234567';

String base32(List<int> data) {
  final sb = StringBuffer();
  var bits = 0, value = 0;
  for (final b in data) {
    value = (value << 8) | b;
    bits += 8;
    while (bits >= 5) {
      sb.write(_b32[(value >> (bits - 5)) & 31]);
      bits -= 5;
    }
    value &= (1 << bits) - 1;
  }
  if (bits > 0) sb.write(_b32[(value << (5 - bits)) & 31]);
  return sb.toString();
}

Uint8List utf8Bytes(String s) => Uint8List.fromList(utf8.encode(s));
Uint8List sha256(List<int> d) => Uint8List.fromList(hash.sha256.convert(d).bytes);

String deviceIdOf(List<int> edPk) => base32(sha256(edPk)).substring(0, 26);

final _rng = Random.secure();
Uint8List randomBytes(int n) => Uint8List.fromList(List.generate(n, (_) => _rng.nextInt(256)));

final _ed = Ed25519();
final _x = X25519();
final _gcm = AesGcm.with256bits();
final _hmac = Hmac.sha256();

/// Device identity: Ed25519 (signaling) + X25519 (envelope keys).
class DeviceKeys {
  final SimpleKeyPair ed;
  final SimpleKeyPair x;
  final Uint8List edPk;
  final Uint8List xPk;
  final String deviceId;

  DeviceKeys._(this.ed, this.x, this.edPk, this.xPk) : deviceId = deviceIdOf(edPk);

  static Future<DeviceKeys> fromSeeds(List<int> edSeed, List<int> xSk) async {
    final ed = await _ed.newKeyPairFromSeed(edSeed);
    final x = await _x.newKeyPairFromSeed(xSk);
    final edPk = Uint8List.fromList((await ed.extractPublicKey()).bytes);
    final xPk = Uint8List.fromList((await x.extractPublicKey()).bytes);
    return DeviceKeys._(ed, x, edPk, xPk);
  }

  static Future<DeviceKeys> generate() => fromSeeds(randomBytes(32), randomBytes(32));

  /// 64 bytes: ed seed || x secret. Persist only in secure storage.
  Future<Uint8List> exportSecret() async {
    final a = await ed.extractPrivateKeyBytes();
    final b = await x.extractPrivateKeyBytes();
    return Uint8List.fromList([...a, ...b]);
  }

  static Future<DeviceKeys> importSecret(List<int> s) {
    if (s.length != 64) throw ArgumentError('secret must be 64 bytes');
    return fromSeeds(s.sublist(0, 32), s.sublist(32));
  }

  Future<Uint8List> sign(List<int> msg) async =>
      Uint8List.fromList((await _ed.sign(msg, keyPair: ed)).bytes);
}

Future<bool> edVerify(List<int> pk, List<int> msg, List<int> sig) async {
  if (pk.length != 32 || sig.length != 64) return false;
  return _ed.verify(msg, signature: Signature(sig, publicKey: SimplePublicKey(pk, type: KeyPairType.ed25519)));
}

Future<Uint8List> hkdf(List<int> ikm, List<int> salt, String info, [int len = 32]) async {
  // RFC 5869. Empty salt means HashLen zeros (identical result to an all-zero salt).
  final prk = await _hmac.calculateMac(ikm, secretKey: SecretKey(salt.isEmpty ? List.filled(32, 0) : salt));
  final out = <int>[];
  var t = <int>[];
  for (var i = 1; out.length < len; i++) {
    final m = await _hmac.calculateMac([...t, ...utf8.encode(info), i], secretKey: SecretKey(prk.bytes));
    t = m.bytes;
    out.addAll(t);
  }
  return Uint8List.fromList(out.sublist(0, len));
}

Future<Uint8List> envelopeKey(DeviceKeys me, List<int> peerXPk, String peerId) async {
  final lo = me.deviceId.compareTo(peerId) < 0 ? me.deviceId : peerId;
  final hi = lo == me.deviceId ? peerId : me.deviceId;
  final shared = await _x.sharedSecretKey(keyPair: me.x, remotePublicKey: SimplePublicKey(peerXPk, type: KeyPairType.x25519));
  final s = await shared.extractBytes();
  if (s.every((b) => b == 0)) throw ArgumentError('invalid peer public key');
  return hkdf(s, sha256(utf8.encode('$lo|$hi')), 'remotepc/v1/envelope');
}

class Envelope {
  final String typ, from, to, sid, n;
  final int seq, ts;
  final Map<String, String>? kx;
  final String ct;
  Envelope({required this.typ, required this.from, required this.to, this.sid = '', required this.seq, required this.ts, required this.n, this.kx, this.ct = ''});

  List<int> aad() => utf8.encode('remotepc-env-v1|1|$typ|$from|$to|$sid|$seq|$ts|$n|${kx?['edPk'] ?? ''}|${kx?['xPk'] ?? ''}');

  Map<String, dynamic> toJson() => {
        'v': 1, 'typ': typ, 'from': from, 'to': to, 'sid': sid, 'seq': seq, 'ts': ts, 'n': n,
        if (kx != null) 'kx': kx, 'ct': ct,
      };

  static Envelope fromJson(Map<String, dynamic> j) {
    if (j['v'] != 1) throw const FormatException('unsupported envelope version');
    final kx = j['kx'] as Map<String, dynamic>?;
    return Envelope(
      typ: j['typ'] as String, from: j['from'] as String, to: j['to'] as String, sid: (j['sid'] ?? '') as String,
      seq: (j['seq'] as num).toInt(), ts: (j['ts'] as num).toInt(), n: j['n'] as String,
      kx: kx?.map((k, v) => MapEntry(k, v as String)), ct: j['ct'] as String,
    );
  }

  Envelope withCt(String c) => Envelope(typ: typ, from: from, to: to, sid: sid, seq: seq, ts: ts, n: n, kx: kx, ct: c);
}

Future<Envelope> sealEnvelope(List<int> key, Envelope header, Object payload, {List<int>? nonce}) =>
    sealRaw(key, header, utf8.encode(jsonEncode(payload)), nonce: nonce);

Future<Envelope> sealRaw(List<int> key, Envelope header, List<int> plaintext, {List<int>? nonce}) async {
  final n = nonce ?? randomBytes(12);
  final h = Envelope(typ: header.typ, from: header.from, to: header.to, sid: header.sid, seq: header.seq, ts: header.ts, n: b64u(n), kx: header.kx);
  final box = await _gcm.encrypt(plaintext, secretKey: SecretKey(key), nonce: n, aad: h.aad());
  return h.withCt(b64u([...box.cipherText, ...box.mac.bytes]));
}

Future<dynamic> openEnvelope(List<int> key, Envelope e) async {
  final ct = fromB64u(e.ct);
  if (ct.length < 16) throw const FormatException('ciphertext too short');
  final box = SecretBox(ct.sublist(0, ct.length - 16), nonce: fromB64u(e.n), mac: Mac(ct.sublist(ct.length - 16)));
  final pt = await _gcm.decrypt(box, secretKey: SecretKey(key), aad: e.aad());
  return jsonDecode(utf8.decode(pt));
}

/// Receiver checks 3–5 of PROTOCOL.md §3.
class ReplayGuard {
  final _nonces = <String, int>{};
  final _seqs = <String, int>{};
  final int windowMs;
  ReplayGuard({this.windowMs = 120000});

  String? check(Envelope e, int nowMs) {
    if ((e.ts - nowMs).abs() > windowMs) return 'stale';
    if (_nonces.containsKey('${e.from}|${e.n}')) return 'replay';
    if ((_seqs['${e.from}|${e.sid}'] ?? 0) >= e.seq) return 'seq';
    return null;
  }

  void accept(Envelope e, int nowMs) {
    _nonces['${e.from}|${e.n}'] = nowMs;
    _seqs['${e.from}|${e.sid}'] = e.seq;
    if (_nonces.length > 4096) _nonces.removeWhere((_, t) => nowMs - t > 600000);
  }
}

List<int> pairTranscript({required String hostId, required String clientId, required String clientEdPk, required String clientXPk,
        required String hostEdPk, required String hostXPk, required String pairNonce}) =>
    utf8.encode('remotepc-pair-v1|$hostId|$clientId|$clientEdPk|$clientXPk|$hostEdPk|$hostXPk|$pairNonce');

Future<Uint8List> pairProof(List<int> secret, List<int> transcript) async {
  final key = await hkdf(secret, const [], 'remotepc/v1/pair-proof');
  final mac = await _hmac.calculateMac(transcript, secretKey: SecretKey(key));
  return Uint8List.fromList(mac.bytes);
}

String verifyCode(List<int> transcript) {
  final h = sha256(transcript);
  final n = ((h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3]) & 0xFFFFFFFF;
  return (n % 1000000).toString().padLeft(6, '0');
}

const pairCodeAlphabet = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';

String? normalizePairCode(String input) {
  final s = input.toUpperCase().replaceAll(RegExp(r'[\s-]'), '');
  if (s.length != 10) return null;
  for (final c in s.split('')) {
    if (!pairCodeAlphabet.contains(c)) return null;
  }
  return s;
}

/// Sender sequence: strictly increasing, never reused after an app restart.
class SeqCounter {
  int _last = 0;
  int next() {
    final now = DateTime.now().millisecondsSinceEpoch;
    _last = now > _last ? now : _last + 1;
    return _last;
  }
}
