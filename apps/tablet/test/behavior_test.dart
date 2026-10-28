import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:localdm_tablet/data/app_state.dart';
import 'package:localdm_tablet/main.dart';
import 'package:localdm_tablet/screens/debug_panel.dart';
import 'package:localdm_tablet/screens/reader.dart';

// 只验行为：测试环境的字体是方块字，外观靠浏览器截图看。
Future<AppState> pump(WidgetTester tester, Size size, Map<String, String> link) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final state = AppState();
  await tester.runAsync(state.load);
  state.applyLink(link);
  await tester.pumpWidget(TabletApp(state: state));
  await tester.pump(const Duration(milliseconds: 400));
  return state;
}

void main() {
  testWidgets('iPad Air 4 横竖屏都用侧边导航栏，分屏变窄后换底部导航', (tester) async {
    await pump(tester, const Size(1180, 820), {'stage': 'inGame', 'section': 'talk'});
    expect(find.byType(NavigationRail), findsOneWidget);

    tester.view.physicalSize = const Size(820, 1180);
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.byType(NavigationRail), findsOneWidget);

    tester.view.physicalSize = const Size(500, 900);
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.byType(NavigationRail), findsNothing);
    expect(find.byType(NavigationBar), findsOneWidget);
  });

  testWidgets('认领时被别人抢先：留在选角色页并提示', (tester) async {
    final state = await pump(tester, const Size(1180, 820), {'stage': 'pickRole'});
    state.nextClaimTaken = true;
    await tester.tap(find.text('合成角色 4'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('就是我'));
    await tester.pump(const Duration(milliseconds: 500));
    await tester.pump(const Duration(milliseconds: 300));
    expect(state.stage, Stage.pickRole);
    expect(find.textContaining('刚被别人选走了'), findsOneWidget);
    expect(state.catalog.claimedRoles, contains('role-4'));
  });

  testWidgets('竖屏轻点右侧翻到下一页，翻页后控件收起', (tester) async {
    final state = await pump(tester, const Size(820, 1180), {'stage': 'inGame', 'section': 'book', 'page': '1', 'text': '0'});
    await tester.tapAt(const Offset(800, 600));
    await tester.pumpAndSettle();
    expect(state.page, 2);
    expect(state.immersive, isTrue);
  });

  testWidgets('横屏一次显示两页，右侧翻页跳两页', (tester) async {
    final state = await pump(tester, const Size(1180, 820), {'stage': 'inGame', 'section': 'book', 'page': '1', 'text': '0'});
    await tester.tapAt(const Offset(1160, 400));
    await tester.pumpAndSettle();
    expect(state.page, 3);
  });

  testWidgets('线索卡翻面；没有背面的卡提示', (tester) async {
    await pump(tester, const Size(1180, 820), {'stage': 'inGame', 'section': 'cards'});
    await tester.tap(find.text('翻到背面'));
    await tester.pumpAndSettle();
    expect(find.text('看正面'), findsOneWidget);

    await tester.tap(find.text('书房').at(1)); // card-2，没有卡背
    await tester.pump(const Duration(milliseconds: 300));
    await tester.tap(find.text('没有背面'));
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.text('这张卡没有背面'), findsOneWidget);
  });

  testWidgets('盖着的下一页按住 0.65 秒翻开；中途松手不翻；更后面的页翻不开', (tester) async {
    final state = await pump(tester, const Size(820, 1180), {'stage': 'inGame', 'page': '2', 'opened': '1', 'text': '0'});
    expect(find.text('按住拆封'), findsOneWidget);

    var g = await tester.startGesture(const Offset(450, 600));
    await tester.pump(); // 动画从下一帧开始计时
    await tester.pump(const Duration(milliseconds: 300));
    await g.up();
    await tester.pumpAndSettle();
    expect(state.openedPages, 1, reason: '只按了 300ms');

    g = await tester.startGesture(const Offset(450, 600));
    await tester.pump(); // 动画从下一帧开始计时
    await tester.pump(const Duration(milliseconds: 700));
    await g.up();
    await tester.pumpAndSettle();
    expect(state.openedPages, 2);

    expect(state.openPage(4), isFalse, reason: '只能按顺序翻');
  });

  testWidgets('横屏点左页只单独全屏这一页', (tester) async {
    await pump(tester, const Size(1180, 820), {'stage': 'inGame', 'page': '1', 'opened': '12', 'text': '0'});
    await tester.tapAt(const Offset(420, 400));
    await tester.pumpAndSettle();
    expect(find.byType(PageViewer), findsOneWidget);
    expect(tester.widget<PageViewer>(find.byType(PageViewer)).page, 1);
  });

  testWidgets('横屏滑条一格是两页：手册 4 页拖到第二格就到第 3–4 页', (tester) async {
    final state = await pump(tester, const Size(1180, 820), {'stage': 'inGame', 'section': 'handbook'});
    final slider = tester.widget<Slider>(find.byType(Slider));
    expect(slider.divisions, 1);
    slider.onChanged!(1);
    await tester.pumpAndSettle();
    expect(state.handbookPage, 3);
  });

  testWidgets('断线后只有当时前后两页能看', (tester) async {
    final state = await pump(tester, const Size(820, 1180), {'stage': 'inGame', 'page': '5'});
    state.setOnline(false);
    expect([for (var p = 1; p <= 12; p++) if (state.pageCached(p)) p], [3, 4, 5, 6, 7]);
    state.setOnline(true);
    expect(state.pageCached(12), isTrue);
  });

  testWidgets('正式版默认不显示原型调试把手，只有原型参数下才显示', (tester) async {
    final normal = await pump(tester, const Size(1180, 820), const {});
    expect(normal.showDebug, isFalse);
    expect(find.byType(DebugHandle), findsNothing);

    final proto = await pump(tester, const Size(1180, 820), {'stage': 'waitingStory'});
    expect(proto.showDebug, isTrue);
    expect(find.byType(DebugHandle), findsOneWidget);
  });

  testWidgets('全部线索卡暂停分发时横屏不崩溃，显示等待提示', (tester) async {
    final state = await pump(tester, const Size(1180, 820), {'stage': 'inGame', 'section': 'cards'});
    state.update(() {
      for (final c in state.catalog.cards) {
        c.isAvailable = false;
      }
    });
    await tester.pumpAndSettle();
    expect(find.text('还没有可用的线索卡，等主持人发放'), findsOneWidget);
  });

  testWidgets('公开对话页能发言；主持忙时发送键灰掉，正在朗读时换成停止键', (tester) async {
    final state = await pump(tester, const Size(1180, 820), {'stage': 'inGame', 'section': 'talk'});
    final before = state.messages.length;
    await tester.enterText(find.byType(TextField), '我想问一下');
    await tester.tap(find.byTooltip('发送'));
    await tester.pump(const Duration(milliseconds: 100));
    expect(state.messages.length, before + 1);
    expect(state.messages.last.text, '我想问一下');

    state.update(() => state.hostBusy = true);
    await tester.pump();
    expect(tester.widget<IconButton>(find.widgetWithIcon(IconButton, Icons.send_rounded)).onPressed, isNull);

    state.update(() => state.hostSpeaking = true);
    await tester.pump();
    expect(find.byTooltip('停止朗读'), findsOneWidget);
  });

  testWidgets('没有原稿图的角色本：文字版不受翻开限制，全部显示', (tester) async {
    final state = await pump(tester, const Size(1180, 820), {'stage': 'inGame', 'section': 'book', 'text': '1', 'opened': '0'});
    state.update(() => state.rolePageCount = 0);
    await tester.pumpAndSettle();
    expect(find.textContaining('后面的内容还盖着'), findsNothing);
  });

  test('地址规范化：自动补默认端口并去掉协议与路径', () {
    expect(AppState.normalizeAddress('192.168.1.50'), '192.168.1.50:5188');
    expect(AppState.normalizeAddress('http://192.168.1.50:5188/app/'), '192.168.1.50:5188');
    expect(AppState.normalizeAddress('https://10.0.0.2:6000/'), '10.0.0.2:6000');
    expect(AppState.normalizeAddress('   '), '');
  });
}
