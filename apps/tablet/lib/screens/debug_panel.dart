import 'package:flutter/material.dart';

import '../data/app_state.dart';
import '../main.dart';

/// 原型专用：右上角一个小把手，拉开后切换 App 独有的各种状态。真 App 没有这块。
class DebugHandle extends StatelessWidget {
  const DebugHandle({super.key});

  @override
  Widget build(BuildContext context) {
    return Positioned(
      top: MediaQuery.paddingOf(context).top + 6,
      right: 6,
      child: Opacity(
        opacity: 0.55,
        child: IconButton.filledTonal(
          tooltip: '原型调试',
          icon: const Icon(Icons.science_outlined, size: 20),
          onPressed: () => Scaffold.of(context).openEndDrawer(),
        ),
      ),
    );
  }
}

class DebugPanel extends StatelessWidget {
  const DebugPanel({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    void stage(Stage s) {
      Navigator.of(context).pop();
      app.go(s);
    }

    return Drawer(
      width: 340,
      child: SafeArea(
        child: ListView(padding: const EdgeInsets.symmetric(vertical: 8), children: [
          const ListTile(title: Text('原型调试'), subtitle: Text('真 App 里没有这块，用来看各种状态')),
          const _Header('打开 App 之后的每一步'),
          for (final (s, label) in [
            (Stage.connect, '首次：扫码连接电脑'),
            (Stage.localNetwork, 'iPad：本地网络授权'),
            (Stage.waitingStory, '等主持人选剧本'),
            (Stage.preparing, '材料准备中'),
            (Stage.pickRole, '选角色'),
            (Stage.inGame, '游戏中'),
          ])
            ListTile(dense: true, title: Text(label), selected: app.stage == s, onTap: () => stage(s)),
          const _Header('游戏中的意外'),
          SwitchListTile(
            dense: true,
            title: const Text('和电脑断开'),
            value: !app.online,
            onChanged: (v) => app.setOnline(!v),
          ),
          SwitchListTile(
            dense: true,
            title: const Text('下一次认领被别人抢先（409）'),
            value: app.nextClaimTaken,
            onChanged: (v) => app.update(() => app.nextClaimTaken = v),
          ),
          SwitchListTile(
            dense: true,
            title: const Text('主持人正在说话'),
            value: app.hostSpeaking,
            onChanged: (v) => app.update(() => app.hostSpeaking = v),
          ),
          SwitchListTile(
            dense: true,
            title: const Text('「回忆」组暂停分发'),
            value: app.catalog.cards.any((c) => !c.isAvailable),
            onChanged: (v) => app.update(() {
              for (final c in app.catalog.cards.where((c) => c.groupName == '回忆')) {
                c.isAvailable = !v;
              }
            }),
          ),
          ListTile(
            dense: true,
            title: const Text('主持人发来一条消息'),
            onTap: () => app.addHostMessage('大家先把自己的角色本读完，十分钟后开始自我介绍。'),
          ),
          ListTile(
            dense: true,
            title: const Text('杀掉 App 再打开'),
            subtitle: const Text('回到上次读到的页'),
            onTap: () {
              Navigator.of(context).pop();
              app.simulateRelaunch();
            },
          ),
          const _Header('外观'),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
            child: SegmentedButton<ThemeModePref>(
              segments: const [
                ButtonSegment(value: ThemeModePref.dark, label: Text('深色')),
                ButtonSegment(value: ThemeModePref.light, label: Text('浅色')),
                ButtonSegment(value: ThemeModePref.system, label: Text('跟随系统')),
              ],
              selected: {app.themePref},
              onSelectionChanged: (v) => app.update(() => app.themePref = v.first),
            ),
          ),
        ]),
      ),
    );
  }
}

class _Header extends StatelessWidget {
  const _Header(this.text);
  final String text;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 4),
        child: Text(text, style: Theme.of(context).textTheme.labelLarge?.copyWith(color: Theme.of(context).colorScheme.primary)),
      );
}
