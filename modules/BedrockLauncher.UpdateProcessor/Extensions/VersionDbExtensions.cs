using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BedrockLauncher.UpdateProcessor.Enums;

namespace BedrockLauncher.UpdateProcessor.Extensions
{
    public class VersionDbExtensions
    {
        // Cache compiled regexes by VersionType to avoid repeated recompilation.
        private static readonly ConcurrentDictionary<VersionType, Regex> _regexCache
            = new ConcurrentDictionary<VersionType, Regex>();

        public static Regex GetRegex(VersionType type)
        {
            return _regexCache.GetOrAdd(type, t =>
            {
                var id = t == VersionType.Preview
                    ? @"Microsoft\.MinecraftWindowsBeta_"
                    : @"Microsoft\.MinecraftUWP_";
                return new Regex(
                    @$"({id}([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+)_(.*)__8wekyb3d8bbwe.*)",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));
            });
        }

        public static string FallbackArch => "???";

        public static string GetVersionArch(string packageMoniker, VersionType versionType)
        {
            Regex regex = GetRegex(versionType);
            Match match = regex.Match(packageMoniker);
            // Fix: Regex.Match() never returns null — check Success instead.
            if (!match.Success) return FallbackArch;
            string arch = match.Groups[6].Value;
            return string.IsNullOrEmpty(arch) ? FallbackArch : arch;
        }

        /// <summary>
        /// Returns true if the two architecture strings are equivalent (case-insensitive).
        /// </summary>
        public static bool DoesVersionArchMatch(string sourceArch, string targetArch)
        {
            return string.Equals(sourceArch, targetArch, StringComparison.OrdinalIgnoreCase);
        }

        // Keep the old name as a deprecated alias so existing call-sites don't break.
        [Obsolete("Use DoesVersionArchMatch (fixed spelling).")]
        public static bool DoesVersionArchMatch(string sourceArch, string targetArch)
            => DoesVersionArchMatch(sourceArch, targetArch);
    }
}
