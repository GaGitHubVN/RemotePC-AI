import 'package:flutter/cupertino.dart' show CupertinoPageTransitionsBuilder;
import 'package:flutter/material.dart';

/// Shared animation tokens (also mirrored on Windows). Reduce Motion collapses durations to zero.
class Motion {
  static bool reduced = false;
  static Duration get fast => reduced ? Duration.zero : const Duration(milliseconds: 140);
  static Duration get medium => reduced ? Duration.zero : const Duration(milliseconds: 240);
  static Duration get slow => reduced ? Duration.zero : const Duration(milliseconds: 380);
  static const Curve standard = Curves.easeOutCubic;
  static const Curve emphasized = Curves.easeOutBack;
  static const SpringDescription spring = SpringDescription(mass: 1, stiffness: 420, damping: 32);
  static const double pressScale = 0.96;
}

class Brand {
  static const seed = Color(0xFF4F46E5);
  static const accent2 = Color(0xFF06B6D4);
  static const gradient = LinearGradient(colors: [Color(0xFF4F46E5), Color(0xFF06B6D4)], begin: Alignment.topLeft, end: Alignment.bottomRight);
  static const radius = 18.0;
}

ThemeData buildTheme(Brightness b) {
  final scheme = ColorScheme.fromSeed(seedColor: Brand.seed, brightness: b);
  return ThemeData(
    useMaterial3: true,
    colorScheme: scheme,
    scaffoldBackgroundColor: b == Brightness.dark ? const Color(0xFF0F1117) : const Color(0xFFF4F6FB),
    cardTheme: CardThemeData(
      elevation: 0,
      color: b == Brightness.dark ? const Color(0xFF1A1E2C) : Colors.white,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(Brand.radius)),
      margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
    ),
    pageTransitionsTheme: const PageTransitionsTheme(builders: {
      TargetPlatform.android: PredictiveBackPageTransitionsBuilder(),
      TargetPlatform.iOS: CupertinoPageTransitionsBuilder(),
    }),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      border: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: BorderSide.none),
    ),
  );
}

/// Button wrapper with the shared press-scale animation.
class Pressable extends StatefulWidget {
  final Widget child;
  final VoidCallback? onTap;
  const Pressable({super.key, required this.child, this.onTap});
  @override
  State<Pressable> createState() => _PressableState();
}

class _PressableState extends State<Pressable> {
  bool _down = false;
  @override
  Widget build(BuildContext context) => GestureDetector(
        onTapDown: (_) => setState(() => _down = true),
        onTapCancel: () => setState(() => _down = false),
        onTapUp: (_) => setState(() => _down = false),
        onTap: widget.onTap,
        child: AnimatedScale(scale: _down ? Motion.pressScale : 1, duration: Motion.fast, curve: Motion.standard, child: widget.child),
      );
}

/// Empty / error state used across screens.
class EmptyState extends StatelessWidget {
  final IconData icon;
  final String title;
  final String? subtitle;
  final Widget? action;
  const EmptyState({super.key, required this.icon, required this.title, this.subtitle, this.action});
  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: TweenAnimationBuilder<double>(
          tween: Tween(begin: 0, end: 1),
          duration: Motion.slow,
          curve: Motion.standard,
          builder: (_, v, child) => Opacity(opacity: v, child: Transform.translate(offset: Offset(0, 12 * (1 - v)), child: child)),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Icon(icon, size: 56, color: t.colorScheme.primary),
            const SizedBox(height: 16),
            Text(title, style: t.textTheme.titleMedium, textAlign: TextAlign.center),
            if (subtitle != null) ...[const SizedBox(height: 8), Text(subtitle!, style: t.textTheme.bodySmall, textAlign: TextAlign.center)],
            if (action != null) ...[const SizedBox(height: 20), action!],
          ]),
        ),
      ),
    );
  }
}
