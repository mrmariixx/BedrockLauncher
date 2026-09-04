using BedrockLauncher.UpdateProcessor.Enums;
using BedrockLauncher.UpdateProcessor.Interfaces;
using System;

namespace BedrockLauncher.UpdateProcessor.Classes
{
    public struct VersionInfoTxt : IVersionInfo
    {
        public Guid uuid;
        public string packageMoniker;
        public string serverId;

        public string version;
        public string architecture;
        public VersionType type;

        public VersionInfoTxt(string _uuid, string _packageMoniker, string _serverId, string _architexture, VersionType _type)
        {
            if (!Guid.TryParse(_uuid, out uuid)) uuid = Guid.Empty;
            packageMoniker = _packageMoniker;
            serverId = _serverId;

            version = MinecraftVersion.ConvertVersion(_packageMoniker, _type).ToString();
            architecture = _architexture;
            type = _type;
        }

        public string GetArchitecture() => architecture;

        public VersionType GetVersionType() => type;

        public bool GetIsBeta() => type == VersionType.Beta;

        public Guid GetUUID() => uuid;

        public string GetVersion() => version;
    }
}
