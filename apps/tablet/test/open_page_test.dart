import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:localdm_tablet/data/api.dart';
import 'package:localdm_tablet/data/app_state.dart';

AppState connected(Future<http.Response> Function(http.Request) handler) =>
    AppState()..api = TabletApi(Uri.parse('http://pc/'), 'device', MockClient(handler));

void main() {
  test('电脑拒绝翻页时退回电脑记的页数，后面的页才翻得开', () async {
    final state = connected((_) async => http.Response('{"opened":false,"openedPages":0}', 409));
    expect(state.openPage(1), isTrue);
    expect(state.openedPages, 1);
    await pumpEventQueue();
    expect(state.openedPages, 0);
  });

  test('请求发不出去时退回上一页', () async {
    final state = connected((_) async => throw http.ClientException('断网'));
    state.openPage(1);
    await pumpEventQueue();
    expect(state.openedPages, 0);
  });

  test('电脑确认翻开就保持', () async {
    final state = connected((_) async => http.Response('{"opened":true,"openedPages":1}', 200));
    state.openPage(1);
    await pumpEventQueue();
    expect(state.openedPages, 1);
  });
}
