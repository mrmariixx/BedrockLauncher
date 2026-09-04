using BedrockLauncher.UpdateProcessor.Classes;
using BedrockLauncher.UpdateProcessor.Enums;
using System;
using System.Collections.Generic;

namespace BedrockLauncher.UpdateProcessor.Interfaces
{
    public interface IVersionDb
    {
        void AddVersion(List<UpdateInfo> u, VersionType type);
        void Save(string winstoreDBFile);
        List<IVersionInfo> GetVersions();
        void PraseRaw(string data, Dictionary<Guid, string> architectures);
    }
}
