"""一键生成《界面设计稿v4（磨削方案模型）.html》。

步骤：① 从《界面修订稿（以轧辊为中心）.html》拆出 v3 样机 SVG（沿用页面的工作区底稿）→ build/v3/
      ② 按 screens.py 生成 v4 全部画面两档 SVG → build/out/
      ③ build_doc.py 拼装评审记录（data/）与画面，写回 docs/design/。
用法：python build.py            全部
      python build.py 5.2.2 5.3.1  只重画这几个画面（仍会重新拼装文档）
"""
import os, re, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
V3DOC = os.path.abspath(os.path.join(HERE, '..', '..', '界面修订稿（以轧辊为中心）.html'))

def extract_v3():
    out = os.path.join(HERE, 'build', 'v3'); os.makedirs(out, exist_ok=True)
    s = open(V3DOC, encoding='utf-8').read()
    heads = [(m.start(), re.sub('<[^>]+>', '', m.group(1))) for m in re.finditer(r'<h4>(.*?)</h4>', s)]
    for i, (pos, title) in enumerate(heads):
        seg = s[pos:heads[i + 1][0] if i + 1 < len(heads) else len(s)]
        for j, sv in enumerate(re.findall(r'<svg class="mk".*?</svg>', seg, flags=re.S)):
            open(os.path.join(out, f"{title.split()[0]}-{'std' if j == 0 else 'cmp'}.svg"), 'w', encoding='utf-8').write(sv)

def render(only):
    import mk, screens
    out = os.path.join(HERE, 'build', 'out'); os.makedirs(out, exist_ok=True)
    for sid, title, tag, cap, fn in screens.SCREENS:
        if only and sid not in only: continue
        for p in (mk.STD, mk.CMP):
            open(os.path.join(out, f'{sid}-{p.name}.svg'), 'w', encoding='utf-8').write(fn(p))

if __name__ == '__main__':
    only = sys.argv[1:]
    extract_v3()
    render(only)
    import build_doc  # noqa: F401  导入即拼装
