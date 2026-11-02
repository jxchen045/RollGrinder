"""RGX 界面样机生成器：与《界面修订稿 v3》同一外壳、同一令牌，真实像素。"""
import re, html, math, os

# ---------- 令牌 ----------
C = dict(
    bg='#EBF0F6', left='#1B2228', leftOn='#0199C8', side='#404C58', title='#E6EBF1', mode='#3D5466',
    blue='#0199C8', cur='#67C2DF', sel='#A9DCEC', hdr='#C6CBD1', win='#EBF0F6', border='#A7B1BA',
    ink='#141A1F', ink2='#4A5864', line='#D5DCE3', key='#DDE1EA', keyOff='#8A96A1', path='#A7B1BA',
    chan='#6D7F8D', ask='#F4E3A1', cancel='#8E1B1B', ok='#37703D', okSoft='#E7F4EA', okLine='#9CCBA7',
    warn='#B8600A', warnSoft='#FDF0DC', warnLine='#E3B26A', red='#B3241C', redSoft='#F8E1DF', redLine='#E0A19B',
    info='#15507F', infoSoft='#E4F1F8', infoLine='#8CC3DE', ro='#DDE2E7', white='#FFFFFF', alt='#F4F7FA',
    grid='#E1E7EC', target='#15507F', meas='#B3241C', tol='#9AA0A6', band='#E6ECF1', pre='#7A8591', seg='#8D97A3',
    wheel='#E8891A', chg='#7A3FA0', chgSoft='#F1E8F7', chgLine='#C7A6DD', mode2='#3D5466',
)

class P:  # 档位
    def __init__(s, **k): s.__dict__.update(k)

STD = P(name='std', W=1920, H=1080, left=80, itemY=4, itemH=85, itemP=88, iconF=24, iconDy=37, labF=18, labDy=68.6,
        sideY=1010, sideH=66, titleH=52, titleF=22, infoF=18, modeW=200, pathY=52, pathH=32, pathF=22, chanH=32, chanF=18,
        rcolX=1720, rcolW=200, skW=98, skH=46, skP=48, skF=18, vkX=1723, vkW=194, vkH=89, vkY=202, vkP=95, vkF=22,
        dlgY=962, dlgH=34, dlgF=22, hkY=996, hkH=84, hkX=83, hkP=205, hkW=199, hkKH=76, hkF=22, lastX=1723, lastW=194,
        gap=6, wtH=36, wtF=22, wtRF=18, f=22, fs=18, fv=24, rowH=52, hdrH=41.6, kvP=40.6, fieldH=42, pad=8,
        L1=58, L2=32)
CMP = P(name='cmp', W=1366, H=768, left=64, itemY=4, itemH=73, itemP=76, iconF=22, iconDy=31.9, labF=16, labDy=59.3,
        sideY=707, sideH=57, titleH=40, titleF=20, infoF=16, modeW=156, pathY=40, pathH=28, pathF=16, chanH=28, chanF=16,
        rcolX=1210, rcolW=156, skW=76, skH=34, skP=36, skF=16, vkX=1213, vkW=150, vkH=61.5, vkY=152, vkP=65.5, vkF=20,
        dlgY=676, dlgH=28, dlgF=20, hkY=704, hkH=64, hkX=67, hkP=143.25, hkW=137.2, hkKH=56, hkF=20, lastX=1213, lastW=150,
        gap=4, wtH=34, wtF=20, wtRF=16, f=20, fs=16, fv=20, rowH=46, hdrH=36.8, kvP=35.9, fieldH=36, pad=8,
        L1=40, L2=28)

AREAS = [('machine', '⚙', '机床'), ('rolls', '◍', '轧辊'), ('plan', '⌒', '方案'), ('tpl', '▦', '模板'),
         ('wheel', '◎', '砂轮'), ('records', '▤', '记录'), ('diag', '⚠', '诊断')]

def esc(s):
    return html.escape(str(s), quote=False)

def tw(s, size):
    """估算文字宽度。"""
    w = 0.0
    for ch in str(s):
        o = ord(ch)
        if o > 0x2E80: w += size * 1.0
        elif ch in 'ilI.,:;|!\'·': w += size * 0.3
        elif ch == ' ': w += size * 0.3
        elif ch.isupper() or ch in 'mwMW%@': w += size * 0.68
        else: w += size * 0.55
    return w

class Canvas:
    def __init__(s, p):
        s.p = p; s.o = []
    def rect(s, x, y, w, h, fill, stroke=None, sw=1, rx=0, dash=None, op=None):
        a = f'<rect x="{x:.1f}" y="{y:.1f}" width="{max(w,0):.1f}" height="{max(h,0):.1f}" fill="{fill}"'
        if stroke: a += f' stroke="{stroke}" stroke-width="{sw}"'
        if rx: a += f' rx="{rx}"'
        if dash: a += f' stroke-dasharray="{dash}"'
        if op is not None: a += f' fill-opacity="{op}"'
        s.o.append(a + ' />')
    def text(s, x, y, t, size, fill=None, anchor='start', weight=400, mono=False, italic=False):
        fill = fill or C['ink']
        cls = ' class="m"' if mono else ''
        st = ' font-style="italic"' if italic else ''
        s.o.append(f'<text x="{x:.1f}" y="{y:.1f}" font-size="{size}" fill="{fill}" text-anchor="{anchor}" font-weight="{weight}"{cls}{st}>{esc(t)}</text>')
    def line(s, x1, y1, x2, y2, stroke=None, sw=1, dash=None):
        d = f' stroke-dasharray="{dash}"' if dash else ''
        s.o.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke or C["line"]}" stroke-width="{sw}"{d}/>')
    def path(s, d, stroke, sw=2, fill='none', dash=None, op=None):
        a = f'<path d="{d}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}"'
        if dash: a += f' stroke-dasharray="{dash}"'
        if op is not None: a += f' fill-opacity="{op}"'
        s.o.append(a + '/>')
    def raw(s, t): s.o.append(t)

# ---------- 外壳 ----------
def shell(p, area, mode, path, path_r='', machine=False, chan=('', '', ''), vkeys=(), hkeys=(), hk_on=None,
          dialog='', ask=False, user='操作者 李工', clock='2026-11-02 10:31', modeL=None, more=True, areas=None):
    c = Canvas(p)
    c.rect(0, 0, p.W, p.H, C['bg'])
    c.rect(0, 0, p.left, p.H, C['left'])
    for i, (k, ic, lab) in enumerate(areas or AREAS):
        y = p.itemY + i * p.itemP
        c.rect(3, y, p.left - 6, p.itemH, C['leftOn'] if k == area else C['left'])
        c.text(p.left / 2, y + p.iconDy, ic, p.iconF, C['white'], 'middle')
        c.text(p.left / 2, y + p.labDy, lab, p.labF, C['white'], 'middle')
    c.rect(3, p.sideY, p.left - 6, p.sideH, C['side'])
    c.text(p.left / 2, p.sideY + p.sideH * 0.6, '⇆ 侧屏', p.labF, C['white'], 'middle')
    # 标题行
    rx = p.rcolX
    c.rect(p.left, 0, rx - p.left, p.titleH, C['title'])
    c.text(p.left + 12, p.titleH * 0.652, 'RGX 轧辊磨床', p.titleF, C['ink'], weight=700)
    c.text(rx - 12, p.titleH * 0.652, f'{user} · NC 已连接 · {clock}', p.infoF, C['ink2'], 'end')
    c.rect(rx, 0, p.rcolW, p.titleH, C['left'])
    hw = (p.rcolW - 6) / 2
    c.rect(rx + 2, 2, hw, p.titleH - 4, C['mode']); c.rect(rx + 4 + hw, 2, hw, p.titleH - 4, C['mode'])
    al = modeL or dict((k, l) for k, _, l in AREAS).get(area, '')
    c.text(rx + 2 + hw / 2, p.titleH * 0.625, al, p.infoF, C['white'], 'middle', 700)
    c.text(rx + 4 + hw * 1.5, p.titleH * 0.625, mode, p.infoF, C['white'], 'middle', 700)
    # 路径条
    if machine:
        c.rect(p.left, p.pathY, rx - p.left, p.pathH, C['path'])
        c.text(p.left + 10, p.pathY + p.pathH * 0.703, path, p.pathF, C['ink'])
        if path_r: c.text(rx - 10, p.pathY + p.pathH * 0.703, path_r, p.pathF, C['ink'], 'end')
        cy = p.pathY + p.pathH
        c.rect(p.left, cy, rx - p.left, p.chanH, C['chan'])
        c.text(p.left + 10, cy + p.chanH * 0.703, chan[0], p.chanF, C['white'])
        c.text((p.left + rx) / 2, cy + p.chanH * 0.703, chan[1], p.chanF, '#9BE3A2', 'middle', 700)
        c.text(rx - 10, cy + p.chanH * 0.703, chan[2], p.chanF, C['white'], 'end')
    else:
        c.rect(p.left, p.pathY, rx - p.left, p.pathH, C['blue'])
        c.text(p.left + 10, p.pathY + p.pathH * 0.703, path, p.pathF, C['white'])
        if path_r: c.text(rx - 10, p.pathY + p.pathH * 0.703, path_r, p.pathF, C['white'], 'end')
    # 右栏
    c.rect(rx, p.pathY, p.rcolW, p.dlgY - p.pathY, C['path'])
    sk = ['↶ 撤销', '↷ 重做', '⌨ 键盘', '▦ 计算', 'i 帮助', '◉ 截屏']
    for i, t in enumerate(sk):
        x = rx + 1 + (i % 2) * (p.skW + 2); y = p.pathY + 1 + (i // 2) * p.skP
        c.rect(x, y, p.skW, p.skH, C['side']); c.text(x + p.skW / 2, y + p.skH * 0.64, t, p.skF, C['white'], 'middle')
    vk = list(vkeys) + [''] * (8 - len(vkeys))
    for i, k in enumerate(vk[:8]):
        y = p.vkY + i * p.vkP
        kind = None
        if isinstance(k, tuple): k, kind = k
        fill, ink = C['key'], C['ink']
        if kind == 'cancel': fill, ink = C['cancel'], C['white']
        elif kind == 'ok': fill, ink = C['ok'], C['white']
        elif kind == 'on': fill, ink = C['blue'], C['white']
        elif kind == 'off': ink = C['keyOff']
        c.rect(p.vkX, y, p.vkW, p.vkH, fill)
        lines = k.split('\n')
        for j, ln in enumerate(lines):
            yy = y + p.vkH / 2 + p.vkF * 0.35 + (j - (len(lines) - 1) / 2) * (p.vkF * 1.15)
            c.text(p.vkX + p.vkW / 2, yy, ln, p.vkF, ink, 'middle')
    # 对话行
    c.rect(p.left, p.dlgY, p.W - p.left, p.dlgH, C['ask'] if ask else C['path'])
    c.text(p.left + 10, p.dlgY + p.dlgH * 0.73, dialog, p.dlgF, C['ink'], weight=700 if ask else 400)
    # 横键
    c.rect(p.left, p.hkY, p.W - p.left, p.hkH, C['path'])
    hk = list(hkeys) + [''] * (8 - len(hkeys))
    for i, k in enumerate(hk[:8]):
        x = p.hkX + i * p.hkP
        on = (i == hk_on); off = False
        if isinstance(k, tuple): k, off = k[0], k[1] == 'off'
        c.rect(x, p.hkY + 4, p.hkW, p.hkKH, C['blue'] if on else C['key'])
        c.text(x + p.hkW / 2, p.hkY + 4 + p.hkKH * 0.604, k, p.hkF, C['white'] if on else (C['keyOff'] if off else C['ink']), 'middle')
    c.rect(p.lastX, p.hkY + 4, p.lastW, p.hkKH, C['key'])
    c.text(p.lastX + p.lastW / 2, p.hkY + 4 + p.hkKH * 0.61, '>' if more else '', p.hkF + 2, C['keyOff'], 'middle', 700)
    top = p.pathY + p.pathH + (p.chanH if machine else 0) + p.gap
    work = (p.left + p.gap, top, rx - p.gap, p.dlgY - p.gap)
    return c, work

def finish(c, style_w=100.0):
    p = c.p
    w = 100.0 if p.name == 'std' else 71.15
    defs = ('<defs><pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">'
            '<line x1="0" y1="0" x2="0" y2="6" stroke="#9AA6B2" stroke-width="1.5"/></pattern>'
            '<pattern id="hatchw" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">'
            '<line x1="0" y1="0" x2="0" y2="6" stroke="#E3B26A" stroke-width="2"/></pattern></defs>')
    return f'<svg class="mk" viewBox="0 0 {p.W} {p.H}" role="img" style="width:{w:.2f}%">' + defs + ''.join(c.o) + '</svg>'

# ---------- 组件 ----------
def window(c, x, y, w, h, title, active=False, right='', rightcol=None):
    p = c.p
    c.rect(x, y, w, h, C['win'], C['border'])
    c.rect(x, y, w, p.wtH, C['blue'] if active else C['hdr'])
    ink = C['white'] if active else C['ink']
    c.text(x + 8, y + p.wtH * 0.72, title, p.wtF, ink, weight=500)
    if right: c.text(x + w - 8, y + p.wtH * 0.68, right, p.wtRF, rightcol or ink, 'end')
    return (x + 1, y + p.wtH + 1, x + w - 1, y + h - 1)

def badge(c, x, y, t, kind='plain', size=None):
    """徽标（V13）：高 28 / 24，圆角 4，1px 边，带图标。y 为文字基线。返回宽度。"""
    p = c.p; size = size or p.fs
    st = {'plain': (C['white'], C['border'], C['ink2']),
          'cur': ('#3D5466', '#3D5466', '#FFFFFF'),          # ★ 当前
          'frz': ('#FFFFFF', '#6D7F8D', '#404C58'),          # ❄ 冻结
          'hist': (None, None, '#7A8591'),                   # ◷ 历史
          'ok': (C['okSoft'], C['okLine'], C['ok']),         # ＝ 一致 / ✓
          'warn': (C['warnSoft'], C['warnLine'], '#8A4A00'), # ≠ 已改动 / ↑ 标准已更新 / ▲
          'red': ('#FFFFFF', '#8E1B1B', '#8E1B1B'),
          'info': ('#FFFFFF', '#6D7F8D', '#404C58'),
          'dark': ('#3D5466', '#3D5466', '#FFFFFF')}[kind]
    w = tw(t, size) + size * 0.9
    h = (28 if p.name == 'std' else 24) * size / (p.fs)
    if st[0]: c.rect(x, y - h * 0.74, w, h, st[0], st[1], 1, rx=4)
    c.text(x + w / 2, y, t, size, st[2], 'middle', 600)
    return w

def table(c, x, y, w, cols, rows, sel=None, header=True, rowh=None, alt=True, maxrows=None, fs=None, hs=None):
    """cols: [(名称, 宽度比例, 'l'/'r'/'c', 仅标准档?)]；cell: str 或 dict(t=, col=, b=, badge=, mono=, chg=)。"""
    p = c.p; rowh = rowh or p.rowH; fs = fs or p.f; hs = hs or p.fs
    cols = [col for col in cols if not (len(col) > 3 and col[3] and p.name == 'cmp')]
    keep = [i for i, col in enumerate(cols)]
    tot = sum(col[1] for col in cols)
    xs = []; cx = x
    for col in cols:
        cw = w * col[1] / tot; xs.append((cx, cw)); cx += cw
    yy = y
    if header:
        c.rect(x, yy, w, p.hdrH, C['hdr'])
        for (cx, cw), col in zip(xs, cols):
            al = col[2] if len(col) > 2 else 'l'
            tx = cx + 8 if al == 'l' else (cx + cw - 8 if al == 'r' else cx + cw / 2)
            c.text(tx, yy + p.hdrH * 0.66, col[0], hs, C['ink2'], {'l': 'start', 'r': 'end', 'c': 'middle'}[al])
        yy += p.hdrH
    for ri, row in enumerate(rows[:maxrows] if maxrows else rows):
        if isinstance(row, dict) and row.get('_sub'):
            c.rect(x, yy, w, rowh * 0.8, '#DCE3EA'); c.text(x + 8, yy + rowh * 0.55, row['_sub'], hs, C['ink2'], weight=700)
            yy += rowh * 0.8; continue
        cells = row['cells'] if isinstance(row, dict) else row
        fill = C['sel'] if ri == sel else ((C['alt'] if ri % 2 else C['white']) if alt else C['white'])
        if isinstance(row, dict) and row.get('fill'): fill = row['fill']
        c.rect(x, yy, w, rowh, fill)
        c.line(x, yy + rowh, x + w, yy + rowh)
        full = [cells[i] if i < len(cells) else '' for i in range(len(cells))]
        # 紧凑档删列：cells 与原 cols 对齐，需要过滤
        for (cx, cw), col, cell in zip(xs, cols, _cells_for(cols, full, c.p)):
            al = col[2] if len(col) > 2 else 'l'
            if isinstance(cell, dict):
                t = cell.get('t', ''); col_ = cell.get('col', C['ink']); b = 700 if cell.get('b') else 400
                if cell.get('badge'):
                    bx = cx + 8 if al != 'r' else cx + cw - 8 - (tw(t, hs) + hs * 0.9)
                    badge(c, bx, yy + rowh * 0.64, t, cell['badge']); continue
                if cell.get('chg'):
                    chg_value(c, cx + cw - 8 if al == 'r' else cx + 8, yy + rowh * 0.65, t, fs, al)
                    continue
                mono = cell.get('mono', False)
            else:
                t = cell; col_ = C['ink']; b = 700 if ri == sel and False else 400; mono = False
            tx = cx + 8 if al == 'l' else (cx + cw - 8 if al == 'r' else cx + cw / 2)
            c.text(tx, yy + rowh * 0.65, t, fs, col_, {'l': 'start', 'r': 'end', 'c': 'middle'}[al], b, mono)
            if isinstance(cell, dict) and cell.get('after'):
                ax = tx + (tw(t, fs) + 8 if al == 'l' else 8)
                badge(c, ax, yy + rowh * 0.64, cell['after'], cell.get('akind', 'plain'))
        yy += rowh
    return yy

_COLS_CACHE = {}
def _cells_for(cols, cells, p):
    return cells  # 调用方在紧凑档按需自行传入删减后的 cells

def tbl(c, x, y, w, cols, rows, **k):
    """cols 第 4 项为 True 表示仅标准档：紧凑档同时删掉 rows 中对应的格。"""
    p = c.p
    if p.name == 'cmp':
        idx = [i for i, col in enumerate(cols) if not (len(col) > 3 and col[3])]
        cols2 = [cols[i] for i in idx]
        rows2 = []
        for r in rows:
            if isinstance(r, dict) and r.get('_sub'): rows2.append(r); continue
            cells = r['cells'] if isinstance(r, dict) else r
            nc = [cells[i] if i < len(cells) else '' for i in idx]
            rows2.append(dict(r, cells=nc) if isinstance(r, dict) else nc)
        return table(c, x, y, w, cols2, rows2, **k)
    return table(c, x, y, w, cols, rows, **k)

def chg_value(c, x, y, t, size, al='r', old=None):
    """本次调整（V12）：设定值灰小字 → 本次值深色半粗；左侧竖条与"调"角标由调用方按格画。"""
    if old is None and '→' in t:
        old, t = [u.strip() for u in t.split('→', 1)]
    anc = {'l': 'start', 'r': 'end', 'c': 'middle'}[al]
    if old is None:
        c.text(x, y, t, size, '#1B2228', anc, 700, mono=True); return
    small = c.p.fs
    if al == 'r':
        c.text(x, y, t, size, '#1B2228', 'end', 700, mono=True)
        x2 = x - tw(t, size) * 0.95 - 6
        c.text(x2, y, '→', small, '#6D7F8D', 'end')
        c.text(x2 - small - 4, y, old, small, '#6D7F8D', 'end', mono=True)
    else:
        c.text(x, y, old, small, '#6D7F8D', 'start', mono=True)
        x2 = x + tw(old, small) * 0.95 + 6
        c.text(x2, y, '→', small, '#6D7F8D')
        c.text(x2 + small + 4, y, t, size, '#1B2228', 'start', 700, mono=True)

def adj_mark(c, x, y, h):
    """格左 4px #3D5466 竖条 + 右上"调"角标的竖条部分。"""
    c.rect(x, y + 3, 4, h - 6, '#3D5466')

def kv(c, x, y, w, pairs, pitch=None, lw=None, fs=None, vb=False):
    p = c.p; pitch = pitch or p.kvP; fs = fs or p.f
    yy = y
    for pr in pairs:
        lab, val = pr[0], pr[1]
        opt = pr[2] if len(pr) > 2 else {}
        c.text(x + 4, yy + pitch * 0.62, lab, fs, C['ink2'])
        if isinstance(val, dict) and val.get('chg'):
            chg_value(c, x + w - 4, yy + pitch * 0.62, val['t'], fs)
        elif isinstance(val, dict) and val.get('badge'):
            bw = tw(val['t'], p.fs) + p.fs * 0.9
            badge(c, x + w - 4 - bw, yy + pitch * 0.62, val['t'], val['badge'])
        else:
            c.text(x + w - 4, yy + pitch * 0.62, val, fs, opt.get('col', C['ink']), 'end', 700 if opt.get('b') or vb else 400)
        c.line(x, yy + pitch, x + w, yy + pitch)
        yy += pitch
    return yy

def field(c, x, y, w, lab, val, unit='', state='normal', lw=0.42, note=''):
    p = c.p; h = p.fieldH
    c.text(x + 4, y + h * 0.7, lab, p.f, C['ink'])
    fx = x + w * lw; fw = w * (1 - lw) - (tw(unit, p.fs) + 18 if unit else 4)
    fill = {'normal': C['white'], 'sel': C['cur'], 'ro': C['ro'], 'chg': C['chgSoft']}[state]
    c.rect(fx, y, fw, h, fill, C['chgLine'] if state == 'chg' else None)
    c.text(fx + fw - 8, y + h * 0.71, val, p.fv, C['chg'] if state == 'chg' else C['ink'], 'end', 700 if state == 'chg' else 400, mono=True)
    if unit: c.text(fx + fw + 6, y + h * 0.66, unit, p.fs, C['ink2'])
    if note: c.text(fx + 8, y + h * 0.66, note, p.fs, C['ink2'])
    return y + h + (10 if p.name == 'std' else 6)

def banner(c, x, y, w, t, kind='ok', right='', h=None, sub=''):
    p = c.p; h = h or (p.rowH * 0.85 if not sub else p.rowH * 1.45)
    fill, line, ink = {'ok': (C['okSoft'], C['okLine'], C['ok']), 'warn': (C['warnSoft'], C['warnLine'], C['warn']),
                       'info': (C['infoSoft'], C['infoLine'], C['info']), 'red': (C['redSoft'], C['redLine'], C['red']),
                       'chg': (C['chgSoft'], C['chgLine'], C['chg']), 'ask': ('#FBF3D0', '#D9BE58', '#6B5300')}[kind]
    c.rect(x, y, w, h, fill, line)
    c.rect(x, y, 6, h, line)
    ty = y + (h * 0.62 if not sub else p.f * 1.25)
    c.text(x + 16, ty, t, p.f, ink, weight=700)
    if right: c.text(x + w - 10, ty, right, p.f, C['ink2'], 'end')
    if sub: c.text(x + 16, ty + p.f * 1.35, sub, p.fs, C['ink'])
    return y + h + 6

def chips(c, x, y, items, h=None):
    p = c.p; h = h or p.fieldH * 0.85; xx = x
    for t, on in items:
        w = tw(t, p.f) + 24
        c.rect(xx, y, w, h, C['blue'] if on else C['key'])
        c.text(xx + w / 2, y + h * 0.68, t, p.f, C['white'] if on else C['ink'], 'middle')
        xx += w + 8
    return y + h

def nice(lo, hi, n=5):
    span = hi - lo
    raw = span / n; mag = 10 ** math.floor(math.log10(raw)); r = raw / mag
    step = (1 if r < 1.5 else 2 if r < 3 else 5 if r < 7 else 10) * mag
    t = math.ceil(lo / step) * step; out = []
    while t <= hi + 1e-9: out.append(round(t, 10)); t += step
    return out

def chart(c, x, y, w, h, series, xr, yr, xlab='Z mm（头架 → 尾架）', ylab='直径量 µm', legend='', xt=None, yt=None,
          vlines=(), hlines=(), bands=(), reset=True, note='', marks=(), hbands=()):
    """series: [(pts, color, width, dash)]；vlines: [(x, label, color, dash)]；bands: [(x0,x1,color,op,label)]。"""
    p = c.p
    c.rect(x, y, w, h, C['white'], C['line'])
    fs = p.fs
    lx = x + fs * 3.4; rx_ = x + w - fs * 1.5; ty = y + fs * 3.3; by = y + h - fs * 3.2
    if legend: c.text(x + 6, y + fs * 1.15, legend, fs, C['ink2'])
    if reset:
        bw = tw('⟲ 复位视图', fs) + 14
        c.rect(x + w - bw - 4, y + 4, bw, fs * 1.6, C['white'], C['border']); c.text(x + w - 4 - bw / 2, y + 4 + fs * 1.15, '⟲ 复位视图', fs, C['ink'], 'middle')
    if note: c.text(x + w - (tw('⟲ 复位视图', fs) + 26 if reset else 6), y + fs * 1.15, note, fs, C['ink2'], 'end')
    c.text(x + 6, ty - fs * 0.6, ylab, fs, C['ink2'])
    X = lambda v: lx + (v - xr[0]) / (xr[1] - xr[0]) * (rx_ - lx)
    Y = lambda v: by - (v - yr[0]) / (yr[1] - yr[0]) * (by - ty)
    for b in bands:
        x0, x1, col, op = b[:4]
        c.rect(X(x0), ty, X(x1) - X(x0), by - ty, col, op=op)
        if len(b) > 4 and b[4]: c.text((X(x0) + X(x1)) / 2, ty + fs * 1.1, b[4], fs * 0.9, C['ink2'], 'middle')
    for y0_, y1_, col, op in hbands:
        c.rect(lx, Y(y1_), rx_ - lx, Y(y0_) - Y(y1_), col, op=op)
    for v in (yt or nice(*yr)):
        c.line(lx, Y(v), rx_, Y(v), C['grid']); c.text(lx - 6, Y(v) + fs * 0.35, f'{v:g}', fs, C['ink2'], 'end')
    for v in (xt or nice(*xr, n=4)):
        c.line(X(v), ty, X(v), by, C['grid']); c.text(X(v), by + fs * 1.3, f'{v:g}', fs, C['ink2'], 'middle')
    c.rect(lx, ty, rx_ - lx, by - ty, 'none', '#B5BEC3')
    for hv, lab, col, dash in hlines:
        c.line(lx, Y(hv), rx_, Y(hv), col, 1.5, dash); c.text(rx_ - 4, Y(hv) - 4, lab, fs * 0.9, col, 'end')
    for vv, lab, col, dash in vlines:
        c.line(X(vv), ty, X(vv), by, col, 1.5, dash)
        if lab: c.text(X(vv), ty - 4, lab, fs * 0.9, col, 'middle')
    for pts, col, sw, dash in series:
        d = 'M' + ' L'.join(f'{X(a):.1f},{Y(b):.1f}' for a, b in pts)
        c.path(d, col, sw, dash=dash)
    for mx, my, lab, col in marks:
        c.rect(X(mx) - 4, Y(my) - 4, 8, 8, col)
        if lab: c.text(X(mx) + 8, Y(my) - 6, lab, fs * 0.9, col)
    c.text((lx + rx_) / 2, y + h - fs * 0.45, xlab, fs, C['ink2'], 'middle')
    return X, Y

def crown(L, c0, end=0.0, n=60, x0=0.0):
    pts = []
    for i in range(n + 1):
        z = x0 + L * i / n
        if z < x0 + end or z > x0 + L - end: v = 0.0
        else:
            u = (z - x0 - L / 2) / (L / 2 - end)
            v = c0 * (1 - u * u)
        pts.append((z, v))
    return pts

# ---------- 从 v3 样机改 ----------
def load_v3(path):
    return open(path, encoding='utf-8').read()

_EL = re.compile(r'<(rect|text|line|path|polyline|polygon|circle)\b([^>]*?)(/?)>(?:([^<]*)</text>)?')

def el_xy(attrs, tag):
    a = dict(re.findall(r'([\w-]+)="([^"]*)"', attrs))
    if tag in ('rect', 'text'): return float(a.get('x', 0)), float(a.get('y', 0))
    if tag == 'line': return float(a.get('x1', 0)), float(a.get('y1', 0))
    if tag == 'circle': return float(a.get('cx', 0)), float(a.get('cy', 0))
    m = re.search(r'([\d.]+)[ ,]([\d.]+)', a.get('d', a.get('points', '0 0')))
    return (float(m.group(1)), float(m.group(2))) if m else (0, 0)

def drop_box(svg, x0, y0, x1, y1):
    """删除起点落在框内的元素。"""
    def rep(m):
        x, y = el_xy(m.group(2), m.group(1))
        return '' if (x0 <= x < x1 and y0 <= y < y1) else m.group(0)
    return _EL.sub(rep, svg)

def append(svg, c):
    return svg.replace('</svg>', ''.join(c.o) + '</svg>')

# ---------- 以 v4 外壳 + v3 工作区重组 ----------
V3_AREA = {'机床': 'machine', '轧辊': 'rolls', '辊形': 'plan', '工艺': 'plan', '砂轮': 'wheel', '记录': 'records', '诊断': 'diag'}

def _els(svg):
    out = []
    for m in _EL.finditer(svg):
        tag, attrs, _, txt = m.group(1), m.group(2), m.group(3), m.group(4)
        a = dict(re.findall(r'([\w-]+)="([^"]*)"', attrs))
        x, y = el_xy(attrs, tag)
        out.append((tag, a, html.unescape(txt or ''), x, y, m.group(0)))
    return out

def from_v3(svg, p, subs=(), over=None):
    """读 v3 样机的外壳参数，换成 v4 外壳；工作区原样保留（文字按 subs 替换）。over 可覆盖外壳参数。"""
    els = _els(svg)
    def texts_at(cond):
        return [e for e in els if e[0] == 'text' and cond(e)]
    # 区域
    area = 'machine'
    for i in range(7):
        y = p.itemY + i * p.itemP
        r = [e for e in els if e[0] == 'rect' and abs(e[3] - 3) < .5 and abs(e[4] - y) < .5]
        if r and r[0][1].get('fill') == C['leftOn']:
            lab = texts_at(lambda e, y=y: abs(e[4] - (y + p.labDy)) < 1 and e[3] < p.left)
            area = V3_AREA.get(lab[0][2] if lab else '', 'machine')
    rx = p.rcolX
    info = texts_at(lambda e: e[4] < p.titleH and e[1].get('text-anchor') == 'end' and e[3] < rx)
    modes = texts_at(lambda e: e[4] < p.titleH and e[3] > rx)
    path_rect = [e for e in els if e[0] == 'rect' and abs(e[4] - p.pathY) < .5 and abs(e[3] - p.left) < .5]
    machine = bool(path_rect) and path_rect[0][1].get('fill') == C['path']
    pts = texts_at(lambda e: p.pathY < e[4] < p.pathY + p.pathH and p.left <= e[3] < rx)
    path = next((e[2] for e in pts if e[1].get('text-anchor') == 'start'), '')
    path_r = next((e[2] for e in pts if e[1].get('text-anchor') == 'end'), '')
    chan = ('', '', '')
    if machine:
        cy = p.pathY + p.pathH
        ct = texts_at(lambda e: cy < e[4] < cy + p.chanH and p.left <= e[3] < rx)
        g = lambda an: next((e[2] for e in ct if e[1].get('text-anchor') == an), '')
        chan = (g('start'), g('middle'), g('end'))
    vk = []
    for i in range(8):
        y = p.vkY + i * p.vkP
        r = [e for e in els if e[0] == 'rect' and abs(e[3] - p.vkX) < .5 and abs(e[4] - y) < .5]
        t = texts_at(lambda e, y=y: abs(e[3] - (p.vkX + p.vkW / 2)) < 1 and y < e[4] < y + p.vkH)
        lab = '\n'.join(e[2] for e in t)
        fill = r[0][1].get('fill') if r else C['key']
        kind = {'#8E1B1B': 'cancel', '#37703D': 'ok', '#0199C8': 'on'}.get(fill)
        if not kind and t and t[0][1].get('fill') == C['keyOff']: kind = 'off'
        vk.append((lab, kind) if kind else lab)
    hk = []; on = None
    for i in range(8):
        x = p.hkX + i * p.hkP
        r = [e for e in els if e[0] == 'rect' and abs(e[3] - x) < .6 and abs(e[4] - (p.hkY + 4)) < .5]
        t = texts_at(lambda e, x=x: abs(e[3] - (x + p.hkW / 2)) < 1 and e[4] > p.hkY)
        hk.append(t[0][2] if t else '')
        if r and r[0][1].get('fill') == C['blue']: on = i
    dr = [e for e in els if e[0] == 'rect' and abs(e[4] - p.dlgY) < .5 and abs(e[3] - p.left) < .5]
    ask = bool(dr) and dr[0][1].get('fill') == C['ask']
    dt = texts_at(lambda e: p.dlgY < e[4] < p.dlgY + p.dlgH)
    dialog = dt[0][2] if dt else ''
    infotxt = info[0][2] if info else ''
    user, _, rest = infotxt.partition(' · NC 已连接 · ')
    kw = dict(area=area, mode=modes[1][2] if len(modes) > 1 else 'JOG', path=path, path_r=path_r, machine=machine,
              chan=chan, vkeys=vk, hkeys=hk, hk_on=on, dialog=dialog, ask=ask, user=user or '操作者 李工',
              clock='2026-11-02 ' + rest[-5:] if rest else '2026-11-02 10:31',
              modeL=modes[0][2] if modes else None)
    if over: kw.update(over)
    subs = sorted(subs, key=lambda ab: -len(ab[0]))
    def sub(s):
        for a, b in subs: s = s.replace(a, b)
        return s
    for k in ('path', 'path_r', 'dialog'): kw[k] = sub(kw[k])
    kw['chan'] = tuple(sub(t) for t in kw['chan'])
    kw['vkeys'] = [(sub(v[0]), v[1]) if isinstance(v, tuple) else sub(v) for v in kw['vkeys']]
    kw['hkeys'] = [(sub(h[0]), h[1]) if isinstance(h, tuple) else sub(h) for h in kw['hkeys']]
    c, w = shell(p, **kw)
    top = p.pathY + p.pathH + (p.chanH if machine else 0)
    keep = []
    for tag, a, txt, x, y, raw in els:
        if p.left <= x < rx and top <= y < p.dlgY and not (tag == 'rect' and x == 0):
            if tag == 'text' and txt: raw = re.sub(r'>[^<]*</text>$', lambda m: '>' + html.escape(sub(txt), quote=False) + '</text>', raw)
            keep.append(raw)
    # 宽于工作区的大底色矩形（如 x=0 的整屏底）不保留
    c.o.extend(keep)
    return c

V3DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'build', 'v3') + os.sep
SUBS = [('凸度300 v3 · 工作辊-标准 v5', '凸度300 · 工作辊标准（方案 3）'), ('凸度300 v3', '凸度300'), ('工作辊-标准 v5', '工作辊标准'),
        ('标准 v5', '工作辊标准'), ('按同一计划新开作业', '按同一方案新开作业'), ('第 4 道 精磨 · 3 / 6', '4 精磨 · 第 3/6 道'), ('2026-10-01', '2026-11-02'), ('J20261001', 'J20261102')]

def v3svg(sid, p):
    return open(f'{V3DIR}{sid}-{p.name}.svg', encoding='utf-8').read()

def find_win(svg, title):
    """按窗口标题找窗口矩形 (x, y, w, h)。"""
    m = re.search(r'font-weight="500"\s*>' + re.escape(title), svg)
    if not m: raise KeyError(title)
    i = m.start()
    rs = re.findall(r'<rect x="([\d.]+)" y="([\d.]+)" width="([\d.]+)" height="([\d.]+)" fill="#EBF0F6" stroke', svg[:i])
    x, y, w, h = map(float, rs[-1]); return x, y, w, h

def patch(sid, p, subs=(), over=None, drops=(), draw=None):
    """v3 画面 → v4 外壳；drops：要清掉重画的矩形区域；draw(c, p, src) 在其上补画。"""
    src = v3svg(sid, p)
    c = from_v3(src, p, list(SUBS) + list(subs), over)
    body = ''.join(c.o)
    for (x0, y0, x1, y1) in drops:
        body = drop_box(body, x0, y0, x1, y1)
    c.o = [body]
    if draw: draw(c, p, src)
    return finish(c)
