import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';

import '../app_state.dart';
import '../core/host_link.dart';
import '../core/storage.dart';
import '../l10n.dart';
import '../ui/theme.dart';

class AgentTasksScreen extends StatefulWidget {
  final PairedHost host;
  const AgentTasksScreen({super.key, required this.host});
  @override
  State<AgentTasksScreen> createState() => _AgentTasksScreenState();
}

class _AgentTasksScreenState extends State<AgentTasksScreen> {
  late final HostLink link = AppState.instance.link(widget.host);
  List<dynamic> _tasks = [];
  List<dynamic> _templates = [];
  StreamSubscription? _sub;

  Future<void> _load() async {
    if (!link.isConnected) return;
    try {
      final r = await link.call('agent.list');
      final tpl = _templates.isEmpty ? await link.call('agent.templates') : null;
      if (mounted) {
        setState(() {
          _tasks = r['_list'] ?? [];
          if (tpl != null) _templates = tpl['_list'] ?? [];
        });
      }
    } on RpcError catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  void initState() {
    super.initState();
    link.addListener(_load);
    _sub = link.events.where((e) => (e['ev'] as String).startsWith('agent.')).listen((_) => _load());
    _load();
  }

  @override
  void dispose() {
    link.removeListener(_load);
    _sub?.cancel();
    super.dispose();
  }

  Future<void> _start(String request) async {
    try {
      final t = await link.call('agent.run', {'request': request});
      if (mounted) Navigator.push(context, MaterialPageRoute(builder: (_) => AgentTaskScreen(host: widget.host, taskId: t['id'])));
    } on RpcError catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  Widget build(BuildContext context) {
    final ctl = TextEditingController();
    return Scaffold(
      appBar: AppBar(title: Text(tr('tasks'))),
      body: ListView(padding: const EdgeInsets.only(bottom: 24), children: [
        Padding(
          padding: const EdgeInsets.all(16),
          child: TextField(
            controller: ctl,
            minLines: 1,
            maxLines: 4,
            decoration: InputDecoration(hintText: 'Ví dụ: Tạo thư mục Homework trên Desktop', suffixIcon: IconButton(icon: const Icon(Icons.send), onPressed: () => _start(ctl.text.trim()))),
          ),
        ),
        if (_templates.isNotEmpty)
          SizedBox(
            height: 44,
            child: ListView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.symmetric(horizontal: 12), children: [
              for (final t in _templates) Padding(padding: const EdgeInsets.all(4), child: ActionChip(label: Text(t['title']), onPressed: () => ctl.text = t['request'])),
            ]),
          ),
        if (_tasks.isEmpty) Padding(padding: const EdgeInsets.all(32), child: Center(child: Text(link.isConnected ? 'No tasks yet.' : tr('offline')))),
        for (final t in _tasks)
          Card(
            child: ListTile(
              leading: _stateIcon(context, t['state']),
              title: Text(t['request'], maxLines: 2, overflow: TextOverflow.ellipsis),
              subtitle: Text('${t['state']} · ${(t['steps'] as List).length} steps'),
              onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => AgentTaskScreen(host: widget.host, taskId: t['id']))),
            ),
          ),
      ]),
    );
  }
}

Widget _stateIcon(BuildContext c, String s) => switch (s) {
      'Completed' => const Icon(Icons.check_circle, color: Colors.green),
      'Failed' => Icon(Icons.error, color: Theme.of(c).colorScheme.error),
      'Cancelled' => const Icon(Icons.cancel, color: Colors.grey),
      'WaitingForApproval' => const Icon(Icons.pending_actions, color: Colors.orange),
      'Paused' => const Icon(Icons.pause_circle),
      _ => const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)),
    };

/// Live view of one task: plan, timeline, screenshots, approvals, pause/resume/cancel.
class AgentTaskScreen extends StatefulWidget {
  final PairedHost host;
  final String taskId;
  const AgentTaskScreen({super.key, required this.host, required this.taskId});
  @override
  State<AgentTaskScreen> createState() => _AgentTaskScreenState();
}

class _AgentTaskScreenState extends State<AgentTaskScreen> {
  late final HostLink link = AppState.instance.link(widget.host);
  Map<String, dynamic>? _task;
  final Map<int, String> _images = {};
  StreamSubscription? _sub;
  String? _lastApproval;

  @override
  void initState() {
    super.initState();
    _sub = link.events.where((e) => e['p']?['taskId'] == widget.taskId).listen((e) {
      final p = e['p'] as Map<String, dynamic>;
      if (p['task'] != null) setState(() => _task = p['task']);
      if (e['ev'] == 'agent.image') setState(() => _images[p['step']] = p['png']);
      if (e['ev'] == 'agent.approval') _refresh();
    });
    _refresh();
  }

  @override
  void dispose() {
    _sub?.cancel();
    super.dispose();
  }

  Future<void> _refresh() async {
    try {
      final t = await link.call('agent.status', {'taskId': widget.taskId});
      if (!mounted) return;
      setState(() => _task = t);
      final a = t['approval'];
      if (a != null && a['approvalId'] != _lastApproval) {
        _lastApproval = a['approvalId'];
        _showApproval(Map<String, dynamic>.from(a));
      }
    } on RpcError catch (_) {}
  }

  Future<void> _showApproval(Map<String, dynamic> a) async {
    final planCtl = TextEditingController(text: a['detail']);
    final isPlan = a['kind'] == 'plan';
    final risk = a['risk'] as String;
    final ok = await showModalBottomSheet<bool>(
      context: context,
      isDismissible: false,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (c) => Padding(
        padding: EdgeInsets.fromLTRB(20, 0, 20, 20 + MediaQuery.of(c).viewInsets.bottom),
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [
            Icon(risk == 'Sensitive' ? Icons.warning_amber_rounded : Icons.shield_outlined, color: risk == 'Sensitive' ? Colors.red : Colors.orange),
            const SizedBox(width: 8),
            Expanded(child: Text(tr('approval_needed'), style: Theme.of(c).textTheme.titleMedium)),
            Chip(label: Text(risk)),
          ]),
          const SizedBox(height: 8),
          Text(a['title'], style: Theme.of(c).textTheme.titleSmall),
          const SizedBox(height: 8),
          if (isPlan)
            TextField(controller: planCtl, maxLines: 8, minLines: 3)
          else
            Container(
              width: double.infinity,
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(color: Theme.of(c).colorScheme.surfaceContainerHighest, borderRadius: BorderRadius.circular(10)),
              child: SelectableText(a['detail'], style: const TextStyle(fontFamily: 'monospace', fontSize: 12)),
            ),
          const SizedBox(height: 16),
          Row(children: [
            Expanded(child: OutlinedButton(onPressed: () => Navigator.pop(c, false), child: Text(tr('reject')))),
            const SizedBox(width: 12),
            Expanded(child: FilledButton(
                style: risk == 'Sensitive' ? FilledButton.styleFrom(backgroundColor: Colors.red) : null,
                onPressed: () => Navigator.pop(c, true), child: Text(tr('approve')))),
          ]),
        ]),
      ),
    );
    try {
      await link.call('agent.approve', {'taskId': widget.taskId, 'approvalId': a['approvalId'], 'approved': ok == true, if (isPlan && ok == true) 'plan': planCtl.text});
    } on RpcError catch (_) {}
  }

  Future<void> _loadImage(int step) async {
    try {
      final r = await link.call('agent.image', {'taskId': widget.taskId, 'step': step});
      setState(() => _images[step] = r['png']);
    } on RpcError catch (_) {}
  }

  @override
  Widget build(BuildContext context) {
    final t = _task;
    final th = Theme.of(context);
    final running = t != null && !['Completed', 'Failed', 'Cancelled'].contains(t['state']);
    return Scaffold(
      appBar: AppBar(title: Text(tr('tasks')), actions: [
        if (running && t['state'] != 'Paused')
          IconButton(tooltip: tr('pause'), icon: const Icon(Icons.pause), onPressed: () => link.call('agent.pause', {'taskId': widget.taskId, 'pause': true})),
        if (running && t['state'] == 'Paused')
          IconButton(tooltip: tr('resume'), icon: const Icon(Icons.play_arrow), onPressed: () => link.call('agent.pause', {'taskId': widget.taskId, 'pause': false})),
        if (running) IconButton(tooltip: tr('cancel'), icon: const Icon(Icons.stop_circle_outlined), onPressed: () => link.call('agent.cancel', {'taskId': widget.taskId})),
      ]),
      body: t == null
          ? const Center(child: CircularProgressIndicator())
          : ListView(padding: const EdgeInsets.all(16), children: [
              Text(t['request'], style: th.textTheme.titleMedium),
              const SizedBox(height: 8),
              Row(children: [_stateIcon(context, t['state']), const SizedBox(width: 8), Text(t['state'])]),
              if (t['approval'] != null)
                Card(
                  color: th.colorScheme.tertiaryContainer,
                  child: ListTile(leading: const Icon(Icons.pending_actions), title: Text(tr('approval_needed')), subtitle: Text(t['approval']['title']),
                      onTap: () => _showApproval(Map<String, dynamic>.from(t['approval']))),
                ),
              if (t['plan'] != null) ...[
                const SizedBox(height: 12),
                Text('Plan', style: th.textTheme.labelLarge),
                Text(t['plan']),
              ],
              const SizedBox(height: 16),
              for (final s in t['steps'] as List)
                AnimatedContainer(
                  duration: Motion.medium,
                  margin: const EdgeInsets.only(bottom: 8),
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(color: th.colorScheme.surfaceContainerHighest, borderRadius: BorderRadius.circular(14)),
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Row(children: [
                      Icon(switch (s['status']) { 'ok' => Icons.check_circle, 'running' => Icons.timelapse, 'rejected' || 'denied' => Icons.block, _ => Icons.error_outline },
                          size: 18, color: s['status'] == 'ok' ? Colors.green : (s['status'] == 'running' ? null : th.colorScheme.error)),
                      const SizedBox(width: 6),
                      Expanded(child: Text(s['description'], style: const TextStyle(fontWeight: FontWeight.w600))),
                      if (s['verified'] == true) const Tooltip(message: 'verified', child: Icon(Icons.verified, size: 16, color: Colors.green)),
                    ]),
                    Text(s['tool'], style: th.textTheme.labelSmall),
                    if (s['result'] != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text(s['result'], maxLines: 6, overflow: TextOverflow.ellipsis, style: th.textTheme.bodySmall)),
                    if (s['error'] != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text(s['error'], style: TextStyle(color: th.colorScheme.error, fontSize: 12))),
                    if (s['hasImage'] == true)
                      _images[s['index']] != null
                          ? Padding(padding: const EdgeInsets.only(top: 8), child: ClipRRect(borderRadius: BorderRadius.circular(10), child: Image.memory(base64Decode(_images[s['index']]!))))
                          : TextButton.icon(onPressed: () => _loadImage(s['index']), icon: const Icon(Icons.image), label: const Text('Show screenshot')),
                  ]),
                ),
              if (t['answer'] != null) Card(child: Padding(padding: const EdgeInsets.all(14), child: SelectableText(t['answer']))),
              if (t['error'] != null) Text(t['error'], style: TextStyle(color: th.colorScheme.error)),
            ]),
    );
  }
}
