"""从 design/logo.png 生成安卓、iOS、网页的 App 图标，以及电脑端的 app.ico。

新 logo（绘合·Paletale）本身已是 1:1 居中正方形图标（1254×1254，无底字），直接按目标尺寸缩放。
每个目标文件按它现有的尺寸重画，不改文件名和清单。
用法：python tool/make_icons.py（在 apps/tablet 下运行）
"""
from pathlib import Path

from PIL import Image

root = Path(__file__).resolve().parent.parent
emblem = Image.open(root / "design" / "logo.png").convert("RGB")

targets = [
    *(root / "android/app/src/main/res").glob("mipmap-*/ic_launcher.png"),
    *(root / "ios/Runner/Assets.xcassets/AppIcon.appiconset").glob("*.png"),
    *(root / "web/icons").glob("*.png"),
    root / "web/favicon.png",
]
for path in targets:
    size = Image.open(path).size
    # iOS 的 1024 图标不能带透明通道，全部存成 RGB。
    emblem.resize(size, Image.Resampling.LANCZOS).save(path)
    print(f"{path.relative_to(root)} {size[0]}x{size[1]}")

# 电脑端：程序、窗口、安装器共用这一个 ico。
ico = root.parent.parent / "src/LocalDm.App/Assets/app.ico"
# WPF 读不了 24 位条目的 ico，窗口一加载就抛 XamlParseException：存成带透明通道的 32 位。
emblem.convert("RGBA").save(ico, sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)])
print(f"{ico} ico")
