import 'dart:io';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:share_plus/share_plus.dart';

import '../app_state.dart';
import '../core/host_link.dart';
import '../core/storage.dart';
import '../l10n.dart';
import '../ui/adaptive.dart';
import '../ui/platform_ui.dart';
import 'home.dart';

class FilesRootScreen extends StatelessWidget {
  const FilesRootScreen({super.key});
  @override
  Widget build(BuildContext context) => ListenableBuilder(
        listenable: AppState.instance,
        builder: (context, _) {
          final hosts = AppState.instance.hosts;
          return AppPage(title: tr('files'), large: true, slivers: [
            if (hosts.isEmpty)
              SliverFillRemaining(hasScrollBody: false, child: Center(child: EmptyState(icon: Ic.folder, title: tr('no_pcs'), subtitle: tr('no_pcs_sub'))))
            else
              SliverList.list(children: [
                Section(header: tr('paired_pcs'), children: [
                  for (final h in hosts) Tile(title: h.name, icon: Ic.pc, onTap: () => openFiles(context, h)),
                ]),
              ]),
          ]);
        },
      );
}

class Transfer {
  final String name;
  final bool upload;
  double progress = 0;
  String? error;
  bool done = false;
  Transfer(this.name, this.upload);
}

String fmtSize(dynamic b) {
  if (b == null) return '';
  final n = (b as num).toDouble();
  if (n < 1024) return '${n.toInt()} B';
  if (n < 1048576) return '${(n / 1024).toStringAsFixed(1)} KB';
  if (n < 1073741824) return '${(n / 1048576).toStringAsFixed(1)} MB';
  return '${(n / 1073741824).toStringAsFixed(2)} GB';
}

/// One folder on the PC (only the roots the PC owner allowed). Sub-folders are pushed as new pages, so back/swipe-back
/// behave natively.
class FileBrowserScreen extends StatefulWidget {
  final PairedHost host;
  final String path;
  const FileBrowserScreen({super.key, required this.host, this.path = ''});
  @override
  State<FileBrowserScreen> createState() => _FileBrowserScreenState();
}

class _FileBrowserScreenState extends State<FileBrowserScreen> {
  late final HostLink link = AppState.instance.link(widget.host);
  List<Map<String, dynamic>> _items = [];
  String _sort = 'name';
  bool _loading = false, _loaded = false;
  String? _error;
  String _query = '';
  final List<Transfer> _transfers = [];

  String get _path => widget.path;

  @override
  void initState() {
    super.initState();
    link.addListener(_onLink);
    _load();
  }

  void _onLink() {
    if (link.isConnected && !_loaded && !_loading) _load();
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    link.removeListener(_onLink);
    super.dispose();
  }

  Future<void> _load() async {
    if (!link.isConnected) return;
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      if (_path.isEmpty) {
        final r = await link.call('fs.roots');
        _items = List<Map<String, dynamic>>.from(r['_list']).map((x) => {'name': x['name'], 'type': 'folder'}).toList();
      } else if (_query.isNotEmpty) {
        final r = await link.call('fs.search', {'path': _path, 'query': _query});
        _items = List<Map<String, dynamic>>.from(r['_list']);
      } else {
        final r = await link.call('fs.list', {'path': _path});
        _items = List<Map<String, dynamic>>.from(r['items']);
      }
      _sortItems();
      _loaded = true;
    } on RpcError catch (e) {
      _error = e.code == 'forbidden' ? tr('not_allowed') : e.message;
    }
    if (mounted) setState(() => _loading = false);
  }

  void _sortItems() => _items.sort((a, b) {
        if (a['type'] != b['type']) return a['type'] == 'folder' ? -1 : 1;
        return switch (_sort) {
          'size' => ((b['size'] ?? 0) as num).compareTo((a['size'] ?? 0) as num),
          'date' => (b['modified'] ?? '').compareTo(a['modified'] ?? ''),
          _ => (a['name'] as String).toLowerCase().compareTo((b['name'] as String).toLowerCase()),
        };
      });

  String _child(String name) => _path.isEmpty ? name : '$_path/$name';

  void _open(Map<String, dynamic> item) {
    final path = item['path'] as String? ?? _child(item['name']);
    if (item['type'] == 'folder') {
      pushPage(context, FileBrowserScreen(host: widget.host, path: path));
    } else {
      _fileActions(item, path);
    }
  }

  Future<void> _fileActions(Map<String, dynamic> item, String path) async {
    final canModify = widget.host.can('modify_files');
    final v = await showActions(context, title: item['name'], message: '${fmtSize(item['size'])} · ${(item['modified'] as String? ?? '').split('T').first}', actions: [
      DialogAction('download', tr('download')),
      if (canModify) DialogAction('rename', tr('rename')),
      if (canModify) DialogAction('delete', tr('delete'), destructive: true),
      DialogAction('cancel', tr('cancel'), cancel: true),
    ]);
    switch (v) {
      case 'download':
        _download(path, item['name']);
      case 'rename':
        _rename(path, item['name']);
      case 'delete':
        _delete([path], item['name']);
    }
  }

  Future<void> _download(String path, String name) async {
    final t = Transfer(name, false);
    setState(() => _transfers.add(t));
    try {
      final f = await link.download(path, progress: (d, total) => setState(() => t.progress = total == 0 ? 1 : d / total));
      t.done = true;
      if (mounted) {
        final r = await showActions(context, title: name, message: tr('saved_verified'), actions: [
          DialogAction('share', tr('share')),
          DialogAction('ok', tr('ok'), cancel: true),
        ]);
        if (r == 'share') await SharePlus.instance.share(ShareParams(files: [XFile(f.path)]));
      }
    } catch (e) {
      t.error = '$e';
    }
    if (mounted) setState(() {});
  }

  Future<void> _upload() async {
    if (!widget.host.can('send_files')) return showToast(context, tr('not_allowed'));
    final picked = await FilePicker.pickFiles();
    if (picked.isEmpty) return;
    for (final pf in picked.where((f) => f.path != null)) {
      final t = Transfer(pf.name, true);
      setState(() => _transfers.add(t));
      try {
        await link.upload(File(pf.path!), folder: _path, progress: (d, total) => setState(() => t.progress = total == 0 ? 1 : d / total));
        t.done = true;
      } catch (e) {
        t.error = '$e';
      }
      if (mounted) setState(() {});
    }
    _load();
  }

  Future<void> _mkdir() async {
    final name = (await showPrompt(context, title: tr('new_folder'), ok: tr('ok'), cancel: tr('cancel')))?.trim();
    if (name == null || name.isEmpty) return;
    try {
      await link.call('fs.mkdir', {'path': _child(name)});
      _load();
    } on RpcError catch (e) {
      if (mounted) showToast(context, e.message);
    }
  }

  Future<void> _rename(String path, String old) async {
    final name = (await showPrompt(context, title: tr('rename'), text: old, ok: tr('ok'), cancel: tr('cancel')))?.trim();
    if (name == null || name.isEmpty || name == old) return;
    try {
      await link.call('fs.rename', {'path': path, 'name': name});
      _load();
    } on RpcError catch (e) {
      if (mounted) showToast(context, e.message);
    }
  }

  Future<void> _delete(List<String> paths, String label) async {
    final r = await showAlert(context, title: '${tr('delete')} “$label”?', message: tr('confirm_delete'), actions: [
      DialogAction('cancel', tr('cancel'), cancel: true),
      DialogAction('delete', tr('delete'), destructive: true),
    ]);
    if (r != 'delete') return;
    try {
      await link.call('fs.delete', {'paths': paths, 'confirmed': true});
      _load();
    } on RpcError catch (e) {
      if (mounted) showToast(context, e.message);
    }
  }

  Future<void> _pickSort() async {
    final v = await showActions(context, title: tr('sort'), actions: [
      DialogAction('name', tr('sort_name')),
      DialogAction('date', tr('sort_date')),
      DialogAction('size', tr('sort_size')),
      DialogAction('cancel', tr('cancel'), cancel: true),
    ]);
    if (v == null) return;
    setState(() {
      _sort = v;
      _sortItems();
    });
  }

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final title = _path.isEmpty ? widget.host.name : _path.split('/').last;
    return AppPage(
      title: title,
      onRefresh: _load,
      actions: [
        if (_path.isNotEmpty) BarItem('upload', Ic.upload, tr('upload'), _upload),
        if (_path.isNotEmpty) BarItem('mkdir', Ic.newFolder, tr('new_folder'), _mkdir),
        BarItem('sort', Ic.sort, tr('sort'), _pickSort),
      ],
      slivers: [
        if (_path.isNotEmpty)
          SliverToBoxAdapter(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 10),
              child: SearchField(placeholder: tr('search'), onSubmitted: (v) {
                _query = v.trim();
                _load();
              }),
            ),
          ),
        if (_loading || !link.isConnected) const SliverToBoxAdapter(child: Padding(padding: EdgeInsets.fromLTRB(16, 0, 16, 10), child: ProgressLine())),
        if (!link.isConnected && link.error != null)
          SliverToBoxAdapter(child: Padding(padding: const EdgeInsets.fromLTRB(20, 0, 20, 10), child: Text(link.error!, style: TextStyle(color: p.danger)))),
        if (_error != null) SliverToBoxAdapter(child: Padding(padding: const EdgeInsets.all(20), child: Text(_error!, style: TextStyle(color: p.danger)))),
        SliverList.list(children: [
          if (_transfers.isNotEmpty)
            Section(header: tr('transfers'), children: [
              for (final t in _transfers.reversed.take(4))
                Tile(
                  title: t.name,
                  icon: t.upload ? Ic.upload : Ic.download,
                  iconColor: t.error != null ? p.danger : (t.done ? p.success : null),
                  chevron: false,
                  subtitle: t.error ?? (t.done ? tr('saved_verified') : '${(t.progress * 100).toStringAsFixed(0)}%'),
                  trailing: t.done ? Icon(Ic.verified.data, color: p.success, size: 20) : (t.error == null ? SizedBox(width: 60, child: ProgressLine(value: t.progress)) : null),
                ),
            ]),
          if (_loaded && _items.isEmpty && _error == null) EmptyState(icon: Ic.folder, title: tr('folder_empty')),
          if (_items.isNotEmpty)
            Section(children: [
              for (final it in _items)
                Tile(
                  title: it['name'],
                  icon: it['type'] == 'folder' ? Ic.folder : Ic.file,
                  iconColor: it['type'] == 'folder' ? const Color(0xFF0A84FF) : const Color(0xFF8E8E93),
                  subtitle: it['type'] == 'file' ? '${fmtSize(it['size'])} · ${(it['modified'] as String? ?? '').split('T').first}' : it['path'] as String?,
                  chevron: it['type'] == 'folder',
                  onTap: () => _open(it),
                ),
            ]),
        ]),
      ],
    );
  }
}
