# RollGrinder

轧辊磨床上位机（SINUMERIK ONE）。上位机负责工艺编排、辊形编辑、磨削监控、补偿计算与记录；
磨削过程的执行、轨迹与安全联锁由 NC 与 PLC 承担。参数下发后上位机不参与实时控制回路，
进程被强制结束时当前这支辊仍能磨完。

## 解决方案结构

| 项目 | 目标框架 | 允许引用 | 说明 |
|---|---|---|---|
| `src/RollGrinder.Core` | net8.0 | 无 | 领域模型与计算，零第三方依赖 |
| `src/RollGrinder.Contracts` | net8.0 | 无 | 接口与 DTO（`IMachineGateway`、`ITagMap`、`IMachineConfigProvider`、`IAppOptions`） |
| `src/RollGrinder.Nc` | net8.0 | Core、Contracts | NC 程序与参数生成 |
| `src/RollGrinder.Device` | net8.0 | Contracts | `OpcUaGateway` / `StubGateway` / `FileGateway`，类型为 internal |
| `src/RollGrinder.Data` | net8.0 | Contracts、Core | SQLite 存储 |
| `src/RollGrinder.Sim` | net8.0 | Contracts、Core | 磨削过程仿真，类型为 internal |
| `src/RollGrinder.Composition` | net8.0 | Contracts、Core、Device、Sim | 组合根：命令行解析、配置载入、按配置装配网关与领域注册表 |
| `src/RollGrinder.Services` | net8.0 | Contracts、Core、Nc、Data | 应用服务：监视、报警、下发、测量补偿、记录 |
| `src/RollGrinder.App` | net8.0-windows | Contracts、Core、Nc、Data、Composition、Services | WPF 界面与 Generic Host |

App 用不到 Device 与 Sim 的类型：两者的实现类是 `internal`，只对 `RollGrinder.Composition`
可见；`tests/RollGrinder.Integration.Tests` 另有架构测试断言 App 程序集不引用这两个程序集。

## 在 VS2022 里连 GitHub 并拉取更新

仓库只有一个分支 `claude/roll-grinder-hmi-software-rzc6gp`，它同时是默认分支：
克隆下来就是最新代码，不必切分支、也不必合并。

**首次克隆**：VS2022 → `Git` 菜单 → `克隆存储库`，URL 填
`https://github.com/jxchen045/RollGrinder.git`，本地路径自选 → `克隆`。
首次会弹浏览器让你登录 GitHub 账号授权，登录一次以后就记住了。
打开后 VS 会提示缺少组件（`.vsconfig` 声明的），点`安装`即可。

**日常拉取更新**：`Git` 菜单 → `拉取`，或右下角 `Git 更改`窗口的 ↓ 箭头。
拉取前先**停止调试**（程序在跑时输出目录被占用，重新生成会失败）。

**拉取后如果启动报"配置缺少字段"**：说明你本地 `bin\Debug\net8.0-windows\config\`
下的 `machine.json` / `tagmap.json` / `hmi.json` 是旧版本首次启动时生成的，
而新版本给样例加了字段。这几份文件被 `.gitignore` 排除，拉取不会动它们——
这是有意的（现场配置不能被更新覆盖），但开发机上直接删掉即可，
下次启动会从新的 `*.sample.json` 重新生成。报错信息里会点名是哪份文件、缺哪一项。
VS 默认每几分钟自动**抓取**一次，有新提交时分支名旁会出现 `↓N` 角标——
但它只抓取不合并，代码进入工作区仍需你点一次`拉取`。这是有意的：
自动合并会在你本地有改动时打断你。

想让"有更新"更显眼：`工具` → `选项` → `源代码管理` → `Git 全局设置`，
确认`自动提取远程分支` = `True`（默认开）。

**拉取不会动你的现场文件**：`.gitignore` 里排除了 `bin/`、`obj/`、`data/`
以及 `config/machine.json`、`config/tagmap.json`——你本地调过的配置和数据库
不会被更新覆盖。跟着更新走的只有 `config/*.sample.json` 模板。

**你自己改了代码要交上来**：`Git 更改`窗口写提交说明 → `提交全部` → 点 ↑ `推送`。
如果推送被拒（说远程有新提交），先`拉取`再推送。

## 环境要求

| 用途 | 要求 |
|---|---|
| 开发 | Visual Studio 2022 17.8 或更高（要装"**.NET 桌面开发**"工作负载），或任意 .NET 8 及以上的 SDK |
| 运行 | 机床工控机装 **.NET 8 Desktop Runtime**（框架依赖式发布） |

`global.json` 把 SDK 下限钉在 8.0.100、`rollForward` 设为 `latestMajor`，
所以装了 .NET 9 / 10 的 SDK 也能直接编译，不必再单独装 8.0.1xx。
目标框架仍是 `net8.0` / `net8.0-windows`，换 SDK 不改变产物。
若报"找不到 global.json 指定的 SDK 版本"，说明本机连 8.0 以上的 SDK 都没有，装一个即可。

构建开着"警告即错误"，更新的 SDK 默认会做 NuGet 漏洞审计（含传递依赖）：依赖里出现已公开的漏洞，
构建就会失败（NU1901–NU1904）。处理办法是把对应的包升到修补版本，而不是关掉审计。
2026-09 用 .NET 10 SDK 核过一遍：`Microsoft.Data.Sqlite` 8.0.10 带的 `SQLitePCLRaw.lib.e_sqlite3 2.1.6`
有高危漏洞（GHSA-2m69-gcr7-jv3q），已升到 8.0.31（带 2.1.12）；.NET 10 的代码分析规则全部通过。

## 构建与测试

```bash
dotnet build RollGrinder.sln
dotnet test  RollGrinder.sln
```

在非 Windows 机器上也能编译 WPF 工程（`Directory.Build.props` 里开了 `EnableWindowsTargeting`），
但运行需要 Windows。

## 运行

程序目录下的 `config/` 与 `data/` 存放配置与数据库，升级时必须保留；
`config/*.sample.json` 是随程序发布的模板，首次启动时若缺少正式配置会从模板复制一份，
已存在的配置永不覆盖。

```
RollGrinder.App.exe                      # 默认走 OPC UA 网关
RollGrinder.App.exe --stub               # 打桩模式，无需机床
RollGrinder.App.exe --gateway file       # 文件回放网关
RollGrinder.App.exe --config D:\cfg --data D:\data
```

日志滚动写入 `data/logs/rollgrinder-<日期>.log`，保留 31 天。

四种网关都可用：

| 取值 | 用途 |
|---|---|
| `opcua`（默认） | 经 OPC UA 连 SINUMERIK ONE。客户端证书自签在 `data\pki\own`，端点、安全策略与超时来自 `machine.json` 的 `controller` |
| `sim` | 按下发的参数模拟走刀与去除量，无机床联调 |
| `stub` | 只回放写进去的值，跑通界面与流程 |
| `file` | 回放一段 `.jsonl` 录制，离线复盘；`--replay <file>` 直接指定文件 |

录制文件是 JSON Lines，一行一帧、按 `offsetMs` 升序，每帧只写变化的量，未出现的量沿用上一帧
（样例见 `deploy/replay-sample.jsonl`）：

```jsonl
{"offsetMs":0,"values":{"machine.channelState":0,"axis.Z.actualPositionMm":0.0}}
{"offsetMs":1000,"values":{"machine.channelState":2,"machine.programName":"RG01_WR.MPF"}}
```

回放是只读的事实记录，所以下发的写入不回灌进回放流：写入值另存一层 overlay（读得到），
同时追加到同目录的 `writes-<时间>.jsonl` 便于核对。

## 看一遍全部界面（无需机床）

VS2022 里打开解决方案，把 `RollGrinder.App` 设为启动项目，在工具栏的启动下拉里选
**"仿真机床 (推荐)"**，F5。也可以命令行：

```powershell
dotnet run --project src\RollGrinder.App -- --gateway sim
```

首次启动会在输出目录（`src\RollGrinder.App\bin\Debug\net8.0-windows\`）自动建出
`config\`（从模板复制）与 `data\`（数据库 + 日志），然后主窗口打开，五个页签如下：

| 页签 | 立刻能看到 | 要动手才有内容 |
|---|---|---|
| **磨削监控** | 连接状态、通道状态、各轴位置与转速、实测直径、右侧趋势图 | 下发作业后通道转"运行中"，直径开始下降，趋势图走起来 |
| **工艺编排** | 辊件几何输入、辊形类型下拉（四类）、按 schema 自动生成的参数行 | 加工序后出现工序卡片；点"下发参数" |
| **测量与补偿** | 采点按钮、测点表、偏差/补偿图 | 采点 → 归档 → 计算补偿 |
| **磨削记录** | 日期范围与查询按钮 | 下发过作业后才有记录 |
| **报警** | 启动时的连接信息；出错时的故障条目 | 底部报警条同步显示最新一条 |

走一遍完整闭环（约两分钟）：

1. **工艺编排**页：辊件号填 `R-1`，辊身长度 `2000`，公称直径 `650`，辊形选"凸度"，
   把凸度参数改成 `120`（µm 直径量）；工序下拉选"粗磨"点"添加工序"，再加一道"精磨"、
   一道"光磨"。点**下发参数**——状态显示"已下发；磨削由 NC 执行"。
2. **磨削监控**页：通道状态变成"运行中"，程序名出现 `SIM_RG-01.MPF`，
   Z 轴位置在 0–2000 之间往复，实测直径从 650.6 缓慢降向 650，趋势图实时绘制。
3. **测量与补偿**页：作业号填**工艺编排页顶部那个 `J…` 号**，点几次"采集测点"
   （每次取当前拖板位置 + 测径读数），点"归档测量"，再点"计算补偿"——
   偏差、峰谷值与合格判定出来，图上画出偏差与补偿两条曲线。
   回到工艺编排页再点一次"下发参数"，补偿就叠加进下发的辊形里了。
4. **磨削记录**页：点"查询"，看到刚才那条记录（辊号、辊形、状态、最大偏差）；
   选中后填备注点"标记完成"，或点"导出 CSV"。
5. **报警**页：把"工艺编排"页的单刀切深改成 `200`（µm）再下发，会看到校验拦下、
   逐条列出超限原因，且**一个字节都没有写到机床**。

其他模式：启动下拉里的"打桩"只回放写进去的值（画面不动，适合看布局）；
"回放录制文件"放的是 `deploy\replay-sample.jsonl` 那段 5 秒录制；
"OPC UA (连真机床)" 走 `machine.json` 里的端点，连不上时界面照样起来，只在报警条上报错。

## 配置

程序目录下的 `config/` 有三份配置，各有对应的 `*.sample.json` 模板：

| 文件 | 内容 |
|---|---|
| `machine.json` | 本台机床：轴（有无/行程/闭环/进给与转速上限）、测量通道、选件、阈值、辊件界限、工序类型对应的 NC 代码 |
| `tagmap.json` | 逻辑变量名 → 物理地址（OPC UA NodeId）、类型、读写权限、单位、缩放、数组长度与下标偏置（地址里用 `{index}` 占位，渲染成 `indexOffset + 下标`） |
| `hmi.json` | 上位机自身：轮询周期、界面刷新频率（5–10 Hz）、辊形采样点数、补偿增益与平滑、保留天数、公差 |

代码里不出现物理地址、轴名、行程与阈值；换一台机床只改这三份配置。

## 部署与升级

```powershell
deploy\publish.ps1
deploy\upgrade.ps1 -SourceDirectory artifacts\publish -TargetDirectory D:\RollGrinder
```

升级只替换程序文件与 `config\*.sample.json`；现场的 `config\*.json` 与 `data\`（数据库、日志）
原样保留，升级前还会自动备份一份配置。工控机需要 .NET 8 Desktop Runtime
（框架依赖式发布，约 23 MB）。

## 数据库

`data/rollgrinder.db`，结构版本记在 `PRAGMA user_version`，启动时自动迁移；
数据库版本比程序新时拒绝启动，不拿旧代码去动新结构。
辊形与工序参数按键值存，新增一类不改表。
