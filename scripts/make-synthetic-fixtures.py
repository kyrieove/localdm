# -*- coding: utf-8 -*-
"""Generate the T004 synthetic fixture material.

All text is invented for testing. It contains no real script content.
Outputs:
  fixtures/synthetic/text-12pages.pdf   text PDF, 12 pages (> MinerU 10-page default limit)
  fixtures/synthetic/scan-3pages.pdf    image-only PDF, 3 pages (forces OCR path)
  fixtures/synthetic/images/*.png       loose images for the image import path
  fixtures/synthetic/.private/expected.json
                                        ground truth; hidden from git, tests only
"""

import json
import os

import fitz  # PyMuPDF

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "fixtures", "synthetic"))
PRIVATE = os.path.join(ROOT, ".private")
CJK_FONT_CANDIDATES = [
    r"C:\Windows\Fonts\msyh.ttc",
    r"C:\Windows\Fonts\msyhbd.ttc",
    r"C:\Windows\Fonts\simhei.ttf",
]

PAGES = [
    # page 1: cover / public intro
    ("封面", [
        "合成测试剧本《灯塔回声》",
        "本文件为自动化测试专用合成材料，不含任何真实剧本内容。",
        "人数：5 人（4 男 1 女，可反串）",
        "时长：约 3 小时",
    ]),
    # page 2: rules
    ("主持规则", [
        "一、本剧本分为阅本、搜证、讨论、投票、真相五个阶段。",
        "二、第一阶段时长 10 分钟，阅读个人剧本。",
        "三、第二阶段发放线索卡，每轮最多两张。",
        "四、投票采用纸笔方式，由主持人汇总。",
    ]),
    # page 3..12: role books, each page one role
    ("角色：唐", ["你是灯塔看守人唐。", "案发当晚你在塔顶值班，听到码头方向有争吵声。", "你的秘密：你私自改过灯塔值班记录。"]),
    ("角色：护士", ["你是护士安。", "你负责照顾船长的旧伤，知道他的用药时间。", "你的秘密：你把止痛药换成了维生素。"]),
    ("角色：杰", ["你是机械师杰。", "你维修过码头的绞盘，知道它的承重限制。", "你的秘密：你欠码头一笔赌债。"]),
    ("角色：海", ["你是船务代理海。", "你负责货物报关单据。", "你的秘密：你替人伪造过一份报关单。"]),
    ("角色：玉", ["你是记者玉。", "你来岛上调查十年前的沉船事故。", "你的秘密：你手上有未公开的录音。"]),
    ("线索 1-4", [
        "线索 1：码头的绳索有新磨损痕迹。",
        "线索 2：塔顶的灯油桶比记录少了半桶。",
        "线索 3：船长房间的抽屉被撬开。",
        "线索 4：厨房的账本缺了一页。",
    ]),
    ("线索 5-8", [
        "线索 5：医务室的药瓶标签被换过。",
        "线索 6：绞盘的操作日志停在事故当晚。",
        "线索 7：报关单上有一个涂改的名字。",
        "线索 8：十年前的报纸剪报被收藏在书里。",
    ]),
    ("阶段条件", [
        "投票条件：完成两轮搜证讨论。",
        "真相条件：投票汇总提交且主持人确认。",
        "搜查限制：每人每轮最多查看一张线索。",
    ]),
    ("真相", [
        "真相：杰在争执中失手推动了绞盘，导致船长落水。",
        "杰的动机：为偿还赌债而挪用货款被船长发现。",
        "结局：杰被揭穿，其余角色各自面对自己的隐瞒。",
    ]),
    ("复盘", [
        "复盘要点：灯油桶的减少可以印证塔顶曾离开人。",
        "复盘要点：绳索磨损与绞盘日志互相印证。",
        "复盘要点：报关单的涂改指向资金流向。",
    ]),
]

SCAN_PAGE_TEXT = [
    "扫描页 1（合成）：这是只有图片的页面，必须通过 OCR 才能得到文字。",
    "扫描页 2（合成）：线索卡编号 21，内容为码头仓库的钥匙。",
    "扫描页 3（合成）：主持备注（合成版）第二轮搜证限时 8 分钟。",
]


def pick_font() -> str | None:
    for path in CJK_FONT_CANDIDATES:
        if os.path.exists(path):
            return path
    return None


def write_text_pdf(font_path: str) -> dict:
    doc = fitz.open()
    page_texts = []
    for idx, (title, lines) in enumerate(PAGES, start=1):
        page = doc.new_page(width=595, height=842)  # A4 portrait, points
        y = 80.0
        page.insert_text((60, y), f"[合成材料] {title}", fontsize=18,
                         fontname='china-s')
        y += 40
        for line in lines:
            page.insert_text((60, y), line, fontsize=12,
                             fontname='china-s')
            y += 26
        page_texts.append("\n".join(lines))
    path = os.path.join(ROOT, "text-12pages.pdf")
    doc.save(path)
    doc.close()
    return {"path": "text-12pages.pdf", "pageCount": len(PAGES), "pageTexts": page_texts}


def write_scan_pdf(font_path: str) -> dict:
    doc = fitz.open()
    for idx, text in enumerate(SCAN_PAGE_TEXT, start=1):
        # Render the text into a raster image, then insert the image only:
        # the PDF has no text layer, so extraction requires OCR.
        tmp = fitz.open()
        tp = tmp.new_page(width=595, height=842)
        tp.insert_text((60, 120), text, fontsize=16,
                       fontname='china-s')
        pix = tp.get_pixmap(dpi=110)
        tmp.close()
        page = doc.new_page(width=595, height=842)
        page.insert_image(page.rect, pixmap=pix, keep_proportion=True)
    path = os.path.join(ROOT, "scan-3pages.pdf")
    doc.save(path, garbage=4, deflate=True, clean=True)
    doc.close()
    return {"path": "scan-3pages.pdf", "pageCount": len(SCAN_PAGE_TEXT), "pageTexts": SCAN_PAGE_TEXT}


def write_images(font_path: str) -> list:
    os.makedirs(os.path.join(ROOT, "images"), exist_ok=True)
    results = []
    items = [
        ("01-cover.png", "合成图片 01：灯塔正面照片说明"),
        ("02-clue.png", "合成图片 02：绞盘铭牌上的出厂编号"),
    ]
    for name, text in items:
        doc = fitz.open()
        page = doc.new_page(width=800, height=600)
        page.insert_text((40, 80), text, fontsize=20,
                         fontname='china-s')
        pix = page.get_pixmap(dpi=100)
        out = os.path.join(ROOT, "images", name)
        pix.save(out)
        doc.close()
        results.append({"path": f"images/{name}", "pageCount": 1, "pageTexts": [text]})
    return results


def main() -> None:
    font_path = pick_font()
    if font_path is None:
        raise SystemExit("no CJK font found; cannot render synthetic CJK text")
    text_pdf = write_text_pdf(font_path)
    scan_pdf = write_scan_pdf(font_path)
    images = write_images(font_path)

    expected = {
        "note": "Synthetic ground truth for automated tests only. Not real script content.",
        "files": [text_pdf, scan_pdf, *images],
        "keyFacts": [
            "杰在争执中失手推动了绞盘，导致船长落水。",
            "投票采用纸笔方式，由主持人汇总。",
            "线索 6：绞盘的操作日志停在事故当晚。",
        ],
        "murderer": "杰",
        "playerCount": 5,
        "publicPhases": ["阅本", "搜证", "讨论", "投票", "真相"],
        "secretPhrases": [
            "你私自改过灯塔值班记录",
            "你把止痛药换成了维生素",
            "你欠码头一笔赌债",
        ],
    }
    os.makedirs(PRIVATE, exist_ok=True)
    with open(os.path.join(PRIVATE, "expected.json"), "w", encoding="utf-8") as fh:
        json.dump(expected, fh, ensure_ascii=False, indent=2)
    print("ok", font_path)


if __name__ == "__main__":
    main()
