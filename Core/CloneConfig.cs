using Sql2SqlCloner.Core.DataTransfer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Sql2SqlCloner.Core
{
    /// <summary>
    /// Strongly-typed configuration loaded once from the YAML job file.
    /// Replaces the legacy app.config (ConfigurationManager) and Properties.Settings.
    /// Exposed via the static <see cref="Current"/> so the engine can read it the
    /// same way it used to read ConfigurationManager/Properties.Settings.Default.
    /// </summary>
    public class CloneConfig
    {
        public static CloneConfig Current { get; set; }

        public EndpointConfig Source { get; set; } = new EndpointConfig();
        public EndpointConfig Destination { get; set; } = new EndpointConfig();
        public CloneOptions Options { get; set; } = new CloneOptions();
        public IList<SchemaMapEntry> Schemas { get; set; } = new List<SchemaMapEntry>();
        public EngineConfig Engine { get; set; } = new EngineConfig();

        [YamlIgnore]
        private IDictionary<string, string> schemaMap;
        [YamlIgnore]
        private ISet<string> includeSchemas;

        /// <summary>
        /// Case-insensitive source-schema =&gt; destination-schema map.
        /// Schemas listed without a destination map to themselves.
        /// </summary>
        [YamlIgnore]
        public IDictionary<string, string> SchemaMap
        {
            get
            {
                EnsureSchemasResolved();
                return schemaMap;
            }
        }

        /// <summary>
        /// Set of source schemas to include, or null to include every non-system schema.
        /// </summary>
        [YamlIgnore]
        public ISet<string> IncludeSchemas
        {
            get
            {
                EnsureSchemasResolved();
                return includeSchemas;
            }
        }

        private void EnsureSchemasResolved()
        {
            if (schemaMap != null)
            {
                return;
            }
            schemaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Schemas == null || Schemas.Count == 0)
            {
                //no explicit list: copy all non-system schemas with no renaming
                includeSchemas = null;
                return;
            }
            includeSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Schemas)
            {
                if (string.IsNullOrWhiteSpace(entry?.Source))
                {
                    continue;
                }
                var src = entry.Source.Trim();
                var dst = string.IsNullOrWhiteSpace(entry.Destination) ? src : entry.Destination.Trim();
                includeSchemas.Add(src);
                schemaMap[src] = dst;
            }
        }

        /// <summary>Returns the destination schema name for a given source schema (identity if unmapped).</summary>
        public string MapSchema(string sourceSchema)
        {
            if (string.IsNullOrEmpty(sourceSchema))
            {
                return sourceSchema;
            }
            return SchemaMap.TryGetValue(sourceSchema, out var dest) ? dest : sourceSchema;
        }

        /// <summary>True when the given source schema should be copied.</summary>
        public bool IncludesSchema(string sourceSchema) =>
            IncludeSchemas == null || (sourceSchema != null && IncludeSchemas.Contains(sourceSchema));

        /// <summary>True when at least one schema is renamed (enables the script-rewriting path).</summary>
        public bool HasSchemaRenames =>
            SchemaMap.Any(kv => !string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase));

        private IList<KeyValuePair<string, string>> renamePairs;
        private IList<KeyValuePair<string, string>> RenamePairs =>
            renamePairs ??= SchemaMap
                .Where(kv => !string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase))
                //longest source schema first so e.g. "sales" cannot partially shadow "sales_archive"
                .OrderByDescending(kv => kv.Key.Length)
                .ToList();

        /// <summary>
        /// Maps the schema part of an unbracketed <c>schema.name</c> identifier
        /// (e.g. <c>sales.Orders</c> =&gt; <c>sales_archive.Orders</c>). Identity when unmapped.
        /// </summary>
        public string MapQualifiedName(string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName) || !HasSchemaRenames)
            {
                return qualifiedName;
            }
            var idx = qualifiedName.IndexOf('.');
            if (idx <= 0)
            {
                return qualifiedName;
            }
            return $"{MapSchema(qualifiedName.Substring(0, idx))}.{qualifiedName.Substring(idx + 1)}";
        }

        /// <summary>
        /// Rewrites schema-qualified identifiers in a generated T-SQL script so the destination
        /// schema name is used for every renamed schema. Handles bracketed (<c>[src].</c>),
        /// bare (<c>src.</c>), and <c>SCHEMA::[src]</c> references. Schema names hidden inside
        /// dynamic-SQL string literals / <c>OBJECT_ID('...')</c> arguments are deliberately NOT
        /// rewritten (script-text rewriting cannot reach them safely).
        /// </summary>
        public string ApplySchemaRenames(string script)
        {
            if (string.IsNullOrEmpty(script) || !HasSchemaRenames)
            {
                return script;
            }
            foreach (var pair in RenamePairs)
            {
                var src = Regex.Escape(pair.Key);
                var dst = pair.Value;
                //bracketed qualifier: [src]. -> [dst].
                script = Regex.Replace(script, @"\[" + src + @"\]\s*\.", $"[{dst}].", RegexOptions.IgnoreCase);
                //bare qualifier: src. -> dst. (avoid identifier chars, brackets, dots and string quotes before it)
                script = Regex.Replace(script, @"(?<![\w\.\]@#$'])" + src + @"(?=\s*\.)", dst, RegexOptions.IgnoreCase);
                //ALTER AUTHORIZATION ON SCHEMA::[src] / SCHEMA :: src
                script = Regex.Replace(script, @"(SCHEMA\s*::\s*)\[?" + src + @"\]?(?![\w])", $"$1[{dst}]", RegexOptions.IgnoreCase);
                //CREATE SCHEMA [src] [AUTHORIZATION ...]
                script = Regex.Replace(script, @"(CREATE\s+SCHEMA\s+)\[?" + src + @"\]?(?![\w])", $"$1[{dst}]", RegexOptions.IgnoreCase);
            }
            return script;
        }

        public static CloneConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Configuration file not found: {path}");
            }
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var config = deserializer.Deserialize<CloneConfig>(File.ReadAllText(path))
                         ?? throw new Exception($"Configuration file is empty: {path}");
            config.Options ??= new CloneOptions();
            config.Engine ??= new EngineConfig();
            config.Source ??= new EndpointConfig();
            config.Destination ??= new EndpointConfig();
            config.Schemas ??= new List<SchemaMapEntry>();
            return config;
        }
    }

    public class EndpointConfig
    {
        public string ConnectionString { get; set; }
    }

    public class SchemaMapEntry
    {
        public string Source { get; set; }
        public string Destination { get; set; }
    }

    /// <summary>The ~18 copy toggles that the WinForms GUI used to persist in Properties.Settings.</summary>
    public class CloneOptions
    {
        public bool CopySchema { get; set; } = true;
        public bool CopyData { get; set; } = true;
        public bool CopyConstraints { get; set; } = true;
        public bool DropAndRecreateObjects { get; set; }
        public bool ClearDestinationDatabase { get; set; }
        public bool CopySecurity { get; set; }
        public bool CopyExtendedProperties { get; set; } = true;
        public bool CopyPermissions { get; set; }
        public bool CopyFullText { get; set; }
        public bool StopIfErrors { get; set; } = true;
        public SqlCollationAction CopyCollation { get; set; } = SqlCollationAction.Ignore_collation;
        public bool DisableNotForReplication { get; set; }
        public bool DeleteDestinationTables { get; set; }
        public bool IgnoreFileGroup { get; set; } = true;
        public bool DecryptObjects { get; set; }
        public bool IncrementalDataCopy { get; set; }
    }

    /// <summary>The former &lt;appSettings&gt; tuning keys.</summary>
    public class EngineConfig
    {
        public int BatchSize { get; set; } = 5000;
        public int SqlTimeout { get; set; } = 1800;
        public string DefaultPassword { get; set; } = "D3F@u1TP@s$W0rd!";
        public bool RaiserrorTransform { get; set; } = true;
        public string NonCompliantDataDeletion { get; set; } = "ask";
        public string AlwaysIncludeTables { get; set; } = "";
        public bool EnablePreload { get; set; }
        public bool EnableBackgroundProcessing { get; set; }
        public bool DisableDisabledObjects { get; set; }
        public string ExcludeObjects { get; set; } = "";
        public string ExcludeDataLoading { get; set; } = "";
        public string FilterDataLoading { get; set; } = "";
        public long GlobalTop { get; set; }
    }
}
