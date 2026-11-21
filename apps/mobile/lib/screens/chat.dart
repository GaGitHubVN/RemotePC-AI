import 'dart:convert';
import 'dart:io';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_markdown_plus/flutter_markdown_plus.dart';
import 'package:uuid/uuid.dart';

import '../app_state.dart';
import '../core/host_link.dart';
import '../core/storage.dart';
import '../l10n.dart';
import '../ui/adaptive.dart';
import '../ui/glass.dart';
import '../ui/platform_ui.dart';
import '../ui/theme.dart';
import 'agent.dart';
import 'cloud_ai.dart';
import 'home.dart';

/// Conversations across all PCs, stored locally on the phone.
class ChatListScreen extends StatefulWidget {
  const ChatListScreen({super.key});
  @override
  State<ChatListScreen> createState() => _ChatListScreenState();
}

class _ChatListScreenState extends State<ChatListScreen> {
  List<Map<String, dynamic>> _chats = [];
  String _q = '';

  Future<void> _load() async {
    final c = await ChatStore.list();
    if (mounted) setState(() => _chats = c);
  }

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _newChat() async {
    final hosts = AppState.instance.hosts;
    if (hosts.isEmpty) return showToast(context, tr('no_pcs'));
    final h = hosts.length == 1 ? hosts.first : await _pickHost(context);
    if (h == null || !mounted) return;
    openChat(context, h);
    await Future<void>.delayed(const Duration(milliseconds: 400));
    _load();
  }

  Future<void> _chatActions(Map<String, dynamic> c) async {
    final v = await showActions(context, title: c['title'], actions: [
      DialogAction('rename', tr('rename')),
      DialogAction('delete', tr('delete'), destructive: true),
      DialogAction('cancel', tr('cancel'), cancel: true),
    ]);
    if (!mounted) return;
    if (v == 'rename') {
      final name = (await showPrompt(context, title: tr('rename'), text: c['title'], ok: tr('ok'), cancel: tr('cancel')))?.trim();
      if (name != null && name.isNotEmpty) {
        c['title'] = name;
        await ChatStore.save(c);
      }
    } else if (v == 'delete') {
      await ChatStore.delete(c['id']);
    }
    _load();
  }

  @override
  Widget build(BuildContext context) {
    final hosts = AppState.instance.hosts;
    final filtered = _chats
        .where((c) => _q.isEmpty || (c['title'] as String).toLowerCase().contains(_q) || (c['messages'] as List).any((m) => (m['content'] as String).toLowerCase().contains(_q)))
        .toList();
    return AppPage(
      title: tr('ai'),
      large: true,
      onRefresh: _load,
      actions: [BarItem('compose', Ic.compose, tr('new_chat'), _newChat)],
      slivers: [
        SliverToBoxAdapter(child: Padding(padding: const EdgeInsets.fromLTRB(16, 0, 16, 10), child: SearchField(placeholder: tr('search'), onChanged: (v) => setState(() => _q = v.toLowerCase())))),
        if (filtered.isEmpty)
          SliverFillRemaining(
            hasScrollBody: false,
            child: Center(
              child: EmptyState(
                icon: Ic.chat,
                title: tr('no_chats'),
                subtitle: tr('no_chats_sub'),
                action: hosts.isEmpty ? null : SizedBox(width: 260, child: PrimaryButton(id: 'new-chat', label: tr('new_chat'), icon: Ic.compose, onPressed: _newChat)),
              ),
            ),
          )
        else
          SliverList.list(children: [
            Section(children: [
              for (final c in filtered)
                GestureDetector(
                  onLongPress: () => _chatActions(c),
                  child: Tile(
                    title: c['title'],
                    subtitle: '${c['model'] ?? ''} · ${DateTime.fromMillisecondsSinceEpoch(c['updated'])}'.split('.').first,
                    icon: Ic.ai,
                    iconColor: const Color(0xFFAF52DE),
                    onTap: () async {
                      final h = hosts.where((x) => x.id == c['hostId']).firstOrNull;
                      if (h == null) return;
                      openChat(context, h, c);
                      await Future<void>.delayed(const Duration(milliseconds: 400));
                      _load();
                    },
                  ),
                ),
            ]),
          ]),
      ],
    );
  }
}

Future<PairedHost?> _pickHost(BuildContext context) async {
  final hosts = AppState.instance.hosts;
  final id = await showActions(context, title: tr('choose_pc'), actions: [
    for (final h in hosts) DialogAction(h.id, h.name),
    DialogAction('cancel', tr('cancel'), cancel: true),
  ]);
  return hosts.where((h) => h.id == id).firstOrNull;
}

class ChatScreen extends StatefulWidget {
  final PairedHost host;
  final Map<String, dynamic>? chat;
  const ChatScreen({super.key, required this.host, this.chat});
  @override
  State<ChatScreen> createState() => _ChatScreenState();
}

class _ChatScreenState extends State<ChatScreen> {
  late final HostLink link = AppState.instance.link(widget.host);
  late Map<String, dynamic> _chat;
  final _input = TextEditingController();
  final _scroll = ScrollController();
  bool _streaming = false;
  int? _streamId;
  String _partial = '';
  String _thinking = '';
  Map<String, dynamic>? _status;
  final List<Map<String, dynamic>> _attachments = [];
  String? _uploading;

  List<Map<String, dynamic>> get _messages => List<Map<String, dynamic>>.from(_chat['messages']);

  @override
  void initState() {
    super.initState();
    _chat = widget.chat != null
        ? Map<String, dynamic>.from(widget.chat!)
        : {'id': const Uuid().v4(), 'hostId': widget.host.id, 'title': tr('new_chat'), 'messages': <Map<String, dynamic>>[], 'updated': DateTime.now().millisecondsSinceEpoch};
    link.addListener(_onLink);
    _input.addListener(() => setState(() {}));
    _onLink();
  }

  void _onLink() {
    if (link.isConnected && _status == null) _refreshStatus();
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    link.removeListener(_onLink);
    super.dispose();
  }

  void _toast(String m) {
    if (mounted) showToast(context, m);
  }

  Future<void> _refreshStatus({bool rescan = false}) async {
    try {
      final s = await link.call(rescan ? 'ai.models' : 'ai.status', rescan ? {'rescan': true} : null);
      if (mounted) setState(() => _status = s);
    } on RpcError catch (e) {
      _toast(e.message);
    }
  }

  Map<String, dynamic>? get _modelInfo => _status?['selected']?['info'] as Map<String, dynamic>?;

  Future<void> _pickModel() async {
    await _refreshStatus(rescan: true);
    if (!mounted || _status == null) return;
    final models = List<Map<String, dynamic>>.from(_status!['models'] ?? const []);
    final providers = List<Map<String, dynamic>>.from(_status!['providers'] ?? const []);
    final sel = _status!['selected'];
    final picked = await showPanel<Map<String, dynamic>>(context, title: tr('select_model'), children: (c) {
      final p = Palette.of(c);
      return [
        Section(header: tr('providers'), children: [
          for (final pr in providers)
            Tile(
              title: '${pr['name']} · ${pr['remote'] == true ? tr('cloud_badge') : tr('local_badge')}',
              subtitle: pr['running'] == true ? (pr['remote'] == true ? '${pr['endpoint']}' : 'v${pr['version'] ?? '?'} · ${pr['endpoint']}') : '${pr['error']}',
              leading: Icon(Icons.circle, size: 10, color: pr['running'] == true ? p.success : p.secondary),
              chevron: false,
            ),
          Tile(
            title: tr('add_cloud_ai'),
            subtitle: 'Claude · OpenAI · Gemini · OpenRouter · Groq · DeepSeek',
            icon: Ic.cloud,
            iconColor: const Color(0xFF5E5CE6),
            onTap: () => Navigator.of(c).pop(const {'__cloud': true}),
          ),
        ]),
        Section(header: tr('models'), footer: models.isEmpty ? tr('no_models') : null, children: [
          for (final m in models)
            Tile(
              title: m['id'],
              subtitle: [
                m['providerId'],
                if (m['contextLength'] != null) 'ctx ${m['contextLength']}',
                'tools ${m['tools'] == null ? '?' : (m['tools'] ? '✓' : '✗')}',
                'vision ${m['vision'] == null ? '?' : (m['vision'] ? '✓' : '✗')}',
                if (m['parameterSize'] != null) m['parameterSize'],
                if (m['quantization'] != null) m['quantization'],
              ].join(' · '),
              trailing: sel?['model'] == m['id'] ? Icon(Ic.check.data, color: p.accent) : null,
              chevron: false,
              onTap: () => Navigator.of(c).pop(m),
            ),
        ]),
      ];
    });
    if (picked == null) return;
    if (picked['__cloud'] == true) {
      if (!mounted) return;
      final r = await pushPage<Map<String, dynamic>>(context, CloudAiScreen(link: link));
      if (r != null && mounted) {
        setState(() => _status = r);
        await _pickModel();
      }
      return;
    }
    try {
      final s = await link.call('ai.select', {'provider': picked['providerId'], 'model': picked['id']});
      setState(() => _status = s);
    } on RpcError catch (e) {
      _toast(e.message);
    }
  }

  Future<void> _attach({required bool image}) async {
    if (!widget.host.can('send_files')) return _toast(tr('not_allowed'));
    final picked = await FilePicker.pickFile(type: image ? FileType.image : FileType.any);
    final path = picked?.path;
    if (path == null) return;
    final f = File(path);
    if (image) {
      if (_modelInfo?['vision'] == false) return _toast(tr('no_vision'));
      final bytes = await f.readAsBytes();
      if (bytes.length > 6 * 1024 * 1024) return _toast(tr('too_large'));
      setState(() => _attachments.add({'kind': 'image', 'name': f.uri.pathSegments.last, 'b64': base64Encode(bytes)}));
      return;
    }
    setState(() => _uploading = f.uri.pathSegments.last);
    try {
      final remote = await link.upload(f, progress: (d, t) => setState(() => _uploading = '${f.uri.pathSegments.last} ${(d * 100 / t).toStringAsFixed(0)}%'));
      final ex = await link.call('ai.extract', {'path': remote, 'maxChars': 40000});
      setState(() => _attachments.add({
            'kind': 'file', 'name': f.uri.pathSegments.last, 'path': remote, 'supported': ex['supported'], 'text': ex['text'],
            'note': ex['supported'] == true ? '${ex['kind']}${ex['truncated'] == true ? ', truncated' : ''}' : ex['message'],
          }));
    } on Object catch (e) {
      _toast('$e');
    } finally {
      if (mounted) setState(() => _uploading = null);
    }
  }

  /// Builds the user message content; file contents are clearly marked as untrusted data.
  String _composeUserContent(String text) {
    final b = StringBuffer(text);
    for (final a in _attachments.where((a) => a['kind'] == 'file')) {
      if (a['supported'] == true) {
        b.write('\n\n<attached_file name="${a['name']}" untrusted="true">\n${a['text']}\n</attached_file>');
      } else {
        b.write('\n\n[Attached file "${a['name']}" saved on the PC at ${a['path']} — its content could not be read: ${a['note']}]');
      }
    }
    return b.toString();
  }

  Future<void> _send({String? override, bool regenerate = false}) async {
    final text = override ?? _input.text.trim();
    if (!regenerate && text.isEmpty && _attachments.isEmpty) return;
    if (!link.isConnected) return _toast(tr('offline'));
    final msgs = _messages;
    if (!regenerate) {
      msgs.add({
        'role': 'user', 'content': _composeUserContent(text), 'display': text,
        'attachments': _attachments.map((a) => {'name': a['name'], 'kind': a['kind'], 'note': a['note']}).toList(),
        if (_attachments.any((a) => a['kind'] == 'image')) 'images': _attachments.where((a) => a['kind'] == 'image').map((a) => a['b64']).toList(),
      });
      if (msgs.where((m) => m['role'] == 'user').length == 1) _chat['title'] = text.isEmpty ? 'Chat' : (text.length > 40 ? '${text.substring(0, 40)}…' : text);
    }
    _input.clear();
    setState(() {
      _chat['messages'] = msgs;
      _attachments.clear();
      _streaming = true;
      _partial = '';
      _thinking = '';
    });
    _scrollToEnd();
    try {
      _streamId = link.lastRpcId + 1;
      final r = await link.callStreaming('ai.chat', {
        'messages': msgs.map((m) => {'role': m['role'], 'content': m['content'], if (m['images'] != null) 'images': m['images']}).toList(),
      }, (ev) {
        final p = ev['p'] as Map<String, dynamic>;
        setState(() {
          _partial += (p['d'] as String?) ?? '';
          _thinking += (p['t'] as String?) ?? '';
        });
        _scrollToEnd();
      });
      final usage = r['usage'] as Map<String, dynamic>?;
      msgs.add({
        'role': 'assistant', 'content': r['content'] ?? _partial, 'model': r['model'],
        if (usage != null) 'meta': '${usage['firstTokenMs']?.toStringAsFixed(0) ?? '?'} ms first token · ${usage['tokensPerSecond']?.toStringAsFixed(1) ?? '?'} tok/s',
      });
      _chat['model'] = r['model'];
    } on RpcError catch (e) {
      msgs.add({'role': 'assistant', 'content': _partial.isEmpty ? '⚠️ ${e.message}' : '$_partial\n\n⚠️ ${e.message}', 'error': true});
    } finally {
      if (mounted) {
        setState(() {
          _chat['messages'] = msgs;
          _streaming = false;
          _partial = '';
        });
      }
      _chat['updated'] = DateTime.now().millisecondsSinceEpoch;
      await ChatStore.save(_chat);
    }
  }

  void _stop() {
    if (_streamId != null) link.cancelCall(_streamId!);
  }

  void _scrollToEnd() => WidgetsBinding.instance.addPostFrameCallback((_) {
        if (_scroll.hasClients) _scroll.animateTo(_scroll.position.maxScrollExtent, duration: Motion.fast, curve: Motion.standard);
      });

  Future<void> _runAsAgent() async {
    final text = _input.text.trim();
    if (text.isEmpty) return;
    if (!widget.host.can('ai_agent')) return _toast(tr('not_allowed'));
    if (_modelInfo?['tools'] == false) return _toast(tr('model_no_tools'));
    try {
      final task = await link.call('agent.run', {'request': text});
      _input.clear();
      if (mounted) pushPage(context, AgentTaskScreen(host: widget.host, taskId: task['id']));
    } on RpcError catch (e) {
      _toast(e.message);
    }
  }

  Future<void> _plusMenu() async {
    final v = await showActions(context, actions: [
      DialogAction('file', tr('attach_file')),
      DialogAction('image', tr('attach_image')),
      if (widget.host.can('ai_agent')) DialogAction('agent', tr('agent_mode')),
      DialogAction('cancel', tr('cancel'), cancel: true),
    ]);
    switch (v) {
      case 'file':
        _attach(image: false);
      case 'image':
        _attach(image: true);
      case 'agent':
        _runAsAgent();
    }
  }

  Future<void> _messageActions(int i, Map<String, dynamic> m) async {
    final last = i == _messages.length - 1;
    final v = await showActions(context, actions: [
      DialogAction('copy', tr('copy')),
      if (m['role'] == 'user' && !_streaming) DialogAction('edit', tr('edit')),
      if (m['role'] == 'assistant' && last && !_streaming) DialogAction('regen', tr('regenerate')),
      DialogAction('cancel', tr('cancel'), cancel: true),
    ]);
    switch (v) {
      case 'copy':
        await Clipboard.setData(ClipboardData(text: m['content']));
      case 'edit':
        _input.text = (m['display'] ?? m['content']) as String;
        setState(() => _chat['messages'] = _messages.sublist(0, i));
      case 'regen':
        _chat['messages'] = _messages.sublist(0, i);
        _send(regenerate: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final msgs = _messages;
    final model = _status?['selected']?['model'] as String?;
    final titleWidget = GestureDetector(
      onTap: link.isConnected ? _pickModel : null,
      child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: PlatformUi.isIOS ? CrossAxisAlignment.center : CrossAxisAlignment.start, children: [
        Text(_chat['title'], maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 17, fontWeight: FontWeight.w600, color: p.label)),
        Text('${model ?? (link.isConnected ? tr('select_model') : stateLabel(link.state))} ▾', style: TextStyle(fontSize: 12, color: p.secondary), maxLines: 1),
      ]),
    );
    return AppPage(
      title: _chat['title'],
      titleWidget: titleWidget,
      grouped: false,
      controller: _scroll,
      actions: [if (widget.host.can('ai_agent')) BarItem('tasks', Ic.agent, tr('tasks'), () => pushPage(context, AgentTasksScreen(host: widget.host)))],
      bottom: _composer(context, p),
      slivers: [
        if (!link.isConnected) const SliverToBoxAdapter(child: Padding(padding: EdgeInsets.fromLTRB(16, 0, 16, 8), child: ProgressLine())),
        SliverPadding(
          padding: const EdgeInsets.symmetric(horizontal: 12),
          sliver: SliverList.builder(
            itemCount: msgs.length + (_streaming ? 1 : 0),
            itemBuilder: (c, i) {
              if (i == msgs.length) return _Bubble(role: 'assistant', text: _partial, thinking: _thinking, streaming: true);
              final m = msgs[i];
              return GestureDetector(
                onLongPress: () => _messageActions(i, m),
                child: _Bubble(
                  role: m['role'], text: (m['display'] ?? m['content']) as String, meta: m['meta'],
                  attachments: List<Map<String, dynamic>>.from(m['attachments'] ?? const []), error: m['error'] == true,
                ),
              );
            },
          ),
        ),
      ],
    );
  }

  Widget _composer(BuildContext context, Palette p) {
    final canSend = link.isConnected && (_input.text.trim().isNotEmpty || _attachments.isNotEmpty);
    final chips = (_attachments.isNotEmpty || _uploading != null)
        ? SizedBox(
            height: 40,
            child: ListView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.symmetric(horizontal: 12), children: [
              if (_uploading != null) _Chip(label: _uploading!, icon: null, busy: true),
              for (final a in _attachments)
                _Chip(
                  label: '${a['name']}${a['note'] != null ? ' (${a['note']})' : ''}',
                  icon: a['kind'] == 'image' ? Ic.image : (a['supported'] == false ? Ic.warning : Ic.file),
                  onRemove: () => setState(() => _attachments.remove(a)),
                ),
            ]),
          )
        : null;
    final field = AdaptiveTextField(controller: _input, placeholder: tr('type_message'), minLines: 1, maxLines: 6, action: TextInputAction.newline, dense: true, capitalization: TextCapitalization.sentences);
    Widget plus, send;
    switch (PlatformUi.style) {
      case UiStyle.glass:
        plus = GlassBar(actions: const [GlassAction('plus', 'plus', 'Attach')], onAction: (_) => _plusMenu());
        send = _streaming
            ? GlassBar(actions: [GlassAction('stop', 'stop.fill', tr('stop'), prominent: true)], onAction: (_) => _stop())
            : Opacity(opacity: canSend ? 1 : 0.45, child: GlassBar(actions: [GlassAction('send', 'arrow.up', 'Send', prominent: true)], onAction: (_) => canSend ? _send() : null));
      case UiStyle.cupertino:
        plus = CupertinoButton(padding: const EdgeInsets.all(4), minimumSize: const Size(36, 36), onPressed: _plusMenu, child: const Icon(CupertinoIcons.plus_circle_fill, size: 30));
        send = CupertinoButton(
          padding: const EdgeInsets.all(4),
          minimumSize: const Size(36, 36),
          onPressed: _streaming ? _stop : (canSend ? _send : null),
          child: Icon(_streaming ? CupertinoIcons.stop_circle_fill : CupertinoIcons.arrow_up_circle_fill, size: 32),
        );
      case UiStyle.material:
        plus = IconButton(onPressed: _plusMenu, icon: const Icon(Icons.add_circle_outline));
        send = _streaming
            ? IconButton.filled(tooltip: tr('stop'), onPressed: _stop, icon: const Icon(Icons.stop))
            : IconButton.filled(onPressed: canSend ? _send : null, icon: const Icon(Icons.arrow_upward));
    }
    final row = Padding(
      padding: const EdgeInsets.fromLTRB(8, 6, 8, 8),
      child: Row(crossAxisAlignment: CrossAxisAlignment.end, children: [plus, const SizedBox(width: 6), Expanded(child: field), const SizedBox(width: 6), send]),
    );
    final content = SafeArea(top: false, child: Column(mainAxisSize: MainAxisSize.min, children: [?chips, row]));
    if (PlatformUi.style == UiStyle.cupertino) {
      return DecoratedBox(
        decoration: BoxDecoration(
          color: CupertinoColors.systemBackground.resolveFrom(context).withValues(alpha: 0.94),
          border: Border(top: BorderSide(color: p.separator, width: 0.5)),
        ),
        child: content,
      );
    }
    return content;
  }
}

class _Chip extends StatelessWidget {
  final String label;
  final AIcon? icon;
  final bool busy;
  final VoidCallback? onRemove;
  const _Chip({required this.label, this.icon, this.busy = false, this.onRemove});
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Container(
      margin: const EdgeInsets.only(right: 6, top: 4, bottom: 4),
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(color: p.separator.withValues(alpha: 0.25), borderRadius: BorderRadius.circular(16)),
      child: Row(mainAxisSize: MainAxisSize.min, children: [
        if (busy) const Spinner(size: 12) else if (icon != null) Icon(icon!.data, size: 15, color: p.secondary),
        const SizedBox(width: 6),
        ConstrainedBox(constraints: const BoxConstraints(maxWidth: 200), child: Text(label, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 13, color: p.label))),
        if (onRemove != null) GestureDetector(onTap: onRemove, child: Padding(padding: const EdgeInsets.only(left: 6), child: Icon(Ic.close.data, size: 14, color: p.secondary))),
      ]),
    );
  }
}

class _Bubble extends StatelessWidget {
  final String role, text;
  final String? meta, thinking;
  final bool streaming, error;
  final List<Map<String, dynamic>> attachments;
  const _Bubble({required this.role, required this.text, this.meta, this.thinking, this.streaming = false, this.error = false, this.attachments = const []});

  static final _codeBlock = RegExp(r'```[a-zA-Z0-9_+-]*\n([\s\S]*?)```');

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final p = Palette.of(context);
    final user = role == 'user';
    final codes = user ? const <String>[] : _codeBlock.allMatches(text).map((m) => m.group(1)!).toList();
    final ios = PlatformUi.isIOS;
    final bubbleColor = user
        ? (ios ? Brand.seed : t.colorScheme.primary)
        : (error ? p.danger.withValues(alpha: 0.15) : (ios ? CupertinoColors.secondarySystemFill.resolveFrom(context) : t.colorScheme.surfaceContainerHighest));
    final fg = user ? Colors.white : p.label;
    return Align(
      alignment: user ? Alignment.centerRight : Alignment.centerLeft,
      child: ConstrainedBox(
        constraints: BoxConstraints(maxWidth: MediaQuery.sizeOf(context).width * 0.84),
        child: Container(
          margin: const EdgeInsets.symmetric(vertical: 4),
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
          decoration: BoxDecoration(color: bubbleColor, borderRadius: BorderRadius.circular(PlatformUi.isGlass ? 22 : 18)),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            if ((thinking ?? '').isNotEmpty) Padding(padding: const EdgeInsets.only(bottom: 6), child: Text('${tr('thinking')} ${thinking!.length > 120 ? thinking!.substring(thinking!.length - 120) : thinking}', style: TextStyle(fontSize: 12, fontStyle: FontStyle.italic, color: p.secondary))),
            for (final a in attachments)
              Padding(padding: const EdgeInsets.only(bottom: 6), child: Text('📎 ${a['name']}${a['note'] != null ? ' — ${a['note']}' : ''}', style: TextStyle(color: user ? Colors.white70 : p.secondary, fontSize: 12))),
            if (streaming && text.isEmpty)
              const Spinner(size: 16)
            else if (user)
              Text(text, style: TextStyle(color: fg, fontSize: 16))
            else
              MarkdownBody(
                data: text,
                selectable: true,
                styleSheet: MarkdownStyleSheet.fromTheme(t).copyWith(
                  p: TextStyle(fontSize: 16, color: fg, height: 1.35),
                  code: TextStyle(fontFamily: ios ? 'Menlo' : 'monospace', fontSize: 13, color: fg),
                ),
              ),
            if (!streaming) ...[
              if (meta != null) Padding(padding: const EdgeInsets.only(top: 6), child: Text(meta!, style: TextStyle(fontSize: 11, color: user ? Colors.white70 : p.secondary))),
              if (codes.isNotEmpty)
                Wrap(spacing: 4, children: [
                  for (var i = 0; i < codes.length; i++)
                    LinkButton(label: '${tr('copy')} #${i + 1}', icon: Ic.copy, onPressed: () => Clipboard.setData(ClipboardData(text: codes[i]))),
                ]),
            ],
          ]),
        ),
      ),
    );
  }
}
