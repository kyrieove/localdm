import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../data/app_state.dart';
import '../main.dart';
import 'sheets.dart';

/// 原稿解码宽度上限：iPad Air 4 竖屏整页显示约 535pt 宽 ×2 倍屏 ≈ 1070px，
/// 再大只在放大时有用；真实扫描页 1754 宽解码要 17MB/页，压到 1100 后约 7MB。
const decodeWidth = 1100;

ImageProvider pageImage(ImageProvider image) => ResizeImage(image, width: decodeWidth);

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

/// 按住翻开要按满的时长，与网页端 page-flip.js 的 HOLD_MS 一致。
const holdToOpen = Duration(milliseconds: 650);

class RoleReader extends StatelessWidget {
  const RoleReader({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    return AnimatedSwitcher(
      duration: const Duration(milliseconds: 220),
      child: app.textMode || app.rolePageCount == 0
          ? const TextReader(key: ValueKey('text'))
          : PageReader(
              key: const ValueKey('pages'),
              title: '${app.myRole?.displayName ?? ''} 的角色本',
              pageCount: app.rolePageCount,
              page: app.page,
              onPage: app.setPage,
              asset: (n) => app.image('/tablet/role/source/$n', 'assets/fake/role_$n.jpg'),
              allowText: true,
              openedPages: app.openedPages,
              onOpen: app.openPage,
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
      pageCount: app.handbookPageCount,
      page: app.handbookPage,
      onPage: app.setHandbookPage,
      asset: (n) => app.image('/tablet/handbook/source/$n', 'assets/fake/handbook_$n.jpg'),
      allowText: false,
    );
  }
}

/// 原稿翻页：竖屏一页、横屏两页并排。左右两边轻点翻页。
/// 竖屏：中间轻点开关控件，双击/双指放大这一页。
/// 横屏：点哪一页就单独全屏看哪一页（不会连带放大旁边那页），点两页之外的空白开关控件。
/// 角色本的页一开始盖着，按住下一页约 0.65 秒才翻开，只能按顺序翻。
class PageReader extends StatefulWidget {
  const PageReader({
    super.key,
    required this.title,
    required this.pageCount,
    required this.page,
    required this.onPage,
    required this.asset,
    required this.allowText,
    this.openedPages,
    this.onOpen,
  });

  final String title;
  final int pageCount;
  final int page;
  final ValueChanged<int> onPage;
  final ImageProvider Function(int page) asset;
  final bool allowText;
  final int? openedPages; // null：不盖页（手册）
  final bool Function(int page)? onOpen;

  @override
  State<PageReader> createState() => _PageReaderState();
}

class _PageReaderState extends State<PageReader> {
  PageController? _pages;
  bool? _spread;
  bool _zoomed = false;
  bool _seeking = false;

  @override
  void dispose() {
    _pages?.dispose();
    super.dispose();
  }

  int _viewOf(int page) => _spread! ? (page - 1) ~/ 2 : page - 1;
  int _firstPageOf(int view) => _spread! ? view * 2 + 1 : view + 1;
  int get _viewCount => _spread! ? (widget.pageCount + 1) ~/ 2 : widget.pageCount;
  bool _covered(int p) => widget.openedPages != null && p > widget.openedPages!;

  void _precache(int page) {
    final app = AppScope.of(context);
    for (var p = page - 2; p <= page + 2; p++) {
      if (p >= 1 && p <= widget.pageCount && app.pageCached(p) && !_covered(p)) precacheImage(pageImage(widget.asset(p)), context);
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

  void _openViewer(int p) {
    Navigator.of(context).push(PageRouteBuilder(
      opaque: false,
      transitionDuration: const Duration(milliseconds: 260),
      reverseTransitionDuration: const Duration(milliseconds: 200),
      pageBuilder: (context, a, _) => FadeTransition(opacity: a, child: PageViewer(asset: widget.asset(p), page: p)),
    ));
  }

  Widget _pageOrCover(AppState app, int p, {required bool spread}) {
    final Widget child;
    if (_covered(p)) {
      child = CoverPage(
        key: ValueKey('cover-$p'),
        page: p,
        isNext: p == widget.openedPages! + 1,
        nextToOpen: widget.openedPages! + 1,
        onOpen: () => widget.onOpen!(p),
      );
    } else {
      final page = _Page(key: ValueKey('page-$p'), asset: widget.asset(p), page: p, available: app.pageCached(p), night: app.night);
      child = spread ? GestureDetector(onTap: () => _openViewer(p), child: page) : page;
    }
    // 翻开那一下：盖页淡出、原稿轻微放大淡入。
    return AnimatedSwitcher(
      duration: const Duration(milliseconds: 320),
      transitionBuilder: (c, a) => FadeTransition(opacity: a, child: ScaleTransition(scale: Tween(begin: 0.96, end: 1.0).animate(a), child: c)),
      child: child,
    );
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
        _zoomed = false;
      }
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _precache(widget.page);
      });
      final view = _viewOf(widget.page);
      return ColoredBox(
        color: Theme.of(context).colorScheme.surfaceContainerLowest,
        child: Stack(children: [
          PageView.builder(
            controller: _pages,
            itemCount: _viewCount,
            physics: _zoomed ? const NeverScrollableScrollPhysics() : const PageScrollPhysics(),
            onPageChanged: (v) {
              _zoomed = false;
              widget.onPage(_firstPageOf(v));
              if (!_seeking) app.toggleImmersive(true); // 一翻页就收起控件，像实体书；拖滑条时不收
            },
            itemBuilder: (context, v) {
              final first = _firstPageOf(v);
              if (spread) {
                final pages = [first, if (first + 1 <= widget.pageCount) first + 1];
                return GestureDetector(
                  onTap: app.toggleImmersive, // 只有点到两页之外的空白才会到这里
                  child: Padding(
                    padding: const EdgeInsets.symmetric(vertical: 12, horizontal: 16),
                    child: Row(mainAxisAlignment: MainAxisAlignment.center, children: [
                      for (final p in pages)
                        Flexible(child: Padding(padding: const EdgeInsets.symmetric(horizontal: 4), child: _pageOrCover(app, p, spread: true))),
                    ]),
                  ),
                );
              }
              return _ZoomablePage(
                onTap: app.toggleImmersive,
                onZoomChanged: (z) {
                  if (z != _zoomed) setState(() => _zoomed = z);
                },
                zoomEnabled: !_covered(first),
                child: Padding(padding: const EdgeInsets.symmetric(vertical: 12, horizontal: 8), child: Center(child: _pageOrCover(app, first, spread: false))),
              );
            },
          ),
          // 左右翻页区：各占 18% 宽；放大时让给平移。
          if (!_zoomed) ...[
            Positioned(left: 0, top: 0, bottom: 0, width: c.maxWidth * 0.18, child: GestureDetector(behavior: HitTestBehavior.opaque, onTap: () => _turn(-1))),
            Positioned(right: 0, top: 0, bottom: 0, width: c.maxWidth * 0.18, child: GestureDetector(behavior: HitTestBehavior.opaque, onTap: () => _turn(1))),
          ],
          _ReaderChrome(
            visible: !app.immersive,
            title: widget.title,
            view: view,
            viewCount: _viewCount,
            labelOf: (v) {
              final a = _firstPageOf(v), b = spread ? (a + 1).clamp(1, widget.pageCount) : a;
              return a == b ? '第 $a 页' : '第 $a–$b 页';
            },
            pageCount: widget.pageCount,
            allowText: widget.allowText,
            onSeek: (v) {
              // 滑条按「一屏」走：横屏一格是两页，所以 1→2、3→4 这种同一屏内的拖动不会被吞掉。
              _seeking = true;
              _pages!.jumpToPage(v);
              _seeking = false;
            },
          ),
        ]),
      );
    });
  }
}

/// 竖屏单页：双击放大到点击处 2.5 倍，双指 1–4 倍。
class _ZoomablePage extends StatefulWidget {
  const _ZoomablePage({required this.child, required this.onTap, required this.onZoomChanged, required this.zoomEnabled});
  final Widget child;
  final VoidCallback onTap;
  final ValueChanged<bool> onZoomChanged;
  final bool zoomEnabled;

  @override
  State<_ZoomablePage> createState() => _ZoomablePageState();
}

class _ZoomablePageState extends State<_ZoomablePage> with SingleTickerProviderStateMixin {
  final _zoom = TransformationController();
  late final _anim = AnimationController(vsync: this, duration: const Duration(milliseconds: 220));
  Animation<Matrix4>? _tween;
  Offset _at = Offset.zero;

  @override
  void initState() {
    super.initState();
    _anim.addListener(() => _zoom.value = _tween!.value);
    _zoom.addListener(() => widget.onZoomChanged(_zoom.value.getMaxScaleOnAxis() > 1.01));
  }

  @override
  void dispose() {
    _zoom.dispose();
    _anim.dispose();
    super.dispose();
  }

  void _toggle() {
    final zoomed = _zoom.value.getMaxScaleOnAxis() > 1.01;
    const s = 2.5;
    final end = zoomed
        ? Matrix4.identity()
        : (Matrix4.identity()
          ..translateByDouble(-_at.dx * (s - 1), -_at.dy * (s - 1), 0, 1)
          ..scaleByDouble(s, s, 1, 1));
    _tween = Matrix4Tween(begin: _zoom.value, end: end).animate(CurvedAnimation(parent: _anim, curve: Curves.easeOutCubic));
    _anim.forward(from: 0);
  }

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: widget.onTap,
      onDoubleTapDown: widget.zoomEnabled ? (d) => _at = d.localPosition : null,
      onDoubleTap: widget.zoomEnabled ? _toggle : null,
      child: InteractiveViewer(transformationController: _zoom, maxScale: 4, panEnabled: widget.zoomEnabled, scaleEnabled: widget.zoomEnabled, child: widget.child),
    );
  }
}

/// 横屏点开的单页全屏：黑底，只有这一页；双击/双指放大，下拉或点关闭回去。
class PageViewer extends StatefulWidget {
  const PageViewer({super.key, required this.asset, required this.page});
  final ImageProvider asset;
  final int page;

  @override
  State<PageViewer> createState() => _PageViewerState();
}

class _PageViewerState extends State<PageViewer> {
  double _drag = 0;
  bool _zoomed = false;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final fade = (1 - _drag.abs() / 400).clamp(0.3, 1.0);
    return GestureDetector(
      onVerticalDragUpdate: _zoomed ? null : (d) => setState(() => _drag += d.delta.dy),
      onVerticalDragEnd: _zoomed
          ? null
          : (d) {
              if (_drag.abs() > 120 || d.velocity.pixelsPerSecond.dy.abs() > 900) {
                Navigator.pop(context);
              } else {
                setState(() => _drag = 0);
              }
            },
      child: Material(
        color: Colors.black.withValues(alpha: 0.95 * fade),
        child: SafeArea(
          child: Stack(children: [
            Transform.translate(
              offset: Offset(0, _drag),
              child: _ZoomablePage(
                onTap: () {},
                zoomEnabled: true,
                onZoomChanged: (z) {
                  if (z != _zoomed) setState(() => _zoomed = z);
                },
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Center(child: _Page(asset: widget.asset, page: widget.page, available: true, night: app.night)),
                ),
              ),
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
            Positioned(
              top: 18,
              right: 24,
              child: Text('第 ${widget.page} 页', style: TextStyle(color: Colors.white.withValues(alpha: 0.75), fontSize: 15)),
            ),
          ]),
        ),
      ),
    );
  }
}

class _Page extends StatelessWidget {
  const _Page({super.key, required this.asset, required this.page, required this.available, required this.night});
  final ImageProvider asset;
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

/// 封条印章上的大写页码：1→壹，12→拾贰，23→贰拾叁；超过 99 直接用数字。
String chineseNumeral(int n) {
  const d = ['', '壹', '贰', '叁', '肆', '伍', '陆', '柒', '捌', '玖'];
  if (n < 1 || n > 99) return '$n';
  if (n < 10) return d[n];
  return '${n >= 20 ? d[n ~/ 10] : ''}拾${d[n % 10]}';
}

/// 盖着的原稿页。下一页：按住，圆环走满就翻开并震一下；中途松手或滑动就作废。
/// 更后面的页：点一下抖一抖，提示先翻哪一页。
class CoverPage extends StatefulWidget {
  const CoverPage({super.key, required this.page, required this.isNext, required this.nextToOpen, required this.onOpen});
  final int page;
  final bool isNext;
  final int nextToOpen;
  final VoidCallback onOpen;

  @override
  State<CoverPage> createState() => _CoverPageState();
}

class _CoverPageState extends State<CoverPage> with TickerProviderStateMixin {
  late final _hold = AnimationController(vsync: this, duration: holdToOpen)
    ..addStatusListener((s) {
      if (s == AnimationStatus.completed) {
        HapticFeedback.mediumImpact();
        widget.onOpen();
      }
    });
  late final _shake = AnimationController(vsync: this, duration: const Duration(milliseconds: 360));
  Offset? _down;

  @override
  void dispose() {
    _hold.dispose();
    _shake.dispose();
    super.dispose();
  }

  void _cancel() {
    if (!_hold.isCompleted) _hold.reverse();
    _down = null;
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final next = widget.isNext;
    // 封条：像没拆封的角色本，一道封条斜贴在页中间；按住时灯色从左往右把封条「烧」开。
    final band = Transform.rotate(
      angle: -4 * math.pi / 180,
      child: LayoutBuilder(
        // 封条比页宽一点，斜过来两端不露空角。
        builder: (context, box) => OverflowBox(
          maxWidth: box.maxWidth * 1.1,
          child: Container(
            width: box.maxWidth * 1.1,
            height: 64,
            decoration: BoxDecoration(
              color: next ? scheme.primaryContainer : scheme.surfaceContainerHighest,
              border: Border.symmetric(horizontal: BorderSide(color: next ? scheme.primary.withValues(alpha: 0.35) : scheme.outlineVariant)),
            ),
            child: Stack(fit: StackFit.expand, children: [
              AnimatedBuilder(
                animation: _hold,
                builder: (context, _) => FractionallySizedBox(
                  alignment: Alignment.centerLeft,
                  widthFactor: next ? _hold.value : 0,
                  child: DecoratedBox(decoration: BoxDecoration(gradient: LinearGradient(colors: [scheme.primary.withValues(alpha: 0.5), scheme.primary]))),
                ),
              ),
              Center(
                child: Text('第 ${widget.page} 页',
                    style: TextStyle(fontSize: 18, fontWeight: FontWeight.w600, letterSpacing: 6, color: next ? scheme.onPrimaryContainer : scheme.onSurfaceVariant)),
              ),
            ]),
          ),
        ),
      ),
    );
    final seal = Container(
      width: 58,
      height: 58,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        color: next ? scheme.primary : scheme.surfaceContainerHighest,
        boxShadow: const [BoxShadow(color: Colors.black38, blurRadius: 6, offset: Offset(0, 2))],
      ),
      child: Text(chineseNumeral(widget.page),
          style: TextStyle(fontSize: widget.page > 10 ? 17 : 22, fontWeight: FontWeight.w700, color: next ? scheme.onPrimary : scheme.outline)),
    );
    final face = AspectRatio(
      aspectRatio: 821 / 1259,
      child: Container(
        clipBehavior: Clip.antiAlias,
        decoration: BoxDecoration(
          gradient: LinearGradient(
              begin: Alignment.topLeft, end: Alignment.bottomRight, colors: [scheme.surfaceContainerHigh, scheme.surfaceContainer]),
          borderRadius: BorderRadius.circular(6),
          border: Border.all(color: scheme.outlineVariant),
        ),
        child: Stack(children: [
          // 内框细线：像角色本的版心
          Positioned.fill(
            child: Container(
              margin: const EdgeInsets.all(14),
              decoration: BoxDecoration(borderRadius: BorderRadius.circular(3), border: Border.all(color: scheme.primary.withValues(alpha: 0.14))),
            ),
          ),
          Align(alignment: const Alignment(0, -0.12), child: band),
          Align(alignment: const Alignment(0, -0.34), child: seal),
          Align(
            alignment: const Alignment(0, 0.76),
            child: Text(next ? '按住拆封' : '先拆第 ${widget.nextToOpen} 页', style: TextStyle(fontSize: 16, color: scheme.onSurfaceVariant)),
          ),
        ]),
      ),
    );

    if (!widget.isNext) {
      return GestureDetector(
        onTap: () {
          HapticFeedback.lightImpact();
          _shake.forward(from: 0);
        },
        child: AnimatedBuilder(
          animation: _shake,
          builder: (context, child) {
            final t = _shake.value;
            return Transform.translate(offset: Offset(8 * (1 - t) * math.sin(t * math.pi * 6), 0), child: child);
          },
          child: face,
        ),
      );
    }
    // Listener 不进手势竞争：按住的同时还能左右滑动翻页，一滑就作废这次按住。
    return Listener(
      onPointerDown: (e) {
        _down = e.position;
        _hold.forward(from: 0);
      },
      onPointerMove: (e) {
        if (_down != null && (e.position - _down!).distance > 12) _cancel();
      },
      onPointerUp: (_) => _cancel(),
      onPointerCancel: (_) => _cancel(),
      child: face,
    );
  }
}

/// 阅读控件：顶部书名与页码、原稿/文字切换、阅读设置；底部滑条。浮在原稿上，轻点收起。
class _ReaderChrome extends StatelessWidget {
  const _ReaderChrome({
    required this.visible,
    required this.title,
    required this.view,
    required this.viewCount,
    required this.labelOf,
    required this.pageCount,
    required this.allowText,
    required this.onSeek,
  });

  final bool visible;
  final String title;
  final int view;
  final int viewCount;
  final String Function(int view) labelOf;
  final int pageCount;
  final bool allowText;
  final ValueChanged<int> onSeek;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final scheme = Theme.of(context).colorScheme;
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
                      Text('${labelOf(view)} · 共 $pageCount 页', style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 14)),
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
                  Text('1', style: TextStyle(color: scheme.onSurfaceVariant)),
                  Expanded(
                    child: viewCount < 2
                        ? const SizedBox()
                        : Slider(
                            value: view.toDouble(),
                            max: (viewCount - 1).toDouble(),
                            divisions: viewCount - 1,
                            label: labelOf(view),
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
    if (app.rolePageCount == 0) return const SizedBox();
    return SegmentedButton<bool>(
      showSelectedIcon: false,
      segments: const [
        ButtonSegment(value: false, label: Text('原稿')),
        ButtonSegment(value: true, label: Text('文字')),
      ],
      selected: {app.textMode},
      onSelectionChanged: (v) => app.updateReading(() => app.textMode = v.first),
    );
  }
}

/// 文字重排：字号行距可调，一行不超过约 34 个字。只显示已翻开的原稿页上的内容。
/// 长按一段跳到它所在的原稿页核对。
class TextReader extends StatelessWidget {
  const TextReader({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final scheme = Theme.of(context).colorScheme;
    final body = TextStyle(fontSize: app.fontSize, height: app.lineSpacing, color: scheme.onSurface);
    // 没有原稿图就没有「按住翻开」的地方：文字全部显示，否则整本是空的。
    bool visible(int? p) => p == null || app.rolePageCount == 0 || p <= app.openedPages;
    final hiddenLeft = app.book.chapters.any((c) => c.blocks.any((b) => !visible(b.sourcePageNumber)));
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
                    for (final b in ch.blocks.where((b) => visible(b.sourcePageNumber)))
                      if (b.kind == 'ChapterTitle')
                        Padding(
                          padding: const EdgeInsets.only(top: 24, bottom: 16),
                          child: Text(b.text, style: TextStyle(fontSize: app.fontSize * 1.5, fontWeight: FontWeight.w700, color: scheme.onSurface)),
                        )
                      else
                        GestureDetector(
                          onLongPress: b.sourcePageNumber == null || app.rolePageCount == 0
                              ? null
                              : () {
                                  HapticFeedback.mediumImpact();
                                  app.updateReading(() {
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
              child: Text(
                hiddenLeft ? '后面的内容还盖着：切到「原稿」，按住第 ${app.openedPages + 1} 页翻开' : '长按任一段，跳到它所在的原稿页核对',
                textAlign: TextAlign.center,
                style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 14),
              ),
            ),
          ],
        ),
      ),
    ]);
  }
}
