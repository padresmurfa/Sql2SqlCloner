# Sql2SqlCloner

Cross-platform CLI tool for cloning a SQL Server database — schema and/or data — from one server to another. Runs on macOS, Linux, and Windows (.NET 8).

## Origin

Forked from the [SqlDbCloner](https://www.codeproject.com/Articles/994806/SQL-Server-Database-Cloning-Tool-using-Csharp) project originally published on CodeProject. That project shipped as a **WinForms (.NET Framework 4.8)** desktop application with a GUI. This fork:

1. Converts it to a **headless .NET 8 CLI** so it can run on macOS and Linux without a Windows desktop.
2. Replaces `ConfigurationManager`/`app.config` with a **YAML job file** (`--config job.yaml`) that describes what to clone and how.
3. Improves secrets handling — passwords are never accepted as CLI arguments; instead they are read from environment variables or a masked interactive prompt.
4. Fixes several bugs that manifested against real production SQL Server instances (see [Fixes](#fixes)).

The upstream WinForms UI code has been removed. The SQL transfer engine in `Core/` is retained and largely unchanged.

## Features

- Copy schema objects: tables, views, stored procedures, functions, indexes, foreign keys, check constraints, extended properties, triggers, full-text objects, XML schemas, partitions, security policies, and more.
- Copy data with `SqlBulkCopy` (identity insert, configurable batch size, per-table row cap / TOP / WHERE / ORDER BY filters).
- Schema include filter: restrict the clone to a subset of schemas.
- System-versioned (temporal) table support.
- Change-tracking table support.
- Encrypted object decryption via the DAC connection (optional).
- Incremental data copy mode (skip rows already in the destination, using unique-key detection).
- Collation conversion options.
- Non-compliant data deletion when foreign key constraints cannot be re-enabled.

## Requirements

- .NET 8 SDK (`dotnet build`) or runtime (`dotnet Sql2SqlCloner.dll`).
- Network access to source and destination SQL Server instances.
- The destination database must already exist (`CREATE DATABASE ...`).

Tested against SQL Server 2016 and later. The engine uses SMO 170.13.0 and `Microsoft.Data.SqlClient` 5.2.2.

## Build

```bash
dotnet build
```

Output lands in `bin/Debug/net8.0/` by default.

## Usage

```
Sql2SqlCloner --config <job.yaml> [--schema-only | --data-only] [--yes]

  --config <path>   Path to a YAML job file (required). Also: --config=<path>.
  --schema-only     Copy schema only (overrides options.copyData).
  --data-only       Copy data only (overrides options.copySchema).
  --yes, -y         Auto-confirm destructive prompts (clearDestinationDatabase / deleteDestinationTables).
  --help, -h        Show help.
```

Example:

```bash
dotnet Sql2SqlCloner.dll --config clone-prod.yaml --yes
```

## Job file (YAML configuration)

All behaviour is controlled by a YAML job file. `job.sample.yaml` (shipped alongside the binary) documents every key with inline comments. Minimal example:

```yaml
source:
  connectionString: "Server=prod-server;Database=MyDb;Integrated Security=true;TrustServerCertificate=true"

destination:
  connectionString: "Server=localhost,1433;Database=MyDbCopy;User Id=sa;TrustServerCertificate=true"

options:
  copySchema: true
  copyData: true
  copyConstraints: true
  copyExtendedProperties: true
  clearDestinationDatabase: true
  stopIfErrors: true
  copyCollation: Ignore_collation

engine:
  sqlTimeout: 3600
  batchSize: 5000
  globalTop: 0        # 0 = no row cap
```

### Secrets / passwords

Connection strings in the job file **must not contain passwords** for real databases. When a SQL-auth string omits the password, the tool resolves it in this order:

1. Environment variable (`SQL2SQL_SOURCE_PASSWORD`, `SQL2SQL_DEST_PASSWORD`, `SQL2SQL_DAC_PASSWORD`).
2. `insecureLocalTestPassword` in the endpoint block — **only for local / disposable test databases**; never use this for production or any shared database.
3. Masked interactive prompt (hidden input, not echoed).

Passwords are **never** accepted as command-line arguments.

```yaml
# Safe for local test databases only:
destination:
  connectionString: "Server=localhost,1433;Database=TestDb;User Id=sa;TrustServerCertificate=true"
  insecureLocalTestPassword: "my-local-dev-password"
```

### Schema filter

Omit the `schemas:` section to clone every non-system schema. Add it to restrict the clone:

```yaml
schemas:
  - source: dbo
  - source: sales
```

### Data filters

```yaml
engine:
  globalTop: 1000                     # cap every table at 1000 rows
  excludeDataLoading: "dbo.AuditLog"  # skip data for this table (schema still copied)
  filterDataLoading: "dbo.Orders TOP 500, dbo.Events WHERE Status = 1"
```

## Exit codes

| Code | Meaning |
|------|---------|
| 0    | Success |
| 1    | Runtime error (connection, copy failure) |
| 2    | Configuration / usage error |
| 130  | Cancelled (Ctrl+C or destructive prompt declined) |

## Fixes

Bugs fixed relative to the upstream and earlier versions of this fork:

**`Property DefaultSchema is not available for Database`** — SMO's bulk metadata fetch returns `DefaultSchema = NULL` for Windows/AD-group logins, then the scripter throws when it reads the property. Fixed by injecting `"dbo"` into the SMO `Property` object via its internal `SetValue`/`SetRetrieved` methods before scripting begins (`SqlSchemaTransfer.EnsureDefaultSchema`).

**`Setting SYSTEM_VERSIONING to ON failed because history table contains overlapping records`** — When copying temporal (system-versioned) tables, the current table is bulk-copied separately and its `GENERATED ALWAYS` period columns get fresh timestamps. Re-enabling versioning with `DATA_CONSISTENCY_CHECK=ON` then rejects the result. Fixed by using `DATA_CONSISTENCY_CHECK=OFF`, which is the standard approach for temporal data migration.

## Project structure

```
Program.cs                   CLI entry point — argument parsing, secret resolution, orchestration
Core/
  CloneConfig.cs             YAML config model (loaded into CloneConfig.Current)
  SqlTransfer.cs             Base class — connection management, constraint helpers
  SchemaCopyRunner.cs        Schema copy orchestration (multi-pass retry loop)
  DataCopyRunner.cs          Data copy orchestration (bulk copy, per-table SELECT building)
  SchemaTransfer/
    SqlSchemaTransfer.cs     SMO-based schema scripting and object transfer
    SqlSchemaObject.cs       Object model (CopyStatus enum, SqlSchemaTable)
  DataTransfer/
    SqlDataTransfer.cs       SqlBulkCopy wrapper, constraint enable/disable, temporal table handling
job.sample.yaml              Full annotated configuration reference
```

## License

See [LICENSE](LICENSE).
