# HANDOFF — Convert Sql2SqlCloner WinForms → cross-platform .NET 8 CLI

This document is for an agent taking over an in-progress conversion. Read it fully before
touching code. The full approved plan is at `/Users/david/.claude/plans/zippy-foraging-lightning.md`.

## Goal

Convert this legacy **non-SDK .NET Framework 4.8 WinForms** app into a **single cross-platform
.NET 8 console application** that runs on macOS/Linux, reusing the existing SQL transfer engine in
`Core/`. Two original problems being fixed:
1. C# Dev Kit rejects the old non-SDK project format.
2. `MSB3644` — the .NET 8 SDK on macOS can't build a `net48` target (no Framework reference assemblies).

### Additional confirmed requirements (from the user)
- **Config comes from a YAML file whose path is a command-line argument** (`--config job.yaml`).
- **Source→destination schema remapping** (e.g. copy `sales` into `sales_archive`).
- **Copy multiple schemas in one run** (the engine already loads all non-system schemas; the new
  part is filtering to a chosen set + renaming).
- **Secrets hygiene:** connection strings may live in YAML only when password-free (Azure AD /
  integrated / no secret). If a SQL-auth connection string omits the password, obtain it WITHOUT
  putting it in shell history: env vars `SQL2SQL_SOURCE_PASSWORD` / `SQL2SQL_DEST_PASSWORD`
  (and `SQL2SQL_DAC_PASSWORD` for the decrypt DAC), otherwise masked console prompt. NEVER accept a
  password as a CLI argument.

## Feasibility — CONFIRMED
- `dotnet restore` + `dotnet build` succeed on net8.0. SMO 170.13.0, `Microsoft.Data.SqlClient`
  5.2.2, `Microsoft.SqlServer.Management.SqlParser` 173.8.0, and `YamlDotNet` 16.2.1 all resolve and
  the engine **compiles cleanly on .NET 8** (with the current stub `Program.cs`).
- SMO ships .NET/netstandard2.0 binaries; SqlClient uses managed networking on macOS. No
  `SqlGeography`/`SqlGeometry`/spatial usage exists, so the native `SqlServerTypes` loader was not needed.

## Environment
- Working dir (git worktree): `/Users/david/dev/claude/Sql2SqlCloner/gallant-hypatia-8d6b51`
  on branch `david/gallant-hypatia-8d6b51` (main branch is `master`).
- macOS, `dotnet` 8.0.419. WinForms cannot RUN here; the app can only be **compiled** on macOS.
  End-to-end testing needs SQL Server (use Docker — `mcr.microsoft.com/mssql/server`, or
  `azure-sql-edge` on Apple Silicon).

---

## STATUS — what is DONE

### ✅ Task 1 — csproj SDK-style + legacy files removed
- `Sql2SqlCloner.csproj` rewritten as SDK-style `net8.0` console (`OutputType=Exe`). PackageReferences:
  SMO 170.13.0, Microsoft.Data.SqlClient 5.2.2, Microsoft.SqlServer.Management.SqlParser 173.8.0,
  YamlDotNet 16.2.1. Also copies `job.sample.yaml` to output (file not created yet — see Task 6).
- Deleted: all WinForms forms (`ChooseConnections.*`, `ChooseSchemas.*`, `CopySchema.*`,
  `CopyTabledata.*`), all `*.resx`, `Components/` (ConnectionDialog, SQLConnectionDialog,
  TriStateTreeView, InputBoxValidate, NotepadHelper, **Constants.cs**), `SqlServerTypes/`,
  `Properties/` (Settings, Resources, AssemblyInfo, app.manifest, AssemblyCopyright.*), `ILLink/`,
  `Resources/*.png` (kept `Resources/clone.ico`), `app.config`, `packages.config`,
  `GlobalSuppressions.cs`.
- `Sql2SqlCloner.sln` still references the project (unchanged; fine).

### ✅ Task 2 — YAML config layer
- **Created `Core/CloneConfig.cs`** — the entire config model, YamlDotNet-deserialized
  (camelCase naming), exposed via static `CloneConfig.Current`. Contains:
  - `Source` / `Destination` (`EndpointConfig.ConnectionString`)
  - `Options` (`CloneOptions` — the ~16 copy toggles formerly in `Properties.Settings`,
    incl. `CopyCollation` as the `SqlCollationAction` enum)
  - `Schemas` (`IList<SchemaMapEntry>` with `Source`/`Destination`)
  - `Engine` (`EngineConfig` — former `<appSettings>`: BatchSize, SqlTimeout, DefaultPassword,
    RaiserrorTransform, NonCompliantDataDeletion, AlwaysIncludeTables, EnablePreload,
    EnableBackgroundProcessing, DisableDisabledObjects, ExcludeObjects, ExcludeDataLoading,
    FilterDataLoading, GlobalTop)
  - **Schema-map helpers (ready to use):** `SchemaMap` (case-insensitive src→dst dict),
    `IncludeSchemas` (set, or `null` = all non-system schemas), `MapSchema(src)`,
    `IncludesSchema(src)`, `HasSchemaRenames`, and `static Load(path)`.
- **All 9 Core config reads replaced** (no more `ConfigurationManager` / `Properties.Settings`):
  - `Core/SqlTransfer.cs` — `SqlTimeout` (removed `using System.Configuration;`)
  - `Core/DataTransfer/SqlDataTransfer.cs` — `NonCompliantDataDeletion`, `BatchSize`,
    `IncrementalDataCopy`
  - `Core/SchemaTransfer/SqlSchemaTransfer.cs` — `DecryptObjects`, `DefaultPassword`,
    `RaiserrorTransform`, `AlwaysIncludeTables`, `EnablePreload` (removed `using System.Configuration;`)

### ✅ Task 3 — Core made cross-platform
- `Core/SchemaTransfer/SqlSchemaObject.cs` — `Bitmap Status` → **`CopyStatus` enum**
  (`None/Waiting/Ok/Warning/Error`, defined in same file). Dropped `using System.Drawing;`.
  The old `item.Status.Tag = Constants.OK/ERROR/...` pattern is replaced by setting the enum directly.
- `Core/DataTransfer/SqlDataTransfer.cs` — removed `using System.Windows.Forms;` and both
  `MessageBox.Show` calls in `EnableAllDestinationConstraints`. Non-compliant-data decision now uses
  `CloneConfig.Current.Engine.NonCompliantDataDeletion` (`true`/`false`/`ask`); for `ask` it calls a
  new injectable callback **`public Func<string,bool> ConfirmDeleteNonCompliantData`** (CLI sets it;
  null ⇒ do not delete). The "no data left to delete" case now throws instead of MessageBox.
- `Core/SchemaTransfer/SqlSchemaTransfer.cs` `ClearDestinationDatabase` — replaced
  `Thread.CurrentContext.DoCallBack(...)` (Framework remoting, gone in .NET) with a direct
  `callback?.Invoke(obj)`.

**Build state:** `dotnet build` SUCCEEDS with the current stub `Program.cs`.

---

## STATUS — what REMAINS

### 🔲 Task 4 — schema remapping in engine  (IN PROGRESS — nothing wired into the engine yet)
Only the `CloneConfig` helpers exist. The engine has **not** been modified to use them. Implement the
map at every point a schema name flows to the destination. Apply renames only when
`CloneConfig.Current.HasSchemaRenames` is true; always apply the include-set filter when
`IncludeSchemas != null`.

Key methods/locations in `Core/SchemaTransfer/SqlSchemaTransfer.cs` (find by method name; line numbers
have shifted from edits):
1. **Object selection / include filter** — done by the runner OR in `GetSqlObjects` (objects are added
   as `Name = "{schema}.{name}"`). Easiest: the new `SchemaCopyRunner`/`DataCopyRunner` filter
   `SourceObjects` by `CloneConfig.Current.IncludesSchema(schemaOf(obj))`. Decide one place and be
   consistent. Note `SqlSchemaObject.Name` is `schema.name`; parse the schema off the front.
2. **Destination schema creation** in `TransferObject` (search `CREATE SCHEMA` / `existingschemas`):
   create the **mapped** schema name, and seed `existingschemas` with mapped names.
3. **Generated-DDL identifier rewrite** — the script is already walked token-by-token in
   `ParseSqlScript` + `ConvertScriptProperCase`. Cleanest robust approach: add a final text pass
   `ApplySchemaRenames(string script)` applied to every `scriptRun` right before
   `command.ExecuteNonQuery()` in `TransferObject`, replacing `[srcSchema].` and bare `srcSchema.`
   (word-boundary, bracket-aware) with the mapped schema for each renamed schema. Document that schema
   names hidden in dynamic-SQL string literals / `OBJECT_ID('...')` are NOT rewritten.
4. **Drop detection** in `TransferObject` (`namewithschema`): compare against destination using the
   mapped schema.
5. **`GetDestinationTableOrViewByName`** (currently looks up `destinationDatabase.Tables[tb.Name, tb.Schema]`):
   use the **mapped** schema. This method feeds `ApplyIndexes`, `ApplyForeignKeys`, `ApplyChecks`.
6. **`ApplyForeignKeys`** — also remap `ReferencedTableSchema` when that schema is in the rename map
   (cross-schema FK target). `ApplyChecks`/`ApplyIndexes` mostly inherit via the lookup in #5.
7. **`CopyExtendedProperties`** and **`CopySchemaAuthorization`** — map schema where they qualify
   destination objects / `ALTER AUTHORIZATION ON SCHEMA::`.
8. **Data copy** — see Task 5 / the `TransferData` note below.

### 🔲 Task 5 — port form orchestration into Core runners
The copy orchestration lives ONLY in the now-deleted forms. Re-create it (logic preserved verbatim
where noted) as two new Core classes, driven by `CloneConfig.Current.Options` + `IProgress<…>` +
`Action<string>` console callbacks, setting `SqlSchemaObject.Status = CopyStatus.*`:

- **`Core/SchemaCopyRunner.cs`** — port `CopySchema.DoWork` + `ProcessItemsBackground` + `HandleWarning`.
  Sequence: optional ClearDestinationDatabase → change-tracking on/off SQL → collation-mode switch
  (sets `overrideCollation`/`useSourceCollation`/`NoCollation`) → set `IncludePermissions`,
  `IncludeDatabaseRoleMemberships=false`, `IgnoreFileGroup` → **multi-pass retry** object creation
  (CRITICAL: port verbatim — it lets dependent objects/FKs/schema-bound views succeed across passes)
  → if CopyConstraints: indexes→FKs→checks via `ApplyIndexes/ApplyForeignKeys/ApplyChecks` →
  CopyExtendedProperties → CopyRolePermissions → `EnableDestinationConstraints` →
  optional `DisableDisabledObjects` (gate on `Engine.DisableDisabledObjects`) →
  optional `CopySchemaAuthorization` (if CopySecurity) → if CopyData `RemoveSchemaBindingFromDestination`
  → `EnableDestinationDDLTriggers`.
  - The "EnableBackgroundProcessing" parallel path (gate on `Engine.EnableBackgroundProcessing`) is
    optional — can be kept or dropped (it spins up a 2nd `SqlSchemaTransfer` for procs/UDFs).
  - Pre-filter `CopyList` like the form did: drop `User`/`DatabaseRole` if `!CopySecurity`; drop
    `FullTextCatalog`/`FullTextStopList` unless `CopyFullText && CopyConstraints`.
  - Remove system-versioned history tables from the list (the form did this).
- **`Core/DataCopyRunner.cs`** — port `CopyTabledata` ctor init + `DoWork`.
  - Init: build per-table `SELECT` — `SELECT [TOP n] [cols, with COLLATE rewrite when
    convertCollation] FROM [srcSchema].[table] WITH(NOLOCK) [WhereFilter] [OrderByFields]`.
    Honor `Engine.GlobalTop`, per-table `TopRecords`/`WhereFilter`/`OrderByFields` (set from
    `Engine.FilterDataLoading` during object load). `convertCollation` is true when
    `CopyCollation == Set_destination_db_collation`.
  - DoWork: `DisableAllDestinationConstraints` → for each table `TransferData(...)` → for standalone
    (no relationships) tables `EnableTableConstraints` → `ReAddSchemaBindingToDestination` →
    (on success) `EnableAllDestinationConstraints` + optional `DisableDisabledObjects`.
  - The 2-thread SELECT-precompute can be kept or simplified to single-threaded.
- **Replace WinForms confirmations** (`DeleteDatabaseConfirm` / `DeleteDatabaseDataConfirm`) with the
  CLI `--yes` flag (skip prompts) or an interactive console y/n.

#### ⚠️ `TransferData` + schema remap interaction (IMPORTANT)
`SqlDataTransfer.TransferData(string tableName, string query)` currently uses the single `tableName`
for BOTH the destination bulk-copy target AND the source column mapping (`GetMapping` /
`GetSchema(SourceConnection, tableName)`). With schema rename the source table is `[srcSchema].[t]`
and the destination is `[dstSchema].[t]`, so one name cannot serve both. **Add an overload**
`TransferData(string destTableName, string sourceTableName, string query)` (keep the old 2-arg as a
wrapper passing the same name twice) and have `GetMapping` use `sourceTableName` for the source side
and `destTableName` for the destination side. The `DataCopyRunner` passes mapped dest + source names.

### 🔲 Task 6 — CLI `Program.cs` + `job.sample.yaml`
- `Program.cs` is currently a STUB. Rewrite to:
  1. Parse args: `--config <path.yaml>` (required), `--schema-only`, `--data-only`, `--yes`, `--help`.
  2. `CloneConfig.Current = CloneConfig.Load(path)`; validate.
  3. Resolve connection strings + secrets per the rules above (env var, else masked
     `Console.ReadKey(intercept:true)` prompt; never a CLI arg). Build DAC connection string for
     decrypt (sa) the way the old `ChooseConnections.cs` did, only if `Options.DecryptObjects`.
  4. Build `new SqlSchemaTransfer(src, dst, skipPreload:false, dacConnStr, ct)` and
     `new SqlDataTransfer(src, dst, schemaTransfer.LstPostExecutionExecute)`; set
     `dataTransfer.ConfirmDeleteNonCompliantData` to a console prompt (or `--yes`).
  5. Run `SchemaCopyRunner` (if `Options.CopySchema` and not `--data-only`) then `DataCopyRunner`
     (if `Options.CopyData` and not `--schema-only`). Print progress + final summary; set
     `Environment.ExitCode` to non-zero on errors. Honor Ctrl+C via a `CancellationTokenSource`.
- Create **`job.sample.yaml`** documenting every key (use the YAML shape in the plan file / the
  `CloneConfig` model). Mirrors the old `app.config` defaults.

### 🔲 Task 7 — build + end-to-end verification
- `dotnet build` on macOS must stay green.
- Spin up SQL Server in Docker; seed a source DB with multiple schemas (`dbo`, `sales`, `hr`),
  cross-schema FKs, a view, a stored proc, and rows. Run `dotnet run -- --config job.yaml` with a
  rename (`sales`→`sales_archive`, `hr`→`people`). Verify objects exist under renamed schemas
  (tables, FKs, view/proc bodies), row counts match, constraints enabled, exit code 0.
- Test: multi-schema filter; omit `schemas:` (⇒ all, no rename); `--schema-only`/`--data-only`;
  each `copyCollation` mode; an `excludeObjects`/`filterDataLoading` filter; secrets hygiene
  (password via prompt and via `SQL2SQL_*` env var; YAML connection string without a password).

---

## Engine API cheat-sheet (all in `Core/`, namespaces `Sql2SqlCloner.Core[.SchemaTransfer|.DataTransfer]`)
- `SqlTransfer(src, dest, IList<string> lstPostExecutionExecute)` — base; connection mgmt,
  `EnableDestinationConstraints`, `DisableAllDestinationConstraints`, `DeleteDestinationDatabase`,
  `DisableDisabledObjects`, `RunInDestination(sql)`, `GetSource/DestinationSqlCommand`.
- `SqlSchemaTransfer(src, dst, bool skipPreload, string DACConnectionString, CancellationToken ct)`
  — `SourceObjects`/`DestinationObjects` (`IList<SqlSchemaObject>`), `TransferObject(obj, dropIfExists,
  overrideCollation, useSourceCollation, alterInsteadOfCreate, removeSchemaBinding)`,
  `ApplyIndexes`, `ApplyForeignKeys`, `ApplyChecks`, `CopyExtendedProperties`, `CopyRolePermissions`,
  `CopySchemaAuthorization`, `RemoveSchemaBindingFromDestination`, `ReAddSchemaBindingToDestination`,
  `ClearDestinationDatabase(Action<NamedSmoObject>)`, `RefreshAll/RefreshDestination(Objects)`,
  `Source/DestinationCxInfo()`, option props `IncludePermissions/IncludeDatabaseRoleMemberships/
  NoCollation/IgnoreFileGroup`.
- `SqlDataTransfer(src, dest, lstPostExecutionExecute)` — `TransferData(tableName, query)`,
  `EnableTableConstraints`, `EnableAllDestinationConstraints`, `ConfirmDeleteNonCompliantData` (new).
- `SqlSchemaObject { CopyStatus Status; string Name /* "schema.name" */; string Type; bool CopyData;
  NamedSmoObject Object; string Error; SqlSchemaObject Parent; NameWithBrackets; NameWithoutBrackets;
  static AddBrackets(name) }`. `SqlSchemaTable : SqlSchemaObject { long RowCount; long TopRecords;
  string WhereFilter; string OrderByFields; bool HasRelationships }`.
- `SqlCollationAction { Ignore_collation, No_collation, Keep_source_db_collation, Set_destination_db_collation }`.

## Reference: the deleted forms (recover orchestration from git)
The orchestration to port lived in `CopySchema.cs` (`DoWork`, `ProcessItemsBackground`,
`HandleWarning`) and `CopyTabledata.cs` (ctor init + `DoWork`). Recover them from git history:
`git show HEAD:CopySchema.cs` and `git show HEAD:CopyTabledata.cs` (also `ChooseConnections.cs` for the
DAC connection-string building and how the transfers were wired). These are uncommitted deletions in
the working tree, so `HEAD` still has them.

## Build / run commands
```
dotnet build
dotnet run -- --config job.yaml
```
