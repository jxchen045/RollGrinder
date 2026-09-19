using System;
using System.Globalization;
using RollGrinder.App.Localization;
using RollGrinder.Services.Alarms;

namespace RollGrinder.App.ViewModels;

/// <summary>报警列表里的一行。文案按资源键取，细节保持原样不翻译。</summary>
public sealed class AlarmRowViewModel
{
    private readonly AlarmEntry entry;

    public AlarmRowViewModel(AlarmEntry entry, IStringLocalizer localizer)
    {
        this.entry = entry ?? throw new ArgumentNullException(nameof(entry));
        ArgumentNullException.ThrowIfNull(localizer);

        Message = localizer[entry.MessageResourceKey];
        SeverityText = localizer["Severity_" + entry.Severity];
    }

    public long Id => this.entry.Id;

    public string TimeText => this.entry.RaisedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    public string SeverityText { get; }

    public AlarmSeverity Severity => this.entry.Severity;

    public string Message { get; }

    public string Detail => this.entry.Detail ?? string.Empty;
}
