import 'dart:async';
import 'dart:convert';

import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';

import '../app_state.dart';
import '../core/host_link.dart';
import '../core/storage.dart';
import '../l10n.dart';
import '../ui/adaptive.dart';
import '../ui/platform_ui.dart';
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
  final _ctl = TextEditingController();

  Future<void> _load() async {
    if (!link.isConnected) {
      if (mounted) setState(() {});
      return;
    }
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
      if (mounted) showToast(context, e.message);
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

  Future<void> _start() async {
    final request = _ctl.text.trim();
    if (request.isEmpty) return;
    try {
      final t = await link.call('agent.run', {'request': request});
      _ctl.clear();
      if (mounted) pushPage(context, AgentTaskScreen(host: widget.host, taskId: t['id']));
    } on RpcError catch (e) {
      if (mounted) showToast(context, e.message);
    }
  }

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return AppPage(title: tr('tasks'), onRefresh: _load, slivers: [
      SliverList.list(children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 10),
          child: AdaptiveTextField(controller: _ctl, placeholder: tr('agent_hint'), minLines: 2, maxLines: 5, capitalization: TextCapitalization.sentences),
        ),
        Padding(padding: const EdgeInsets.fromLTRB(16, 0, 16, 12), child: PrimaryButton(id: 'agent-run', label: tr('agent_mode'), icon: Ic.agent, onPressed: link.isConnected ? _start : null)),
        if (_templates.isNotEmpty)
          SizedBox(
            height: 44,
            child: ListView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.symmetric(horizontal: 12), children: [
              for (final t in _templates)
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 4),
                  child: PlatformUi.isIOS
                      ? CupertinoButton.tinted(
                          padding: const EdgeInsets.symmetric(horizontal: 14),
                          minimumSize: const Size(0, 36),
                          borderRadius: BorderRadius.circular(18),
                          onPressed: () => _ctl.text = t['request'],
                          child: Text(t['title'], style: const TextStyle(fontSize: 14)),
                        )
                      : ActionChip(label: Text(t['title']), onPressed: () => _ctl.text = t['request']),
                ),
            ]),
          ),
        const SizedBox(height: 8),
        if (!link.isConnected) const Padding(padding: EdgeInsets.fromLTRB(16, 0, 16, 8), child: ProgressLine()),
        if (_tasks.isEmpty)
          Padding(padding: const EdgeInsets.all(32), child: Center(child: Text(link.isConnected ? tr('no_tasks') : tr('offline'), style: TextStyle(color: p.secondary))))
        else
          Section(children: [
            for (final t in _tasks)
              Tile(
                title: t['request'],
                subtitle: '${t['state']} · ${(t['steps'] as List).length} ${tr('steps')}',
                leading: stateIcon(context, t['state']),
                onTap: () => pushPage(context, AgentTaskScreen(host: widget.host, taskId: t['id'])),
              ),
          ]),
      ]),
    ]);
  }
}

Widget stateIcon(BuildContext c, String s) {
  final p = Palette.of(c);
  return switch (s) {
    'Completed' => Icon(Ic.ok.data, color: p.success),
    'Failed' => Icon(Ic.error.data, color: p.danger),
    'Cancelled' => Icon(Ic.close.data, color: p.secondary),
    'WaitingForApproval' => Icon(Ic.clock.data, color: p.warning),
    'Paused' => Icon(Ic.pause.data, color: p.secondary),
    _ => const Spinner(size: 20),
  };
}

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
    final sensitive = risk == 'Sensitive';
    final ok = await showPanel<bool>(context, title: tr('approval_needed'), dismissible: false, children: (c) {
      final p = Palette.of(c);
      return [
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 0, 20, 12),
          child: Row(children: [
            Icon(sensitive ? Ic.warning.data : Ic.shield.data, color: sensitive ? p.danger : p.warning),
            const SizedBox(width: 8),
            Expanded(child: Text(a['title'], style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600, color: p.label))),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
              decoration: BoxDecoration(color: (sensitive ? p.danger : p.warning).withValues(alpha: 0.15), borderRadius: BorderRadius.circular(12)),
              child: Text(risk, style: TextStyle(fontSize: 12, color: sensitive ? p.danger : p.warning, fontWeight: FontWeight.w600)),
            ),
          ]),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
          child: isPlan
              ? AdaptiveTextField(controller: planCtl, minLines: 4, maxLines: 10)
              : Container(
                  width: double.infinity,
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(color: p.card, borderRadius: BorderRadius.circular(PlatformUi.isGlass ? 20 : 12)),
                  child: SelectableText(a['detail'], style: TextStyle(fontFamily: PlatformUi.isIOS ? 'Menlo' : 'monospace', fontSize: 12, color: p.label)),
                ),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
          child: Row(children: [
            Expanded(child: PrimaryButton(id: 'reject', label: tr('reject'), secondary: true, onPressed: () => Navigator.of(c).pop(false))),
            const SizedBox(width: 12),
            Expanded(child: PrimaryButton(id: 'approve', label: tr('approve'), destructive: sensitive, onPressed: () => Navigator.of(c).pop(true))),
          ]),
        ),
      ];
    });
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
    final p = Palette.of(context);
    final running = t != null && !['Completed', 'Failed', 'Cancelled'].contains(t['state']);
    final radius = PlatformUi.isGlass ? 22.0 : (PlatformUi.isIOS ? 12.0 : 18.0);
    return AppPage(
      title: tr('tasks'),
      actions: [
        if (running && t['state'] != 'Paused') BarItem('pause', Ic.pause, tr('pause'), () => link.call('agent.pause', {'taskId': widget.taskId, 'pause': true})),
        if (running && t['state'] == 'Paused') BarItem('resume', Ic.play, tr('resume'), () => link.call('agent.pause', {'taskId': widget.taskId, 'pause': false})),
        if (running) BarItem('cancel', Ic.stop, tr('cancel'), () => link.call('agent.cancel', {'taskId': widget.taskId}), destructive: true),
      ],
      slivers: [
        if (t == null)
          const SliverFillRemaining(child: Center(child: Spinner(size: 28)))
        else
          SliverPadding(
            padding: const EdgeInsets.symmetric(horizontal: 16),
            sliver: SliverList.list(children: [
              Text(t['request'], style: TextStyle(fontSize: 20, fontWeight: FontWeight.w600, color: p.label)),
              const SizedBox(height: 8),
              Row(children: [stateIcon(context, t['state']), const SizedBox(width: 8), Text(t['state'], style: TextStyle(color: p.secondary))]),
              if (t['approval'] != null) ...[
                const SizedBox(height: 12),
                PrimaryButton(id: 'open-approval', label: '${tr('approval_needed')}: ${t['approval']['title']}', icon: Ic.shield, onPressed: () => _showApproval(Map<String, dynamic>.from(t['approval']))),
              ],
              if (t['plan'] != null) ...[
                const SizedBox(height: 16),
                Text(tr('plan'), style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600, color: p.secondary)),
                const SizedBox(height: 4),
                Text(t['plan'], style: TextStyle(color: p.label)),
              ],
              const SizedBox(height: 16),
              for (final s in t['steps'] as List)
                AnimatedContainer(
                  duration: Motion.medium,
                  margin: const EdgeInsets.only(bottom: 8),
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(color: p.card, borderRadius: BorderRadius.circular(radius)),
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Row(children: [
                      Icon(
                        switch (s['status']) { 'ok' => Ic.ok.data, 'running' => Ic.clock.data, 'rejected' || 'denied' => Ic.close.data, _ => Ic.error.data },
                        size: 18,
                        color: s['status'] == 'ok' ? p.success : (s['status'] == 'running' ? p.secondary : p.danger),
                      ),
                      const SizedBox(width: 6),
                      Expanded(child: Text(s['description'], style: TextStyle(fontWeight: FontWeight.w600, color: p.label))),
                      if (s['verified'] == true) Icon(Ic.verified.data, size: 16, color: p.success),
                    ]),
                    Text(s['tool'], style: TextStyle(fontSize: 12, color: p.secondary)),
                    if (s['result'] != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text(s['result'], maxLines: 6, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 13, color: p.label))),
                    if (s['error'] != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text(s['error'], style: TextStyle(color: p.danger, fontSize: 12))),
                    if (s['hasImage'] == true)
                      _images[s['index']] != null
                          ? Padding(padding: const EdgeInsets.only(top: 8), child: ClipRRect(borderRadius: BorderRadius.circular(10), child: Image.memory(base64Decode(_images[s['index']]!))))
                          : LinkButton(label: tr('show_screenshot'), icon: Ic.image, onPressed: () => _loadImage(s['index'])),
                  ]),
                ),
              if (t['answer'] != null)
                Container(
                  padding: const EdgeInsets.all(14),
                  decoration: BoxDecoration(color: p.card, borderRadius: BorderRadius.circular(radius)),
                  child: SelectableText(t['answer'], style: TextStyle(color: p.label)),
                ),
              if (t['error'] != null) Text(t['error'], style: TextStyle(color: p.danger)),
            ]),
          ),
      ],
    );
  }
}
