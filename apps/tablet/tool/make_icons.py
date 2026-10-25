"""从 design/logo.webp 生成安卓、iOS、网页的 App 图标，以及电脑端的 app.ico。

图标只取上半部的拱门图案：字标「Dungeon Master」缩到 48px 看不清。
每个目标文件按它现有的尺寸重画，不改文件名和清单。
用法：python tool/make_icons.py（在 apps/tablet 下运行）
"""
from pathlib import Path

from PIL import Image

root = Path(__file__).resolve().parent.parent
logo = Image.open(root / "design" / "logo.webp").convert("RGB")
w, h = logo.size
# 图案大约占原图 x 0.24–0.76、y 0.07–0.73；取以它为中心的正方形，四周留一点底色。
side = int(w * 0.68)
cx, cy = w // 2, int(h * 0.40)
emblem = logo.crop((cx - side // 2, cy - side // 2, cx + side // 2, cy + side // 2))
# 再放到大一圈的底色上：iPad 圆角、安卓圆形遮罩都不切到拱门。底色取原图左上角。
full = int(side * 1.2)
margin = (full - side) // 2
canvas = Image.new("RGB", (full, full), logo.getpixel((8, 8)))
canvas.paste(emblem, (margin, margin))
emblem = canvas

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
