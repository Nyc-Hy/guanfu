namespace Workbench.Domain;

/// <summary>
/// 编号（Key）的归一化口径。
///
/// 人嘴里的「req001」和库里的「REQ-001」得认成同一个编号 ——
/// 少个连字符、大小写不同，都不该让人再猜写法。
///
/// 归一化规则：**只留字母数字并转小写**。
/// 放在 Domain 层是因为两处都要用：
/// - 应用层做「这个编号有没有被占用」的判断；
/// - 基础设施层给编号算一个归一化列，用唯一索引把并发抢号挡在数据库上。
/// 两处口径必须一致，所以只有这一份实现。
/// </summary>
public static class TaskKeys
{
    /// <summary>归一化后的编号（用于比较与唯一索引）。</summary>
    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[value.Length];
        var len = 0;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[len++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer[..len]);
    }
}
