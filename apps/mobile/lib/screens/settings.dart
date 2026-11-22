import 'package:flutter/material.dart';

import '../app_state.dart';
import '../l10n.dart';
import '../ui/adaptive.dart';
import '../ui/platform_ui.dart';
import 'home.dart';

String uiStyleLabel() => switch (PlatformUi.style) {
      UiStyle.glass => '${tr('ui_glass')} · iOS ${PlatformUi.osMajor}',
      UiStyle.cupertino => '${tr('ui_ios')} ${PlatformUi.osMajor}',
      UiStyle.material => '${tr('ui_material')} · Android',
    };

class SettingsScreen extends StatelessWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final s = AppState.instance;
    return ListenableBuilder(
      listenable: s,
      builder: (context, _) => AppPage(
        title: tr('settings'),
        large: true,
        slivers: [
          SliverList.list(children: [
            Section(header: tr('appearance'), children: [
              Tile(
                title: tr('language'),
                icon: Ic.language,
                iconColor: const Color(0xFF0A84FF),
                chevron: false,
                trailing: Segmented<String>(options: const {'vi': 'VI', 'en': 'EN'}, value: s.language, onChanged: (v) {
                  s.language = v;
                  s.savePrefs();
                }),
              ),
              Tile(
                title: tr('theme'),
                icon: Ic.theme,
                iconColor: const Color(0xFF5E5CE6),
                chevron: false,
                value: switch (s.themeMode) { 'light' => tr('theme_light'), 'dark' => tr('theme_dark'), _ => tr('theme_system') },
                onTap: () async {
                  final v = await showActions(context, title: tr('theme'), actions: [
                    DialogAction('system', tr('theme_system')),
                    DialogAction('light', tr('theme_light')),
                    DialogAction('dark', tr('theme_dark')),
                    DialogAction('cancel', tr('cancel'), cancel: true),
                  ]);
                  if (v == null) return;
                  s.themeMode = v;
                  await s.savePrefs();
                  await PlatformUi.setThemeMode(v);
                },
              ),
              Tile(
                title: tr('reduce_motion'),
                icon: Ic.motion,
                iconColor: const Color(0xFF30B0C7),
                chevron: false,
                trailing: AdaptiveSwitch(value: s.reduceMotion, onChanged: (v) {
                  s.reduceMotion = v;
                  s.savePrefs();
                }),
              ),
            ]),
            Section(header: tr('control'), children: [
              Tile(
                title: tr('trackpad'),
                icon: Ic.touch,
                iconColor: const Color(0xFFFF9F0A),
                chevron: false,
                trailing: AdaptiveSwitch(value: s.trackpadMode, onChanged: (v) {
                  s.trackpadMode = v;
                  s.savePrefs();
                }),
              ),
              Tile(
                title: tr('zero_delay'),
                subtitle: tr('zero_delay_note'),
                subtitleLines: 3,
                icon: Ic.screen,
                iconColor: const Color(0xFF30D158),
                chevron: false,
                trailing: AdaptiveSwitch(value: s.zeroDelay, onChanged: (v) {
                  s.zeroDelay = v;
                  s.savePrefs();
                }),
              ),
              Tile(
                title: tr('sensitivity'),
                icon: Ic.pointer,
                iconColor: const Color(0xFFFF375F),
                chevron: false,
                value: s.pointerSensitivity.toStringAsFixed(1),
              ),
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
                child: AdaptiveSlider(min: 0.5, max: 3, divisions: 10, value: s.pointerSensitivity, onChanged: (v) {
                  s.pointerSensitivity = v;
                  s.savePrefs();
                }),
              ),
            ]),
            Section(children: [
              Tile(title: tr('security'), icon: Ic.shield, iconColor: const Color(0xFF34C759), onTap: () => pushPage(context, const SecurityScreen())),
            ]),
            Section(header: tr('about'), footer: tr('about_text'), children: [
              Tile(title: tr('version'), value: '0.4.0', chevron: false),
              Tile(title: tr('ui_style'), value: uiStyleLabel(), chevron: false),
            ]),
          ]),
        ],
      ),
    );
  }
}

class SecurityScreen extends StatelessWidget {
  const SecurityScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final s = AppState.instance;
    return AppPage(title: tr('security'), slivers: [
      SliverList.list(children: [
        Section(header: tr('this_phone'), footer: tr('protection_text'), children: [
          Tile(title: tr('device_id'), subtitle: s.keys.deviceId, chevron: false),
        ]),
        Section(header: tr('paired_pcs'), children: [
          for (final h in s.hosts)
            Tile(title: h.name, subtitle: h.permissions.join(', '), icon: Ic.pc, onTap: () => pushPage(context, HostDetailsScreen(host: h))),
        ]),
        Section(children: [
          Tile(
            title: tr('reset_identity'),
            icon: Ic.refresh,
            destructive: true,
            onTap: () async {
              final r = await showAlert(context, title: tr('reset_identity'), message: tr('reset_confirm'), actions: [
                DialogAction('cancel', tr('cancel'), cancel: true),
                DialogAction('reset', tr('reset_identity'), destructive: true),
              ]);
              if (r == 'reset') await s.resetIdentity();
            },
          ),
        ]),
      ]),
    ]);
  }
}
