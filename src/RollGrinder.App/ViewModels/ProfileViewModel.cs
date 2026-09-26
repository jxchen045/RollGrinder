using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Data;
using RollGrinder.Core.Units;
using RollGrinder.Services.Alarms;

namespace RollGrinder.App.ViewModels;

/// <summary>曲线段列表里的一行。</summary>
public sealed partial class SegmentRowViewModel : ObservableObject
{
    public SegmentRowViewModel(RollProfileSegment segment, string displayName, string purposeText)
    {
        Segment = segment;
        DisplayName = displayName;
        PurposeText = purposeText;
    }

    public RollProfileSegment Segment { get; }

    public int Order => Segment.Order;

    public string OrderText => Segment.Order.ToString(CultureInfo.InvariantCulture);

    /// <summary>曲线类型名，例如 Polynom / Taper。</summary>
    public string DisplayName { get; }

    /// <summary>区间与用途，例如 "0 – 150 mm · 头架端锥"。</summary>
    public string PurposeText { get; }

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>
/// 辊形编辑。版面见 docs/design/B-Profile-辊形编辑.html：
/// 左边是可叠加的曲线段，中上是预览，中下是该段参数（由 Schema 自动生成，新增类型不改界面）。
/// </summary>
public sealed partial class ProfileViewModel : PageViewModelBase
{
    /// <summary>中高轴（补偿执行轴）在 machine.json 里的 role。</summary>
    public const string CrownAxisRole = "RollProfile";

    private readonly RollProfileTypeRegistry profileTypes;
    private readonly MachineDescription machine;
    private readonly HmiSettings settings;

    private readonly RollGeometry geometry;

    private readonly IRollProfileRepository library;

    private CompositeRollProfile composite;

    /// <summary>编辑参数行时不要反过来又触发一次回写，否则 Rebuild 里的重建会递归。</summary>
    private bool suppressWriteBack;

    /// <summary>上一次"干净"的辊形，供"放弃修改"回退。</summary>
    private CompositeRollProfile committedComposite;

    public ProfileViewModel(
        RollProfileTypeRegistry profileTypes,
        MachineDescription machine,
        HmiSettings settings,
        IRollProfileRepository library,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
    {
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        NamePrompt = new NamePromptViewModel(localizer);

        this.geometry = RollGeometry.FromDiameter(
            machine.Workpiece.MinBodyLengthMm, machine.Workpiece.MinDiameterMm);
        this.composite = CompositeRollProfile.Single(
            ProfileTypeKeys.Cylindrical, this.geometry, ParameterSet.Empty);
        this.committedComposite = this.composite;

        AvailableTypes = new ObservableCollection<string>(profileTypes.All.Select(type => type.Key));
        this.selectedTypeForInsert = AvailableTypes.FirstOrDefault() ?? ProfileTypeKeys.Cylindrical;

        SetFunctionKeys(new[]
        {
            new FunctionKeyViewModel("Fn_Save", new AsyncRelayCommand(
                () => SaveAsync(CancellationToken.None)), localizer, FunctionKeyKind.Primary, requiresEditable: true),
            new FunctionKeyViewModel("Fn_SaveAs", SaveAsCommand, localizer, requiresEditable: true),
            new FunctionKeyViewModel("Fn_NewSegment", InsertSegmentCommand, localizer, requiresEditable: true),
            // 两个键都要选文件，对话框在视图里；这里只负责触发与收结果。
            new FunctionKeyViewModel("Fn_ImportPoints", RequestImportPointsCommand, localizer),
            new FunctionKeyViewModel("Fn_GeneratePoints", RequestGeneratePointsCommand, localizer),
            new FunctionKeyViewModel("Fn_Validate", ValidateCommand, localizer),
            new FunctionKeyViewModel("Fn_ProfileLibrary", OpenLibraryCommand, localizer),
        });

        Rebuild();
    }

    public override PageKey Key => PageKey.Profile;

    public override string TitleResourceKey => "Page_Profile";

    public override string MenuHintResourceKey => "Menu_ProfileHint";

    /// <summary>编辑页：自动循环挂着程序时落只读锁，免得改了辊形以为机床会跟着变。</summary>
    public override bool LocksDuringRun => true;

    /// <summary>离线可用：只和数据库与配置打交道，不碰机床。</summary>
    public override bool WorksOffline => true;

    public ObservableCollection<SegmentRowViewModel> Segments { get; } = new();

    public ObservableCollection<ParameterRowViewModel> SegmentParameters { get; } = new();

    public ObservableCollection<string> AvailableTypes { get; }

    /// <summary>合成辊形（辊身坐标 mm，直径量 mm）。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMm)> ComposedPoints { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>仅主辊形（第 1 段），作为对照线。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMm)> MainPoints { get; private set; } =
        Array.Empty<(double, double)>();

    public event EventHandler? PreviewChanged;

    /// <summary>
    /// 导进来的对照点列（辊身坐标 mm，直径量 mm）。
    ///
    /// 只作**对照**，不改这条辊形：上一版辊形、现场量出来的曲线、别人给的
    /// 一张表，拿进来跟当前设计叠着看。要改还是去改曲线段的参数——
    /// 点列一旦能直接变成辊形，这条辊形就再也说不清是按什么算出来的。
    /// </summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMm)> ReferencePoints { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>界面要导入点列时触发；路径由视图选。</summary>
    public event EventHandler? ImportPointsRequested;

    /// <summary>界面要导出点列时触发；路径由视图选。</summary>
    public event EventHandler? GeneratePointsRequested;

    /// <summary>对照点列与当前设计差得最多的地方（直径量 µm）；没导入时为空。</summary>
    [ObservableProperty]
    private string referenceDeviationText = string.Empty;

    /// <summary>最近一个动作的结果提示。</summary>
    [ObservableProperty]
    private string statusResourceKey = string.Empty;

    /// <summary>提示文字。资源键为空时不显示。</summary>
    public string StatusText => string.IsNullOrEmpty(StatusResourceKey) ? string.Empty : Localizer[StatusResourceKey];

    partial void OnStatusResourceKeyChanged(string value) => OnPropertyChanged(nameof(StatusText));

    [RelayCommand]
    private void RequestImportPoints() => ImportPointsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RequestGeneratePoints() => GeneratePointsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 按当前合成曲线生成点列并写成 CSV。
    ///
    /// 点数就是下发用的采样点数——生成出来的这一份和真正下发给 NC 的
    /// 是同一条线，不是另算一遍。
    /// </summary>
    public Task GeneratePointsAsync(string filePath, CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            var text = new StringBuilder();
            text.AppendLine("bodyPositionMm,diameterOffsetMicrometer");
            foreach ((double positionMm, double diameterMm) in ComposedPoints)
            {
                text.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{positionMm:F3},{UnitConversion.MmToMicrometer(diameterMm):F2}"));
            }

            await File.WriteAllTextAsync(filePath, text.ToString(), new UTF8Encoding(true), token)
                .ConfigureAwait(true);

            StatusResourceKey = "Profile_PointsGenerated";
        }, cancellationToken);

    /// <summary>读一份 CSV 点列作为对照线。</summary>
    public Task ImportPointsAsync(string filePath, CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            var points = new List<(double, double)>();
            foreach (string line in await File.ReadAllLinesAsync(filePath, token).ConfigureAwait(true))
            {
                if (TryParsePoint(line, out (double, double) point))
                {
                    points.Add(point);
                }
            }

            if (points.Count < 2)
            {
                // 一两个点连不成一条线；读错了文件该说出来，而不是画半条。
                StatusResourceKey = "Profile_PointsNotUsable";
                return;
            }

            points.Sort((left, right) => left.Item1.CompareTo(right.Item1));
            ReferencePoints = points;
            RefreshReferenceDeviation();
            StatusResourceKey = "Profile_PointsImported";
            PreviewChanged?.Invoke(this, EventArgs.Empty);
        }, cancellationToken);

    /// <summary>
    /// 一行 CSV → 一个点。表头与空行跳过；解析不了的行也跳过——
    /// 现场的表常常带着注释与单位行，为这个整份不读太不划算。
    /// </summary>
    private static bool TryParsePoint(string line, out (double BodyPositionMm, double DiameterMm) point)
    {
        point = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string[] parts = line.Split(',', ';', '\t');
        if (parts.Length < 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double positionMm)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double micrometer))
        {
            return false;
        }

        point = (positionMm, UnitConversion.MicrometerToMm(micrometer));
        return true;
    }

    /// <summary>对照线与当前设计差得最多的地方。看的就是这个数。</summary>
    private void RefreshReferenceDeviation()
    {
        if (ReferencePoints.Count == 0 || ComposedPoints.Count < 2)
        {
            ReferenceDeviationText = string.Empty;
            return;
        }

        double worst = 0.0;
        foreach ((double positionMm, double diameterMm) in ReferencePoints)
        {
            worst = Math.Max(worst, Math.Abs(diameterMm - InterpolateComposed(positionMm)));
        }

        ReferenceDeviationText = Localizer.Format(
            "Profile_ReferenceDeviationFormat", UnitConversion.MmToMicrometer(worst));
    }

    /// <summary>当前设计在某个辊身位置上的值（直径量 mm），线性插值。</summary>
    private double InterpolateComposed(double bodyPositionMm)
    {
        IReadOnlyList<(double Position, double Diameter)> points = ComposedPoints;
        if (bodyPositionMm <= points[0].Position)
        {
            return points[0].Diameter;
        }

        for (int i = 1; i < points.Count; i++)
        {
            if (bodyPositionMm > points[i].Position)
            {
                continue;
            }

            double span = points[i].Position - points[i - 1].Position;
            double t = span <= 0.0 ? 0.0 : (bodyPositionMm - points[i - 1].Position) / span;
            return points[i - 1].Diameter + (t * (points[i].Diameter - points[i - 1].Diameter));
        }

        return points[^1].Diameter;
    }

    [ObservableProperty]
    private SegmentRowViewModel? selectedSegment;

    [ObservableProperty]
    private string selectedTypeForInsert;

    [ObservableProperty]
    private string segmentParametersTitle = string.Empty;

    [ObservableProperty]
    private string maxChordErrorText = "--";

    [ObservableProperty]
    private string segmentCountText = "--";

    [ObservableProperty]
    private string taperAlignmentText = "--";

    [ObservableProperty]
    private string compensationAxisText = "--";

    partial void OnSelectedSegmentChanged(SegmentRowViewModel? value)
    {
        foreach (SegmentRowViewModel row in Segments)
        {
            row.IsSelected = ReferenceEquals(row, value);
        }

        this.suppressWriteBack = true;
        try
        {
            SegmentFromMmText = value is null
                ? string.Empty
                : value.Segment.FromMm.ToString("F1", CultureInfo.CurrentCulture);
            SegmentToMmText = value is null
                ? string.Empty
                : value.Segment.ToMm.ToString("F1", CultureInfo.CurrentCulture);
            SegmentIsMirrored = value?.Segment.IsMirrored ?? false;
        }
        finally
        {
            this.suppressWriteBack = false;
        }

        ShowSegmentParameters(value);
    }

    /// <summary>选中段的区间起点（辊身坐标 mm）。</summary>
    [ObservableProperty]
    private string segmentFromMmText = string.Empty;

    /// <summary>选中段的区间终点（辊身坐标 mm）。</summary>
    [ObservableProperty]
    private string segmentToMmText = string.Empty;

    /// <summary>选中段是否沿区间中点镜像——头架端那一段倒角就是尾架端那一段的镜像。</summary>
    [ObservableProperty]
    private bool segmentIsMirrored;

    partial void OnSegmentFromMmTextChanged(string value) => WriteBackSelectedSegment();

    partial void OnSegmentToMmTextChanged(string value) => WriteBackSelectedSegment();

    partial void OnSegmentIsMirroredChanged(bool value) => WriteBackSelectedSegment();

    /// <summary>
    /// 把右侧参数格与区间框里的内容写回选中的那一段。
    ///
    /// 在这之前参数格是**只读的假象**：改了凸度，预览、校验、下发全都当没看见。
    /// </summary>
    private void WriteBackSelectedSegment()
    {
        if (this.suppressWriteBack || SelectedSegment is null)
        {
            return;
        }

        RollProfileSegment current = SelectedSegment.Segment;

        if (!TryParseDouble(SegmentFromMmText, out double fromMm)
            || !TryParseDouble(SegmentToMmText, out double toMm))
        {
            // 正在输入的中间态（空串、只打了一个减号）不报错也不回写，等它打完。
            return;
        }

        var pairs = new List<KeyValuePair<string, ParameterValue>>(SegmentParameters.Count);
        foreach (ParameterRowViewModel row in SegmentParameters)
        {
            ParameterValue? value = row.ToParameterValue();
            if (value is null)
            {
                // 这一格现在填的东西还不成立，等它填对了再回写。
                return;
            }

            pairs.Add(new KeyValuePair<string, ParameterValue>(row.Key, value));
        }

        RollProfileSegment updated;
        try
        {
            updated = RollProfileSegment.Create(
                current.Order,
                current.ProfileTypeKey,
                fromMm,
                toMm,
                new ParameterSet(pairs),
                SegmentIsMirrored);
        }
        catch (DomainException)
        {
            // 区间还没填成立（终点小于起点之类），先不回写；校验会在保存时再报一次。
            return;
        }

        this.composite = this.composite.Replace(current.Order, updated);
        MarkDirty();

        int keepOrder = current.Order;
        Rebuild();
        SelectedSegment = Segments.FirstOrDefault(row => row.Order == keepOrder) ?? Segments.FirstOrDefault();
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    [RelayCommand]
    private void InsertSegment()
    {
        // 新段默认落在辊身后四分之一，现场再按需要改区间。
        double fromMm = this.geometry.BodyLengthMm * 0.75;
        IRollProfileType profileType = this.profileTypes.Get(SelectedTypeForInsert);

        this.composite = this.composite.Add(RollProfileSegment.Create(
            this.composite.Segments.Count + 1,
            profileType.Key,
            fromMm,
            this.geometry.BodyLengthMm,
            profileType.Schema.CreateDefaults()));

        MarkDirty();
        Rebuild();
    }

    [RelayCommand]
    private void RemoveSegment()
    {
        if (SelectedSegment is null || this.composite.Segments.Count <= 1)
        {
            return;
        }

        this.composite = this.composite.RemoveAt(SelectedSegment.Order);
        MarkDirty();
        Rebuild();
    }

    [RelayCommand]
    private void MoveSegmentUp()
    {
        if (SelectedSegment is null)
        {
            return;
        }

        this.composite = this.composite.MoveUp(SelectedSegment.Order);
        MarkDirty();
        Rebuild();
    }

    [RelayCommand]
    private void Validate() => Rebuild();

    // ── 辊形库 ────────────────────────────────────────────────────────────────
    //
    // 辊形是可复用的模板：同一条 CVC 辊形会被几十支辊用到，所以它有自己的库。
    // 作业引用它的时候复制一份快照，库里之后改了也不动已经磨过的那支辊的记录。

    /// <summary>当前辊形的名字，库里按这个名字找。</summary>
    [ObservableProperty]
    private string profileName = string.Empty;

    /// <summary>当前辊形在库里的标识；还没存过就是 null（按"保存"时现生成一个）。</summary>
    [ObservableProperty]
    private string? profileId;

    /// <summary>库里现有的辊形，"打开"面板上列的就是这些。</summary>
    public ObservableCollection<RollProfileSummary> LibraryEntries { get; } = new();

    /// <summary>"打开"面板开着没有。</summary>
    [ObservableProperty]
    private bool isLibraryOpen;

    [ObservableProperty]
    private RollProfileSummary? selectedLibraryEntry;

    partial void OnProfileNameChanged(string value)
    {
        MarkDirty();
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>有名字才谈得上保存——没名字存进库里就找不回来了。</summary>
    public override bool CanSave => !string.IsNullOrWhiteSpace(ProfileName);

    /// <summary>
    /// 存回当前这条辊形。走的是页面基类的保存契约，
    /// 所以"改了没存就想离开"那道拦截也会用到它。
    /// </summary>
    public override Task<bool> SaveAsync(CancellationToken cancellationToken) =>
        StoreAsync(ProfileId ?? NewProfileId(), ProfileName, cancellationToken);

    /// <summary>"另存为"的命名框。</summary>
    public NamePromptViewModel NamePrompt { get; }

    /// <summary>
    /// 另存一条新辊形，库里原来那条不动。先起名字：预填"原名-副本"，
    /// 名字在库里已经有了就留在框里说清楚，不存。
    /// </summary>
    [RelayCommand]
    private void SaveAs() => NamePrompt.Open(
        Localizer["Profile_SaveAsTitle"],
        string.IsNullOrWhiteSpace(ProfileName) ? string.Empty : Localizer.Format("Library_CopyNameFormat", ProfileName.Trim()),
        async (name, token) =>
        {
            if (await this.library.IsNameTakenAsync(name, null, token).ConfigureAwait(true))
            {
                return Localizer.Format("Library_NameTakenFormat", name);
            }

            return await StoreAsync(NewProfileId(), name, token).ConfigureAwait(true)
                ? null
                : Localizer["Library_SaveFailed"];
        });

    private async Task<bool> StoreAsync(string profileId, string name, CancellationToken cancellationToken)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            // 没名字存进去就找不回来了，宁可不存。
            Alarms.Raise(AlarmSeverity.Warning, "Profile_NeedsName", code: AlarmCodes.DomainFailure);
            return false;
        }

        try
        {
            // 库里名字唯一：同名的两条分不清哪条是哪条（第一轮甲方测试）。
            if (await this.library.IsNameTakenAsync(name, profileId, cancellationToken).ConfigureAwait(true))
            {
                Alarms.Raise(AlarmSeverity.Warning, "Library_NameTaken", name, AlarmCodes.DomainFailure);
                return false;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            RollProfileDefinition? existing = await this.library.GetAsync(profileId, cancellationToken).ConfigureAwait(true);

            await this.library.SaveAsync(
                new RollProfileDefinition(
                    profileId,
                    name,
                    this.geometry.BodyLengthMm,
                    this.composite,
                    existing?.CreatedAtUtc ?? now,
                    now),
                cancellationToken).ConfigureAwait(true);

            ProfileId = profileId;
            ProfileName = name;
            this.committedComposite = this.composite;
            IsDirty = false;
            Alarms.Raise(AlarmSeverity.Information, "Profile_Saved", ProfileName, AlarmCodes.HandoverCompleted);
            return true;
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
            return false;
        }
    }

    /// <summary>打开辊形库面板并刷新列表。</summary>
    [RelayCommand]
    private async Task OpenLibraryAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<RollProfileSummary> entries =
                await this.library.ListAsync(LibraryListLimit, cancellationToken).ConfigureAwait(true);

            LibraryEntries.Clear();
            foreach (RollProfileSummary entry in entries)
            {
                LibraryEntries.Add(entry);
            }

            SelectedLibraryEntry = LibraryEntries.FirstOrDefault();
            IsLibraryOpen = true;
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    [RelayCommand]
    private void CloseLibrary() => IsLibraryOpen = false;

    /// <summary>把选中的那条辊形调进编辑器。当前未保存的改动会丢，所以脏的时候先拦一下。</summary>
    [RelayCommand]
    private async Task LoadFromLibraryAsync(CancellationToken cancellationToken)
    {
        if (SelectedLibraryEntry is null)
        {
            return;
        }

        try
        {
            RollProfileDefinition? definition = await this.library
                .GetAsync(SelectedLibraryEntry.ProfileId, cancellationToken).ConfigureAwait(true);
            if (definition is null)
            {
                return;
            }

            this.composite = definition.Profile;
            this.committedComposite = definition.Profile;
            ProfileId = definition.ProfileId;
            ProfileName = definition.Name;
            IsDirty = false;
            IsLibraryOpen = false;
            Rebuild();
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    /// <summary>从库里删掉选中的那条。已经用过它的作业不受影响——作业存的是快照。</summary>
    [RelayCommand]
    private async Task DeleteFromLibraryAsync(CancellationToken cancellationToken)
    {
        if (SelectedLibraryEntry is null)
        {
            return;
        }

        try
        {
            await this.library.DeleteAsync(SelectedLibraryEntry.ProfileId, cancellationToken).ConfigureAwait(true);
            if (string.Equals(ProfileId, SelectedLibraryEntry.ProfileId, StringComparison.Ordinal))
            {
                // 编辑器里还开着它：曲线留着，但它已经不在库里了，再存就是新的一条。
                ProfileId = null;
            }

            await OpenLibraryAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    /// <summary>"打开"面板一次列多少条。</summary>
    private const int LibraryListLimit = 200;

    /// <summary>新条目的标识。精确到毫秒：只到秒的话，一秒内另存两次会悄悄盖掉前一条。</summary>
    private static string NewProfileId() =>
        string.Create(CultureInfo.InvariantCulture, $"P{DateTimeOffset.Now:yyyyMMddHHmmssfff}");

    /// <summary>放弃修改：回到上次进入本页时的辊形，而不是清空。</summary>
    public override void DiscardChanges()
    {
        this.composite = this.committedComposite;
        base.DiscardChanges();
        Rebuild();
    }

    /// <summary>切到本页时记住当前状态，"放弃修改"才有东西可回。</summary>
    public override void OnActivated() => this.committedComposite = this.composite;

    private void ShowSegmentParameters(SegmentRowViewModel? row)
    {
        SegmentParameters.Clear();
        if (row is null)
        {
            SegmentParametersTitle = string.Empty;
            return;
        }

        IRollProfileType profileType = this.profileTypes.Get(row.Segment.ProfileTypeKey);
        SegmentParametersTitle = Localizer["ProfileType_" + profileType.Key];

        ParameterSet values = profileType.Schema.ApplyDefaults(row.Segment.Parameters);
        foreach (ParameterDescriptor descriptor in profileType.Schema.Descriptors)
        {
            var parameterRow = new ParameterRowViewModel(descriptor, values.Get(descriptor.Key), Localizer);
            parameterRow.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ParameterRowViewModel.Text))
                {
                    WriteBackSelectedSegment();
                }
            };
            SegmentParameters.Add(parameterRow);
        }
    }

    private void Rebuild()
    {
        Segments.Clear();
        foreach (RollProfileSegment segment in this.composite.Segments)
        {
            Segments.Add(new SegmentRowViewModel(
                segment,
                Localizer["ProfileType_" + segment.ProfileTypeKey],
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"{segment.FromMm:F0} – {segment.ToMm:F0} mm")));
        }

        SelectedSegment = Segments.FirstOrDefault();
        RefreshPreview();
        RefreshValidation();
    }

    private void RefreshPreview()
    {
        try
        {
            RollProfile composed = this.composite.Compose(
                this.geometry, this.profileTypes, this.settings.ProfileSampleCount);
            ComposedPoints = composed.Points
                .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMm(point.RadiusOffsetMm)))
                .ToArray();

            var mainOnly = new CompositeRollProfile(new[] { this.composite.Segments[0] });
            RollProfile main = mainOnly.Compose(this.geometry, this.profileTypes, this.settings.ProfileSampleCount);
            MainPoints = main.Points
                .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMm(point.RadiusOffsetMm)))
                .ToArray();
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
            ComposedPoints = Array.Empty<(double, double)>();
            MainPoints = Array.Empty<(double, double)>();
        }

        RefreshReferenceDeviation();
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshValidation()
    {
        int sampleCount = this.settings.ProfileSampleCount;
        SegmentCountText = string.Create(
            CultureInfo.CurrentCulture,
            $"{sampleCount - 1} / {this.geometry.BodyLengthMm / (sampleCount - 1):F1} mm");

        MaxChordErrorText = ComposedPoints.Count >= 3
            ? string.Create(CultureInfo.CurrentCulture, $"{EstimateChordErrorMicrometer():F2} µm")
            : "--";

        bool aligned = this.composite.Segments
            .Skip(1)
            .All(segment => segment.FromMm >= 0.0 && segment.ToMm <= this.geometry.BodyLengthMm);
        TaperAlignmentText = Localizer[aligned ? "Profile_Aligned" : "Profile_NotAligned"];

        AxisDescription? crownAxis = this.machine.Axes.FirstOrDefault(axis =>
            axis.IsPresent && string.Equals(axis.Role, CrownAxisRole, StringComparison.Ordinal));
        CompensationAxisText = crownAxis?.Name ?? Localizer["Common_NotConfigured"];
    }

    /// <summary>
    /// 弦高误差：把折线与更细一级采样的曲线比，取最大偏差。
    /// 点数不够时这个值会变大，提醒把采样点数调上去。
    /// </summary>
    private double EstimateChordErrorMicrometer()
    {
        RollProfile fine = this.composite.Compose(
            this.geometry, this.profileTypes, (this.settings.ProfileSampleCount * 2) - 1);
        RollProfile coarse = this.composite.Compose(
            this.geometry, this.profileTypes, this.settings.ProfileSampleCount);

        double worst = 0.0;
        foreach (ProfilePoint point in fine.Points)
        {
            double difference = Math.Abs(point.RadiusOffsetMm - coarse.RadiusOffsetAtMm(point.BodyPositionMm));
            worst = Math.Max(worst, difference);
        }

        return UnitConversion.RadiusMmToDiameterMicrometer(worst);
    }
}
