import 'package:flutter/material.dart';

import '../app_state.dart';
import '../l10n.dart';
import '../ui/theme.dart';

class OnboardingScreen extends StatefulWidget {
  const OnboardingScreen({super.key});
  @override
  State<OnboardingScreen> createState() => _OnboardingScreenState();
}

class _OnboardingScreenState extends State<OnboardingScreen> {
  final _page = PageController();
  int _i = 0;

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    Widget item(IconData icon, String text) => Padding(
          padding: const EdgeInsets.symmetric(vertical: 10),
          child: Row(children: [
            Container(
              width: 44, height: 44,
              decoration: BoxDecoration(color: t.colorScheme.primaryContainer, borderRadius: BorderRadius.circular(14)),
              child: Icon(icon, color: t.colorScheme.onPrimaryContainer),
            ),
            const SizedBox(width: 14),
            Expanded(child: Text(text, style: t.textTheme.bodyLarge)),
          ]),
        );
    return Scaffold(
      body: SafeArea(
        child: Column(children: [
          Expanded(
            child: PageView(
              controller: _page,
              onPageChanged: (i) => setState(() => _i = i),
              children: [
                Padding(
                  padding: const EdgeInsets.all(28),
                  child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Container(
                      width: 72, height: 72,
                      decoration: BoxDecoration(gradient: Brand.gradient, borderRadius: BorderRadius.circular(20)),
                      child: const Icon(Icons.desktop_windows_rounded, color: Colors.white, size: 36),
                    ),
                    const SizedBox(height: 24),
                    Text(tr('onb_title'), style: t.textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w600)),
                    const SizedBox(height: 20),
                    item(Icons.screen_share_outlined, tr('onb_1')),
                    item(Icons.auto_awesome_outlined, tr('onb_2')),
                    item(Icons.verified_user_outlined, tr('onb_3')),
                  ]),
                ),
                Padding(
                  padding: const EdgeInsets.all(28),
                  child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(tr('perm_title'), style: t.textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w600)),
                    const SizedBox(height: 20),
                    item(Icons.qr_code_scanner, tr('perm_camera')),
                    item(Icons.wifi, tr('perm_network')),
                    item(Icons.insert_drive_file_outlined, tr('perm_files')),
                  ]),
                ),
              ],
            ),
          ),
          Padding(
            padding: const EdgeInsets.all(20),
            child: Row(children: [
              for (var d = 0; d < 2; d++)
                AnimatedContainer(
                  duration: Motion.medium,
                  margin: const EdgeInsets.only(right: 6),
                  width: d == _i ? 22 : 8, height: 8,
                  decoration: BoxDecoration(color: d == _i ? t.colorScheme.primary : t.colorScheme.outlineVariant, borderRadius: BorderRadius.circular(4)),
                ),
              const Spacer(),
              FilledButton(
                onPressed: () {
                  if (_i == 0) {
                    _page.nextPage(duration: Motion.medium, curve: Motion.standard);
                  } else {
                    AppState.instance.onboarded = true;
                    AppState.instance.savePrefs();
                  }
                },
                child: Text(_i == 0 ? tr('continue') : tr('start')),
              ),
            ]),
          ),
        ]),
      ),
    );
  }
}
