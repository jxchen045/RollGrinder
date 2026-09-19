using System;
using System.IO;

namespace RollGrinder.Integration.Tests;

internal static class RepositoryLayout
{
    public static string Root { get; } = FindRoot();

    public static string ConfigSampleDirectory => Path.Combine(Root, "config");

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
