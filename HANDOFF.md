# Conversion notes — WinForms → .NET 8 CLI

This file records the non-obvious decisions made during the conversion of Sql2SqlCloner from a
WinForms (.NET Framework 4.8) desktop application to a cross-platform .NET 8 CLI. It is retained as
a reference for future maintainers; see `README.md` for user-facing documentation.

## What was removed

- All WinForms forms (`ChooseConnections`, `ChooseSchemas`, `CopySchema`, `CopyTabledata`).
- `Components/` (ConnectionDialog, SQLConnectionDialog, TriStateTreeView, InputBoxValidate,
  NotepadHelper, Constants.cs).
- `SqlServerTypes/` native loader (no spatial types used).
- `Properties/` (Settings, Resources, AssemblyInfo, app.manifest).
- `app.config` / `packages.config`.
- `GlobalSuppressions.cs`, `ILLink/`.
- `System.Drawing` dependency (`Bitmap Status` on `SqlSchemaObject` replaced by `CopyStatus` enum).
- `System.Configuration` / `ConfigurationManager` (all reads replaced by `CloneConfig.Current`).
- Schema renaming capability (was added, then removed at user request — the schema include filter
  is retained).

## Architecture decisions

**YAML config over app.config.** `YamlDotNet` 16.2.1 deserialises into `CloneConfig` (camelCase
naming). The static `CloneConfig.Current` singleton replaces every former `Properties.Settings`
and `ConfigurationManager.AppSettings` read across the engine.

**Orchestration moved to Core.** `CopySchema.DoWork`/`ProcessItemsBackground` → `SchemaCopyRunner`.
`CopyTabledata` ctor + `DoWork` → `DataCopyRunner`. The multi-pass retry loop in `SchemaCopyRunner`
is preserved verbatim from the original form code.

**`InvariantGlobalization=false` required.** SMO (SQL Server Management Objects) depends on
ICU/globalization. Setting this to `true` (the .NET 8 publish default for single-file) causes
"Globalization Invariant Mode is not supported" at runtime. The csproj explicitly sets it to `false`.

**Secrets hygiene.** Passwords are resolved in order: environment variable →
`insecureLocalTestPassword` (non-sensitive local DBs only) → masked interactive prompt. They are
never accepted as CLI arguments; `--password` / `--pwd` are detected and rejected with an error.

## Bug fixes vs. upstream

### `Property DefaultSchema is not available for Database`

SMO's bulk metadata fetch (`SetDefaultInitFields(true)`) fetches `DefaultSchema` for every
database, but returns `NULL` for Windows/AD group logins (the login has no personal default schema).
When the scripter later reads `db.DefaultSchema`, SMO throws because the property is marked
retrieved-but-null and its getter enforces availability.

Fix: `SqlSchemaTransfer.EnsureDefaultSchema(Database db)` — after loading each database, enumerate
`db.Properties` (safe), find the `DefaultSchema` property, then inject `"dbo"` via SMO's internal
`Property.SetValue`/`SetRetrieved` methods using reflection. This makes the getter return `"dbo"`
without hitting the server again.

Attempts that did NOT work:
- `SetDefaultInitFields(typeof(Database), false)` — SMO still fetches and caches the NULL.
- `db.DefaultSchema = "dbo"` (public setter) — throws the same exception.
- `db.Properties["DefaultSchema"].Value = "dbo"` — string indexer also throws.

### `Setting SYSTEM_VERSIONING to ON failed because history table contains overlapping records`

When copying a temporal (system-versioned) table, the cloner:
1. Turns SYSTEM_VERSIONING off on the destination.
2. Bulk-copies the current rows into the main table.
3. Bulk-copies the history rows into the history table.
4. Re-enables SYSTEM_VERSIONING.

Step 2 produces fresh `ValidFrom` timestamps (the columns are `GENERATED ALWAYS`) while the history
table retains the original periods. `DATA_CONSISTENCY_CHECK=ON` then rejects the inconsistency.

Fix: use `DATA_CONSISTENCY_CHECK=OFF` when calling `ALTER TABLE … SET(SYSTEM_VERSIONING=ON …)`.
This is the standard approach for temporal data migration and is documented by Microsoft for
exactly this scenario. The table remains correctly versioned after the copy.
