using System.Globalization;

namespace WhalePod.Web;

public static class DisplayFormat
{
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    public static string Time(DateTimeOffset? value)
    {
        return value?.ToOffset(ChinaOffset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";
    }

    public static string State(string state)
    {
        return state switch
        {
            "active" => "有效",
            "disabled" => "已停用",
            "exhausted" => "已用尽",
            "expired" => "已过期",
            _ => state
        };
    }

    public static string TodayInChina()
    {
        return DateTimeOffset.UtcNow.ToOffset(ChinaOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}