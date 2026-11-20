import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';

/// Shared animation tokens (also mirrored on Windows). Reduce Motion collapses durations to zero.
class Motion {
  static bool reduced = false;
  static Duration get fast => reduced ? Duration.zero : const Duration(milliseconds: 140);
  static Duration get medium => reduced ? Duration.zero : const Duration(milliseconds: 240);
  static Duration get slow => reduced ? Duration.zero : const Duration(milliseconds: 380);
  static const Curve standard = Curves.easeOutCubic;
  static const Curve emphasized = Curves.easeOutBack;
}

class Brand {
  static const seed = Color(0xFF4F46E5);
  static const accent2 = Color(0xFF06B6D4);
  static const gradient = LinearGradient(colors: [Color(0xFF4F46E5), Color(0xFF06B6D4)], begin: Alignment.topLeft, end: Alignment.bottomRight);
}

/// Android: Material 3 (expressive shapes, tonal surfaces, predictive back).
ThemeData buildMaterialTheme(Brightness b) {
  final scheme = ColorScheme.fromSeed(seedColor: Brand.seed, brightness: b, dynamicSchemeVariant: DynamicSchemeVariant.tonalSpot);
  return ThemeData(
    useMaterial3: true,
    colorScheme: scheme,
    scaffoldBackgroundColor: scheme.surface,
    appBarTheme: AppBarTheme(backgroundColor: scheme.surface, surfaceTintColor: Colors.transparent, scrolledUnderElevation: 2),
    cardTheme: CardThemeData(
      elevation: 0,
      color: scheme.surfaceContainerLow,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(24)),
      margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
    ),
    listTileTheme: const ListTileThemeData(contentPadding: EdgeInsets.symmetric(horizontal: 20, vertical: 2)),
    navigationBarTheme: NavigationBarThemeData(
      backgroundColor: scheme.surfaceContainer,
      indicatorColor: scheme.secondaryContainer,
      labelBehavior: NavigationDestinationLabelBehavior.alwaysShow,
      height: 72,
    ),
    filledButtonTheme: FilledButtonThemeData(style: FilledButton.styleFrom(shape: const StadiumBorder(), padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 14))),
    floatingActionButtonTheme: FloatingActionButtonThemeData(shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(20))),
    pageTransitionsTheme: const PageTransitionsTheme(builders: {TargetPlatform.android: PredictiveBackPageTransitionsBuilder()}),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: scheme.surfaceContainerHighest,
      border: OutlineInputBorder(borderRadius: BorderRadius.circular(28), borderSide: BorderSide.none),
      contentPadding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
    ),
    snackBarTheme: SnackBarThemeData(shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16))),
  );
}

/// iOS: Material widgets that remain (Markdown, selection, ink) take iOS system colours; the visible chrome is Cupertino.
ThemeData buildIosSupportTheme(Brightness b) {
  final dark = b == Brightness.dark;
  final scheme = ColorScheme.fromSeed(seedColor: Brand.seed, brightness: b).copyWith(
    primary: Brand.seed,
    surface: dark ? const Color(0xFF000000) : const Color(0xFFF2F2F7),
    surfaceContainerHighest: dark ? const Color(0xFF2C2C2E) : const Color(0xFFE5E5EA),
    error: dark ? const Color(0xFFFF453A) : const Color(0xFFFF3B30),
  );
  return ThemeData(
    useMaterial3: true,
    platform: TargetPlatform.iOS,
    colorScheme: scheme,
    scaffoldBackgroundColor: scheme.surface,
    splashFactory: NoSplash.splashFactory,
    cupertinoOverrideTheme: buildCupertinoTheme(b),
  );
}

CupertinoThemeData buildCupertinoTheme(Brightness b) => CupertinoThemeData(brightness: b, primaryColor: Brand.seed, applyThemeToAll: true);
