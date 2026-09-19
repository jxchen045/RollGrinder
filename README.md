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
