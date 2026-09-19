using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;

namespace RollGrinder.Composition;

/// <summary>
/// 首次启动引导：配置目录缺少正式配置时，从同目录的 *.sample.json 复制一份。
/// 已存在的配置绝不覆盖——升级时必须保留现场配置。
/// </summary>
public static class ConfigBootstrapper
{
    public const string SampleSuffix = ".sample.json";

    /// <summary>
    /// 确保 config/ 与 data/ 存在，并按需从样例生成正式配置。
    /// </summary>
    /// <param name="options">运行选项。</param>
    /// <param name="sampleDirectory">样例目录，默认与配置目录相同（随程序发布）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>本次实际生成的文件路径。</returns>
    public static async Task<IReadOnlyList<string>> EnsureConfigurationAsync(
        IAppOptions options,
        string? sampleDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(options.ConfigDirectory);
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.LogDirectory);

        string samples = sampleDirectory ?? options.ConfigDirectory;
        var created = new List<string>();
        if (!Directory.Exists(samples))
        {
            return created;
        }

        foreach (string samplePath in Directory.EnumerateFiles(samples, "*" + SampleSuffix))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(samplePath);
            string targetName = fileName[..^SampleSuffix.Length] + ".json";
            string targetPath = Path.Combine(options.ConfigDirectory, targetName);
            if (File.Exists(targetPath))
            {
                continue;
            }

            await CopyAsync(samplePath, targetPath, cancellationToken).ConfigureAwait(false);
            created.Add(targetPath);
        }

        return created;
    }

    private static async Task CopyAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        await using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        await using FileStream target = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }
}
