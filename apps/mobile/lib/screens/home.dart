import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';

import '../app_state.dart';
import '../core/host_link.dart';
import '../core/storage.dart';
import '../l10n.dart';
import '../ui/adaptive.dart';
import '../ui/platform_ui.dart';
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
    void add() => pushRoot(context, const PairScreen(), fullscreen: true);
    return ListenableBuilder(
      listenable: s,
      builder: (context, _) => AppPage(
        title: tr('home'),
        large: true,
        actions: [BarItem('add', Ic.add, tr('add_pc'), add)],
        slivers: [
          if (s.hosts.isEmpty)
            SliverFillRemaining(
              hasScrollBody: false,
              child: Center(
                child: EmptyState(
                  icon: Ic.pc,
                  title: tr('no_pcs'),
                  subtitle: tr('no_pcs_sub'),
                  action: SizedBox(width: 260, child: PrimaryButton(id: 'add-pc', label: tr('add_pc'), icon: Ic.qr, onPressed: add)),
                ),
              ),
            )
          else
            SliverList.list(children: [for (final h in s.hosts) HostCard(host: h)]),
        ],
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

Color stateColor(BuildContext c, LinkState st) {
  final p = Palette.of(c);
  return switch (st) {
    LinkState.connected => p.success,
    LinkState.failed => p.danger,
    LinkState.idle || LinkState.closed => p.secondary,
    _ => p.warning,
  };
}

bool isBusy(LinkState s) => s == LinkState.connectingTransport || s == LinkState.openingSession || s == LinkState.negotiating || s == LinkState.reconnecting;

void openRemote(BuildContext context, PairedHost host) {
  final link = AppState.instance.link(host);
  if (!link.isConnected && !isBusy(link.state)) link.connect();
  pushRoot(context, RemoteScreen(host: host), fullscreen: true);
}

void openChat(BuildContext context, PairedHost host, [Map<String, dynamic>? chat]) {
  final link = AppState.instance.link(host);
  if (!link.isConnected && !isBusy(link.state)) link.connect();
  pushRoot(context, ChatScreen(host: host, chat: chat));
}

void openFiles(BuildContext context, PairedHost host) {
  final link = AppState.instance.link(host);
  if (!link.isConnected && !isBusy(link.state)) link.connect();
  pushPage(context, FileBrowserScreen(host: host));
}

void openTasks(BuildContext context, PairedHost host) {
  final link = AppState.instance.link(host);
  if (!link.isConnected && !isBusy(link.state)) link.connect();
  pushPage(context, AgentTasksScreen(host: host));
}

class HostCard extends StatelessWidget {
  final PairedHost host;
  const HostCard({super.key, required this.host});

  @override
  Widget build(BuildContext context) {
    final link = AppState.instance.link(host);
    final p = Palette.of(context);
    final ios = PlatformUi.isIOS;
    final radius = PlatformUi.isGlass ? 26.0 : (ios ? 12.0 : 24.0);
    return ListenableBuilder(
      listenable: link,
      builder: (context, _) {
        final header = Padding(
          padding: const EdgeInsets.fromLTRB(16, 14, 8, 10),
          child: Row(children: [
            Container(
              width: 48, height: 48,
              decoration: BoxDecoration(gradient: Brand.gradient, borderRadius: BorderRadius.circular(ios ? 12 : 16)),
              child: Icon(Ic.pc.data, color: Colors.white, size: 26),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(host.name, style: TextStyle(fontSize: 17, fontWeight: FontWeight.w600, color: p.label), maxLines: 1, overflow: TextOverflow.ellipsis),
                const SizedBox(height: 3),
                Row(children: [
                  if (isBusy(link.state)) const Padding(padding: EdgeInsets.only(right: 6), child: Spinner(size: 12))
                  else Container(width: 8, height: 8, margin: const EdgeInsets.only(right: 6), decoration: BoxDecoration(shape: BoxShape.circle, color: stateColor(context, link.state))),
                  Flexible(
                    child: Text('${stateLabel(link.state)}${link.transport.isNotEmpty && link.isConnected ? ' · ${link.transport}' : ''}',
                        style: TextStyle(fontSize: 13, color: p.secondary), overflow: TextOverflow.ellipsis),
                  ),
                ]),
              ]),
            ),
            ios
                ? CupertinoButton(padding: const EdgeInsets.all(8), onPressed: () => pushPage(context, HostDetailsScreen(host: host)),
                    child: Icon(CupertinoIcons.info_circle, size: 24, semanticLabel: tr('details')))
                : IconButton(tooltip: tr('details'), icon: const Icon(Icons.more_vert), onPressed: () => pushPage(context, HostDetailsScreen(host: host))),
          ]),
        );
        final error = link.error != null && !link.isConnected
            ? Padding(
                padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
                child: Text(link.error!, style: TextStyle(fontSize: 13, color: p.danger)),
              )
            : const SizedBox.shrink();
        final primary = Padding(
          padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
          child: Row(children: [
            Expanded(child: PrimaryButton(id: 'connect-${host.id}', label: tr('open_remote'), icon: Ic.screen, onPressed: () => openRemote(context, host))),
            if (link.state == LinkState.failed || link.state == LinkState.reconnecting) ...[
              const SizedBox(width: 10),
              SizedBox(width: ios ? 110 : 130, child: PrimaryButton(id: 'retry-${host.id}', label: tr('retry_now'), secondary: true, onPressed: link.retryNow)),
            ],
          ]),
        );
        final tiles = <Widget>[
          Tile(title: tr('chat'), icon: Ic.ai, iconColor: const Color(0xFFAF52DE), onTap: () => openChat(context, host)),
          Tile(title: tr('files'), icon: Ic.folder, iconColor: const Color(0xFF0A84FF), onTap: () => openFiles(context, host)),
          if (host.can('ai_agent')) Tile(title: tr('tasks'), icon: Ic.agent, iconColor: const Color(0xFFFF9F0A), onTap: () => openTasks(context, host)),
        ];
        if (ios) {
          return Padding(
            padding: const EdgeInsets.fromLTRB(16, 6, 16, 10),
            child: Container(
              clipBehavior: Clip.antiAlias,
              decoration: BoxDecoration(color: p.card, borderRadius: BorderRadius.circular(radius)),
              child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                header,
                error,
                primary,
                Container(height: 0.5, margin: const EdgeInsets.only(left: 16), color: p.separator),
                for (var i = 0; i < tiles.length; i++) ...[
                  tiles[i],
                  if (i < tiles.length - 1) Container(height: 0.5, margin: const EdgeInsets.only(left: 58), color: p.separator),
                ],
              ]),
            ),
          );
        }
        return Card(
          margin: const EdgeInsets.fromLTRB(16, 6, 16, 10),
          child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            header,
            error,
            primary,
            Padding(
              padding: const EdgeInsets.fromLTRB(12, 0, 12, 14),
              child: Wrap(spacing: 8, runSpacing: 8, children: [
                FilledButton.tonalIcon(onPressed: () => openChat(context, host), icon: const Icon(Icons.auto_awesome), label: Text(tr('chat'))),
                FilledButton.tonalIcon(onPressed: () => openFiles(context, host), icon: const Icon(Icons.folder_open), label: Text(tr('files'))),
                if (host.can('ai_agent'))
                  FilledButton.tonalIcon(onPressed: () => openTasks(context, host), icon: const Icon(Icons.smart_toy_outlined), label: Text(tr('tasks'))),
              ]),
            ),
          ]),
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
    return ListenableBuilder(
      listenable: link,
      builder: (context, _) => AppPage(
        title: host.name,
        slivers: [
          SliverList.list(children: [
            Section(header: tr('pc_info'), children: [
              Tile(title: tr('remote_state'), value: stateLabel(link.state), chevron: false),
              Tile(title: tr('device_id'), subtitle: host.id, chevron: false),
              Tile(title: tr('permissions'), subtitle: host.permissions.isEmpty ? '—' : host.permissions.join(', '), subtitleLines: 4, chevron: false),
              Tile(title: 'LAN', subtitle: host.lanEndpoints.isEmpty ? '—' : host.lanEndpoints.join(', '), chevron: false),
              Tile(title: 'Internet', subtitle: host.signalingUrl ?? '—', chevron: false),
            ]),
            Section(children: [
              Tile(title: tr('diagnostics'), icon: Ic.diag, iconColor: const Color(0xFF34C759), onTap: () => pushPage(context, DiagnosticsScreen(link: link))),
              if (link.isConnected) Tile(title: tr('disconnect'), icon: Ic.unlink, iconColor: const Color(0xFFFF9F0A), onTap: link.disconnect),
            ]),
            Section(children: [
              Tile(
                title: tr('remove_pc'),
                icon: Ic.delete,
                destructive: true,
                onTap: () async {
                  final r = await showAlert(context, title: tr('remove_pc'), message: tr('remove_confirm'), actions: [
                    DialogAction('cancel', tr('cancel'), cancel: true),
                    DialogAction('delete', tr('delete'), destructive: true),
                  ]);
                  if (r == 'delete') {
                    await AppState.instance.removeHost(host);
                    if (context.mounted) Navigator.of(context).pop();
                  }
                },
              ),
            ]),
          ]),
        ],
      ),
    );
  }
}

/// Developer diagnostics: all latency components we can measure, labelled with how they are measured.
class DiagnosticsScreen extends StatelessWidget {
  final HostLink link;
  const DiagnosticsScreen({super.key, required this.link});

  @override
  Widget build(BuildContext context) => ListenableBuilder(
        listenable: link,
        builder: (context, _) {
          final s = link.stats;
          Widget row(String k, String v, [String? how]) => Tile(title: k, subtitle: how, value: v, chevron: false);
          return AppPage(title: tr('diagnostics'), slivers: [
            SliverList.list(children: [
              Section(header: tr('connection'), children: [
                row('State', link.state.name),
                row('Signaling', link.transport.isEmpty ? '—' : link.transport),
                row('Media path', s.path, 'selected ICE candidate pair'),
                row('Network RTT', '${s.rttMs.toStringAsFixed(0)} ms', 'candidate-pair currentRoundTripTime'),
                row('One-way (estimate)', '${(s.rttMs / 2).toStringAsFixed(0)} ms', 'RTT/2 — clocks not synchronised'),
                row('Input round trip', link.inputRttMs == null ? '—' : '${link.inputRttMs!.toStringAsFixed(0)} ms', 'ctl ping over the data channel'),
              ]),
              Section(header: 'Video', footer: 'Capture, colour conversion and encode times are measured on the PC (Sessions page). Phone render time is not exposed by WebRTC and is not estimated.', children: [
                row('Codec', s.codec.isEmpty ? '—' : s.codec),
                row('Resolution', s.width == 0 ? '—' : '${s.width}×${s.height}'),
                row('FPS (decoded)', s.fps.toStringAsFixed(0)),
                row('Bitrate', '${s.kbps.toStringAsFixed(0)} kbps'),
                row('Jitter', '${s.jitterMs.toStringAsFixed(1)} ms'),
                row('Jitter buffer', '${s.jitterBufferMs.toStringAsFixed(0)} ms'),
                row('Decode / frame', '${s.decodeMs.toStringAsFixed(1)} ms'),
                row('Packets lost', '${s.packetsLost}'),
              ]),
            ]),
          ]);
        },
      );
}
