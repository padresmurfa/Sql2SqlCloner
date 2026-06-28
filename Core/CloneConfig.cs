using Sql2SqlCloner.Core.DataTransfer;
using System;
using System.Collections.Generic;
using System.IO;
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
        public IList<SchemaEntry> Schemas { get; set; } = new List<SchemaEntry>();
        public EngineConfig Engine { get; set; } = new EngineConfig();

        [YamlIgnore]
        private bool schemasResolved;
        [YamlIgnore]
        private ISet<string> includeSchemas;

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
            if (schemasResolved)
            {
                return;
            }
            schemasResolved = true;
            if (Schemas == null || Schemas.Count == 0)
            {
                //no explicit list: copy every non-system schema
                includeSchemas = null;
                return;
            }
            includeSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Schemas)
            {
                if (!string.IsNullOrWhiteSpace(entry?.Source))
                {
                    includeSchemas.Add(entry.Source.Trim());
                }
            }
            if (includeSchemas.Count == 0)
            {
                //list present but empty/whitespace -> treat as "all"
                includeSchemas = null;
            }
        }

        /// <summary>True when the given schema should be copied.</summary>
        public bool IncludesSchema(string schema) =>
            IncludeSchemas == null || (schema != null && IncludeSchemas.Contains(schema));

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
            config.Schemas ??= new List<SchemaEntry>();
            return config;
        }
    }

    public class EndpointConfig
    {
        public string ConnectionString { get; set; }

        /// <summary>
        /// Optional inline password for NON-SENSITIVE databases only (local / disposable test
        /// databases). It is stored in clear text in the YAML file, so it must NEVER be used for
        /// production or any real / shared database — use the SQL2SQL_*_PASSWORD environment
        /// variables or the masked prompt for those. It is applied only when the connection string
        /// has a User Id, has no password, and is not using integrated / Azure AD auth. The matching
        /// environment variable, when present, takes precedence over this value.
        /// </summary>
        public string InsecureLocalTestPassword { get; set; }
    }

    /// <summary>One entry in the optional <c>schemas:</c> include list (which schemas to copy).</summary>
    public class SchemaEntry
    {
        public string Source { get; set; }
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
