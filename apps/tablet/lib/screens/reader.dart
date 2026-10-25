import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../data/app_state.dart';
import '../main.dart';
import 'sheets.dart';

/// 原稿解码宽度上限：iPad Air 4 竖屏整页显示约 535pt 宽 ×2 倍屏 ≈ 1070px，
/// 再大只在放大时有用；真实扫描页 1754 宽解码要 17MB/页，压到 1100 后约 7MB。
const decodeWidth = 1100;

ImageProvider pageImage(String asset) => ResizeImage(AssetImage(asset), width: decodeWidth);

/// 夜读：原稿是白纸黑字，暗房间里刺眼。压暗＝整体降亮偏暖；反白＝纸变深、字变浅。
const identityMatrix = <double>[1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];
const dimMatrix = <double>[0.66, 0, 0, 0, 0, 0, 0.60, 0, 0, 0, 0, 0, 0.50, 0, 0, 0, 0, 0, 1, 0];
// 反白后纸 ≈ (30,26,22) 暖近黑、字 ≈ (197,187,165) 暖米色，不发蓝。
const invertMatrix = <double>[-0.86, 0, 0, 0, 242, 0, -0.82, 0, 0, 224, 0, 0, -0.74, 0, 194, 0, 0, 0, 1, 0];

Widget nightFilter(NightMode mode, Widget child) => switch (mode) {
      NightMode.off => child,
      NightMode.dim => ColorFiltered(colorFilter: const ColorFilter.matrix(dimMatrix), child: child),
      NightMode.invert => ColorFiltered(colorFilter: const ColorFilter.matrix(invertMatrix), child: child),
    };

class RoleReader extends StatelessWidget {
  const RoleReader({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    return AnimatedSwitcher(
      duration: const Duration(milliseconds: 220),
      child: app.textMode
          ? const TextReader(key: ValueKey('text'))
          : PageReader(
              key: const ValueKey('pages'),
              title: '${app.myRole?.displayName ?? ''} 的角色本',
              pageCount: AppState.rolePageCount,
              page: app.page,
              onPage: app.setPage,
              asset: (n) => 'assets/fake/role_$n.jpg',
              allowText: true,
            ),
    );
  }
}

class HandbookReader extends StatelessWidget {
  const HandbookReader({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    return PageReader(
      title: '组织者手册',
      pageCount: AppState.handbookPageCount,
      page: app.handbookPage,
      onPage: app.setHandbookPage,
      asset: (n) => 'assets/fake/handbook_$n.jpg',
      allowText: false,
    );
  }
}

/// 原稿翻页：竖屏一页、横屏两页并排。左右两边轻点翻页，中间轻点收起/展开控件，
/// 双击放大到点的位置，双指缩放；放大时不翻页，只平移。
class PageReader extends StatefulWidget {
  const PageReader({
    super.key,
    required this.title,
    required this.pageCount,
    required this.page,
    required this.onPage,
    required this.asset,
    required this.allowText,
  });

  final String title;
  final int pageCount;
  final int page;
  final ValueChanged<int> onPage;
  final String Function(int page) asset;
  final bool allowText;

  @override
  State<PageReader> createState() => _PageReaderState();
}

class _PageReaderState extends State<PageReader> with TickerProviderStateMixin {
  PageController? _pages;
  bool? _spread;
  final _zoom = TransformationController();
  late final _zoomAnim = AnimationController(vsync: this, duration: const Duration(milliseconds: 220));
  Animation<Matrix4>? _zoomTween;
  Offset _doubleTapAt = Offset.zero;
  bool _zoomed = false;
  bool _seeking = false;

  @override
  void initState() {
    super.initState();
    _zoomAnim.addListener(() => _zoom.value = _zoomTween!.value);
    _zoom.addListener(() {
      final z = _zoom.value.getMaxScaleOnAxis() > 1.01;
      if (z != _zoomed) setState(() => _zoomed = z);
    });
  }

  @override
  void dispose() {
    _pages?.dispose();
    _zoom.dispose();
    _zoomAnim.dispose();
    super.dispose();
  }

  int _viewOf(int page) => _spread! ? (page - 1) ~/ 2 : page - 1;
  int _firstPageOf(int view) => _spread! ? view * 2 + 1 : view + 1;
  int get _viewCount => _spread! ? (widget.pageCount + 1) ~/ 2 : widget.pageCount;

  void _precache(int page) {
    final app = AppScope.of(context);
    for (var p = page - 2; p <= page + 2; p++) {
      if (p >= 1 && p <= widget.pageCount && app.pageCached(p)) precacheImage(pageImage(widget.asset(p)), context);
    }
  }

  void _turn(int delta) {
    final target = _viewOf(widget.page) + delta;
    if (target < 0 || target >= _viewCount) {
      HapticFeedback.lightImpact(); // 到头了：轻震一下，不弹提示
      return;
    }
    _pages!.animateToPage(target, duration: const Duration(milliseconds: 260), curve: Curves.easeOutCubic);
  }

  void _toggleZoom() {
    final Matrix4 end;
    if (_zoomed) {
      end = Matrix4.identity();
    } else {
      const s = 2.5;
      end = Matrix4.identity()
        ..translateByDouble(-_doubleTapAt.dx * (s - 1), -_doubleTapAt.dy * (s - 1), 0, 1)
        ..scaleByDouble(s, s, 1, 1);
    }
    _zoomTween = Matrix4Tween(begin: _zoom.value, end: end).animate(CurvedAnimation(parent: _zoomAnim, curve: Curves.easeOutCubic));
    _zoomAnim.forward(from: 0);
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    return LayoutBuilder(builder: (context, c) {
      final spread = c.maxWidth > c.maxHeight * 1.1;
      if (spread != _spread) {
        // 转屏：保持在同一页，换成单页/双页的翻页器。
        _spread = spread;
        _pages?.dispose();
        _pages = PageController(initialPage: _viewOf(widget.page));
        _zoom.value = Matrix4.identity();
      }
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _precache(widget.page);
      });
      final chrome = !app.immersive;
      return ColoredBox(
        color: Theme.of(context).colorScheme.surfaceContainerLowest,
        child: Stack(children: [
          PageView.builder(
            controller: _pages,
            itemCount: _viewCount,
            physics: _zoomed ? const NeverScrollableScrollPhysics() : const PageScrollPhysics(),
            onPageChanged: (v) {
              _zoom.value = Matrix4.identity();
              widget.onPage(_firstPageOf(v));
              if (!_seeking) app.toggleImmersive(true); // 一翻页就收起控件，像实体书；拖滑条时不收
            },
            itemBuilder: (context, v) {
              final first = _firstPageOf(v);
              final pages = [first, if (spread && first + 1 <= widget.pageCount) first + 1];
              return GestureDetector(
                onTap: app.toggleImmersive,
                onDoubleTapDown: (d) => _doubleTapAt = d.localPosition,
                onDoubleTap: _toggleZoom,
                child: InteractiveViewer(
                  transformationController: v == _viewOf(widget.page) ? _zoom : null,
                  maxScale: 4,
                  child: Padding(
                    padding: EdgeInsets.symmetric(vertical: 12, horizontal: spread ? 16 : 8),
                    child: Row(mainAxisAlignment: MainAxisAlignment.center, children: [
                      for (final p in pages)
                        Flexible(
                          child: Padding(
                            padding: const EdgeInsets.symmetric(horizontal: 4),
                            child: _Page(asset: widget.asset(p), page: p, available: app.pageCached(p), night: app.night),
                          ),
                        ),
                    ]),
                  ),
                ),
              );
            },
          ),
          // 左右翻页区：各占 18% 宽；放大时让给平移。
          if (!_zoomed) ...[
            Positioned(left: 0, top: 0, bottom: 0, width: c.maxWidth * 0.18, child: GestureDetector(behavior: HitTestBehavior.opaque, onTap: () => _turn(-1))),
            Positioned(right: 0, top: 0, bottom: 0, width: c.maxWidth * 0.18, child: GestureDetector(behavior: HitTestBehavior.opaque, onTap: () => _turn(1))),
          ],
          _ReaderChrome(
            visible: chrome,
            title: widget.title,
            page: widget.page,
            pageCount: widget.pageCount,
            spread: spread,
            allowText: widget.allowText,
            onSeek: (p) {
              _seeking = true;
              _pages!.jumpToPage(_viewOf(p));
              _seeking = false;
            },
          ),
        ]),
      );
    });
  }
}

class _Page extends StatelessWidget {
  const _Page({required this.asset, required this.page, required this.available, required this.night});
  final String asset;
  final int page;
  final bool available;
  final NightMode night;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return AspectRatio(
      aspectRatio: 821 / 1259,
      child: available
          ? DecoratedBox(
              decoration: BoxDecoration(boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.35), blurRadius: 18, offset: const Offset(0, 6))]),
              child: nightFilter(night, Image(image: pageImage(asset), fit: BoxFit.contain, gaplessPlayback: true)),
            )
          : Container(
              color: scheme.surfaceContainer,
              alignment: Alignment.center,
              padding: const EdgeInsets.all(24),
              child: Column(mainAxisSize: MainAxisSize.min, children: [
                Icon(Icons.cloud_off_rounded, color: scheme.onSurfaceVariant, size: 36),
                const SizedBox(height: 12),
                Text('第 $page 页还没下载\n连上电脑后自动出现', textAlign: TextAlign.center, style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 15, height: 1.6)),
              ]),
            ),
    );
  }
}

/// 阅读控件：顶部书名与页码、原稿/文字切换、阅读设置；底部页码滑条。浮在原稿上，轻点中间收起。
class _ReaderChrome extends StatelessWidget {
  const _ReaderChrome({
    required this.visible,
    required this.title,
    required this.page,
    required this.pageCount,
    required this.spread,
    required this.allowText,
    required this.onSeek,
  });

  final bool visible;
  final String title;
  final int page;
  final int pageCount;
  final bool spread;
  final bool allowText;
  final ValueChanged<int> onSeek;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final scheme = Theme.of(context).colorScheme;
    final pageLabel = spread && page + 1 <= pageCount ? '第 $page–${page + 1} 页 · 共 $pageCount 页' : '第 $page 页 · 共 $pageCount 页';
    final bar = scheme.surfaceContainer.withValues(alpha: 0.96);
    return IgnorePointer(
      ignoring: !visible,
      child: AnimatedOpacity(
        opacity: visible ? 1 : 0,
        duration: const Duration(milliseconds: 180),
        child: Column(children: [
          Material(
            color: bar,
            child: SafeArea(
              bottom: false,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(24, 10, 64, 10),
                child: Row(children: [
                  Expanded(
                    child: Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
                      Text(title, style: Theme.of(context).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w600), overflow: TextOverflow.ellipsis),
                      Text(pageLabel, style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 14)),
                    ]),
                  ),
                  if (allowText) const ModeSwitch(),
                  const SizedBox(width: 8),
                  IconButton(tooltip: '阅读设置', icon: const Icon(Icons.text_fields_rounded), onPressed: () => showReadingSheet(context)),
                ]),
              ),
            ),
          ),
          const Spacer(),
          Material(
            color: bar,
            child: SafeArea(
              top: false,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(20, 6, 20, 6),
                child: Row(children: [
                  Text('$page', style: TextStyle(color: scheme.onSurfaceVariant, fontFeatures: const [FontFeature.tabularFigures()])),
                  Expanded(
                    child: Slider(
                      value: page.toDouble(),
                      min: 1,
                      max: pageCount.toDouble(),
                      divisions: pageCount - 1,
                      label: '第 $page 页',
                      onChanged: (v) => onSeek(v.round()),
                    ),
                  ),
                  Text('$pageCount', style: TextStyle(color: scheme.onSurfaceVariant)),
                  const SizedBox(width: 8),
                  IconButton(
                    tooltip: '收起控件',
                    icon: const Icon(Icons.fullscreen_rounded),
                    onPressed: () => app.toggleImmersive(true),
                  ),
                ]),
              ),
            ),
          ),
        ]),
      ),
    );
  }
}

class ModeSwitch extends StatelessWidget {
  const ModeSwitch({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    return SegmentedButton<bool>(
      showSelectedIcon: false,
      segments: const [
        ButtonSegment(value: false, label: Text('原稿')),
        ButtonSegment(value: true, label: Text('文字')),
      ],
      selected: {app.textMode},
      onSelectionChanged: (v) => app.update(() => app.textMode = v.first),
    );
  }
}

/// 文字重排：字号行距可调，一行不超过约 34 个字。长按一段跳到它所在的原稿页核对。
class TextReader extends StatelessWidget {
  const TextReader({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final scheme = Theme.of(context).colorScheme;
    final body = TextStyle(fontSize: app.fontSize, height: app.lineSpacing, color: scheme.onSurface);
    return Column(children: [
      Material(
        color: scheme.surfaceContainer,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(24, 10, 64, 10),
          child: Row(children: [
            Expanded(
              child: Text('${app.myRole?.displayName ?? ''} 的角色本 · 文字版',
                  style: Theme.of(context).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w600)),
            ),
            const ModeSwitch(),
            const SizedBox(width: 8),
            IconButton(tooltip: '阅读设置', icon: const Icon(Icons.text_fields_rounded), onPressed: () => showReadingSheet(context)),
          ]),
        ),
      ),
      Expanded(
        child: ListView(
          padding: const EdgeInsets.symmetric(vertical: 40, horizontal: 32),
          children: [
            for (final ch in app.book.chapters)
              Center(
                child: ConstrainedBox(
                  constraints: BoxConstraints(maxWidth: app.fontSize * 34),
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    for (final b in ch.blocks)
                      if (b.kind == 'ChapterTitle')
                        Padding(
                          padding: const EdgeInsets.only(top: 24, bottom: 16),
                          child: Text(b.text, style: TextStyle(fontSize: app.fontSize * 1.5, fontWeight: FontWeight.w700, color: scheme.onSurface)),
                        )
                      else
                        GestureDetector(
                          onLongPress: b.sourcePageNumber == null
                              ? null
                              : () {
                                  HapticFeedback.mediumImpact();
                                  app.update(() {
                                    app.textMode = false;
                                    app.page = b.sourcePageNumber!;
                                  });
                                },
                          child: Padding(
                            padding: const EdgeInsets.only(bottom: 18),
                            child: Text(b.text, style: body),
                          ),
                        ),
                  ]),
                ),
              ),
            Padding(
              padding: const EdgeInsets.only(top: 24),
              child: Text('长按任一段，跳到它所在的原稿页核对', textAlign: TextAlign.center, style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 14)),
            ),
          ],
        ),
      ),
    ]);
  }
}
