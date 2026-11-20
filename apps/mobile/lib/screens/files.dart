import 'dart:io';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:share_plus/share_plus.dart';

import '../app_state.dart';
import '../core/host_link.dart';
import '../core/storage.dart';
import '../l10n.dart';
import '../ui/theme.dart';

class FilesRootScreen extends StatelessWidget {
  const FilesRootScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final hosts = AppState.instance.hosts;
    return Scaffold(
      appBar: AppBar(title: Text(tr('files'))),
      body: hosts.isEmpty
          ? EmptyState(icon: Icons.folder_off_outlined, title: tr('no_pcs'))
          : ListView(children: [
              for (final h in hosts)
                ListTile(
                  leading: const Icon(Icons.computer),
                  title: Text(h.name),
                  trailing: const Icon(Icons.chevron_right),
                  onTap: () {
                    final l = AppState.instance.link(h);
                    if (!l.isConnected) l.connect();
                    Navigator.push(context, MaterialPageRoute(builder: (_) => FileBrowserScreen(host: h)));
                  },
                ),
            ]),
    );
  }
}

class Transfer {
  final String name;
  final bool upload;
  double progress = 0;
  String? error;
  bool done = false;
  Transfer(this.name, this.upload);
}

/// Browse only the folders the PC owner allowed (Desktop, Documents, Downloads, Received, …).
class FileBrowserScreen extends StatefulWidget {
  final PairedHost host;
  const FileBrowserScreen({super.key, required this.host});
  @override
  State<FileBrowserScreen> createState() => _FileBrowserScreenState();
}

class _FileBrowserScreenState extends State<FileBrowserScreen> {
  late final HostLink link = AppState.instance.link(widget.host);
  String _path = '';
  List<Map<String, dynamic>> _items = [];
  final Set<String> _selected = {};
  String _sort = 'name';
  bool _loading = false;
  String? _error;
  String _query = '';
  final List<Transfer> _transfers = [];

  @override
  void initState() {
    super.initState();
    link.addListener(_onLink);
    _load();
  }

  void _onLink() {
    if (link.isConnected && _items.isEmpty && !_loading) _load();
  }

  @override
  void dispose() {
    link.removeListener(_onLink);
    super.dispose();
  }

  Future<void> _load() async {
    if (!link.isConnected) return;
    setState(() { _loading = true; _error = null; });
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
    } on RpcError catch (e) {
      _error = e.code == 'forbidden' ? tr('not_allowed') : e.message;
    }
    if (mounted) setState(() { _loading = false; _selected.clear(); });
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
    if (item['type'] == 'folder') {
      setState(() { _path = item['path'] as String? ?? _child(item['name']); _query = ''; });
      _load();
    } else {
      _details(item);
    }
  }

  Future<void> _details(Map<String, dynamic> item) async {
    final path = item['path'] as String? ?? _child(item['name']);
    Map<String, dynamic>? stat;
    try {
      stat = await link.call('fs.stat', {'path': path});
    } on RpcError catch (_) {}
    if (!mounted) return;
    await showModalBottomSheet(context: context, showDragHandle: true, builder: (c) => Column(mainAxisSize: MainAxisSize.min, children: [
          ListTile(title: Text(item['name']), subtitle: Text(path)),
          if (stat != null) ListTile(dense: true, title: Text('${_size(stat['size'])} · ${stat['kind']}'), subtitle: Text('Modified ${stat['modified']}')),
          ListTile(leading: const Icon(Icons.download), title: Text(tr('download')), onTap: () { Navigator.pop(c); _download(path, item['name']); }),
          if (widget.host.can('modify_files')) ...[
            ListTile(leading: const Icon(Icons.drive_file_rename_outline), title: Text(tr('rename')), onTap: () { Navigator.pop(c); _rename(path, item['name']); }),
            ListTile(leading: const Icon(Icons.delete_outline), title: Text(tr('delete')), onTap: () { Navigator.pop(c); _delete([path]); }),
          ],
        ]));
  }

  static String _size(dynamic b) {
    if (b == null) return '';
    final n = (b as num).toDouble();
    if (n < 1024) return '${n.toInt()} B';
    if (n < 1048576) return '${(n / 1024).toStringAsFixed(1)} KB';
    if (n < 1073741824) return '${(n / 1048576).toStringAsFixed(1)} MB';
    return '${(n / 1073741824).toStringAsFixed(2)} GB';
  }

  Future<void> _download(String path, String name) async {
    final tr0 = Transfer(name, false);
    setState(() => _transfers.add(tr0));
    try {
      final f = await link.download(path, progress: (d, t) => setState(() => tr0.progress = t == 0 ? 1 : d / t));
      tr0.done = true;
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(
          content: Text('✓ $name (SHA-256 verified)'),
          action: SnackBarAction(label: 'Share', onPressed: () => SharePlus.instance.share(ShareParams(files: [XFile(f.path)]))),
        ));
      }
    } catch (e) {
      tr0.error = '$e';
    }
    if (mounted) setState(() {});
  }

  Future<void> _upload() async {
    if (!widget.host.can('send_files')) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(tr('not_allowed'))));
      return;
    }
    final picked = await FilePicker.pickFiles();
    if (picked.isEmpty) return;
    final folder = _path.isEmpty ? null : _path;
    for (final pf in picked.where((f) => f.path != null)) {
      final t = Transfer(pf.name, true);
      setState(() => _transfers.add(t));
      try {
        await link.upload(File(pf.path!), folder: folder, progress: (d, total) => setState(() => t.progress = total == 0 ? 1 : d / total));
        t.done = true;
      } catch (e) {
        t.error = '$e';
      }
      if (mounted) setState(() {});
    }
    _load();
  }

  Future<String?> _ask(String title, String initial) {
    final c = TextEditingController(text: initial);
    return showDialog<String>(context: context, builder: (d) => AlertDialog(
          title: Text(title),
          content: TextField(controller: c, autofocus: true),
          actions: [TextButton(onPressed: () => Navigator.pop(d), child: Text(tr('cancel'))), FilledButton(onPressed: () => Navigator.pop(d, c.text.trim()), child: const Text('OK'))],
        ));
  }

  Future<void> _mkdir() async {
    final name = await _ask(tr('new_folder'), '');
    if (name == null || name.isEmpty) return;
    try {
      await link.call('fs.mkdir', {'path': _child(name)});
      _load();
    } on RpcError catch (e) {
      _snack(e.message);
    }
  }

  Future<void> _rename(String path, String old) async {
    final name = await _ask(tr('rename'), old);
    if (name == null || name.isEmpty || name == old) return;
    try {
      await link.call('fs.rename', {'path': path, 'name': name});
      _load();
    } on RpcError catch (e) {
      _snack(e.message);
    }
  }

  Future<void> _delete(List<String> paths) async {
    final ok = await showDialog<bool>(context: context, builder: (d) => AlertDialog(
          title: Text('${tr('delete')} (${paths.length})'),
          content: Text(tr('confirm_delete')),
          actions: [TextButton(onPressed: () => Navigator.pop(d, false), child: Text(tr('cancel'))), FilledButton(onPressed: () => Navigator.pop(d, true), child: Text(tr('delete')))],
        ));
    if (ok != true) return;
    try {
      await link.call('fs.delete', {'paths': paths, 'confirmed': true});
      _load();
    } on RpcError catch (e) {
      _snack(e.message);
    }
  }

  void _snack(String m) => ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(m)));

  @override
  Widget build(BuildContext context) {
    final selecting = _selected.isNotEmpty;
    return PopScope(
      canPop: _path.isEmpty,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop && _path.isNotEmpty) {
          setState(() { final i = _path.lastIndexOf('/'); _path = i < 0 ? '' : _path.substring(0, i); _query = ''; });
          _load();
        }
      },
      child: Scaffold(
        appBar: AppBar(
          title: Text(selecting ? '${_selected.length} selected' : (_path.isEmpty ? widget.host.name : _path.split('/').last)),
          actions: [
            if (selecting) ...[
              IconButton(icon: const Icon(Icons.download), onPressed: () { for (final p in _selected.toList()) { _download(p, p.split('/').last); } setState(_selected.clear); }),
              if (widget.host.can('modify_files')) IconButton(icon: const Icon(Icons.delete_outline), onPressed: () => _delete(_selected.toList())),
            ] else ...[
              PopupMenuButton<String>(
                icon: const Icon(Icons.sort),
                onSelected: (v) => setState(() { _sort = v; _sortItems(); }),
                itemBuilder: (_) => const [PopupMenuItem(value: 'name', child: Text('Name')), PopupMenuItem(value: 'date', child: Text('Date')), PopupMenuItem(value: 'size', child: Text('Size'))],
              ),
              if (_path.isNotEmpty) IconButton(icon: const Icon(Icons.create_new_folder_outlined), onPressed: _mkdir),
            ],
          ],
        ),
        floatingActionButton: _path.isEmpty ? null : FloatingActionButton(onPressed: _upload, child: const Icon(Icons.upload)),
        body: Column(children: [
          if (_path.isNotEmpty)
            Padding(
              padding: const EdgeInsets.fromLTRB(12, 4, 12, 4),
              child: TextField(
                decoration: InputDecoration(prefixIcon: const Icon(Icons.search), hintText: tr('search'), isDense: true),
                onSubmitted: (v) { _query = v.trim(); _load(); },
              ),
            ),
          if (_loading || !link.isConnected) const LinearProgressIndicator(),
          if (_error != null) Padding(padding: const EdgeInsets.all(16), child: Text(_error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
          Expanded(
            child: RefreshIndicator(
              onRefresh: _load,
              child: ListView(children: [
                for (final it in _items)
                  ListTile(
                    leading: Icon(it['type'] == 'folder' ? Icons.folder : Icons.insert_drive_file_outlined, color: it['type'] == 'folder' ? Theme.of(context).colorScheme.primary : null),
                    title: Text(it['name'], maxLines: 1, overflow: TextOverflow.ellipsis),
                    subtitle: it['type'] == 'file' ? Text('${_size(it['size'])} · ${(it['modified'] as String? ?? '').split('T').first}') : (it['path'] != null ? Text(it['path']) : null),
                    selected: _selected.contains(it['path'] ?? _child(it['name'])),
                    trailing: selecting && it['type'] == 'file' ? Checkbox(value: _selected.contains(it['path'] ?? _child(it['name'])), onChanged: (_) => _toggle(it)) : null,
                    onTap: () => selecting && it['type'] == 'file' ? _toggle(it) : _open(it),
                    onLongPress: it['type'] == 'file' && _path.isNotEmpty ? () => _toggle(it) : null,
                  ),
              ]),
            ),
          ),
          if (_transfers.isNotEmpty)
            Material(
              elevation: 3,
              child: Column(mainAxisSize: MainAxisSize.min, children: [
                for (final t in _transfers.reversed.take(3))
                  ListTile(
                    dense: true,
                    leading: Icon(t.upload ? Icons.upload : Icons.download),
                    title: Text(t.name, maxLines: 1),
                    subtitle: t.error != null ? Text(t.error!, style: const TextStyle(color: Colors.red)) : LinearProgressIndicator(value: t.progress),
                    trailing: t.done ? const Icon(Icons.verified, color: Colors.green) : null,
                  ),
              ]),
            ),
        ]),
      ),
    );
  }

  void _toggle(Map<String, dynamic> it) {
    final p = it['path'] as String? ?? _child(it['name']);
    setState(() => _selected.contains(p) ? _selected.remove(p) : _selected.add(p));
  }
}
