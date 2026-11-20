import 'dart:io';
import 'dart:ui';

import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

/// Liquid Glass integration.
///
/// - iOS 26+: a native SwiftUI view using `.glassEffect()` / `GlassEffectContainer` is embedded with a
///   UiKitView (see ios/Runner/GlassToolbar.swift). Support is decided by native code at runtime
///   (`if #available(iOS 26.0, *)`), never by parsing version strings here.
/// - iOS 18–25 and Android: a regular native-looking bar (Cupertino-style translucency on iOS, Material 3
///   surface on Android). No imitation of Liquid Glass with blur.
class Glass {
  static const _ch = MethodChannel('ai.remotepc/glass');
  static bool? _supported;

  static Future<bool> isSupported() async {
    if (_supported != null) return _supported!;
    if (!Platform.isIOS) return _supported = false;
    try {
      _supported = await _ch.invokeMethod<bool>('isSupported') ?? false;
    } on PlatformException {
      _supported = false;
    } on MissingPluginException {
      _supported = false;
    }
    return _supported!;
  }
}

class GlassAction {
  final String id;
  final IconData icon;
  final String sfSymbol;
  final String label;
  final bool active;
  const GlassAction(this.id, this.icon, this.sfSymbol, this.label, {this.active = false});
}

/// Floating toolbar for the remote screen.
class GlassToolbar extends StatefulWidget {
  final List<GlassAction> actions;
  final void Function(String id) onAction;
  const GlassToolbar({super.key, required this.actions, required this.onAction});
  @override
  State<GlassToolbar> createState() => _GlassToolbarState();
}

class _GlassToolbarState extends State<GlassToolbar> {
  bool? _native;
  MethodChannel? _viewChannel;

  @override
  void initState() {
    super.initState();
    Glass.isSupported().then((v) => mounted ? setState(() => _native = v) : null);
  }

  @override
  void didUpdateWidget(covariant GlassToolbar old) {
    super.didUpdateWidget(old);
    _viewChannel?.invokeMethod('update', _params());
  }

  Map<String, dynamic> _params() => {
        'actions': widget.actions.map((a) => {'id': a.id, 'symbol': a.sfSymbol, 'label': a.label, 'active': a.active}).toList(),
      };

  @override
  Widget build(BuildContext context) {
    if (_native == null) return const SizedBox(height: 64);
    if (_native!) {
      return SizedBox(
        height: 64,
        child: UiKitView(
          viewType: 'ai.remotepc/glass-toolbar',
          creationParams: _params(),
          creationParamsCodec: const StandardMessageCodec(),
          gestureRecognizers: <Factory<OneSequenceGestureRecognizer>>{Factory(() => EagerGestureRecognizer())},
          onPlatformViewCreated: (id) {
            _viewChannel = MethodChannel('ai.remotepc/glass-toolbar/$id')
              ..setMethodCallHandler((call) async {
                if (call.method == 'tap') widget.onAction(call.arguments as String);
              });
          },
        ),
      );
    }
    return _FallbackBar(actions: widget.actions, onAction: widget.onAction);
  }
}

class _FallbackBar extends StatelessWidget {
  final List<GlassAction> actions;
  final void Function(String) onAction;
  const _FallbackBar({required this.actions, required this.onAction});

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final ios = Platform.isIOS;
    final bar = Container(
      height: 60,
      padding: const EdgeInsets.symmetric(horizontal: 6),
      decoration: BoxDecoration(
        // iOS 18–25: system-like translucent chrome; Android: Material 3 surface container.
        color: ios ? t.colorScheme.surface.withValues(alpha: 0.82) : t.colorScheme.surfaceContainerHigh,
        borderRadius: BorderRadius.circular(30),
        boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.25), blurRadius: 16, offset: const Offset(0, 4))],
      ),
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Row(children: [
          for (final a in actions)
            Tooltip(
              message: a.label,
              child: IconButton(
                isSelected: a.active,
                icon: Icon(a.icon, color: a.active ? t.colorScheme.primary : null),
                onPressed: () => onAction(a.id),
              ),
            ),
        ]),
      ),
    );
    if (!ios) return bar;
    return ClipRRect(
      borderRadius: BorderRadius.circular(30),
      // Standard iOS material (UIBlurEffect-like), the same chrome iOS 18 navigation bars use — not a glass imitation.
      child: BackdropFilter(filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20), child: bar),
    );
  }
}
