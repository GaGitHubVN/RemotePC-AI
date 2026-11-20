import 'package:flutter_test/flutter_test.dart';
import 'package:remotepc_ai/core/host_link.dart';

/// Regression for the iOS crash on "Connect": JSON null in iceServers became NSNull inside flutter_webrtc.
void main() {
  test('null username/credential are dropped, urls normalised to a list', () {
    final r = sanitizeIceServers([
      {'urls': ['stun:stun.l.google.com:19302'], 'username': null, 'credential': null},
    ]);
    expect(r, [
      {'urls': ['stun:stun.l.google.com:19302']},
    ]);
    expect(r.first.containsKey('username'), isFalse);
  });

  test('TURN credentials kept; string urls accepted; junk removed', () {
    final r = sanitizeIceServers([
      {'urls': 'turn:t.example:3478?transport=udp', 'username': 'u', 'credential': 'c'},
      {'urls': [null, 42, 'http://evil', ' turns:t.example:443 ']},
      {'urls': null},
      'not a map',
    ]);
    expect(r, [
      {'urls': ['turn:t.example:3478?transport=udp'], 'username': 'u', 'credential': 'c'},
      {'urls': ['turns:t.example:443']},
    ]);
  });
}
