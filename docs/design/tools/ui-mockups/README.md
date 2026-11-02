# 界面样机生成器（界面设计稿 v4）

生成 `docs/design/界面设计稿v4（磨削方案模型）.html`：全套画面按 1920×1080（标准档）与 1366×768（紧凑档）真实像素画成 SVG，外壳、令牌与《界面修订稿（以轧辊为中心）》一致。

```
python build.py              # 全部重画并拼装文档（只需 Python 3，无第三方依赖）
python build.py 5.2.2 5.3.1  # 只重画这几个画面
node render_png.js 5.2.2     # 可选：渲染 PNG 逐张检查（Playwright + Chromium）
```

| 文件 | 内容 |
|---|---|
| `mk.py` | 外壳（左栏、标题行、路径条、通道行、竖键、对话行、横键）、令牌、组件（窗口、表格、徽标、字段、提示条、曲线图），以及"v4 外壳 + v3 工作区"的拼装（`from_v3` / `patch`） |
| `screens.py` | 每个画面一个函数，`@screen(编号, 标题, 新/改/不变, 说明)` 登记；示例数据集中在文件开头与各画面内 |
| `build_doc.py` | 拼装文档：`inputs/doc_body.html` 是正文模板，`inputs/` 里的评审意见与裁决填进表格 |
| `inputs/` | 第 1 轮四个角色的意见（`review_r1_*.md`）、冲突裁决（`decisions_r1.md`）、第 2 轮意见与处理（`review_r2_*`） |
| `build/` | 生成物（不入库）：`v3/` 从 v3 文档拆出的样机，`out/` v4 画面 SVG / PNG |

改画面的一般做法：改 `screens.py` 里对应函数 → `python build.py 编号` → `node render_png.js 编号` 看图 → 提交生成的文档。
沿用 v3 的画面（标"不变"）由 `copy_screen` 登记，文字差异用替换表（`mk.SUBS` 与各自的 `subs`）处理。
