import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

import 'models.dart';

/// App 从打开到进入本局要经过的几步。每一步都是一整屏，不在同一页里插提示。
enum Stage { connect, localNetwork, localNetworkDenied, waitingStory, preparing, pickRole, inGame }

enum NightMode { off, dim, invert }

enum Section { book, cards, talk, handbook }

/// 原型的全部状态。真 App 里这里换成 HTTP 客户端，字段不变。
class AppState extends ChangeNotifier {
  // 接口缺口：/tablet/role/book 没给原稿页数、也没有手册页数，原型先写死；真 App 需要接口补字段。
  static const rolePageCount = 12;
  static const handbookPageCount = 4;
  static const cardsWithBack = {'card-1', 'card-3', 'card-5'};
  static const serverAddress = '192.168.1.23:5188';

  late Catalog catalog;
  late RoleBook book;
  late TableUi ui;
  final List<PublicMessage> messages = [];

  Stage stage = Stage.connect;
  String? myRoleId;
  Section section = Section.book;

  int page = 1; // 角色本当前原稿页（1 起）
  int handbookPage = 1;
  // 已按住翻开的原稿页数：后面的页盖着，只能按顺序翻开（与网页端 TabletSpikeState.OpenedPages 同规则）。
  // 接口缺口：现在只有 Blazor 在进程内拿得到，HTTP 没有。
  int openedPages = 0;
  bool textMode = false;
  bool immersive = false; // 阅读时隐藏导航栏与控件
  double fontSize = 19;
  double lineSpacing = 1.95;
  NightMode night = NightMode.dim;
  ThemeModePref themePref = ThemeModePref.dark;

  bool online = true;
  int reconnectAttempt = 0;
  bool hostSpeaking = false;
  bool hostBusy = false;
  int unreadTalk = 0;
  bool curtain = false;
  bool nextClaimTaken = false;
  String? pendingToast;
  bool showDebug = true; // 原型调试把手；截图时用 clean=1 隐藏
  String? pendingSheet; //原型网址参数：进来就打开 phase / reading / role 面板

  Future<void> load() async {
    Future<Map<String, dynamic>> read(String name) async =>
        jsonDecode(await rootBundle.loadString('assets/fixtures/$name.json')) as Map<String, dynamic>;
    catalog = Catalog.fromJson(await read('catalog'));
    book = RoleBook.fromJson(await read('role_book'));
    ui = TableUi.fromJson(await read('ui'));
    final pub = await read('public');
    messages.addAll([for (final m in pub['messages'] as List) PublicMessage.fromJson(m)]);
    final progress = (await read('role_progress'))['settings'] as Map<String, dynamic>;
    fontSize = (progress['fontSize'] as num).toDouble();
    lineSpacing = double.parse(progress['lineSpacing'] as String);
    textMode = progress['preferText'] as bool;
    page = progress['sourcePageNumber'] as int;
    // 合成服务有一个角色已被别人占着，选角色页才有「已被认领」可看。
    catalog.claimedRoles.add('role-2');
  }

  /// 原型专用：网页版用网址参数直达某一屏，例如 ?stage=inGame&section=cards&page=3。
  void applyLink(Map<String, String> q) {
    stage = Stage.values.where((s) => s.name == q['stage']).firstOrNull ?? stage;
    section = Section.values.where((s) => s.name == q['section']).firstOrNull ?? section;
    night = NightMode.values.where((s) => s.name == q['night']).firstOrNull ?? night;
    themePref = ThemeModePref.values.where((s) => s.name == q['theme']).firstOrNull ?? themePref;
    page = int.tryParse(q['page'] ?? '') ?? page;
    textMode = q['text'] == '1' || (q['text'] == null && textMode);
    immersive = q['immersive'] == '1';
    online = q['offline'] != '1';
    _offlineCenter = page;
    hostSpeaking = q['speaking'] == '1';
    pendingSheet = q['sheet'];
    showDebug = q['clean'] != '1';
    if (q['unread'] != null) unreadTalk = int.parse(q['unread']!);
    openedPages = int.tryParse(q['opened'] ?? '') ?? page;
    if (stage == Stage.inGame) {
      myRoleId = 'role-3';
      catalog.claimedRoles.add('role-3');
    }
  }

  Role? get myRole => catalog.roles.where((r) => r.roleId == myRoleId).firstOrNull;

  void go(Stage s) {
    stage = s;
    notifyListeners();
    // 原型没有真电脑：等待页几秒后自己往下走，模拟主持人开了剧本、材料准备好。
    final next = {Stage.waitingStory: Stage.preparing, Stage.preparing: Stage.pickRole}[s];
    if (next != null && showDebug) {
      Timer(const Duration(seconds: 3), () {
        if (stage == s) go(next);
      });
    }
  }

  /// 模拟 POST /tablet/claim：被别人抢先时返回 false（接口 409 TAKEN）。
  Future<bool> claim(String roleId) async {
    await Future<void>.delayed(const Duration(milliseconds: 450));
    if (nextClaimTaken || catalog.claimedRoles.contains(roleId)) {
      nextClaimTaken = false;
      catalog.claimedRoles.add(roleId);
      notifyListeners();
      return false;
    }
    catalog.claimedRoles.add(roleId);
    myRoleId = roleId;
    openedPages = 0;
    stage = Stage.inGame;
    notifyListeners();
    return true;
  }

  void release() {
    catalog.claimedRoles.remove(myRoleId);
    myRoleId = null;
    page = 1;
    openedPages = 0;
    stage = Stage.pickRole;
    notifyListeners();
  }

  void select(Section s) {
    section = s;
    immersive = false;
    if (s == Section.talk) unreadTalk = 0;
    notifyListeners();
  }

  void setPage(int p) {
    page = p.clamp(1, rolePageCount);
    notifyListeners();
  }

  /// 按住翻开：只有下一页能翻，返回是否翻开。
  bool openPage(int p) {
    if (p != openedPages + 1) return false;
    openedPages = p;
    notifyListeners();
    return true;
  }

  void setHandbookPage(int p) {
    handbookPage = p.clamp(1, handbookPageCount);
    notifyListeners();
  }

  void toggleImmersive([bool? value]) {
    immersive = value ?? !immersive;
    notifyListeners();
  }

  void update(VoidCallback change) {
    change();
    notifyListeners();
  }

  void addHostMessage(String text) {
    messages.add(PublicMessage(messages.length + 1, 'Host', 'HostReply', text));
    if (section != Section.talk) unreadTalk++;
    notifyListeners();
  }

  /// 模拟杀掉 App 再打开：阅读位置从电脑端进度接口取回。
  void simulateRelaunch() {
    stage = Stage.inGame;
    section = Section.book;
    immersive = false;
    pendingToast = '已回到第 $page 页，上次读到这里';
    notifyListeners();
  }

  int _offlineCenter = 1;

  void setOnline(bool value) {
    online = value;
    reconnectAttempt = value ? 0 : 1;
    _offlineCenter = page;
    notifyListeners();
  }

  /// 断线时还能看的页：内存里只留断线那一刻当前页前后各 2 页。
  bool pageCached(int p) => online || (p - _offlineCenter).abs() <= 2;
}

enum ThemeModePref { system, light, dark }
