import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:remotepc_ai/core/protocol/crypto.dart';

void main() {
  final v = jsonDecode(File('test/fixtures/v1.json').readAsStringSync()) as Map<String, dynamic>;
  String s(String path) {
    dynamic n = v;
    for (final p in path.split('.')) {
      n = (n as Map<String, dynamic>)[p];
    }
    return n as String;
  }

  test('base32 RFC 4648', () {
    const cases = {'': '', 'f': 'my', 'fo': 'mzxq', 'foo': 'mzxw6', 'foob': 'mzxw6yq', 'fooba': 'mzxw6ytb', 'foobar': 'mzxw6ytboi'};
    cases.forEach((i, o) => expect(base32(utf8.encode(i)), o));
  });

  test('keys and device ids match vectors', () async {
    final h = await DeviceKeys.fromSeeds(fromB64u(s('keys.host.edSeed')), fromB64u(s('keys.host.xSk')));
    expect(b64u(h.edPk), s('keys.host.edPk'));
    expect(b64u(h.xPk), s('keys.host.xPk'));
    expect(h.deviceId, s('keys.host.id'));
    final secret = await h.exportSecret();
    expect((await DeviceKeys.importSecret(secret)).deviceId, h.deviceId);
  });

  test('hello signature is byte-identical (Ed25519 deterministic)', () async {
    final h = await DeviceKeys.fromSeeds(fromB64u(s('keys.host.edSeed')), fromB64u(s('keys.host.xSk')));
    final sig = await h.sign(utf8.encode(s('hello.message')));
    expect(b64u(sig), s('hello.sig'));
    expect(await edVerify(h.edPk, utf8.encode(s('hello.message')), sig), isTrue);
    expect(await edVerify(h.edPk, utf8.encode('tampered'), sig), isFalse);
  });

  test('envelope key, open TS-sealed vectors, byte-exact seal', () async {
    final c = await DeviceKeys.fromSeeds(fromB64u(s('keys.client.edSeed')), fromB64u(s('keys.client.xSk')));
    final key = await envelopeKey(c, fromB64u(s('keys.host.xPk')), s('keys.host.id'));
    expect(b64u(key), s('envelope.key'));
    final sealed = Envelope.fromJson((v['envelope'] as Map)['sealed'] as Map<String, dynamic>);
    expect((await openEnvelope(key, sealed))['text'], 'Xin chào 👋');
    final pair = Envelope.fromJson((v['envelope'] as Map)['pairRequest'] as Map<String, dynamic>);
    expect((await openEnvelope(key, pair))['pairNonce'], 'nonce-1');
    final again = await sealRaw(key, sealed, utf8.encode('{"reqId":"r1","text":"Xin chào 👋"}'), nonce: fromB64u(sealed.n));
    expect(again.ct, sealed.ct);
    final tampered = Envelope(typ: sealed.typ, from: sealed.from, to: sealed.to, sid: sealed.sid, seq: sealed.seq + 1, ts: sealed.ts, n: sealed.n, ct: sealed.ct);
    expect(() => openEnvelope(key, tampered), throwsA(anything));
  });

  test('pairing transcript, proofs and verify code', () async {
    final p = v['pairing'] as Map<String, dynamic>;
    final t = pairTranscript(hostId: p['hostId'], clientId: p['clientId'], clientEdPk: p['clientEdPk'], clientXPk: p['clientXPk'],
        hostEdPk: p['hostEdPk'], hostXPk: p['hostXPk'], pairNonce: p['pairNonce']);
    expect(utf8.decode(t), p['transcript']);
    expect(b64u(await pairProof(fromB64u(p['qrSecret']), t)), p['qrProof']);
    expect(b64u(await pairProof(utf8.encode(p['code']), t)), p['codeProof']);
    expect(verifyCode(t), p['verifyCode']);
    expect(normalizePairCode('k7mxq-2rtp9'), 'K7MXQ2RTP9');
    expect(normalizePairCode('K7MXQ2RTP1'), isNull);
  });

  test('replay guard', () {
    final g = ReplayGuard();
    final e = Envelope(typ: 'x', from: 'a', to: 'b', sid: 's', seq: 5, ts: 1000, n: 'n1');
    expect(g.check(e, 1000), isNull);
    g.accept(e, 1000);
    expect(g.check(e, 1000), 'replay');
    expect(g.check(Envelope(typ: 'x', from: 'a', to: 'b', sid: 's', seq: 5, ts: 1000, n: 'n2'), 1000), 'seq');
    expect(g.check(Envelope(typ: 'x', from: 'a', to: 'b', sid: 's', seq: 6, ts: 1000, n: 'n3'), 200000), 'stale');
  });
}
