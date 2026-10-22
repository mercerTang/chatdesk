using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ChatServer.Data;

/// <summary>
/// SQLite 无法对 DateTimeOffset 排序，统一存成 Unix 毫秒（INTEGER）：
/// 既能走索引 / ORDER BY，读回仍是带时区的 DateTimeOffset。
/// </summary>
public class DateTimeOffsetToUnixMillisecondsConverter : ValueConverter<DateTimeOffset, long>
{
    public DateTimeOffsetToUnixMillisecondsConverter()
        : base(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value))
    {
    }
}

public class NullableDateTimeOffsetToUnixMillisecondsConverter : ValueConverter<DateTimeOffset?, long?>
{
    public NullableDateTimeOffsetToUnixMillisecondsConverter()
        : base(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : (long?)null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : (DateTimeOffset?)null)
    {
    }
}
