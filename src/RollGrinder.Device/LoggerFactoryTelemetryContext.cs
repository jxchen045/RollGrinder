using Microsoft.Extensions.Logging;
using Opc.Ua;

namespace RollGrinder.Device;

/// <summary>
/// 把 OPC UA 客户端栈的诊断日志接到宿主的日志工厂上（现场即 Serilog 的滚动文件），
/// 免得调试信息只能靠附加调试器才看得到。
/// </summary>
internal sealed class LoggerFactoryTelemetryContext : TelemetryContextBase
{
    public LoggerFactoryTelemetryContext(ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
    }
}
