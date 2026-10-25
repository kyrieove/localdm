import 'package:flutter/material.dart';

import '../data/app_state.dart';
import '../main.dart';
import 'cards.dart';
import 'reader.dart';
import 'sheets.dart';
import 'talk.dart';

/// 游戏中的外壳：宽度 ≥600 用左侧导航栏（iPad 横竖屏都是），更窄（分屏）用底部导航。
/// 阅读时导航栏随控件一起收起，整屏留给原稿。
class Shell extends StatefulWidget {
  const Shell({super.key});

  @override
  State<Shell> createState() => _ShellState();
}

class _ShellState extends State<Shell> {
  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final app = AppScope.of(context);
    final sheet = app.pendingSheet;
    if (sheet != null) {
      app.pendingSheet = null;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted) return;
        switch (sheet) {
          case 'phase':
            showPhaseSheet(context);
          case 'reading':
            showReadingSheet(context);
          case 'role':
            showRoleSheet(context);
        }
      });
    }
    final toast = app.pendingToast;
    if (toast != null) {
      app.pendingToast = null;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted) return;
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(toast)));
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final sections = [
      Section.book,
      Section.cards,
      Section.talk,
      if (app.handbookPageCount > 0) Section.handbook,
    ];
    final body = switch (app.section) {
      Section.book => const RoleReader(),
      Section.cards => const CardsScreen(),
      Section.talk => const TalkScreen(),
      Section.handbook => const HandbookReader(),
    };
    final content = Column(children: [
      const OfflineBanner(),
      Expanded(
        child: AnimatedSwitcher(
          duration: const Duration(milliseconds: 220),
          child: KeyedSubtree(key: ValueKey(app.section), child: body),
        ),
      ),
    ]);

    return Stack(children: [
      LayoutBuilder(builder: (context, c) {
        if (c.maxWidth < 600) {
          return Scaffold(
            body: content,
            bottomNavigationBar: app.immersive
                ? null
                : NavigationBar(
                    selectedIndex: sections.indexOf(app.section),
                    onDestinationSelected: (i) => app.select(sections[i]),
                    destinations: [for (final s in sections) NavigationDestination(icon: _icon(app, s, false), selectedIcon: _icon(app, s, true), label: _label(s))],
                  ),
          );
        }
        return Scaffold(
          body: Row(children: [
            // 收起导航栏用宽度动画，正文跟着平滑变宽，不是一下跳过去。
            ClipRect(
              child: AnimatedAlign(
                duration: const Duration(milliseconds: 240),
                curve: Curves.easeOutCubic,
                alignment: Alignment.centerRight,
                widthFactor: app.immersive ? 0 : 1,
                child: _Rail(sections: sections),
              ),
            ),
            Expanded(child: content),
          ]),
        );
      }),
      if (app.curtain) const Curtain(),
    ]);
  }
}

String _label(Section s) => switch (s) {
      Section.book => '角色本',
      Section.cards => '线索',
      Section.talk => '对话',
      Section.handbook => '手册',
    };

Widget _icon(AppState app, Section s, bool selected) {
  final icon = switch (s) {
    Section.book => selected ? Icons.auto_stories : Icons.auto_stories_outlined,
    Section.cards => selected ? Icons.style : Icons.style_outlined,
    Section.talk => selected ? Icons.forum : Icons.forum_outlined,
    Section.handbook => selected ? Icons.menu_book : Icons.menu_book_outlined,
  };
  if (s == Section.talk && app.unreadTalk > 0) {
    return Badge(label: Text('${app.unreadTalk}'), child: Icon(icon));
  }
  return Icon(icon);
}

class _Rail extends StatelessWidget {
  const _Rail({required this.sections});
  final List<Section> sections;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final scheme = Theme.of(context).colorScheme;
    return NavigationRail(
      minWidth: 88,
      groupAlignment: -0.2,
      selectedIndex: sections.indexOf(app.section),
      onDestinationSelected: (i) => app.select(sections[i]),
      leading: Padding(
        padding: const EdgeInsets.only(top: 12, bottom: 20),
        child: PhaseDial(onTap: () => showPhaseSheet(context)),
      ),
      trailing: Expanded(
        child: Align(
          alignment: Alignment.bottomCenter,
          child: Padding(
            padding: const EdgeInsets.only(bottom: 20),
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              IconButton(
                tooltip: '遮住屏幕',
                iconSize: 26,
                icon: const Icon(Icons.visibility_off_outlined),
                onPressed: () => app.update(() => app.curtain = true),
              ),
              const SizedBox(height: 16),
              InkWell(
                customBorder: const CircleBorder(),
                onTap: () => showRoleSheet(context),
                child: CircleAvatar(
                  radius: 24,
                  backgroundColor: scheme.primary,
                  child: Text(app.myRole?.displayName.characters.last ?? '?',
                      style: TextStyle(color: scheme.onPrimary, fontSize: 20, fontWeight: FontWeight.w700)),
                ),
              ),
            ]),
          ),
        ),
      ),
      destinations: [
        for (final s in sections)
          NavigationRailDestination(icon: _icon(app, s, false), selectedIcon: _icon(app, s, true), label: Text(_label(s))),
      ],
    );
  }
}

/// 导航栏顶上的环节表盘：一眼看到现在第几环，点开看全部环节。
class PhaseDial extends StatelessWidget {
  const PhaseDial({super.key, required this.onTap});
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final ui = AppScope.of(context).ui;
    final scheme = Theme.of(context).colorScheme;
    return Semantics(
      button: true,
      label: '当前环节 ${ui.phaseTitle}，第 ${ui.phaseIndex} 环，共 ${ui.phaseCount} 环',
      child: InkWell(
        borderRadius: BorderRadius.circular(16),
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.all(6),
          child: Column(children: [
            SizedBox(
              width: 48,
              height: 48,
              child: Stack(alignment: Alignment.center, children: [
                CircularProgressIndicator(
                  value: ui.phaseIndex / ui.phaseCount,
                  strokeWidth: 4,
                  backgroundColor: scheme.outlineVariant,
                  strokeCap: StrokeCap.round,
                ),
                Text('${ui.phaseIndex}/${ui.phaseCount}', style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: scheme.onSurface)),
              ]),
            ),
            const SizedBox(height: 6),
            Text(ui.phaseTitle, style: TextStyle(fontSize: 13, color: scheme.primary, fontWeight: FontWeight.w600)),
          ]),
        ),
      ),
    );
  }
}

/// 断线时顶上滑下一条横幅：内容照常能看（已缓存的页），App 自己在重连。
class OfflineBanner extends StatelessWidget {
  const OfflineBanner({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final scheme = Theme.of(context).colorScheme;
    return AnimatedSize(
      duration: const Duration(milliseconds: 240),
      curve: Curves.easeOutCubic,
      child: app.online
          ? const SizedBox(width: double.infinity)
          : Material(
              color: scheme.surfaceContainerHighest,
              child: SafeArea(
                bottom: false,
                child: Padding(
                  padding: const EdgeInsets.fromLTRB(20, 10, 12, 10),
                  child: Row(children: [
                    const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2.5)),
                    const SizedBox(width: 14),
                    Expanded(
                      child: Text('和电脑断开了，正在重连。已经打开过的页还能看。',
                          style: TextStyle(color: scheme.onSurface, fontSize: 15)),
                    ),
                    TextButton(
                        onPressed: () => app.api == null ? app.setOnline(true) : app.go(Stage.connect),
                        child: const Text('重新扫码')),
                  ]),
                ),
              ),
            ),
    );
  }
}

/// 一键遮住：邻座凑过来、起身离座时用。轻点两下回到原处。
class Curtain extends StatelessWidget {
  const Curtain({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    return Positioned.fill(
      child: GestureDetector(
        onDoubleTap: () => app.update(() => app.curtain = false),
        child: ColoredBox(
          color: Colors.black,
          child: Center(
            child: Text('轻点两下回来', style: TextStyle(color: Colors.white.withValues(alpha: 0.35), fontSize: 16)),
          ),
        ),
      ),
    );
  }
}
