using System;
using System.Globalization;
using System.Resources;

namespace RollGrinder.App.Localization;

/// <summary>
/// 基于 .resx 的实现：中性资源为 zh-CN，另有 en-US 卫星资源。
/// </summary>
public sealed class ResxStringLocalizer : IStringLocalizer
{
    private readonly ResourceManager resourceManager;

    public ResxStringLocalizer()
        : this(new ResourceManager("RollGrinder.App.Resources.Strings", typeof(ResxStringLocalizer).Assembly))
    {
    }

    public ResxStringLocalizer(ResourceManager resourceManager)
    {
        this.resourceManager = resourceManager ?? throw new ArgumentNullException(nameof(resourceManager));
    }

    public string this[string key]
    {
        get
        {
            string? value = this.resourceManager.GetString(key, CultureInfo.CurrentUICulture);
            return value ?? "!" + key + "!";
        }
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, this[key], arguments);
}
