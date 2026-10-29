import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../data/models.dart';
import '../main.dart';

/// 选角色：大块的角色牌，点一下从底部确认。别人已认领的锁住。
class RolePickScreen extends StatelessWidget {
  const RolePickScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final scheme = Theme.of(context).colorScheme;
    final text = Theme.of(context).textTheme;
    final count = app.catalog.roles.length;
    // 4 人一排；5、6 人排 3 列两行，不在第二行留一排空格；更多人最多 4 列。
    final columns = count <= 4 ? math.max(count, 1) : math.min((count + 1) ~/ 2, 4);
    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(40, 36, 40, 24),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [
            Expanded(child: Text('《${app.catalog.storyTitle}》', style: text.titleMedium?.copyWith(color: scheme.primary))),
            TextButton.icon(
              icon: const Icon(Icons.qr_code_scanner_rounded, size: 18),
              label: const Text('换电脑'),
              onPressed: () => app.disconnect(),
            ),
          ]),
          const SizedBox(height: 6),
          Text('选你的角色', style: text.displaySmall?.copyWith(fontWeight: FontWeight.w600)),
          const SizedBox(height: 8),
          Text('选好后这台平板只显示这个角色的本子。', style: text.titleMedium?.copyWith(color: scheme.onSurfaceVariant)),
          const SizedBox(height: 28),
          Expanded(
            child: Align(
              alignment: Alignment.topLeft,
              child: ConstrainedBox(
                // 每张牌最宽 260，和以前一样大
                constraints: BoxConstraints(maxWidth: columns * 260 + (columns - 1) * 16),
                child: GridView.count(
                  crossAxisCount: columns,
                  mainAxisSpacing: 16,
                  crossAxisSpacing: 16,
                  childAspectRatio: 1.2, // 4–6 个角色在 iPad 横屏一屏放下，不用滚
                  children: [for (final r in app.catalog.roles) _RoleTile(role: r, taken: app.catalog.claimedRoles.contains(r.roleId))],
                ),
              ),
            ),
          ),
        ]),
      ),
    );
  }
}

class _RoleTile extends StatelessWidget {
  const _RoleTile({required this.role, required this.taken});
  final Role role;
  final bool taken;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final text = Theme.of(context).textTheme;
    // 名字的最后一个字当头像：真实剧本多是两三个字的人名，合成剧本是「合成角色 N」。
    final mark = role.displayName.characters.last;
    // 已认领不用半透明压暗（会把字的对比度压到不达标），换底色和锁图标区分。
    return AnimatedSwitcher(
      duration: const Duration(milliseconds: 200),
      child: Material(
        key: ValueKey(taken),
        color: taken ? scheme.surfaceContainerLow : scheme.surfaceContainerHigh,
        borderRadius: BorderRadius.circular(24),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: taken ? null : () => _confirm(context),
          child: Padding(
            padding: const EdgeInsets.all(20),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Expanded(
                child: Center(
                  child: CircleAvatar(
                    radius: 40,
                    backgroundColor: taken ? scheme.surfaceContainerHighest : scheme.primaryContainer,
                    child: taken
                        ? Icon(Icons.lock_outline_rounded, size: 36, color: scheme.onSurfaceVariant)
                        : Text(mark, style: TextStyle(fontSize: 40, fontWeight: FontWeight.w600, color: scheme.onPrimaryContainer)),
                  ),
                ),
              ),
              Text(role.displayName, style: text.titleLarge?.copyWith(fontWeight: FontWeight.w600)),
              const SizedBox(height: 4),
              Text(taken ? '已被认领' : '可以选', style: text.bodyMedium?.copyWith(color: taken ? scheme.onSurfaceVariant : scheme.primary)),
            ]),
          ),
        ),
      ),
    );
  }

  Future<void> _confirm(BuildContext context) async {
    final app = AppScope.of(context);
    final messenger = ScaffoldMessenger.of(context);
    final go = await showModalBottomSheet<bool>(
      context: context,
      builder: (context) => Padding(
        padding: const EdgeInsets.fromLTRB(32, 0, 32, 32),
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Text('以「${role.displayName}」加入？', style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w600)),
          const SizedBox(height: 10),
          Text('选错了可以在角色头像里退出重选，或请主持人在电脑上释放。',
              style: Theme.of(context).textTheme.bodyLarge?.copyWith(color: Theme.of(context).colorScheme.onSurfaceVariant)),
          const SizedBox(height: 24),
          FilledButton(
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(56)),
            onPressed: () => Navigator.pop(context, true),
            child: const Text('就是我'),
          ),
        ]),
      ),
    );
    if (go != true) return;
    final ok = await app.claim(role.roleId);
    if (!ok) {
      HapticFeedback.heavyImpact();
      messenger.showSnackBar(SnackBar(content: Text('「${role.displayName}」刚被别人选走了，换一个吧')));
    }
  }
}
