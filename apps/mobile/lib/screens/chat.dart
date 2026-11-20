import 'dart:convert';
import 'dart:io';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_markdown_plus/flutter_markdown_plus.dart';
import 'package:uuid/uuid.dart';

import '../app_state.dart';
import '../core/host_link.dart';
import '../core/storage.dart';
import '../l10n.dart';
import '../ui/theme.dart';
import 'agent.dart';

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

  @override
  Widget build(BuildContext context) {
    final hosts = AppState.instance.hosts;
    final filtered = _chats.where((c) => _q.isEmpty || (c['title'] as String).toLowerCase().contains(_q) ||
        (c['messages'] as List).any((m) => (m['content'] as String).toLowerCase().contains(_q))).toList();
    return Scaffold(
      appBar: AppBar(title: Text(tr('ai'))),
      floatingActionButton: hosts.isEmpty
          ? null
          : FloatingActionButton.extended(
              icon: const Icon(Icons.add_comment_outlined),
              label: Text(tr('new_chat')),
              onPressed: () async {
                final h = hosts.length == 1 ? hosts.first : await _pickHost(context);
                if (h == null || !context.mounted) return;
                final link = AppState.instance.link(h);
                if (!link.isConnected) link.connect();
                await Navigator.push(context, MaterialPageRoute(builder: (_) => ChatScreen(host: h)));
                _load();
              },
            ),
      body: Column(children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
          child: TextField(decoration: InputDecoration(prefixIcon: const Icon(Icons.search), hintText: tr('search')), onChanged: (v) => setState(() => _q = v.toLowerCase())),
        ),
        Expanded(
          child: filtered.isEmpty
              ? EmptyState(icon: Icons.forum_outlined, title: tr('new_chat'))
              : ListView(children: [
                  for (final c in filtered)
                    Dismissible(
                      key: ValueKey(c['id']),
                      direction: DismissDirection.endToStart,
                      background: Container(color: Theme.of(context).colorScheme.errorContainer, alignment: Alignment.centerRight, padding: const EdgeInsets.only(right: 20), child: const Icon(Icons.delete)),
                      onDismissed: (_) => ChatStore.delete(c['id']).then((_) => _load()),
                      child: ListTile(
                        leading: const CircleAvatar(child: Icon(Icons.auto_awesome, size: 18)),
                        title: Text(c['title'], maxLines: 1, overflow: TextOverflow.ellipsis),
                        subtitle: Text('${c['model'] ?? ''} · ${DateTime.fromMillisecondsSinceEpoch(c['updated'])}'.split('.').first, maxLines: 1),
                        onTap: () async {
                          final h = hosts.where((x) => x.id == c['hostId']).firstOrNull;
                          if (h == null) return;
                          final link = AppState.instance.link(h);
                          if (!link.isConnected) link.connect();
                          await Navigator.push(context, MaterialPageRoute(builder: (_) => ChatScreen(host: h, chat: c)));
                          _load();
                        },
                        onLongPress: () async {
                          final ctl = TextEditingController(text: c['title']);
                          final name = await showDialog<String>(context: context, builder: (d) => AlertDialog(
                                title: Text(tr('rename')),
                                content: TextField(controller: ctl, autofocus: true),
                                actions: [TextButton(onPressed: () => Navigator.pop(d), child: Text(tr('cancel'))), FilledButton(onPressed: () => Navigator.pop(d, ctl.text), child: const Text('OK'))],
                              ));
                          if (name != null && name.trim().isNotEmpty) {
                            c['title'] = name.trim();
                            await ChatStore.save(c);
                            _load();
                          }
                        },
                      ),
                    ),
                ]),
        ),
      ]),
    );
  }
}

Future<PairedHost?> _pickHost(BuildContext context) => showModalBottomSheet<PairedHost>(
      context: context,
      showDragHandle: true,
      builder: (c) => ListView(shrinkWrap: true, children: [
        for (final h in AppState.instance.hosts) ListTile(leading: const Icon(Icons.computer), title: Text(h.name), onTap: () => Navigator.pop(c, h)),
      ]),
    );

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
    _chat = widget.chat != null ? Map<String, dynamic>.from(widget.chat!) : {
      'id': const Uuid().v4(), 'hostId': widget.host.id, 'title': tr('new_chat'), 'messages': <Map<String, dynamic>>[],
      'updated': DateTime.now().millisecondsSinceEpoch,
    };
    link.addListener(_onLink);
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

  Future<void> _refreshStatus({bool rescan = false}) async {
    try {
      final s = await link.call(rescan ? 'ai.models' : 'ai.status', rescan ? {'rescan': true} : null);
      if (mounted) setState(() => _status = s);
    } on RpcError catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  Map<String, dynamic>? get _modelInfo => _status?['selected']?['info'] as Map<String, dynamic>?;

  Future<void> _pickModel() async {
    await _refreshStatus(rescan: true);
    if (!mounted || _status == null) return;
    final models = List<Map<String, dynamic>>.from(_status!['models'] ?? const []);
    final sel = _status!['selected'];
    final picked = await showModalBottomSheet<Map<String, dynamic>>(context: context, showDragHandle: true, isScrollControlled: true, builder: (c) => DraggableScrollableSheet(
          expand: false,
          builder: (c, sc) => ListView(controller: sc, children: [
            Padding(padding: const EdgeInsets.all(16), child: Text(tr('select_model'), style: Theme.of(context).textTheme.titleMedium)),
            for (final p in List<Map<String, dynamic>>.from(_status!['providers'] ?? const []))
              ListTile(dense: true, leading: Icon(Icons.circle, size: 10, color: p['running'] == true ? Colors.green : Colors.grey), title: Text(p['name']), subtitle: Text(p['running'] == true ? 'v${p['version'] ?? '?'} · ${p['endpoint']}' : '${p['error']}')),
            const Divider(),
            if (models.isEmpty) const ListTile(title: Text('No models found on the PC. Download one in Ollama or LM Studio on the PC.')),
            for (final m in models)
              ListTile(
                title: Text(m['id']),
                subtitle: Text([
                  m['providerId'],
                  if (m['contextLength'] != null) 'ctx ${m['contextLength']}',
                  'tools ${m['tools'] == null ? '?' : (m['tools'] ? '✓' : '✗')}',
                  'vision ${m['vision'] == null ? '?' : (m['vision'] ? '✓' : '✗')}',
                  if (m['parameterSize'] != null) m['parameterSize'],
                  if (m['quantization'] != null) m['quantization'],
                ].join(' · ')),
                trailing: sel?['model'] == m['id'] ? const Icon(Icons.check_circle) : null,
                onTap: () => Navigator.pop(c, m),
              ),
          ]),
        ));
    if (picked == null) return;
    try {
      final s = await link.call('ai.select', {'provider': picked['providerId'], 'model': picked['id']});
      setState(() => _status = s);
    } on RpcError catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  Future<void> _attach({required bool image}) async {
    if (!widget.host.can('send_files')) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(tr('not_allowed'))));
      return;
    }
    final picked = await FilePicker.pickFile(type: image ? FileType.image : FileType.any);
    final path = picked?.path;
    if (path == null) return;
    final f = File(path);
    if (image) {
      if (_modelInfo?['vision'] == false) {
        if (mounted) ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('The selected model cannot see images. Choose a vision model.')));
        return;
      }
      final bytes = await f.readAsBytes();
      if (bytes.length > 6 * 1024 * 1024) {
        if (mounted) ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Image too large (max 6 MB).')));
        return;
      }
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
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('$e')));
    } finally {
      setState(() => _uploading = null);
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
    if (!link.isConnected) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(tr('offline'))));
      return;
    }
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
      msgs.add({'role': 'assistant', 'content': r['content'] ?? _partial, 'model': r['model'],
        if (usage != null) 'meta': '${usage['firstTokenMs']?.toStringAsFixed(0) ?? '?'} ms first token · ${usage['tokensPerSecond']?.toStringAsFixed(1) ?? '?'} tok/s'});
      _chat['model'] = r['model'];
    } on RpcError catch (e) {
      msgs.add({'role': 'assistant', 'content': _partial.isEmpty ? '⚠️ ${e.message}' : '$_partial\n\n⚠️ ${e.message}', 'error': true});
    } finally {
      setState(() {
        _chat['messages'] = msgs;
        _streaming = false;
        _partial = '';
      });
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
    if (!widget.host.can('ai_agent')) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(tr('not_allowed'))));
      return;
    }
    if (_modelInfo?['tools'] == false) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(tr('model_no_tools'))));
      return;
    }
    try {
      final task = await link.call('agent.run', {'request': text});
      _input.clear();
      if (mounted) Navigator.push(context, MaterialPageRoute(builder: (_) => AgentTaskScreen(host: widget.host, taskId: task['id'])));
    } on RpcError catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final msgs = _messages;
    return Scaffold(
      appBar: AppBar(
        title: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(_chat['title'], maxLines: 1, overflow: TextOverflow.ellipsis),
          InkWell(
            onTap: link.isConnected ? _pickModel : null,
            child: Text('${_status?['selected']?['model'] ?? (link.isConnected ? tr('select_model') : tr('offline'))} ▾', style: t.textTheme.bodySmall),
          ),
        ]),
        actions: [
          if (widget.host.can('ai_agent'))
            IconButton(tooltip: tr('tasks'), icon: const Icon(Icons.smart_toy_outlined),
                onPressed: () => Navigator.push(context, MaterialPageRoute(builder: (_) => AgentTasksScreen(host: widget.host)))),
        ],
      ),
      body: Column(children: [
        if (!link.isConnected) LinearProgressIndicator(value: link.state == LinkState.failed ? 0 : null),
        Expanded(
          child: ListView.builder(
            controller: _scroll,
            padding: const EdgeInsets.all(12),
            itemCount: msgs.length + (_streaming ? 1 : 0),
            itemBuilder: (c, i) {
              if (i == msgs.length) return _Bubble(role: 'assistant', text: _partial, thinking: _thinking, streaming: true);
              final m = msgs[i];
              return _Bubble(
                role: m['role'], text: (m['display'] ?? m['content']) as String, meta: m['meta'], attachments: List<Map<String, dynamic>>.from(m['attachments'] ?? const []),
                error: m['error'] == true,
                onCopy: () => Clipboard.setData(ClipboardData(text: m['content'])),
                onRegenerate: m['role'] == 'assistant' && i == msgs.length - 1 && !_streaming
                    ? () {
                        _chat['messages'] = msgs.sublist(0, i);
                        _send(regenerate: true);
                      }
                    : null,
                onEdit: m['role'] == 'user' && !_streaming
                    ? () {
                        _input.text = (m['display'] ?? m['content']) as String;
                        setState(() => _chat['messages'] = msgs.sublist(0, i));
                      }
                    : null,
              );
            },
          ),
        ),
        if (_attachments.isNotEmpty || _uploading != null)
          SizedBox(
            height: 48,
            child: ListView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.symmetric(horizontal: 12), children: [
              if (_uploading != null) Padding(padding: const EdgeInsets.all(4), child: Chip(avatar: const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2)), label: Text(_uploading!))),
              for (final a in _attachments)
                Padding(
                  padding: const EdgeInsets.all(4),
                  child: InputChip(
                    avatar: Icon(a['kind'] == 'image' ? Icons.image : (a['supported'] == false ? Icons.warning_amber : Icons.description), size: 16),
                    label: Text('${a['name']}${a['note'] != null ? ' (${a['note']})' : ''}', overflow: TextOverflow.ellipsis),
                    onDeleted: () => setState(() => _attachments.remove(a)),
                  ),
                ),
            ]),
          ),
        SafeArea(
          top: false,
          child: Padding(
            padding: const EdgeInsets.fromLTRB(8, 4, 8, 8),
            child: Row(crossAxisAlignment: CrossAxisAlignment.end, children: [
              PopupMenuButton<String>(
                icon: const Icon(Icons.add_circle_outline),
                onSelected: (v) => switch (v) { 'file' => _attach(image: false), 'image' => _attach(image: true), 'agent' => _runAsAgent(), _ => null },
                itemBuilder: (_) => [
                  PopupMenuItem(value: 'file', child: ListTile(leading: const Icon(Icons.attach_file), title: Text(tr('attach_file')))),
                  PopupMenuItem(value: 'image', child: ListTile(leading: const Icon(Icons.image_outlined), title: Text(tr('attach_image')))),
                  if (widget.host.can('ai_agent')) PopupMenuItem(value: 'agent', child: ListTile(leading: const Icon(Icons.smart_toy_outlined), title: Text(tr('agent_mode')))),
                ],
              ),
              Expanded(
                child: TextField(
                  controller: _input,
                  minLines: 1,
                  maxLines: 6,
                  textInputAction: TextInputAction.newline,
                  decoration: InputDecoration(hintText: tr('type_message'), contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10)),
                ),
              ),
              const SizedBox(width: 6),
              AnimatedSwitcher(
                duration: Motion.fast,
                child: _streaming
                    ? IconButton.filled(key: const ValueKey('stop'), tooltip: tr('stop'), onPressed: _stop, icon: const Icon(Icons.stop))
                    : IconButton.filled(key: const ValueKey('send'), onPressed: link.isConnected ? () => _send() : null, icon: const Icon(Icons.arrow_upward)),
              ),
            ]),
          ),
        ),
      ]),
    );
  }
}

class _Bubble extends StatelessWidget {
  final String role, text;
  final String? meta, thinking;
  final bool streaming, error;
  final List<Map<String, dynamic>> attachments;
  final VoidCallback? onCopy, onRegenerate, onEdit;
  const _Bubble({required this.role, required this.text, this.meta, this.thinking, this.streaming = false, this.error = false,
      this.attachments = const [], this.onCopy, this.onRegenerate, this.onEdit});

  static final _codeBlock = RegExp(r'```[a-zA-Z0-9_+-]*\n([\s\S]*?)```');

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final user = role == 'user';
    final codes = user ? const <String>[] : _codeBlock.allMatches(text).map((m) => m.group(1)!).toList();
    return TweenAnimationBuilder<double>(
      tween: Tween(begin: 0, end: 1),
      duration: Motion.medium,
      curve: Motion.standard,
      builder: (_, v, c) => Opacity(opacity: v, child: Transform.translate(offset: Offset(0, 8 * (1 - v)), child: c)),
      child: Align(
        alignment: user ? Alignment.centerRight : Alignment.centerLeft,
        child: ConstrainedBox(
          constraints: BoxConstraints(maxWidth: MediaQuery.of(context).size.width * 0.86),
          child: Container(
            margin: const EdgeInsets.symmetric(vertical: 5),
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: user ? t.colorScheme.primary : (error ? t.colorScheme.errorContainer : t.colorScheme.surfaceContainerHighest),
              borderRadius: BorderRadius.circular(18).copyWith(bottomRight: user ? const Radius.circular(4) : null, bottomLeft: user ? null : const Radius.circular(4)),
            ),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              if ((thinking ?? '').isNotEmpty)
                ExpansionTile(tilePadding: EdgeInsets.zero, title: const Text('Thinking…', style: TextStyle(fontSize: 13)), children: [Text(thinking!, style: t.textTheme.bodySmall)]),
              for (final a in attachments)
                Padding(padding: const EdgeInsets.only(bottom: 6), child: Text('📎 ${a['name']}${a['note'] != null ? ' — ${a['note']}' : ''}', style: TextStyle(color: user ? Colors.white70 : null, fontSize: 12))),
              if (streaming && text.isEmpty)
                const _TypingDots()
              else if (user)
                SelectableText(text, style: const TextStyle(color: Colors.white))
              else
                MarkdownBody(data: text, selectable: true, styleSheet: MarkdownStyleSheet.fromTheme(t).copyWith(code: t.textTheme.bodySmall?.copyWith(fontFamily: 'monospace'))),
              if (!streaming) ...[
                if (meta != null) Padding(padding: const EdgeInsets.only(top: 6), child: Text(meta!, style: t.textTheme.labelSmall)),
                Row(mainAxisSize: MainAxisSize.min, children: [
                  for (var i = 0; i < codes.length; i++)
                    TextButton.icon(onPressed: () => Clipboard.setData(ClipboardData(text: codes[i])), icon: const Icon(Icons.code, size: 16), label: Text('${tr('copy')} #${i + 1}')),
                  if (onCopy != null) IconButton(visualDensity: VisualDensity.compact, iconSize: 18, tooltip: tr('copy'), onPressed: onCopy, icon: Icon(Icons.copy, color: user ? Colors.white70 : null)),
                  if (onEdit != null) IconButton(visualDensity: VisualDensity.compact, iconSize: 18, tooltip: tr('edit'), onPressed: onEdit, icon: const Icon(Icons.edit, color: Colors.white70)),
                  if (onRegenerate != null) IconButton(visualDensity: VisualDensity.compact, iconSize: 18, tooltip: tr('regenerate'), onPressed: onRegenerate, icon: const Icon(Icons.refresh)),
                ]),
              ],
            ]),
          ),
        ),
      ),
    );
  }
}

/// "AI thinking" indicator — finite per-frame cost, stops when the widget is removed.
class _TypingDots extends StatefulWidget {
  const _TypingDots();
  @override
  State<_TypingDots> createState() => _TypingDotsState();
}

class _TypingDotsState extends State<_TypingDots> with SingleTickerProviderStateMixin {
  late final _c = AnimationController(vsync: this, duration: const Duration(milliseconds: 900));
  @override
  void initState() {
    super.initState();
    if (!Motion.reduced) _c.repeat();
  }

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
        animation: _c,
        builder: (_, _) => Row(mainAxisSize: MainAxisSize.min, children: [
          for (var i = 0; i < 3; i++)
            Opacity(
              opacity: Motion.reduced ? 0.6 : 0.3 + 0.7 * ((((_c.value * 3) - i) % 3) < 1 ? 1 : 0),
              child: const Padding(padding: EdgeInsets.all(3), child: CircleAvatar(radius: 4)),
            ),
        ]),
      );
}
