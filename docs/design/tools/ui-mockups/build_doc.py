import sys, re, html, os
sys.path.insert(0, os.path.dirname(__file__))
import mk, screens

HERE = os.path.dirname(os.path.abspath(__file__))
D = os.path.join(HERE, 'inputs')
BUILD = os.path.join(HERE, 'build')
DESIGN = os.path.abspath(os.path.join(HERE, '..', '..'))
V3 = os.path.join(DESIGN, '界面修订稿（以轧辊为中心）.html')
OUT = os.path.join(DESIGN, '界面设计稿v4（磨削方案模型）.html')
e = lambda s: html.escape(str(s), quote=False)

head = open(V3, encoding='utf-8').read()
head = head[:head.index('<body')]
head = re.sub(r'<title>.*?</title>', '<title>界面设计稿 v4</title>', head)

# ---------- 评审意见 ----------
def items(fn, prefix):
    out = []
    for ln in open(os.path.join(D, fn), encoding='utf-8'):
        m = re.match(rf'^({prefix}\d+)\s+(.*?)\s*(高|中|低)?\s*$', ln.strip())
        if m: out.append((m.group(1), m.group(2), m.group(3) or ''))
    return out

HANDLE = {
    'O1': ('不采纳', 'D1：改为不阻塞（I7），默认稳妥项；O2 的写法保留 → 5.2.2'),
    'O3': ('部分', 'D1：普通更新只提示 + 竖键 6；必须采用自动换 → 5.2.2'),
    'O5': ('部分', 'D3：不出现"冻结"；全机统一"设定值 / 本次值"；方案行叫"这支辊的磨法"'),
    'O6': ('部分', '按 I7 排竖键（1 方案 ▸ · 2 本次调整 ▸ · 5 示教起点 Z · 6 改用新模板…）→ 5.2.2'),
    'O9': ('部分', 'D4：不加列、就地显示；标记用竖条 + Δ（V12），不用橙底 → 5.3.1'),
    'O12': ('部分', 'D2：模板占第 4 格（I1），操作者只读'),
    'O13': ('采纳', '方案区路径条写辊号与状态（I2）；本稿未单独画"未选辊"的空状态'),
    'P4': ('部分', 'D5：新方案名 / 用途 / 叠图采纳；"设为当前"改为竖键 6 / 8（I10），不用勾选框 → 5.5.4'),
    'P17': ('不采纳', 'D2：模板占左栏第 4 格，Ctrl+5…7 不变（I1）'),
    'V12': ('部分', 'D4：角标用 Δ（I20 统一标记）'),
    'I15': ('采纳', '→ 5.3.1'),
}
WHERE = {
    'O2': '5.2.2', 'O4': '5.2.2、路径条', 'O7': '5.2.2', 'O8': '5.3.1', 'O10': '5.2.5', 'O11': '5.2.2（说明）', 'O14': '5.3.4、5.3.5',
    'O15': '5.2.1', 'O16': '5.8.1', 'O17': '5.4.1', 'O18': '5.8.2',
    'P1': '5.5.2', 'P2': '5.5.2、5.5.3', 'P3': '5.5.2、5.5.3', 'P5': '5.5.3', 'P6': '5.5.1', 'P7': '5.6.2', 'P8': '5.8.4', 'P9': '5.5.5',
    'P10': '5.5.5（竖键 6 复制范围）', 'P11': '5.6.1', 'P12': '5.4.1', 'P13': '5.8.2', 'P14': '5.8.3', 'P15': '5.3.4（设定折返 Z 在标题）',
    'P16': '5.8.1', 'P18': '5.10.1', 'P19': '5.4.3',
    'V1': '5.2.2、5.3.1', 'V2': '5.3.1', 'V3': '5.8.1、5.8.2', 'V4': '5.4.1', 'V5': '5.3.5', 'V6': '5.3.1、5.3.5', 'V7': '5.2.2', 'V8': '路径条',
    'V9': '—（v3 简图未重画）', 'V10': '5.8.2', 'V11': '5.2.3、5.5.4、5.5.5', 'V13': '全局', 'V14': '5.2.2', 'V15': '5.5.2、5.5.3', 'V16': '5.3.1',
    'V17': '5.3.4', 'V18': '5.5.5', 'V19': '叠图', 'V20': '5.3.1、5.5.3',
    'I1': '左栏', 'I2': '5.5.x 路径条', 'I3': '路径条"« 返回"', 'I4': '5.4.1', 'I5': '5.2.3、5.2.4、5.5.5', 'I6': '5.2.3', 'I7': '5.2.2', 'I8': '5.2.5',
    'I9': '5.5.x 路径条', 'I10': '5.5.4', 'I11': '5.5.2', 'I12': '5.6.2', 'I13': '5.6.2', 'I14': '5.3.1', 'I16': '5.8.4', 'I17': '5.5.5',
    'I18': '5.4.3', 'I19': '5.6.1、5.10.1、5.11.1', 'I20': '全局术语',
}

def review_table(title, its):
    rows = []
    for k, t, pr in its:
        h, where = HANDLE.get(k, ('采纳', '→ ' + WHERE.get(k, '')))
        if k in HANDLE and k in WHERE and '→' not in where: where += ' → ' + WHERE[k]
        cls = {'采纳': 'good', '不采纳': 'bad'}.get(h, '')
        rows.append(f'<tr><td class="num">{k}</td><td>{e(t)}</td><td>{pr}</td><td class="{cls}">{h}</td><td>{e(where)}</td></tr>')
    return (f'<h3>{title}</h3><div class="tw"><table><thead><tr><th>#</th><th>意见</th><th>级</th><th>处理</th><th>落在</th></tr></thead><tbody>'
            + ''.join(rows) + '</tbody></table></div>')

def keep_line(fn, tag):
    for ln in open(os.path.join(D, fn), encoding='utf-8'):
        if ln.startswith(tag): return ln[len(tag):].strip(' ：:')
    return ''

r1 = (review_table('操作工（O）', items('review_r1_PO.md', 'O')) +
      review_table('工艺员（P）', items('review_r1_PO.md', 'P')) +
      review_table('视觉设计（V）', items('review_r1_V.md', 'V')) +
      review_table('交互设计（I）', items('review_r1_I.md', 'I')))

dec = []
for ln in open(os.path.join(D, 'decisions_r1.md'), encoding='utf-8'):
    m = re.match(r'^(D\d+)\s+(.*?)：(.*)$', ln.strip())
    if m: dec.append(f'<tr><td class="num">{m.group(1)}</td><td>{e(m.group(2))}</td><td>{e(m.group(3))}</td></tr>')
    elif ln.startswith('不采纳汇总'): dec_rej = ln.strip()

r2 = open(os.path.join(D, 'review_r2.html'), encoding='utf-8').read() if os.path.exists(os.path.join(D, 'review_r2.html')) else ''

# ---------- 画面 ----------
GROUPS = [('5.1', '机床区 · JOG（手动）'), ('5.2', '机床区 · 作业（选辊 → 核对 → 下发）'), ('5.3', '机床区 · AUTO（自动）'),
          ('5.4', '轧辊区'), ('5.5', '方案区（新：取代辊形区、工艺区）'), ('5.6', '模板区（新）'), ('5.7', '砂轮区'),
          ('5.8', '记录区 · 查询与报表'), ('5.9', '诊断'), ('5.10', '调试（区域菜单进，制造商）'), ('5.11', '通用')]
TAGC = {'新': 'var(--ok)', '改': '#B8600A', '不变': 'var(--ink-3)'}
def key(sid): return tuple(int(x) for x in sid.split('.'))
scr = sorted(screens.SCREENS, key=lambda s: key(s[0]))
parts = []; toc6 = []
for g, gt in GROUPS:
    gid = 'g' + g
    toc6.append(f'<li><a href="#{gid}">{g} {gt}</a></li>')
    parts.append(f'<h3 id="{gid}">{g} {e(gt)}</h3>')
    for sid, title, tag, cap, fn in scr:
        if not (sid.startswith(g + '.') and sid.count('.') == 2): continue
        std = open(os.path.join(BUILD, 'out', f'{sid}-std.svg'), encoding='utf-8').read()
        cmp = open(os.path.join(BUILD, 'out', f'{sid}-cmp.svg'), encoding='utf-8').read()
        parts.append(f'<h4 id="p{sid}">{sid} {e(title)}</h4><p class="cap"><b style="color:{TAGC[tag]}">［{tag}］</b> {e(cap)}</p>'
                     f'<div class="pair"><div class="lab">标准档 1920×1080</div>{std}<div class="lab">紧凑档 1366×768（同一比例尺）</div>{cmp}</div>')
n_new = sum(1 for s in scr if s[2] == '新'); n_chg = sum(1 for s in scr if s[2] == '改'); n_same = sum(1 for s in scr if s[2] == '不变')

body = open(os.path.join(D, 'doc_body.html'), encoding='utf-8').read()
body = (body.replace('{{R1}}', r1).replace('{{DEC}}', ''.join(dec)).replace('{{DECREJ}}', e(dec_rej))
            .replace('{{R2}}', r2).replace('{{SCREENS}}', ''.join(parts)).replace('{{TOC6}}', ''.join(toc6))
            .replace('{{N}}', str(len(scr))).replace('{{NNEW}}', str(n_new)).replace('{{NCHG}}', str(n_chg)).replace('{{NSAME}}', str(n_same))
            .replace('{{KO}}', e(keep_line('review_r1_PO.md', 'O 坚持'))).replace('{{KP}}', e(keep_line('review_r1_PO.md', 'P 坚持')))
            .replace('{{KV}}', e(keep_line('review_r1_V.md', 'V 坚持'))).replace('{{KI}}', e(keep_line('review_r1_I.md', 'I 坚持'))))
open(OUT, 'w', encoding='utf-8').write(head + body)
print(OUT, len(head + body))
