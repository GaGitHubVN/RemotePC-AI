import 'package:flutter/material.dart';

import '../app_state.dart';
import '../l10n.dart';
import '../ui/adaptive.dart';
import '../ui/platform_ui.dart';
import '../ui/theme.dart';

class OnboardingScreen extends StatefulWidget {
  const OnboardingScreen({super.key});
  @override
  State<OnboardingScreen> createState() => _OnboardingScreenState();
}

class _OnboardingScreenState extends State<OnboardingScreen> {
  final _page = PageController();
  int _i = 0;

  void _next() {
    if (_i == 0) {
      _page.nextPage(duration: Motion.medium, curve: Motion.standard);
    } else {
      AppState.instance.onboarded = true;
      AppState.instance.savePrefs();
    }
  }

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    Widget item(AIcon icon, String text, Color c) => Padding(
          padding: const EdgeInsets.symmetric(vertical: 10),
          child: Row(children: [
            Container(
              width: 44, height: 44,
              decoration: BoxDecoration(color: c, borderRadius: BorderRadius.circular(PlatformUi.isGlass ? 14 : 11)),
              child: Icon(icon.data, color: Colors.white, size: 22),
            ),
            const SizedBox(width: 14),
            Expanded(child: Text(text, style: TextStyle(fontSize: 16, color: p.label, height: 1.3))),
          ]),
        );
    Widget page(List<Widget> children) => Padding(
          padding: const EdgeInsets.symmetric(horizontal: 28),
          child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.start, children: children),
        );
    final title = TextStyle(fontSize: PlatformUi.isIOS ? 30 : 28, fontWeight: FontWeight.w700, color: p.label, height: 1.15);
    return Material(
      color: PlatformUi.isIOS ? p.bg : Theme.of(context).colorScheme.surface,
      child: SafeArea(
        child: Column(children: [
          Expanded(
            child: PageView(
              controller: _page,
              onPageChanged: (i) => setState(() => _i = i),
              children: [
                page([
                  Container(
                    width: 76, height: 76,
                    decoration: BoxDecoration(gradient: Brand.gradient, borderRadius: BorderRadius.circular(PlatformUi.isIOS ? 18 : 22)),
                    child: Icon(Ic.pc.data, color: Colors.white, size: 38),
                  ),
                  const SizedBox(height: 24),
                  Text(tr('onb_title'), style: title),
                  const SizedBox(height: 22),
                  item(Ic.screen, tr('onb_1'), const Color(0xFF0A84FF)),
                  item(Ic.ai, tr('onb_2'), const Color(0xFFAF52DE)),
                  item(Ic.shield, tr('onb_3'), const Color(0xFF34C759)),
                ]),
                page([
                  Text(tr('perm_title'), style: title),
                  const SizedBox(height: 22),
                  item(Ic.qr, tr('perm_camera'), const Color(0xFF8E8E93)),
                  item(Ic.wifi, tr('perm_network'), const Color(0xFF0A84FF)),
                  item(Ic.file, tr('perm_files'), const Color(0xFFFF9F0A)),
                ]),
              ],
            ),
          ),
          Row(mainAxisAlignment: MainAxisAlignment.center, children: [
            for (var d = 0; d < 2; d++)
              AnimatedContainer(
                duration: Motion.medium,
                margin: const EdgeInsets.symmetric(horizontal: 4),
                width: d == _i ? 22 : 8, height: 8,
                decoration: BoxDecoration(color: d == _i ? p.accent : p.separator, borderRadius: BorderRadius.circular(4)),
              ),
          ]),
          Padding(
            padding: const EdgeInsets.fromLTRB(24, 20, 24, 16),
            child: PrimaryButton(id: 'onboarding-next', label: _i == 0 ? tr('continue') : tr('start'), onPressed: _next),
          ),
        ]),
      ),
    );
  }
}
