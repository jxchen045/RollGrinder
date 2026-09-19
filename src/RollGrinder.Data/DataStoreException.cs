using System;

namespace RollGrinder.Data;

/// <summary>
/// 存储层异常。界面统一捕获后转为报警条目。
/// </summary>
public class DataStoreException : Exception
{
    public DataStoreException(string message)
        : base(message)
    {
    }

    public DataStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
