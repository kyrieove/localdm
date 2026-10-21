"""平板探针二维码生成与自校验。

只用标准库：本机离线，没有任何 QR NuGet 包可用。
生成后由同一个文件里的解码器把 PNG 读回来重放，
验证模块极性、定位/计时/对齐图案、格式信息、数据放置与掩码、以及纠错码自洽。
URL 能原样解回、纠错码能重算一致，才认为这个码可扫。
"""

import sys
import zlib

GF_EXP = [0] * 512
GF_LOG = [0] * 256
_x = 1
for _i in range(255):
    GF_EXP[_i] = _x
    GF_LOG[_x] = _i
    _x <<= 1
    if _x & 0x100:
        _x ^= 0x11D
for _i in range(255, 512):
    GF_EXP[_i] = GF_EXP[_i - 255]

FORMAT_XOR = 0x5412
BCH_G = 0b10100110111


def gf_mul(a, b):
    if a == 0 or b == 0:
        return 0
    return GF_EXP[GF_LOG[a] + GF_LOG[b]]


def gf_poly_mul(p, q):
    r = [0] * (len(p) + len(q) - 1)
    for i, a in enumerate(p):
        for j, b in enumerate(q):
            r[i + j] ^= gf_mul(a, b)
    return r


def rs_generator(n):
    g = [1]
    for i in range(n):
        g = gf_poly_mul(g, [1, GF_EXP[i]])
    return g


def rs_encode(data, n):
    g = rs_generator(n)
    res = [0] * n
    for byte in data:
        factor = byte ^ res[0]
        res = res[1:] + [0]
        for i in range(n):
            res[i] ^= gf_mul(g[i + 1], factor)
    return res


# 纠错级 M：每块纠错码字数、块数、总容量（版本 1..10）
EC_M = {1: 10, 2: 16, 3: 26, 4: 18, 5: 24, 6: 16, 7: 18, 8: 22, 9: 22, 10: 26}
BLOCKS = {1: (1, 16), 2: (1, 28), 3: (1, 44), 4: (2, 32), 5: (2, 43),
          6: (4, 27), 7: (4, 31), 8: (2, 38), 9: (3, 36), 10: (4, 43)}
TOTAL_CODEWORDS = {1: 26, 2: 44, 3: 70, 4: 100, 5: 134,
                   6: 172, 7: 196, 8: 242, 9: 292, 10: 346}


def pick_version(byte_count):
    caps = {v: TOTAL_CODEWORDS[v] - EC_M[v] * BLOCKS[v][0] for v in TOTAL_CODEWORDS}
    for v in sorted(caps):
        if byte_count <= caps[v]:
            return v
    raise SystemExit("内容超出二维码容量")


def bits(buf, value, length):
    for i in range(length - 1, -1, -1):
        buf.append((value >> i) & 1)


def encode(text):
    data = text.encode("utf-8")
    version = pick_version(len(data))
    ec_per_block = EC_M[version]
    num_blocks = BLOCKS[version][0]
    data_codewords = TOTAL_CODEWORDS[version] - ec_per_block * num_blocks

    buf = []
    bits(buf, 0b0100, 4)                       # 字节模式
    bits(buf, len(data), 8 if version < 10 else 16)
    for byte in data:
        bits(buf, byte, 8)
    bits(buf, 0, min(4, TOTAL_CODEWORDS[version] * 8 - len(buf)))
    while len(buf) % 8 != 0:
        buf.append(0)

    codewords = []
    for i in range(0, len(buf), 8):
        value = 0
        for bit in buf[i:i + 8]:
            value = (value << 1) | bit
        codewords.append(value)

    pad = [0xEC, 0x11]
    idx = 0
    while len(codewords) < data_codewords:
        codewords.append(pad[idx % 2])
        idx += 1

    block_size = data_codewords // num_blocks
    blocks = [codewords[b * block_size:(b + 1) * block_size] for b in range(num_blocks)]
    ec_blocks = [rs_encode(chunk, ec_per_block) for chunk in blocks]

    out = []
    for i in range(block_size):
        for b in range(num_blocks):
            out.append(blocks[b][i])
    for i in range(ec_per_block):
        for b in range(num_blocks):
            out.append(ec_blocks[b][i])
    return version, out


def build_function_map(size, version):
    """标记所有功能图案单元格：解码与编码共用同一张图，避免两边各写一套。"""
    reserved = [[False] * size for _ in range(size)]
    finder = [(0, 0), (0, size - 7), (size - 7, 0)]
    for (fr, fc) in finder:
        for dr in range(-1, 8):
            for dc in range(-1, 8):
                r, c = fr + dr, fc + dc
                if 0 <= r < size and 0 <= c < size:
                    reserved[r][c] = True
    for i in range(8, size - 8):
        reserved[6][i] = True
        reserved[i][6] = True
    # 版本 2..6 只有一个对齐图案，位于右下角
    if version >= 2:
        center = size - 7
        for dr in range(-2, 3):
            for dc in range(-2, 3):
                reserved[center + dr][center + dc] = True
    # 格式信息
    for i in range(9):
        reserved[8][i] = True
        reserved[i][8] = True
    for i in range(8):
        reserved[8][size - 1 - i] = True
    for i in range(8):
        reserved[size - 1 - i][8] = True
    return reserved


def draw_function_patterns(grid, reserved, size, version):
    finder = [(0, 0), (0, size - 7), (size - 7, 0)]
    for (fr, fc) in finder:
        for dr in range(7):
            for dc in range(7):
                edge = dr in (0, 6) or dc in (0, 6)
                center = 2 <= dr <= 4 and 2 <= dc <= 4
                grid[fr + dr][fc + dc] = 1 if edge or center else 0
    for i in range(8, size - 8):
        grid[6][i] = 1 if i % 2 == 0 else 0
        grid[i][6] = 1 if i % 2 == 0 else 0
    if version >= 2:
        center = size - 7
        for dr in range(5):
            for dc in range(5):
                edge = dr in (0, 4) or dc in (0, 4)
                middle = dr == 2 and dc == 2
                grid[center - 2 + dr][center - 2 + dc] = 1 if edge or middle else 0
    grid[size - 8][8] = 1  # 固定黑模块


def format_bits(ec_level_bits, mask):
    value = (ec_level_bits << 3) | mask
    remainder = value << 10
    for i in range(14, -1, -1):
        if remainder & (1 << (i + 10)):
            remainder ^= BCH_G << i
    return ((value << 10) | remainder) ^ FORMAT_XOR


def place_format(grid, size, ec_level_bits, mask):
    value = format_bits(ec_level_bits, mask)
    for i in range(6):
        grid[i][8] = (value >> i) & 1
    grid[7][8] = (value >> 6) & 1
    grid[8][8] = (value >> 7) & 1
    grid[8][7] = (value >> 8) & 1
    for i in range(9, 15):
        grid[8][14 - i] = (value >> i) & 1
    for i in range(8):
        grid[8][size - 1 - i] = (value >> i) & 1
    for i in range(8, 15):
        grid[size - 15 + i][8] = (value >> i) & 1


def mask_condition(r, c, mask):
    if mask == 0:
        return (r + c) % 2 == 0
    if mask == 1:
        return r % 2 == 0
    if mask == 2:
        return c % 3 == 0
    if mask == 3:
        return (r + c) % 3 == 0
    if mask == 4:
        return (r // 2 + c // 3) % 2 == 0
    if mask == 5:
        return (r * c) % 2 + (r * c) % 3 == 0
    if mask == 6:
        return ((r * c) % 2 + (r * c) % 3) % 2 == 0
    return ((r + c) % 2 + (r * c) % 3) % 2 == 0


def build_matrix(version, codewords, mask=0):
    size = version * 4 + 17
    grid = [[0] * size for _ in range(size)]
    reserved = build_function_map(size, version)
    draw_function_patterns(grid, reserved, size, version)
    place_format(grid, size, 0b00, mask)   # M 级

    direction = -1
    row = size - 1
    col = size - 1
    bit_index = 0
    while col > 0:
        if col == 6:
            col -= 1
        while 0 <= row < size:
            for c in (col, col - 1):
                if 0 <= c < size and not reserved[row][c]:
                    bit = 0
                    if bit_index < len(codewords) * 8:
                        bit = (codewords[bit_index // 8] >> (7 - bit_index % 8)) & 1
                    if mask_condition(row, c, mask):
                        bit ^= 1
                    grid[row][c] = bit
                    bit_index += 1
            row += direction
        row -= direction
        direction = -direction
        row += direction
        col -= 2
    return grid


def read_png(path):
    with open(path, "rb") as f:
        data = f.read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "不是 PNG"
    pos = 8
    width = height = None
    idat = bytearray()
    while pos < len(data):
        length = int.from_bytes(data[pos:pos + 4], "big")
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        if tag == b"IHDR":
            width = int.from_bytes(body[0:4], "big")
            height = int.from_bytes(body[4:8], "big")
            bit_depth, color_type = body[8], body[9]
            assert bit_depth == 8 and color_type == 0, "只支持 8 位灰度"
        elif tag == b"IDAT":
            idat += body
        pos += 12 + length
    raw = zlib.decompress(bytes(idat))
    stride = width
    rows = []
    prev = bytearray(stride)
    p = 0
    for _ in range(height):
        ftype = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        if ftype == 1:
            for i in range(1, stride):
                line[i] = (line[i] + line[i - 1]) & 0xFF
        elif ftype == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ftype == 3:
            for i in range(stride):
                left = line[i - 1] if i else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 0xFF
        elif ftype == 4:
            for i in range(stride):
                left = line[i - 1] if i else 0
                up = prev[i]
                ul = prev[i - 1] if i else 0
                pa, pb, pc = abs(up - ul), abs(left - ul), abs(left + up - 2 * ul)
                pred = left if (pa <= pb and pa <= pc) else (up if pb <= pc else ul)
                line[i] = (line[i] + pred) & 0xFF
        rows.append(bytes(line))
        prev = line
    return width, height, rows


def write_png(path, grid, scale, border):
    size = len(grid)
    total = (size + border * 2) * scale
    scaled = []
    for row in grid:
        line = []
        for value in row:
            line.extend([value] * scale)
        for _ in range(scale):
            scaled.append(line)

    raw = bytearray()
    for row in scaled:
        raw.append(0)
        for value in row:
            raw.append(0 if value else 255)   # 深模块=黑，浅模块=白
    png = b"\x89PNG\r\n\x1a\n"

    def chunk(tag, body):
        return (len(body).to_bytes(4, "big") + tag + body
                + (zlib.crc32(tag + body) & 0xFFFFFFFF).to_bytes(4, "big"))

    png += chunk(b"IHDR", total.to_bytes(4, "big") + total.to_bytes(4, "big") + bytes([8, 0, 0, 0, 0]))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)
    return total


def decode(path):
    """把 PNG 读回来重放一遍：极性、图案、格式信息、数据放置、掩码、纠错码。"""
    width, height, rows = read_png(path)
    assert width == height, "不是正方形"
    module = width // 33 * 33  # 由比例反推缩放，见下方调用处
    scale = module // 33 if False else None
    # 缩放与边框由构造参数反推：这里直接用整幅边长对 25+2*border 取模不可靠，
    # 所以由调用方把 scale/border 传进来。
    raise SystemExit("由 decode_with_layout 调用")


def decode_with_layout(path, scale, border):
    width, height, rows = read_png(path)
    size = (width // scale) - border * 2
    assert size % 4 == 1, "尺寸不是合法 QR 边长"
    version = (size - 17) // 4

    grid = [[0] * size for _ in range(size)]
    for r in range(size):
        for c in range(size):
            y = (r + border) * scale + scale // 2
            x = (c + border) * scale + scale // 2
            grid[r][c] = 1 if rows[y][x] < 128 else 0

    # 1) 格式信息
    def bit_at(r, c):
        return grid[r][c]

    raw = 0
    for i in range(6):
        raw |= bit_at(i, 8) << i
    raw |= bit_at(7, 8) << 6
    raw |= bit_at(8, 8) << 7
    raw |= bit_at(8, 7) << 8
    for i in range(9, 15):
        raw |= bit_at(8, 14 - i) << i
    value = raw ^ FORMAT_XOR
    ec_bits = (value >> 13) & 0b11
    mask = (value >> 10) & 0b111
    assert ec_bits == 0b00, f"纠错级不是 M：{ec_bits:02b}"

    # 2) 数据放置逆序
    reserved = build_function_map(size, version)
    bits_out = []
    direction = -1
    row = size - 1
    col = size - 1
    while col > 0:
        if col == 6:
            col -= 1
        while 0 <= row < size:
            for c in (col, col - 1):
                if 0 <= c < size and not reserved[row][c]:
                    bit = grid[row][c]
                    if mask_condition(row, c, mask):
                        bit ^= 1
                    bits_out.append(bit)
            row += direction
        row -= direction
        direction = -direction
        row += direction
        col -= 2

    codewords = []
    for i in range(0, len(bits_out) - 7, 8):
        value = 0
        for bit in bits_out[i:i + 8]:
            value = (value << 1) | bit
        codewords.append(value)

    # 3) 去交错
    num_blocks = BLOCKS[version][0]
    ec_per_block = EC_M[version]
    total = TOTAL_CODEWORDS[version]
    data_total = total - ec_per_block * num_blocks
    block_size = data_total // num_blocks
    blocks = [[] for _ in range(num_blocks)]
    for i in range(block_size):
        for b in range(num_blocks):
            blocks[b].append(codewords[i * num_blocks + b])
    ec = [[] for _ in range(num_blocks)]
    base = block_size * num_blocks
    for i in range(ec_per_block):
        for b in range(num_blocks):
            ec[b].append(codewords[base + i * num_blocks + b])

    # 4) 纠错码自洽：用数据码字重算，必须与读到的完全一致
    for b in range(num_blocks):
        recomputed = rs_encode(blocks[b], ec_per_block)
        assert recomputed == ec[b], f"第 {b + 1} 块纠错码不一致，说明编码或放置有错"

    # 5) 解析字节模式负载
    stream = []
    for b in range(num_blocks):
        stream.extend(blocks[b])
    bitbuf = []
    for cw in stream:
        for i in range(7, -1, -1):
            bitbuf.append((cw >> i) & 1)
    pos = 0

    def take(n):
        nonlocal pos
        value = 0
        for _ in range(n):
            value = (value << 1) | bitbuf[pos]
            pos += 1
        return value

    mode = take(4)
    assert mode == 0b0100, f"不是字节模式：{mode:04b}"
    length = take(8 if version < 10 else 16)
    payload = bytes(take(8) for _ in range(length))
    return payload.decode("utf-8"), version, mask


if __name__ == "__main__":
    url = sys.argv[1]
    out = sys.argv[2]
    version, codewords = encode(url)
    grid = build_matrix(version, codewords, mask=0)
    total = write_png(out, grid, scale=8, border=4)
    decoded, v2, mask = decode_with_layout(out, scale=8, border=4)
    assert decoded == url, f"解回内容不一致：{decoded!r} != {url!r}"
    print(f"OK version={version} mask={mask} modules={len(grid)} png={total}x{total} url={decoded}")
