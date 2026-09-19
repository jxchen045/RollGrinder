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

`--gateway sim` 会按下发的参数模拟走刀与去除量，适合无机床联调；`--stub` 只回放写入的值。
`OpcUaGateway` 与 `FileGateway` 目前仍是空桩，调用即抛 `GatewayException`——接 OPC UA
客户端库需要引入第三方包，待确认后再做。

## 配置

程序目录下的 `config/` 有三份配置，各有对应的 `*.sample.json` 模板：

| 文件 | 内容 |
|---|---|
| `machine.json` | 本台机床：轴（有无/行程/闭环/进给与转速上限）、测量通道、选件、阈值、辊件界限、工序类型对应的 NC 代码 |
| `tagmap.json` | 逻辑变量名 → 物理地址、类型、读写权限、单位、数组长度（地址里用 `{index}` 占位） |
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
