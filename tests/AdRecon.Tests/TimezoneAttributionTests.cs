using AdRecon.Api.Services;

namespace AdRecon.Tests;

/// <summary>曝光按广告位所属时区归日。</summary>
public class TimezoneAttributionTests
{
    [Fact]
    public void NewYorkHourBeforeLocalMidnight_BelongsToPreviousDay()
    {
        // UTC 2026-08-02 02:00 = 纽约(EDT, UTC-4) 2026-08-01 22:00 → 归日 8/1
        var date = ServiceDateCalculator.ToServiceDate(
            new DateTime(2026, 8, 2, 2, 0, 0, DateTimeKind.Utc), "America/New_York");
        Assert.Equal(new DateOnly(2026, 8, 1), date);
    }

    [Fact]
    public void ShanghaiHourBeforeUtcMidnight_BelongsToNextDay()
    {
        // UTC 2026-08-01 17:00 = 上海(UTC+8) 2026-08-02 01:00 → 归日 8/2
        var date = ServiceDateCalculator.ToServiceDate(
            new DateTime(2026, 8, 1, 17, 0, 0, DateTimeKind.Utc), "Asia/Shanghai");
        Assert.Equal(new DateOnly(2026, 8, 2), date);
    }

    [Fact]
    public void LondonSummerTime_UsesDstOffset()
    {
        // UTC 2026-08-01 23:30 = 伦敦(BST, UTC+1) 2026-08-02 00:30 → 归日 8/2
        var date = ServiceDateCalculator.ToServiceDate(
            new DateTime(2026, 8, 1, 23, 30, 0, DateTimeKind.Utc), "Europe/London");
        Assert.Equal(new DateOnly(2026, 8, 2), date);
    }

    [Fact]
    public void LondonWinterTime_UsesStandardOffset()
    {
        // UTC 2026-01-01 23:30 = 伦敦(GMT, UTC+0) 2026-01-01 23:30 → 归日不变
        var date = ServiceDateCalculator.ToServiceDate(
            new DateTime(2026, 1, 1, 23, 30, 0, DateTimeKind.Utc), "Europe/London");
        Assert.Equal(new DateOnly(2026, 1, 1), date);
    }

    [Fact]
    public void DedupKey_IsSourceSlotHour()
    {
        var key = ServiceDateCalculator.DedupKey(
            "ADX-LOG", "SH-OPENSCREEN", new DateTime(2026, 8, 1, 13, 0, 0, DateTimeKind.Utc));
        Assert.Equal("ADX-LOG|SH-OPENSCREEN|2026-08-01T13", key);
    }
}
