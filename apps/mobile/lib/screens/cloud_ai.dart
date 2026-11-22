import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:url_launcher/url_launcher.dart';

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
  final _code = TextEditingController();
  String? _flowId;
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
    _code.dispose();
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

  Future<void> _open(String url) async {
    var ok = false;
    try {
      ok = await launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
    } catch (_) {}
    if (!ok) {
      await Clipboard.setData(ClipboardData(text: url));
      if (mounted) showToast(context, tr('open_failed'));
    }
  }

  /// Account sign-in: the PC creates the PKCE verifier and the login URL; the phone only opens it and relays the
  /// one-time code shown by the service. The account key is created and kept on the PC.
  Future<void> _startSignIn() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final r = await widget.link.call('ai.cloud.signin.start', {'preset': _preset});
      if (!mounted) return;
      setState(() => _flowId = r['flowId'] as String?);
      await _open(r['url'] as String);
    } on RpcError catch (e) {
      if (mounted) setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _finishSignIn() async {
    final code = _code.text.trim();
    if (code.isEmpty || _flowId == null) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final r = await widget.link.call('ai.cloud.signin.finish', {'flowId': _flowId, 'code': code}, const Duration(seconds: 60));
      _code.clear();
      _flowId = null;
      await _done(r);
    } on RpcError catch (e) {
      if (mounted) setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _done(Map<String, dynamic> r) async {
    final added = r['added'] as Map?;
    if (!mounted) return;
    if (added?['ok'] == true) {
      showToast(context, tr('cloud_added'));
      Navigator.of(context).pop(r);
    } else {
      setState(() => _error = '${tr('cloud_failed')}: ${added?['error'] ?? ''}');
      await _load();
    }
  }

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
      await _done(r);
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
                  subtitle: '${a['auth'] == 'oauth' ? tr('via_account') : tr('api_key')} ${a['keyHint']} · ${a['baseUrl']}',
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
                onTap: () => setState(() {
                  _preset = e.key;
                  _flowId = null;
                }),
              ),
          ]),
          if (_current?['oauth'] == true)
            Section(header: tr('signin_title'), footer: tr('signin_note'), children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 12),
                child: PrimaryButton(
                  id: 'cloud-signin',
                  icon: Ic.link,
                  label: tr('signin_with').replaceAll('{0}', _current!['name'] as String),
                  secondary: _flowId != null,
                  onPressed: _busy ? null : _startSignIn,
                ),
              ),
              if (_flowId != null) ...[
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 0, 16, 10),
                  child: AdaptiveTextField(controller: _code, placeholder: tr('signin_code'), prefix: Ic.key, onSubmitted: (_) => _finishSignIn()),
                ),
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
                  child: PrimaryButton(id: 'cloud-signin-finish', label: tr('signin_finish'), onPressed: _busy ? null : _finishSignIn),
                ),
              ],
            ]),
          Section(header: _current?['oauth'] == true ? tr('or_api_key') : tr('api_key'), children: [
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
                onTap: () => _open(_current!['keyUrl'] as String),
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
