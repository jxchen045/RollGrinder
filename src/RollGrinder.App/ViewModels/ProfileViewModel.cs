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

/// <summary>段表里的一行：类型、起点 Z、段长、终点 Z。</summary>
public sealed partial class SegmentRowViewModel : ObservableObject
{
    public SegmentRowViewModel(int order, string displayName)
    {
        Order = order;
        OrderText = order.ToString(CultureInfo.InvariantCulture);
        DisplayName = displayName;
    }

    /// <summary>第几段（从 1 起，从头架往尾架数）。</summary>
    public int Order { get; }

    public string OrderText { get; }

    /// <summary>曲线类型名。</summary>
    public string DisplayName { get; }

    /// <summary>起点 Z（mm）。改了前面某段的长度，这里就地跟着变，不重建列表。</summary>
    [ObservableProperty]
    private string startText = string.Empty;

    [ObservableProperty]
    private string lengthText = string.Empty;

    [ObservableProperty]
    private string endText = string.Empty;

    [ObservableProperty]
    private bool isSelected;

    /// <summary>这一段有错误（出界、参数不成立、段界跳变）：行尾一条红标。</summary>
    [ObservableProperty]
    private bool hasError;
}

/// <summary>点表里的一行（段内 Z mm、直径偏差 µm），界面上直接改文字。</summary>
public sealed partial class PointRowViewModel : ObservableObject
{
    public PointRowViewModel(string zText, string valueText)
    {
        this.zText = zText;
        this.valueText = valueText;
    }

    [ObservableProperty]
    private string zText;

    [ObservableProperty]
    private string valueText;
}

/// <summary>问题列表里的一行。</summary>
/// <param name="Text">说明文字（已本地化）。</param>
/// <param name="IsError">错误（不能保存）还是提示。</param>
public sealed record ProfileIssueRowViewModel(string Text, bool IsError);

/// <summary>
/// 辊形编辑（阶段 1）。Z 原点是磨削起点（头架侧辊身端面），向尾架为正。
///
/// 段表只填第一段的起点 Z 和每段的长度，终点自动算、段与段首尾相接，不会重叠或断开。
/// 勾"对称"只编头架端的段，尾架端镜像生成；落库的是展开后的整条辊形，不存"对称"。
/// 每改一次就校验：段长合计对不上设计长度、段界跳变、参数越界、点表不成立都是错误，有错不能保存。
/// 打开旧的叠加辊形时，按原来的合成曲线转成一段点表，形状不变。
/// </summary>
public sealed partial class ProfileViewModel : PageViewModelBase
{
    /// <summary>中高轴（补偿执行轴）在 machine.json 里的 role。</summary>
    public const string CrownAxisRole = "RollProfile";

    /// <summary>辊形已经排满设计长度时，新插一段的默认长度（mm）。</summary>
    public const double DefaultInsertLengthMm = 100.0;

    private readonly RollProfileTypeRegistry profileTypes;
    private readonly MachineDescription machine;
    private readonly HmiSettings settings;
    private readonly IRollProfileRepository library;
    private readonly AsyncRelayCommand saveKeyCommand;

    /// <summary>编辑用的参考几何：辊身长度就是辊形的设计长度，直径只用来算曲线，取机床最小直径。</summary>
    private RollGeometry geometry;

    /// <summary>第一段的起点 Z（mm）。</summary>
    private double startZMm;

    /// <summary>
    /// 段表上的段：不对称时是整条辊形；对称时只是头架端的那些（可带一段跨中点的中间段）。
    /// 这是编辑的依据，<see cref="composite"/> 由它推出来。
    /// </summary>
    private readonly List<SequentialSegment> segments = new();

    /// <summary>整条辊形（预览、校验、存库都用它）；一段都没有或对称展开不了时为 null。</summary>
    private CompositeRollProfile? composite;

    /// <summary>对称展开不了的原因。</summary>
    private SymmetryResult? symmetryResult;

    /// <summary>框里正在输入、还不成立的内容（资源键）；成立了就清掉。</summary>
    private string? pendingInputErrorKey;

    /// <summary>设计长度框里的内容不成立。</summary>
    private bool bodyLengthInvalid;

    /// <summary>程序里改输入框时不要反过来又触发一次回写。</summary>
    private bool suppressWriteBack;

    /// <summary>上一次"干净"的辊形与设计长度，供"放弃修改"回退。</summary>
    private CompositeRollProfile? committedComposite;

    private double committedBodyLengthMm;

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

        this.geometry = RollGeometry.FromDiameter(machine.Workpiece.MinBodyLengthMm, machine.Workpiece.MinDiameterMm);
        this.segments.Add(new SequentialSegment(ProfileTypeKeys.Cylindrical, this.geometry.BodyLengthMm, ParameterSet.Empty));
        this.bodyLengthMmText = FormatLength(this.geometry.BodyLengthMm);

        AvailableTypes = new ObservableCollection<string>(profileTypes.All.Select(type => type.Key));
        this.selectedTypeForInsert = AvailableTypes.FirstOrDefault() ?? ProfileTypeKeys.Cylindrical;

        // 有错误时"保存""另存为"变灰。插段在左栏"插入"按钮上，功能条上不重复。
        this.saveKeyCommand = new AsyncRelayCommand(() => SaveAsync(CancellationToken.None), () => !HasErrors);
        SetFunctionKeys(new[]
        {
            new FunctionKeyViewModel("Fn_Save", this.saveKeyCommand, localizer, FunctionKeyKind.Primary, requiresEditable: true),
            new FunctionKeyViewModel("Fn_SaveAs", SaveAsCommand, localizer, requiresEditable: true),
            // 两个键都要选文件，对话框在视图里；这里只负责触发与收结果。
            new FunctionKeyViewModel("Fn_ImportPoints", RequestImportPointsCommand, localizer, requiresEditable: true),
            new FunctionKeyViewModel("Fn_GeneratePoints", RequestGeneratePointsCommand, localizer),
            new FunctionKeyViewModel("Fn_Validate", ValidateCommand, localizer),
            new FunctionKeyViewModel("Fn_ProfileLibrary", OpenLibraryCommand, localizer),
        });

        RecomputeComposite();
        this.committedComposite = this.composite;
        this.committedBodyLengthMm = this.geometry.BodyLengthMm;
        Rebuild(1);
    }

    public override PageKey Key => PageKey.Profile;

    public override string TitleResourceKey => "Page_Profile";

    public override string MenuHintResourceKey => "Menu_ProfileHint";

    /// <summary>编辑页：自动循环挂着程序时落只读锁，免得改了辊形以为机床会跟着变。</summary>
    public override bool LocksDuringRun => true;

    /// <summary>离线可用：只和数据库与配置打交道，不碰机床。</summary>
    public override bool WorksOffline => true;

    public ObservableCollection<SegmentRowViewModel> Segments { get; } = new();

    /// <summary>选中段的参数格（点表那一列不在这里，在 <see cref="PointRows"/>）。</summary>
    public ObservableCollection<ParameterRowViewModel> SegmentParameters { get; } = new();

    /// <summary>选中段是点表时，它的点。</summary>
    public ObservableCollection<PointRowViewModel> PointRows { get; } = new();

    public ObservableCollection<string> AvailableTypes { get; }

    /// <summary>当前的整条辊形；删到空或对称展开不了时为 null。给自检与视图读。</summary>
    public CompositeRollProfile? Composite => this.composite;

    // ── 预览 ──────────────────────────────────────────────────────────────────

    /// <summary>合成辊形（辊身坐标 mm，直径量 mm）。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMm)> ComposedPoints { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>选中段那一截（加粗高亮）。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMm)> SelectedSegmentPoints { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>段界的 Z（虚线）。</summary>
    public IReadOnlyList<double> BoundaryZs { get; private set; } = Array.Empty<double>();

    /// <summary>选中段是点表时，表里的原始点（辊身坐标 mm，直径量 mm），和插值曲线画在一起。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMm)> TablePoints { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>导进来的对照线（辊身坐标 mm，直径量 mm）。只作对照，不改辊形。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMm)> ReferencePoints { get; private set; } =
        Array.Empty<(double, double)>();

    public event EventHandler? PreviewChanged;

    /// <summary>界面要导入点表（进点表段）时触发；路径由视图选。</summary>
    public event EventHandler? ImportPointsRequested;

    /// <summary>界面要导入对照线时触发；路径由视图选。</summary>
    public event EventHandler? ImportReferenceRequested;

    /// <summary>界面要导出点列时触发；路径由视图选。</summary>
    public event EventHandler? GeneratePointsRequested;

    /// <summary>对照线与当前设计差得最多的地方（直径量 µm）；没导入时为空。</summary>
    [ObservableProperty]
    private string referenceDeviationText = string.Empty;

    // ── 校验 ──────────────────────────────────────────────────────────────────

    /// <summary>每改一次就重新算的问题列表：错误在前，提示在后。</summary>
    public ObservableCollection<ProfileIssueRowViewModel> Issues { get; } = new();

    /// <summary>有错误：不能保存。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    private bool hasErrors;

    partial void OnHasErrorsChanged(bool value) => this.saveKeyCommand.NotifyCanExecuteChanged();

    /// <summary>问题列表上方那一行："有 N 处错误" / "校验通过"。</summary>
    [ObservableProperty]
    private string issueSummaryText = string.Empty;

    [ObservableProperty]
    private string maxChordErrorText = "--";

    [ObservableProperty]
    private string segmentCountText = "--";

    [ObservableProperty]
    private string compensationAxisText = "--";

    /// <summary>最近一个动作的结果提示。</summary>
    [ObservableProperty]
    private string statusResourceKey = string.Empty;

    /// <summary>提示文字。资源键为空时不显示。</summary>
    public string StatusText => string.IsNullOrEmpty(StatusResourceKey) ? string.Empty : Localizer[StatusResourceKey];

    partial void OnStatusResourceKeyChanged(string value) => OnPropertyChanged(nameof(StatusText));

    // ── 设计长度与对称 ────────────────────────────────────────────────────────

    /// <summary>设计长度（mm）。段长合计要正好等于它。</summary>
    [ObservableProperty]
    private string bodyLengthMmText;

    partial void OnBodyLengthMmTextChanged(string value)
    {
        if (this.suppressWriteBack)
        {
            return;
        }

        WorkpieceLimits limits = this.machine.Workpiece;
        if (!TryParseDouble(value, out double lengthMm)
            || lengthMm < limits.MinBodyLengthMm
            || lengthMm > limits.MaxBodyLengthMm)
        {
            this.bodyLengthInvalid = true;
            RefreshIssues();
            return;
        }

        this.bodyLengthInvalid = false;
        if (Math.Abs(lengthMm - this.geometry.BodyLengthMm) < 1e-9)
        {
            RefreshIssues();
            return;
        }

        this.geometry = RollGeometry.FromDiameter(lengthMm, limits.MinDiameterMm);
        MarkDirty();
        RecomputeComposite();
        Rebuild(SelectedSegment?.Order);
    }

    /// <summary>
    /// 对称编辑：只编头架端的段，尾架端镜像生成。只是编辑辅助——落库的是展开后的整条辊形。
    /// </summary>
    [ObservableProperty]
    private bool isSymmetric;

    /// <summary>对称开关能不能按：含 CVC 这类本身不对称的段时置灰（已经开着的照样能关）。</summary>
    [ObservableProperty]
    private bool canToggleSymmetry = true;

    partial void OnIsSymmetricChanged(bool value)
    {
        if (this.suppressWriteBack)
        {
            return;
        }

        if (value)
        {
            // 打开对称：现有辊形得本身左右对称，才折得回头架端那一半；空的直接开始编。
            IReadOnlyList<SequentialSegment>? half = this.composite is null
                ? Array.Empty<SequentialSegment>()
                : ProfileSymmetry.TryFold(this.composite, this.geometry.BodyLengthMm, this.profileTypes);
            if (half is null)
            {
                StatusResourceKey = "Profile_SymmetryNotFoldable";
                SetSymmetricSilently(false);
                return;
            }

            ReplaceSegments(half);
        }
        else if (this.composite is not null)
        {
            // 关掉对称：展开后的整条辊形就是要继续编的段，两端从此各自独立。
            ReplaceSegments(this.composite.SequentialSegments());
        }

        StatusResourceKey = string.Empty;
        RecomputeComposite();
        Rebuild(1);
    }

    private void SetSymmetricSilently(bool value)
    {
        this.suppressWriteBack = true;
        try
        {
            IsSymmetric = value;
        }
        finally
        {
            this.suppressWriteBack = false;
        }
    }

    private void ReplaceSegments(IEnumerable<SequentialSegment> replacement)
    {
        List<SequentialSegment> list = replacement.ToList();
        this.segments.Clear();
        this.segments.AddRange(list);
    }

    // ── 选中段 ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private SegmentRowViewModel? selectedSegment;

    [ObservableProperty]
    private string selectedTypeForInsert;

    [ObservableProperty]
    private string segmentParametersTitle = string.Empty;

    /// <summary>选中段的起点 Z。只有第一段能填，其余段的起点是上一段的终点。</summary>
    [ObservableProperty]
    private string segmentStartText = string.Empty;

    [ObservableProperty]
    private bool isFirstSegmentSelected;

    /// <summary>选中段的长度（mm）。</summary>
    [ObservableProperty]
    private string segmentLengthText = string.Empty;

    /// <summary>选中段是否沿段中点镜像（点表、CVC 等）。</summary>
    [ObservableProperty]
    private bool segmentIsMirrored;

    /// <summary>选中段能不能镜像。锥度（端部减薄）按所在半边自动朝向端面，镜像对它无效，勾选框置灰。</summary>
    [ObservableProperty]
    private bool canMirrorSegment;

    /// <summary>选中段是点表：参数区显示点表格子。</summary>
    [ObservableProperty]
    private bool isPointTableSelected;

    [ObservableProperty]
    private PointRowViewModel? selectedPoint;

    partial void OnSegmentStartTextChanged(string value) => WriteBackSelectedSegment();

    partial void OnSegmentLengthTextChanged(string value) => WriteBackSelectedSegment();

    partial void OnSegmentIsMirroredChanged(bool value) => WriteBackSelectedSegment();

    partial void OnSelectedSegmentChanged(SegmentRowViewModel? value)
    {
        foreach (SegmentRowViewModel row in Segments)
        {
            row.IsSelected = ReferenceEquals(row, value);
        }

        // 换了一段，上一段框里没打完的内容作废，它的输入错误也一起清掉。
        this.pendingInputErrorKey = null;

        SequentialSegment? segment = value is null ? null : this.segments[value.Order - 1];
        this.suppressWriteBack = true;
        try
        {
            IsFirstSegmentSelected = value?.Order == 1;
            SegmentStartText = value?.StartText ?? string.Empty;
            SegmentLengthText = segment is null ? string.Empty : FormatLength(segment.LengthMm);
            CanMirrorSegment = segment is not null && !this.profileTypes.Get(segment.ProfileTypeKey).IsEndRelief;
            SegmentIsMirrored = CanMirrorSegment && segment!.IsMirrored;
        }
        finally
        {
            this.suppressWriteBack = false;
        }

        ShowSegmentParameters(segment);
        RefreshSelectionPreview();
        RefreshIssues();
    }

    /// <summary>
    /// 把左下的起点 / 长度 / 镜像、右下的参数格和点表写回选中的那一段。
    /// 只就地更新段表各行的 Z 与预览，不重建列表和参数格——焦点留在正在输入的框里。
    /// </summary>
    private void WriteBackSelectedSegment()
    {
        if (this.suppressWriteBack || SelectedSegment is null)
        {
            return;
        }

        int index = SelectedSegment.Order - 1;
        SequentialSegment current = this.segments[index];

        if (!TryParseDouble(SegmentLengthText, out double lengthMm) || lengthMm <= 0.0
            || (index == 0 && (!TryParseDouble(SegmentStartText, out double start) || start < 0.0)))
        {
            ShowInputError("Profile_Issue_InputLength");
            return;
        }

        var pairs = new List<KeyValuePair<string, ParameterValue>>();
        foreach (ParameterRowViewModel row in SegmentParameters)
        {
            ParameterValue? value = row.ToParameterValue();
            if (value is null)
            {
                ShowInputError("Profile_Issue_InputParameter");
                return;
            }

            pairs.Add(new KeyValuePair<string, ParameterValue>(row.Key, value));
        }

        if (IsPointTableSelected)
        {
            IReadOnlyList<TablePoint>? points = ParsePointRows();
            if (points is null)
            {
                ShowInputError("Profile_Issue_InputPoints");
                return;
            }

            pairs.Add(new KeyValuePair<string, ParameterValue>(PointTableProfileType.PointsKey, ParameterValue.FromPoints(points)));
        }

        if (index == 0)
        {
            TryParseDouble(SegmentStartText, out this.startZMm);
        }

        this.pendingInputErrorKey = null;
        this.segments[index] = current with
        {
            LengthMm = lengthMm,
            Parameters = new ParameterSet(pairs),
            IsMirrored = CanMirrorSegment && SegmentIsMirrored,
        };
        MarkDirty();
        RecomputeComposite();
        RefreshRowPositions();
        RefreshPreview();
        RefreshValidation();
        RefreshIssues();
    }

    private IReadOnlyList<TablePoint>? ParsePointRows()
    {
        var points = new List<TablePoint>(PointRows.Count);
        foreach (PointRowViewModel row in PointRows)
        {
            if (!TryParseDouble(row.ZText, out double z) || !TryParseDouble(row.ValueText, out double value))
            {
                return null;
            }

            points.Add(new TablePoint(z, value));
        }

        return points;
    }

    private void ShowInputError(string resourceKey)
    {
        this.pendingInputErrorKey = resourceKey;
        RefreshIssues();
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string FormatLength(double lengthMm) =>
        lengthMm.ToString("0.###", CultureInfo.CurrentCulture);

    // ── 段操作 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 在选中段之后插一段所选类型。还没排满设计长度（对称时是中点）就补齐剩下的长度；
    /// 已经排满了就先给 <see cref="DefaultInsertLengthMm"/>，校验会提示段长合计超了，由人去分。
    /// </summary>
    [RelayCommand]
    private void InsertSegment()
    {
        IRollProfileType profileType = this.profileTypes.Get(SelectedTypeForInsert);
        if (IsSymmetric && !profileType.SupportsSymmetricEditing)
        {
            StatusResourceKey = "Profile_SymmetryUnsupportedType";
            return;
        }

        double target = IsSymmetric ? this.geometry.BodyLengthMm / 2.0 : this.geometry.BodyLengthMm;
        double remaining = target - (this.startZMm + this.segments.Sum(segment => segment.LengthMm));
        double lengthMm = remaining > ProfileLayoutCheck.ToleranceMm ? remaining : DefaultInsertLengthMm;

        int position = SelectedSegment?.Order ?? this.segments.Count;
        this.segments.Insert(position, NewSegment(profileType, lengthMm, isMirrored: false));
        StatusResourceKey = string.Empty;
        MarkDirty();
        RecomputeComposite();
        Rebuild(position + 1);
    }

    /// <summary>复制选中段，接在它后面。</summary>
    [RelayCommand]
    private void CopySegment()
    {
        if (SelectedSegment is null)
        {
            return;
        }

        int position = SelectedSegment.Order;
        this.segments.Insert(position, this.segments[position - 1]);
        MarkDirty();
        RecomputeComposite();
        Rebuild(position + 1);
    }

    /// <summary>把选中段换成所选类型，长度与镜像不变，参数回到新类型的默认值。</summary>
    [RelayCommand]
    private void ChangeSegmentType()
    {
        if (SelectedSegment is null)
        {
            return;
        }

        IRollProfileType profileType = this.profileTypes.Get(SelectedTypeForInsert);
        if (IsSymmetric && !profileType.SupportsSymmetricEditing)
        {
            StatusResourceKey = "Profile_SymmetryUnsupportedType";
            return;
        }

        int index = SelectedSegment.Order - 1;
        SequentialSegment current = this.segments[index];
        this.segments[index] = NewSegment(profileType, current.LengthMm, current.IsMirrored);
        MarkDirty();
        RecomputeComposite();
        Rebuild(index + 1);
    }

    /// <summary>删掉选中的段，后面的段往前接上。可以删到一段不剩——空辊形不能保存。</summary>
    [RelayCommand]
    private void RemoveSegment()
    {
        if (SelectedSegment is null)
        {
            return;
        }

        int order = SelectedSegment.Order;
        this.segments.RemoveAt(order - 1);
        MarkDirty();
        RecomputeComposite();

        // 选中留在原位置（删的是最后一段就落到新的最后一段），连着按"删除"能一段段删。
        Rebuild(Math.Min(order, this.segments.Count));
    }

    [RelayCommand]
    private void MoveSegmentUp()
    {
        if (SelectedSegment is null || SelectedSegment.Order <= 1)
        {
            return;
        }

        int index = SelectedSegment.Order - 1;
        (this.segments[index - 1], this.segments[index]) = (this.segments[index], this.segments[index - 1]);
        MarkDirty();
        RecomputeComposite();

        // 选中跟着这一段走，连按几次就能一直往上挪。
        Rebuild(index);
    }

    [RelayCommand]
    private void MoveSegmentDown()
    {
        if (SelectedSegment is null || SelectedSegment.Order >= this.segments.Count)
        {
            return;
        }

        int index = SelectedSegment.Order - 1;
        (this.segments[index + 1], this.segments[index]) = (this.segments[index], this.segments[index + 1]);
        MarkDirty();
        RecomputeComposite();
        Rebuild(index + 2);
    }

    [RelayCommand]
    private void Validate() => Rebuild(SelectedSegment?.Order);

    private static SequentialSegment NewSegment(IRollProfileType profileType, double lengthMm, bool isMirrored) =>
        new(
            profileType.Key,
            lengthMm,
            profileType.Key == ProfileTypeKeys.PointTable
                ? PointTableProfileType.DefaultsFor(lengthMm)
                : profileType.Schema.CreateDefaults(),
            isMirrored && !profileType.IsEndRelief);

    // ── 点表 ──────────────────────────────────────────────────────────────────

    /// <summary>在选中点之后加一个点：落在它和下一个点的正中，值取两者平均；选中的是最后一个点就往后接。</summary>
    [RelayCommand]
    private void AddPoint()
    {
        if (!IsPointTableSelected)
        {
            return;
        }

        int index = SelectedPoint is null ? PointRows.Count - 1 : PointRows.IndexOf(SelectedPoint);
        IReadOnlyList<TablePoint>? points = ParsePointRows();
        TablePoint added;
        if (points is null || points.Count == 0)
        {
            added = new TablePoint(0.0, 0.0);
            index = PointRows.Count - 1;
        }
        else if (index >= 0 && index < points.Count - 1)
        {
            added = new TablePoint((points[index].X + points[index + 1].X) / 2.0, (points[index].Y + points[index + 1].Y) / 2.0);
        }
        else
        {
            index = points.Count - 1;
            added = new TablePoint(points[^1].X + 10.0, points[^1].Y);
        }

        PointRowViewModel row = NewPointRow(added);
        PointRows.Insert(index + 1, row);
        SelectedPoint = row;
        WriteBackSelectedSegment();
    }

    [RelayCommand]
    private void RemovePoint()
    {
        if (!IsPointTableSelected || SelectedPoint is null)
        {
            return;
        }

        int index = PointRows.IndexOf(SelectedPoint);
        PointRows.Remove(SelectedPoint);
        SelectedPoint = PointRows.Count == 0 ? null : PointRows[Math.Min(index, PointRows.Count - 1)];
        WriteBackSelectedSegment();
    }

    private PointRowViewModel NewPointRow(TablePoint point)
    {
        var row = new PointRowViewModel(
            point.X.ToString("0.###", CultureInfo.CurrentCulture),
            point.Y.ToString("0.###", CultureInfo.CurrentCulture));
        row.PropertyChanged += (_, _) => WriteBackSelectedSegment();
        return row;
    }

    [RelayCommand]
    private void RequestImportPoints() => ImportPointsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RequestImportReference() => ImportReferenceRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RequestGeneratePoints() => GeneratePointsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 导入点表（CSV：Z mm, 直径偏差 µm）。选中的是点表段就换掉它的点；否则在选中段之后插一段点表。
    /// 点挪到从 Z = 0 起（导出的表常是整根辊身坐标），段长取点的跨度。
    /// </summary>
    public Task ImportPointsAsync(string filePath, CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            IReadOnlyList<TablePoint> points = PointTableCsv.StartAtZero(
                PointTableCsv.Parse(await File.ReadAllLinesAsync(filePath, token).ConfigureAwait(true)));
            if (points.Count < PointTableProfileType.MinimumPoints || points[^1].X <= 0.0)
            {
                // 一两个点连不成一条线；读错了文件该说出来，而不是画半条。
                StatusResourceKey = "Profile_PointsNotUsable";
                return;
            }

            double lengthMm = points[^1].X;
            int index;
            if (SelectedSegment is not null && IsPointTableSelected)
            {
                index = SelectedSegment.Order - 1;
                SequentialSegment current = this.segments[index];
                this.segments[index] = current with
                {
                    LengthMm = lengthMm,
                    Parameters = current.Parameters.With(PointTableProfileType.PointsKey, ParameterValue.FromPoints(points)),
                };
            }
            else
            {
                index = SelectedSegment?.Order ?? this.segments.Count;
                this.segments.Insert(index, new SequentialSegment(
                    ProfileTypeKeys.PointTable,
                    lengthMm,
                    PointTableProfileType.DefaultsFor(lengthMm)
                        .With(PointTableProfileType.PointsKey, ParameterValue.FromPoints(points))));
            }

            StatusResourceKey = "Profile_PointsImportedIntoSegment";
            MarkDirty();
            RecomputeComposite();
            Rebuild(index + 1);
        }, cancellationToken);

    /// <summary>
    /// 按当前合成曲线生成点列并写成 CSV。点数就是下发用的采样点数——
    /// 生成出来的这一份和真正下发给 NC 的是同一条线，不是另算一遍。
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

    /// <summary>
    /// 读一份 CSV 点列作为对照线：上一版辊形、现场量出来的曲线，拿进来和当前设计叠着看。
    /// 只作对照，不改辊形——要把表变成辊形用"导入点表"。
    /// </summary>
    public Task ImportReferenceAsync(string filePath, CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            IReadOnlyList<TablePoint> points =
                PointTableCsv.Parse(await File.ReadAllLinesAsync(filePath, token).ConfigureAwait(true));
            if (points.Count < 2)
            {
                StatusResourceKey = "Profile_PointsNotUsable";
                return;
            }

            ReferencePoints = points
                .OrderBy(point => point.X)
                .Select(point => (point.X, UnitConversion.MicrometerToMm(point.Y)))
                .ToArray();
            RefreshReferenceDeviation();
            StatusResourceKey = "Profile_PointsImported";
            PreviewChanged?.Invoke(this, EventArgs.Empty);
        }, cancellationToken);

    /// <summary>对照线与当前设计差得最多的地方。看的就是这个数。</summary>
    private void RefreshReferenceDeviation()
    {
        if (ReferencePoints.Count == 0 || ComposedPoints.Count < 2)
        {
            ReferenceDeviationText = string.Empty;
            return;
        }

        var composed = new RollProfile(ComposedPoints.Select(point => new ProfilePoint(point.BodyPositionMm, point.DiameterMm)));
        double worst = ReferencePoints.Max(point => Math.Abs(point.DiameterMm - composed.RadiusOffsetAtMm(point.BodyPositionMm)));
        ReferenceDeviationText = Localizer.Format("Profile_ReferenceDeviationFormat", UnitConversion.MmToMicrometer(worst));
    }

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
    public override async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        string name = ProfileName.Trim();
        string targetId = ProfileId ?? NewProfileId();
        if (name.Length > 0 && !HasErrors)
        {
            try
            {
                // 改了名字撞上库里另一条：不悄悄存成两条同名的，问一句"覆盖 / 改名 / 取消"。
                if (await this.library.FindIdByNameAsync(name, targetId, cancellationToken).ConfigureAwait(true) is not null)
                {
                    NamePrompt.Open(
                        Localizer["Library_SaveTitle"],
                        name,
                        (chosen, overwrite, token) => SaveUnderNameAsync(chosen, targetId, overwrite, token),
                        Localizer.Format("Library_NameTakenFormat", name));
                    return false;
                }
            }
            catch (DataStoreException ex)
            {
                Alarms.RaiseException(ex);
                return false;
            }
        }

        return await StoreAsync(targetId, name, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>起名字的框（另存为、保存时撞名）。</summary>
    public NamePromptViewModel NamePrompt { get; }

    public override bool HasModalPrompt => NamePrompt.IsOpen;

    public override bool TryDismissPrompt()
    {
        if (!NamePrompt.IsOpen)
        {
            return false;
        }

        NamePrompt.CancelCommand.Execute(null);
        return true;
    }

    /// <summary>
    /// 另存一条新辊形，库里原来那条不动。先起名字：预填"原名-副本"；
    /// 名字在库里已经有了，框里说清楚，由操作员选覆盖、改名或取消。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStore))]
    private void SaveAs() => NamePrompt.Open(
        Localizer["Profile_SaveAsTitle"],
        string.IsNullOrWhiteSpace(ProfileName) ? string.Empty : Localizer.Format("Library_CopyNameFormat", ProfileName.Trim()),
        (name, overwrite, token) => SaveUnderNameAsync(name, NewProfileId(), overwrite, token));

    private bool CanStore() => !HasErrors;

    /// <summary>按指定名字存；名字被库里另一条占了，要么覆盖那一条（存进它的标识），要么退回去让人改名。</summary>
    private async Task<NamePromptOutcome> SaveUnderNameAsync(
        string name, string targetId, bool overwrite, CancellationToken cancellationToken)
    {
        try
        {
            string? holder = await this.library.FindIdByNameAsync(name, targetId, cancellationToken).ConfigureAwait(true);
            if (holder is not null)
            {
                if (!overwrite)
                {
                    return NamePromptOutcome.Conflict(Localizer.Format("Library_NameTakenFormat", name));
                }

                targetId = holder;
            }
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
            return NamePromptOutcome.Refused(Localizer["Library_SaveFailed"]);
        }

        return await StoreAsync(targetId, name, cancellationToken).ConfigureAwait(true)
            ? NamePromptOutcome.Done
            : NamePromptOutcome.Refused(Localizer["Library_SaveFailed"]);
    }

    private async Task<bool> StoreAsync(string profileId, string name, CancellationToken cancellationToken)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            // 没名字存进去就找不回来了，宁可不存。
            Alarms.Raise(AlarmSeverity.Warning, "Profile_NeedsName", code: AlarmCodes.DomainFailure);
            return false;
        }

        if (HasErrors || this.composite is null)
        {
            // 有错的辊形不进库：问题列表里逐条写着，改好再存。
            Alarms.Raise(AlarmSeverity.Warning, "Profile_HasErrors", code: AlarmCodes.DomainFailure);
            return false;
        }

        try
        {
            // 库里名字唯一：同名的两条分不清哪条是哪条（第一轮甲方测试）。兜底，正常走不到这里。
            if (await this.library.FindIdByNameAsync(name, profileId, cancellationToken).ConfigureAwait(true) is not null)
            {
                Alarms.Raise(AlarmSeverity.Warning, "Library_NameTaken", name, AlarmCodes.DomainFailure);
                return false;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            RollProfileDefinition? existing = await this.library.GetAsync(profileId, cancellationToken).ConfigureAwait(true);

            // 存的是展开后的整条辊形：对称只是编辑辅助，不落库。
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
            this.committedBodyLengthMm = this.geometry.BodyLengthMm;
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

    /// <summary>
    /// 把选中的那条辊形调进编辑器。旧的叠加辊形按原合成曲线转成一段点表（形状不变），
    /// 并标成"改过"——存一次，库里这条就换成新格式。
    /// </summary>
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

            SetBodyLength(definition.BodyLengthMm);
            bool legacy = definition.Profile.Layout == ProfileLayout.Superimposed;
            CompositeRollProfile loaded = legacy
                ? LegacyProfileConversion.ToPointTable(definition.Profile, this.geometry, this.profileTypes, this.settings.ProfileSampleCount)
                : definition.Profile;

            LoadProfile(loaded);
            this.committedComposite = loaded;
            this.committedBodyLengthMm = this.geometry.BodyLengthMm;
            ProfileId = definition.ProfileId;
            ProfileName = definition.Name;
            IsDirty = legacy;
            StatusResourceKey = legacy ? "Profile_LegacyConverted" : string.Empty;
            IsLibraryOpen = false;
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

    /// <summary>放弃修改：回到上次进入本页（或上次存盘、打开）时的辊形，而不是清空。</summary>
    public override void DiscardChanges()
    {
        SetBodyLength(this.committedBodyLengthMm);
        this.pendingInputErrorKey = null;
        LoadProfile(this.committedComposite);
        base.DiscardChanges();
    }

    /// <summary>切到本页时记住当前状态，"放弃修改"才有东西可回。</summary>
    public override void OnActivated()
    {
        this.committedComposite = this.composite;
        this.committedBodyLengthMm = this.geometry.BodyLengthMm;
    }

    /// <summary>把一条整辊形放进段表（对称关掉，两端各自独立）。</summary>
    private void LoadProfile(CompositeRollProfile? profile)
    {
        SetSymmetricSilently(false);
        this.startZMm = profile?.StartZMm ?? 0.0;
        ReplaceSegments(profile?.SequentialSegments() ?? Array.Empty<SequentialSegment>());
        RecomputeComposite();
        Rebuild(1);
    }

    /// <summary>换设计长度（载入、回退），不算一次修改。</summary>
    private void SetBodyLength(double lengthMm)
    {
        this.geometry = RollGeometry.FromDiameter(lengthMm, this.machine.Workpiece.MinDiameterMm);
        this.bodyLengthInvalid = false;
        this.suppressWriteBack = true;
        try
        {
            BodyLengthMmText = FormatLength(lengthMm);
        }
        finally
        {
            this.suppressWriteBack = false;
        }
    }

    // ── 重建 ──────────────────────────────────────────────────────────────────

    /// <summary>由段表推出整条辊形：对称时展开头架端，否则按起点 Z 顺接。</summary>
    private void RecomputeComposite()
    {
        this.symmetryResult = null;
        if (this.segments.Count == 0)
        {
            this.composite = null;
        }
        else if (IsSymmetric)
        {
            this.symmetryResult = ProfileSymmetry.Expand(this.geometry.BodyLengthMm, this.startZMm, this.segments, this.profileTypes);
            this.composite = this.symmetryResult.Profile;
        }
        else
        {
            this.composite = CompositeRollProfile.Sequential(this.startZMm, this.segments);
        }

        CanToggleSymmetry = IsSymmetric
            || ProfileSymmetry.IsSupported(this.segments.Select(segment => segment.ProfileTypeKey), this.profileTypes);
        OnPropertyChanged(nameof(Composite));
    }

    /// <summary>按段表重建各行、预览与校验。</summary>
    /// <param name="selectOrder">重建后选中第几段；null 或找不到时选第一段。</param>
    private void Rebuild(int? selectOrder = null)
    {
        Segments.Clear();
        for (int i = 0; i < this.segments.Count; i++)
        {
            Segments.Add(new SegmentRowViewModel(i + 1, Localizer["ProfileType_" + this.segments[i].ProfileTypeKey]));
        }

        RefreshRowPositions();
        SelectedSegment = Segments.FirstOrDefault(row => row.Order == selectOrder) ?? Segments.FirstOrDefault();
        RefreshPreview();
        RefreshValidation();
        RefreshIssues();
    }

    /// <summary>段表各行的起点、长度、终点（改了某段长度，后面各行就地跟着变）。</summary>
    private void RefreshRowPositions()
    {
        double z = this.startZMm;
        for (int i = 0; i < Segments.Count && i < this.segments.Count; i++)
        {
            double length = this.segments[i].LengthMm;
            Segments[i].StartText = FormatLength(z);
            Segments[i].LengthText = FormatLength(length);
            Segments[i].EndText = FormatLength(z + length);
            z += length;
        }
    }

    private void ShowSegmentParameters(SequentialSegment? segment)
    {
        SegmentParameters.Clear();
        PointRows.Clear();
        IsPointTableSelected = false;
        if (segment is null)
        {
            SegmentParametersTitle = string.Empty;
            return;
        }

        IRollProfileType profileType = this.profileTypes.Get(segment.ProfileTypeKey);
        SegmentParametersTitle = Localizer["ProfileType_" + profileType.Key];

        ParameterSet values = profileType.Schema.ApplyDefaults(segment.Parameters);
        foreach (ParameterDescriptor descriptor in profileType.Schema.Descriptors)
        {
            if (descriptor.Kind == ParameterValueKind.Points)
            {
                // 点表不放进参数格，单独一张表格编辑。
                IsPointTableSelected = true;
                foreach (TablePoint point in values.Get(descriptor.Key).Points)
                {
                    PointRows.Add(NewPointRow(point));
                }

                continue;
            }

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

        SelectedPoint = PointRows.FirstOrDefault();
    }

    /// <summary>
    /// 边编边校验：每改一次就把问题列表重算一遍（第一轮甲方测试 辊形 1⑥⑨）。
    /// 错误在前，有错不能保存。
    /// </summary>
    private void RefreshIssues()
    {
        var errors = new List<string>();
        var hints = new List<string>();
        var badOrders = new HashSet<int>();

        if (this.bodyLengthInvalid)
        {
            errors.Add(Localizer.Format(
                "Profile_Issue_BodyLengthFormat",
                this.machine.Workpiece.MinBodyLengthMm,
                this.machine.Workpiece.MaxBodyLengthMm));
        }

        if (this.pendingInputErrorKey is not null)
        {
            errors.Add(Localizer[this.pendingInputErrorKey]);
            if (SelectedSegment is not null)
            {
                badOrders.Add(SelectedSegment.Order);
            }
        }

        var parameterErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (this.symmetryResult is { Failure: not SymmetryFailure.None } failed)
        {
            errors.Add(DescribeSymmetryFailure(failed));
        }
        else
        {
            foreach (ProfileIssue issue in ProfileLayoutCheck.Check(this.composite, this.geometry.BodyLengthMm, this.profileTypes))
            {
                (issue.IsError ? errors : hints).Add(DescribeIssue(issue, parameterErrors));
                if (issue.IsError && issue.SegmentOrder is int order)
                {
                    badOrders.Add(order);
                }
            }
        }

        foreach (SegmentRowViewModel row in Segments)
        {
            row.HasError = badOrders.Contains(row.Order);
        }

        // 选中段越界的参数就地标在参数格上；解析不了的格子自己已经写着原因，不去盖它。
        foreach (ParameterRowViewModel row in SegmentParameters)
        {
            if (parameterErrors.TryGetValue(row.Key, out string? reason) && string.IsNullOrEmpty(row.ErrorText))
            {
                row.ErrorText = reason;
            }
        }

        PointTableErrorText = parameterErrors.TryGetValue(PointTableProfileType.PointsKey, out string? pointReason)
            ? pointReason
            : string.Empty;

        Issues.Clear();
        foreach (string text in errors)
        {
            Issues.Add(new ProfileIssueRowViewModel(text, true));
        }

        foreach (string text in hints)
        {
            Issues.Add(new ProfileIssueRowViewModel(text, false));
        }

        HasErrors = errors.Count > 0;
        IssueSummaryText = errors.Count > 0
            ? Localizer.Format("Profile_IssuesSummaryFormat", errors.Count)
            : Localizer["Profile_IssuesNone"];
    }

    /// <summary>选中段点表的问题（点太少、Z 不递增、没排满段长），写在表格下面。</summary>
    [ObservableProperty]
    private string pointTableErrorText = string.Empty;

    private string DescribeSymmetryFailure(SymmetryResult result) => result.Failure switch
    {
        SymmetryFailure.DoesNotReachCenter => Localizer.Format("Profile_Issue_SymmetryReachFormat", result.MissingMm),
        SymmetryFailure.CenterNotSelfSymmetric => Localizer["Profile_Issue_SymmetryCenter"],
        SymmetryFailure.UnsupportedType => Localizer["Profile_Issue_SymmetryType"],
        _ => Localizer["Profile_Issue_NoSegments"],
    };

    private string DescribeIssue(ProfileIssue issue, IDictionary<string, string> selectedSegmentParameterErrors)
    {
        switch (issue.Kind)
        {
            case ProfileIssueKind.NoSegments:
                return Localizer["Profile_Issue_NoSegments"];

            case ProfileIssueKind.OutsideBody:
                return Localizer.Format(
                    "Profile_Issue_OutsideBodyFormat", issue.SegmentOrder!, issue.FromMm, issue.ToMm, this.geometry.BodyLengthMm);

            case ProfileIssueKind.NotCovered:
                return Localizer.Format("Profile_Issue_NotCoveredFormat", issue.FromMm, issue.ToMm);

            case ProfileIssueKind.Overlap:
                return Localizer.Format(
                    "Profile_Issue_OverlapFormat", issue.SegmentOrder!, issue.OtherSegmentOrder!, issue.FromMm, issue.ToMm);

            case ProfileIssueKind.BoundaryJump:
                return Localizer.Format(
                    "Profile_Issue_BoundaryJumpFormat", issue.OtherSegmentOrder!, issue.SegmentOrder!, issue.FromMm, issue.JumpMicrometer);

            default:
                var violation = new ViolationRowViewModel(issue.Violation!, Localizer);
                if (issue.SegmentOrder == SelectedSegment?.Order)
                {
                    selectedSegmentParameterErrors[issue.Violation!.ParameterKey] = violation.ReasonText;
                }

                return Localizer.Format(
                    "Profile_Issue_ParameterFormat", issue.SegmentOrder!, violation.ParameterText, violation.ReasonText);
        }
    }

    private void RefreshPreview()
    {
        ComposedPoints = Array.Empty<(double, double)>();
        BoundaryZs = Array.Empty<double>();
        if (this.composite is not null)
        {
            try
            {
                RollProfile composed = this.composite.Compose(this.geometry, this.profileTypes, this.settings.ProfileSampleCount);
                ComposedPoints = composed.Points
                    .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMm(point.RadiusOffsetMm)))
                    .ToArray();
                BoundaryZs = this.composite.Segments.Skip(1).Select(segment => segment.FromMm).ToArray();
            }
            catch (DomainException ex)
            {
                Alarms.RaiseException(ex);
            }
        }

        RefreshSelectionPreview();
        RefreshReferenceDeviation();
    }

    /// <summary>当前段加粗那一截、点表的原始点。只动选中相关的部分，然后通知视图重画。</summary>
    private void RefreshSelectionPreview()
    {
        SelectedSegmentPoints = Array.Empty<(double, double)>();
        TablePoints = Array.Empty<(double, double)>();

        if (this.composite is not null && SelectedSegment is not null && SelectedSegment.Order <= this.composite.Segments.Count)
        {
            RollProfileSegment selected = this.composite.Segments[SelectedSegment.Order - 1];
            RollProfile segmentOnly = CompositeRollProfile
                .Sequential(selected.FromMm, new[] { SequentialSegment.From(selected) })
                .Compose(RollGeometry.Create(Math.Max(this.geometry.BodyLengthMm, selected.ToMm), this.geometry.NominalRadiusMm), this.profileTypes, this.settings.ProfileSampleCount);
            SelectedSegmentPoints = segmentOnly.Points
                .Where(point => point.BodyPositionMm >= selected.FromMm && point.BodyPositionMm <= selected.ToMm)
                .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMm(point.RadiusOffsetMm)))
                .ToArray();

            if (selected.ProfileTypeKey == ProfileTypeKeys.PointTable
                && selected.Parameters.TryGet(PointTableProfileType.PointsKey, out ParameterValue? points) && points is not null)
            {
                TablePoints = points.Points
                    .Select(point => (
                        selected.IsMirrored ? selected.ToMm - point.X : selected.FromMm + point.X,
                        UnitConversion.MicrometerToMm(point.Y)))
                    .ToArray();
            }
        }

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
        if (this.composite is null)
        {
            return 0.0;
        }

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
