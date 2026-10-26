import 'package:flutter/cupertino.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../data/app_state.dart';
import '../main.dart';

/// 首次打开：扫电脑屏幕上的二维码。横屏左边取景框、右边说明；竖屏上下排。
class ConnectScreen extends StatelessWidget {
  const ConnectScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final scheme = Theme.of(context).colorScheme;
    final text = Theme.of(context).textTheme;

    final viewfinder = _Viewfinder(
      useFakeCamera: app.showDebug,
      onScanned: (raw) => app.connect(raw),
    );
    final info = Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('连接主持电脑', style: text.displaySmall?.copyWith(color: scheme.onSurface, fontWeight: FontWeight.w600)),
        const SizedBox(height: 16),
        Text('在电脑上打开主持软件，左侧底部有平板图标，点开是一个大二维码。把平板对准它，连上后会自动进入。',
            style: text.titleMedium?.copyWith(color: scheme.onSurfaceVariant, height: 1.6)),
        const SizedBox(height: 28),
        const _Step(n: 1, text: '平板和电脑连同一个 Wi-Fi'),
        const _Step(n: 2, text: '对准电脑上的二维码'),
        const _Step(n: 3, text: '以后打开 App 会自动连上这台电脑'),
        const SizedBox(height: 28),
        OutlinedButton.icon(
          icon: const Icon(Icons.keyboard_outlined),
          label: const Text('扫不了？手动输入地址'),
          style: OutlinedButton.styleFrom(minimumSize: const Size(0, 52), padding: const EdgeInsets.symmetric(horizontal: 20)),
          onPressed: () => _manualAddress(context),
        ),
      ],
    );

    return SafeArea(
      child: LayoutBuilder(builder: (context, c) {
        final landscape = c.maxWidth > c.maxHeight;
        return Padding(
          padding: const EdgeInsets.all(48),
          child: landscape
              ? Row(children: [
                  Expanded(child: Center(child: viewfinder)),
                  const SizedBox(width: 56),
                  Expanded(child: info),
                ])
              : Column(children: [
                  Expanded(child: Center(child: viewfinder)),
                  const SizedBox(height: 40),
                  info,
                ]),
        );
      }),
    );
  }

  Future<void> _manualAddress(BuildContext context) async {
    final app = AppScope.of(context);
    final controller = TextEditingController(text: app.serverAddress);
    final ok = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('输入电脑地址'),
        content: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
          const Text('地址印在电脑上二维码的下面。'),
          const SizedBox(height: 16),
          TextField(
            controller: controller,
            autofocus: true,
            keyboardType: TextInputType.url,
            decoration: const InputDecoration(
              border: OutlineInputBorder(),
              prefixText: 'http://',
              hintText: '192.168.1.x:5188',
            ),
            onSubmitted: (_) => Navigator.pop(context, true),
          ),
        ]),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('取消')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('连接')),
        ],
      ),
    );
    if (ok == true) await app.connect(controller.text);
  }
}

class _Step extends StatelessWidget {
  const _Step({required this.n, required this.text});
  final int n;
  final String text;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(children: [
        CircleAvatar(
          radius: 15,
          backgroundColor: scheme.primaryContainer,
          child: Text('$n', style: TextStyle(color: scheme.onPrimaryContainer, fontWeight: FontWeight.w600)),
        ),
        const SizedBox(width: 14),
        Text(text, style: Theme.of(context).textTheme.titleMedium),
      ]),
    );
  }
}

/// 相机取景框：真机调系统摄像头扫二维码；没有相机权限或原型模式下显示深色取景框与提示。
class _Viewfinder extends StatefulWidget {
  const _Viewfinder({required this.useFakeCamera, required this.onScanned});
  final bool useFakeCamera;
  final ValueChanged<String> onScanned;

  @override
  State<_Viewfinder> createState() => _ViewfinderState();
}

class _ViewfinderState extends State<_Viewfinder> with SingleTickerProviderStateMixin {
  late final _line = AnimationController(vsync: this, duration: const Duration(milliseconds: 1800))..repeat(reverse: true);
  bool _handled = false;

  @override
  void dispose() {
    _line.dispose();
    super.dispose();
  }

  void _onDetect(BarcodeCapture capture) {
    if (_handled) return;
    for (final code in capture.barcodes) {
      final raw = code.rawValue?.trim();
      if (raw == null || raw.isEmpty) continue;
      if (AppState.normalizeAddress(raw).isEmpty) continue;
      _handled = true;
      widget.onScanned(raw);
      return;
    }
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final canScan = !widget.useFakeCamera &&
        (kIsWeb || defaultTargetPlatform == TargetPlatform.iOS || defaultTargetPlatform == TargetPlatform.android);
    return AspectRatio(
      aspectRatio: 1,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: ClipRRect(
          borderRadius: BorderRadius.circular(28),
          child: ColoredBox(
            color: const Color(0xFF070605),
            child: LayoutBuilder(builder: (context, c) {
              return Stack(children: [
                if (canScan)
                  MobileScanner(
                    onDetect: _onDetect,
                    errorBuilder: (context, error) => Center(
                      child: Padding(
                        padding: const EdgeInsets.all(32),
                        child: Column(mainAxisSize: MainAxisSize.min, children: [
                          Icon(Icons.videocam_off_outlined, size: 48, color: Colors.white.withValues(alpha: 0.5)),
                          const SizedBox(height: 12),
                          Text(
                            '未开启相机或没有摄像头\n请在系统设置里允许相机权限，或点右侧手动输入地址',
                            textAlign: TextAlign.center,
                            style: TextStyle(color: Colors.white.withValues(alpha: 0.75), fontSize: 14, height: 1.5),
                          ),
                        ]),
                      ),
                    ),
                  )
                else
                  Center(
                    child: Icon(Icons.qr_code_2, size: c.maxWidth * 0.42, color: Colors.white.withValues(alpha: 0.09)),
                  ),
                Positioned.fill(child: IgnorePointer(child: CustomPaint(painter: _Corners(scheme.primary)))),
                AnimatedBuilder(
                  animation: _line,
                  builder: (context, _) => Positioned(
                    left: c.maxWidth * 0.14,
                    right: c.maxWidth * 0.14,
                    top: c.maxHeight * (0.16 + 0.66 * Curves.easeInOut.transform(_line.value)),
                    child: IgnorePointer(child: Container(height: 2, color: scheme.primary.withValues(alpha: 0.8))),
                  ),
                ),
                Positioned(
                  left: 16,
                  right: 16,
                  bottom: 22,
                  child: IgnorePointer(
                    child: Text(
                      canScan ? '将电脑上的二维码放进框内' : '请对准二维码，或点右侧手动输入地址',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: Colors.white.withValues(alpha: 0.75), fontSize: 14),
                    ),
                  ),
                ),
              ]);
            }),
          ),
        ),
      ),
    );
  }
}

class _Corners extends CustomPainter {
  _Corners(this.color);
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    final p = Paint()
      ..color = color
      ..strokeWidth = 5
      ..style = PaintingStyle.stroke
      ..strokeCap = StrokeCap.round;
    final inset = size.width * 0.12, len = size.width * 0.12;
    final r = Rect.fromLTRB(inset, inset, size.width - inset, size.height - inset);
    for (final (o, dx, dy) in [
      (r.topLeft, 1.0, 1.0),
      (r.topRight, -1.0, 1.0),
      (r.bottomLeft, 1.0, -1.0),
      (r.bottomRight, -1.0, -1.0),
    ]) {
      canvas.drawLine(o, o.translate(dx * len, 0), p);
      canvas.drawLine(o, o.translate(0, dy * len), p);
    }
  }

  @override
  bool shouldRepaint(_Corners old) => old.color != color;
}

/// iPad 首次连接局域网前，先说清为什么要授权，再弹系统框（原型用同样样式的对话框代替）。
class LocalNetworkScreen extends StatelessWidget {
  const LocalNetworkScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final denied = app.stage == Stage.localNetworkDenied;
    final scheme = Theme.of(context).colorScheme;
    final text = Theme.of(context).textTheme;

    Future<void> ask() async {
      final allowed = await showCupertinoDialog<bool>(
        context: context,
        builder: (context) => CupertinoAlertDialog(
          title: const Text('“Dungeon Master”想要查找并连接到本地网络上的设备'),
          content: const Text('用来连接同一 Wi-Fi 下的主持电脑，读取你的角色本和线索卡。'),
          actions: [
            CupertinoDialogAction(onPressed: () => Navigator.pop(context, false), child: const Text('不允许')),
            CupertinoDialogAction(isDefaultAction: true, onPressed: () => Navigator.pop(context, true), child: const Text('好')),
          ],
        ),
      );
      app.go(allowed == true ? Stage.waitingStory : Stage.localNetworkDenied);
    }

    return SafeArea(
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 520),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Icon(denied ? Icons.wifi_off_rounded : Icons.wifi_find_rounded, size: 72, color: scheme.primary),
            const SizedBox(height: 24),
            Text(denied ? '没有本地网络权限，连不上电脑' : '下一步要允许访问本地网络',
                textAlign: TextAlign.center, style: text.headlineMedium?.copyWith(fontWeight: FontWeight.w600)),
            const SizedBox(height: 16),
            Text(
              denied
                  ? '打开「设置 › 隐私与安全性 › 本地网络」，把「Dungeon Master」的开关打开，再回到这里。'
                  : 'iPad 会弹出一个系统提示。请点「好」，否则 App 找不到同一 Wi-Fi 下的主持电脑。',
              textAlign: TextAlign.center,
              style: text.titleMedium?.copyWith(color: scheme.onSurfaceVariant, height: 1.6),
            ),
            const SizedBox(height: 32),
            FilledButton(
              style: FilledButton.styleFrom(minimumSize: const Size(220, 56)),
              onPressed: denied ? () => app.go(Stage.waitingStory) : ask,
              child: Text(denied ? '打开设置' : '继续'),
            ),
          ]),
        ),
      ),
    );
  }
}
