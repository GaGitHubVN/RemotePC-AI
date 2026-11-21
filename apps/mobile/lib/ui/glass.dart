import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

/// Native Liquid Glass views (iOS 26+ only). Each is a SwiftUI view in ios/Runner/AppDelegate.swift embedded with a
/// UiKitView; nothing here imitates glass with blur. Callers must check `PlatformUi.isGlass` first.
class GlassAction {
  final String id;
  final String sfSymbol;
  final String label;
  final bool active, prominent, destructive;
  const GlassAction(this.id, this.sfSymbol, this.label, {this.active = false, this.prominent = false, this.destructive = false});
  Map<String, dynamic> toMap() => {'id': id, 'symbol': sfSymbol, 'label': label, 'active': active, 'prominent': prominent, 'destructive': destructive};
}

class _GlassView extends StatefulWidget {
  final String viewType;
  final List<GlassAction> actions;
  final String? title;
  final bool enabled;
  final void Function(String id) onTap;
  const _GlassView({required this.viewType, required this.actions, required this.onTap, this.title, this.enabled = true});
  @override
  State<_GlassView> createState() => _GlassViewState();
}

class _GlassViewState extends State<_GlassView> {
  MethodChannel? _ch;

  Map<String, dynamic> _params() => {
        'actions': widget.actions.map((a) => a.toMap()).toList(),
        if (widget.title != null) 'title': widget.title,
        'enabled': widget.enabled,
      };

  @override
  void didUpdateWidget(covariant _GlassView old) {
    super.didUpdateWidget(old);
    _ch?.invokeMethod('update', _params());
  }

  @override
  Widget build(BuildContext context) {
    // A UiKitView is a real UIView composited above Flutter. If it stays alive while another route covers or slides
    // over its page, iOS keeps showing it (the "two blue stripes" over the remote screen). So the native view exists
    // only while its route is current and settled; otherwise a Flutter-drawn look-alike takes its place.
    // Every enclosing route counts: the button may sit in a tab navigator while the remote screen is pushed on the
    // root navigator above the whole shell.
    // Walk outwards: route → its navigator → the route that hosts that navigator → … (findAncestorStateOfType skips
    // the element itself; Navigator.maybeOf would return the same navigator again and loop forever).
    final routes = <ModalRoute<dynamic>>[];
    BuildContext? ctx = context;
    for (var depth = 0; ctx != null && depth < 8; depth++) {
      final r = ModalRoute.of(ctx);
      if (r == null || routes.contains(r)) break;
      routes.add(r);
      ctx = ctx.findAncestorStateOfType<NavigatorState>()?.context;
    }
    final anims = <Listenable>[
      for (final r in routes) ...[if (r.animation != null) r.animation!, if (r.secondaryAnimation != null) r.secondaryAnimation!],
    ];
    return ListenableBuilder(
      listenable: Listenable.merge(anims),
      builder: (context, _) {
        final settled = routes.every((r) =>
            r.isCurrent && (r.animation?.isCompleted ?? true) && (r.secondaryAnimation?.isDismissed ?? true));
        if (!settled) return _placeholder(context);
        return UiKitView(
          viewType: widget.viewType,
          creationParams: _params(),
          creationParamsCodec: const StandardMessageCodec(),
          gestureRecognizers: <Factory<OneSequenceGestureRecognizer>>{Factory(() => EagerGestureRecognizer())},
          onPlatformViewCreated: (id) {
            _ch = MethodChannel('ai.remotepc/glass/$id')
              ..setMethodCallHandler((call) async {
                if (call.method == 'tap') widget.onTap(call.arguments as String);
              });
          },
        );
      },
    );
  }

  /// Static stand-in shown during transitions (no platform view).
  Widget _placeholder(BuildContext context) {
    final dark = MediaQuery.platformBrightnessOf(context) == Brightness.dark;
    final a = widget.actions.isEmpty ? null : widget.actions.first;
    final prominent = widget.viewType.endsWith('glass-button') && (a?.prominent ?? false);
    final fill = prominent
        ? (a!.destructive ? const Color(0xFFFF3B30) : const Color(0xFF0A84FF))
        : (dark ? const Color(0x33FFFFFF) : const Color(0x14000000));
    return DecoratedBox(
      decoration: BoxDecoration(color: fill, borderRadius: BorderRadius.circular(999)),
      child: widget.title == null
          ? const SizedBox.expand()
          : Center(
              child: Text(widget.title!,
                  style: TextStyle(
                      color: prominent ? const Color(0xFFFFFFFF) : (dark ? const Color(0xFFFFFFFF) : const Color(0xFF000000)),
                      fontSize: 17,
                      fontWeight: FontWeight.w600,
                      decoration: TextDecoration.none)),
            ),
    );
  }
}

/// Floating remote-control toolbar (GlassEffectContainer of interactive glass circles).
class GlassToolbar extends StatelessWidget {
  final List<GlassAction> actions;
  final void Function(String id) onAction;
  const GlassToolbar({super.key, required this.actions, required this.onAction});
  @override
  Widget build(BuildContext context) =>
      SizedBox(height: 64, child: _GlassView(viewType: 'ai.remotepc/glass-toolbar', actions: actions, onTap: onAction));
}

/// Navigation-bar button group in one glass capsule (iOS 26 bar item style).
class GlassBar extends StatelessWidget {
  final List<GlassAction> actions;
  final void Function(String id) onAction;
  const GlassBar({super.key, required this.actions, required this.onAction});
  @override
  Widget build(BuildContext context) => SizedBox(
        width: 44.0 * actions.length,
        height: 44,
        child: _GlassView(viewType: 'ai.remotepc/glass-bar', actions: actions, onTap: onAction),
      );
}

/// Full-width `.glassProminent` (or `.glass` when [prominent] is false) system button.
class GlassButton extends StatelessWidget {
  final String id, title, sfSymbol;
  final bool prominent, destructive;
  final VoidCallback? onPressed;
  const GlassButton({super.key, required this.id, required this.title, this.sfSymbol = '', this.prominent = true, this.destructive = false, this.onPressed});
  @override
  Widget build(BuildContext context) => SizedBox(
        height: 52,
        child: _GlassView(
          viewType: 'ai.remotepc/glass-button',
          title: title,
          enabled: onPressed != null,
          actions: [GlassAction(id, sfSymbol, title, prominent: prominent, destructive: destructive)],
          onTap: (_) => onPressed?.call(),
        ),
      );
}
