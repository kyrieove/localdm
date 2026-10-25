"""生成原型用的占位原稿页与卡面：尺寸按真实剧本的统计（原稿 821x1259、单卡 996x648），文字取自合成剧本。

用法：python tool/make_fake_pages.py（在 apps/tablet 下运行）。不读任何真实剧本。
"""
import json
import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "assets" / "fake"
FONT = "C:/Windows/Fonts/NotoSerifSC-VF.ttf"
PAGE = (821, 1259)
CARD = (996, 648)
ROLE_PAGES = 12
HANDBOOK_PAGES = 4

random.seed(7)
book = json.loads((ROOT / "assets/fixtures/role_book.json").read_text(encoding="utf-8"))
catalog = json.loads((ROOT / "assets/fixtures/catalog.json").read_text(encoding="utf-8"))
paragraphs = [b["text"] for c in book["chapters"] for b in c["blocks"] if b["kind"] == "Paragraph"]
titles = [c["title"] for c in book["chapters"]]


def font(size):
    return ImageFont.truetype(FONT, size)


def wrap(text, f, width, draw):
    lines, line = [], ""
    for ch in text:
        if draw.textlength(line + ch, font=f) > width:
            lines.append(line)
            line = ch
        else:
            line += ch
    if line:
        lines.append(line)
    return lines


def paper(size):
    # 扫描件的纸色与轻微噪点，让大图解码和缩放的负担接近真实扫描页。
    img = Image.new("RGB", size, (246, 242, 233))
    px = img.load()
    for _ in range(size[0] * size[1] // 6):
        x, y = random.randrange(size[0]), random.randrange(size[1])
        v = random.randint(-14, 6)
        r, g, b = px[x, y]
        px[x, y] = (r + v, g + v, b + v)
    return img


def text_page(n, total, heading):
    img = paper(PAGE)
    d = ImageDraw.Draw(img)
    margin, y = 78, 96
    d.text((margin, y), heading, font=font(40), fill=(40, 34, 28))
    y += 84
    body = font(25)
    i = n
    while y < PAGE[1] - 150:
        for line in wrap("　　" + paragraphs[i % len(paragraphs)], body, PAGE[0] - 2 * margin, d):
            if y >= PAGE[1] - 150:
                break
            d.text((margin, y), line, font=body, fill=(52, 46, 40))
            y += 44
        y += 20
        i += 1
    d.text((PAGE[0] // 2 - 30, PAGE[1] - 84), f"— {n} / {total} —", font=font(20), fill=(120, 112, 100))
    return img


def card(c, back):
    img = paper(CARD)
    d = ImageDraw.Draw(img)
    d.rectangle([24, 24, CARD[0] - 24, CARD[1] - 24], outline=(90, 76, 60), width=4)
    if back:
        d.text((70, 70), "卡背", font=font(30), fill=(120, 100, 80))
        d.text((70, 140), c["displayName"] + " · 背面说明", font=font(52), fill=(50, 40, 30))
        for k, line in enumerate(wrap(paragraphs[c["sourceOrder"] % len(paragraphs)], font(28), CARD[0] - 140, d)[:6]):
            d.text((70, 250 + k * 48), line, font=font(28), fill=(60, 52, 44))
    else:
        d.text((70, 60), c["groupName"] + f" · {c['sourceOrder']:02d}", font=font(30), fill=(120, 100, 80))
        d.text((70, 120), c["displayName"], font=font(96), fill=(40, 32, 24))
        for k, line in enumerate(wrap(paragraphs[(c["sourceOrder"] + 3) % len(paragraphs)], font(28), CARD[0] - 140, d)[:5]):
            d.text((70, 300 + k * 48), line, font=font(28), fill=(60, 52, 44))
    return img


OUT.mkdir(parents=True, exist_ok=True)
for n in range(1, ROLE_PAGES + 1):
    text_page(n, ROLE_PAGES, titles[(n - 1) * len(titles) // ROLE_PAGES]).save(OUT / f"role_{n}.jpg", quality=82)
for n in range(1, HANDBOOK_PAGES + 1):
    text_page(n, HANDBOOK_PAGES, "组织者手册").save(OUT / f"handbook_{n}.jpg", quality=82)
for c in catalog["cards"]:
    card(c, False).save(OUT / f"{c['cardId']}_front.jpg", quality=85)
    if c["sourceOrder"] % 2 == 1:  # 合成服务没有卡背；原型给单号卡补一张，好演示长按翻面
        card(c, True).save(OUT / f"{c['cardId']}_back.jpg", quality=85)
print("ok", len(list(OUT.iterdir())))
