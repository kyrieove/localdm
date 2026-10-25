import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../data/app_state.dart';
import '../data/models.dart';
import '../main.dart';
import 'reader.dart';

/// 线索卡：按分组排成卡墙。宽屏（≥840，iPad 横屏）左墙右详情；窄屏点卡全屏打开（Hero 飞过去）。
class CardsScreen extends StatefulWidget {
  const CardsScreen({super.key});

  @override
  State<CardsScreen> createState() => _CardsScreenState();
}

class _CardsScreenState extends State<CardsScreen> {
  String? _selected;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final cards = app.catalog.cards;
    return LayoutBuilder(builder: (context, c) {
      final twoPane = c.maxWidth >= 840;
      final selected = cards.where((x) => x.cardId == _selected && x.isAvailable).firstOrNull ?? (twoPane ? cards.firstWhere((x) => x.isAvailable) : null);
      void open(ClueCard card) {
        if (twoPane) {
          setState(() => _selected = card.cardId);
        } else {
          Navigator.of(context).push(PageRouteBuilder(
            opaque: false,
            transitionDuration: const Duration(milliseconds: 300),
            reverseTransitionDuration: const Duration(milliseconds: 240),
            pageBuilder: (context, a, _) => FadeTransition(opacity: a, child: CardViewer(card: card)),
          ));
        }
      }

      final wall = _CardWall(cards: cards, selectedId: twoPane ? selected?.cardId : null, onOpen: open, columns: twoPane ? 2 : 3);
      if (!twoPane) return wall;
      return Row(children: [
        SizedBox(width: math.min(440, c.maxWidth * 0.4), child: wall),
        VerticalDivider(width: 1, color: Theme.of(context).colorScheme.outlineVariant),
        Expanded(
          child: selected == null
              ? const SizedBox()
              : Padding(
                  padding: const EdgeInsets.fromLTRB(32, 24, 32, 24),
                  child: _CardDetail(key: ValueKey(selected.cardId), card: selected),
                ),
        ),
      ]);
    });
  }
}

class _CardWall extends StatelessWidget {
  const _CardWall({required this.cards, required this.selectedId, required this.onOpen, required this.columns});
  final List<ClueCard> cards;
  final String? selectedId;
  final ValueChanged<ClueCard> onOpen;
  final int columns;

  @override
  Widget build(BuildContext context) {
    final groups = <String, List<ClueCard>>{};
    for (final c in cards) {
      (groups[c.groupName] ??= []).add(c);
    }
    final scheme = Theme.of(context).colorScheme;
    return CustomScrollView(slivers: [
      SliverPadding(
        padding: const EdgeInsets.fromLTRB(24, 24, 72, 8),
        sliver: SliverToBoxAdapter(
          child: Text('线索卡 · ${cards.length} 张', style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w600)),
        ),
      ),
      for (final MapEntry(key: group, value: list) in groups.entries) ...[
        SliverPadding(
          padding: const EdgeInsets.fromLTRB(24, 16, 24, 10),
          sliver: SliverToBoxAdapter(
            child: Row(children: [
              Text(group, style: TextStyle(color: scheme.primary, fontWeight: FontWeight.w600, fontSize: 15)),
              const SizedBox(width: 8),
              Text('${list.length}', style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 14)),
              if (list.any((c) => !c.isAvailable)) ...[
                const SizedBox(width: 10),
                Icon(Icons.pause_circle_outline_rounded, size: 16, color: scheme.onSurfaceVariant),
                const SizedBox(width: 4),
                Text('这组暂停分发', style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 14)),
              ],
            ]),
          ),
        ),
        SliverPadding(
          padding: const EdgeInsets.symmetric(horizontal: 24),
          sliver: SliverGrid.count(
            crossAxisCount: columns,
            mainAxisSpacing: 14,
            crossAxisSpacing: 14,
            childAspectRatio: 996 / 648 * 0.86,
            children: [for (final c in list) _CardTile(card: c, selected: c.cardId == selectedId, onTap: () => onOpen(c))],
          ),
        ),
      ],
      const SliverToBoxAdapter(child: SizedBox(height: 32)),
    ]);
  }
}

class _CardTile extends StatelessWidget {
  const _CardTile({required this.card, required this.selected, required this.onTap});
  final ClueCard card;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final scheme = Theme.of(context).colorScheme;
    return Semantics(
      button: true,
      enabled: card.isAvailable,
      label: '${card.displayName}${card.isAvailable ? '' : '，暂停分发'}',
      child: GestureDetector(
        onTap: card.isAvailable ? onTap : null,
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Expanded(
            child: AnimatedContainer(
              duration: const Duration(milliseconds: 160),
              decoration: BoxDecoration(
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: selected ? scheme.primary : Colors.transparent, width: 3),
              ),
              child: ClipRRect(
                borderRadius: BorderRadius.circular(9),
                child: card.isAvailable
                    ? Hero(
                        tag: card.cardId,
                        child: nightFilter(app.night, Image(image: ResizeImage(AssetImage('assets/fake/${card.cardId}_front.jpg'), width: 480), fit: BoxFit.cover)),
                      )
                    : Container(
                        color: scheme.surfaceContainerHigh,
                        alignment: Alignment.center,
                        child: Icon(Icons.pause_rounded, color: scheme.onSurfaceVariant, size: 32),
                      ),
              ),
            ),
          ),
          const SizedBox(height: 6),
          Row(children: [
            Expanded(
              child: Text(card.displayName,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(fontSize: 15, color: card.isAvailable ? scheme.onSurface : scheme.onSurfaceVariant, fontWeight: selected ? FontWeight.w700 : FontWeight.w400)),
            ),
            Text('${card.sourceOrder}', style: TextStyle(fontSize: 13, color: scheme.onSurfaceVariant)),
          ]),
        ]),
      ),
    );
  }
}

/// 宽屏右侧的卡片详情：大图 + 翻面按钮，长按也能翻。
class _CardDetail extends StatelessWidget {
  const _CardDetail({super.key, required this.card});
  final ClueCard card;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Padding(
        padding: const EdgeInsets.only(right: 48),
        child: Text(card.displayName, style: Theme.of(context).textTheme.headlineMedium?.copyWith(fontWeight: FontWeight.w600)),
      ),
      Text('${card.groupName} · 第 ${card.sourceOrder} 张', style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 15)),
      const SizedBox(height: 20),
      Expanded(child: Center(child: FlipCard(card: card))),
    ]);
  }
}

/// 一张能翻面的卡：长按或点按钮翻，带 3D 转动与震动；没有卡背时按钮不可用并说明。
class FlipCard extends StatefulWidget {
  const FlipCard({super.key, required this.card, this.zoomable = false});
  final ClueCard card;
  final bool zoomable;

  @override
  State<FlipCard> createState() => _FlipCardState();
}

class _FlipCardState extends State<FlipCard> with SingleTickerProviderStateMixin {
  late final _flip = AnimationController(vsync: this, duration: const Duration(milliseconds: 360));

  bool get _hasBack => AppState.cardsWithBack.contains(widget.card.cardId);

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    // 打开卡就先解码背面，翻到一半时背面已就绪，不闪空白。
    if (_hasBack) precacheImage(pageImage('assets/fake/${widget.card.cardId}_back.jpg'), context);
  }

  @override
  void dispose() {
    _flip.dispose();
    super.dispose();
  }

  void _toggle() {
    if (!_hasBack) {
      HapticFeedback.lightImpact();
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('这张卡没有背面')));
      return;
    }
    HapticFeedback.mediumImpact();
    _flip.isCompleted || _flip.velocity > 0 ? _flip.reverse() : _flip.forward();
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final id = widget.card.cardId;
    Widget face(String side) => ClipRRect(
          borderRadius: BorderRadius.circular(16),
          child: nightFilter(app.night, Image(image: pageImage('assets/fake/${id}_$side.jpg'), fit: BoxFit.contain)),
        );

    final card = GestureDetector(
      onLongPress: _toggle,
      child: AnimatedBuilder(
        animation: _flip,
        builder: (context, _) {
          final t = Curves.easeInOutCubic.transform(_flip.value);
          final back = t > 0.5;
          return Transform(
            alignment: Alignment.center,
            transform: Matrix4.identity()
              ..setEntry(3, 2, 0.0012)
              ..rotateY(math.pi * t),
            child: back ? Transform(alignment: Alignment.center, transform: Matrix4.rotationY(math.pi), child: face('back')) : widget.zoomable
                    ? Hero(tag: id, child: face('front')) // 只有全屏看卡才和卡墙配对飞过去，宽屏详情同屏有同 tag 会冲突
                    : face('front'),
          );
        },
      ),
    );

    return Column(mainAxisSize: MainAxisSize.min, children: [
      Flexible(
        child: AspectRatio(
          aspectRatio: 996 / 648,
          child: widget.zoomable ? InteractiveViewer(maxScale: 4, child: card) : card,
        ),
      ),
      const SizedBox(height: 20),
      AnimatedBuilder(
        animation: _flip,
        builder: (context, _) => FilledButton.tonalIcon(
          style: FilledButton.styleFrom(minimumSize: const Size(200, 52)),
          onPressed: _toggle,
          icon: const Icon(Icons.flip_rounded),
          label: Text(!_hasBack ? '没有背面' : (_flip.value > 0.5 ? '看正面' : '翻到背面')),
        ),
      ),
      const SizedBox(height: 8),
      Text('长按卡片也能翻面', style: TextStyle(color: Theme.of(context).colorScheme.onSurfaceVariant, fontSize: 14)),
    ]);
  }
}

/// 窄屏的全屏看卡：黑底，可缩放；往下拖关闭。
class CardViewer extends StatefulWidget {
  const CardViewer({super.key, required this.card});
  final ClueCard card;

  @override
  State<CardViewer> createState() => _CardViewerState();
}

class _CardViewerState extends State<CardViewer> {
  double _drag = 0;

  @override
  Widget build(BuildContext context) {
    final fade = (1 - _drag.abs() / 400).clamp(0.3, 1.0);
    return GestureDetector(
      onVerticalDragUpdate: (d) => setState(() => _drag += d.delta.dy),
      onVerticalDragEnd: (d) {
        if (_drag.abs() > 120 || d.velocity.pixelsPerSecond.dy.abs() > 900) {
          Navigator.pop(context);
        } else {
          setState(() => _drag = 0);
        }
      },
      child: Material(
        color: Colors.black.withValues(alpha: 0.92 * fade),
        child: SafeArea(
          child: Stack(children: [
            Transform.translate(
              offset: Offset(0, _drag),
              child: Padding(padding: const EdgeInsets.all(32), child: Center(child: FlipCard(card: widget.card, zoomable: true))),
            ),
            Positioned(
              top: 8,
              left: 8,
              child: IconButton(
                tooltip: '关闭',
                color: Colors.white,
                icon: const Icon(Icons.close_rounded, size: 28),
                onPressed: () => Navigator.pop(context),
              ),
            ),
          ]),
        ),
      ),
    );
  }
}
