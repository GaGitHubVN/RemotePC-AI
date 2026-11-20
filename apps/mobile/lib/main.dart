import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'app_state.dart';
import 'l10n.dart';
import 'screens/chat.dart';
import 'screens/files.dart';
import 'screens/home.dart';
import 'screens/onboarding.dart';
import 'screens/settings.dart';
import 'ui/theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
  runApp(const RemotePcApp());
  await AppState.instance.init();
}

class RemotePcApp extends StatelessWidget {
  const RemotePcApp({super.key});

  @override
  Widget build(BuildContext context) => ListenableBuilder(
        listenable: AppState.instance,
        builder: (context, _) {
          final s = AppState.instance;
          Motion.reduced = s.reduceMotion || MediaQuery.maybeDisableAnimationsOf(context) == true;
          return MaterialApp(
            title: 'RemotePC AI',
            debugShowCheckedModeBanner: false,
            theme: buildTheme(Brightness.light),
            darkTheme: buildTheme(Brightness.dark),
            themeMode: switch (s.themeMode) { 'light' => ThemeMode.light, 'dark' => ThemeMode.dark, _ => ThemeMode.system },
            home: !s.ready ? const SplashScreen() : (!s.onboarded ? const OnboardingScreen() : const RootShell()),
          );
        },
      );
}

class SplashScreen extends StatelessWidget {
  const SplashScreen({super.key});
  @override
  Widget build(BuildContext context) => Scaffold(
        body: Center(
          child: TweenAnimationBuilder<double>(
            tween: Tween(begin: 0.85, end: 1),
            duration: Motion.slow,
            curve: Motion.emphasized,
            builder: (_, v, c) => Transform.scale(scale: v, child: c),
            child: Container(
              width: 96, height: 96,
              decoration: BoxDecoration(gradient: Brand.gradient, borderRadius: BorderRadius.circular(26)),
              child: const Icon(Icons.desktop_windows_rounded, color: Colors.white, size: 48),
            ),
          ),
        ),
      );
}

class RootShell extends StatefulWidget {
  const RootShell({super.key});
  @override
  State<RootShell> createState() => _RootShellState();
}

class _RootShellState extends State<RootShell> {
  int _tab = 0;

  @override
  Widget build(BuildContext context) {
    final pages = const [HomeScreen(), ChatListScreen(), FilesRootScreen(), SettingsScreen()];
    return Scaffold(
      body: AnimatedSwitcher(
        duration: Motion.medium,
        switchInCurve: Motion.standard,
        transitionBuilder: (c, a) => FadeTransition(opacity: a, child: SlideTransition(position: Tween(begin: const Offset(0, 0.02), end: Offset.zero).animate(a), child: c)),
        child: KeyedSubtree(key: ValueKey(_tab), child: pages[_tab]),
      ),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _tab,
        onDestinationSelected: (i) => setState(() => _tab = i),
        destinations: [
          NavigationDestination(icon: const Icon(Icons.computer_outlined), selectedIcon: const Icon(Icons.computer), label: tr('home')),
          NavigationDestination(icon: const Icon(Icons.auto_awesome_outlined), selectedIcon: const Icon(Icons.auto_awesome), label: tr('ai')),
          NavigationDestination(icon: const Icon(Icons.folder_outlined), selectedIcon: const Icon(Icons.folder), label: tr('files')),
          NavigationDestination(icon: const Icon(Icons.settings_outlined), selectedIcon: const Icon(Icons.settings), label: tr('settings')),
        ],
      ),
    );
  }
}
