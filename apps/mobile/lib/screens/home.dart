import 'package:flutter/material.dart';

import '../app_state.dart';
import '../core/host_link.dart';
import '../core/storage.dart';
import '../l10n.dart';
import '../ui/theme.dart';
import 'agent.dart';
import 'chat.dart';
import 'files.dart';
import 'pair.dart';
import 'remote.dart';

class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final s = AppState.instance;
    return Scaffold(
      appBar: AppBar(title: Text(tr('app')), centerTitle: false),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const PairScreen())),
        icon: const Icon(Icons.add),
        label: Text(tr('add_pc')),
      ),
      body: ListenableBuilder(
        listenable: s,
        builder: (context, _) => s.hosts.isEmpty
            ? EmptyState(icon: Icons.computer_outlined, title: tr('no_pcs'), subtitle: tr('no_pcs_sub'))
            : ListView(padding: const EdgeInsets.only(top: 8, bottom: 96), children: [for (final h in s.hosts) HostCard(host: h)]),
      ),
    );
  }
}

String stateLabel(LinkState st) => switch (st) {
      LinkState.connected => tr('connected'),
      LinkState.reconnecting => tr('reconnecting'),
      LinkState.failed => tr('failed'),
      LinkState.idle || LinkState.closed => tr('offline'),
      _ => tr('connecting'),
    };

Color stateColor(BuildContext c, LinkState st) => switch (st) {
      LinkState.connected => Colors.green,
      LinkState.failed => Theme.of(c).colorScheme.error,
      LinkState.idle || LinkState.closed => Theme.of(c).colorScheme.outline,
      _ => Colors.orange,
    };

class HostCard extends StatelessWidget {
  final PairedHost host;
  const HostCard({super.key, required this.host});

  @override
  Widget build(BuildContext context) {
    final link = AppState.instance.link(host);
    final t = Theme.of(context);
    return ListenableBuilder(
      listenable: link,
      builder: (context, _) {
        final busy = link.state != LinkState.connected && link.state != LinkState.idle && link.state != LinkState.closed && link.state != LinkState.failed;
        return Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Row(children: [
                Container(
                  width: 46, height: 46,
                  decoration: BoxDecoration(gradient: Brand.gradient, borderRadius: BorderRadius.circular(14)),
                  child: const Icon(Icons.desktop_windows_rounded, color: Colors.white),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(host.name, style: t.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w600)),
                    Row(children: [
                      AnimatedContainer(duration: Motion.medium, width: 8, height: 8, decoration: BoxDecoration(shape: BoxShape.circle, color: stateColor(context, link.state))),
                      const SizedBox(width: 6),
                      Flexible(child: Text('${stateLabel(link.state)}${link.transport.isNotEmpty && link.isConnected ? ' · ${link.transport}' : ''}',
                          style: t.textTheme.bodySmall, overflow: TextOverflow.ellipsis)),
                    ]),
                  ]),
                ),
                IconButton(
                  icon: const Icon(Icons.more_vert),
                  onPressed: () => Navigator.push(context, MaterialPageRoute(builder: (_) => HostDetailsScreen(host: host))),
                ),
              ]),
              if (link.error != null && !link.isConnected) ...[
                const SizedBox(height: 8),
                Text(link.error!, style: t.textTheme.bodySmall?.copyWith(color: t.colorScheme.error)),
              ],
              if (host.lastConnected != null) ...[
                const SizedBox(height: 6),
                Text('Last session: ${DateTime.fromMillisecondsSinceEpoch(host.lastConnected!)}'.split('.').first, style: t.textTheme.bodySmall),
              ],
              const SizedBox(height: 12),
              Wrap(spacing: 8, runSpacing: 8, children: [
                if (busy) const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)),
                if (link.state == LinkState.reconnecting || link.state == LinkState.failed)
                  OutlinedButton.icon(onPressed: link.retryNow, icon: const Icon(Icons.refresh), label: Text(tr('retry_now'))),
                FilledButton.icon(
                  onPressed: () async {
                    if (!link.isConnected) link.connect();
                    if (context.mounted) Navigator.push(context, MaterialPageRoute(builder: (_) => RemoteScreen(host: host), fullscreenDialog: true));
                  },
                  icon: const Icon(Icons.screen_share_outlined),
                  label: Text(tr('connect')),
                ),
                FilledButton.tonalIcon(
                  onPressed: () {
                    if (!link.isConnected) link.connect();
                    Navigator.push(context, MaterialPageRoute(builder: (_) => ChatScreen(host: host)));
                  },
                  icon: const Icon(Icons.auto_awesome),
                  label: Text(tr('chat')),
                ),
                FilledButton.tonalIcon(
                  onPressed: () {
                    if (!link.isConnected) link.connect();
                    Navigator.push(context, MaterialPageRoute(builder: (_) => FileBrowserScreen(host: host)));
                  },
                  icon: const Icon(Icons.folder_open),
                  label: Text(tr('files')),
                ),
                if (host.can('ai_agent'))
                  FilledButton.tonalIcon(
                    onPressed: () {
                      if (!link.isConnected) link.connect();
                      Navigator.push(context, MaterialPageRoute(builder: (_) => AgentTasksScreen(host: host)));
                    },
                    icon: const Icon(Icons.smart_toy_outlined),
                    label: Text(tr('tasks')),
                  ),
              ]),
            ]),
          ),
        );
      },
    );
  }
}

class HostDetailsScreen extends StatelessWidget {
  final PairedHost host;
  const HostDetailsScreen({super.key, required this.host});

  @override
  Widget build(BuildContext context) {
    final link = AppState.instance.link(host);
    return Scaffold(
      appBar: AppBar(title: Text(host.name)),
      body: ListView(children: [
        ListTile(title: const Text('Device ID'), subtitle: SelectableText(host.id)),
        ListTile(title: Text(tr('permissions')), subtitle: Text(host.permissions.isEmpty ? '—' : host.permissions.join(', '))),
        ListTile(title: const Text('LAN'), subtitle: Text(host.lanEndpoints.isEmpty ? '—' : host.lanEndpoints.join(', '))),
        ListTile(title: const Text('Internet'), subtitle: Text(host.signalingUrl ?? '—')),
        ListTile(leading: const Icon(Icons.monitor_heart_outlined), title: Text(tr('diagnostics')),
            onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => DiagnosticsScreen(link: link)))),
        if (link.isConnected)
          ListTile(leading: const Icon(Icons.link_off), title: Text(tr('disconnect')), onTap: link.disconnect),
        ListTile(
          leading: Icon(Icons.delete_outline, color: Theme.of(context).colorScheme.error),
          title: Text(tr('remove_pc'), style: TextStyle(color: Theme.of(context).colorScheme.error)),
          onTap: () async {
            final ok = await showDialog<bool>(context: context, builder: (c) => AlertDialog(
                  title: Text(tr('remove_pc')),
                  content: Text(host.name),
                  actions: [TextButton(onPressed: () => Navigator.pop(c, false), child: Text(tr('cancel'))), FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(tr('delete')))],
                ));
            if (ok == true) {
              await AppState.instance.removeHost(host);
              if (context.mounted) Navigator.pop(context);
            }
          },
        ),
      ]),
    );
  }
}

/// Developer diagnostics: all latency components we can measure, labelled with how they are measured.
class DiagnosticsScreen extends StatelessWidget {
  final HostLink link;
  const DiagnosticsScreen({super.key, required this.link});

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: Text(tr('diagnostics'))),
        body: ListenableBuilder(
          listenable: link,
          builder: (context, _) {
            final s = link.stats;
            Widget row(String k, String v, [String? how]) => ListTile(dense: true, title: Text(k), subtitle: how == null ? null : Text(how), trailing: Text(v, style: const TextStyle(fontWeight: FontWeight.w600)));
            return ListView(children: [
              row('State', link.state.name),
              row('Transport (signaling)', link.transport.isEmpty ? '—' : link.transport),
              row('Media path', s.path, 'from the selected ICE candidate pair'),
              row('Codec', s.codec.isEmpty ? '—' : s.codec),
              row('Resolution', s.width == 0 ? '—' : '${s.width}×${s.height}'),
              row('FPS (decoded)', s.fps.toStringAsFixed(0)),
              row('Bitrate', '${s.kbps.toStringAsFixed(0)} kbps'),
              row('Network RTT', '${s.rttMs.toStringAsFixed(0)} ms', 'ICE candidate-pair currentRoundTripTime'),
              row('Network one-way (estimate)', '${(s.rttMs / 2).toStringAsFixed(0)} ms', 'RTT/2 — clocks are not synchronised'),
              row('Jitter', '${s.jitterMs.toStringAsFixed(1)} ms'),
              row('Jitter buffer', '${s.jitterBufferMs.toStringAsFixed(0)} ms', 'jitterBufferDelay / emitted frames'),
              row('Decode time / frame', '${s.decodeMs.toStringAsFixed(1)} ms', 'totalDecodeTime / framesDecoded'),
              row('Packets lost', '${s.packetsLost}'),
              row('Input round trip', link.inputRttMs == null ? '—' : '${link.inputRttMs!.toStringAsFixed(0)} ms', 'ctl ping over the data channel'),
              const Padding(
                padding: EdgeInsets.all(16),
                child: Text('Capture, colour conversion and encode times are measured on the PC and shown in its Sessions page. '
                    'Render time on the phone is not exposed by WebRTC and is not estimated here.'),
              ),
            ]);
          },
        ),
      );
}
