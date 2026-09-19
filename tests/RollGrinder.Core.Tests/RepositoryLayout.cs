using System;
using System.IO;

namespace RollGrinder.Core.Tests;

/// <summary>
/// 从测试运行目录向上找到仓库根（含 RollGrinder.sln 的目录）。
/// 架构测试要读工程文件，需要这个定位。
/// </summary>
internal static class RepositoryLayout
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RollGrinder.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate RollGrinder.sln above '{AppContext.BaseDirectory}'.");
    }
}
