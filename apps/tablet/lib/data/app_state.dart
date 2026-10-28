import 'dart:async';
import 'dart:convert';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

import 'api.dart';
import 'models.dart';

/// App 从打开到进入本局要经过的几步。每一步都是一整屏，不在同一页里插提示。
enum Stage { connect, localNetwork, localNetworkDenied, waitingStory, preparing, pickRole, inGame }

enum NightMode { off, dim, invert }

enum Section { book, cards, talk, handbook }

/// App 的全部状态。连上电脑后（[api] 不为空）数据都来自 /tablet/* 接口；
/// 没连电脑时用 assets/fixtures 的假数据，给原型调试抽屉和 flutter test 用。
class AppState extends ChangeNotifier {
  static const pollEvery = Duration(seconds: 2);

  TabletApi? api;
  String serverAddress = ''; // 连上后记下真地址
  int rolePageCount = 12;
  int handbookPageCount = 4;

  late Catalog catalog;
  late RoleBook book;
  late TableUi ui;
  final List<PublicMessage> messages = [];
  int _nextSequence = 0;
  String? _sessionId;
  int? _generation;
  Timer? _poller;

  Stage stage = Stage.connect;
  String? myRoleId;
  Section section = Section.book;

  int page = 1; // 角色本当前原稿页（1 起）
  int handbookPage = 1;
  // 已按住翻开的原稿页数：后面的页盖着，只能按顺序翻开（与网页端 TabletSpikeState.OpenedPages 同规则）。
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
  bool showDebug = false; // 正式版默认不出现；仅带原型网址参数（stage= 或 debug=1 且无 clean=1）时出现
  String? pendingSheet; // 原型网址参数：进来就打开 phase / reading / role 面板

  Future<void> load() async {
    Future<Map<String, dynamic>> read(String name) async =>
        jsonDecode(await rootBundle.loadString('assets/fixtures/$name.json')) as Map<String, dynamic>;
    catalog = Catalog.fromJson(await read('catalog'));
    // 合成服务没有带背面的卡；原型给三张配上假背面，好看翻面。
    for (final c in catalog.cards) {
      c.hasBack = const {'card-1', 'card-3', 'card-5'}.contains(c.cardId);
    }
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
    showDebug = (q.containsKey('stage') || q['debug'] == '1') && q['clean'] != '1';
    if (showDebug && serverAddress.isEmpty) serverAddress = '192.168.1.23:5188';
    if (q['unread'] != null) unreadTalk = int.parse(q['unread']!);
    openedPages = int.tryParse(q['opened'] ?? '') ?? page;
    if (stage == Stage.inGame) {
      myRoleId = 'role-3';
      catalog.claimedRoles.add('role-3');
    }
  }

  Role? get myRole => catalog.roles.where((r) => r.roleId == myRoleId).firstOrNull;

  /// 把扫码或手输的地址整理成 `host:port`（没带端口时默认补 5188，忽略后面的路径）。
  static String normalizeAddress(String raw) {
    final s = raw.trim();
    if (s.isEmpty) return '';
    final withScheme = s.contains('://') ? s : 'http://$s';
    final uri = Uri.tryParse(withScheme);
    if (uri == null || uri.host.isEmpty) return '';
    final host = uri.host.contains(':') ? '[${uri.host}]' : uri.host;
    final port = uri.hasPort ? uri.port : 5188;
    return '$host:$port';
  }

  /// 连上电脑：地址存在本机，之后每 2 秒问一次电脑现在是什么状态。
  Future<void> connect(String address) async {
    final trimmed = normalizeAddress(address);
    if (trimmed.isEmpty) return;
    serverAddress = trimmed;
    api = TabletApi(Uri.parse('http://$trimmed/'), await TabletApi.deviceId());
    await TabletApi.saveAddress(trimmed);
    showDebug = false;
    page = 1;
    handbookPage = 1;
    openedPages = 0;
    myRoleId = null;
    ui = TableUi.waiting();
    messages.clear();
    _nextSequence = 0;
    _sessionId = null;
    _generation = null;
    stage = Stage.waitingStory;
    notifyListeners();
    _poller?.cancel();
    await _poll();
    _poller = Timer.periodic(pollEvery, (_) => _poll());
  }

  /// 断开当前电脑，回到扫码/输地址页。
  Future<void> disconnect() async {
    _poller?.cancel();
    _poller = null;
    api = null;
    await TabletApi.clearAddress();
    myRoleId = null;
    openedPages = 0;
    page = 1;
    online = true;
    reconnectAttempt = 0;
    stage = Stage.connect;
    notifyListeners();
  }

  bool _polling = false;

  Future<void> _poll() async {
    final api = this.api;
    if (api == null || _polling) return;
    _polling = true;
    try {
      final cat = await api.json('/tablet/catalog');
      if (cat == null) throw StateError('catalog');
      final previousTitle = catalog.storyTitle;
      final previousGen = _generation;
      catalog = Catalog.fromJson(cat);
      _generation = catalog.generation;
      if (!online) setOnline(true);
      final genChanged = previousGen != null && previousGen != catalog.generation;
      if (genChanged) {
        // 重新开始本局沿用同一个存档 ID、序号从 1 重数：只看 sessionId 认不出来，旧对话和读取位置要在这里清掉。
        messages.clear();
        _nextSequence = 0;
        unreadTalk = 0;
      }
      if (catalog.storyState == 'None') {
        _leaveRole(Stage.waitingStory);
      } else if (catalog.storyState == 'Preparing') {
        _leaveRole(Stage.preparing);
      } else if (stage == Stage.waitingStory || stage == Stage.preparing || previousTitle != catalog.storyTitle || genChanged) {
        // 电脑上换了剧本、重新开始本局、或刚准备好：认领还在（App 重启）就直接回到阅读页，否则回选角色页。
        if (await _loadBook()) {
          stage = Stage.inGame;
        } else {
          _leaveRole(Stage.pickRole);
        }
      } else if (stage == Stage.inGame && !catalog.claimedRoles.contains(myRoleId)) {
        _leaveRole(Stage.pickRole); // 电脑上释放了这个角色
      }
      if (stage == Stage.inGame) await _pollTable(api);
      notifyListeners();
    } catch (_) {
      if (online) setOnline(false);
      reconnectAttempt++;
      notifyListeners();
    } finally {
      _polling = false;
    }
  }

  Future<void> _pollTable(TabletApi api) async {
    final pub = await api.json('/tablet/public?after=$_nextSequence');
    if (pub != null) {
      if (pub['sessionId'] != _sessionId) {
        // 电脑上重新开了一局：序号从头数，旧对话清掉，下一轮从 0 取。
        _sessionId = pub['sessionId'] as String?;
        messages.clear();
        if (_nextSequence != 0) {
          _nextSequence = 0;
          return;
        }
      }
      final fresh = [for (final m in pub['messages'] as List) PublicMessage.fromJson(m)];
      messages.addAll(fresh);
      if (fresh.isNotEmpty && section != Section.talk) unreadTalk += fresh.length;
      _nextSequence = pub['nextSequence'] as int;
      final host = pub['host'] as Map<String, dynamic>;
      hostBusy = host['busy'] as bool;
      hostSpeaking = host['speaking'] as bool;
    }
    final table = await api.json('/tablet/ui');
    ui = table != null ? TableUi.fromJson(table) : TableUi.waiting();
  }

  void _leaveRole(Stage next) {
    if (stage == Stage.connect || stage == Stage.localNetwork || stage == Stage.localNetworkDenied) return;
    myRoleId = null;
    openedPages = 0;
    page = 1;
    ui = TableUi.waiting();
    stage = next;
  }

  /// 取本人角色本和阅读进度；没认领（404）返回 false。
  Future<bool> _loadBook() async {
    final api = this.api!;
    final j = await api.json('/tablet/role/book');
    if (j == null) return false;
    book = RoleBook.fromJson(j);
    myRoleId = j['roleId'] as String;
    rolePageCount = j['sourcePageCount'] as int;
    handbookPageCount = j['handbookPageCount'] as int;
    openedPages = j['openedPages'] as int;
    if (handbookPageCount == 0 && section == Section.handbook) {
      section = Section.book;
    }
    final settings = (await api.json('/tablet/role/progress'))?['settings'] as Map<String, dynamic>?;
    if (settings != null) {
      fontSize = (settings['fontSize'] as num).toDouble();
      lineSpacing = double.tryParse(settings['lineSpacing'] as String) ?? lineSpacing;
      textMode = rolePageCount == 0 ? true : (settings['preferText'] as bool);
      page = (settings['sourcePageNumber'] as int).clamp(1, rolePageCount < 1 ? 1 : rolePageCount);
      if (page > 1 && !textMode) pendingToast = '已回到第 $page 页，上次读到这里';
    } else if (rolePageCount == 0) {
      textMode = true;
    }
    return true;
  }

  /// 原稿页、卡面的图片：连着电脑走接口，原型走打包进 App 的假图。
  ImageProvider image(String apiPath, String fakeAsset) => api?.image(apiPath) ?? AssetImage(fakeAsset);

  void go(Stage s) {
    stage = s;
    notifyListeners();
    // 原型没有真电脑：等待页几秒后自己往下走，模拟主持人开了剧本、材料准备好。
    final next = {Stage.waitingStory: Stage.preparing, Stage.preparing: Stage.pickRole}[s];
    if (next != null && showDebug && api == null) {
      Timer(const Duration(seconds: 3), () {
        if (stage == s) go(next);
      });
    }
  }

  /// POST /tablet/claim：被别人抢先时返回 false（接口 409 TAKEN）。
  Future<bool> claim(String roleId) async {
    final api = this.api;
    if (api != null) {
      final response = await api.post('/tablet/claim?roleId=${Uri.encodeQueryComponent(roleId)}');
      catalog.claimedRoles.add(roleId);
      final ok = response.statusCode == 200 && await _loadBook();
      if (ok) stage = Stage.inGame;
      notifyListeners();
      return ok;
    }
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

  /// POST /tablet/say：频次、字数、主持忙等规则都由电脑判（ADR 0007），这里只把电脑给的文案带回去。
  Future<({bool ok, String message})> say(String text) async {
    final api = this.api;
    if (api == null) {
      messages.add(PublicMessage(messages.length + 1, 'Player', 'PlayerSpeech', text));
      notifyListeners();
      return (ok: true, message: '已发送');
    }
    try {
      final response = await api.post('/tablet/say', {'text': text});
      final j = jsonDecode(utf8.decode(response.bodyBytes)) as Map<String, dynamic>;
      final ok = j['ok'] == true;
      if (ok) {
        hostBusy = true; // 下一轮轮询前就把发送键灰掉，免得连点
        notifyListeners();
      }
      return (ok: ok, message: j['message'] as String? ?? '没发出去，请再试一次');
    } catch (_) {
      return (ok: false, message: '没连上电脑，请稍后再发');
    }
  }

  void stopSpeaking() => api?.post('/tablet/say/stop').ignore();

  void release() {
    api?.post('/tablet/release').ignore();
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
    page = p.clamp(1, rolePageCount < 1 ? 1 : rolePageCount);
    _saveProgress();
    notifyListeners();
  }

  /// 阅读进度存到电脑上：杀掉 App 再开，从 /tablet/role/progress 取回。
  void _saveProgress() {
    api?.post('/tablet/role/progress', {
      'characterOffset': 0,
      'fontSize': fontSize.round(),
      'lineSpacing': lineSpacing.toString(),
      'focusMode': false,
      'preferText': textMode,
      'sourcePageNumber': page,
    }).ignore();
  }

  void updateReading(VoidCallback change) {
    change();
    _saveProgress();
    notifyListeners();
  }

  /// 按住翻开：只有下一页能翻，返回是否翻开。
  bool openPage(int p) {
    if (p != openedPages + 1) return false;
    openedPages = p;
    api?.post('/tablet/role/open/$p').ignore();
    notifyListeners();
    return true;
  }

  void setHandbookPage(int p) {
    handbookPage = p.clamp(1, handbookPageCount < 1 ? 1 : handbookPageCount);
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
