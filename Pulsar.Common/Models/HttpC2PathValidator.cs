using System;
using System.Text.RegularExpressions;

namespace Pulsar.Common.Models
{
    public static class HttpC2PathValidator
    {
        private const int MaxPathLength = 64;
        private static readonly Regex PathPattern = new Regex("^/[A-Za-z0-9/_-]+$", RegexOptions.Compiled);

        public static HttpC2Paths Sanitize(HttpC2Paths paths, out bool changed)
        {
            changed = false;
            var source = paths ?? new HttpC2Paths();

            var sanitized = new HttpC2Paths
            {
                Open = SanitizePath(source.Open, HttpC2Defaults.Open, ref changed),
                Up = SanitizePath(source.Up, HttpC2Defaults.Up, ref changed),
                Down = SanitizePath(source.Down, HttpC2Defaults.Down, ref changed),
                Close = SanitizePath(source.Close, HttpC2Defaults.Close, ref changed)
            };

            return sanitized;
        }

        private static string SanitizePath(string path, string fallback, ref bool changed)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                changed = true;
                return fallback;
            }

            var trimmed = path.Trim();
            if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                trimmed = "/" + trimmed.TrimStart('/');
                changed = true;
            }

            if (trimmed.Length > MaxPathLength)
            {
                trimmed = trimmed.Substring(0, MaxPathLength);
                changed = true;
            }

            if (trimmed.Contains("..", StringComparison.Ordinal) || trimmed.Contains("\\", StringComparison.Ordinal))
            {
                changed = true;
                return fallback;
            }

            if (!PathPattern.IsMatch(trimmed))
            {
                changed = true;
                return fallback;
            }

            return trimmed;
        }

        private static class HttpC2Defaults
        {
            public const string Open = "/c2/open";
            public const string Up = "/c2/up";
            public const string Down = "/c2/down";
            public const string Close = "/c2/close";
        }
    }
}
