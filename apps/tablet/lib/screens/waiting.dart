import 'package:flutter/foundation.dart';
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
    final statusText = app.online
        ? '已连上 ${app.serverAddress}${app.showDebug ? '（原型假地址）' : ''}'
        : '正在连接 ${app.serverAddress}…';
    final offlineIos = !app.online && defaultTargetPlatform == TargetPlatform.iOS;
    return SafeArea(
      child: Stack(children: [
        Positioned(
          top: 20,
          left: 24,
          right: 24,
          child: Row(children: [
            Chip(
              avatar: Icon(app.online ? Icons.link_rounded : Icons.sync_problem_rounded, color: scheme.primary, size: 18),
              label: Text(statusText),
            ),
            const SizedBox(width: 12),
            TextButton.icon(
              icon: const Icon(Icons.qr_code_scanner_rounded, size: 18),
              label: const Text('换电脑'),
              onPressed: () => app.disconnect(),
            ),
          ]),
        ),
        Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 520),
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              const _Lamp(),
              const SizedBox(height: 36),
              Text(
                  !app.online
                      ? '正在连接主持电脑'
                      : preparing
                          ? '《${app.catalog.storyTitle}》材料准备中'
                          : '等主持人打开剧本',
                  textAlign: TextAlign.center,
                  style: text.headlineMedium?.copyWith(fontWeight: FontWeight.w600)),
              const SizedBox(height: 14),
              Text(
                  !app.online
                      ? (offlineIos
                          ? '连不上 ${app.serverAddress}。请确认同一 Wi-Fi，并在 iPad「设置 › 隐私与安全性 › 本地网络」里打开「绘合·Paletale」开关，或点左上角换电脑。'
                          : '连不上 ${app.serverAddress}。请确认平板和电脑在同一 Wi-Fi，或点左上角重新扫码。')
                      : preparing
                          ? '线索卡和角色本正在整理，几秒后自动进入选角色。'
                          : '电脑上一打开剧本，这里会自动变成选角色，不用做任何事。',
                  textAlign: TextAlign.center,
                  style: text.titleMedium?.copyWith(color: scheme.onSurfaceVariant, height: 1.6)),
              if (preparing && app.online) ...[
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
