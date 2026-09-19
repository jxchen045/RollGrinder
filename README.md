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
| `src/RollGrinder.Composition` | net8.0 | Contracts、Device、Sim | 组合根：命令行解析、配置载入、按配置装配网关 |
| `src/RollGrinder.App` | net8.0-windows | Contracts、Core、Nc、Data、Composition | WPF 界面与 Generic Host |

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

T-01 只搭骨架：`OpcUaGateway` 与 `FileGateway` 是空桩，调用即抛 `GatewayException`，
现阶段请用 `--stub` 启动。
