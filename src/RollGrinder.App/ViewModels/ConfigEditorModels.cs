using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;

namespace RollGrinder.App.ViewModels;

/// <summary>配置表单里一格的取值种类。</summary>
public enum ConfigFieldKind
{
    Text = 0,
    Number = 1,
    Flag = 2,
    Choice = 3,
}

/// <summary>
/// 机床配置表单里的一格（修改稿 5.8）：对着 JSON 树里的一个叶子，改了立刻写回树里再整体校验。
/// 名字取资源 "CfgField_" + 字段名，没有就直接写字段名（字段名本来就是现场文件里的那个词）。
/// </summary>
public sealed partial class ConfigFieldViewModel : ObservableObject
{
    private readonly JsonObject owner;
    private readonly string property;
    private readonly Action changed;
    private readonly bool loading;

    public ConfigFieldViewModel(
        string path,
        JsonObject owner,
        string property,
        ConfigFieldKind kind,
        string label,
        string unitText,
        IReadOnlyList<string> choices,
        bool isReadOnly,
        Action changed,
        Func<string, string>? choiceLabel = null)
    {
        Path = path;
        this.owner = owner;
        this.property = property;
        Kind = kind;
        Label = label;
        UnitText = unitText;
        IsReadOnly = isReadOnly;
        this.changed = changed;

        this.loading = true;
        JsonNode? node = owner[property];
        if (kind == ConfigFieldKind.Flag)
        {
            IsChecked = node is JsonValue flag && flag.TryGetValue(out bool value) && value;
        }
        else if (kind == ConfigFieldKind.Number)
        {
            Text = node is JsonValue number && number.TryGetValue(out double value)
                ? value.ToString("R", CultureInfo.CurrentCulture)
                : string.Empty;
        }
        else
        {
            Text = node is JsonValue text && text.TryGetValue(out string? value) ? value : string.Empty;
        }

        foreach (string choice in choices)
        {
            string chosen = choice;
            // 存进文件的是原值（SemiClosed），键上写本地化的名字（半闭环）。
            Choices.Add(new ParameterChoiceViewModel(choice, choiceLabel?.Invoke(choice) ?? choice, new RelayCommand(() => Text = chosen))
            {
                IsSelected = string.Equals(choice, Text, StringComparison.OrdinalIgnoreCase),
            });
        }

        this.loading = false;
    }

    /// <summary>在文件里的路径，例如 axes[0].maxPositionMm。校验问题按它对到格子上。</summary>
    public string Path { get; }

    public ConfigFieldKind Kind { get; }

    public string Label { get; }

    public string UnitText { get; }

    public bool IsReadOnly { get; }

    public bool IsFlag => Kind == ConfigFieldKind.Flag;

    public bool IsChoice => Kind == ConfigFieldKind.Choice;

    public bool IsTextual => Kind is ConfigFieldKind.Text or ConfigFieldKind.Number;

    public ObservableCollection<ParameterChoiceViewModel> Choices { get; } = new();

    [ObservableProperty]
    private string text = string.Empty;

    [ObservableProperty]
    private bool isChecked;

    [ObservableProperty]
    private string issueText = string.Empty;

    public bool HasIssue => IssueText.Length > 0;

    partial void OnIssueTextChanged(string value) => OnPropertyChanged(nameof(HasIssue));

    /// <summary>这一格打的字是不是数（数字格才查）。不是数就不写回，树里还是上一个成立的值。</summary>
    public bool IsParseable { get; private set; } = true;

    partial void OnTextChanged(string value)
    {
        foreach (ParameterChoiceViewModel choice in Choices)
        {
            choice.IsSelected = string.Equals(choice.Key, value, StringComparison.OrdinalIgnoreCase);
        }

        if (this.loading)
        {
            return;
        }

        if (Kind == ConfigFieldKind.Number)
        {
            IsParseable = TryParse(value, out double number);
            if (IsParseable)
            {
                // 原来是整数的格（通道号、超时、工序代码）写回整数，免得文件里冒出 "1.0"。
                bool integral = this.owner[this.property] is JsonValue old && old.ToJsonString().IndexOfAny(new[] { '.', 'e', 'E' }) < 0;
                this.owner[this.property] = integral && number == Math.Floor(number) && Math.Abs(number) < long.MaxValue
                    ? JsonValue.Create((long)number)
                    : JsonValue.Create(number);
            }
        }
        else
        {
            this.owner[this.property] = JsonValue.Create(value);
        }

        this.changed();
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (this.loading || Kind != ConfigFieldKind.Flag)
        {
            return;
        }

        this.owner[this.property] = JsonValue.Create(value);
        this.changed();
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

/// <summary>机床配置表单里的一组（标识、控制器、每根轴、选装、阈值、工件范围……）。</summary>
public sealed class ConfigGroupViewModel
{
    public ConfigGroupViewModel(string title, IReadOnlyList<ConfigFieldViewModel> fields)
    {
        Title = title;
        Fields = fields;
    }

    public string Title { get; }

    public IReadOnlyList<ConfigFieldViewModel> Fields { get; }
}

/// <summary>
/// 标签映射表里的一行：逻辑名、地址、数据类型、读写方向、单位、说明，外加当前值与质量（修改稿 5.8）。
/// 还没登记的必需变量也列成一行（<see cref="IsMissing"/>），按"登记"才加进文件。
/// </summary>
public sealed partial class TagRowViewModel : ObservableObject
{
    private readonly Action changed;

    public TagRowViewModel(string key, JsonObject? node, bool isRequired, Action changed)
    {
        Key = key;
        Node = node;
        IsRequired = isRequired;
        this.changed = changed;
        this.address = Read("address");
        this.dataType = Read("dataType");
        this.access = Read("access");
        this.unit = Read("unit");
        this.description = Read("description");
    }

    public string Key { get; }

    /// <summary>文件里的那一项；还没登记为 null。</summary>
    public JsonObject? Node { get; private set; }

    public bool IsMissing => Node is null;

    /// <summary>下发或监视离不了它。</summary>
    public bool IsRequired { get; }

    [ObservableProperty]
    private string address;

    [ObservableProperty]
    private string dataType;

    [ObservableProperty]
    private string access;

    [ObservableProperty]
    private string unit;

    [ObservableProperty]
    private string description;

    [ObservableProperty]
    private string valueText = "--";

    [ObservableProperty]
    private string qualityText = string.Empty;

    [ObservableProperty]
    private string issueText = string.Empty;

    /// <summary>登记：把这一行加进文件（空地址，校验会要求填上）。</summary>
    public JsonObject Register(string defaultDataType, string defaultAccess)
    {
        Node = new JsonObject
        {
            ["key"] = Key,
            ["address"] = string.Empty,
            ["dataType"] = defaultDataType,
            ["access"] = defaultAccess,
        };
        DataType = defaultDataType;
        Access = defaultAccess;
        OnPropertyChanged(nameof(IsMissing));
        return Node;
    }

    partial void OnAddressChanged(string value) => Write("address", value);

    partial void OnDataTypeChanged(string value) => Write("dataType", value);

    partial void OnAccessChanged(string value) => Write("access", value);

    partial void OnUnitChanged(string value) => Write("unit", value);

    partial void OnDescriptionChanged(string value) => Write("description", value);

    private string Read(string property) =>
        Node?[property] is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;

    private void Write(string property, string value)
    {
        if (Node is null || string.Equals(Read(property), value, StringComparison.Ordinal))
        {
            return;
        }

        if (value.Length == 0 && property is "unit" or "description")
        {
            Node.Remove(property);
        }
        else
        {
            Node[property] = value;
        }

        this.changed();
    }
}
