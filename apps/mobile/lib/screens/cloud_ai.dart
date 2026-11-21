import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../core/host_link.dart';
import '../l10n.dart';
import '../ui/adaptive.dart';
import '../ui/platform_ui.dart';

/// Adds a cloud AI account (Claude, OpenAI, Gemini, OpenRouter, Groq, DeepSeek or any OpenAI-compatible HTTPS URL)
/// to the PC. The key travels once over the end-to-end encrypted session, is DPAPI-encrypted on the PC and is
/// never stored on the phone or sent back. Returns the new `ai.status` map.
class CloudAiScreen extends StatefulWidget {
  final HostLink link;
  const CloudAiScreen({super.key, required this.link});
  @override
  State<CloudAiScreen> createState() => _CloudAiScreenState();
}

class _CloudAiScreenState extends State<CloudAiScreen> {
  final _key = TextEditingController();
  final _url = TextEditingController();
  List<Map<String, dynamic>> _presets = [];
  List<Map<String, dynamic>> _accounts = [];
  String _preset = 'anthropic';
  bool _busy = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _key.clear();
    _key.dispose();
    _url.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final r = await widget.link.call('ai.cloud.list');
      if (!mounted) return;
      setState(() {
        _presets = List<Map<String, dynamic>>.from(r['presets'] ?? const []);
        _accounts = List<Map<String, dynamic>>.from(r['accounts'] ?? const []);
      });
    } on RpcError catch (e) {
      if (mounted) setState(() => _error = e.message);
    }
  }

  Map<String, dynamic>? get _current => _presets.where((p) => p['id'] == _preset).firstOrNull;

  Future<void> _add() async {
    final key = _key.text.trim();
    if (key.isEmpty) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final r = await widget.link.call('ai.cloud.add', {
        'preset': _preset,
        'apiKey': key,
        if (_preset == 'custom') 'url': _url.text.trim(),
      }, const Duration(seconds: 40));
      _key.clear();
      final added = r['added'] as Map?;
      if (!mounted) return;
      if (added?['ok'] == true) {
        showToast(context, tr('cloud_added'));
        Navigator.of(context).pop(r);
      } else {
        setState(() => _error = '${tr('cloud_failed')}: ${added?['error'] ?? ''}');
        await _load();
      }
    } on RpcError catch (e) {
      if (mounted) setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _remove(Map<String, dynamic> a) async {
    final ok = await showAlert(context, title: tr('remove_account'), message: a['name'] as String?, actions: [
      DialogAction('cancel', tr('cancel'), cancel: true),
      DialogAction('remove', tr('remove_account'), destructive: true),
    ]);
    if (ok != 'remove') return;
    try {
      await widget.link.call('ai.cloud.remove', {'id': a['id']});
      await _load();
    } on RpcError catch (e) {
      if (mounted) showToast(context, e.message);
    }
  }

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final options = {for (final x in _presets) x['id'] as String: x['name'] as String, 'custom': 'OpenAI-compatible…'};
    return AppPage(
      title: tr('add_cloud_ai'),
      slivers: [
        SliverList.list(children: [
          if (_accounts.isNotEmpty)
            Section(header: tr('cloud_ai'), children: [
              for (final a in _accounts)
                Tile(
                  title: a['name'] as String,
                  subtitle: '${a['keyHint']} · ${a['baseUrl']}',
                  icon: Ic.cloud,
                  iconColor: const Color(0xFF5E5CE6),
                  chevron: false,
                  trailing: LinkButton(label: tr('delete'), onPressed: () => _remove(a)),
                ),
            ]),
          Section(header: tr('cloud_service'), footer: tr('cloud_note'), children: [
            for (final e in options.entries)
              Tile(
                title: e.value,
                chevron: false,
                trailing: _preset == e.key ? Icon(Ic.check.data, color: p.accent) : null,
                onTap: () => setState(() => _preset = e.key),
              ),
          ]),
          Section(header: tr('api_key'), children: [
            if (_preset == 'custom')
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 10, 16, 4),
                child: AdaptiveTextField(controller: _url, placeholder: tr('custom_url'), keyboard: TextInputType.url),
              ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 10, 16, 10),
              child: AdaptiveTextField(controller: _key, placeholder: tr('api_key_hint'), obscure: true, prefix: Ic.key, onSubmitted: (_) => _add()),
            ),
            if ((_current?['keyUrl'] as String?)?.isNotEmpty == true)
              Tile(
                title: tr('get_key'),
                subtitle: _current!['keyUrl'] as String,
                icon: Ic.link,
                chevron: false,
                onTap: () async {
                  await Clipboard.setData(ClipboardData(text: _current!['keyUrl'] as String));
                  if (context.mounted) showToast(context, tr('copied'));
                },
              ),
          ]),
          if (_error != null)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 4),
              child: Text(_error!, style: TextStyle(color: p.danger)),
            ),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
            child: _busy ? const Center(child: Spinner()) : PrimaryButton(id: 'cloud-add', label: tr('add'), onPressed: _add),
          ),
          if (PlatformUi.isIOS) const SizedBox(height: 40),
        ]),
      ],
    );
  }
}
