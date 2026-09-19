using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BedrockLauncher.UpdateProcessor.Classes;
using BedrockLauncher.UpdateProcessor.Extensions;
using BedrockLauncher.UpdateProcessor.Interfaces;
using BedrockLauncher.UpdateProcessor.Enums;

namespace BedrockLauncher.UpdateProcessor.Databases
{
    /// <summary>
    /// A version database backed by a JSON array-of-arrays file.
    /// Format per entry: ["version", "uuid", typeInt, "architecture"]
    /// </summary>
    public class VersionJsonDb : IVersionDb
    {
        public List<VersionInfoJson> list { get; private set; } = new List<VersionInfoJson>();

        private void SortVersions()
        {
            list.Sort();
            list.Reverse();
        }

        #region Read / Write

        public void ReadJson(string filePath, Dictionary<Guid, string> architectures = null)
        {
            using var reader = File.OpenText(filePath);
            ParseJson(reader.ReadToEnd(), architectures);
        }

        /// <summary>
        /// Writes the database to <paramref name="filePath"/> using indented JSON (pretty-print).
        /// </summary>
        public void WriteJson(string filePath)
        {
            SortVersions();
            var valuesList = JArray.FromObject(list).Select(x => x.Values().ToList()).ToList();
            string json = JsonConvert.SerializeObject(valuesList, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        /// <summary>Parses a JSON string into the version list.</summary>
        public void ParseJson(string json, Dictionary<Guid, string> architectures = null)
        {
            JArray data = JArray.Parse(json);
            // Reverse so that the most-recent entries (appended last) end up sorted correctly.
            var entries = data.ToList();
            entries.Reverse();

            foreach (JArray o in entries)
            {
                string name = o[0].Value<string>();
                string uuid = o[1].Value<string>();
                int    type = o[2].Value<int>();
                string arch = o.Count >= 4 ? o[3].Value<string>() : VersionDbExtensions.FallbackArch;
                PackageType pkgType = o.Count >= 5 ? (PackageType)o[4].Value<int>() : PackageType.UWP;

                var v = new VersionInfoJson(name, uuid, (VersionType)type, arch, pkgType);

                // Resolve unknown architecture from the caller-supplied dictionary.
                if (arch == VersionDbExtensions.FallbackArch && architectures != null)
                {
                    if (architectures.TryGetValue(v.uuid, out string resolvedArch))
                        v = new VersionInfoJson(name, uuid, (VersionType)type, resolvedArch, pkgType);
                }

                if (!list.Exists(x => x.uuid == v.uuid))
                    list.Add(v);
            }

            SortVersions();
        }

        #endregion

        #region IVersionDb Implementation

        public void AddVersion(List<UpdateInfo> updates, VersionType type)
        {
            if (updates == null || updates.Count == 0) return;

            foreach (var u in updates)
            {
                string version = MinecraftVersion.ConvertVersion(u.packageMoniker, type).ToString();
                string arch    = VersionDbExtensions.GetVersionArch(u.packageMoniker, type);
                var    info    = new VersionInfoJson(version, u.updateId, type, arch, PackageType.UWP);

                if (!list.Exists(x => x.uuid == info.uuid))
                    list.Add(info);
            }
        }

        /// <summary>
        /// Saves the database using the compact array-of-arrays format.
        /// </summary>
        public void Save(string filePath)
        {
            SortVersions();
            var sb = new StringBuilder();
            sb.Append('[');

            for (int i = 0; i < list.Count; i++)
            {
                var ver = list[i];
                sb.Append($"[\"{ver.version}\", \"{ver.uuid}\", {(int)ver.type}, \"{ver.architecture}\", {(int)ver.packageType}]");
                if (i < list.Count - 1)
                    sb.Append(',').AppendLine();
            }

            sb.Append(']');
            File.WriteAllText(filePath, sb.ToString());
        }

        public List<IVersionInfo> GetVersions() => list.Cast<IVersionInfo>().ToList();

        /// <inheritdoc/>
        public void ParseRaw(string data, Dictionary<Guid, string> architectures) => ParseJson(data, architectures);

        /// <inheritdoc/>
        [Obsolete("Use ParseRaw (fixed spelling).")]
        public void ParseRaw(string data, Dictionary<Guid, string> architectures) => ParseRaw(data, architectures);

        #endregion
    }
}
