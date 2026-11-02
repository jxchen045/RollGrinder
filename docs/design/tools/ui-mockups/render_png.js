// 可选：把 build/out/*.svg 渲染成 PNG 逐张检查（需要 Playwright + Chromium）。
// 用法：node render_png.js [画面编号 …]
const {chromium} = require('playwright'); const fs = require('fs'); const path = require('path');
(async () => {
  const dir = path.join(__dirname, 'build', 'out'); const ids = process.argv.slice(2);
  const b = await chromium.launch(); const p = await b.newPage();
  for (const f of fs.readdirSync(dir).filter(x => x.endsWith('.svg') && (!ids.length || ids.some(i => x.startsWith(i + '-'))))) {
    const svg = fs.readFileSync(path.join(dir, f), 'utf8').replace(/style="width:[^"]*"/, 'style="width:100%"');
    await p.setViewportSize({width: f.includes('-cmp') ? 1366 : 1920, height: 1080});
    await p.setContent(`<html><body style="margin:0"><style>svg text{font-family:'Noto Sans SC','Noto Sans CJK SC','Microsoft YaHei',sans-serif}svg text.m{font-family:'IBM Plex Mono',Consolas,'DejaVu Sans Mono',monospace}</style>${svg}</body></html>`);
    await (await p.$('svg')).screenshot({path: path.join(dir, f.replace('.svg', '.png'))});
  }
  await b.close();
})();
