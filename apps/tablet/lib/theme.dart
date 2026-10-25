import 'dart:math' as math;

import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';

/// 「灯下」：围桌时房间暗、屏幕是自己手里的一盏灯。
/// 深色是默认，近黑的暖灰底 + 一种琥珀色（台灯光）当唯一强调色；浅色给白天和亮房间。
class Palette {
  static const lamp = Color(0xFFF2B35E);

  static final dark = ColorScheme.fromSeed(
    seedColor: lamp,
    brightness: Brightness.dark,
    primary: lamp,
    onPrimary: const Color(0xFF2B1C05),
    primaryContainer: const Color(0xFF4A3410),
    onPrimaryContainer: const Color(0xFFFFDDB0),
    surface: const Color(0xFF14120F),
    onSurface: const Color(0xFFECE5D8),
    onSurfaceVariant: const Color(0xFFB5AC9C),
    surfaceContainerLowest: const Color(0xFF0E0C0A),
    surfaceContainerLow: const Color(0xFF1A1714),
    surfaceContainer: const Color(0xFF1E1B17),
    surfaceContainerHigh: const Color(0xFF29251F),
    surfaceContainerHighest: const Color(0xFF34302A),
    outline: const Color(0xFF8C8475),
    outlineVariant: const Color(0xFF3E3931),
    error: const Color(0xFFFFB4A6),
    onError: const Color(0xFF561E12),
  );

  static final light = ColorScheme.fromSeed(
    seedColor: lamp,
    brightness: Brightness.light,
    primary: const Color(0xFF7A4A00),
    onPrimary: Colors.white,
    primaryContainer: const Color(0xFFFFDDB0),
    onPrimaryContainer: const Color(0xFF2B1C05),
    surface: const Color(0xFFFAF7F2),
    onSurface: const Color(0xFF1F1B16),
    onSurfaceVariant: const Color(0xFF575045),
    surfaceContainerLowest: Colors.white,
    surfaceContainerLow: const Color(0xFFF4F0E9),
    surfaceContainer: const Color(0xFFEFEAE2),
    surfaceContainerHigh: const Color(0xFFE9E3DA),
    surfaceContainerHighest: const Color(0xFFE2DCD2),
    outline: const Color(0xFF7D7567),
    outlineVariant: const Color(0xFFD3CCC0),
    error: const Color(0xFFB3261E),
    onError: Colors.white,
  );

  /// 文字与背景的配对：设计里出现的每一对都在这里，测试逐对算对比度。
  static List<(String, Color, Color)> textPairs(ColorScheme s) => [
        ('正文 onSurface / surface', s.onSurface, s.surface),
        ('次要字 onSurfaceVariant / surface', s.onSurfaceVariant, s.surface),
        ('次要字 onSurfaceVariant / surfaceContainer', s.onSurfaceVariant, s.surfaceContainer),
        ('正文 onSurface / surfaceContainerHigh', s.onSurface, s.surfaceContainerHigh),
        ('强调字 primary / surface', s.primary, s.surface),
        ('强调字 primary / surfaceContainer', s.primary, s.surfaceContainer),
        ('角色牌「可以选」 primary / surfaceContainerHigh', s.primary, s.surfaceContainerHigh),
        ('角色牌「已被认领」 onSurfaceVariant / surfaceContainerLow', s.onSurfaceVariant, s.surfaceContainerLow),
        ('阅读控件页码 onSurfaceVariant / surfaceContainerHigh', s.onSurfaceVariant, s.surfaceContainerHigh),
        ('按钮字 onPrimary / primary', s.onPrimary, s.primary),
        ('徽标字 onPrimaryContainer / primaryContainer', s.onPrimaryContainer, s.primaryContainer),
        ('错误字 error / surface', s.error, s.surface),
      ];
}

/// WCAG 2.x 对比度。
double contrast(Color a, Color b) {
  double lum(Color c) {
    double ch(double v) => v <= 0.03928 ? v / 12.92 : math.pow((v + 0.055) / 1.055, 2.4).toDouble();
    return 0.2126 * ch(c.r) + 0.7152 * ch(c.g) + 0.0722 * ch(c.b);
  }

  final la = lum(a), lb = lum(b);
  return (math.max(la, lb) + 0.05) / (math.min(la, lb) + 0.05);
}

ThemeData buildTheme(ColorScheme scheme) {
  final base = ThemeData(colorScheme: scheme, useMaterial3: true);
  return base.copyWith(
    scaffoldBackgroundColor: scheme.surface,
    navigationRailTheme: NavigationRailThemeData(
      backgroundColor: scheme.surfaceContainerLow,
      indicatorColor: scheme.primaryContainer,
      selectedIconTheme: IconThemeData(color: scheme.onPrimaryContainer),
      labelType: NavigationRailLabelType.all,
      selectedLabelTextStyle: TextStyle(color: scheme.onSurface, fontSize: 13, fontWeight: FontWeight.w600),
      unselectedLabelTextStyle: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
    ),
    bottomSheetTheme: BottomSheetThemeData(
      backgroundColor: scheme.surfaceContainerLow,
      showDragHandle: true,
      constraints: const BoxConstraints(maxWidth: 640),
    ),
    snackBarTheme: const SnackBarThemeData(behavior: SnackBarBehavior.floating, width: 420),
    pageTransitionsTheme: const PageTransitionsTheme(builders: {
      // iPad 上右滑返回；安卓用系统默认的预测返回转场。
      TargetPlatform.iOS: CupertinoPageTransitionsBuilder(),
      TargetPlatform.android: PredictiveBackPageTransitionsBuilder(),
    }),
  );
}
