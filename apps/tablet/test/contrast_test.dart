import 'package:flutter/painting.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:localdm_tablet/screens/reader.dart';
import 'package:localdm_tablet/theme.dart';

// 设计里用到的每一对文字/底色都要 ≥4.5:1（WCAG AA 正文）。跑测试时把数值打出来，写进设计文档。
void main() {
  for (final (name, scheme) in [('深色', Palette.dark), ('浅色', Palette.light)]) {
    test('$name配色文字对比度 ≥ 4.5', () {
      for (final (pair, fg, bg) in Palette.textPairs(scheme)) {
        final ratio = contrast(fg, bg);
        // ignore: avoid_print
        print('$name | $pair | ${ratio.toStringAsFixed(2)}');
        expect(ratio, greaterThanOrEqualTo(4.5), reason: '$name $pair');
      }
    });
  }

  // 原稿夜读：用 reader.dart 里同样的矩阵算扫描页纸色和字色变换后的对比度。
  // 纸 (246,242,233)、字 (52,46,40) 取自占位页；真实扫描页要用真页抽样复核。
  test('原稿夜读后纸与字的对比度 ≥ 4.5', () {
    Color apply(List<double> m, Color c) {
      double ch(int row) => ((m[row * 5] * c.r * 255 + m[row * 5 + 1] * c.g * 255 + m[row * 5 + 2] * c.b * 255 + m[row * 5 + 4]) / 255).clamp(0, 1);
      return Color.from(alpha: 1, red: ch(0), green: ch(1), blue: ch(2));
    }

    const paper = Color(0xFFF6F2E9), ink = Color(0xFF342E28);
    for (final (name, m) in [('原样', identityMatrix), ('压暗', dimMatrix), ('反白', invertMatrix)]) {
      final ratio = contrast(apply(m, ink), apply(m, paper));
      // ignore: avoid_print
      print('原稿夜读 | $name | ${ratio.toStringAsFixed(2)}');
      expect(ratio, greaterThanOrEqualTo(4.5), reason: name);
    }
  });
}
