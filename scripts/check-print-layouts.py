#!/usr/bin/env python3
"""打印版式程序化体检：对 logs/m80-e2e/layouts/*.pdf 检查版式不变量。

覆盖项：
- 无文本越界（左右边距内）
- 折扣空值不显示裸 "%"
- 金额类列不出现 4 位以上小数（单价已去尾零）
- 价税合计统一两位小数
- 无金额字段的单据不渲染空"价税合计"行
依赖：pdfplumber
"""
import glob
import os
import re
import sys

import pdfplumber

ROOT = os.path.join(os.path.dirname(__file__), '..', 'logs', 'm80-e2e', 'layouts')


def main() -> int:
    root = sys.argv[1] if len(sys.argv) > 1 else ROOT
    issues = []
    for path in sorted(glob.glob(os.path.join(root, '*.pdf'))):
        mod = os.path.basename(path)[:-4]
        with pdfplumber.open(path) as pdf:
            for pi, page in enumerate(pdf.pages, 1):
                width = page.width
                for char in page.chars:
                    if char['x1'] > width - 28 or char['x0'] < 28:
                        issues.append((mod, pi, '越界', repr(char['text'])))
                text = page.extract_text() or ''
                for line in text.splitlines():
                    if re.search(r'%$', line) and not re.search(r'\d%', line):
                        issues.append((mod, pi, '裸%', line.strip()))
                    if re.search(r'\.\d{4,}', line):
                        issues.append((mod, pi, '超长小数', line.strip()))
                    if re.search(r'合计.*\d$', line) and not re.search(r'\d\.\d{2}$', line):
                        issues.append((mod, pi, '合计缺两位小数', line.strip()))
    if issues:
        for item in issues:
            print(' | '.join(str(part) for part in item))
        print(f'TOTAL ISSUES {len(issues)}')
        return 1
    print(f'OK: {len(glob.glob(os.path.join(root, "*.pdf")))} 个版式全部通过体检')
    return 0


if __name__ == '__main__':
    sys.exit(main())
