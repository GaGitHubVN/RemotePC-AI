import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';

import 'app_state.dart';
import 'l10n.dart';
import 'screens/chat.dart';
import 'screens/files.dart';
import 'screens/home.dart';
import 'screens/onboarding.dart';
import 'screens/settings.dart';
import 'ui/adaptive.dart';
import 'ui/platform_ui.dart';
import 'ui/theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
  await PlatformUi.init();
  runApp(const RemotePcApp());
  await AppState.instance.init();
  PlatformUi.setThemeMode(AppState.instance.themeMode);
}

/// Hides the iOS 26 native tab bar while anything is shown above the tab shell on the root navigator (remote desktop,
/// conversations, pairing, sheets, dialogs) — Flutter content cannot be drawn above a native view.
class TabBarVisibilityObserver extends NavigatorObserver {
  int _above = 0;
  void _apply() => NativeTabBar.setHidden(_above > 0);
  @override
  void didPush(Route<dynamic> route, Route<dynamic>? previousRoute) {
    if (previousRoute != null) _above++;
    _apply();
  }

  @override
  void didPop(Route<dynamic> route, Route<dynamic>? previousRoute) {
    if (_above > 0) _above--;
    _apply();
  }

  @override
  void didRemove(Route<dynamic> route, Route<dynamic>? previousRoute) {
    if (_above > 0 && previousRoute != null) _above--;
    _apply();
  }
}

final _tabObserver = TabBarVisibilityObserver();

class RemotePcApp extends StatelessWidget {
  const RemotePcApp({super.key});

  @override
  Widget build(BuildContext context) => ListenableBuilder(
        listenable: AppState.instance,
        builder: (context, _) {
          final s = AppState.instance;
          Motion.reduced = s.reduceMotion || MediaQuery.maybeDisableAnimationsOf(context) == true;
          final ios = PlatformUi.isIOS;
          return MaterialApp(
            title: 'RemotePC AI',
            debugShowCheckedModeBanner: false,
            theme: ios ? buildIosSupportTheme(Brightness.light) : buildMaterialTheme(Brightness.light),
            darkTheme: ios ? buildIosSupportTheme(Brightness.dark) : buildMaterialTheme(Brightness.dark),
            themeMode: switch (s.themeMode) { 'light' => ThemeMode.light, 'dark' => ThemeMode.dark, _ => ThemeMode.system },
            locale: Locale(s.language),
            supportedLocales: const [Locale('vi'), Locale('en')],
            localizationsDelegates: const [GlobalMaterialLocalizations.delegate, GlobalCupertinoLocalizations.delegate, GlobalWidgetsLocalizations.delegate],
            navigatorObservers: [_tabObserver],
            home: !s.ready ? const SplashScreen() : (!s.onboarded ? const OnboardingScreen() : const RootShell()),
          );
        },
      );
}

class SplashScreen extends StatelessWidget {
  const SplashScreen({super.key});
  @override
  Widget build(BuildContext context) => ColoredBox(
        color: Palette.of(context).grouped,
        child: Center(
          child: Container(
            width: 96, height: 96,
            decoration: BoxDecoration(gradient: Brand.gradient, borderRadius: BorderRadius.circular(26)),
            child: Icon(Ic.pc.data, color: Colors.white, size: 48),
          ),
        ),
      );
}

class _TabDef {
  final String key;
  final AIcon icon;
  final String sfSelected;
  final Widget page;
  const _TabDef(this.key, this.icon, this.sfSelected, this.page);
}

class RootShell extends StatefulWidget {
  const RootShell({super.key});
  @override
  State<RootShell> createState() => _RootShellState();
}

class _RootShellState extends State<RootShell> {
  static const _tabs = [
    _TabDef('home', Ic.pc, 'desktopcomputer', HomeScreen()),
    _TabDef('ai', Ic.ai, 'sparkles', ChatListScreen()),
    _TabDef('files', AIcon(Icons.folder_outlined, CupertinoIcons.folder, 'folder'), 'folder.fill', FilesRootScreen()),
    _TabDef('settings', AIcon(Icons.settings_outlined, CupertinoIcons.settings, 'gearshape'), 'gearshape.fill', SettingsScreen()),
  ];
  final _keys = List.generate(_tabs.length, (_) => GlobalKey<NavigatorState>());
  int _tab = 0;
  bool _native = false;
  String? _configuredLang;

  @override
  void initState() {
    super.initState();
    NativeTabBar.selected.addListener(_onNativeSelect);
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _configureNative();
  }

  Future<void> _configureNative() async {
    if (!PlatformUi.isGlass || _configuredLang == AppState.instance.language) return;
    _configuredLang = AppState.instance.language;
    final ok = await NativeTabBar.configure([for (final t in _tabs) NativeTab(tr(t.key), t.icon.sf, t.sfSelected)], _tab);
    if (mounted && ok != _native) setState(() => _native = ok);
  }

  void _onNativeSelect() {
    final i = NativeTabBar.selected.value;
    if (i == _tab) {
      _keys[i].currentState?.popUntil((r) => r.isFirst); // re-tap pops to the tab root, as in UIKit
    } else {
      setState(() => _tab = i);
    }
  }

  @override
  void dispose() {
    NativeTabBar.selected.removeListener(_onNativeSelect);
    super.dispose();
  }

  Widget _navigator(int i) => Navigator(
        key: _keys[i],
        onGenerateRoute: (settings) => pageRoute((_) => _tabs[i].page),
      );

  @override
  Widget build(BuildContext context) {
    if (PlatformUi.isGlass) _configureNative();
    final stack = IndexedStack(index: _tab, children: [for (var i = 0; i < _tabs.length; i++) _navigator(i)]);

    if (PlatformUi.style == UiStyle.material) {
      return NavigatorPopHandler(
        onPopWithResult: (_) => _keys[_tab].currentState?.maybePop(),
        child: Scaffold(
          body: stack,
          bottomNavigationBar: NavigationBar(
            selectedIndex: _tab,
            onDestinationSelected: (i) => i == _tab ? _keys[i].currentState?.popUntil((r) => r.isFirst) : setState(() => _tab = i),
            destinations: [for (final t in _tabs) NavigationDestination(icon: Icon(t.icon.material), label: tr(t.key))],
          ),
        ),
      );
    }
    if (PlatformUi.isGlass && _native) {
      // iOS 26: the tab bar is the system UITabBarController's (Liquid Glass). Flutter draws only the tab content.
      return stack;
    }
    // iOS 18–25 (or if the native tab bar could not be installed): the iOS 18 tab bar.
    return CupertinoTabScaffold(
      tabBar: CupertinoTabBar(
        currentIndex: _tab,
        activeColor: Brand.seed,
        onTap: (i) {
          if (i == _tab) _keys[i].currentState?.popUntil((r) => r.isFirst);
          setState(() => _tab = i);
        },
        items: [for (final t in _tabs) BottomNavigationBarItem(icon: Icon(t.icon.cupertino), label: tr(t.key))],
      ),
      tabBuilder: (_, i) => _navigator(i),
    );
  }
}
