import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../app_state.dart';
import '../core/host_link.dart';
import '../core/protocol/crypto.dart';
import '../core/signaling.dart';
import '../l10n.dart';
import '../ui/adaptive.dart';
import '../ui/glass.dart';
import '../ui/platform_ui.dart';
import '../ui/theme.dart';

/// Leading "close" for screens presented full-screen.
Widget closeLeading(BuildContext context) => switch (PlatformUi.style) {
      UiStyle.glass => GlassBar(actions: [GlassAction('close', 'xmark', tr('cancel'))], onAction: (_) => Navigator.of(context).maybePop()),
      UiStyle.cupertino => CupertinoButton(padding: EdgeInsets.zero, onPressed: () => Navigator.of(context).maybePop(), child: Text(tr('cancel'))),
      UiStyle.material => IconButton(icon: const Icon(Icons.close), onPressed: () => Navigator.of(context).maybePop()),
    };

class PairScreen extends StatefulWidget {
  const PairScreen({super.key});
  @override
  State<PairScreen> createState() => _PairScreenState();
}

class _PairScreenState extends State<PairScreen> {
  String _mode = 'qr';
  String? _verify, _error;
  bool _busy = false, _done = false;
  List<Map<String, dynamic>> _found = [];
  Map<String, dynamic>? _selected;
  bool _scanning = false;
  final _code = TextEditingController();

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
      if (mounted) Navigator.of(context).pop();
    } catch (e) {
      setState(() => _error = e.toString().replaceFirst('Bad state: ', '').replaceFirst('FormatException: ', ''));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _scan() async {
    setState(() => _scanning = true);
    final r = await LanDiscovery.discover();
    if (!mounted) return;
    setState(() {
      _found = r;
      _scanning = false;
      if (r.length == 1) _selected = r.first;
    });
  }

  void _submitCode() {
    final code = normalizePairCode(_code.text);
    if (code == null || _selected == null) {
      showToast(context, tr('choose_pc_code'));
      return;
    }
    _run(PairingInvite.fromCode(_selected!, code));
  }

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final showStatus = _busy || _verify != null || _error != null || _done;
    return AppPage(
      title: tr('add_pc'),
      leading: closeLeading(context),
      slivers: [
        SliverToBoxAdapter(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
            child: Center(
              child: Segmented<String>(
                options: {'qr': tr('scan_qr'), 'code': tr('enter_code')},
                value: _mode,
                onChanged: (v) {
                  setState(() => _mode = v);
                  if (v == 'code' && _found.isEmpty && !_scanning) _scan();
                },
              ),
            ),
          ),
        ),
        if (showStatus) SliverToBoxAdapter(child: _status(context, p)),
        if (_mode == 'qr')
          SliverToBoxAdapter(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
              child: Column(children: [
                AspectRatio(
                  aspectRatio: 1,
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(PlatformUi.isGlass ? 32 : 18),
                    child: _busy || _verify != null || _done
                        ? ColoredBox(color: p.card, child: Center(child: Icon(Ic.qr.data, size: 64, color: p.secondary)))
                        : MobileScanner(onDetect: (c) {
                            final raw = c.barcodes.isEmpty ? null : c.barcodes.first.rawValue;
                            if (raw == null || _busy) return;
                            try {
                              _run(PairingInvite.parseQr(raw));
                            } on FormatException catch (e) {
                              setState(() => _error = e.message);
                            }
                          }),
                  ),
                ),
                const SizedBox(height: 12),
                Text(tr('qr_hint'), textAlign: TextAlign.center, style: TextStyle(fontSize: 15, color: p.secondary)),
              ]),
            ),
          )
        else
          SliverList.list(children: [
            Section(
              header: tr('find_lan'),
              footer: _found.isEmpty && !_scanning ? tr('no_pc_found') : null,
              children: [
                for (final h in _found)
                  Tile(
                    title: h['name'] ?? 'PC',
                    subtitle: '${h['address']}',
                    icon: Ic.pc,
                    trailing: _selected?['id'] == h['id'] ? Icon(Ic.check.data, color: p.accent) : null,
                    chevron: false,
                    onTap: () => setState(() => _selected = h),
                  ),
                Tile(
                  title: _scanning ? tr('connecting') : tr('retry_now'),
                  icon: Ic.refresh,
                  iconColor: const Color(0xFF8E8E93),
                  trailing: _scanning ? const Spinner(size: 16) : null,
                  chevron: false,
                  onTap: _scanning ? null : _scan,
                ),
              ],
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 8),
              child: AdaptiveTextField(controller: _code, placeholder: 'XXXXX-XXXXX', prefix: Ic.code, capitalization: TextCapitalization.characters, onSubmitted: (_) => _submitCode()),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 4),
              child: PrimaryButton(id: 'pair-code', label: tr('connect'), onPressed: _busy ? null : _submitCode),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(32, 6, 32, 0),
              child: Text(tr('code_hint'), textAlign: TextAlign.center, style: TextStyle(fontSize: 13, color: p.secondary)),
            ),
          ]),
      ],
    );
  }

  Widget _status(BuildContext context, Palette p) => Padding(
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
        child: AnimatedSwitcher(
          duration: Motion.medium,
          child: Container(
            key: ValueKey('$_verify$_error$_done'),
            width: double.infinity,
            padding: const EdgeInsets.all(20),
            decoration: BoxDecoration(color: p.card, borderRadius: BorderRadius.circular(PlatformUi.isGlass ? 26 : (PlatformUi.isIOS ? 12 : 24))),
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              if (_done) ...[
                Icon(Ic.ok.data, color: p.success, size: 52),
                const SizedBox(height: 8),
                Text(tr('paired'), style: TextStyle(fontSize: 17, fontWeight: FontWeight.w600, color: p.label)),
              ] else if (_error != null) ...[
                Icon(Ic.warning.data, color: p.danger, size: 36),
                const SizedBox(height: 8),
                Text(_error!, textAlign: TextAlign.center, style: TextStyle(color: p.label)),
              ] else ...[
                const Spinner(size: 24),
                const SizedBox(height: 12),
                Text(tr('pairing'), style: TextStyle(fontSize: 17, fontWeight: FontWeight.w600, color: p.label)),
                if (_verify != null) ...[
                  const SizedBox(height: 12),
                  Text('${_verify!.substring(0, 3)} ${_verify!.substring(3)}',
                      style: TextStyle(fontSize: 40, fontWeight: FontWeight.w700, letterSpacing: 4, color: p.accent, fontFeatures: const [FontFeature.tabularFigures()])),
                  const SizedBox(height: 8),
                  Text(tr('verify_code'), textAlign: TextAlign.center, style: TextStyle(fontSize: 14, color: p.secondary)),
                ],
              ],
            ]),
          ),
        ),
      );
}
