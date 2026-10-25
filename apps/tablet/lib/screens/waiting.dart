import 'package:flutter/material.dart';

import '../data/app_state.dart';
import '../main.dart';

/// 连上了，但主持人还没打开剧本，或材料还在准备。电脑那边一动，这里自动往下走。
class WaitingScreen extends StatelessWidget {
  const WaitingScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final preparing = app.stage == Stage.preparing;
    final scheme = Theme.of(context).colorScheme;
    final text = Theme.of(context).textTheme;
    return SafeArea(
      child: Stack(children: [
        Positioned(
          top: 20,
          left: 24,
          child: Chip(
            avatar: Icon(Icons.link_rounded, color: scheme.primary, size: 18),
            label: Text('已连上 ${AppState.serverAddress}${app.showDebug ? '（原型假地址）' : ''}'),
          ),
        ),
        Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 520),
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              const _Lamp(),
              const SizedBox(height: 36),
              Text(preparing ? '《${app.catalog.storyTitle}》材料准备中' : '等主持人打开剧本',
                  textAlign: TextAlign.center, style: text.headlineMedium?.copyWith(fontWeight: FontWeight.w600)),
              const SizedBox(height: 14),
              Text(preparing ? '线索卡和角色本正在整理，几秒后自动进入选角色。' : '电脑上一打开剧本，这里会自动变成选角色，不用做任何事。',
                  textAlign: TextAlign.center, style: text.titleMedium?.copyWith(color: scheme.onSurfaceVariant, height: 1.6)),
              if (preparing) ...[
                const SizedBox(height: 28),
                SizedBox(width: 280, child: LinearProgressIndicator(borderRadius: BorderRadius.circular(4))),
              ],
            ]),
          ),
        ),
      ]),
    );
  }
}

/// 一盏慢慢呼吸的灯：告诉玩家 App 没卡住，只是在等。
class _Lamp extends StatefulWidget {
  const _Lamp();

  @override
  State<_Lamp> createState() => _LampState();
}

class _LampState extends State<_Lamp> with SingleTickerProviderStateMixin {
  late final _c = AnimationController(vsync: this, duration: const Duration(milliseconds: 2400))..repeat(reverse: true);

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final lamp = Theme.of(context).colorScheme.primary;
    return AnimatedBuilder(
      animation: _c,
      builder: (context, _) {
        final t = Curves.easeInOut.transform(_c.value);
        return Container(
          width: 120,
          height: 120,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            gradient: RadialGradient(colors: [
              lamp.withValues(alpha: 0.55 + 0.25 * t),
              lamp.withValues(alpha: 0.12 + 0.1 * t),
              lamp.withValues(alpha: 0),
            ], stops: const [0.0, 0.45, 1.0]),
          ),
          child: Icon(Icons.local_library_outlined, size: 44, color: Theme.of(context).colorScheme.onPrimary),
        );
      },
    );
  }
}
