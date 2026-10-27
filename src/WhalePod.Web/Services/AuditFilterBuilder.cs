using System.Globalization;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Web.Services;

public sealed record AuditFilterInput(
    string? From,
    string? To,
    string? Actor,
    string? Action,
    string? TargetType,
    string? TargetId);

public sealed record AuditFilterBuildResult(
    AuditFilter? Filter,
    DateOnly FromDate,
    DateOnly ToDate,
    string? Error);

public static class AuditFilterBuilder
{
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    public static DateOnly TodayInChina()
    {
        return DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(ChinaOffset).DateTime);
    }

    public static AuditFilterBuildResult Build(
        AuditFilterInput input,
        DateOnly defaultFrom,
        DateOnly defaultTo)
    {
        if (!TryParseDate(input.From, defaultFrom, out var fromDate)
            || !TryParseDate(input.To, defaultTo, out var toDate))
        {
            return new AuditFilterBuildResult(null, defaultFrom, defaultTo, "invalid-date");
        }

        if (fromDate > toDate)
        {
            return new AuditFilterBuildResult(null, fromDate, toDate, "invalid-range");
        }

        if (fromDate == DateOnly.MinValue || toDate == DateOnly.MaxValue)
        {
            return new AuditFilterBuildResult(null, fromDate, toDate, "invalid-date");
        }

        Guid? targetId = null;
        if (!string.IsNullOrWhiteSpace(input.TargetId))
        {
            if (!Guid.TryParse(input.TargetId.Trim(), out var parsedTargetId))
            {
                return new AuditFilterBuildResult(null, fromDate, toDate, "invalid-target");
            }

            targetId = parsedTargetId;
        }

        var fromUtc = new DateTimeOffset(fromDate.ToDateTime(TimeOnly.MinValue), ChinaOffset).ToUniversalTime();
        var toExclusiveUtc = new DateTimeOffset(
            toDate.AddDays(1).ToDateTime(TimeOnly.MinValue),
            ChinaOffset).ToUniversalTime();

        var filter = new AuditFilter(
            input.Actor,
            input.Action,
            input.TargetType,
            targetId,
            fromUtc,
            toExclusiveUtc);

        return new AuditFilterBuildResult(filter, fromDate, toDate, null);
    }

    private static bool TryParseDate(string? value, DateOnly fallback, out DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            date = fallback;
            return true;
        }

        return DateOnly.TryParseExact(
            value.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }
}