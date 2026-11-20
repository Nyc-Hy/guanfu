namespace Guanfu.Application.Contracts;

/// <summary>评审协调者提交的本机证据，按轮次保存。旧轮次可以没有这份证据。</summary>
public sealed record ReviewReportEvidence(
    string? Baseline,
    string? Revision,
    string? DatabaseChange,
    IReadOnlyList<ReviewFinding>? Findings,
    IReadOnlyList<RequirementCheck>? Requirements,
    IReadOnlyList<RequirementDiff>? Changes,
    IReadOnlyList<ReviewTestCase>? TestCases,
    IReadOnlyList<ReviewCheck>? Checks = null);

public sealed record ReviewCheck(string Category, string Status, string Summary, string? Evidence = null);

public sealed record ReviewFinding(
    string Key, string Category, string Severity, string Status, string Title,
    string Evidence, string? Location = null, string? Verification = null,
    IReadOnlyList<string>? RelatedCategories = null);

public sealed record RequirementCheck(
    string Key, string Requirement, string Source, string Status,
    string? Implementation = null, string? Evidence = null);

public sealed record RequirementDiff(
    string? RequirementKey, string Path, string Change, string? Reference = null,
    string? Patch = null, bool Shared = false);

public sealed record ReviewTestCase(
    string Name, string? RequirementKey, string Status, string? Evidence = null, string? Environment = null);

public sealed record ReviewEvidenceSnapshot(
    int Version, ReviewReportEvidence Evidence, DateTimeOffset RecordedAt, string? RecordedBy = null);

/// <summary>SQL 清单按数组顺序记录执行步骤。各阶段独立编号。</summary>
public sealed record ReleasePackageOptions(IReadOnlyList<ReleaseSqlStep>? SqlSteps = null, string? Notes = null);

public sealed record ReleaseSqlStep(Guid ItemId, string Phase, string Target, int? ArtifactVersion = null);
