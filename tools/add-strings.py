#!/usr/bin/env python3
"""把一批文案写进两份 resw（zh-CN / en-US）。

用法：
    python tools/add-strings.py batch.json

batch.json 形如：
    {
      "Version.Page.Title": ["版本管理", "Versions"],
      "Version.Page.Subtitle": ["...", "..."]
    }

已存在的 key 原位替换文本，不存在的追加到 `</root>` 之前。
纯文本编辑，不经过 XML 解析器：resw 顶部的 xsd:schema 有命名空间前缀，
交给 ElementTree 重写会被改成 ns0/ns1，MRT 那边不一定认。
"""

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FILES = {
    "zh": ROOT / "Strings" / "zh-CN" / "Resources.resw",
    "en": ROOT / "Strings" / "en-US" / "Resources.resw",
}

ENTRY = '  <data name="{key}" xml:space="preserve">\n    <value>{value}</value>\n  </data>\n'


def escape(text: str) -> str:
    return (
        text.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
    )


def apply(text: str, pairs: dict, index: int) -> tuple[str, int, int]:
    added = updated = 0
    for key, values in pairs.items():
        value = escape(values[index])
        pattern = re.compile(
            r'(<data name="' + re.escape(key) + r'"[^>]*>\s*<value>)(.*?)(</value>)',
            re.S,
        )
        if pattern.search(text):
            new_text, count = pattern.subn(lambda m: m.group(1) + value + m.group(3), text, count=1)
            if count and new_text != text:
                updated += 1
            text = new_text
        else:
            cut = text.rindex("</root>")
            text = text[:cut] + ENTRY.format(key=key, value=value) + text[cut:]
            added += 1
    return text, added, updated


def main() -> int:
    if len(sys.argv) != 2:
        print("用法：python tools/add-strings.py batch.json")
        return 2

    pairs = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))

    for index, (lang, path) in enumerate(FILES.items()):
        text = path.read_text(encoding="utf-8")
        text, added, updated = apply(text, pairs, index)
        path.write_text(text, encoding="utf-8")
        print(f"{lang}: 新增 {added} 条 / 更新 {updated} 条 → {path.relative_to(ROOT)}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
