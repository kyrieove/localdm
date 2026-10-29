import 'dart:convert';
import 'dart:math';

import 'package:flutter/widgets.dart';
import 'package:http/http.dart' as http;
import 'package:shared_preferences/shared_preferences.dart';

/// 电脑端平板服务的 HTTP 客户端。设备身份走 X-LocalDm-Device 请求头（ADR 0012 细节 1），
/// 不靠 Cookie：杀掉 App、隔天再开还是同一台设备，认领和阅读进度都在。
class TabletApi {
  TabletApi(this.base, this.device, [http.Client? client]) : _client = client ?? http.Client();

  final Uri base;
  final String device;
  final http.Client _client;

  Map<String, String> get _headers => {'X-LocalDm-Device': device};

  /// 首次启动生成 32 位随机编号存在本机（网页版存在浏览器里）。
  static Future<String> deviceId() async {
    final prefs = await SharedPreferences.getInstance();
    final saved = prefs.getString('device');
    if (saved != null) return saved;
    final random = Random.secure();
    final fresh = List.generate(32, (_) => random.nextInt(16).toRadixString(16)).join();
    await prefs.setString('device', fresh);
    return fresh;
  }

  static Future<String?> savedAddress() async => (await SharedPreferences.getInstance()).getString('address');

  static Future<void> saveAddress(String address) async =>
      (await SharedPreferences.getInstance()).setString('address', address);

  static Future<void> clearAddress() async =>
      (await SharedPreferences.getInstance()).remove('address');

  Future<http.Response> get(String path) =>
      _client.get(base.resolve(path), headers: _headers).timeout(const Duration(seconds: 6));

  Future<http.Response> post(String path, [Object? body]) => _client
      .post(base.resolve(path),
          headers: {..._headers, if (body != null) 'content-type': 'application/json'},
          body: body == null ? null : jsonEncode(body))
      .timeout(const Duration(seconds: 6));

  /// 200 时返回 JSON，其余状态码返回 null；连不上抛异常，由调用方算作断线。
  Future<Map<String, dynamic>?> json(String path) async {
    final response = await get(path);
    return response.statusCode == 200 ? jsonDecode(utf8.decode(response.bodyBytes)) as Map<String, dynamic> : null;
  }

  ImageProvider image(String path) => NetworkImage(base.resolve(path).toString(), headers: _headers);
}
