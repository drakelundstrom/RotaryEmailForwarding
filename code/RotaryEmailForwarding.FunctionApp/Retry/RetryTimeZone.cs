namespace RotaryEmailForwarding.FunctionApp.Retry;

public static class RetryTimeZone
{
    public static TimeZoneInfo Resolve(string configuredTimeZone)
    {
        var candidates = configuredTimeZone switch
        {
            "Eastern Standard Time" => new[] { "Eastern Standard Time", "America/New_York" },
            "America/New_York" => new[] { "America/New_York", "Eastern Standard Time" },
            _ => new[] { configuredTimeZone }
        };

        foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
