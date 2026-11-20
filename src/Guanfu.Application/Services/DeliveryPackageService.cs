using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>
/// 交付包的落盘位置。包要留在数据目录里 —— 打完之后还能再下载，
/// 也能事后查「发出去的是哪一版」。
/// </summary>
public interface IDeliveryPackageStore
{
    Task<StoredFile> SaveAsync(
        Guid milestoneId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default);

    string ResolveAbsolutePath(string relativePath);
}

/// <summary>
/// 交付打包：把一个迭代版本要发出去的东西打成一个 zip + 一份清单。
///
/// 为什么要有这一步：**归档不等于交付**。
/// 归档是「过程里攒下了什么」，交付是「这个版本发出去的是哪些」。
/// 项管拿到的应该是后者 —— 一个能直接转发的包，而不是让人自己去看板里翻。
/// </summary>
public interface IDeliveryPackageService
{
    /// <summary>这个迭代版本下能打包的东西（候选池）。</summary>
    Task<DeliveryCandidatesDto> ListCandidatesAsync(
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    /// <summary>勾选之后打包。返回新包。</summary>
    Task<DeliveryPackageDto> CreateAsync(
        Guid milestoneId,
        IReadOnlyList<Guid> itemIds,
        string? createdBy = null,
        CancellationToken cancellationToken = default,
        ReleasePackageOptions? release = null);

    /// <summary>这个迭代版本打过的包（新到旧）。</summary>
    Task<IReadOnlyList<DeliveryPackageDto>> ListAsync(
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    Task<DeliveryPackageDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 签发一个分享链接（交付的最后一公里）。
    ///
    /// 包打好了，项管没有观复账号 —— 他要的是一个能点的链接。
    /// 明文链接**只在这里返回一次**（库里只存哈希）。
    /// </summary>
    Task<DeliveryShareDto> CreateShareAsync(
        Guid id,
        int validDays = 7,
        CancellationToken cancellationToken = default);

    /// <summary>撤销分享链接（发错了、或者东西不该外传）。</summary>
    Task<bool> RevokeShareAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 用分享令牌找到包（公开下载用）。
    ///
    /// 令牌认不出、过期、被撤销 —— 都返回 null（不解释是哪一种）。
    /// </summary>
    Task<(string FileName, string Path)?> ResolveShareAsync(string? token, CancellationToken cancellationToken = default);

    /// <summary>包在磁盘上的绝对路径。</summary>
    Task<string> ResolvePathAsync(DeliveryPackageDto package, CancellationToken cancellationToken = default);
}

public sealed class DeliveryPackageService(
    IAppDbContext db,
    IDocumentStore documents,
    IDeliveryPackageStore store,
    IClock clock,
    ICurrentMemberScope? currentMember = null,
    IReviewService? reviews = null) : IDeliveryPackageService
{
    /// <summary>当前身份（没人登录 = 单人本机模式 = 不过滤）。</summary>
    private MemberScope? Scope => currentMember?.Current;
    /// <summary>包里清单文件的名字（固定，人一看就知道那是清单）。</summary>
    private const string ManifestFileName = "清单.md";

    private const string MachineManifestFileName = "manifest.json";

    public async Task<DeliveryCandidatesDto> ListCandidatesAsync(
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        var milestone = await db.Milestones.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == milestoneId, cancellationToken)
            ?? throw new NotFoundException("里程碑", milestoneId);

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == milestone.ProjectId, cancellationToken);

        var tasks = await LoadScopeTasksAsync(milestoneId, cancellationToken);

        var candidates = new List<DeliveryCandidateDto>();

        if (tasks.Count > 0)
        {
            var taskIds = tasks.Select(t => t.Id).ToList();

            var docs = await db.Documents.AsNoTracking()
                .Where(d => taskIds.Contains(d.TaskId))
                .ToListAsync(cancellationToken);

            foreach (var doc in docs)
            {
                var task = tasks.First(t => t.Id == doc.TaskId);
                candidates.Add(new DeliveryCandidateDto(
                    doc.Id,
                    "document",
                    doc.FileName,
                    doc.FileName,
                    doc.Kind,
                    doc.SizeBytes,
                    task.Key ?? task.Title,
                    task.Title,
                    true,
                    IsSqlFile(doc.FileName)));
            }

            var artifacts = await db.Artifacts.AsNoTracking()
                .Where(a => taskIds.Contains(a.TaskId))
                .ToListAsync(cancellationToken);

            foreach (var artifact in artifacts)
            {
                // 只有内容在观复这边的产物能进包。
                // 只留了外部路径的（文件在别人机器上）打包进去只会得到一个坏链接。
                if (string.IsNullOrWhiteSpace(artifact.Content))
                {
                    continue;
                }

                var task = tasks.First(t => t.Id == artifact.TaskId);
                candidates.Add(new DeliveryCandidateDto(
                    artifact.Id,
                    "artifact",
                    artifact.Title,
                    GuessFileName(artifact),
                    UiText.ArtifactTypeLabel(artifact.Type),
                    Encoding.UTF8.GetByteCount(artifact.Content ?? string.Empty),
                    task.Key ?? task.Title,
                    task.Title,
                    true,
                    IsSqlArtifact(artifact),
                    artifact.Revision));
            }
        }

        return new DeliveryCandidatesDto(
            milestone.Id,
            milestone.Name,
            project?.Name ?? "未命名项目",
            candidates);
    }

    public async Task<DeliveryPackageDto> CreateAsync(
        Guid milestoneId,
        IReadOnlyList<Guid> itemIds,
        string? createdBy = null,
        CancellationToken cancellationToken = default,
        ReleasePackageOptions? release = null)
    {
        EnsureCanManage();

        if (itemIds is null || (itemIds.Count == 0 && release is null))
        {
            throw new GuanfuException("没有勾选任何东西，打出来的包是空的。");
        }

        var milestone = await db.Milestones.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == milestoneId, cancellationToken)
            ?? throw new NotFoundException("里程碑", milestoneId);

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == milestone.ProjectId, cancellationToken);

        var tasks = await LoadScopeTasksAsync(milestoneId, cancellationToken);
        if (release is not null && tasks.Count == 0)
        {
            throw new GuanfuException("这个迭代版本还没有可汇总的任务。");
        }
        var taskIds = tasks.Select(t => t.Id).ToList();

        var selected = itemIds.Distinct().ToList();

        var docs = await db.Documents.AsNoTracking()
            .Where(d => selected.Contains(d.Id) && taskIds.Contains(d.TaskId))
            .ToListAsync(cancellationToken);

        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => selected.Contains(a.Id) && taskIds.Contains(a.TaskId))
            .ToListAsync(cancellationToken);

        if (selected.Count > 0 && docs.Count + artifacts.Count == 0)
        {
            throw new GuanfuException(
                "勾选的条目一个也没找到 —— 可能它们不属于这个迭代版本，或者已经被删了。");
        }

        if (release is not null && docs.Count + artifacts.Count(a => !string.IsNullOrWhiteSpace(a.Content)) != selected.Count)
        {
            throw new GuanfuException("有条目已失效、没有内容或不属于此版本，请刷新候选池后重新确认。");
        }

        var sqlPlan = BuildSqlPlan(release, docs, artifacts);
        var sqlScripts = new List<PackagedSqlStep>();
        var now = clock.UtcNow;
        var who = string.IsNullOrWhiteSpace(createdBy) ? "human" : createdBy.Trim();
        var taskById = tasks.ToDictionary(t => t.Id);

        var items = new List<DeliveryPackageItemDto>();
        var entries = new List<(string EntryName, byte[] Bytes)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in docs)
        {
            var task = taskById[doc.TaskId];
            var bytes = await ReadDocumentBytesAsync(doc, cancellationToken);
            var entryName = EntryPath(doc.Id, task, doc.FileName, "文档");
            AddEntry("文档", doc.FileName, entryName, bytes, task);
        }

        foreach (var artifact in artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Content))
            {
                continue;
            }

            var task = taskById[artifact.TaskId];
            var entryName = EntryPath(artifact.Id, task, GuessFileName(artifact), "产物");
            var bytes = Encoding.UTF8.GetBytes(artifact.Content!);
            AddEntry(UiText.ArtifactTypeLabel(artifact.Type), artifact.Title, entryName, bytes, task, artifact.Revision);
        }

        List<ReleaseReportTask>? releaseTasks = null;
        if (release is not null)
        {
            releaseTasks = await LoadReleaseTasksAsync(tasks, cancellationToken);
            foreach (var taskReport in releaseTasks)
            {
                IReadOnlyList<SpecItemDto> specItems = taskReport.Round is not null && reviews is not null
                    ? await reviews.GetSpecItemsAsync(taskReport.Id, taskReport.Round.Round, cancellationToken)
                    : [];
                var html = ReviewReportHtml.Task(taskReport.Name, taskReport.Round, taskReport.TestRun, specItems,
                    taskReport.PreviousRound);
                AddEntry("任务评审报告", taskReport.Name, taskReport.ReportPath, Encoding.UTF8.GetBytes(html), taskById[taskReport.Id]);
            }

            sqlScripts = sqlScripts.OrderBy(s => s.Phase switch { "deploy" => 0, "verify" => 1, _ => 2 })
                .ThenBy(s => s.Order).ToList();
            var title = $"{project?.Name ?? "未命名项目"} · {milestone.Name} · 发版总览";
            var overview = ReviewReportHtml.Release(title, now.ToUniversalTime(), milestone.AcceptedAt,
                milestone.AcceptedBy, releaseTasks, sqlScripts);
            AddEntry("版本总览", title, "index.html", Encoding.UTF8.GetBytes(overview));
            AddEntry("发布说明", "发布说明", "发布说明.md",
                Encoding.UTF8.GetBytes(BuildReleaseNotes(milestone.Name, sqlScripts, release.Notes)));
        }

        if (entries.Count == 0)
        {
            throw new GuanfuException("勾选的条目里没有能装进包的内容。");
        }

        var manifest = BuildManifest(project?.Name ?? "未命名项目", milestone.Name, who, now, items);
        var machine = GuanfuJson.Serialize(new
        {
            project = project?.Name,
            milestone = milestone.Name,
            createdBy = who,
            createdAt = now,
            release = releaseTasks is null ? null : new
            {
                acceptedAt = milestone.AcceptedAt,
                acceptedBy = milestone.AcceptedBy,
                tasks = releaseTasks.Select(t => new
                {
                    taskId = t.Id, task = t.Name, report = t.ReportPath, status = t.Status,
                    reviewRound = t.Round?.Round, reviewEvidenceVersion = t.Round?.ReportEvidence?.Version,
                    revision = t.Round?.ReportEvidence?.Evidence.Revision ?? t.Round?.CommitHash,
                    baseline = t.Round?.ReportEvidence?.Evidence.Baseline,
                    testRunId = t.TestRun?.Id
                }),
                sql = sqlScripts
            },
            items = items.Select(i => new
            {
                kind = i.Kind,
                title = i.Title,
                path = i.FileName,
                taskKey = i.TaskKey,
                taskTitle = i.TaskTitle,
                sizeBytes = i.SizeBytes,
                artifactVersion = i.ArtifactVersion,
                sha256 = i.Sha256
            })
        });

        var fileName = BuildFileName(release is null ? milestone.Name : $"{milestone.Name}-发版工具包", now);

        byte[] zipBytes;
        using (var buffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (entryName, bytes) in entries)
                {
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(bytes, cancellationToken);
                }

                var manifestEntry = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
                await using (var stream = manifestEntry.Open())
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(manifest), cancellationToken);
                }

                var machineEntry = archive.CreateEntry(MachineManifestFileName, CompressionLevel.Optimal);
                await using (var stream = machineEntry.Open())
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(machine), cancellationToken);
                }
            }

            zipBytes = buffer.ToArray();
        }

        var saved = await store.SaveAsync(
            milestoneId,
            fileName,
            new MemoryStream(zipBytes, writable: false),
            cancellationToken);

        var involvedTaskIds = releaseTasks?.Select(t => t.Id).ToList() ?? docs.Select(d => d.TaskId)
            .Concat(artifacts.Where(a => !string.IsNullOrWhiteSpace(a.Content)).Select(a => a.TaskId))
            .Distinct()
            .ToList();

        var package = new DeliveryPackage
        {
            ProjectId = milestone.ProjectId,
            MilestoneId = milestoneId,
            FileName = fileName,
            StoredPath = saved.RelativePath,
            SizeBytes = saved.SizeBytes,
            ItemCount = items.Count,
            Items = GuanfuJson.Serialize(items),
            TaskIds = GuanfuJson.Serialize(involvedTaskIds),
            Manifest = manifest,
            CreatedBy = who,
            CreatedAt = now
        };

        db.DeliveryPackages.Add(package);

        // 留痕：打进去的东西来自哪些任务，就给哪些任务记一笔。
        // 「这个任务的产物被发进哪个包了」事后要能查得到。
        foreach (var taskId in involvedTaskIds)
        {
            db.StatusEvents.Add(new StatusEvent
            {
                TaskId = taskId,
                FromStatus = null,
                ToStatus = null,
                Source = TaskEventSource.Human,
                Actor = who,
                Note = $"打了一个交付包（{milestone.Name}）",
                EventType = TaskEventTypes.DeliveryPacked,
                Payload = GuanfuJson.Serialize(new
                {
                    packageId = package.Id,
                    milestoneId,
                    milestoneName = milestone.Name,
                    fileName,
                    itemCount = items.Count,
                    sizeBytes = saved.SizeBytes
                }),
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        string EntryPath(Guid sourceId, TaskItem task, string fileName, string directory)
        {
            var name = $"{Prefix(task)}-{Sanitize(fileName)}";
            if (!sqlPlan.TryGetValue(sourceId, out var script))
            {
                return UniqueName(used, $"{directory}/{name}");
            }
            if (!IsSqlFile(name))
            {
                name += ".sql";
            }
            var path = UniqueName(used, $"sql/{script.Step.Phase}/{script.Order:D3}-{name}");
            sqlScripts.Add(new PackagedSqlStep(sourceId, task.Id, task.Key ?? task.Title,
                script.Step.Phase, script.Order, script.Step.Target.Trim(), path));
            return path;
        }

        void AddEntry(string kind, string title, string path, byte[] bytes, TaskItem? task = null, int? revision = null)
        {
            entries.Add((path, bytes));
            items.Add(new DeliveryPackageItemDto(kind, title, path, task?.Key ?? task?.Title ?? "—", task?.Title ?? "—",
                bytes.LongLength, revision, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }

        return ToDto(package, items);
    }

    public async Task<IReadOnlyList<DeliveryPackageDto>> ListAsync(
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.DeliveryPackages.AsNoTracking()
            .Where(p => p.MilestoneId == milestoneId)
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .ToListAsync(cancellationToken);

        var scope = Scope;
        if (scope is null || scope.IsAdmin)
        {
            return rows.Select(p => ToDto(p, ParseItems(p.Items))).ToList();
        }

        var sources = rows.Select(p => (Package: p, TaskIds: ReadTaskIds(p))).ToList();
        var taskIds = sources.SelectMany(p => p.TaskIds).Distinct().ToArray();
        var visibleTaskIds = await LoadVisibleTaskIdsAsync(scope, taskIds, cancellationToken);

        return sources
            .Where(p => p.TaskIds.Length > 0 && p.TaskIds.All(visibleTaskIds.Contains))
            .Select(p => ToDto(p.Package, ParseItems(p.Package.Items)))
            .ToList();
    }

    public async Task<DeliveryPackageDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await db.DeliveryPackages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException("交付包", id);

        await EnsureCanReadAsync(row, cancellationToken);
        return ToDto(row, ParseItems(row.Items));
    }

    public async Task<DeliveryShareDto> CreateShareAsync(
        Guid id,
        int validDays = 7,
        CancellationToken cancellationToken = default)
    {
        EnsureCanManage();
        var package = await db.DeliveryPackages
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException("交付包", id);

        var token = GenerateToken();
        var now = clock.UtcNow;

        package.ShareTokenHash = Hash(token);
        package.ShareExpiresAt = now.AddDays(Math.Clamp(validDays, 1, 90));
        package.ShareRevokedAt = null;

        await db.SaveChangesAsync(cancellationToken);

        return new DeliveryShareDto(
            package.Id,
            token,
            package.ShareExpiresAt,
            package.FileName);
    }

    public async Task<bool> RevokeShareAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureCanManage();
        var package = await db.DeliveryPackages
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (package is null || package.ShareTokenHash is null)
        {
            return false;
        }

        package.ShareRevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<(string FileName, string Path)?> ResolveShareAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var presented = Hash(token.Trim());

        var package = await db.DeliveryPackages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ShareTokenHash == presented, cancellationToken);

        if (package is null || package.ShareRevokedAt is not null)
        {
            return null;
        }

        if (package.ShareExpiresAt is DateTimeOffset expires && expires <= clock.UtcNow)
        {
            return null;
        }

        return (package.FileName, store.ResolveAbsolutePath(package.StoredPath));
    }

    public async Task<string> ResolvePathAsync(
        DeliveryPackageDto package,
        CancellationToken cancellationToken = default)
    {
        var row = await db.DeliveryPackages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == package.Id, cancellationToken)
            ?? throw new NotFoundException("交付包", package.Id);

        await EnsureCanReadAsync(row, cancellationToken);
        return store.ResolveAbsolutePath(row.StoredPath);
    }

    private void EnsureCanManage()
    {
        if (Scope is { IsAdmin: false })
        {
            throw new GuanfuException("需要管理员权限才能打包或管理分享链接。");
        }
    }

    private async Task EnsureCanReadAsync(DeliveryPackage package, CancellationToken cancellationToken)
    {
        var scope = Scope;
        if (scope is null || scope.IsAdmin)
        {
            return;
        }

        var taskIds = ReadTaskIds(package);
        var visibleTaskIds = await LoadVisibleTaskIdsAsync(scope, taskIds, cancellationToken);

        if (taskIds.Length == 0 || !taskIds.All(visibleTaskIds.Contains))
        {
            throw new NotFoundException("交付包", package.Id);
        }
    }

    private async Task<HashSet<Guid>> LoadVisibleTaskIdsAsync(
        MemberScope scope, Guid[] taskIds, CancellationToken cancellationToken)
    {
        if (taskIds.Length == 0)
        {
            return [];
        }

        var ids = await db.Tasks.AsNoTracking()
            .VisibleTo(scope)
            .Where(t => taskIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    // 存量包缺少完整的来源记录，只对管理员与 owner 开放。
    private static Guid[] ReadTaskIds(DeliveryPackage package)
    {
        if (package.TaskIds is null)
        {
            return [];
        }

        try
        {
            return GuanfuJson.Deserialize<Guid[]>(package.TaskIds) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<byte[]> ReadDocumentBytesAsync(Document doc, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(doc.StoredPath))
        {
            var absolute = documents.ResolveAbsolutePath(doc.StoredPath);
            try
            {
                return await File.ReadAllBytesAsync(absolute, cancellationToken);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // 原件可能在查看候选项和生成包之间被移走。
            }
        }

        throw new GuanfuException($"无法打包：所选文档《{doc.FileName}》的原件缺失。请恢复原件或取消勾选后重试。");
    }

    /// <summary>这个里程碑下面（含子里程碑）的任务。</summary>
    private async Task<List<TaskItem>> LoadScopeTasksAsync(Guid milestoneId, CancellationToken cancellationToken)
    {
        var all = await db.Milestones.AsNoTracking()
            .Select(m => new { m.Id, m.ParentId })
            .ToListAsync(cancellationToken);

        var scope = new HashSet<Guid> { milestoneId };
        var grew = true;

        while (grew)
        {
            grew = false;
            foreach (var milestone in all)
            {
                if (milestone.ParentId is Guid parent &&
                    scope.Contains(parent) &&
                    scope.Add(milestone.Id))
                {
                    grew = true;
                }
            }
        }

        // 可见性：候选池只能圈**看得见**的活 ——
        // 否则成员能把别人的产物打进交付包发出去。
        return await db.Tasks.AsNoTracking()
            .VisibleTo(Scope)
            .Where(t => t.MilestoneId != null && scope.Contains(t.MilestoneId!.Value))
            .Where(t => !t.IsArchived)
            .OrderBy(t => t.Key)
            .ToListAsync(cancellationToken);
    }

    private static bool IsSqlFile(string name) => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    private static bool IsSqlArtifact(Artifact artifact) =>
        artifact.Type == ArtifactType.SqlScript || IsSqlFile(GuessFileName(artifact));

    private static Dictionary<Guid, (ReleaseSqlStep Step, int Order)> BuildSqlPlan(
        ReleasePackageOptions? release, IReadOnlyList<Document> documents, IReadOnlyList<Artifact> artifacts)
    {
        var plan = new Dictionary<Guid, (ReleaseSqlStep Step, int Order)>();
        if (release is null)
        {
            return plan;
        }

        var sqlIds = documents.Where(d => IsSqlFile(d.FileName)).Select(d => d.Id)
            .Concat(artifacts.Where(IsSqlArtifact).Select(a => a.Id)).ToHashSet();
        var revisions = artifacts.ToDictionary(a => a.Id, a => a.Revision);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in release.SqlSteps ?? [])
        {
            if (step is null || !sqlIds.Contains(step.ItemId) ||
                step.Phase is not ("deploy" or "verify" or "rollback") ||
                string.IsNullOrWhiteSpace(step.Target) || step.Target.Length > 200)
            {
                throw new GuanfuException("SQL 清单只能引用已勾选的脚本，每项须填写阶段和目标数据库。");
            }
            if (step.ArtifactVersion is int version && revisions.GetValueOrDefault(step.ItemId) != version)
            {
                throw new GuanfuException("SQL 产物版本已更新，请刷新候选池并重新确认脚本内容。");
            }

            var order = counts.GetValueOrDefault(step.Phase) + 1;
            counts[step.Phase] = order;
            if (!plan.TryAdd(step.ItemId, (step, order)))
            {
                throw new GuanfuException("SQL 清单里有重复的脚本。");
            }
        }

        if (plan.Count != sqlIds.Count)
        {
            throw new GuanfuException("请为所有勾选的 SQL 确认阶段、顺序和目标数据库。");
        }

        if (release.Notes?.Length > 32_000)
        {
            throw new GuanfuException("发布说明最多 32000 字符。");
        }

        return plan;
    }

    private async Task<List<ReleaseReportTask>> LoadReleaseTasksAsync(
        IReadOnlyList<TaskItem> tasks, CancellationToken cancellationToken)
    {
        var taskIds = tasks.Select(t => t.Id).ToList();
        var rounds = await db.ReviewRounds.AsNoTracking().Include(r => r.Items)
            .Where(r => taskIds.Contains(r.TaskId))
            .Where(r => db.ReviewRounds.Count(next => next.TaskId == r.TaskId && next.Round > r.Round) < 2)
            .OrderByDescending(r => r.Round).ToListAsync(cancellationToken);
        var roundsByTask = rounds.ToLookup(r => r.TaskId);
        var tests = await db.TestRuns.AsNoTracking().Where(t => taskIds.Contains(t.TaskId))
            .GroupBy(t => t.TaskId)
            .Select(group => group.OrderByDescending(t => t.ReportedAt).ThenByDescending(t => t.Id).First())
            .ToListAsync(cancellationToken);
        var testsByTask = tests.ToDictionary(t => t.TaskId);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return tasks.Select(t =>
        {
            var taskRounds = roundsByTask[t.Id].Select(r => ReviewService.ToDto(r)).ToList();
            return new ReleaseReportTask(t.Id, $"{t.Key} {t.Title}".Trim(), t.Status.ToString(),
                UniqueName(paths, $"reports/{Prefix(t)}.html"),
                taskRounds.FirstOrDefault(), testsByTask.GetValueOrDefault(t.Id), taskRounds.ElementAtOrDefault(1));
        }).ToList();
    }

    private static string BuildReleaseNotes(
        string milestone, IReadOnlyList<PackagedSqlStep> scripts, string? notes)
    {
        var text = new StringBuilder().AppendLine($"# {milestone} · 发布说明").AppendLine();
        if (!string.IsNullOrWhiteSpace(notes))
        {
            text.AppendLine(notes.Trim()).AppendLine();
        }

        text.AppendLine("## 发布步骤").AppendLine()
            .AppendLine("1. 打开 index.html，核对版本验收状态、任务评审、测试和数据库变更。")
            .AppendLine("2. 按团队发布流程核对目标环境、备份和回滚条件，再按清单执行上线步骤。")
            .AppendLine("3. 核对上线结果，执行核验步骤，记录结果。")
            .AppendLine("4. 触发回滚条件时，按确认的回滚清单和团队恢复流程处理。")
            .AppendLine()
            .AppendLine("## SQL 执行清单").AppendLine()
            .AppendLine("| 阶段 | 顺序 | 目标数据库 | 来源任务 | 路径 |")
            .AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var step in scripts)
        {
            text.AppendLine($"| {ReviewReportHtml.Phase(step.Phase)} | {step.Order} | {Escape(step.Target)} | {Escape(step.TaskKey)} | {Escape(step.Path)} |");
        }
        if (scripts.Count == 0)
        {
            text.AppendLine().AppendLine("此包没有纳入 SQL。");
        }
        else if (!scripts.Any(s => s.Phase == "rollback"))
        {
            text.AppendLine().AppendLine("此包未附回滚脚本。发布前请确认恢复方式。");
        }

        return text.ToString();
    }

    private static string BuildManifest(
        string projectName,
        string milestoneName,
        string who,
        DateTimeOffset now,
        IReadOnlyList<DeliveryPackageItemDto> items)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# 交付清单");
        sb.AppendLine();
        sb.AppendLine($"- 项目：{projectName}");
        sb.AppendLine($"- 迭代版本：{milestoneName}");
        sb.AppendLine($"- 打包时间：{now.ToLocalTime():yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- 打包人：{who}");
        sb.AppendLine($"- 条目数：{items.Count}");
        sb.AppendLine();
        sb.AppendLine("| 类别 | 名称 | 包内路径 | 来自任务 | 大小（字节） |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var item in items)
        {
            sb.AppendLine($"| {item.Kind} | {Escape(item.Title)} | {item.FileName} | {Escape(item.TaskKey)} | {item.SizeBytes} |");
        }

        sb.AppendLine();
        sb.AppendLine("> 本清单与包同时生成；包是打包那一刻的快照，之后的改动不会自动进去。");

        return sb.ToString();
    }

    private static string BuildFileName(string milestoneName, DateTimeOffset now)
    {
        var safe = Sanitize(milestoneName);
        return $"{safe}-{now:yyyyMMdd-HHmm}.zip";
    }

    private static string Prefix(TaskItem task)
    {
        var key = string.IsNullOrWhiteSpace(task.Key) ? "任务" : task.Key;
        return Sanitize(key);
    }

    /// <summary>产物在包里的文件名：标题 + 原来的扩展名（脚本类要保留 .sql 才好认）。</summary>
    private static string GuessFileName(Artifact artifact)
    {
        var title = string.IsNullOrWhiteSpace(artifact.Title) ? "产物" : artifact.Title.Trim();

        if (!string.IsNullOrWhiteSpace(artifact.ExternalPath))
        {
            var external = Path.GetFileName(artifact.ExternalPath);
            if (!string.IsNullOrWhiteSpace(external))
            {
                var extension = Path.GetExtension(external);
                if (!string.IsNullOrEmpty(extension) && !title.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return title + extension;
                }
            }
        }

        return title;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) || char.IsControl(ch) || "<>:\"/\\|?*".Contains(ch) ? '_' : ch);
        }

        var cleaned = builder.ToString().Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "未命名" : cleaned;
    }

    private static string UniqueName(HashSet<string> used, string name)
    {
        if (used.Add(name))
        {
            return name;
        }

        var directory = Path.GetDirectoryName(name) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = string.IsNullOrEmpty(directory)
                ? $"{stem}-{i}{extension}"
                : $"{directory.Replace('\\', '/')}/{stem}-{i}{extension}";

            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        throw new GuanfuException("文件名冲突太多，打包中止。");
    }

    private static string Escape(string? text) =>
        (text ?? string.Empty).Replace("|", "\\|").Replace("\n", " ");

    private static IReadOnlyList<DeliveryPackageItemDto> ParseItems(string json)
    {
        try
        {
            var items = GuanfuJson.Deserialize<List<DeliveryPackageItemDto>>(json);
            return items ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>生成分享令牌（32 字节随机 → base64url）。</summary>
    private static string GenerateToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>分享令牌的哈希（库里只存它）。</summary>
    private static string Hash(string token) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static DeliveryPackageDto ToDto(
        DeliveryPackage package,
        IReadOnlyList<DeliveryPackageItemDto> items) => new(
        package.Id,
        package.ProjectId,
        package.MilestoneId,
        package.FileName,
        package.SizeBytes,
        package.ItemCount,
        items,
        package.Manifest,
        package.CreatedBy,
        package.CreatedAt,
        package.ShareExpiresAt,
        package.ShareRevokedAt);
}
