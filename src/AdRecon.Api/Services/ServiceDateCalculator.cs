using NodaTime;

namespace AdRecon.Api.Services;

/// <summary>
/// 曝光归日：把 UTC 小时换算到广告位所属时区的日历日。
/// 用 NodaTime 自带 tzdb，不依赖宿主机时区数据，保证跨环境一致。
/// </summary>
public static class ServiceDateCalculator
{
    public static DateOnly ToServiceDate(DateTime hourUtc, string timeZoneId)
    {
        var utc = hourUtc.Kind switch
        {
            DateTimeKind.Utc => hourUtc,
            DateTimeKind.Local => hourUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(hourUtc, DateTimeKind.Utc)
        };
        var instant = Instant.FromDateTimeUtc(utc);
        var zone = DateTimeZoneProviders.Tzdb[timeZoneId]
            ?? throw new ArgumentException($"未知时区: {timeZoneId}");
        var localDate = instant.InZone(zone).Date;
        return new DateOnly(localDate.Year, localDate.Month, localDate.Day);
    }

    /// <summary>行级去重键：同一来源对同一广告位同一小时只允许一条事实。</summary>
    public static string DedupKey(string source, string slotCode, DateTime hourUtc)
        => $"{source}|{slotCode}|{hourUtc:yyyy-MM-dd'T'HH}";
}
