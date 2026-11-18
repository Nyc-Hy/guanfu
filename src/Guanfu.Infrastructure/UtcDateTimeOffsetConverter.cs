using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Guanfu.Infrastructure;

/// <summary>
/// DateTimeOffset → UTC DateTime。
///
/// SQLite 的 provider 明确不支持对 DateTimeOffset 列做 ORDER BY（直接抛
/// NotSupportedException: "SQLite does not support expressions of type 'DateTimeOffset'
/// in ORDER BY clauses"）。这里统一转成 UTC 的 DateTime 存储：
/// SQLite 以文本存 DateTime，字典序恰好就是时间序，排序结果正确。
/// </summary>
public sealed class UtcDateTimeOffsetConverter()
    : ValueConverter<DateTimeOffset, DateTime>(
        v => v.UtcDateTime,
        v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));

public sealed class NullableUtcDateTimeOffsetConverter()
    : ValueConverter<DateTimeOffset?, DateTime?>(
        v => v == null ? null : v.Value.UtcDateTime,
        v => v == null ? null : new DateTimeOffset(DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)));
