import 'package:flutter/material.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../app_state.dart';
import '../core/host_link.dart';
import '../core/protocol/crypto.dart';
import '../core/signaling.dart';
import '../l10n.dart';
import '../ui/theme.dart';

class PairScreen extends StatefulWidget {
  const PairScreen({super.key});
  @override
  State<PairScreen> createState() => _PairScreenState();
}

class _PairScreenState extends State<PairScreen> {
  String? _verify;
  String? _error;
  bool _busy = false;
  bool _done = false;

  Future<void> _run(PairingInvite inv) async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _error = null;
      _verify = null;
    });
    try {
      final s = AppState.instance;
      final host = await HostLink.pair(s.keys, inv, s.deviceName, s.platformName, (code) => setState(() => _verify = code));
      await s.addHost(host);
      setState(() => _done = true);
      await Future<void>.delayed(const Duration(milliseconds: 900));
      if (mounted) Navigator.pop(context);
    } catch (e) {
      setState(() => _error = e.toString().replaceFirst('Bad state: ', '').replaceFirst('FormatException: ', ''));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    return DefaultTabController(
      length: 2,
      child: Scaffold(
        appBar: AppBar(title: Text(tr('add_pc')), bottom: TabBar(tabs: [Tab(text: tr('scan_qr')), Tab(text: tr('enter_code'))])),
        body: Stack(children: [
          TabBarView(children: [
            _busy || _verify != null
                ? const SizedBox()
                : MobileScanner(onDetect: (c) {
                    final raw = c.barcodes.isEmpty ? null : c.barcodes.first.rawValue;
                    if (raw == null || _busy) return;
                    try {
                      _run(PairingInvite.parseQr(raw));
                    } on FormatException catch (e) {
                      setState(() => _error = e.message);
                    }
                  }),
            _CodeTab(onInvite: _run),
          ]),
          if (_busy || _verify != null || _error != null || _done)
            Align(
              alignment: Alignment.bottomCenter,
              child: AnimatedSwitcher(
                duration: Motion.medium,
                child: Card(
                  key: ValueKey('$_verify$_error$_done'),
                  margin: const EdgeInsets.all(16),
                  child: Padding(
                    padding: const EdgeInsets.all(20),
                    child: Column(mainAxisSize: MainAxisSize.min, children: [
                      if (_done) ...[
                        const Icon(Icons.check_circle, color: Colors.green, size: 56),
                        Text(tr('paired'), style: t.textTheme.titleMedium),
                      ] else if (_error != null) ...[
                        Icon(Icons.error_outline, color: t.colorScheme.error, size: 40),
                        const SizedBox(height: 8),
                        Text(_error!, textAlign: TextAlign.center),
                      ] else ...[
                        const LinearProgressIndicator(),
                        const SizedBox(height: 16),
                        Text(tr('pairing'), style: t.textTheme.titleMedium),
                        if (_verify != null) ...[
                          const SizedBox(height: 12),
                          Text('${_verify!.substring(0, 3)} ${_verify!.substring(3)}',
                              style: t.textTheme.displaySmall?.copyWith(fontWeight: FontWeight.w700, letterSpacing: 4, color: t.colorScheme.primary)),
                          const SizedBox(height: 8),
                          Text(tr('verify_code'), textAlign: TextAlign.center),
                        ],
                      ],
                    ]),
                  ),
                ),
              ),
            ),
        ]),
      ),
    );
  }
}

class _CodeTab extends StatefulWidget {
  final Future<void> Function(PairingInvite) onInvite;
  const _CodeTab({required this.onInvite});
  @override
  State<_CodeTab> createState() => _CodeTabState();
}

class _CodeTabState extends State<_CodeTab> {
  List<Map<String, dynamic>> _found = [];
  Map<String, dynamic>? _selected;
  bool _scanning = false;
  final _code = TextEditingController();
  final _ip = TextEditingController();

  Future<void> _scan() async {
    setState(() => _scanning = true);
    final r = await LanDiscovery.discover();
    setState(() {
      _found = r;
      _scanning = false;
      if (r.length == 1) _selected = r.first;
    });
  }

  @override
  void initState() {
    super.initState();
    _scan();
  }

  @override
  Widget build(BuildContext context) => ListView(padding: const EdgeInsets.all(16), children: [
        Row(children: [
          Expanded(child: Text(tr('find_lan'), style: Theme.of(context).textTheme.titleSmall)),
          if (_scanning) const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2)) else IconButton(onPressed: _scan, icon: const Icon(Icons.refresh)),
        ]),
        for (final h in _found)
          RadioListTile<String>(
            value: h['id'], groupValue: _selected?['id'], onChanged: (_) => setState(() => _selected = h),
            title: Text(h['name'] ?? 'PC'), subtitle: Text('${h['address']}'),
          ),
        if (_found.isEmpty && !_scanning)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 8),
            child: Text('No PC answered. On iPhone, LAN discovery may be unavailable — scan the QR code instead.', style: Theme.of(context).textTheme.bodySmall),
          ),
        const SizedBox(height: 16),
        TextField(
          controller: _code,
          textCapitalization: TextCapitalization.characters,
          decoration: const InputDecoration(labelText: 'XXXXX-XXXXX', prefixIcon: Icon(Icons.password)),
        ),
        const SizedBox(height: 16),
        FilledButton(
          onPressed: () {
            final code = normalizePairCode(_code.text);
            if (code == null || _selected == null) {
              ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Choose a PC and enter the 10-character code.')));
              return;
            }
            widget.onInvite(PairingInvite.fromCode(_selected!, code));
          },
          child: Text(tr('connect')),
        ),
        const SizedBox(height: 8),
        Text('Code pairing works on the same Wi-Fi. The PC also shows a 6-digit check code; it must match the phone.', style: Theme.of(context).textTheme.bodySmall),
        // Manual address is kept for networks where discovery is blocked.
        ExpansionTile(title: const Text('Enter PC address manually'), children: [
          TextField(controller: _ip, decoration: const InputDecoration(labelText: '192.168.1.20')),
          TextButton(
            onPressed: () => ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
                content: Text('Manual pairing needs the PC\'s public keys, which only the QR code or LAN discovery provide. Use the QR code.'))),
            child: const Text('Why can\'t I pair with just an IP?'),
          ),
        ]),
      ]);
}
