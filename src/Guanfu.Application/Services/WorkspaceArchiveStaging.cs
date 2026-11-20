using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>先在目标目录所在文件系统准备完整副本，再替换受管理的条目。</summary>
public sealed class WorkspaceArchiveStaging : IDisposable
{
    private readonly string _target;
    private readonly string _work;
    private readonly string _parent;
    private bool _keepForRecovery;

    public string StagePath { get; }

    public WorkspaceArchiveStaging(string target)
    {
        _target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        _parent = Path.GetDirectoryName(_target)
            ?? throw new GuanfuException("不能将文件系统根目录作为归档目标。");
        Directory.CreateDirectory(_parent);

        _work = Path.Combine(_parent, $".guanfu-archive-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_work);
        }
        else
        {
            Directory.CreateDirectory(_work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        StagePath = Directory.CreateDirectory(Path.Combine(_work, "stage")).FullName;
    }

    public void Publish(IReadOnlyList<string> entries, CancellationToken cancellationToken, Action? validateTarget = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockPath = Path.Combine(_parent, $".{Path.GetFileName(_target)}.guanfu-archive.lock");
        var lease = Acquire(lockPath);
        try
        {
            RefuseLink(_target);
            validateTarget?.Invoke();
            if (!Directory.Exists(_target))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(StagePath, _target);
                return;
            }

            var previous = Directory.CreateDirectory(Path.Combine(_work, "previous")).FullName;
            foreach (var entry in entries)
            {
                var destination = Path.Combine(_target, entry);
                var staged = Path.Combine(StagePath, entry);
                RefuseLink(destination);
                if ((Directory.Exists(staged) && File.Exists(destination)) ||
                    (File.Exists(staged) && Directory.Exists(destination)))
                {
                    throw new GuanfuException($"归档目标中的「{entry}」类型不符，原数据仍保留。");
                }
            }

            // 到这里暂存已完成。替换和回滚期间不响应取消，防止只切换一半。
            cancellationToken.ThrowIfCancellationRequested();
            var removed = new List<string>();
            var published = new List<string>();
            try
            {
                foreach (var entry in entries)
                {
                    if (TryMove(_target, previous, entry))
                    {
                        removed.Add(entry);
                    }
                }
                foreach (var entry in entries)
                {
                    if (TryMove(StagePath, _target, entry))
                    {
                        published.Add(entry);
                    }
                }
            }
            catch (Exception publishError) when (IsFileFailure(publishError))
            {
                try
                {
                    foreach (var entry in published.AsEnumerable().Reverse())
                    {
                        MoveRequired(_target, StagePath, entry);
                    }
                    foreach (var entry in removed.AsEnumerable().Reverse())
                    {
                        MoveRequired(previous, _target, entry);
                    }
                }
                catch (Exception rollbackError) when (IsFileFailure(rollbackError))
                {
                    _keepForRecovery = true;
                    throw new GuanfuException($"归档切换失败（{publishError.Message}），回滚也未完成（{rollbackError.Message}）。原件保存在 {previous}。");
                }

                throw new GuanfuException($"归档切换失败，已恢复原文件：{publishError.Message}");
            }
        }
        finally
        {
            lease.Dispose();
            if (!_keepForRecovery)
            {
                try
                {
                    File.Delete(lockPath);
                }
                catch (Exception ex) when (IsFileFailure(ex))
                {
                    // 清理锁失败不改变已提交或已回滚的结果。
                }
            }
        }
    }

    private static FileStream Acquire(string path)
    {
        try
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            throw new GuanfuException($"无法锁定归档目标。请检查是否有其他归档操作或未完成的切换：{path}");
        }
    }

    private static bool TryMove(string source, string target, string name)
    {
        var from = Path.Combine(source, name);
        var to = Path.Combine(target, name);
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else if (File.Exists(from))
        {
            File.Move(from, to);
        }
        else
        {
            return false;
        }
        return true;
    }

    private static void MoveRequired(string source, string target, string name)
    {
        if (!TryMove(source, target, name))
        {
            throw new IOException($"找不到待回滚的「{name}」。");
        }
    }

    private static void RefuseLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null ||
            (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new GuanfuException($"归档目标含符号链接，拒绝替换：{path}");
        }
    }

    private static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

    public void Dispose()
    {
        if (_keepForRecovery)
        {
            return;
        }

        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // 临时目录只包含本次操作的副本，无法清理时保留目录供人工清理。
        }
    }
}
