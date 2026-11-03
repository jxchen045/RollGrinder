from mk import *

ROLL = 'R-2026-031'
PLAN = '凸度300 · 工作辊标准'
PATH_M = f'辊 {ROLL} · {PLAN}（方案 3）· Ø600.350（台账）'
L = 1800.0
SCREENS = []

def screen(sid, title, tag, cap):
    def deco(fn):
        SCREENS.append((sid, title, tag, cap, fn)); return fn
    return deco

def split(work, *fr, gap=None, vertical=False):
    x0, y0, x1, y1 = work
    g = gap if gap is not None else 6
    tot = sum(fr); n = len(fr)
    if vertical:
        span = y1 - y0 - g * (n - 1); out = []; y = y0
        for f in fr:
            h = span * f / tot; out.append((x0, y, x1, y + h)); y += h + g
        return out
    span = x1 - x0 - g * (n - 1); out = []; x = x0
    for f in fr:
        w = span * f / tot; out.append((x, y0, x + w, y1)); x += w + g
    return out

LAST_WIN = {}

def win(c, r, title, active=False, right='', rc=None):
    LAST_WIN['r'] = r; LAST_WIN['right'] = bool(right)
    return window(c, r[0], r[1], r[2] - r[0], r[3] - r[1], title, active, right, rc)

def profile_chart(c, r, legend, series, note='纵向放大 ×900', end=40, real=None, marks=(), extra_v=(), tol=None):
    """辊形图。"纵向放大"写进所在窗口的标题栏右侧（R3-7），图内顶部只留图例与复位视图；tol 给出时沿最后一条曲线画 ±公差带。"""
    x0, y0, x1, y1 = r
    p = c.p
    wr = LAST_WIN.get('r')
    if note and wr and not LAST_WIN.get('right') and wr[0] <= x0 <= wr[2] and wr[1] <= y0 <= wr[3]:
        c.text(wr[2] - 8, wr[1] + p.wtH * 0.68, note, p.wtRF, C['ink'], 'end')
    bands = [(-L / 2, -L / 2 + end, 'url(#hatch)', 1, ''), (L / 2 - end, L / 2, 'url(#hatch)', 1, '')]
    v = [(0, '中心 0', '#8D97A3', '6 3 2 3')] + list(extra_v)
    X, Y = chart(c, x0, y0, x1 - x0, y1 - y0, series, (-L / 2, L / 2), (-100, 400), xlab='Z mm（中心 0，头架 ← → 尾架）',
                 ylab='直径量 µm', legend=legend, xt=[-900, -450, 0, 450, 900], yt=[-100, 0, 100, 200, 300, 400],
                 vlines=v, bands=bands, note='', marks=marks)
    if tol:
        pts, col, sw, dash = series[-1]
        up = ' L'.join(f'{X(a):.1f},{Y(b + tol):.1f}' for a, b in pts)
        dn = ' L'.join(f'{X(a):.1f},{Y(b - tol):.1f}' for a, b in reversed(pts))
        c.path('M' + up + ' L' + dn + ' Z', C['okLine'], 1, fill=C['okSoft'], op=0.9)
        c.path('M' + ' L'.join(f'{X(a):.1f},{Y(b):.1f}' for a, b in pts), col, sw, dash=dash)
    return X, Y

def cr(c0=300, end=40, Lr=L):
    return [(z - Lr / 2, v) for z, v in crown(Lr, c0, end, 80)]

def wavy(c0=300):
    import math
    return [(z, v + 8 * math.sin(z / 37.0) + 30) for z, v in cr(c0)]

def steps_grid(c, r, items, cols=3):
    """程序步骤（R3-3）：3 列网格，块高 54 / 44（戴手套可点）；开 = #3D5466 底白字 ✓，关 = 白底框 ○。"""
    p = c.p; x0, y0, x1, y1 = r; g = 8
    h = 54 if p.name == 'std' else 44
    w = (x1 - x0 - g * (cols - 1)) / cols
    for i, (t, on) in enumerate(items):
        x = x0 + (i % cols) * (w + g); y = y0 + (i // cols) * (h + g)
        s_ = (t + ' ✓') if on else ('○ ' + t)
        c.rect(x, y, w, h, '#3D5466' if on else '#FFFFFF', None if on else C['border'])
        c.text(x + w / 2, y + h * 0.64, s_, p.f, '#FFFFFF' if on else C['ink2'], 'middle')
    return y0 + ((len(items) + cols - 1) // cols) * (h + g)

def gray_title(svg, title, p):
    """把某个窗口的青色标题改成灰色（焦点不在这里，R3-5）。"""
    x, y, w_, h = find_win(svg, title)
    head = f'<rect x="{x:.1f}" y="{y:.1f}" width="{w_:.1f}" height="{p.wtH:.1f}" fill="#0199C8" />'
    svg = svg.replace(head, head.replace('#0199C8', C['hdr']))
    def fix(m):
        yy = float(m.group(1))
        return m.group(0).replace('fill="#FFFFFF"', f'fill="{C["ink"]}"') if y < yy < y + p.wtH else m.group(0)
    return re.sub(r'<text x="[\d.]+" y="([\d.]+)"[^>]*fill="#FFFFFF"[^>]*>', fix, svg)

def steps_chips(c, x, y, items):
    """程序步骤（V1）：开 = #3D5466 底白字 ✓；关 = 1px 框 ○。"""
    p = c.p; h = p.fieldH * 0.9; xx = x
    for t, on in items:
        s = (t + ' ✓') if on else ('○ ' + t)
        w = tw(s, p.f) + 22
        c.rect(xx, y, w, h, '#3D5466' if on else '#FFFFFF', None if on else C['border'])
        c.text(xx + w / 2, y + h * 0.68, s, p.f, '#FFFFFF' if on else C['ink2'], 'middle')
        xx += w + 8
    return y + h

def note_row(c, x, y, w, kind, icon, title, right='', state='', sub=''):
    """本次注意区一条（V14）：info / ask / warn / red；有 sub 时两行。"""
    p = c.p; h = (56 if p.name == 'std' else 46) if not sub else (74 if p.name == 'std' else 62)
    if kind == 'warn':
        c.rect(x, y, w, h, C['warnSoft'], C['warnLine']); bar = '#B8600A'
    elif kind == 'red':
        c.rect(x, y, w, h, '#FBE9E7', '#D99A93'); bar = '#8E1B1B'
    elif kind == 'ok':
        c.rect(x, y, w, h, C['okSoft'], C['okLine']); bar = C['ok']
    else:
        c.rect(x, y, w, h, '#FFFFFF', C['border']); bar = '#6D7F8D' if kind == 'info' else '#3D5466'
    if state == 'now': c.rect(x, y, w, h, 'none', '#3D5466', 2)
    c.rect(x, y, 6, h, bar)
    ty = y + (h * 0.66 if not sub else p.f * 1.35)
    c.text(x + 22, ty, icon, p.f, bar, 'middle', 700)
    c.text(x + 40, ty, title, p.f, C['ink'], weight=500)
    if sub: c.text(x + 40, ty + p.fs * 1.45, sub, p.fs, C['ink2'])
    if right:
        col = {'now': '#3D5466', 'done': C['ok']}.get(state, C['ink2'])
        c.text(x + w - 10, ty + (p.fs * 1.45 if sub else 0), right, p.fs, col, 'end', 700 if state == 'now' else 400)
    return y + h + 4


# =========================== 作业 ===========================
@screen('5.2.2', '核对页（一页）', '改', '"这支辊的磨法"写辊形名 + 工艺名，方案号小字。四项输入：本次磨削量为主；磨前直径带来源徽标［台账］/［实测］；'
        '目标直径固定，磨前实测差由粗磨吸收（与现有 StockAdjustment 一致）；磨削起点 Z 是机床坐标（示教值，徽标"沿用上次"），下方小字换算到辊身中心。'
        '图只画设定辊形与 ±公差带；刚测过磨前时再叠"磨前实测"、换了方案时叠"原方案"。程序步骤 3 × 2 大块（54 / 44 px）。'
        '"本次注意"只陈述、不拦人：长度对齐、模板已更新、上次调整都默认取稳妥项（按方案、按设定值），要改用竖键 6 / 2。竖键 8 只给"确认下发"。')
def s522(p):
    c, w = shell(p, 'machine', 'JOG', PATH_M + ' · 作业未下发', machine=True, clock='2026-11-02 10:12',
                 chan=('// 复位 · 已回参考点', '机床 ● 已起动', '作业 › 核对'),
                 vkeys=['方案 ▸', '本次调整 ▸', '测量磨前直径', '按最大可磨量', '示教起点 Z', '改用新模板…', ('✕ 取消', 'cancel'), ('✓ 确认下发', 'ok')],
                 hkeys=['作业', '手动磨削', '测量臂', '尾架', '头架拨盘', '托瓦', '测量对中', '辅助循环 ▸'], hk_on=0,
                 dialog='核对全部通过：✓ 确认下发写入 NC，下发后转自动页，在按钮板上按循环启动', ask=True)
    L_, R_ = split(w, 0.45, 0.55)
    x0, y0, x1, y1 = win(c, L_, f'核对：{ROLL}', True, '工作辊 · F3 上')
    W = x1 - x0 - 16; X = x0 + 8
    y = y0 + 6
    c.text(X + 4, y + p.f * 1.1, '这支辊的磨法', p.f, C['ink2'])
    c.text(X + W - 4, y + p.f * 1.1, PLAN, p.L2 if p.name == 'std' else 24, C['ink'], 'end', 700)
    y += p.f * 1.55
    c.text(X + W - 4, y + p.fs * 0.9, '方案 3 · 名义 1800.0 / 实测 1798.0 mm', p.fs, C['ink2'], 'end')
    y += p.fs * 1.6
    y = field(c, X, y, W, '磨前直径', '600.350', 'mm', lbadge=('台账', 'info'))
    y = field(c, X, y, W, '本次磨削量', '0.350', 'mm', 'sel')
    c.text(X + 4, y + p.fieldH * 0.7, '目标直径', p.f, C['ink'])
    c.text(X + W - tw('mm', p.fs) - 22, y + p.fieldH * 0.71, '600.000', p.fv, C['ink'], 'end', mono=True)
    c.text(X + W - tw('mm', p.fs) - 8, y + p.fieldH * 0.66, 'mm', p.fs, C['ink2'])
    y += p.fieldH
    c.text(X + 4, y + p.fs * 0.95, '目标直径固定；磨前实测差由粗磨吸收', p.fs, C['ink2'])
    y += p.fs * 1.5
    y = field(c, X, y, W, '磨削起点 Z', '140.0', 'mm', lbadge=('沿用上次', 'info'))
    y -= (4 if p.name == 'std' else 2)
    c.text(X + 4, y + p.fs * 0.6, '机床坐标 · 距辊身中心 −760', p.fs, C['ink2'])
    y += p.fs * 1.3
    y = banner(c, X, y, W, '✓ 7 项核对通过', 'ok', '展开 ▸')
    c.text(X + 2, y + p.fs * 1.2, '本次注意 · 3 条，都不用回答', p.fs, C['ink2'], weight=700)
    y += p.fs * 1.6
    y = note_row(c, X, y, W, 'info', 'i', '长度：实测比名义短 2.0 mm', sub='两端各截 1.0 mm，在端部非工作区（40 mm）内')
    y = note_row(c, X, y, W, 'info', '↑', '模板已更新：凸度 300 → 280', '竖键 6 可改用', sub='普通更新 · 本次照旧用方案 3')
    y = note_row(c, X, y, W, 'info', 'Δ', '上次调整 2 项（09-28 李工），本次未沿用', '本次调整 ▸ 载入',
                 sub='拖板 2300→2100 mm/min · 光磨 1→2 道')
    if p.name == 'std':
        c.text(X + 2, y + p.fs * 1.4, '本次调整：无（全部按设定值）', p.fs, C['ink2'])
    A, B = split(R_, 0.66, 0.34, vertical=True)
    x0, y0, x1, y1 = win(c, A, '磨成什么样（含程序倒角）')
    profile_chart(c, (x0 + 4, y0 + 4, x1 - 4, y1 - 4), '━ 方案 3（设定）  ▭ 公差带 ±5 µm  ▨ 端部非工作区',
                  [(cr(), C['target'], 3, None)], tol=5)
    x0, y0, x1, y1 = win(c, B, '程序步骤（本次，点一下切换）')
    steps_grid(c, (x0 + 8, y0 + 8, x1 - 8, y1 - 8), [('磨前测量', True), ('磨后测量', True), ('在线测量', True),
                                                       ('打印磨后', True), ('安装误差', False), ('涡流探伤', False)])
    return finish(c)

JOGK = ['作业', '手动磨削', '测量臂', '尾架', '头架拨盘', '托瓦', '测量对中', '辅助循环 ▸']
AUTOK = ['补偿', '程序段', '道次表', '工序跳转…', '提前结束…', '磨削记录', '作业']

def wrect(svg, title):
    x, y, w_, h = find_win(svg, title); return (x, y, x + w_, y + h)

# ---------- 5.2.1 待磨清单 ----------
@screen('5.2.1', '作业首页：待磨清单', '改', '焦点在辊号框时只有框内为青色，列表标题灰（R3-5）；按方向键进列表时列表标题变青。右边"这支辊的磨法"：辊形名 + 工艺名（L2）、两个徽标（Δ 上次有调整、↑ 模板已更新）、中心为 0 的辊形图；不放来源、冻结等管理信息（O15）。')
def s521(p):
    def draw(c, p, src):
        r = wrect(src, 'R-2026-031 · 下次怎么磨')
        x0, y0, x1, y1 = win(c, r, 'R-2026-031 · 这支辊的磨法')
        X = x0 + 6; W = x1 - x0 - 12
        y = kv(c, X, y0 + 2, W, [('类型 · 用途', '工作辊 · F3 上'), ('辊身 名义 / 实测', '1800.0 / 1798.0'),
                                  ('当前 / 报废', '600.350 / 585.0'), ('剩余可磨', '15.35 mm · 约 40 次')])
        c.text(X + 4, y + p.f * 1.5, PLAN, p.L2 if p.name == 'std' else 24, C['ink'], weight=700)
        c.text(X + W - 4, y + p.f * 1.5, '方案 3', p.fs, C['ink2'], 'end')
        y += p.f * 2.1
        bx = X + 4
        bx += badge(c, bx, y + p.fs * 1.1, 'Δ 上次有调整', 'info') + 8
        badge(c, bx, y + p.fs * 1.1, '↑ 模板已更新', 'warn')
        y += p.fs * 2.0
        profile_chart(c, (X, y, x1 - 6, y1 - 6), '━ 方案 3（设定）', [(cr(), C['target'], 3, None)], note='纵向放大 ×900')
    svg = patch('5.2.1', p, drops=[wrect(v3svg('5.2.1', p), 'R-2026-031 · 下次怎么磨')], draw=draw)
    svg = svg.replace('stroke="#0199C8" stroke-width="2"', f'stroke="{C["border"]}" stroke-width="1"')
    return gray_title(svg, '待磨清单', p)

# ---------- 5.2.3 选方案（作业：换方案） ----------
def plan_picker(p, purpose='job'):
    if purpose == 'job':
        vk = [('本辊方案', 'on'), '其他辊 ▸', '模板 ▸', '', '', ('设为这支辊的\n当前方案…', 'off'), ('✕ 取消', 'cancel'), ('✓ 仅本次用', 'ok')]
        dlg = '本次改用方案 2（凸度250）？✓ 仅本次用 = 以后仍按方案 3，下一步选原因；设为当前… 要管理员（不自动取消）'
        crumb = '作业 › 核对 › 换方案'
    else:
        vk = ['试磨', '临时调机架', '设备原因', '返磨', '其他…', '', ('✕ 取消', 'cancel'), '']
        dlg = '仅本次用 方案 2 · 凸度250：选一个原因（必选，写进这次记录；不自动取消）'
        crumb = '作业 › 核对 › 换方案 › 原因'
    if True:
        c, w = shell(p, 'machine', 'JOG', PATH_M + ' · 作业未下发', machine=True, clock='2026-11-02 10:12',
                     chan=('// 复位 · 已回参考点', '机床 ● 已起动', crumb), vkeys=vk, hkeys=JOGK, hk_on=0,
                     dialog=dlg, ask=True, modal=True)
    L_, R_ = split(w, 0.42, 0.58)
    x0, y0, x1, y1 = win(c, L_, f'{ROLL} 的方案（3）', True, '用途：全部 ▸')
    tbl(c, x0, y0, x1 - x0, [('方案', 1.0), ('辊形 · 工艺', 2.6), ('用途', 0.7, 'l', True), ('最近', 0.8, 'r')],
        [[{'t': '★ 3', 'b': True}, '凸度300 · 工作辊标准', 'F3', '10-28'],
         ['2', '凸度250 · 工作辊标准', 'F3', '09-15'],
         ['1', '圆柱 · 支承过渡', 'F4', '08-02']], sel=1)
    A, B = split(R_, 0.5, 0.5, vertical=True)
    x0, y0, x1, y1 = win(c, A, '当前 vs 本次')
    tbl(c, x0, y0, x1 - x0, [('项', 1.4), ('当前 · 方案 3', 1.3, 'r'), ('本次 · 方案 2', 1.3, 'r')],
        [['辊形', '凸度300', {'t': '凸度250', 'b': True}], ['凸度 µm（直径）', '300', {'t': '250', 'b': True}],
         ['工序 5 光磨道次', '1', {'t': '2', 'b': True}], ['长度对齐', '两端各截 1.0', '两端各截 1.0'],
         ['标准余量 / 预计时长', '0.350 · 64 min', {'t': '0.350 · 68 min', 'b': True}]], rowh=p.rowH * 0.86)
    if purpose == 'reason':
        c.text(x0 + 8, y1 - 12, '仅本次用 → 原因：（请在竖键选）', p.f, '#8A4A00', weight=700)
    x0, y0, x1, y1 = win(c, B, '叠图')
    profile_chart(c, (x0 + 4, y0 + 4, x1 - 4, y1 - 4), '━ 方案 2（本次）  ╍ 方案 3（当前）',
                  [(cr(300), '#8A5A00', 2, '8 5'), (cr(250), C['target'], 3, None)])
    return finish(c)

@screen('5.2.3', '换方案（选方案子视图 · 作业用）', '改', '取代 v3 的"换辊形 / 换程序"。同一个选方案子视图（D8）：左这支辊的方案，竖键切范围（本辊 / 其他辊 / 模板）；'
        '右"当前 vs 本次"对照 + 叠图（V11 线型）。竖键 8 仅本次用（随后选原因，同 v3 5.2.4）· 6 设为当前…（要管理员）· 7 取消。')
def s523(p):
    return plan_picker(p, 'job')

# ---------- 5.2.5 本次调整 ----------
@screen('5.2.5', '本次调整（子视图）', '新', '只列可调参数，允许范围写绝对值（O10）；竖键 1 载入上次，逐项标"上次"（I8）；右边改选中的一项：大号 −/+ 步进（≥ 60 px）与范围条。'
        '不能调的列在下方带 🔒。✓ 用于本次 = 只这一次，方案不变。')
def s525(p):
    c, w = shell(p, 'machine', 'JOG', PATH_M + ' · 作业未下发', machine=True, clock='2026-11-02 10:12',
                 chan=('// 复位 · 已回参考点', '机床 ● 已起动', '作业 › 核对 › 本次调整'),
                 vkeys=['载入上次\n（09-28）', '恢复这一项', '', '', '', '全部恢复\n设定值…', ('✕ 取消', 'cancel'), ('✓ 用于本次', 'ok')],
                 hkeys=JOGK, hk_on=0, dialog='已载入上次 2 项（标"上次"）。拖板 2100 在 2070–2530 内。✓ 用于本次 = 只这一次，方案 3 不变', ask=True)
    L_, R_ = split(w, 0.62, 0.38)
    x0, y0, x1, y1 = win(c, L_, '本次调整 2 项 · 可调参数', True, '方案 3')
    rows = [['3 半精磨', '拖板 mm/min', '2300', {'t': '2100', 'chg': True}, '2070–2530', {'t': '上次', 'badge': 'info'}],
            ['3 半精磨', '头架 r/min', '28', '28', '25–31', ''],
            ['3 半精磨', '周期进给 µm', '10', '10', '≤ 10', ''],
            ['4 精磨', '拖板 mm/min', '800', '800', '720–880', ''],
            ['4 精磨', '折返时间 s', '1.0', '1.0', '0.5–1.5', ''],
            ['5 光磨', '光磨道次', '1', {'t': '2', 'chg': True}, '≥ 1', {'t': '上次', 'badge': 'info'}]]
    yy = tbl(c, x0, y0, x1 - x0, [('工序', 0.85), ('参数', 1.6), ('设定值', 0.75, 'r'), ('本次值', 1.15, 'r'), ('允许', 1.0, 'r'), ('', 0.55)], rows, sel=0)
    # 竖条（V12）
    rh = p.rowH; hy = y0 + p.hdrH
    for i in (0, 5): c.rect(x0, hy + i * rh + 3, 4, rh - 6, '#3D5466')
    yy += 10
    c.text(x0 + 8, yy + p.fs, '🔒 不能调（要改请工艺员改方案）：砂轮线速度 · 变速 · 辊形 · 工序顺序 · 增删工序', p.fs, '#404C58')
    x0, y0, x1, y1 = win(c, R_, '改这一项：3 半精磨 · 拖板速度')
    X = x0 + 10; W = x1 - x0 - 20; y = y0 + 12
    c.text(X, y + p.fs, '设定值 2300 · 上次 2100', p.fs, C['ink2'])
    y += p.fs * 1.8
    big = p.L1 if p.name == 'std' else 40
    c.rect(X, y, W, big * 1.5, C['cur'])
    c.text(X + W - 12, y + big * 1.15, '2100', big, C['ink'], 'end', 600, mono=True)
    c.text(X + 10, y + big * 1.0, 'mm/min', p.fs, C['ink2'])
    y += big * 1.5 + 12
    bw = (W - 12) / 2; bh = 64 if p.name == 'std' else 54
    for i, t in enumerate(['−  50', '+  50']):
        c.rect(X + i * (bw + 12), y, bw, bh, C['key'], C['border'])
        c.text(X + i * (bw + 12) + bw / 2, y + bh * 0.66, t, p.L2 if p.name == 'std' else 26, C['ink'], 'middle', 700)
    y += bh + 22
    c.text(X, y, '允许 2070 – 2530（设定 ±10%）', p.fs, C['ink2'])
    y += 22
    c.rect(X, y, W, 14, '#FFFFFF', C['border'])
    fx = lambda v: X + (v - 2000) / 600 * W
    c.rect(fx(2070), y, fx(2530) - fx(2070), 14, '#C9D3DC')
    c.rect(fx(2300) - 1, y - 6, 2, 26, '#6D7F8D'); c.text(fx(2300), y + 40, '设定 2300', p.fs * 0.9, C['ink2'], 'middle')
    c.rect(fx(2100) - 3, y - 8, 6, 30, '#1B2228'); c.text(fx(2100), y + 40, '本次 2100', p.fs * 0.9, C['ink'], 'middle', 700)
    y += 70
    if p.name == 'std':
        c.text(X, y, '超出范围不收；机床上限 3000 mm/min', p.fs, C['ink2'])
    return finish(c)

# ---------- 5.3.1 自动磨削（参数矩阵就地改） ----------
@screen('5.3.1', '自动磨削（AUTO 基本画面）', '改', '参数矩阵就地改（D6）：Δ 本次调整（竖条 + Δ，灰小字设定值 → 本次值）；⧗ 等待生效（橙虚框，"第 4 道起" = 本工序第 4 道）；🔒 不可调；'
        '等待期间竖键 8 灰、对话行说明；拖板实际值 800 要到第 4 道才变。误差图横轴改为中心 0，砂轮位置同时写机床坐标。实测直径窗标题改灰（V1）。"道"只指工序内的往复道次。')
def s531(p):
    src = v3svg('5.3.1', p)
    mr = wrect(src, '参数矩阵（改的格标红，下一道次生效）')
    er = wrect(src, '误差曲线')
    dr = wrect(src, '实测直径')
    def draw(c, p, src):
        # 参数矩阵
        x0, y0, x1, y1 = win(c, mr, '参数矩阵（本次调整 2 项 · 1 项等待生效）', right='改动 #7 ⧗')
        W = x1 - x0
        cols = [('参数', 1.5), ('4 精磨', 1.0, 'r'), ('5 光磨', 1.0, 'r'), ('6 测量', 0.6, 'r')]
        tbl(c, x0, y0, W, cols, [['拖板速度 mm/min', '', '600', '—'], ['光磨道次', '—', '', '—'], ['砂轮线速度 m/s', '', '', '—']], rowh=p.rowH)
        tot = 4.1; cx = [x0 + W * f / tot for f in (1.5, 2.5, 3.5)]
        ry = y0 + p.hdrH; rh = p.rowH
        mk_ = max(p.fs, 16)
        c.rect(cx[0] + 3, ry + 3, cx[1] - cx[0] - 6, rh - 6, C['warnSoft'], '#B8600A', 2, dash='6 4')
        c.text(cx[1] - 10, ry + rh * 0.65, '720', p.f, '#1B2228', 'end', 700, mono=True)
        c.text(cx[0] + 10, ry + rh * 0.65, '⧗ 第 4 道起', mk_, '#8A4A00', weight=700)
        c.rect(cx[1] + 2, ry + rh + 3, 4, rh - 6, '#3D5466')
        c.text(cx[1] + 12, ry + rh * 1.62, 'Δ', mk_, '#3D5466', weight=700)
        chg_value(c, cx[2] - 10, ry + rh * 1.65, '2', p.f, 'r', old='1')
        for k in (0, 1):
            c.rect(cx[k] + 2, ry + 2 * rh + 1, cx[k + 1] - cx[k] - 4, rh - 2, '#EBF0F6')
            c.text(cx[k + 1] - 10, ry + rh * 2.65, '35.0', p.f, '#404C58', 'end', mono=True)
            c.text(cx[k] + 12, ry + rh * 2.65, '🔒', mk_, '#6D7F8D')
        # 误差图（中心 0）
        x0, y0, x1, y1 = win(c, er, '误差曲线', True, '竖键 1–5 换曲线')
        import math
        pts = [(z, 3.2 * math.sin(z / 61) + 1.4 * math.sin(z / 17)) for z in range(-900, 901, 15)]
        X, Y = chart(c, x0 + 4, y0 + 4, x1 - x0 - 8, y1 - y0 - 8, [(pts, C['meas'], 2, None)], (-900, 900), (-10, 10),
                     xlab='Z mm（中心 0）', ylab='误差 µm（直径）', legend='— 实测 − 设定  ▭ 公差带 ±5 µm',
                     xt=[-900, -450, 0, 450, 900], yt=([-10, -5, 0, 5, 10] if p.name == 'std' else [-5, 5]),
                     hbands=[(-5, 5, C['okSoft'], 1)], vlines=[(280, '砂轮 +280（机床 Z 1180.3）' if p.name == 'std' else '砂轮 +280', C['wheel'], None)],
                     note='RMS 4.1 · 峰谷 12.6' if p.name == 'std' else '')
        # 实测直径标题改灰
        c.rect(dr[0], dr[1], dr[2] - dr[0], p.wtH, C['hdr'])
        c.text(dr[0] + 8, dr[1] + p.wtH * 0.72, '实测直径', p.wtF, C['ink'], weight=500)
        c.text(dr[2] - 8, dr[1] + p.wtH * 0.68, 'mm', p.wtRF, C['ink'], 'end')
    over = dict(hkeys=AUTOK, hk_on=None, clock='2026-11-02 10:58', subs_=None, dialog='改动 #7：工序 4 精磨 拖板 800 → 720 已写入，等 NC 在精磨第 4 道开始时采用；生效前不能再改（可"放弃改动"）',
                ask=False, chan=('// 自动 · 运行', '◈ 运行', '4 精磨 · 第 3/6 道 · 改动 #7 ⧗'),
                path='作业 J20261102-04 · 辊 R-2026-031 · 凸度300 · 工作辊标准（方案 3）· 本次调整 2 项')
    def vk(c0):
        return c0
    over.pop('subs_')
    svg = patch('5.3.1', p, subs=[('46.2 m/s', '35.0 m/s'), ('600.214', '600.034'), ('0.214 mm', '0.034 mm')], over=over, drops=[mr, er, (dr[0], dr[1], dr[2], dr[1] + p.wtH + 1)], draw=draw)
    # 竖键 8"下发改动"变灰
    svg = svg.replace(f'fill="{C["ok"]}" />', f'fill="{C["key"]}" />', 1)
    svg = re.sub(r'(<text[^>]*fill=")#FFFFFF("[^>]*>✓ 下发改动</text>)', r'\g<1>' + C['keyOff'] + r'\2', svg)
    return svg

def pass_table(c, x, y, w, rows, cur=None, compact_drop=True):
    """道次表（V17）。rows: ('_g', 名) 组头 | ('_miss', 文) 缺号 | (道, 左Z, 左停, 左倍, 右Z, 右停, 右倍, 补偿, 平均, 非100)。"""
    p = c.p
    cols = [('道', 0.45, 'r'), ('左折返 Z', 1.05, 'r'), ('停留 s', 0.7, 'r', True), ('倍率', 0.7, 'r'),
            ('右折返 Z', 1.05, 'r'), ('停留 s', 0.7, 'r', True), ('倍率', 0.7, 'r'), ('补偿 µm', 0.85, 'r'),
            ('平均倍率', 0.85, 'r', True), ('非 100%', 0.75, 'r', True)]
    trs = []; sel = None
    for i, r in enumerate(rows):
        if r[0] == '_g': trs.append({'_sub': r[1]}); continue
        if r[0] == '_miss':
            trs.append({'cells': [{'t': '▲', 'col': '#B8600A', 'b': True}, {'t': r[1], 'col': '#8A4A00', 'b': True}] + [''] * 8,
                        'fill': C['warnSoft']}); continue
        cells = []
        for j, v in enumerate(r):
            if isinstance(v, str) and v.startswith('!'): cells.append({'t': '✕ ' + v[1:], 'col': '#8E1B1B', 'b': True, 'mono': True})
            elif isinstance(v, str) and v.startswith('*'): cells.append({'t': v[1:], 'b': True, 'mono': True})
            else: cells.append({'t': v, 'mono': True} if j else {'t': ('▶ ' if (r[0] == cur and i == len(rows) - 1) else '') + v, 'b': r[0] == cur and i == len(rows) - 1})
        trs.append(cells)
        if r[0] == cur and i == len(rows) - 1: sel = len(trs) - 1
    rh = 48 if p.name == 'std' else 40
    return tbl(c, x, y, w, cols, trs, sel=sel, rowh=rh)

PASSES = [('_g', '工序 3 半精磨 · 拖板 设定 2300 → 本次 2100'),
          ('1', '−859.8', '1.0', '100', '+860.1', '1.0', '100', '+2.4', '100%', '0%'),
          ('2', '−860.0', '1.0', '100', '+860.0', '1.0', '100', '+1.6', '100%', '0%'),
          ('3', '−859.9', '1.0', '*95', '+859.8', '1.1', '*95', '+1.1', '*97%', '*18%'),
          ('4', '−860.1', '1.0', '100', '+860.0', '1.0', '100', '+0.8', '100%', '0%'),
          ('_g', '工序 4 精磨 · 拖板 设定 800（第 4 道起 本次 720）'),
          ('1', '−860.0', '1.0', '100', '+860.0', '1.0', '100', '+0.6', '100%', '0%'),
          ('2', '−859.9', '1.0', '!45', '+860.1', '1.0', '100', '+0.4', '*88%', '*31%'),
          ('3', '−860.0', '1.0', '100', '+859.9', '1.0', '100', '+0.3', '100%', '0%')]

@screen('5.3.4', '道次表（AUTO 横键）', '新', '每道一行（V17）：左右折返的实际 Z、停留、倍率，本道补偿，平均倍率与非 100% 时间；按工序分组，当前道 ▶ 选中；'
        '倍率 ≠ 100% 粗体，越界（< 50%）✕ 深红。数据由 NC 在折返时锁存、每道读一次（接口约定第 4 节）。紧凑档去掉停留与非 100% 两列。')
def s534(p):
    c, w = shell(p, 'machine', 'AUTO', '作业 J20261102-04 · 辊 R-2026-031 · 凸度300 · 工作辊标准（方案 3）· 本次调整 2 项', machine=True, clock='2026-11-02 10:58',
                 chan=('// 自动 · 运行', '◈ 运行', '4 精磨 · 第 3/6 道'),
                 vkeys=[('本工序', 'on'), '全部工序', '只看越界', '', '', '', '', ''], hkeys=AUTOK, hk_on=2,
                 dialog='道次表只用于记录与追溯；机床不等上位机，读不到时机床照常磨')
    L_, R_ = split(w, 0.7, 0.3)
    x0, y0, x1, y1 = win(c, L_, '道次表 · 3 半精磨 – 4 精磨', True, '折返 Z 设定 −860.0 / +860.0 mm')
    pass_table(c, x0, y0, x1 - x0, PASSES, cur='3')
    A, B = split(R_, 0.62, 0.38, vertical=True)
    x0, y0, x1, y1 = win(c, A, '工序 4 精磨 摘要')
    kv(c, x0 + 6, y0 + 2, x1 - x0 - 12, [('拖板 设定 / 本次', '800 / 720（第 4 道起）'), ('平均倍率', '96%'), ('有效拖板速度', '≈ 691 mm/min'),
                                          ('非 100% 时间', '10%'), ('折返 Z 最大偏差', '0.1 mm'), ('补偿', '+0.6 → +0.3 µm'),
                                          ('越界', {'t': '✕ 1 次（第 2 道 45%）', 'badge': 'red'})])
    x0, y0, x1, y1 = win(c, B, '记录数据')
    kv(c, x0 + 6, y0 + 2, x1 - x0 - 12, [('NC 已写 / 已读', '15 / 15 道'), ('缓冲', '16 槽 · 溢出 0'), ('状态', {'t': '✓ 完整', 'badge': 'ok'})])
    return finish(c)

@screen('5.3.5', '磨完结果', '改', '在 v3 基础上，左下加"这次怎么磨的"：方案、本次调整、长度对齐、道次表完整性、下发序号（NC 已确认）。'
        '道次数据有缺时只写"记录缺第 x–y 道（不影响合格判定）"（O14）。磨后辊形横轴改中心 0。')
def s535(p):
    src = v3svg('5.3.4', p)
    jr = wrect(src, '判定依据'); gr = wrect(src, '磨后辊形')
    def draw(c, p, src):
        x0, y0, x1, y1 = win(c, jr, '判定依据')
        yy = tbl(c, x0, y0, x1 - x0, [('项', 1.6), ('实测', 0.8, 'r'), ('允许', 0.8, 'r'), ('', 0.3, 'c')],
                 [['辊形误差（峰谷）µm', '2.6', '5.0', {'t': '✓', 'col': C['ok'], 'b': True}], ['圆度 µm', '1.8', '3.0', {'t': '✓', 'col': C['ok'], 'b': True}],
                  ['锥度 µm', '1.2', '4.0', {'t': '✓', 'col': C['ok'], 'b': True}], ['直径偏差 mm', '+0.002', '±0.010', {'t': '✓', 'col': C['ok'], 'b': True}]],
                 rowh=p.rowH * 0.9)
        c.rect(x0, yy + 8, x1 - x0, p.wtH, C['hdr']); c.text(x0 + 8, yy + 8 + p.wtH * 0.72, '这次怎么磨的', p.wtF, C['ink'], weight=500)
        kv(c, x0 + 6, yy + 10 + p.wtH, x1 - x0 - 12, [('磨法', '凸度300 · 工作辊标准（方案 3）'), ('本次调整', '3 项（核对 2 · 运行中 1）'),
                                                      ('长度对齐', '两端各截 1.0 mm'), ('道次表', {'t': '✓ 完整 23 道', 'badge': 'ok'}),
                                                      ('下发', '序号 118 · NC 已确认')])
        x0, y0, x1, y1 = win(c, gr, '磨后辊形')
        import math
        meas = [(z, v + 1.5 * math.sin(z / 40.0)) for z, v in cr()]
        hh = (x1 - x0) / 2.3
        profile_chart(c, (x0 + 4, y0 + 4, x1 - 4, y0 + hh), '━ 方案 3（设定）  ━ 磨后实测', [(cr(), C['target'], 3, None), (meas, C['meas'], 2, None)])
        err = [(z, 2.0 * math.sin(z / 70) + 0.6 * math.sin(z / 13)) for z in range(-900, 901, 15)]
        chart(c, x0 + 4, y0 + hh + 6, x1 - x0 - 8, y1 - y0 - hh - 10, [(err, C['meas'], 2, None)], (-900, 900), (-10, 10),
              xlab='Z mm（中心 0）', ylab='误差 µm（直径）', legend='— 磨后 − 设定  ▭ 公差 ±5', xt=[-900, -450, 0, 450, 900],
              yt=([-10, -5, 0, 5, 10] if p.name == 'std' else [-5, 0, 5]), hbands=[(-5, 5, C['okSoft'], 1)], reset=False)
    hk = ['补偿', '程序段', '道次表', ('工序跳转…', 'off'), ('提前结束…', 'off'), '磨削记录', '作业']
    over = dict(hkeys=hk, hk_on=None, clock='2026-11-02 11:18', vkeys=[('磨削记录', 'on'), '返磨…', '打印…', '', '', '', '', '下一支辊 ▸'])
    return patch('5.3.4', p, over=over, drops=[jr, gr], draw=draw)

ROLLK = ['台账', '导入 / 导出']

@screen('5.4.1', '台账', '改', '右边分三块：这支辊（名义 / 实测长度分开）· 磨削方案摘要（当前 ★ + 最近 2 个，P12）· 履历（加方案列）。'
        '竖键保持 v3 结构（I4）："改计划…"换成"方案 ▸"，打开方案区这支辊的方案列表；方案的动作只在那里。寿命条改灰（V4）。')
def s541(p):
    src = v3svg('5.4.1', p)
    a = wrect(src, 'R-2026-031 · 工作辊 · F3 上'); b = wrect(src, '磨削履历')
    R = (a[0], a[1], b[2], b[3])
    def draw(c, p, src):
        A, B, Cc = split(R, 0.36, 0.30, 0.34, vertical=True)
        x0, y0, x1, y1 = win(c, A, 'R-2026-031 · 工作辊 · F3 上')
        W = x1 - x0 - 12; h2 = (W - 16) / 2
        kv(c, x0 + 6, y0 + 2, h2, [('辊身 名义 / 实测', '1800.0 / 1798.0'), ('当前 / 报废', '600.35 / 585.0'), ('磨削次数', '12 次')])
        kv(c, x0 + 22 + h2, y0 + 2, h2, [('公称直径', '600.0'), ('材质', '高铬铁'), ('净重', '6.8 t')])
        yy = y0 + 2 + p.kvP * 3 + 12
        c.text(x0 + 10, yy + p.f * 0.9, '剩余可磨 15.35 mm', p.f, C['ink2'])
        bx = x0 + 10 + tw('剩余可磨 15.35 mm', p.f) + 20; bw = x1 - bx - 12
        c.rect(bx, yy, bw, 16, '#FFFFFF', C['border']); c.rect(bx, yy, bw * 0.77, 16, '#6D7F8D')
        c.rect(bx + bw * 0.1 - 1, yy - 4, 2, 24, '#B8600A')
        x0, y0, x1, y1 = win(c, B, '磨削方案（共 4 个）', right='全部 → 方案 ▸')
        tbl(c, x0, y0, x1 - x0, [('方案', 0.8), ('辊形 · 工艺', 2.4), ('用途', 0.6, 'l', True), ('用', 0.6, 'r'), ('最近', 0.8, 'r'), ('', 1.4)],
            [[{'t': '★ 3', 'b': True}, '凸度300 · 工作辊标准', 'F3', '4', '10-28', {'t': '↑ 模板已更新', 'badge': 'warn'}],
             ['2', '凸度250 · 工作辊标准', 'F3', '6', '09-15', ''],
             ['1', '圆柱 · 支承过渡', 'F4', '2', '08-02', '']], rowh=p.rowH * 0.9)
        x0, y0, x1, y1 = win(c, Cc, '磨削履历', right='点一行看完整记录')
        tbl(c, x0, y0, x1 - x0, [('日期', 0.8), ('方案', 1.9), ('去除 mm', 0.9, 'r'), ('结果', 1.0), ('标记', 1.2, 'l', True)],
            [['10-28', '3 · 凸度300', '0.35', {'t': '✓ 合格', 'col': C['ok']}, 'Δ 2 项'],
             ['09-28', '3 · 凸度300', '0.42', {'t': '✓ 合格', 'col': C['ok']}, '例外·试磨'],
             ['09-15', '2 · 凸度250', '0.38', {'t': '✕ → 返磨', 'col': '#8E1B1B'}, '']], rowh=p.rowH * 0.9)
    over = dict(vkeys=['下作业 ▸', '方案 ▸', '新登记轧辊', '照此复制登记', '多选', '作废…', '', ''],
                dialog='选中一支辊：右边是尺寸、磨削方案摘要与履历；"方案 ▸"打开它的全部方案')
    return patch('5.4.1', p, over=over, drops=[R], draw=draw)

@screen('5.4.2', '台账 · 多选', '改', '与 v3 同；成批动作由"改计划…"改为"从模板建方案…"（换机架计划时，给选中的辊各建一个新方案）。')
def s542(p):
    subs = [('改计划…', '从模板建方案…'), ('>计划<', '>当前方案<'), ('CVC-A v2', 'CVC-A'), ('现计划', '当前方案'),
            ('凸度300 · 标准（7 支相同）', '凸度300 · 工作辊标准（7 支相同）'),
            ('· 选 1 支 = 逐支改；选多支 = 一次改', '· 选中的辊各从模板复制一个新方案'),
            ('· "改计划…"打开与逐支相同的选辊形子视图', '· 逐支做长度对齐，先看结果再建'),
            ('点行勾选；按"改计划…"给已选的 7 支换辊形或程序', '点行勾选；按"从模板建方案…"给已选的 7 支各建一个新方案')]
    svg = patch('5.4.2', p, subs=subs)
    return svg.replace('>计划</text>', '>当前方案</text>')

@screen('5.4.3', '从模板建方案（多支）', '新', '取代 v3"改计划（多支）"。左：模板与其辊形；右：逐支长度对齐结果与排除原因（I18）。'
        '复制按名义长度对齐；下发时（实测）一列预告以后每次下发的截取，超下发容差的辊（R-007）照样建方案，但下发时会被核对页拦下。竖键 8 建立并设为当前 · 6 只建立… · 7 取消。')
def s543(p):
    c, w = shell(p, 'rolls', 'JOG', '轧辊 / 台账 › 多选 › 从模板建方案', '« 返回 多选', user='管理员 张工',
                 vkeys=['换模板 ▸', '看这支对齐', '排除这支', '', '', '只建立…', ('✕ 取消', 'cancel'), ('✓ 建立并\n设为当前', 'ok')],
                 hkeys=ROLLK, hk_on=0, ask=True, modal=True,
                 dialog='给 6 支建方案（1 支须先确认对齐，1 支排除）。✓ 建立并设为当前：每支一条改动记录（不自动取消）')
    L_, R_ = split(w, 0.38, 0.62)
    A, B = split(L_, 0.42, 0.58, vertical=True)
    x0, y0, x1, y1 = win(c, A, '模板：F3 标准凸度（11-01 版）')
    kv(c, x0 + 6, y0 + 2, x1 - x0 - 12, [('辊形', '凸度 280 µm · 抛物线'), ('工艺', '工作辊标准'), ('名义长度', '1800.0 mm'), ('更新', '11-01 张工')])
    x0, y0, x1, y1 = win(c, B, '模板辊形')
    profile_chart(c, (x0 + 4, y0 + 4, x1 - 4, y1 - 4), '━ 模板 F3 标准凸度（凸度 280）', [(cr(280), C['target'], 3, None)], note='')
    x0, y0, x1, y1 = win(c, R_, '已选 7 支 · 逐支结果', True, '建立 5 · 须确认 1 · 排除 1')
    ok = {'t': '✓ 建立', 'badge': 'ok'}
    tbl(c, x0, y0, x1 - x0, [('辊号', 1.1), ('名义 / 实测', 1.1, 'r'), ('复制对齐（名义）', 1.5), ('下发时（实测）', 1.3), ('结果', 1.1)],
        [['R-2026-031', '1800 / 1798', '相同', '截 1.0', ok],
         ['R-2026-022', '1800 / —', '相同', '—', ok], ['R-2026-007', '1800 / 1796', '相同', {'t': '▲ 差 4.0 超容差', 'col': '#8A4A00', 'b': True}, ok],
         ['R-2026-041', '1800 / —', '相同', '—', ok], ['R-2026-044', '1790 / —', '凸度段 1720 → 1710', '—', {'t': '▲ 须确认', 'badge': 'warn'}],
         ['R-2026-045', '1800 / —', '相同', '—', ok], ['R-2026-046', '1800 / —', '当前为 CVC，类型不同', '—', {'t': '— 排除', 'badge': 'info'}]], sel=4)
    return finish(c)

@screen('5.4.4', '新登记 / 照此复制登记', '改', '左：辊本身，辊身长度分名义（必填）与实测（可空）。右：第一个磨削方案——来源三选一（从模板 / 从其他辊复制 / 空白），'
        '也可以"稍后再建"（作业前必须有当前方案）。复制或套模板时显示长度对齐一行。')
def s544(p):
    c, w = shell(p, 'rolls', 'JOG', '轧辊 / 台账 › 新登记', '« 返回 台账', user='管理员 张工',
                 vkeys=['照同类辊复制…', '选类型 ▸', '选用途 ▸', '选模板…', '从其他辊复制…', '稍后再建方案', ('✕ 取消', 'cancel'), ('✓ 保存', 'ok')],
                 hkeys=ROLLK, hk_on=0, dialog='保存时核对：辊号不重复、尺寸不超机床；方案长度按名义长度对齐')
    L_, R_ = split(w, 0.45, 0.55)
    x0, y0, x1, y1 = win(c, L_, '这支辊 · 必填 *', True)
    X = x0 + 8; W = x1 - x0 - 16; y = y0 + 10
    for lab, v, u, st in [('辊号 *', 'R-2026-032', '', 'sel'), ('类型 *', '工作辊', '', 'normal'), ('材质', '高铬铁', '', 'normal'),
                          ('名义辊身长度 *', '1800.0', 'mm', 'normal'), ('实测辊身长度', '', 'mm', 'normal'),
                          ('公称直径 *', '600.0', 'mm', 'normal'), ('报废直径', '585.0', 'mm', 'normal'), ('净重', '6.8', 't', 'normal'),
                          ('用途', 'F3 上', '', 'normal')]:
        y = field(c, X, y, W, lab, v, u, st, lw=0.45, note='可空：有实测再填' if lab == '实测辊身长度' else '')
    x0, y0, x1, y1 = win(c, R_, '第一个磨削方案')
    X = x0 + 8; W = x1 - x0 - 16; y = y0 + 10
    c.text(X + 4, y + p.f, '来源', p.f, C['ink2'])
    y = steps_chips(c, X + 90, y - 4, [('从模板', True), ('从其他辊复制', False), ('空白', False)]) + 12
    y = kv(c, X, y, W, [('模板', 'F3 标准凸度（11-01 版）'), ('磨法', {'t': '凸度280 · 工作辊标准', 'badge': 'plain'}),
                        ('长度对齐', '模板名义 1800.0 = 本辊 1800.0 · 相同'), ('设为当前', '是（第一个方案）')])
    profile_chart(c, (X, y + 8, x1 - 8, y1 - 8), '━ 将建：方案 1（来自模板）', [(cr(280), C['target'], 3, None)], note='')
    return finish(c)

PLANK = ['方案列表', '辊形', '工艺', '对比', '校验', '导入 / 导出']
PATH_P = '方案 / R-2026-031 › 方案 3 · ★ 当前 · 已用 4 次'

def info_bar(c, x, y, w, t, right=''):
    """V15 信息条：白底、左 6px #6D7F8D、i。"""
    p = c.p; h = 40 if p.name == 'std' else 34
    c.rect(x, y, w, h, '#FFFFFF', C['border']); c.rect(x, y, 6, h, '#6D7F8D')
    c.text(x + 20, y + h * 0.68, 'i', p.f, '#6D7F8D', 'middle', 700)
    c.text(x + 36, y + h * 0.68, t, p.f, C['ink'], weight=500)
    if right: c.text(x + w - 10, y + h * 0.68, right, p.fs, C['ink2'], 'end')
    return y + h + 6

def strip(c, x, y, w, items):
    p = c.p; h = 44 if p.name == 'std' else 38
    c.rect(x, y, w, h, '#FFFFFF', C['border'], 1)
    xx = x + 10
    for lab, val in items:
        c.text(xx, y + h * 0.66, lab, p.fs, C['ink2']); xx += tw(lab, p.fs) + 8
        c.text(xx, y + h * 0.66, val, p.f, C['ink'], weight=700); xx += tw(val, p.f) + 26
    return y + h + 6

@screen('5.5.1', '方案列表', '新', '取代 v3 的辊形库 / 程序库：只列"这支辊"的方案（P6）。路径条写辊号（I2）。'
        '列：★、方案、辊形 · 工艺（后附 ↑ 模板已更新）、用途、来源、与来源（只放 ＝ / ≠）、用次（0 = 没用过）、最近；标准档多名义长度与建立人。'
        '竖键：选轧辊 ▸ · 打开 · 新建方案… · 复制来 ▸（选方案子视图）· 对比 · 设为当前… · 删除…（只对没用过的）。')
def s551(p):
    c, w = shell(p, 'plan', 'JOG', '方案 / R-2026-031 · 工作辊 · F3 上', '共 4 个 · 当前 方案 3', user='管理员 张工',
                 modeL='方案', vkeys=['选轧辊 ▸', '打开', '新建方案…', '复制来 ▸', '对比', '设为当前…', '删除…', ''],
                 hkeys=PLANK, hk_on=0, dialog='方案 4 存自作业 J20261028-02 的调整，还没用过：可以改、可以删；用过一次后修改会另存为新方案')
    A, B = split(w, 0.6, 0.4, vertical=True)
    x0, y0, x1, y1 = win(c, A, 'R-2026-031 的方案', True, '用途：全部 ▸')
    tbl(c, x0, y0, x1 - x0, [('方案', 0.7), ('辊形 · 工艺', 2.2), ('用途', 0.55), ('来源', 2.0), ('与来源', 1.1), ('名义', 0.7, 'r', True),
                             ('用', 0.45, 'r'), ('最近', 0.75, 'r'), ('建立', 0.7, 'l', True)],
        [[{'t': '★ 3', 'b': True}, {'t': '凸度300 · 工作辊标准', 'after': '↑ 模板已更新', 'akind': 'warn'}, 'F3', '模板 F3 标准凸度', {'t': '＝ 一致', 'badge': 'ok'}, '1800', '4', '10-28', '张工'],
         ['4', '凸度300 · 慢拖板', 'F3', '作业 J1028-02 的调整', '—', '1800', '0', '—', '张工'],
         ['2', '凸度250 · 工作辊标准', 'F3', '本辊 方案 1', {'t': '≠ 已改动', 'badge': 'warn'}, '1800', '6', '09-15', '张工'],
         ['1', '圆柱 · 支承过渡', 'F4', 'R-2026-021 / 方案 1', {'t': '＝ 一致', 'badge': 'ok'}, '1800', '2', '08-02', '王工']], sel=1)
    L_, R_ = split(B, 0.4, 0.6)
    x0, y0, x1, y1 = win(c, L_, '方案 4 · 凸度300 · 慢拖板')
    kv(c, x0 + 6, y0 + 2, x1 - x0 - 12, [('辊形', '凸度 300 µm · 抛物线'), ('与方案 3 的差别', '工序 4 精磨 拖板 800 → 720'),
                                          ('非工作区 头 / 尾', '40 / 40 mm')] + ([('建立', '10-28 张工（存为新方案）')] if p.name == 'std' else []))
    x0, y0, x1, y1 = win(c, R_, '辊形')
    profile_chart(c, (x0 + 4, y0 + 4, x1 - 4, y1 - 4), '━ 方案 4', [(cr(), C['target'], 3, None)], note='')
    return finish(c)

@screen('5.5.2', '方案 › 辊形', '改', '编辑"某支辊的某个方案"。顶栏写名义 / 实测长度、头尾非工作区、公差，坐标中心为 0（P1）；打开已用方案即显示信息条（P3、V15）。'
        '辊形与工艺共用一份草稿、一次保存（P2）；竖键 8"保存…"写明将另存为方案 5（I11），按下进入 5.5.4 页内决定。')
def s552(p):
    c, w = shell(p, 'plan', 'JOG', PATH_P + ' · ● 未保存', '« 返回 核对 R-031', user='管理员 张工', modeL='方案',
                 vkeys=['插入段 ▸', '删除段', '上移', '下移', '复制段', '更多 ▸', ('✕ 放弃改动', 'cancel'), ('✓ 保存…\n另存为方案 5', 'ok')],
                 hkeys=PLANK, hk_on=1, dialog='凸度：段中点相对两端的直径差 · µm · 允许 −2000 ~ 2000 · 原值 300')
    x0, y0, x1, y1 = w
    y = strip(c, x0, y0, x1 - x0, [('名义长度', '1800.0（台账）'), ('实测', '1798.0'), ('非工作区 头 / 尾', '40 / 40 mm'), ('公差', '±5 µm'), ('坐标', '中心 0')] if p.name == 'std'
              else [('名义', '1800.0'), ('实测', '1798.0'), ('非工作区', '40 / 40'), ('公差', '±5 µm')])
    y = info_bar(c, x0, y, x1 - x0, '方案 3 已用 4 次：内容不改，保存时辊形和工艺一起另存为方案 5', '已改 1 项（辊形）')
    L_, R_ = split((x0, y, x1, y1), 0.32, 0.68)
    lx0, ly0, lx1, ly1 = win(c, L_, '曲线段（Z：中心 0）')
    yy = tbl(c, lx0, ly0, lx1 - lx0, [('#', 0.3), ('类型', 0.8), ('起点', 0.9, 'r'), ('长度', 0.8, 'r'), ('终点', 0.9, 'r', True)],
             [['1', '圆柱', '−900', '40', '−860'], ['2', '凸度', '−860', '1720', '+860'], ['3', '圆柱', '+860', '40', '+900']], sel=1)
    c.text(lx0 + 8, yy + p.f * 1.4, '段长合计 1800 = 名义长度', p.f, C['ok'])
    c.text(lx0 + 8, yy + p.f * 2.9, '▨ 两端圆柱段 = 端部非工作区', p.fs, C['ink2'])
    A, B = split(R_, 0.6, 0.4, vertical=True)
    ax0, ay0, ax1, ay1 = win(c, A, '辊形预览', right='━ 草稿  ╍ 方案 3')
    profile_chart(c, (ax0 + 4, ay0 + 4, ax1 - 4, ay1 - 4), '━ 草稿（将存为方案 5）  ╍ 方案 3（原）',
                  [(cr(300), '#8A5A00', 2, '8 5'), (cr(280), C['target'], 3, None)])
    bx0, by0, bx1, by1 = win(c, B, '第 2 段参数 · 凸度', True)
    W = (bx1 - bx0) * 0.6
    yy = by0 + 8
    yy = field(c, bx0 + 8, yy, W, '起点 Z', '−860', 'mm', 'ro')
    yy = field(c, bx0 + 8, yy, W, '长度 L', '1720', 'mm')
    yy = field(c, bx0 + 8, yy, W, '凸度（直径）', '280', 'µm', 'sel', note='原 300')
    if p.name == 'std': field(c, bx0 + 8, yy, W, '形状', '抛物线', '')
    return finish(c)

@screen('5.5.3', '方案 › 工艺', '改', '参数表按 P5：方案值、单位、可调方式（±% / 只减 / 只加 / 🔒 不可调）、下限、上限（换算成绝对值）、范围来源、机床限。'
        '选中行"道次"的范围是本方案改的（来源 = 本方案），其余取机床配置的默认范围。紧凑档去掉来源与机床限两列。')
def s553(p):
    c, w = shell(p, 'plan', 'JOG', PATH_P + ' · ● 未保存', '« 返回 核对 R-031', user='管理员 张工', modeL='方案',
                 vkeys=['插入工序 ▸', '删除', '上移', '下移', '改范围…', '更多 ▸', ('✕ 放弃改动', 'cancel'), ('✓ 保存…\n另存为方案 5', 'ok')],
                 hkeys=PLANK, hk_on=2, dialog='道次（工序 4 精磨的往复道次）· 作业里可调 ±2（4 – 8），本方案设定；超出机床限不收')
    x0, y0, x1, y1 = w
    y = strip(c, x0, y0, x1 - x0, [('适用', '工作辊 · 高铬铁'), ('标准余量', '0.350 mm'), ('余量分配', '粗 0.300 · 半精 0.030 · 精 0.020 ✓')])
    y = info_bar(c, x0, y, x1 - x0, '方案 3 已用 4 次：保存时与辊形一起另存为方案 5', '已改 2 项（辊形 1 · 工艺 1）')
    L_, R_ = split((x0, y, x1, y1), 0.22, 0.78)
    lx0, ly0, lx1, ly1 = win(c, L_, '工序序列', right='64 min')
    tbl(c, lx0, ly0, lx1 - lx0, [('#', 0.3), ('工序', 1.0), ('时长', 0.8, 'r')],
        [['▣', '开始', '—'], ['1', '测量', '2'], ['2', '粗磨', '30'], ['3', '半精磨', '12'], ['4', '精磨', '12'], ['5', '光磨', '6'], ['6', '测量', '2'], ['▣', '结束', '—']], sel=4)
    rx0, ry0, rx1, ry1 = win(c, R_, '工序 4 · 精磨 · 参数与作业里可调范围', True)
    lock = {'t': '🔒 不可调', 'col': '#404C58'}
    tbl(c, rx0, ry0, rx1 - rx0, [('参数', 1.5), ('设定值', 0.8, 'r'), ('单位', 0.7), ('可调方式', 1.0), ('下限', 0.6, 'r'), ('上限', 0.6, 'r'),
                                 ('来源', 0.7, 'l', True), ('机床限', 0.9, 'r', True)],
        [['磨削量（直径）', '0.020', 'mm', lock, '—', '—', '—', '—'],
         ['拖板速度', {'t': '760（原 800）', 'b': True}, 'mm/min', '±10%', '684', '836', '默认', '100–3000'],
         ['周期进给', '2', 'µm（直径）', '只减', '1', '2', '默认', '1–20'],
         ['道次', '6', '道', {'t': '±2 道', 'b': True}, '4', '8', {'t': '本方案', 'b': True}, '1–50'],
         ['折返时间', '1.0', 's', '±50%', '0.5', '1.5', '默认', '0–5'],
         ['头架转速', '40', 'r/min', '±10%', '36', '44', '默认', '5–60'],
         ['砂轮线速度', '35', 'm/s', lock, '—', '—', '默认', '≤ 45']], sel=3)
    return finish(c)

@screen('5.5.4', '保存已用方案（页内决定）', '新', '取代 v3"在用影响"。左：改了什么（辊形 + 工艺一起）、新方案名与用途；右：新旧叠图 + 公差带（P4）。'
        '源是当前方案：竖键 8 另存并设为当前 · 6 另存但不设当前…；源是历史方案时两键互换（I10）。不自动取消。进页时焦点在"新方案名"、竖键 8 锁 1 s，防止从 5.5.2 连按两次 8（R2-I2）。')
def s554(p):
    c, w = shell(p, 'plan', 'JOG', PATH_P + ' › 保存', '', user='管理员 张工', modeL='方案',
                 vkeys=['', '', '', '', '', '另存但\n不设当前…', ('✕ 取消', 'cancel'), ('✓ 另存为方案 5\n并设为当前', 'ok')],
                 hkeys=PLANK, hk_on=None, ask=True, modal=True,
                 dialog='方案 3 已用 4 次，内容不改。✓ 另存为方案 5 并设为当前；方案 3 留作历史，要退回在方案列表设回（不自动取消）')
    L_, R_ = split(w, 0.45, 0.55)
    x0, y0, x1, y1 = win(c, L_, '改了什么（2 项）', True)
    yy = tbl(c, x0, y0, x1 - x0, [('项', 1.6), ('方案 3', 0.9, 'r'), ('方案 5（新）', 1.0, 'r')],
             [['辊形 · 凸度 µm', '300', {'t': '280', 'b': True}], ['4 精磨 · 拖板 mm/min', '800', {'t': '760', 'b': True}]])
    yy += 14
    X = x0 + 8; W = x1 - x0 - 16
    yy = field(c, X, yy, W, '新方案名', '凸度280 · 工作辊标准', '', 'sel', lw=0.32)
    yy = field(c, X, yy, W, '用途', 'F3', '', lw=0.32)
    kv(c, X, yy + 4, W, [('来源', '本辊 方案 3'), ('长度', '名义 1800.0（不变）'), ('以后的作业', '默认用方案 5')])
    x0, y0, x1, y1 = win(c, R_, '叠图', right='公差带 ±5 µm')
    pts = cr(280)
    profile_chart(c, (x0 + 4, y0 + 4, x1 - 4, y1 - 4), '━ 方案 5（新）  ╍ 方案 3（原）',
                  [(cr(300), '#8A5A00', 2, '8 5'), ([(z, v + 5) for z, v in pts], '#9AA0A6', 1, '3 3'), ([(z, v - 5) for z, v in pts], '#9AA0A6', 1, '3 3'),
                   (pts, C['target'], 3, None)])
    return finish(c)

@screen('5.5.5', '复制方案 · 长度对齐预览（选方案子视图）', '新', '同一个选方案子视图，用途"复制到本辊"。右上是长度对齐结论（P9）：两边名义长度、差值、方式、每端截 / 延、判定；'
        '下面叠图（中心点划线、非工作区斜线、来源点线、新方案实线）。CVC / 点表的截取与延伸时再加 ✂ / ⟶ 标注（V18）。超出可套用范围时竖键 8 灰。复制出的方案默认不设为当前（I17）。')
def s555(p):
    c, w = shell(p, 'plan', 'JOG', '方案 / R-2026-044 › 复制来', '« 返回 方案列表', user='管理员 张工', modeL='方案',
                 vkeys=['本辊方案', ('其他辊 ▸', 'on'), '模板 ▸', '放大端部', '只复制工艺…', '复制范围 ▸', ('✕ 取消', 'cancel'), ('✓ 复制到本辊', 'ok')],
                 hkeys=PLANK, hk_on=0, ask=True, modal=True,
                 dialog='长度不同（−10.0 mm）：凸度段 1720 → 1710，凸度值不变。看清叠图后 ✓ 复制到本辊（新方案不设为当前）')
    L_, R_ = split(w, 0.36, 0.64)
    x0, y0, x1, y1 = win(c, L_, '其他辊 · 用途 F3', True, '输尾号')
    tbl(c, x0, y0, x1 - x0, [('辊号 / 方案', 1.6), ('辊形 · 工艺', 2.0), ('名义', 0.7, 'r', True)],
        [['R-2026-031 / 3', '凸度300 · 工作辊标准', '1800'], ['R-2026-031 / 2', '凸度250 · 工作辊标准', '1800'],
         ['R-2026-022 / 2', '凸度300 · 工作辊标准', '1800'], ['R-2026-040 / 1', 'CVC-A · 工作辊标准', '1800']], sel=0)
    A, B = split(R_, 0.42, 0.58, vertical=True)
    x0, y0, x1, y1 = win(c, A, '长度对齐', right='▲ 须确认', rc='#8A4A00')
    kv(c, x0 + 6, y0 + 2, x1 - x0 - 12, [('名义长度 来源 → 本辊', '1800.0 → 1790.0（差 −10.0）'),
                                          ('方式', '解析段：两端圆柱 40 不变，凸度段缩短'),
                                          ('段表', '40 · 1720 · 40 → 40 · 1710 · 40'),
                                          ('判定', {'t': '▲ 须确认：差 10 > 复制门槛 1.0', 'badge': 'warn'})] + ([('复制范围', '辊形 + 工艺')] if p.name == 'std' else []))
    x0, y0, x1, y1 = win(c, B, '叠图')
    src_ = cr(300, 40, 1800); new = cr(300, 40, 1790)
    X, Y = profile_chart(c, (x0 + 4, y0 + 4, x1 - 4, y1 - 4), '━ 本辊新方案（1790）  ┈ 来源 R-031 / 3（1800）  ┆ 本辊端 ±895',
                         [(src_, '#8A5A00', 1.5, '2 4'), (new, C['target'], 3, None)],
                         extra_v=[(-895, '', '#15507F', '6 3'), (895, '', '#15507F', '6 3')])
    return finish(c)

@screen('5.5.6', '命名（弹出框样板）', '不变', '全机仍只有 3 种弹出框（命名、登录、报表预览）之一；v4 用于"新方案名""模板名"。名字在同一支辊内不能重复。')
def s556(p):
    c, w = shell(p, 'plan', 'JOG', PATH_P + ' › 保存 › 新方案名', '', user='管理员 张工', modeL='方案',
                 vkeys=['', '', '', '', '', '', ('✕ 取消', 'cancel'), ('✓ 确定', 'ok')], hkeys=PLANK, hk_on=None, modal=True,
                 dialog='输入新方案名；✓ 确定后回到保存页，再决定是否设为当前')
    x0, y0, x1, y1 = w
    c.rect(x0, y0, x1 - x0, y1 - y0, '#DDE2E9')
    c.text((x0 + x1) / 2, y0 + 110, '（下面是"保存已用方案"页，弹出框只盖住工作区中央）', p.fs, C['ink2'], 'middle')
    W = (x1 - x0) * 0.42; H = 250 if p.name == 'std' else 210
    bx = (x0 + x1 - W) / 2; by = (y0 + y1 - H) / 2
    ix0, iy0, ix1, iy1 = window(c, bx, by, W, H, '新方案名', True)
    y = iy0 + 14
    y = field(c, ix0 + 10, y, ix1 - ix0 - 20, '名称', '凸度280 · 工作辊标准', '', 'sel', lw=0.3)
    y = field(c, ix0 + 10, y, ix1 - ix0 - 20, '从', '方案 3 · 凸度300', '', 'ro', lw=0.3)
    c.text(ix0 + 14, y + p.fs, '同一支辊里名字不能重复；方案号由系统给（方案 5）', p.fs, C['ink2'])
    return finish(c)

TPLK = ['模板库', '辊形', '工艺', '对比', '校验', '导入 / 导出', '复制关系']

@screen('5.6.1', '模板库', '新', '标准模板只作复制起点（P11 列）：模板、机架、辊形摘要、更新、改动说明、复制自它的辊（一致 / 已改动）、必须采用与采用进度。'
        '操作者可看、只读（I19）。"撤回必须采用…"只对还没来磨的辊有效（I12）。编辑复用方案区的辊形 / 工艺页，路径条写"模板 › …"。')
def s561(p):
    c, w = shell(p, 'tpl', 'JOG', '模板 / 模板库', '共 6 个', user='管理员 张工', modeL='模板',
                 vkeys=['新建模板…', '打开', '复制为新模板…', '复制关系 ▸', '对比', '撤回必须采用…', '删除…', ''],
                 hkeys=TPLK, hk_on=0, dialog='F3 标准凸度于 11-01 更新（凸度 300 → 280），必须采用 4 支（R-044 已排除），已有 2 支换上')
    A, B = split(w, 0.55, 0.45, vertical=True)
    x0, y0, x1, y1 = win(c, A, '标准模板', True, '机架：全部 ▸')
    tbl(c, x0, y0, x1 - x0, [('模板', 1.5), ('机架', 0.6), ('辊形', 1.3, 'l', True), ('更新', 0.9), ('改动说明', 1.6, 'l', True),
                             ('复制自它', 1.2), ('必须采用', 1.0), ('进度', 0.6, 'r')],
        [['F3 标准凸度', 'F3', '凸度 280 µm', '11-01 张工', '凸度 300→280 等 2 项', '7（5 ＝ · 2 ≠）', {'t': '✓ 是', 'badge': 'warn'}, '2 / 4'],
         ['F3 凸度250', 'F3', '凸度 250 µm', '09-10 张工', '新建', '3（3 ＝）', '否', '—'],
         ['F2 CVC-A', 'F2', 'CVC 点表 41 点', '08-22 王工', '按轧机厂曲线重导', '4（4 ＝）', '否', '—'],
         ['F1 凸度400', 'F1', '凸度 400 µm', '07-30 王工', '光磨 1→2', '5（4 ＝ · 1 ≠）', '否', '—'],
         ['支承辊 圆柱', '—', '圆柱', '06-12 王工', '新建', '6（6 ＝）', '否', '—']], sel=0)
    L_, R_ = split(B, 0.45, 0.55)
    x0, y0, x1, y1 = win(c, L_, '复制自 F3 标准凸度的辊（7）')
    tbl(c, x0, y0, x1 - x0, [('辊号', 1.2), ('与模板', 1.0), ('下次来磨', 1.3)],
        [['R-2026-031', {'t': '＝ 一致', 'badge': 'ok'}, '已换（方案 5）'], ['R-2026-022', {'t': '＝ 一致', 'badge': 'ok'}, '必须采用'],
         ['R-2026-007', {'t': '≠ 已改动', 'badge': 'warn'}, '普通提示'], ['R-2026-041', {'t': '＝ 一致', 'badge': 'ok'}, '必须采用']], rowh=p.rowH * 0.85)
    x0, y0, x1, y1 = win(c, R_, '辊形')
    profile_chart(c, (x0 + 4, y0 + 4, x1 - 4, y1 - 4), '━ 现版（11-01）  ╍ 上一版', [(cr(300), '#8A5A00', 2, '8 5'), (cr(280), C['target'], 3, None)], note='')
    return finish(c)

@screen('5.6.2', '保存模板 · 必须采用（页内决定）', '新', '保存模板时在页内决定（I12）：竖键 8 保存（普通提示）· 6 保存并必须采用… · 7 取消。表格逐支列出复制自它的辊（P7 + I13）：'
        '与模板一致 / 已改动、改动摘要、位置、长度对齐、下次来磨。"已改动"的辊默认"普通提示"，逐支用竖键 2–4 改；竖键 1 只对"一致"的辊生效。')
def s562(p):
    c, w = shell(p, 'tpl', 'JOG', '模板 / F3 标准凸度 › 保存', '', user='管理员 张工', modeL='模板',
                 vkeys=['一致的\n全部必须采用', '这支：普通提示', '这支：必须采用', '这支：不提示', '看差异', '保存并\n必须采用…', ('✕ 取消', 'cancel'), ('✓ 保存\n（普通提示）', 'ok')],
                 hkeys=TPLK, hk_on=None, ask=True, modal=True,
                 dialog='保存 F3 标准凸度的更新：✓ 保存 = 各辊下次来磨时提示；保存并必须采用… = 按下表执行（不自动取消）')
    x0, y0, x1, y1 = w
    y = strip(c, x0, y0, x1 - x0, [('本次更新', '凸度 300 → 280 · 工序 4 精磨拖板 800 → 760'), ('说明', '换轧制计划 F3-26Q4')] if p.name == 'std'
              else [('更新', '凸度 300 → 280 · 拖板 800 → 760')])
    x0_, y0_, x1_, y1_ = win(c, (x0, y, x1, y1), '复制自它的辊（7 支：一致 5 · 已改动 2）', True, '"必须采用"只在保存后对还没来磨的辊有效')
    tbl(c, x0_, y0_, x1_ - x0_, [('辊号', 1.1), ('机架', 0.5, 'l', True), ('当前方案', 0.8), ('与模板', 1.0), ('改动摘要', 1.6, 'l', True),
                                 ('位置', 0.7), ('复制对齐（名义）', 1.3), ('下次来磨', 1.3)],
        [['R-2026-031', 'F3 上', '方案 3', {'t': '＝ 一致', 'badge': 'ok'}, '—', '待磨', '相同', '必须采用'],
         ['R-2026-022', 'F3 下', '方案 2', {'t': '＝ 一致', 'badge': 'ok'}, '—', '在机', '相同', '必须采用'],
         ['R-2026-007', 'F3 下', '方案 4', {'t': '≠ 已改动', 'badge': 'warn'}, '光磨 1→2（特批）', '库存', '相同', {'t': '普通提示 ▸', 'b': True}],
         ['R-2026-041', 'F3 上', '方案 1', {'t': '＝ 一致', 'badge': 'ok'}, '—', '库存', '相同', '必须采用'],
         ['R-2026-044', 'F3 下', '方案 1', {'t': '＝ 一致', 'badge': 'ok'}, '—', '库存', {'t': '▲ 名义差 10 须确认', 'col': '#8A4A00'}, '排除（先处理对齐）'],
         ['R-2026-045', 'F3 下', '方案 2', {'t': '≠ 已改动', 'badge': 'warn'}, '凸度 320（特批）', '在机', '相同', '不提示'],
         ['R-2026-046', 'F3 上', '方案 1', {'t': '＝ 一致', 'badge': 'ok'}, '—', '待磨', '相同', '必须采用']], sel=2)
    return finish(c)

RECK = ['总览', '磨削记录', '日报', '月报', '磨前报表', '统计']

@screen('5.8.1', '总览', '改', '"今日计划变更"改为"待处理"：模板更新的采用进度、反复手调的辊（同一参数连续 3 次调整 → 建议存为新方案，P16 / O16）。'
        '最近磨削列"方案"（辊形 · 工艺）与标记（Δ 本次调整、例外、返磨、▲ 道次表缺）。')
def s581(p):
    c, w = shell(p, 'records', 'JOG', '查询与报表 / 总览', '2026-11-02', user='管理员 张工',
                 vkeys=['查询…', '打印…', '导出…', '存为新方案…', '', '', '', ''], hkeys=RECK, hk_on=0,
                 dialog='点一行看完整记录；"待处理"里的建议由工艺员决定')
    A, B = split(w, 0.42, 0.58, vertical=True)
    a1, a2, a3 = split(A, 1, 1.4, 1)
    x0, y0, x1, y1 = win(c, a1, '临近报废 3', True)
    tbl(c, x0, y0, x1 - x0, [('辊号', 1.4), ('剩余 mm', 0.8, 'r')], [['R-2026-003', '0.9'], ['R-2025-188', '1.4'], ['R-2025-171', '2.0']], rowh=p.rowH * 0.85)
    x0, y0, x1, y1 = win(c, a2, '待处理 3')
    tbl(c, x0, y0, x1 - x0, [('', 0.3), ('事项', 2.6), ('', 1.0, 'r')],
        [[{'t': '↑', 'col': '#8A4A00', 'b': True}, 'F3 标准凸度 必须采用', '2 / 4 已换'],
         [{'t': 'Δ', 'col': '#3D5466', 'b': True}, 'R-2026-022 拖板连续 3 次调慢', '建议存为新方案'],
         [{'t': '▲', 'col': '#B8600A', 'b': True}, 'R-2026-044 长度对齐待确认', '待工艺员']], rowh=p.rowH * 0.85)
    x0, y0, x1, y1 = win(c, a3, '不合格待返磨 1')
    tbl(c, x0, y0, x1 - x0, [('辊号', 1.2), ('原因', 1.2, 'r')], [['R-2026-007', '辊形误差 6.1']], rowh=p.rowH * 0.85)
    x0, y0, x1, y1 = win(c, B, '最近磨削（按日期）', right='筛选：日期 · 班次 · 操作人 · 方案 · 用途 · 有调整 · 合格')
    ok = {'t': '✓ 合格', 'col': C['ok'], 'b': True}
    tbl(c, x0, y0, x1 - x0, [('时间', 1.1), ('辊号', 1.1), ('方案', 2.2), ('去除', 0.6, 'r'), ('结果', 0.9), ('标记', 1.3)],
        [['11-02 10:15', 'R-2026-031', '3 · 凸度300 · 工作辊标准', '0.35', ok, 'Δ 3'],
         ['11-01 21:15', 'R-2026-007', '4 · 凸度300 · 光磨加强', '0.38', {'t': '✕ 返磨', 'col': '#8E1B1B', 'b': True}, ''],
         ['11-01 16:02', 'R-2026-014', '2 · 凸度300 · 工作辊标准', '0.12', {'t': '⏸ 中断', 'col': '#6D7F8D', 'b': True}, ''],
         ['11-01 10:20', 'R-2026-022', '2 · 凸度300 · 工作辊标准', '0.33', ok, 'Δ 1 · ▲ 缺 2 道'],
         ['10-31 14:05', 'R-2026-045', '2 · 凸度320 · 特批', '0.30', ok, '例外·试磨']])
    return finish(c)

@screen('5.8.2', '磨削记录', '改', '顶部一行回答"五个问题"（O18 折叠成一行，点开看详情）；列表加方案与标记列（P13）；竖键加"道次表""存为新方案…"。辊形图横轴中心 0。')
def s582(p):
    c, w = shell(p, 'records', 'JOG', '记录 / 磨削记录', '共 42 条 · 筛选：只看有调整', user='管理员 张工',
                 vkeys=[('磨前 / 磨后', 'on'), '误差曲线', '道次表', '补偿收敛', '下发参数 ▸', '存为新方案…', '打印…', '≡▸'],
                 hkeys=RECK, hk_on=1, dialog='上下扫动翻记录；"下发参数"看实际写进 NC 的数；"存为新方案…"把这次的调整存成方案（工艺员）')
    L_, R_ = split(w, 0.3, 0.7)
    x0, y0, x1, y1 = win(c, L_, '磨削记录', right='11-01 … 11-02')
    ok = {'t': '✓', 'col': C['ok'], 'b': True}
    tbl(c, x0, y0, x1 - x0, [('辊号', 1.3), ('日期', 0.7), ('方案', 0.5, 'r'), ('', 0.3, 'c'), ('标记', 1.0)],
        [['R-2026-031', '11-02', '3', ok, 'Δ 3'], ['R-2026-007', '11-01', '4', {'t': '✕', 'col': '#8E1B1B', 'b': True}, ''],
         ['R-2026-014', '11-01', '2', {'t': '⏸', 'col': '#6D7F8D', 'b': True}, '中断'], ['R-2026-022', '11-01', '2', ok, 'Δ1 ▲缺'],
         ['R-2026-045', '10-31', '2', ok, '例外']], sel=0)
    rx0, ry0, rx1, ry1 = R_
    h = (64 if p.name == 'std' else 56)
    c.rect(rx0, ry0, rx1 - rx0, h, '#FFFFFF', C['border'])
    q = ([('按什么', '方案 3 · 序号 118 ✓'), ('人的决定', 'Δ 3 项'), ('实际', '23 道 · 倍率 97%'), ('结果', '✓ 合格'), ('事件', '越界 1')] if p.name == 'std'
         else [('按什么', '方案 3 ✓'), ('决定', 'Δ 3 项'), ('实际', '23 道 97%'), ('结果', '✓ 合格'), ('事件', '越界 1')])
    cw = (rx1 - rx0) / 5
    for i, (a, b) in enumerate(q):
        c.text(rx0 + i * cw + 10, ry0 + p.fs * 1.15, a, p.fs, C['ink2'])
        c.text(rx0 + i * cw + 10, ry0 + p.fs * 1.15 + p.f * 1.2, b, p.f, C['ok'] if '✓ 合格' in b else C['ink'], weight=600)
        if i: c.line(rx0 + i * cw, ry0 + 6, rx0 + i * cw, ry0 + h - 6)
    M, K = split((rx0, ry0 + h + 6, rx1, ry1), 0.58, 0.42)
    x0, y0, x1, y1 = win(c, M, '磨前 / 磨后', True, '方案 3 · 凸度300 · 工作辊标准')
    import math
    meas = [(z, v + 1.5 * math.sin(z / 40.0)) for z, v in cr()]
    profile_chart(c, (x0 + 4, y0 + 4, x1 - 4, y1 - 4), '━ 磨后  ┅ 磨前', [(wavy(), C['pre'], 2, '6 4'), (meas, C['target'], 3, None)])
    x0, y0, x1, y1 = win(c, K, '12 项指标')
    kv(c, x0 + 6, y0 + 2, x1 - x0 - 12, [('磨前直径 头 / 尾', '600.35 / 600.34'), ('磨后直径 头 / 尾', '600.00 / 600.00'), ('锥度', '1.8 µm'),
                                          ('辊形误差 RMS', '2.6 µm'), ('圆度', '3.1 µm'), ('实际凸度', '+298 µm'), ('长度对齐', '两端截 1.0'),
                                          ('磨削时长', '63 min'), ('补偿迭代', '7 次'), ('操作者', '李工')][: (10 if p.name == 'std' else 8)])
    return finish(c)

@screen('5.8.3', '下发参数（子视图）', '改', '下发那一刻的快照：三列"设定值 / 本次值 / 调整人"（P14），顶部写下发序号与 NC 确认时刻、长度对齐；右边是实际下发的点列（中心 0）。')
def s583(p):
    c, w = shell(p, 'records', 'JOG', '记录 / 磨削记录 › 下发参数', 'R-2026-031 · 11-02 10:15', user='管理员 张工',
                 vkeys=['导出…', '打印…', '', '', '', '', '', '« 返回'], hkeys=RECK, hk_on=1, dialog='这是下发瞬间写入 NC 的数，之后改方案不影响这里')
    x0, y0, x1, y1 = w
    y = strip(c, x0, y0, x1 - x0, [('方案', '3 · 凸度300 · 工作辊标准'), ('下发序号', '118'), ('NC 确认', '10:15:07'), ('长度对齐', '两端各截 1.0')] if p.name == 'std'
              else [('方案', '3'), ('序号', '118 · 10:15:07'), ('对齐', '截 1.0')])
    L_, R_ = split((x0, y, x1, y1), 0.58, 0.42)
    a, b, cc = '', '', ''
    x0_, y0_, x1_, y1_ = win(c, L_, '工序参数（本次调整 3 项）', True)
    rows = [['3 半精磨', '拖板 mm/min', '2300', {'t': '2300→2100', 'chg': True}, '李工 · 核对'],
            ['3 半精磨', '周期进给 µm', '10', '10', ''],
            ['4 精磨', '拖板 mm/min', '800', {'t': '800→720', 'chg': True}, '李工 · 精磨第 4 道起'],
            ['4 精磨', '道次', '6', '6', ''], ['5 光磨', '光磨道次', '1', {'t': '1→2', 'chg': True}, '李工 · 核对'], ['5 光磨', '折返时间 s', '1.0', '1.0', '']]
    tbl(c, x0_, y0_, x1_ - x0_, [('工序', 0.9), ('参数', 1.3), ('设定值', 0.8, 'r'), ('本次值', 1.3, 'r'), ('调整人', 1.4, 'l', True)], rows)
    rh = p.rowH; hy = y0_ + p.hdrH
    for i in (0, 2, 4): c.rect(x0_, hy + i * rh + 3, 4, rh - 6, '#3D5466')
    x0_, y0_, x1_, y1_ = win(c, R_, '下发点列（101 点）')
    profile_chart(c, (x0_ + 4, y0_ + 4, x1_ - 4, y0_ + (x1_ - x0_) / 1.6), '━ 下发点列', [(cr(300, 40, 1798), C['target'], 2, None)], note='')
    yy = y0_ + (x1_ - x0_) / 1.6 + 10
    kv(c, x0_ + 6, yy, x1_ - x0_ - 12, [('点距', '17.98 mm（等距）'), ('首点 / 末点 Z', '−899.0 / +899.0'), ('插值', 'NC 线性 · 弦高 ≤ 0.03 µm')])
    return finish(c)

@screen('5.8.4', '存为新方案（页内决定）', '新', '逐项勾选要带进新方案的调整（P8、I16）：核对时的调整默认勾，运行中的默认不勾并标"第 4 道起"。'
        '作业所用方案已不是当前方案时橙色提醒。名称与本辊已有方案重名时竖键 8 灰并说明。竖键 8 另存（不设当前）· 6 另存并设为当前…。')
def s584(p):
    c, w = shell(p, 'records', 'JOG', '记录 / 磨削记录 › 存为新方案', 'R-2026-031 · 作业 J20261102-04', user='管理员 张工',
                 vkeys=['勾选 / 取消', '', '', '', '', '另存并\n设为当前…', ('✕ 取消', 'cancel'), ('✓ 另存为方案 6\n（不设当前）', 'ok')],
                 hkeys=RECK, hk_on=1, ask=True, modal=True, dialog='把勾选的 2 项存成方案 6（来源：这次作业）。✓ 另存 = 不改变以后怎么磨（不自动取消）')
    L_, R_ = split(w, 0.6, 0.4)
    x0, y0, x1, y1 = win(c, L_, '这次的调整（3 项，勾选 2）', True)
    tbl(c, x0, y0, x1 - x0, [('', 0.35, 'c'), ('来自', 1.3), ('参数', 1.8), ('设定值', 0.75, 'r'), ('本次值', 0.75, 'r'), ('新方案', 0.8, 'r')],
        [[{'t': '☑', 'b': True}, '核对', '3 半精磨 · 拖板', '2300', '2100', {'t': '2100', 'b': True}],
         [{'t': '☑', 'b': True}, '核对', '5 光磨 · 光磨道次', '1', '2', {'t': '2', 'b': True}],
         ['☐', '精磨第 4 道起', '4 精磨 · 拖板', '800', '720', '800']], sel=0)
    x0, y0, x1, y1 = win(c, R_, '新方案')
    X = x0 + 8; W = x1 - x0 - 16; y = y0 + 10
    y = field(c, X, y, W, '名称', '凸度300 · 半精磨慢拖板', '', 'sel', lw=0.3)
    y = field(c, X, y, W, '用途', 'F3', '', lw=0.3)
    y = kv(c, X, y, W, [('基于', '方案 3'), ('来源', '作业 J20261102-04')])
    y += 10
    note_row(c, X, y, W, 'warn', '▲', '这次用的是方案 3', sub='现在的当前方案是方案 5（模板更新后换上）')
    return finish(c)

DIAGK = ['报警', '变量监视', '改动记录', '运行日志', '连接与接口', '备份与恢复']

@screen('5.9.2', '连接与接口', '改', '加"NC 接口"一块，对应《上位机与 NC 接口约定》：下发序号与 NC 已确认、参数有效、改动序号与已采用、道次缓冲写 / 读 / 溢出、看门狗。'
        '"保底与降级"写明：上位机不在控制回路里，断开时机床照常磨完。')
def s592(p):
    c, w = shell(p, 'diag', 'JOG', '诊断 / 连接与接口', '接口约定 v1', user='制造商 王工',
                 vkeys=['刷新', '复制诊断信息', '测试连接…', '', '', '', '', ''], hkeys=DIAGK, hk_on=4,
                 dialog='"NC 接口"里的序号都是单调递增；看门狗只提示，不干预机床')
    L_, R_ = split(w, 0.36, 0.64)
    A, B = split(L_, 0.5, 0.5, vertical=True)
    x0, y0, x1, y1 = win(c, A, 'OPC UA 连接', True)
    kv(c, x0 + 6, y0 + 2, x1 - x0 - 12, [('端点', 'opc.tcp://192.168.214.1:4840'), ('状态', {'t': '● 已连接', 'badge': 'ok'}),
                                          ('监视周期', '100 ms'), ('标签映射', '156 条 · 缺 0'), ('连续在线', '6 h 12 min')])
    x0, y0, x1, y1 = win(c, B, '保底与降级')
    kv(c, x0 + 6, y0 + 2, x1 - x0 - 12, [('NC 侧报废保护', {'t': '✓ 启用', 'badge': 'ok'}), ('补偿降级级别', '0 全功能'),
                                          ('上位机断开时', '机床照常磨完'), ('道次数据', '断开期间存在 NC 缓冲')])
    x0, y0, x1, y1 = win(c, R_, 'NC 接口（接口约定 v1）')
    ok = {'t': '✓', 'badge': 'ok'}
    tbl(c, x0, y0, x1 - x0, [('项', 1.4), ('上位机', 1.0, 'r'), ('NC', 1.0, 'r'), ('说明', 1.6, 'l', True), ('', 0.5, 'c')],
        [{'_sub': '作业下发（第 2 节）'},
         ['下发序号 / NC 已确认', '118', '118', 'NC 循环启动时锁存', ok],
         ['参数有效', '—', 'true', '下发前先置 false', ok],
         {'_sub': '运行中改参数（第 3 节）'},
         ['改动序号 / 已采用', '7', '6', '等精磨第 4 道开始', {'t': '⧗', 'badge': 'warn'}],
         {'_sub': '道次数据（第 4 节）'},
         ['写序号 / 读序号', '15', '15', '16 槽 · 溢出 0', ok],
         ['看门狗', '0.2 s', '门限 10 s', '只提示，不停机', ok],
         {'_sub': '补偿（第 6 节）'},
         ['行程版本 / 修正量', '—', 'v7 · +1.8 µm', '只读', ok]])
    return finish(c)

CFGK = ['机床配置', '标签映射', '系统']
GROUPS = ['轴', '主轴', '测量', '作业核对与方案', '默认可调范围', '辅助动作', '手动行程', '报警阈值']

def cfg_groups(c, r, sel):
    x0, y0, x1, y1 = win(c, r, '分组')
    tbl(c, x0, y0, x1 - x0, [('', 1)], [[g] for g in GROUPS], sel=sel, header=False)

@screen('5.10.1', '机床配置 · 作业核对与方案', '改', 'v3"辊形长度容差 2%"拆成三项（P18）：复制门槛、下发容差（mm 与 % 取小者）、端部非工作区默认；'
        '"改计划权限"改为"方案编辑权限"；加看门狗门限与管理员空闲回操作者（I19）。')
def s5101(p):
    c, w = shell(p, 'cfg', 'JOG', '调试 / 机床配置 machine.json · ● 未保存', '', user='制造商 王工', modeL='调试',
                 vkeys=['加一行', '删除这一行…', '', '', '', '恢复上一版…', ('✕ 放弃改动', 'cancel'), ('✓ 保存…', 'ok')],
                 hkeys=CFGK, hk_on=0, dialog='下发容差：实测与名义辊身长度差多少以内由系统按中心对齐截取 / 延伸 · mm 与 % 取小者 · 保存前自动备份')
    L_, R_ = split(w, 0.24, 0.76)
    cfg_groups(c, L_, 3)
    A, B = split(R_, 0.66, 0.34, vertical=True)
    x0, y0, x1, y1 = win(c, A, '作业核对与方案', True, 'machine.json › jobChecks')
    X = x0 + 8; W = x1 - x0 - 16; y = y0 + 8
    for lab, v, u, st in [('复制门槛（名义长度差）', '1.0', 'mm', 'normal'), ('下发容差', '2.0', 'mm', 'sel'), ('下发容差（比例）', '0.2', '%', 'normal'),
                          ('端部非工作区默认', '端部圆柱段', '', 'normal'), ('工件线速度 下限 / 上限', '40 / 90', 'm/min', 'normal'),
                          ('方案编辑权限', '管理员', '', 'normal'), ('道次数据看门狗', '10', 's', 'normal'), ('管理员空闲回操作者', '15', 'min', 'normal')][: (8 if p.name == 'std' else 7)]:
        y = field(c, X, y, W, lab, v, u, st, lw=0.48)
    x0, y0, x1, y1 = win(c, B, '按辊重限头架转速')
    tbl(c, x0, y0, x1 - x0, [('辊重 ≤ t', 1, 'r'), ('头架最高 r/min', 1, 'r')], [['5', '60'], ['10', '45'], ['20', '30']], rowh=p.rowH * 0.8)
    return finish(c)

@screen('5.10.2', '机床配置 · 默认可调范围', '新', '方案没单独设范围时用这张表（设计说明 5.3 初稿，待甲方工艺员确认）。可调方式：±% / 只减 / 只加 / 不可调；'
        '机床限是硬上限，方案和作业都超不过。')
def s5102(p):
    c, w = shell(p, 'cfg', 'JOG', '调试 / 机床配置 machine.json', '', user='制造商 王工', modeL='调试',
                 vkeys=['改方式 ▸', '', '', '', '', '恢复上一版…', ('✕ 放弃改动', 'cancel'), ('✓ 保存…', 'ok')],
                 hkeys=CFGK, hk_on=0, dialog='默认可调范围：工艺员可在方案里逐个参数收窄或在机床限内放宽')
    L_, R_ = split(w, 0.24, 0.76)
    cfg_groups(c, L_, 4)
    x0, y0, x1, y1 = win(c, R_, '默认可调范围', True, 'machine.json › adjustRanges')
    lock = {'t': '🔒 不可调', 'col': '#404C58'}
    tbl(c, x0, y0, x1 - x0, [('参数', 1.6), ('可调方式', 1.0), ('幅度', 0.8, 'r'), ('机床限', 1.1, 'r'), ('说明', 1.8, 'l', True)],
        [['拖板速度', '±%', '10', '100–3000 mm/min', '按辊况快慢'], ['头架转速', '±%', '10', '5–60 r/min', '按辊重另有上限'],
         ['周期进给 / 连续进给', '只减', '—', '0–20 µm', '吃刀只能更浅'], ['光磨道次', '只加', '—', '0–10', ''],
         ['折返时间', '±%', '50', '0–5 s', ''], ['磨削量、磨前直径、起点 Z', '作业填', '—', '—', '每次都填'],
         ['砂轮线速度', lock, '—', '≤ 45 m/s', ''], ['变速设置', lock, '—', '—', ''], ['辊形、工序顺序', lock, '—', '—', '改方案']], sel=0)
    return finish(c)

def copy_screen(sid, v3id, title, cap, subs=(), over=None, tag='不变', post=None):
    @screen(sid, title, tag, cap)
    def f(p, v3id=v3id, subs=subs, over=over, post=post):
        svg = patch(v3id, p, subs=subs, over=over(p) if callable(over) else over)
        return post(svg, p) if post else svg
    return f

def cmp_l1(svg, p):
    """R3-4：紧凑档手动页位置值 28 → 38 px（L1 紧凑档）。"""
    return re.sub(r'font-size="28"([^>]*class="m")', r'font-size="38"\1', svg) if p.name == 'cmp' else svg

def jog_confirm(p):
    """R3-1：确认态只留停止类与不需确认的键；其余竖键与横键灰。"""
    return dict(vkeys=['砂轮停止', ('拖板往复…', 'off'), '冷却液 关', ('带启动装置', 'off'), ('定位 ▸', 'off'), ('各轴归位…', 'off'),
                       ('✕ 取消', 'cancel'), ('✓ 确认', 'ok')], modal=True,
                dialog=('砂轮给定 35.0 → 38.0 m/s（590 → 640 r/min）。按 ✓ 确认写入；砂轮在转，转速立即变（5 秒不按自动取消）'
                        if p.name == 'std' else '砂轮给定 35.0 → 38.0 m/s，转速立即变。✓ 确认写入（5 s 自动取消）'))

copy_screen('5.1.1', '5.1.1', '手动磨削（JOG 基本画面）', '同 v3，图示改砂轮给定后等确认（5 秒）。确认态（R3-1）：会提问的动作键与横键全部变灰，'
            '只留停止类（砂轮停止）与不需确认的键（冷却液）；按停止键即取消当前确认。紧凑档位置值 38 px（R3-4）。', over=jog_confirm, post=cmp_l1, tag='改')
for _sid, _t in [('5.1.2', '测量臂（手动动作页样板）'), ('5.1.3', '尾架'), ('5.1.4', '头架拨盘'), ('5.1.5', '托瓦'), ('5.1.6', '测量对中')]:
    copy_screen(_sid, _sid, _t, '内容同 v3；换 v4 外壳与路径条写法；紧凑档位置值 38 px（R3-4）。', post=cmp_l1)
copy_screen('5.1.7', '5.1.7', '辅助循环 ▸（横键展开）', '同 v3：横键换成循环清单；图示为启动砂轮修整等确认（5 秒），确认态下其余键灰（R3-1）。',
            over=dict(vkeys=[('启动修整…', 'off'), '', '', '', '', '', ('✕ 取消', 'cancel'), ('✓ 确认', 'ok')], modal=True), tag='改')
@screen('5.2.4', '换方案：选原因', '改', '同 v3 的做法：选了"仅本次用"后竖键换成常用原因，只有"其他…"要输入；记录标"例外"。画面沿用选方案子视图。')
def s524(p):
    return plan_picker(p, 'reason')
copy_screen('5.2.6', '5.2.5', '中断处理', '同 v3：续磨必须先测量，再进核对页；结束则记"中断"。')
copy_screen('5.3.2', '5.3.2', '补偿', '同 v3（收敛图按通则）；横键加"道次表"。每道的补偿量也进道次表。', over=dict(hkeys=AUTOK, hk_on=0))
copy_screen('5.3.3', '5.3.3', '程序段', '同 v3。', over=dict(hkeys=AUTOK, hk_on=1))
copy_screen('5.4.5', '5.4.5', '导入预览', '同 v3：先预览再导入，有错的行不导入；导入文件里的"计划"列改为"方案来源"（模板名）。',
            subs=[('目标辊形', '方案来源'), ('计划', '方案来源')])
copy_screen('5.7.1', '5.7.1', '砂轮', '同 v3。')
copy_screen('5.7.2', '5.7.2', '标定', '同 v3。')
copy_screen('5.9.1', '5.9.1', '报警', '同 v3。')
copy_screen('5.11.1', '5.11.1', '登录（弹出框）', '同 v3：点标题行用户名打开；管理员空闲 15 min 自动回到操作者（可配置）。')
