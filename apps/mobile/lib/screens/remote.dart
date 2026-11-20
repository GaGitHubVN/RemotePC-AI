import 'dart:convert';
import 'dart:math';
import 'dart:ui' show ImageFilter;

import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../app_state.dart';
import '../core/host_link.dart';
import '../core/storage.dart';
import '../l10n.dart';
import '../ui/adaptive.dart';
import '../ui/glass.dart';
import '../ui/platform_ui.dart';
import '../ui/theme.dart';
import 'chat.dart';
import 'home.dart';

/// Full-screen remote desktop. Video first; controls float above it.
class RemoteScreen extends StatefulWidget {
  final PairedHost host;
  const RemoteScreen({super.key, required this.host});
  @override
  State<RemoteScreen> createState() => _RemoteScreenState();
}

class _RemoteScreenState extends State<RemoteScreen> {
  late final HostLink link = AppState.instance.link(widget.host);
  final _renderer = RTCVideoRenderer();
  bool _rendererReady = false;
  bool _toolbar = true;
  bool _keyboard = false;
  bool _showCursor = true;
  late bool _trackpad = AppState.instance.trackpadMode;

  // Pointer (normalized within the remote display) for trackpad mode.
  Offset _pointer = const Offset(0.5, 0.5);
  // Viewport zoom/pan.
  double _zoom = 1;
  Offset _pan = Offset.zero;
  double _zoomStart = 1;
  Offset _panStart = Offset.zero;
  Offset _focalStart = Offset.zero;
  int _pointers = 0;
  bool _dragging = false;
  Offset _lastFocal = Offset.zero;
  Size _viewSize = Size.zero;

  final _modifiers = <int>{};
  final _textCtl = TextEditingController(text: ' ');
  final _textFocus = FocusNode();
  final _hwFocus = FocusNode();

  @override
  void initState() {
    super.initState();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);
    _renderer.initialize().then((_) {
      setState(() => _rendererReady = true);
      _bindStream();
    });
    link.addListener(_bindStream);
    if (!link.isConnected && link.state != LinkState.negotiating && link.state != LinkState.openingSession) link.connect();
  }

  void _bindStream() {
    if (_rendererReady && _renderer.srcObject != link.remoteStream) {
      _renderer.srcObject = link.remoteStream;
      if (mounted) setState(() {});
    }
  }

  @override
  void dispose() {
    link.removeListener(_bindStream);
    _renderer.srcObject = null;
    _renderer.dispose();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    super.dispose();
  }

  // ---------- coordinate mapping ----------

  /// The rectangle the remote frame occupies (object-fit: contain), before zoom/pan.
  Rect _contentRect() {
    final vw = _renderer.videoWidth.toDouble(), vh = _renderer.videoHeight.toDouble();
    if (vw <= 0 || vh <= 0 || _viewSize.isEmpty) return Offset.zero & _viewSize;
    final s = min(_viewSize.width / vw, _viewSize.height / vh);
    final w = vw * s, h = vh * s;
    return Rect.fromLTWH((_viewSize.width - w) / 2, (_viewSize.height - h) / 2, w, h);
  }

  /// Screen point → normalized remote coordinate (removes letterbox and viewport zoom). Null if outside.
  Offset? _toRemote(Offset p) {
    final r = _contentRect();
    final q = (p - _pan) / _zoom;
    final n = Offset((q.dx - r.left) / r.width, (q.dy - r.top) / r.height);
    if (n.dx < -0.02 || n.dy < -0.02 || n.dx > 1.02 || n.dy > 1.02) return null;
    return Offset(n.dx.clamp(0, 1), n.dy.clamp(0, 1));
  }

  Offset _fromRemote(Offset n) {
    final r = _contentRect();
    return Offset(r.left + n.dx * r.width, r.top + n.dy * r.height) * _zoom + _pan;
  }

  bool get _canControl => widget.host.can('control_input');

  void _click(String button, Offset n, {bool double = false}) {
    if (!_canControl) return _toast(tr('not_allowed'));
    HapticFeedback.selectionClick();
    for (var i = 0; i < (double ? 2 : 1); i++) {
      link.sendInput({'k': 'btn', 'b': button, 'd': true, 'x': n.dx, 'y': n.dy});
      link.sendInput({'k': 'btn', 'b': button, 'd': false, 'x': n.dx, 'y': n.dy});
    }
  }

  // ---------- gestures ----------

  void _onScaleStart(ScaleStartDetails d) {
    _pointers = d.pointerCount;
    _zoomStart = _zoom;
    _panStart = _pan;
    _focalStart = d.localFocalPoint;
    _lastFocal = d.localFocalPoint;
  }

  void _onScaleUpdate(ScaleUpdateDetails d) {
    _pointers = max(_pointers, d.pointerCount);
    final delta = d.localFocalPoint - _lastFocal;
    _lastFocal = d.localFocalPoint;
    if (d.pointerCount >= 2) {
      if ((d.scale - 1).abs() > 0.06 || _zoom != _zoomStart) {
        // pinch → zoom the viewport around the focal point
        final z = (_zoomStart * d.scale).clamp(1.0, 5.0);
        setState(() {
          _zoom = z;
          _pan = d.localFocalPoint - (_focalStart - _panStart) * (z / _zoomStart);
          _clampPan();
        });
      } else if (_canControl) {
        // two-finger drag → scroll (vertical and horizontal)
        link.sendInput({'k': 'wheel', 'dx': (-delta.dx * 4).round(), 'dy': (delta.dy * 4).round()});
      }
      return;
    }
    if (!_canControl) {
      setState(() { _pan += delta; _clampPan(); });
      return;
    }
    if (_trackpad) {
      final r = _contentRect();
      final s = AppState.instance.pointerSensitivity;
      _pointer = Offset((_pointer.dx + delta.dx * s / (r.width * _zoom)).clamp(0, 1), (_pointer.dy + delta.dy * s / (r.height * _zoom)).clamp(0, 1));
      link.sendMove(_pointer.dx, _pointer.dy);
      _followPointer();
      setState(() {});
    } else {
      final n = _toRemote(d.localFocalPoint);
      if (n == null) return;
      if (!_dragging) {
        _dragging = true;
        final start = _toRemote(_focalStart) ?? n;
        link.sendInput({'k': 'btn', 'b': 'left', 'd': true, 'x': start.dx, 'y': start.dy});
      }
      link.sendMove(n.dx, n.dy);
    }
  }

  void _onScaleEnd(ScaleEndDetails d) {
    if (_dragging) {
      final n = _trackpad ? _pointer : (_toRemote(_lastFocal) ?? _pointer);
      link.sendInput({'k': 'btn', 'b': 'left', 'd': false, 'x': n.dx, 'y': n.dy});
      _dragging = false;
    }
    _pointers = 0;
  }

  void _clampPan() {
    final maxX = 0.0, minX = _viewSize.width - _viewSize.width * _zoom;
    final maxY = 0.0, minY = _viewSize.height - _viewSize.height * _zoom;
    _pan = Offset(_pan.dx.clamp(minX, maxX), _pan.dy.clamp(minY, maxY));
  }

  /// Keep the trackpad pointer visible while zoomed.
  void _followPointer() {
    if (_zoom <= 1) return;
    final p = _fromRemote(_pointer);
    const m = 40.0;
    var pan = _pan;
    if (p.dx < m) pan += Offset(m - p.dx, 0);
    if (p.dx > _viewSize.width - m) pan -= Offset(p.dx - (_viewSize.width - m), 0);
    if (p.dy < m) pan += Offset(0, m - p.dy);
    if (p.dy > _viewSize.height - m) pan -= Offset(0, p.dy - (_viewSize.height - m));
    _pan = pan;
    _clampPan();
  }

  // ---------- keyboard ----------

  static const _vk = {'esc': 0x1B, 'tab': 0x09, 'enter': 0x0D, 'bksp': 0x08, 'del': 0x2E, 'up': 0x26, 'down': 0x28, 'left': 0x25, 'right': 0x27,
    'home': 0x24, 'end': 0x23, 'pgup': 0x21, 'pgdn': 0x22, 'ctrl': 0x11, 'alt': 0x12, 'shift': 0x10, 'win': 0x5B};

  void _key(int vk) {
    if (!_canControl) return _toast(tr('not_allowed'));
    HapticFeedback.lightImpact();
    if (_modifiers.isNotEmpty) {
      link.sendInput({'k': 'combo', 'vks': [..._modifiers, vk]});
      setState(_modifiers.clear);
    } else {
      link.sendInput({'k': 'key', 'vk': vk, 'd': true});
      link.sendInput({'k': 'key', 'vk': vk, 'd': false});
    }
  }

  void _toggleModifier(int vk) => setState(() => _modifiers.contains(vk) ? _modifiers.remove(vk) : _modifiers.add(vk));

  /// Text field diffing: committed Unicode text is sent as text (works for Vietnamese IME, emoji, any layout);
  /// deletions become Backspace. Composing (IME) text is not sent until committed.
  void _onTextChanged(String v) {
    final value = _textCtl.value;
    if (!value.composing.isCollapsed) return;
    if (v.isEmpty) {
      _key(_vk['bksp']!);
    } else if (v.length > 1) {
      final typed = v.substring(1);
      if (_modifiers.isNotEmpty && typed.length == 1 && RegExp(r'[a-zA-Z0-9]').hasMatch(typed)) {
        _key(typed.toUpperCase().codeUnitAt(0));
      } else if (_canControl) {
        link.sendInput({'k': 'text', 's': typed});
      }
    }
    _textCtl.value = const TextEditingValue(text: ' ', selection: TextSelection.collapsed(offset: 1));
  }

  /// Hardware (Bluetooth) keyboard: physical keys → Windows virtual keys; Windows applies its own layout.
  KeyEventResult _onHardwareKey(FocusNode n, KeyEvent e) {
    if (_keyboard || !_canControl) return KeyEventResult.ignored;
    final vk = _physicalToVk(e.physicalKey);
    if (vk == null) return KeyEventResult.ignored;
    if (e is KeyDownEvent || e is KeyRepeatEvent) {
      link.sendInput({'k': 'key', 'vk': vk, 'd': true});
    } else if (e is KeyUpEvent) {
      link.sendInput({'k': 'key', 'vk': vk, 'd': false});
    }
    return KeyEventResult.handled;
  }

  static int? _physicalToVk(PhysicalKeyboardKey k) {
    final u = k.usbHidUsage & 0xFFFF;
    if (u >= 0x04 && u <= 0x1D) return 0x41 + (u - 0x04); // A-Z
    if (u >= 0x1E && u <= 0x26) return 0x31 + (u - 0x1E); // 1-9
    if (u == 0x27) return 0x30; // 0
    if (u >= 0x3A && u <= 0x45) return 0x70 + (u - 0x3A); // F1-F12
    return const {
      0x28: 0x0D, 0x29: 0x1B, 0x2A: 0x08, 0x2B: 0x09, 0x2C: 0x20, 0x2D: 0xBD, 0x2E: 0xBB, 0x2F: 0xDB, 0x30: 0xDD, 0x31: 0xDC,
      0x33: 0xBA, 0x34: 0xDE, 0x35: 0xC0, 0x36: 0xBC, 0x37: 0xBE, 0x38: 0xBF, 0x39: 0x14, 0x49: 0x2D, 0x4A: 0x24, 0x4B: 0x21,
      0x4C: 0x2E, 0x4D: 0x23, 0x4E: 0x22, 0x4F: 0x27, 0x50: 0x25, 0x51: 0x28, 0x52: 0x26,
      0xE0: 0x11, 0xE1: 0x10, 0xE2: 0x12, 0xE3: 0x5B, 0xE4: 0xA3, 0xE5: 0xA1, 0xE6: 0xA5, 0xE7: 0x5C,
    }[u];
  }

  // ---------- actions ----------

  void _toast(String m) => showToast(context, m);

  Future<void> _action(String id) async {
    switch (id) {
      case 'keyboard':
        setState(() => _keyboard = !_keyboard);
        _keyboard ? _textFocus.requestFocus() : _textFocus.unfocus();
      case 'mode':
        setState(() => _trackpad = !_trackpad);
        AppState.instance.trackpadMode = _trackpad;
        AppState.instance.savePrefs();
        _toast(_trackpad ? tr('trackpad') : tr('touch'));
      case 'display':
        _pickDisplay();
      case 'screenshot':
        try {
          final r = await link.call('screen.capture', {'display': link.currentDisplay});
          if (!mounted) return;
          await showPanel(context, title: tr('screenshot'), children: (c) => [
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 16),
                  child: ClipRRect(borderRadius: BorderRadius.circular(12), child: InteractiveViewer(child: Image.memory(base64Decode(r['png'])))),
                ),
              ]);
        } on RpcError catch (e) {
          _toast(e.message);
        }
      case 'quality':
        _pickQuality();
      case 'cursor':
        setState(() => _showCursor = !_showCursor);
      case 'ai':
        pushRoot(context, ChatScreen(host: widget.host));
      case 'clipboard':
        _clipboard();
      case 'stats':
        pushRoot(context, DiagnosticsScreen(link: link));
      case 'disconnect':
        await link.disconnect();
        if (mounted) Navigator.of(context).pop();
    }
  }

  Future<void> _pickDisplay() async {
    final d = link.displays;
    final v = await showActions(context, title: tr('display'), actions: [
      for (final m in d)
        DialogAction('${m['index']}', '${tr('display')} ${m['index'] + 1} · ${m['w']}×${m['h']}${m['index'] == link.currentDisplay ? '  ✓' : ''}'),
      DialogAction('cancel', tr('cancel'), cancel: true),
    ]);
    final i = int.tryParse(v ?? '');
    if (i == null) return;
    try {
      await link.call('display.select', {'index': i});
      link.currentDisplay = i;
      setState(() {
        _zoom = 1;
        _pan = Offset.zero;
      });
    } on RpcError catch (e) {
      _toast(e.message);
    }
  }

  Future<void> _pickQuality() async {
    final p = await showActions(context, title: tr('quality'), actions: const [
      DialogAction('Auto', 'Auto'),
      DialogAction('DataSaver', 'Data saver'),
      DialogAction('Balanced', 'Balanced'),
      DialogAction('HighQuality', 'High quality'),
      DialogAction('LowLatency', 'Low latency'),
      DialogAction('Custom', '1080p · 60 fps · 12 Mbps'),
      DialogAction('cancel', 'Cancel', cancel: true),
    ]);
    if (p == null) return;
    await link
        .call('stream.set', {'preset': p, if (p == 'Custom') ...{'maxWidth': 1920, 'maxHeight': 1080, 'fps': 60, 'targetKbps': 8000, 'maxKbps': 12000}})
        .catchError((Object e) => <String, dynamic>{});
  }

  Future<void> _clipboard() async {
    if (!widget.host.can('clipboard')) return _toast(tr('not_allowed'));
    final choice = await showActions(context, title: tr('clipboard'), actions: [
      DialogAction('send', tr('send_clipboard')),
      DialogAction('get', tr('get_clipboard')),
      DialogAction('cancel', tr('cancel'), cancel: true),
    ]);
    try {
      if (choice == 'send') {
        final data = await Clipboard.getData('text/plain');
        if (data?.text != null) await link.call('clipboard.set', {'text': data!.text});
        _toast('✓');
      } else if (choice == 'get') {
        final r = await link.call('clipboard.get');
        if (r['text'] != null) await Clipboard.setData(ClipboardData(text: r['text']));
        _toast('✓');
      }
    } on RpcError catch (e) {
      _toast(e.message);
    }
  }

  List<_ToolItem> get _tools => [
        _ToolItem('keyboard', Icons.keyboard, CupertinoIcons.keyboard, 'keyboard', tr('keyboard'), active: _keyboard),
        _ToolItem('mode', _trackpad ? Icons.touch_app : Icons.pan_tool_alt, _trackpad ? CupertinoIcons.hand_point_left : CupertinoIcons.hand_draw,
            _trackpad ? 'hand.point.up.left' : 'hand.tap', _trackpad ? tr('trackpad') : tr('touch')),
        _ToolItem('display', Icons.monitor, CupertinoIcons.tv, 'display.2', tr('display')),
        _ToolItem('screenshot', Icons.photo_camera, CupertinoIcons.camera, 'camera', tr('screenshot')),
        _ToolItem('quality', Icons.hd, CupertinoIcons.slider_horizontal_3, 'dial.medium', tr('quality')),
        _ToolItem('cursor', Icons.mouse, CupertinoIcons.cursor_rays, 'cursorarrow', tr('cursor'), active: _showCursor),
        _ToolItem('clipboard', Icons.content_paste, CupertinoIcons.doc_on_clipboard, 'doc.on.clipboard', tr('clipboard')),
        _ToolItem('ai', Icons.auto_awesome, CupertinoIcons.sparkles, 'sparkles', 'AI'),
        _ToolItem('stats', Icons.speed, CupertinoIcons.speedometer, 'speedometer', tr('diagnostics')),
        _ToolItem('disconnect', Icons.close, CupertinoIcons.xmark, 'xmark', tr('disconnect'), destructive: true),
      ];

  Widget _buildToolbar(BuildContext context) {
    final tools = _tools;
    switch (PlatformUi.style) {
      case UiStyle.glass:
        return GlassToolbar(
          onAction: _action,
          actions: [for (final t in tools) GlassAction(t.id, t.sf, t.label, active: t.active, destructive: t.destructive)],
        );
      case UiStyle.cupertino:
        // iOS 18: dark HUD-style bar (system thick material look), Cupertino icons.
        return ClipRRect(
          borderRadius: BorderRadius.circular(18),
          child: BackdropFilter(
            filter: ImageFilter.blur(sigmaX: 24, sigmaY: 24),
            child: Container(
              height: 58,
              color: const Color(0xB31C1C1E),
              child: ListView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.symmetric(horizontal: 6), children: [
                for (final t in tools)
                  CupertinoButton(
                    padding: const EdgeInsets.symmetric(horizontal: 11),
                    onPressed: () => _action(t.id),
                    child: Icon(t.cupertino, size: 24, semanticLabel: t.label,
                        color: t.destructive ? CupertinoColors.systemRed : (t.active ? CupertinoColors.activeBlue : CupertinoColors.white)),
                  ),
              ]),
            ),
          ),
        );
      case UiStyle.material:
        final s = Theme.of(context).colorScheme;
        return Material(
          color: s.surfaceContainerHigh,
          elevation: 6,
          shape: const StadiumBorder(),
          child: SizedBox(
            height: 60,
            child: ListView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.symmetric(horizontal: 8), children: [
              for (final t in tools)
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 6, horizontal: 2),
                  child: t.active
                      ? IconButton.filledTonal(tooltip: t.label, onPressed: () => _action(t.id), icon: Icon(t.material))
                      : IconButton(tooltip: t.label, onPressed: () => _action(t.id), icon: Icon(t.material, color: t.destructive ? s.error : null)),
                ),
            ]),
          ),
        );
    }
  }

  Widget _statusCard(BuildContext context) {
    final p = Palette.of(context);
    final ios = PlatformUi.isIOS;
    return Center(
      child: Container(
        width: 300,
        padding: const EdgeInsets.all(20),
        decoration: BoxDecoration(
          color: ios ? CupertinoColors.secondarySystemGroupedBackground.resolveFrom(context) : Theme.of(context).colorScheme.surfaceContainerHigh,
          borderRadius: BorderRadius.circular(PlatformUi.isGlass ? 28 : (ios ? 14 : 28)),
        ),
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          if (link.state != LinkState.failed) const Spinner(size: 28) else Icon(Ic.wifiOff.data, color: p.danger, size: 36),
          const SizedBox(height: 12),
          Text(stateLabel(link.state), style: TextStyle(fontSize: 17, fontWeight: FontWeight.w600, color: p.label)),
          if (link.error != null) Padding(padding: const EdgeInsets.only(top: 6), child: Text(link.error!, textAlign: TextAlign.center, style: TextStyle(fontSize: 14, color: p.secondary))),
          const SizedBox(height: 16),
          PrimaryButton(id: 'remote-retry', label: tr('retry_now'), icon: Ic.refresh, onPressed: link.retryNow),
          const SizedBox(height: 8),
          LinkButton(label: tr('cancel'), onPressed: () => Navigator.of(context).pop()),
        ]),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.black,
      body: Focus(
        focusNode: _hwFocus,
        autofocus: true,
        onKeyEvent: _onHardwareKey,
        child: ListenableBuilder(
          listenable: link,
          builder: (context, _) => Stack(children: [
            // ---------------- video + gestures ----------------
            Positioned.fill(
              child: LayoutBuilder(builder: (context, c) {
                _viewSize = c.biggest;
                return GestureDetector(
                  behavior: HitTestBehavior.opaque,
                  onTapUp: (d) {
                    if (_trackpad) {
                      _click('left', _pointer);
                    } else if (_toRemote(d.localPosition) case final n?) {
                      link.sendMove(n.dx, n.dy);
                      _click('left', n);
                    }
                  },
                  onDoubleTapDown: (d) {
                    final n = _trackpad ? _pointer : _toRemote(d.localPosition);
                    if (n != null) _click('left', n, double: true);
                  },
                  onDoubleTap: () {},
                  onLongPressStart: (d) {
                    final n = _trackpad ? _pointer : _toRemote(d.localPosition);
                    if (n != null) _click('right', n);
                  },
                  onScaleStart: _onScaleStart,
                  onScaleUpdate: _onScaleUpdate,
                  onScaleEnd: _onScaleEnd,
                  child: ClipRect(
                    child: Transform(
                      transform: Matrix4.identity()
                        ..translateByDouble(_pan.dx, _pan.dy, 0, 1)
                        ..scaleByDouble(_zoom, _zoom, 1, 1),
                      child: _rendererReady ? RTCVideoView(_renderer, objectFit: RTCVideoViewObjectFit.RTCVideoViewObjectFitContain) : const SizedBox(),
                    ),
                  ),
                );
              }),
            ),
            // trackpad pointer
            if (_trackpad && _showCursor && link.isConnected)
              Positioned(
                left: _fromRemote(_pointer).dx - 2,
                top: _fromRemote(_pointer).dy - 2,
                child: IgnorePointer(child: Icon(PlatformUi.isIOS ? CupertinoIcons.location_north_fill : Icons.navigation, size: 22, color: Colors.white, shadows: const [Shadow(blurRadius: 4)])),
              ),
            // ---------------- status overlay ----------------
            if (!link.isConnected) _statusCard(context),
            // ---------------- toolbar ----------------
            Positioned(
              left: 12, right: 12, bottom: 12 + MediaQuery.viewInsetsOf(context).bottom,
              child: SafeArea(
                child: Column(mainAxisSize: MainAxisSize.min, children: [
                  if (_keyboard)
                    _KeyRow(modifiers: _modifiers, onKey: (k) => _key(_vk[k] ?? int.parse(k)), onModifier: (k) => _toggleModifier(_vk[k]!), onCombo: (vks) => link.sendInput({'k': 'combo', 'vks': vks})),
                  AnimatedSlide(
                    duration: Motion.medium,
                    curve: Motion.standard,
                    offset: _toolbar ? Offset.zero : const Offset(0, 1.6),
                    child: _buildToolbar(context),
                  ),
                ]),
              ),
            ),
            // stats + toolbar handle
            Positioned(
              right: 8, top: MediaQuery.paddingOf(context).top + 8,
              child: Row(children: [
                if (link.isConnected)
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                    decoration: BoxDecoration(color: Colors.black54, borderRadius: BorderRadius.circular(10)),
                    child: Text('${link.stats.fps.toStringAsFixed(0)} fps · ${link.stats.rttMs.toStringAsFixed(0)} ms · ${link.stats.path}',
                        style: const TextStyle(color: Colors.white70, fontSize: 11)),
                  ),
                const SizedBox(width: 6),
                if (PlatformUi.isGlass)
                  GlassBar(
                    actions: [GlassAction('toggle', _toolbar ? 'chevron.down' : 'chevron.up', 'Toolbar')],
                    onAction: (_) => setState(() => _toolbar = !_toolbar),
                  )
                else if (PlatformUi.isIOS)
                  CupertinoButton(
                    padding: const EdgeInsets.all(8),
                    color: const Color(0xB31C1C1E),
                    borderRadius: BorderRadius.circular(20),
                    minimumSize: const Size(40, 40),
                    onPressed: () => setState(() => _toolbar = !_toolbar),
                    child: Icon(_toolbar ? CupertinoIcons.chevron_down : CupertinoIcons.chevron_up, color: CupertinoColors.white, size: 18),
                  )
                else
                  IconButton.filledTonal(icon: Icon(_toolbar ? Icons.expand_more : Icons.expand_less), onPressed: () => setState(() => _toolbar = !_toolbar)),
              ]),
            ),
            // hidden text input for the soft keyboard
            Positioned(
              left: -1000, top: 0, width: 10, height: 10,
              child: TextField(
                controller: _textCtl,
                focusNode: _textFocus,
                onChanged: _onTextChanged,
                autocorrect: false,
                enableSuggestions: false,
                keyboardType: TextInputType.text,
                onSubmitted: (_) {
                  _key(0x0D);
                  _textFocus.requestFocus();
                },
              ),
            ),
          ]),
        ),
      ),
    );
  }
}

class _ToolItem {
  final String id;
  final IconData material, cupertino;
  final String sf, label;
  final bool active, destructive;
  const _ToolItem(this.id, this.material, this.cupertino, this.sf, this.label, {this.active = false, this.destructive = false});
}

class _KeyRow extends StatelessWidget {
  final Set<int> modifiers;
  final void Function(String) onKey;
  final void Function(String) onModifier;
  final void Function(List<int>) onCombo;
  const _KeyRow({required this.modifiers, required this.onKey, required this.onModifier, required this.onCombo});

  @override
  Widget build(BuildContext context) {
    Widget k(String label, VoidCallback f, {bool on = false}) => Padding(
          padding: const EdgeInsets.all(3),
          child: Material(
            color: on ? Theme.of(context).colorScheme.primary : Colors.black.withValues(alpha: 0.6),
            borderRadius: BorderRadius.circular(8),
            child: InkWell(
              borderRadius: BorderRadius.circular(8),
              onTap: f,
              child: Padding(padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8), child: Text(label, style: const TextStyle(color: Colors.white, fontSize: 13))),
            ),
          ),
        );
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Row(children: [
          for (final m in ['ctrl', 'alt', 'shift', 'win']) k(m[0].toUpperCase() + m.substring(1), () => onModifier(m), on: modifiers.contains(_RemoteScreenState._vk[m])),
          for (final key in ['esc', 'tab', 'enter', 'bksp', 'del', 'up', 'down', 'left', 'right', 'home', 'end', 'pgup', 'pgdn']) k(key.toUpperCase(), () => onKey(key)),
          k('Ctrl+C', () => onCombo([0x11, 0x43])), k('Ctrl+V', () => onCombo([0x11, 0x56])), k('Ctrl+Z', () => onCombo([0x11, 0x5A])), k('Ctrl+A', () => onCombo([0x11, 0x41])),
          k('Alt+Tab', () => onCombo([0x12, 0x09])),
          for (var f = 1; f <= 12; f++) k('F$f', () => onKey('${0x70 + f - 1}')),
        ]),
      ),
    );
  }
}
