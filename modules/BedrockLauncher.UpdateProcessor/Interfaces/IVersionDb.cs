using BedrockLauncher.UpdateProcessor.Enums;
using BedrockLauncher.UpdateProcessor.Interfaces;

namespace BedrockLauncher.UpdateProcessor.Interfaces
{
    public interface IVersionDb
    {
        void AddVersion(System.Collections.Generic.List<BedrockLauncher.UpdateProcessor.Classes.UpdateInfo> u, VersionType type);
        void Save(string filePath);
        System.Collections.Generic.List<IVersionInfo> GetVersions();
        /// <summary>Parses raw version data (e.g. JSON string) into the database.</summary>
        void ParseRaw(string data, System.Collections.Generic.Dictionary<System.Guid, string> architectures);

        // Deprecated alias kept for backward compatibility.
        [System.Obsolete("Use ParseRaw (fixed spelling).")]
        void ParseRaw(string data, System.Collections.Generic.Dictionary<System.Guid, string> architectures);
    }
}
