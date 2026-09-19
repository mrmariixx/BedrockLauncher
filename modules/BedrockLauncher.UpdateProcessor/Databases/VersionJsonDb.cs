using BedrockLauncher.UpdateProcessor.Classes;
using BedrockLauncher.UpdateProcessor.Enums;
using BedrockLauncher.UpdateProcessor.Extensions;
using BedrockLauncher.UpdateProcessor.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BedrockLauncher.UpdateProcessor.Databases
{
    public class VersionJsonDb : IVersionDb
    {
        public List<VersionInfoJson> list { get; private set; } =
            new List<VersionInfoJson>();

        private void SortVersions()
        {
            list.Sort();
            list.Reverse();
        }

        public void ReadJson(
            string filePath,
            Dictionary<Guid, string> architectures = null)
        {
            if (!File.Exists(filePath))
            {
                list.Clear();
                return;
            }

            string data = File.ReadAllText(filePath);
            ParseJson(data, architectures);
        }

        public void WriteJson(string filePath)
        {
            SortVersions();

            var valuesList = list
                .Select(version => new JArray(
                    version.version,
                    version.uuid.ToString(),
                    (int)version.type,
                    version.architecture,
                    (int)version.packageType))
                .ToList();

            string json = JsonConvert.SerializeObject(
                valuesList,
                Formatting.Indented);

            Directory.CreateDirectory(
                Path.GetDirectoryName(filePath) ?? ".");

            File.WriteAllText(filePath, json);
        }

        public void ParseJson(
            string json,
            Dictionary<Guid, string> architectures = null)
        {
            list.Clear();

            if (string.IsNullOrWhiteSpace(json))
                return;

            JArray data = JArray.Parse(json);

            foreach (JArray item in data)
            {
                if (item.Count < 3)
                    continue;

                string version = item[0]?.Value<string>();
                string uuid = item[1]?.Value<string>();
                int typeValue = item[2]?.Value<int>() ?? 0;

                if (string.IsNullOrWhiteSpace(version) ||
                    string.IsNullOrWhiteSpace(uuid))
                {
                    continue;
                }

                VersionType versionType = (VersionType)typeValue;

                string architecture =
                    item.Count >= 4
                        ? item[3]?.Value<string>()
                        : VersionDbExtensions.FallbackArch;

                PackageType packageType =
                    item.Count >= 5
                        ? (PackageType)(item[4]?.Value<int>() ?? 0)
                        : PackageType.UWP;

                if (string.IsNullOrWhiteSpace(architecture))
                    architecture = VersionDbExtensions.FallbackArch;

                if (architecture == VersionDbExtensions.FallbackArch &&
                    architectures != null &&
                    Guid.TryParse(uuid, out Guid parsedUuid) &&
                    architectures.TryGetValue(
                        parsedUuid,
                        out string knownArchitecture))
                {
                    architecture = knownArchitecture;
                }

                var parsed = new VersionInfoJson(
                    version,
                    uuid,
                    versionType,
                    architecture,
                    packageType);

                if (!list.Any(x => x.uuid == parsed.uuid))
                    list.Add(parsed);
            }

            SortVersions();
        }

        public void AddVersion(
            List<UpdateInfo> updates,
            VersionType type)
        {
            if (updates == null || updates.Count == 0)
                return;

            foreach (UpdateInfo update in updates)
            {
                string version =
                    MinecraftVersion
                        .ConvertVersion(update.packageMoniker, type)
                        .ToString();

                string architecture =
                    VersionDbExtensions.GetVersionArch(
                        update.packageMoniker,
                        type);

                var info = new VersionInfoJson(
                    version,
                    update.updateId,
                    type,
                    architecture,
                    PackageType.UWP);

                if (!list.Any(x => x.uuid == info.uuid))
                    list.Add(info);
            }

            SortVersions();
        }

        public void Save(string filePath)
        {
            WriteJson(filePath);
        }

        public List<IVersionInfo> GetVersions()
        {
            return list.Cast<IVersionInfo>().ToList();
        }

        public void ParseRaw(
            string data,
            Dictionary<Guid, string> architectures = null)
        {
            ParseJson(data, architectures);
        }

        // Compatibility with the original misspelled method.
        public void PraseRaw(
            string data,
            Dictionary<Guid, string> architectures = null)
        {
            ParseRaw(data, architectures);
        }
    }
}