import 'package:flutter/material.dart';

import '../data/app_state.dart';
import '../main.dart';

/// 底部面板统一入口：带拖动把手，可下滑关闭。
Future<T?> _sheet<T>(BuildContext context, Widget Function(BuildContext) body) => showModalBottomSheet<T>(
      context: context,
      isScrollControlled: true,
      builder: (context) => AnimatedBuilder(
        // 面板里改设置时实时重建，背后的页面也同时变化，看得到效果。
        animation: AppScope.of(context),
        builder: (context, _) => SafeArea(child: Padding(padding: const EdgeInsets.fromLTRB(28, 0, 28, 24), child: body(context))),
      ),
    );

Widget _title(BuildContext context, String text) => Padding(
      padding: const EdgeInsets.only(bottom: 20),
      child: Text(text, style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w600)),
    );

Widget _row(BuildContext context, String label, Widget control) => Padding(
      padding: const EdgeInsets.symmetric(vertical: 10),
      child: Row(children: [
        SizedBox(width: 96, child: Text(label, style: Theme.of(context).textTheme.titleMedium)),
        Expanded(child: control),
      ]),
    );

Future<void> showReadingSheet(BuildContext context) => _sheet(context, (context) {
      final app = AppScope.of(context);
      return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        _title(context, '阅读设置'),
        _row(
          context,
          '原稿夜读',
          SegmentedButton<NightMode>(
            segments: const [
              ButtonSegment(value: NightMode.off, label: Text('原样')),
              ButtonSegment(value: NightMode.dim, label: Text('压暗')),
              ButtonSegment(value: NightMode.invert, label: Text('反白')),
            ],
            selected: {app.night},
            onSelectionChanged: (v) => app.update(() => app.night = v.first),
          ),
        ),
        _row(
          context,
          '文字字号',
          Row(children: [
            const Text('A', style: TextStyle(fontSize: 14)),
            Expanded(
              child: Slider(
                value: app.fontSize,
                min: 16,
                max: 28,
                divisions: 6,
                label: '${app.fontSize.round()}',
                onChanged: (v) => app.updateReading(() => app.fontSize = v),
              ),
            ),
            const Text('A', style: TextStyle(fontSize: 24)),
          ]),
        ),
        _row(
          context,
          '文字行距',
          SegmentedButton<double>(
            segments: const [
              ButtonSegment(value: 1.6, label: Text('紧')),
              ButtonSegment(value: 1.95, label: Text('标准')),
              ButtonSegment(value: 2.3, label: Text('松')),
            ],
            selected: {app.lineSpacing},
            onSelectionChanged: (v) => app.updateReading(() => app.lineSpacing = v.first),
          ),
        ),
        _row(
          context,
          '界面',
          SegmentedButton<ThemeModePref>(
            segments: const [
              ButtonSegment(value: ThemeModePref.dark, label: Text('深色')),
              ButtonSegment(value: ThemeModePref.light, label: Text('浅色')),
              ButtonSegment(value: ThemeModePref.system, label: Text('跟随系统')),
            ],
            selected: {app.themePref},
            onSelectionChanged: (v) => app.update(() => app.themePref = v.first),
          ),
        ),
        const SizedBox(height: 8),
        Text('屏幕在 App 打开期间保持常亮。字号和读到哪页会存到电脑上，换天再开也在原处。',
            style: TextStyle(color: Theme.of(context).colorScheme.onSurfaceVariant, fontSize: 14, height: 1.5)),
      ]);
    });

Future<void> showPhaseSheet(BuildContext context) => _sheet(context, (context) {
      final ui = AppScope.of(context).ui;
      final scheme = Theme.of(context).colorScheme;
      return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        _title(context, '本局进行到：${ui.phaseTitle}'),
        for (final (i, p) in ui.phases.indexed)
          ListTile(
            contentPadding: EdgeInsets.zero,
            leading: CircleAvatar(
              radius: 16,
              backgroundColor: switch (p.status) {
                'Current' => scheme.primary,
                'Done' => scheme.surfaceContainerHighest,
                _ => Colors.transparent,
              },
              foregroundColor: p.status == 'Current' ? scheme.onPrimary : scheme.onSurfaceVariant,
              child: p.status == 'Done' ? const Icon(Icons.check_rounded, size: 18) : Text('${i + 1}'),
            ),
            title: Text(p.title,
                style: TextStyle(
                  fontSize: 17,
                  fontWeight: p.status == 'Current' ? FontWeight.w700 : FontWeight.w400,
                  color: p.status == 'Upcoming' ? scheme.onSurfaceVariant : scheme.onSurface,
                )),
            trailing: p.status == 'Current' ? Text('进行中', style: TextStyle(color: scheme.primary)) : null,
          ),
        if (ui.hint != null) ...[
          const SizedBox(height: 12),
          Container(
            padding: const EdgeInsets.all(16),
            decoration: BoxDecoration(color: scheme.surfaceContainerHigh, borderRadius: BorderRadius.circular(16)),
            child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Icon(Icons.lightbulb_outline_rounded, color: scheme.primary),
              const SizedBox(width: 12),
              Expanded(child: Text(ui.hint!, style: const TextStyle(fontSize: 16, height: 1.5))),
            ]),
          ),
        ],
        const SizedBox(height: 16),
        Text('已发 ${ui.releasedMaterialCount} 份材料，还有 ${ui.pendingMaterialCount} 份待发。推进环节${ui.controlsNote}。',
            style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 14)),
      ]);
    });

Future<void> showRoleSheet(BuildContext context) => _sheet(context, (context) {
      final app = AppScope.of(context);
      final scheme = Theme.of(context).colorScheme;
      return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        _title(context, '你是「${app.myRole?.displayName ?? ''}」'),
        Text('《${app.catalog.storyTitle}》 · 连在 ${app.serverAddress}', style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 15)),
        const SizedBox(height: 24),
        OutlinedButton.icon(
          style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52), foregroundColor: scheme.error),
          icon: const Icon(Icons.logout_rounded),
          label: const Text('退出这个角色，重新选'),
          onPressed: () async {
            final yes = await showDialog<bool>(
              context: context,
              builder: (context) => AlertDialog(
                title: const Text('退出这个角色？'),
                content: const Text('别人就能选它了。读到哪页会清掉。'),
                actions: [
                  TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('取消')),
                  FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('退出')),
                ],
              ),
            );
            if (yes == true && context.mounted) {
              Navigator.pop(context);
              app.release();
            }
          },
        ),
      ]);
    });
