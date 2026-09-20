#!/usr/bin/env python3
"""把思源黑体的原始字重文件裁剪成可打包的子集。

为什么需要这个脚本
------------------
思源黑体每个字重都是完整字库（65535 个字形、约 16 MB），七个字重加起来超过
110 MB。启动器只需要界面里可能出现的字符，因此必须裁剪后再打包，否则安装包
会被字体撑大一个数量级。

保留范围
--------
* GB2312 全字集 —— 保证简体中文界面、用户输入、日志输出都不会缺字。
* 拉丁基本区与拉丁补充区 —— 覆盖英文界面、数字、`AstraBrew` 这类专名。
* 常用标点、箭头、数学符号、几何图形、圈号、CJK 标点、假名、全角形式 ——
  覆盖版本号（`v1.18.0`）、分隔符（`·`）、状态标记（`✓`）等界面元素。

只处理界面真正用到的三档字重
----------------------------
界面代码只调用 `regular()`(400)、`medium()`(500)、`bold()`(700) 三档，
其余字重（ExtraLight 250 / Light 300 / Normal 350 / Heavy 900）没有调用点，
一并裁剪只会白占体积。

用法
----
    python subset_fonts.py <原始字体目录> <输出目录>

依赖 fonttools：

    pip install fonttools brotli

输出文件名为 ``SourceHanSansSC-<字重>.otf``，字体族名统一为
``Source Han Sans SC``，与 ``src/core/typography.rs`` 里的
``DEFAULT_FAMILY_NAME`` 保持一致。任何一侧改名都必须同步另一侧。
"""

from __future__ import annotations

import os
import sys

from fontTools import subset
from fontTools.ttLib import TTFont

# 需要打包的字重。键是原始文件名里的字重后缀，值是期望的 CSS 字重数值，
# 用于裁剪后回写 OS/2.usWeightClass，让 fontdb 能正确分档。
WANTED_WEIGHTS: dict[str, int] = {
    "Regular": 400,
    "Medium": 500,
    "Bold": 700,
}

# 统一的字体族名。必须与 typography.rs 的 DEFAULT_FAMILY_NAME 一致。
FAMILY_NAME = "Source Han Sans SC"

# 输出的字重名，下标与 WANTED_WEIGHTS 的数值对应，供 name 表使用。
WEIGHT_SUBFAMILY: dict[int, str] = {
    400: "Regular",
    500: "Medium",
    700: "Bold",
}


def build_charset() -> str:
    """构造要保留的字符集合。"""
    chars: set[str] = set()

    # GB2312 全字集：汉字与国标标点。用解码探测而不是硬编码码表，
    # 这样非法组合会被自动跳过。
    for high in range(0xA1, 0xFA):
        for low in range(0xA1, 0xFF):
            try:
                chars.add(bytes([high, low]).decode("gb2312"))
            except UnicodeDecodeError:
                pass

    # 拉丁、标点与符号区段。这些区间的选取依据是源码里真实出现过的字符：
    # 方框绘制字符用于控制台日志的分隔线与树形结构，界面里出现上千次；
    # 圈号与装饰符号用于状态标记；箭头与数学符号用于日志与提示。
    ranges = [
        (0x0020, 0x007F),  # 拉丁基本
        (0x00A0, 0x0100),  # 拉丁补充
        (0x2000, 0x2070),  # 常用标点
        (0x20A0, 0x20D0),  # 货币符号
        (0x2100, 0x2150),  # 字母式符号（℃、№ 等）
        (0x2190, 0x2200),  # 箭头
        (0x2200, 0x2300),  # 数学运算符
        (0x2400, 0x2450),  # 控制图形
        (0x2460, 0x2500),  # 圈号
        (0x2500, 0x2580),  # 方框绘制（控制台日志的 ├ │ └ 等）
        (0x25A0, 0x2700),  # 几何图形与杂项符号
        (0x3000, 0x3100),  # CJK 标点与假名
        (0xFF00, 0x10000),  # 全角形式与半角片假名
    ]
    for start, end in ranges:
        for codepoint in range(start, end):
            chars.add(chr(codepoint))

    chars.discard("\x00")
    return "".join(sorted(chars))


def subset_one(source: str, target: str, text: str, weight: int) -> int:
    """裁剪单个字重文件，返回输出体积（字节）。"""
    options = subset.Options()
    # 保留全部排版特性：中英文混排的光标定位、字距调整都依赖 GPOS/GSUB。
    options.layout_features = ["*"]
    # 保留全部 name 记录，裁剪后再统一改名，避免半途丢掉语言变体。
    options.name_IDs = ["*"]
    options.name_legacy = True
    options.name_languages = ["*"]
    options.notdef_outline = True
    options.recalc_bounds = True
    # 竖排相关的表对横排界面没有意义，去掉能省一点体积。
    options.drop_tables = ["VORG", "vhea", "vmtx", "BASE", "DSIG"]

    font = subset.load_font(source, options)
    subsetter = subset.Subsetter(options=options)
    subsetter.populate(text=text)
    subsetter.subset(font)

    _rename(font, weight)

    subset.save_font(font, target, options)
    font.close()
    return os.path.getsize(target)


def _rename(font: TTFont, weight: int) -> None:
    """把字体族名统一成 FAMILY_NAME，并写入正确的字重信息。

    原始文件的名字表并不一致：只有 Regular 与 Bold 把纯族名放在 nid 1，
    其余字重把「族名 + 字重」塞进 nid 1，真正的族名藏在 nid 16。fontdb 优先
    取 nid 16、缺失时退回 nid 1，因此七份文件虽然碰巧能聚成一组，但依赖的是
    偶然的命名巧合。这里显式规范化，让分组结果由我们决定而不是由偶然决定。
    """
    subfamily = WEIGHT_SUBFAMILY[weight]
    name_table = font["name"]

    # 先清掉所有族名/子族名记录，避免残留记录覆盖新值。
    for record in list(name_table.names):
        if record.nameID in (1, 2, 16, 17):
            name_table.names.remove(record)

    for platform_id, plat_enc_id, lang_id in ((3, 1, 0x409), (3, 1, 0x804), (1, 0, 0)):
        name_table.setName(FAMILY_NAME, 1, platform_id, plat_enc_id, lang_id)
        name_table.setName(subfamily, 2, platform_id, plat_enc_id, lang_id)
        name_table.setName(FAMILY_NAME, 16, platform_id, plat_enc_id, lang_id)
        name_table.setName(subfamily, 17, platform_id, plat_enc_id, lang_id)

    # 字重数值写进 OS/2：fontdb 判断字重只看这一处。
    font["OS/2"].usWeightClass = weight


def verify(source: str, target: str, text: str, weight: int) -> None:
    """裁剪后自检：确认源字体里**有的**字符一个都没丢，且字重写对了。

    自检基准必须取「源字体与目标字符集的交集」而不是目标字符集本身：请求的
    区间里必然有一部分码位源字体本来就没有（不间断空格、方向控制符、思源黑体
    不收录的个别符号），拿它们当缺失会把正常结果误判成失败。真正的风险是
    「源字体有、裁剪后却没了」，那才是会渲染成缺字方框的情形。
    """
    source_font = TTFont(source, lazy=True, fontNumber=0)
    target_font = TTFont(target, lazy=True, fontNumber=0)
    try:
        source_cmap = source_font.getBestCmap() or {}
        target_cmap = target_font.getBestCmap() or {}

        expected = {ord(ch) for ch in text if ord(ch) in source_cmap}
        missing = sorted(expected - set(target_cmap))

        actual_weight = target_font["OS/2"].usWeightClass
        # 抽样校验界面必然用到的字符，作为「不是空字体」的兜底。
        probes = "中文A18，。·→│┌√×"
        probe_missing = [ch for ch in probes if ord(ch) not in target_cmap]

        status = "OK" if not missing and actual_weight == weight and not probe_missing else "FAIL"
        print(
            f"  自检 {status}：源有 {len(source_cmap)} 字形 / 应保留 {len(expected)} 个，"
            f"成品 {len(target_cmap)} 字形，丢失 {len(missing)} 个，"
            f"字重 {actual_weight}（期望 {weight}）"
        )
        if probe_missing:
            print(f"  基础探针缺失：{''.join(probe_missing)}")
        if missing:
            preview = "".join(chr(c) for c in missing[:20])
            print(f"  丢失示例：{preview}")
        if status == "FAIL":
            raise SystemExit(f"{target} 自检未通过")
    finally:
        source_font.close()
        target_font.close()


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(__doc__)
        return 2

    source_dir, target_dir = argv[1], argv[2]
    os.makedirs(target_dir, exist_ok=True)
    text = build_charset()
    print(f"保留字符数：{len(text)}")

    total = 0
    for suffix, weight in WANTED_WEIGHTS.items():
        source = os.path.join(source_dir, f"SourceHanSansSC-{suffix}.otf")
        if not os.path.isfile(source):
            print(f"缺少源文件：{source}")
            return 1
        target = os.path.join(target_dir, f"SourceHanSansSC-{suffix}.otf")
        size = subset_one(source, target, text, weight)
        total += size
        print(f"{suffix:10s} {os.path.getsize(source) / 1048576:6.2f} MB -> {size / 1048576:6.2f} MB")
        verify(source, target, text, weight)

    print(f"合计 {total / 1048576:.2f} MB")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
