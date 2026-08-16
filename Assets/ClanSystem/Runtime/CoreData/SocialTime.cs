using System;

namespace ClanSystem.CoreData
{
    /// <summary>
    /// Conversions between the Unix-millisecond timestamps used on the wire and <see cref="DateTime"/>.
    /// </summary>
    public static class SocialTime
    {
        private static readonly DateTime _epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static DateTime FromUnixMs(long milliseconds)
        {
            if (milliseconds <= 0)
            {
                return _epoch;
            }

            return _epoch.AddMilliseconds(milliseconds);
        }

        public static long ToUnixMs(DateTime value)
        {
            return (long)(value.ToUniversalTime() - _epoch).TotalMilliseconds;
        }

        /// <summary>
        /// Compact "5m ago" style label for roster and activity rows.
        /// </summary>
        public static string DescribeAge(long milliseconds)
        {
            if (milliseconds <= 0)
            {
                return "unknown";
            }

            TimeSpan age = DateTime.UtcNow - FromUnixMs(milliseconds);
            if (age.TotalSeconds < 60)
            {
                return "just now";
            }

            if (age.TotalMinutes < 60)
            {
                return ((int)age.TotalMinutes) + "m ago";
            }

            if (age.TotalHours < 24)
            {
                return ((int)age.TotalHours) + "h ago";
            }

            return ((int)age.TotalDays) + "d ago";
        }
    }
}
