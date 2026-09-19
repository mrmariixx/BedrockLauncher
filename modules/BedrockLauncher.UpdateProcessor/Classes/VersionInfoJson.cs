using BedrockLauncher.UpdateProcessor.Interfaces;
using System;
using System.Collections.Generic;
using BedrockLauncher.UpdateProcessor.Enums;

namespace BedrockLauncher.UpdateProcessor.Classes
{
    /// <summary>
    /// Represents a version entry stored in the community/store JSON database.
    /// </summary>
    public struct VersionInfoJson : IVersionInfo, IComparable<VersionInfoJson>, IComparer<VersionInfoJson>
    {
        public string version;
        public Guid   uuid;
        public VersionType type;
        public string architecture;
        public PackageType packageType;

        /// <param name="architecture">Target architecture string (e.g. "x64", "x86", "arm64").</param>
        public VersionInfoJson(string version, string uuid, VersionType type, string architecture, PackageType packageType = PackageType.UWP)
        {
            if (!Guid.TryParse(uuid, out this.uuid)) this.uuid = Guid.Empty;
            this.version      = version;
            this.type         = type;
            this.architecture = architecture;
            this.packageType  = packageType;
        }

        public string     GetArchitecture() => architecture;
        public Guid       GetUUID()         => uuid;
        public string     GetVersion()      => version;
        public VersionType GetVersionType() => type;
        public bool       GetIsBeta()       => type == VersionType.Beta;
        public PackageType GetPackageType() => packageType;

        public int Compare(VersionInfoJson x, VersionInfoJson y)
        {
            if (MinecraftVersion.TryParse(x.version, out var a) &&
                MinecraftVersion.TryParse(y.version, out var b))
                return a.CompareTo(b);

            // Fallback to lexicographic comparison when parsing fails.
            return string.Compare(x.version, y.version, StringComparison.OrdinalIgnoreCase);
        }

        public int CompareTo(VersionInfoJson other) => Compare(this, other);
    }
}
