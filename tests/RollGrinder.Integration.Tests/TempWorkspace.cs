using System;
using System.IO;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 一次性的临时程序目录，用来验证首次启动行为而不污染仓库。
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "rollgrinder-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>把仓库里的 config/*.sample.json 拷进来，模拟随程序发布的样例。</summary>
    public string CreateSampleDirectory()
    {
        string samples = Path.Combine(Root, "program-config");
        Directory.CreateDirectory(samples);
        foreach (string file in Directory.EnumerateFiles(RepositoryLayout.ConfigSampleDirectory, "*.sample.json"))
        {
            File.Copy(file, Path.Combine(samples, Path.GetFileName(file)));
        }

        return samples;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言结果。
        }
    }
}
