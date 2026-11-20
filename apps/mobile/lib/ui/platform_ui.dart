import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

/// Which UI the app renders. Decided once at startup from the running OS:
/// - [material]  Android: Material 3.
/// - [cupertino] iOS 18–25: the iOS 18 design (Cupertino widgets, system alerts).
/// - [glass]     iOS 26+: native Liquid Glass — a real UITabBarController tab bar, SwiftUI `.glass` buttons and
///               toolbars, system alerts. Availability is reported by native code via `#available(iOS 26.0, *)`.
enum UiStyle { material, cupertino, glass }

class PlatformUi {
  static const _ch = MethodChannel('ai.remotepc/platform');
  static UiStyle style = UiStyle.material;
  static int osMajor = 0;
  static bool reduceTransparency = false;

  static bool get isIOS => style != UiStyle.material;
  static bool get isGlass => style == UiStyle.glass;

  static Future<void> init() async {
    if (!Platform.isIOS) {
      style = UiStyle.material;
      osMajor = int.tryParse(RegExp(r'\d+').firstMatch(Platform.operatingSystemVersion)?.group(0) ?? '') ?? 0;
      return;
    }
    bool? glass;
    try {
      final info = await _ch.invokeMapMethod<String, dynamic>('info');
      osMajor = (info?['osMajor'] as num?)?.toInt() ?? 0;
      glass = info?['glass'] as bool?;
      reduceTransparency = info?['reduceTransparency'] as bool? ?? false;
    } on MissingPluginException {
      // Fall through to parsing "Version 26.1 (Build 23B85)".
    } on PlatformException {
      // same
    }
    if (osMajor == 0) {
      osMajor = int.tryParse(RegExp(r'(\d+)\.').firstMatch(Platform.operatingSystemVersion)?.group(1) ?? '') ?? 18;
    }
    style = (glass ?? osMajor >= 26) ? UiStyle.glass : UiStyle.cupertino;
    debugPrint('RemotePC UI: iOS $osMajor → ${style.name}');
  }

  /// Keeps native chrome (tab bar, alerts, glass views) in the same light/dark mode as the Flutter UI.
  static Future<void> setThemeMode(String mode) async {
    if (!isIOS) return;
    try {
      await _ch.invokeMethod('setStyle', mode);
    } on PlatformException {
      // older build without the method
    }
  }
}

/// A dialog/sheet action.
class DialogAction {
  final String id;
  final String title;
  final bool cancel, destructive, preferred;
  const DialogAction(this.id, this.title, {this.cancel = false, this.destructive = false, this.preferred = false});
  Map<String, dynamic> toMap() => {
        'id': id, 'title': title, 'preferred': preferred,
        'style': cancel ? 'cancel' : (destructive ? 'destructive' : 'default'),
      };
}

/// UIAlertController bridge (iOS only — Android uses Material dialogs in adaptive.dart).
class NativeDialogs {
  static const _ch = MethodChannel('ai.remotepc/dialogs');

  static Future<String?> alert({String? title, String? message, required List<DialogAction> actions}) =>
      _ch.invokeMethod<String>('alert', {'title': title, 'message': message, 'actions': actions.map((a) => a.toMap()).toList()});

  static Future<String?> actionSheet({String? title, String? message, required List<DialogAction> actions}) =>
      _ch.invokeMethod<String>('actionSheet', {'title': title, 'message': message, 'actions': actions.map((a) => a.toMap()).toList()});

  static Future<String?> prompt({required String title, String? message, String text = '', String? placeholder, required String ok, required String cancel}) async {
    final r = await _ch.invokeMapMethod<String, dynamic>('prompt', {
      'title': title, 'message': message, 'text': text, 'placeholder': placeholder,
      'actions': [DialogAction('cancel', cancel, cancel: true).toMap(), DialogAction('ok', ok, preferred: true).toMap()],
    });
    return r?['text'] as String?;
  }
}

class NativeTab {
  final String title, symbol, selectedSymbol;
  const NativeTab(this.title, this.symbol, this.selectedSymbol);
}

/// iOS 26+: the system UITabBarController that hosts the Flutter view.
class NativeTabBar {
  static const _ch = MethodChannel('ai.remotepc/tabbar');
  static final selected = ValueNotifier<int>(0);
  static bool _active = false;
  static bool _hidden = false;
  static bool get active => _active;

  static Future<bool> configure(List<NativeTab> tabs, int index) async {
    _ch.setMethodCallHandler((call) async {
      if (call.method == 'selected') selected.value = call.arguments as int;
    });
    try {
      _active = await _ch.invokeMethod<bool>('configure', {
            'selected': index,
            'items': [for (final t in tabs) {'title': t.title, 'symbol': t.symbol, 'selectedSymbol': t.selectedSymbol}],
          }) ??
          false;
    } on PlatformException {
      _active = false;
    } on MissingPluginException {
      _active = false;
    }
    return _active;
  }

  static void select(int i) {
    selected.value = i;
    if (_active) _ch.invokeMethod('select', {'index': i});
  }

  static void setHidden(bool hidden) {
    if (!_active || hidden == _hidden) return;
    _hidden = hidden;
    _ch.invokeMethod('setHidden', {'hidden': hidden, 'animated': true});
  }
}
