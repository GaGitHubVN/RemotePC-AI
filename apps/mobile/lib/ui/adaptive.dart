import 'dart:async';

import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';

import 'glass.dart';
import 'platform_ui.dart';
import 'theme.dart';

/// One icon in the three design languages: Material (Android), Cupertino (iOS 18 Flutter UI) and an SF Symbol name
/// (native iOS 26 glass views and the native tab bar).
class AIcon {
  final IconData material, cupertino;
  final String sf;
  const AIcon(this.material, this.cupertino, this.sf);
  IconData get data => PlatformUi.isIOS ? cupertino : material;
}

class Ic {
  static const add = AIcon(Icons.add, CupertinoIcons.add, 'plus');
  static const pc = AIcon(Icons.desktop_windows_rounded, CupertinoIcons.desktopcomputer, 'desktopcomputer');
  static const ai = AIcon(Icons.auto_awesome, CupertinoIcons.sparkles, 'sparkles');
  static const folder = AIcon(Icons.folder_rounded, CupertinoIcons.folder_fill, 'folder.fill');
  static const file = AIcon(Icons.insert_drive_file_outlined, CupertinoIcons.doc, 'doc');
  static const settings = AIcon(Icons.settings_outlined, CupertinoIcons.gear_alt_fill, 'gearshape.fill');
  static const more = AIcon(Icons.more_horiz, CupertinoIcons.ellipsis, 'ellipsis');
  static const refresh = AIcon(Icons.refresh, CupertinoIcons.arrow_clockwise, 'arrow.clockwise');
  static const delete = AIcon(Icons.delete_outline, CupertinoIcons.trash, 'trash');
  static const rename = AIcon(Icons.drive_file_rename_outline, CupertinoIcons.pencil, 'pencil');
  static const download = AIcon(Icons.download_rounded, CupertinoIcons.arrow_down_circle, 'arrow.down.circle');
  static const upload = AIcon(Icons.upload_rounded, CupertinoIcons.arrow_up_circle, 'arrow.up.circle');
  static const newFolder = AIcon(Icons.create_new_folder_outlined, CupertinoIcons.folder_badge_plus, 'folder.badge.plus');
  static const sort = AIcon(Icons.sort, CupertinoIcons.arrow_up_arrow_down, 'arrow.up.arrow.down');
  static const back = AIcon(Icons.arrow_back, CupertinoIcons.chevron_back, 'chevron.left');
  static const close = AIcon(Icons.close, CupertinoIcons.xmark, 'xmark');
  static const check = AIcon(Icons.check, CupertinoIcons.checkmark, 'checkmark');
  static const send = AIcon(Icons.arrow_upward, CupertinoIcons.arrow_up, 'arrow.up');
  static const stop = AIcon(Icons.stop_rounded, CupertinoIcons.stop_fill, 'stop.fill');
  static const attach = AIcon(Icons.attach_file, CupertinoIcons.paperclip, 'paperclip');
  static const image = AIcon(Icons.image_outlined, CupertinoIcons.photo, 'photo');
  static const agent = AIcon(Icons.smart_toy_outlined, CupertinoIcons.wand_stars, 'wand.and.stars');
  static const copy = AIcon(Icons.copy, CupertinoIcons.doc_on_doc, 'doc.on.doc');
  static const edit = AIcon(Icons.edit_outlined, CupertinoIcons.pencil, 'pencil');
  static const screen = AIcon(Icons.screen_share_outlined, CupertinoIcons.tv, 'display');
  static const info = AIcon(Icons.info_outline, CupertinoIcons.info_circle, 'info.circle');
  static const shield = AIcon(Icons.shield_outlined, CupertinoIcons.lock_shield, 'lock.shield');
  static const unlink = AIcon(Icons.link_off, CupertinoIcons.xmark_circle, 'xmark.circle');
  static const pause = AIcon(Icons.pause, CupertinoIcons.pause_fill, 'pause.fill');
  static const play = AIcon(Icons.play_arrow, CupertinoIcons.play_fill, 'play.fill');
  static const diag = AIcon(Icons.monitor_heart_outlined, CupertinoIcons.speedometer, 'speedometer');
  static const language = AIcon(Icons.language, CupertinoIcons.globe, 'globe');
  static const theme = AIcon(Icons.dark_mode_outlined, CupertinoIcons.moon, 'moon');
  static const motion = AIcon(Icons.animation, CupertinoIcons.wind, 'wind');
  static const touch = AIcon(Icons.touch_app_outlined, CupertinoIcons.hand_point_left, 'hand.point.up.left');
  static const pointer = AIcon(Icons.speed, CupertinoIcons.scope, 'scope');
  static const qr = AIcon(Icons.qr_code_scanner, CupertinoIcons.qrcode_viewfinder, 'qrcode.viewfinder');
  static const code = AIcon(Icons.password, CupertinoIcons.number, 'number');
  static const wifi = AIcon(Icons.wifi, CupertinoIcons.wifi, 'wifi');
  static const wifiOff = AIcon(Icons.wifi_off, CupertinoIcons.wifi_slash, 'wifi.slash');
  static const warning = AIcon(Icons.warning_amber_rounded, CupertinoIcons.exclamationmark_triangle, 'exclamationmark.triangle');
  static const ok = AIcon(Icons.check_circle, CupertinoIcons.checkmark_circle_fill, 'checkmark.circle.fill');
  static const error = AIcon(Icons.error_outline, CupertinoIcons.xmark_circle_fill, 'xmark.circle.fill');
  static const clock = AIcon(Icons.schedule, CupertinoIcons.clock, 'clock');
  static const chat = AIcon(Icons.forum_outlined, CupertinoIcons.chat_bubble_2, 'bubble.left.and.bubble.right');
  static const share = AIcon(Icons.ios_share, CupertinoIcons.square_arrow_up, 'square.and.arrow.up');
  static const search = AIcon(Icons.search, CupertinoIcons.search, 'magnifyingglass');
  static const compose = AIcon(Icons.edit_square, CupertinoIcons.square_pencil, 'square.and.pencil');
  static const verified = AIcon(Icons.verified, CupertinoIcons.checkmark_seal_fill, 'checkmark.seal.fill');
  static const lock = AIcon(Icons.lock_outline, CupertinoIcons.lock, 'lock');
}

/// Semantic colours per design language.
class Palette {
  final Color bg, grouped, card, label, secondary, separator, accent, danger, success, warning;
  const Palette(this.bg, this.grouped, this.card, this.label, this.secondary, this.separator, this.accent, this.danger, this.success, this.warning);

  static Palette of(BuildContext c) {
    if (PlatformUi.isIOS) {
      Color r(CupertinoDynamicColor x) => x.resolveFrom(c);
      return Palette(r(CupertinoColors.systemBackground), r(CupertinoColors.systemGroupedBackground), r(CupertinoColors.secondarySystemGroupedBackground),
          r(CupertinoColors.label), r(CupertinoColors.secondaryLabel), r(CupertinoColors.separator), Brand.seed, r(CupertinoColors.systemRed),
          r(CupertinoColors.systemGreen), r(CupertinoColors.systemOrange));
    }
    final s = Theme.of(c).colorScheme;
    return Palette(s.surface, s.surface, s.surfaceContainerLow, s.onSurface, s.onSurfaceVariant, s.outlineVariant, s.primary, s.error,
        const Color(0xFF1E9E5A), const Color(0xFFE08A00));
  }
}

// ----------------------------------------------------------------------------------------------------------------
// Pages
// ----------------------------------------------------------------------------------------------------------------

class BarItem {
  final String id;
  final AIcon icon;
  final String label;
  final VoidCallback onTap;
  final bool destructive;
  const BarItem(this.id, this.icon, this.label, this.onTap, {this.destructive = false});
}

/// Standard page: large or inline title, trailing bar items, scrolling body, optional pinned bottom (composer).
///
/// - Android: Material 3 `SliverAppBar.large` / pinned `SliverAppBar`.
/// - iOS 18: `CupertinoSliverNavigationBar` (large title) / `CupertinoNavigationBar`, text/icon bar buttons.
/// - iOS 26: the same Cupertino layout, but bar items and the back button are native SwiftUI glass (GlassBar).
class AppPage extends StatelessWidget {
  final String title;
  final Widget? titleWidget;
  final List<BarItem> actions;
  final bool large;
  final List<Widget> slivers;
  final Widget? bottom;
  final Future<void> Function()? onRefresh;
  final bool grouped;
  final Widget? leading;
  final ScrollController? controller;

  const AppPage({super.key, required this.title, this.titleWidget, this.actions = const [], this.large = false, required this.slivers,
      this.bottom, this.onRefresh, this.grouped = true, this.leading, this.controller});

  @override
  Widget build(BuildContext context) => switch (PlatformUi.style) {
        UiStyle.material => _material(context),
        _ => _cupertino(context),
      };

  Widget _material(BuildContext context) {
    final acts = [
      for (final a in actions) IconButton(tooltip: a.label, onPressed: a.onTap, icon: Icon(a.icon.material, color: a.destructive ? Theme.of(context).colorScheme.error : null)),
      const SizedBox(width: 4),
    ];
    final titleW = titleWidget ?? Text(title);
    Widget scroll = CustomScrollView(controller: controller, slivers: [
      large ? SliverAppBar.large(title: titleW, actions: acts, leading: leading) : SliverAppBar(pinned: true, title: titleW, actions: acts, leading: leading),
      ...slivers,
      const SliverToBoxAdapter(child: SizedBox(height: 24)),
    ]);
    if (onRefresh != null) scroll = RefreshIndicator(onRefresh: onRefresh!, edgeOffset: 120, child: scroll);
    return Scaffold(body: Column(children: [Expanded(child: scroll), ?bottom]));
  }

  Widget _cupertino(BuildContext context) {
    final glass = PlatformUi.isGlass;
    final canPop = Navigator.of(context).canPop();
    Widget? trailing;
    if (actions.isNotEmpty) {
      trailing = glass
          ? GlassBar(
              actions: [for (final a in actions) GlassAction(a.id, a.icon.sf, a.label, destructive: a.destructive)],
              onAction: (id) => actions.firstWhere((a) => a.id == id).onTap(),
            )
          : Row(mainAxisSize: MainAxisSize.min, children: [
              for (final a in actions)
                CupertinoButton(
                  padding: const EdgeInsets.symmetric(horizontal: 6),
                  minimumSize: const Size(36, 36),
                  onPressed: a.onTap,
                  child: Icon(a.icon.cupertino, size: 24, semanticLabel: a.label, color: a.destructive ? CupertinoColors.systemRed.resolveFrom(context) : null),
                ),
            ]);
    }
    Widget? lead = leading;
    if (lead == null && glass && canPop) {
      lead = GlassBar(actions: const [GlassAction('back', 'chevron.left', 'Back')], onAction: (_) => Navigator.of(context).maybePop());
    }
    final bg = grouped ? CupertinoColors.systemGroupedBackground.resolveFrom(context) : CupertinoColors.systemBackground.resolveFrom(context);
    final List<Widget> body;
    if (large) {
      body = [
        CupertinoSliverNavigationBar(
          largeTitle: Text(title),
          middle: titleWidget,
          leading: lead,
          trailing: trailing,
          border: null,
          backgroundColor: glass ? bg.withValues(alpha: 0.72) : null,
          padding: glass ? const EdgeInsetsDirectional.only(start: 12, end: 12) : null,
          transitionBetweenRoutes: !glass,
        ),
        if (onRefresh != null) CupertinoSliverRefreshControl(onRefresh: onRefresh),
        ...slivers,
        SliverToBoxAdapter(child: SizedBox(height: 24 + MediaQuery.paddingOf(context).bottom)),
      ];
      return CupertinoPageScaffold(
        backgroundColor: bg,
        child: Material(type: MaterialType.transparency, child: Column(children: [Expanded(child: CustomScrollView(controller: controller, slivers: body)), ?bottom])),
      );
    }
    final bar = CupertinoNavigationBar(
      middle: titleWidget ?? Text(title, overflow: TextOverflow.ellipsis),
      leading: lead,
      trailing: trailing,
      border: glass ? null : const Border(bottom: BorderSide(color: Color(0x33000000), width: 0)),
      backgroundColor: glass ? bg.withValues(alpha: 0.72) : null,
      padding: glass ? const EdgeInsetsDirectional.only(start: 12, end: 12) : null,
      automaticBackgroundVisibility: !glass,
      transitionBetweenRoutes: !glass,
    );
    return CupertinoPageScaffold(
      backgroundColor: bg,
      navigationBar: bar,
      child: Material(
        type: MaterialType.transparency,
        child: Column(children: [
          Expanded(
            child: CustomScrollView(controller: controller, slivers: [
              SliverToBoxAdapter(child: SizedBox(height: MediaQuery.paddingOf(context).top + (glass ? 8 : 0))),
              if (onRefresh != null) CupertinoSliverRefreshControl(onRefresh: onRefresh),
              ...slivers,
              SliverToBoxAdapter(child: SizedBox(height: 24 + (bottom == null ? MediaQuery.paddingOf(context).bottom : 0))),
            ]),
          ),
          ?bottom,
        ]),
      ),
    );
  }
}

// ----------------------------------------------------------------------------------------------------------------
// Lists
// ----------------------------------------------------------------------------------------------------------------

class Section extends StatelessWidget {
  final String? header, footer;
  final List<Widget> children;
  const Section({super.key, this.header, this.footer, required this.children});

  @override
  Widget build(BuildContext context) {
    if (PlatformUi.isIOS) {
      final radius = PlatformUi.isGlass ? 26.0 : 10.0;
      return CupertinoListSection.insetGrouped(
        header: header == null ? null : Text(header!),
        footer: footer == null ? null : Text(footer!, style: TextStyle(fontSize: 13, color: CupertinoColors.secondaryLabel.resolveFrom(context))),
        backgroundColor: Colors.transparent,
        decoration: BoxDecoration(color: CupertinoColors.secondarySystemGroupedBackground.resolveFrom(context), borderRadius: BorderRadius.circular(radius)),
        margin: const EdgeInsetsDirectional.fromSTEB(16, 0, 16, 8),
        children: children.isEmpty ? null : children,
      );
    }
    final t = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 8),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        if (header != null) Padding(padding: const EdgeInsets.fromLTRB(16, 8, 16, 8), child: Text(header!, style: t.textTheme.labelLarge?.copyWith(color: t.colorScheme.primary))),
        Card(margin: EdgeInsets.zero, clipBehavior: Clip.antiAlias, child: Column(children: children)),
        if (footer != null) Padding(padding: const EdgeInsets.fromLTRB(16, 8, 16, 0), child: Text(footer!, style: t.textTheme.bodySmall)),
      ]),
    );
  }
}

class Tile extends StatelessWidget {
  final String title;
  final String? subtitle, value;
  final AIcon? icon;
  final Color? iconColor;
  final Widget? leading, trailing;
  final VoidCallback? onTap;
  final bool destructive, chevron;
  final int subtitleLines;
  const Tile({super.key, required this.title, this.subtitle, this.value, this.icon, this.iconColor, this.leading, this.trailing, this.onTap,
      this.destructive = false, this.chevron = true, this.subtitleLines = 2});

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    if (PlatformUi.isIOS) {
      final lead = leading ??
          (icon == null
              ? null
              : Container(
                  width: 30, height: 30,
                  decoration: BoxDecoration(color: destructive ? p.danger : (iconColor ?? p.accent), borderRadius: BorderRadius.circular(PlatformUi.isGlass ? 9 : 7)),
                  child: Icon(icon!.cupertino, color: CupertinoColors.white, size: 18),
                ));
      return CupertinoListTile(
        leading: lead,
        leadingSize: 30,
        title: Text(title, style: TextStyle(color: destructive ? p.danger : null), maxLines: 2, overflow: TextOverflow.ellipsis),
        subtitle: subtitle == null ? null : Text(subtitle!, maxLines: subtitleLines, overflow: TextOverflow.ellipsis),
        additionalInfo: value == null ? null : Text(value!),
        trailing: trailing ?? (onTap != null && chevron && !destructive ? const CupertinoListTileChevron() : null),
        onTap: onTap,
      );
    }
    return ListTile(
      leading: leading ?? (icon == null ? null : Icon(icon!.material, color: destructive ? p.danger : iconColor)),
      title: Text(title, style: TextStyle(color: destructive ? p.danger : null)),
      subtitle: subtitle == null ? null : Text(subtitle!, maxLines: subtitleLines, overflow: TextOverflow.ellipsis),
      trailing: trailing ?? (value == null ? null : Text(value!, style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: p.secondary))),
      onTap: onTap,
    );
  }
}

// ----------------------------------------------------------------------------------------------------------------
// Controls
// ----------------------------------------------------------------------------------------------------------------

class PrimaryButton extends StatelessWidget {
  final String id, label;
  final AIcon? icon;
  final VoidCallback? onPressed;
  final bool destructive, secondary;
  const PrimaryButton({super.key, required this.id, required this.label, this.icon, this.onPressed, this.destructive = false, this.secondary = false});

  @override
  Widget build(BuildContext context) {
    switch (PlatformUi.style) {
      case UiStyle.glass:
        return GlassButton(id: id, title: label, sfSymbol: icon?.sf ?? '', prominent: !secondary, destructive: destructive, onPressed: onPressed);
      case UiStyle.cupertino:
        final child = Row(mainAxisAlignment: MainAxisAlignment.center, mainAxisSize: MainAxisSize.min, children: [
          if (icon != null) ...[Icon(icon!.cupertino, size: 20), const SizedBox(width: 6)],
          Flexible(child: Text(label, overflow: TextOverflow.ellipsis)),
        ]);
        return SizedBox(
          width: double.infinity,
          child: secondary
              ? CupertinoButton.tinted(onPressed: onPressed, color: destructive ? CupertinoColors.systemRed : null, child: child)
              : CupertinoButton.filled(onPressed: onPressed, color: destructive ? CupertinoColors.systemRed : null, child: child),
        );
      case UiStyle.material:
        final i = icon == null ? null : Icon(icon!.material);
        final style = destructive ? FilledButton.styleFrom(backgroundColor: Theme.of(context).colorScheme.error, minimumSize: const Size.fromHeight(52)) : FilledButton.styleFrom(minimumSize: const Size.fromHeight(52));
        return secondary
            ? FilledButton.tonalIcon(onPressed: onPressed, style: style, icon: i, label: Text(label))
            : FilledButton.icon(onPressed: onPressed, style: style, icon: i, label: Text(label));
    }
  }
}

/// Small inline text action.
class LinkButton extends StatelessWidget {
  final String label;
  final VoidCallback? onPressed;
  final AIcon? icon;
  const LinkButton({super.key, required this.label, this.onPressed, this.icon});
  @override
  Widget build(BuildContext context) => PlatformUi.isIOS
      ? CupertinoButton(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
          minimumSize: const Size(32, 32),
          onPressed: onPressed,
          child: Row(mainAxisSize: MainAxisSize.min, children: [if (icon != null) ...[Icon(icon!.cupertino, size: 18), const SizedBox(width: 4)], Text(label, style: const TextStyle(fontSize: 15))]),
        )
      : TextButton.icon(onPressed: onPressed, icon: icon == null ? null : Icon(icon!.material, size: 18), label: Text(label));
}

class AdaptiveSwitch extends StatelessWidget {
  final bool value;
  final ValueChanged<bool> onChanged;
  const AdaptiveSwitch({super.key, required this.value, required this.onChanged});
  @override
  Widget build(BuildContext context) =>
      PlatformUi.isIOS ? CupertinoSwitch(value: value, onChanged: onChanged, activeTrackColor: CupertinoColors.systemGreen) : Switch(value: value, onChanged: onChanged);
}

class AdaptiveSlider extends StatelessWidget {
  final double value, min, max;
  final int? divisions;
  final ValueChanged<double> onChanged;
  const AdaptiveSlider({super.key, required this.value, required this.min, required this.max, this.divisions, required this.onChanged});
  @override
  Widget build(BuildContext context) => PlatformUi.isIOS
      ? CupertinoSlider(value: value, min: min, max: max, divisions: divisions, onChanged: onChanged)
      : Slider(value: value, min: min, max: max, divisions: divisions, label: value.toStringAsFixed(1), onChanged: onChanged);
}

class Segmented<T extends Object> extends StatelessWidget {
  final Map<T, String> options;
  final T value;
  final ValueChanged<T> onChanged;
  const Segmented({super.key, required this.options, required this.value, required this.onChanged});
  @override
  Widget build(BuildContext context) => PlatformUi.isIOS
      ? CupertinoSlidingSegmentedControl<T>(
          groupValue: value,
          children: {for (final e in options.entries) e.key: Padding(padding: const EdgeInsets.symmetric(horizontal: 10), child: Text(e.value))},
          onValueChanged: (v) => v == null ? null : onChanged(v),
        )
      : SegmentedButton<T>(
          showSelectedIcon: false,
          segments: [for (final e in options.entries) ButtonSegment(value: e.key, label: Text(e.value))],
          selected: {value},
          onSelectionChanged: (s) => onChanged(s.first),
        );
}

class AdaptiveTextField extends StatelessWidget {
  final TextEditingController? controller;
  final String? placeholder;
  final AIcon? prefix;
  final int minLines, maxLines;
  final ValueChanged<String>? onChanged, onSubmitted;
  final Widget? suffix;
  final bool autofocus, dense;
  final TextInputAction? action;
  final TextCapitalization capitalization;
  final FocusNode? focusNode;
  const AdaptiveTextField({super.key, this.controller, this.placeholder, this.prefix, this.minLines = 1, this.maxLines = 1, this.onChanged, this.onSubmitted,
      this.suffix, this.autofocus = false, this.dense = false, this.action, this.capitalization = TextCapitalization.none, this.focusNode});

  @override
  Widget build(BuildContext context) {
    if (PlatformUi.isIOS) {
      return CupertinoTextField(
        controller: controller,
        focusNode: focusNode,
        placeholder: placeholder,
        minLines: minLines,
        maxLines: maxLines,
        autofocus: autofocus,
        textInputAction: action,
        textCapitalization: capitalization,
        onChanged: onChanged,
        onSubmitted: onSubmitted,
        padding: EdgeInsets.symmetric(horizontal: 12, vertical: dense ? 8 : 11),
        prefix: prefix == null ? null : Padding(padding: const EdgeInsets.only(left: 10), child: Icon(prefix!.cupertino, size: 18, color: CupertinoColors.secondaryLabel.resolveFrom(context))),
        suffix: suffix,
        decoration: BoxDecoration(
          color: CupertinoColors.tertiarySystemFill.resolveFrom(context),
          borderRadius: BorderRadius.circular(PlatformUi.isGlass ? 22 : 10),
        ),
      );
    }
    return TextField(
      controller: controller,
      focusNode: focusNode,
      minLines: minLines,
      maxLines: maxLines,
      autofocus: autofocus,
      textInputAction: action,
      textCapitalization: capitalization,
      onChanged: onChanged,
      onSubmitted: onSubmitted,
      decoration: InputDecoration(hintText: placeholder, isDense: dense, prefixIcon: prefix == null ? null : Icon(prefix!.material), suffixIcon: suffix),
    );
  }
}

class SearchField extends StatelessWidget {
  final String placeholder;
  final ValueChanged<String>? onChanged, onSubmitted;
  const SearchField({super.key, required this.placeholder, this.onChanged, this.onSubmitted});
  @override
  Widget build(BuildContext context) => PlatformUi.isIOS
      ? CupertinoSearchTextField(placeholder: placeholder, onChanged: onChanged, onSubmitted: onSubmitted,
          borderRadius: BorderRadius.circular(PlatformUi.isGlass ? 22 : 10))
      : SearchBar(hintText: placeholder, leading: const Icon(Icons.search), onChanged: onChanged, onSubmitted: onSubmitted, elevation: const WidgetStatePropertyAll(0));
}

class Spinner extends StatelessWidget {
  final double size;
  const Spinner({super.key, this.size = 20});
  @override
  Widget build(BuildContext context) => PlatformUi.isIOS
      ? CupertinoActivityIndicator(radius: size / 2)
      : SizedBox(width: size, height: size, child: const CircularProgressIndicator(strokeWidth: 2.5));
}

class ProgressLine extends StatelessWidget {
  final double? value;
  const ProgressLine({super.key, this.value});
  @override
  Widget build(BuildContext context) => ClipRRect(
        borderRadius: BorderRadius.circular(2),
        child: LinearProgressIndicator(value: value, minHeight: PlatformUi.isIOS ? 3 : 4, color: Palette.of(context).accent,
            backgroundColor: Palette.of(context).separator.withValues(alpha: 0.3)),
      );
}

/// Empty / error state used across screens.
class EmptyState extends StatelessWidget {
  final AIcon icon;
  final String title;
  final String? subtitle;
  final Widget? action;
  const EmptyState({super.key, required this.icon, required this.title, this.subtitle, this.action});
  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(32, 48, 32, 32),
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        Icon(icon.data, size: 52, color: p.secondary),
        const SizedBox(height: 14),
        Text(title, style: TextStyle(fontSize: 20, fontWeight: FontWeight.w600, color: p.label), textAlign: TextAlign.center),
        if (subtitle != null) ...[const SizedBox(height: 8), Text(subtitle!, style: TextStyle(fontSize: 15, color: p.secondary), textAlign: TextAlign.center)],
        if (action != null) ...[const SizedBox(height: 22), action!],
      ]),
    );
  }
}

// ----------------------------------------------------------------------------------------------------------------
// Navigation, dialogs, toasts
// ----------------------------------------------------------------------------------------------------------------

Route<T> pageRoute<T>(WidgetBuilder b, {bool fullscreen = false}) =>
    PlatformUi.isIOS ? CupertinoPageRoute<T>(builder: b, fullscreenDialog: fullscreen) : MaterialPageRoute<T>(builder: b, fullscreenDialog: fullscreen);

/// Pushes inside the current tab (tab bar stays visible).
Future<T?> pushPage<T>(BuildContext context, Widget page) => Navigator.of(context).push<T>(pageRoute((_) => page));

/// Pushes above the tab shell (tab bar hidden): remote desktop, conversations, pairing.
Future<T?> pushRoot<T>(BuildContext context, Widget page, {bool fullscreen = false}) =>
    Navigator.of(context, rootNavigator: true).push<T>(pageRoute((_) => page, fullscreen: fullscreen));

Future<String?> showAlert(BuildContext context, {required String title, String? message, required List<DialogAction> actions}) async {
  if (PlatformUi.isIOS) return NativeDialogs.alert(title: title, message: message, actions: actions);
  return showDialog<String>(
    context: context,
    builder: (c) => AlertDialog(
      title: Text(title),
      content: message == null ? null : Text(message),
      actions: [
        for (final a in actions)
          a.preferred || a.destructive
              ? FilledButton(
                  style: a.destructive ? FilledButton.styleFrom(backgroundColor: Theme.of(c).colorScheme.error) : null,
                  onPressed: () => Navigator.pop(c, a.id),
                  child: Text(a.title))
              : TextButton(onPressed: () => Navigator.pop(c, a.cancel ? null : a.id), child: Text(a.title)),
      ],
    ),
  );
}

Future<String?> showActions(BuildContext context, {String? title, String? message, required List<DialogAction> actions}) async {
  if (PlatformUi.isIOS) return NativeDialogs.actionSheet(title: title, message: message, actions: actions);
  return showModalBottomSheet<String>(
    context: context,
    showDragHandle: true,
    builder: (c) => SafeArea(
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        if (title != null) ListTile(title: Text(title, style: Theme.of(c).textTheme.titleMedium), subtitle: message == null ? null : Text(message)),
        for (final a in actions.where((a) => !a.cancel))
          ListTile(
            title: Text(a.title, style: TextStyle(color: a.destructive ? Theme.of(c).colorScheme.error : null)),
            onTap: () => Navigator.pop(c, a.id),
          ),
      ]),
    ),
  );
}

Future<String?> showPrompt(BuildContext context, {required String title, String? message, String text = '', required String ok, required String cancel}) async {
  if (PlatformUi.isIOS) return NativeDialogs.prompt(title: title, message: message, text: text, ok: ok, cancel: cancel);
  final ctl = TextEditingController(text: text);
  return showDialog<String>(
    context: context,
    builder: (c) => AlertDialog(
      title: Text(title),
      content: TextField(controller: ctl, autofocus: true, onSubmitted: (v) => Navigator.pop(c, v)),
      actions: [TextButton(onPressed: () => Navigator.pop(c), child: Text(cancel)), FilledButton(onPressed: () => Navigator.pop(c, ctl.text), child: Text(ok))],
    ),
  );
}

/// Content sheet (model picker, approvals, file details). iOS: the iOS sheet; Android: Material bottom sheet.
Future<T?> showPanel<T>(BuildContext context, {required String title, required List<Widget> Function(BuildContext) children, bool dismissible = true}) {
  if (PlatformUi.isIOS) {
    return showCupertinoSheet<T>(
      context: context,
      enableDrag: dismissible,
      showDragHandle: true,
      scrollableBuilder: (c, sc) => CupertinoPageScaffold(
        backgroundColor: CupertinoColors.systemGroupedBackground.resolveFrom(c),
        child: Material(
          type: MaterialType.transparency,
          child: SafeArea(
            top: false,
            child: ListView(controller: sc, padding: const EdgeInsets.only(top: 20, bottom: 16), children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(20, 4, 20, 12),
                child: Text(title, style: TextStyle(fontSize: 22, fontWeight: FontWeight.w700, color: CupertinoColors.label.resolveFrom(c))),
              ),
              ...children(c),
            ]),
          ),
        ),
      ),
    );
  }
  return showModalBottomSheet<T>(
    context: context,
    isScrollControlled: true,
    isDismissible: dismissible,
    enableDrag: dismissible,
    showDragHandle: true,
    useSafeArea: true,
    builder: (c) => DraggableScrollableSheet(
      expand: false,
      initialChildSize: 0.6,
      maxChildSize: 0.95,
      builder: (c, sc) => ListView(controller: sc, children: [
        Padding(padding: const EdgeInsets.fromLTRB(24, 0, 24, 12), child: Text(title, style: Theme.of(c).textTheme.titleLarge)),
        ...children(c),
      ]),
    ),
  );
}

OverlayEntry? _toast;
Timer? _toastTimer;

/// Brief message. Android: SnackBar. iOS: a system-style capsule near the top (iOS has no snackbar).
void showToast(BuildContext context, String message) {
  if (!PlatformUi.isIOS) {
    ScaffoldMessenger.maybeOf(context)?.showSnackBar(SnackBar(content: Text(message), behavior: SnackBarBehavior.floating, duration: const Duration(seconds: 3)));
    return;
  }
  final overlay = Overlay.maybeOf(context, rootOverlay: true);
  if (overlay == null) return;
  _toast?.remove();
  _toastTimer?.cancel();
  final entry = OverlayEntry(builder: (c) {
    final dark = CupertinoTheme.brightnessOf(c) == Brightness.dark;
    return Positioned(
      left: 24, right: 24, top: MediaQuery.paddingOf(c).top + 8,
      child: IgnorePointer(
        child: TweenAnimationBuilder<double>(
          tween: Tween(begin: 0, end: 1),
          duration: Motion.medium,
          curve: Motion.standard,
          builder: (_, v, ch) => Opacity(opacity: v, child: Transform.translate(offset: Offset(0, -12 * (1 - v)), child: ch)),
          child: Center(
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 12),
              decoration: BoxDecoration(
                color: dark ? const Color(0xF22C2C2E) : const Color(0xF2FFFFFF),
                borderRadius: BorderRadius.circular(PlatformUi.isGlass ? 26 : 14),
                boxShadow: const [BoxShadow(color: Color(0x33000000), blurRadius: 18, offset: Offset(0, 6))],
              ),
              child: Text(message, textAlign: TextAlign.center, style: TextStyle(fontSize: 15, decoration: TextDecoration.none, color: dark ? Colors.white : Colors.black, fontWeight: FontWeight.w500)),
            ),
          ),
        ),
      ),
    );
  });
  _toast = entry;
  overlay.insert(entry);
  _toastTimer = Timer(const Duration(milliseconds: 2600), () {
    if (_toast == entry) {
      entry.remove();
      _toast = null;
    }
  });
}
