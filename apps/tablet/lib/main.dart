import 'package:flutter/material.dart';

import 'data/app_state.dart';
import 'screens/connect.dart';
import 'screens/debug_panel.dart';
import 'screens/role_pick.dart';
import 'screens/shell.dart';
import 'screens/waiting.dart';
import 'theme.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final state = AppState();
  await state.load();
  state.applyLink(Uri.base.queryParameters);
  runApp(TabletApp(state: state));
}

/// 让任意一屏都能拿到 AppState，并在它变化时重建。
class AppScope extends InheritedNotifier<AppState> {
  const AppScope({super.key, required AppState state, required super.child}) : super(notifier: state);

  static AppState of(BuildContext context) => context.dependOnInheritedWidgetOfExactType<AppScope>()!.notifier!;
}

class TabletApp extends StatelessWidget {
  const TabletApp({super.key, required this.state});
  final AppState state;

  @override
  Widget build(BuildContext context) {
    return AppScope(
      state: state,
      child: ListenableBuilder(
        listenable: state,
        builder: (context, _) => MaterialApp(
          title: '本局主持',
          debugShowCheckedModeBanner: false,
          theme: buildTheme(Palette.light),
          darkTheme: buildTheme(Palette.dark),
          themeMode: switch (state.themePref) {
            ThemeModePref.system => ThemeMode.system,
            ThemeModePref.light => ThemeMode.light,
            ThemeModePref.dark => ThemeMode.dark,
          },
          home: const RootScreen(),
        ),
      ),
    );
  }
}

class RootScreen extends StatelessWidget {
  const RootScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final screen = switch (app.stage) {
      Stage.connect => const ConnectScreen(),
      Stage.localNetwork || Stage.localNetworkDenied => const LocalNetworkScreen(),
      Stage.waitingStory || Stage.preparing => const WaitingScreen(),
      Stage.pickRole => const RolePickScreen(),
      Stage.inGame => const Shell(),
    };
    return Scaffold(
      endDrawer: const DebugPanel(),
      body: Stack(children: [
        // 各步之间用淡入淡出（Material 的 fade through），不是网页式整页跳转。
        AnimatedSwitcher(
          duration: const Duration(milliseconds: 280),
          switchInCurve: Curves.easeOutCubic,
          child: KeyedSubtree(key: ValueKey(app.stage == Stage.localNetworkDenied ? Stage.localNetwork : app.stage), child: screen),
        ),
        if (app.showDebug) const DebugHandle(),
      ]),
    );
  }
}
