using Microsoft.SqlServer.Management.Smo;
using Sql2SqlCloner.Core.DataTransfer;
using Sql2SqlCloner.Core.SchemaTransfer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Sql2SqlCloner.Core
{
    /// <summary>
    /// Headless port of the former <c>CopySchema</c> WinForms orchestration
    /// (<c>DoWork</c> + <c>ProcessItemsBackground</c> + <c>HandleWarning</c>).
    /// Drives <see cref="SqlSchemaTransfer"/> from <see cref="CloneConfig.Current"/> and reports
    /// progress through an <see cref="Action{T}"/> console callback instead of a data grid.
    /// </summary>
    public class SchemaCopyRunner
    {
        private readonly SqlSchemaTransfer SchemaTransfer;
        private readonly bool disableNotForReplication;
        private readonly Action<string> log;
        private readonly CancellationToken token;
        private string lastError = "";
        private int errorCount;

        public IList<SqlSchemaObject> CopyList { get; }
        public int ErrorCount => errorCount;
        public string LastError => lastError;

        public SchemaCopyRunner(SqlSchemaTransfer transferSchema, IList<SqlSchemaObject> lstObjects,
            bool disableNotForReplication, Action<string> log, CancellationToken token)
        {
            SchemaTransfer = transferSchema;
            this.disableNotForReplication = disableNotForReplication;
            this.log = log ?? (_ => { });
            this.token = token;

            var options = CloneConfig.Current.Options;
            CopyList = lstObjects.ToList();
            if (!options.CopySecurity)
            {
                CopyList = CopyList.Where(t => t.Type != "User" && t.Type != "DatabaseRole").ToList();
            }
            if (!(options.CopyFullText && options.CopyConstraints))
            {
                CopyList = CopyList.Where(t => t.Type != "FullTextCatalog" && t.Type != "FullTextStopList").ToList();
            }
        }

        /// <summary>Runs the full schema copy. Returns the number of errors encountered.</summary>
        public int Run()
        {
            if (CopyList.Count == 0)
            {
                log("No SQL objects found in source database to copy");
                return 0;
            }
            CopyList.ToList().ForEach(c => c.Status = CopyStatus.None);
            DoWork();
            return errorCount;
        }

        private void DoWork()
        {
            var options = CloneConfig.Current.Options;
            var engine = CloneConfig.Current.Engine;

            if (options.ClearDestinationDatabase)
            {
                log($"Clearing destination database {SchemaTransfer.DestinationCxInfo()}...");
                try
                {
                    SchemaTransfer.ClearDestinationDatabase();
                }
                catch (Exception ex)
                {
                    log($"Error clearing destination database: {ex.Message}");
                }
            }

            //check change tracking
            SchemaTransfer.RunInDestination($"SELECT CASE WHEN (SELECT COUNT(*) FROM sys.change_tracking_databases WHERE UPPER(DB_NAME(database_id))= UPPER('{SchemaTransfer.DestinationDatabaseName}'))>0 THEN 'ALTER DATABASE [{SchemaTransfer.DestinationDatabaseName}] SET CHANGE_TRACKING = OFF' ELSE NULL END");
            SchemaTransfer.RunInDestination(
                $"SELECT CASE WHEN (SELECT COUNT(*) FROM sys.change_tracking_databases WHERE UPPER(DB_NAME(database_id))=UPPER('{SchemaTransfer.SourceDatabaseName}'))=0 THEN NULL " +
                $"ELSE (SELECT 'ALTER DATABASE [{SchemaTransfer.DestinationDatabaseName}] SET CHANGE_TRACKING = ON '+" +
                "'(CHANGE_RETENTION = ' + CAST(retention_period AS VARCHAR) + '  ' + retention_period_units_desc + ', AUTO_CLEANUP = ' + CASE WHEN is_auto_cleanup_on='1' THEN 'ON' ELSE 'OFF' END  +')'" +
                $" FROM sys.change_tracking_databases WHERE UPPER(DB_NAME(database_id))= UPPER('{SchemaTransfer.SourceDatabaseName}')) END");

            log($"Copying schema from '{SchemaTransfer.SourceCxInfo()}' to '{SchemaTransfer.DestinationCxInfo()}'");
            bool overrideCollation, useSourceCollation;
            SchemaTransfer.NoCollation = false;
            switch (options.CopyCollation)
            {
                //ignore collation, do nothing
                case SqlCollationAction.Ignore_collation:
                    overrideCollation = useSourceCollation = false;
                    break;
                //no collation
                case SqlCollationAction.Keep_source_db_collation:
                    overrideCollation = useSourceCollation = false;
                    SchemaTransfer.NoCollation = false;
                    break;
                //override collation, use source db
                case SqlCollationAction.No_collation:
                    overrideCollation = useSourceCollation = true;
                    SchemaTransfer.NoCollation = true;
                    break;
                //override collation, use destination db (source collation will be converted at SELECT time)
                case SqlCollationAction.Set_destination_db_collation:
                    overrideCollation = true;
                    useSourceCollation = false;
                    break;
                default:
                    overrideCollation = useSourceCollation = false;
                    break;
            }
            SchemaTransfer.IncludePermissions = options.CopyPermissions;
            SchemaTransfer.IncludeDatabaseRoleMemberships = false; //not implemented yet
            SchemaTransfer.IgnoreFileGroup = options.IgnoreFileGroup;

            var CopyConstraints = options.CopyConstraints;
            var retries = 0;
            var currList = CopyList;
            var finishedPass1 = false;
            var finishedPass2 = false;
            var lstDelete = new List<string>();
            var previousListCount = 0;

            //system-versioned history tables will be created automatically, remove them
            foreach (Table systemhistorytable in currList.Select(o => o.Object).OfType<Table>().Where(table =>
                table.GetTableProperty("IsSystemVersioned")).ToList())
            {
                var historyItem = currList.FirstOrDefault(t => (t.Object is Table table) && table.ID == systemhistorytable.HistoryTableID);
                if (historyItem != null)
                {
                    currList.Remove(historyItem);
                }
            }

            //the first time indexes won't be available, therefore some items dependent on them
            //such as FullText objects won't be created, the second time indexes will be available
            //so that those objects will be created
            while (!finishedPass2)
            {
                //do all objects, retrying failed ones until no more objects can be created,
                //this way all dependent objects will be created
                while (!finishedPass1)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (retries > 0)
                    {
                        currList = currList.Where(item => !string.IsNullOrEmpty(item.Error) &&
                                                    item.Status != CopyStatus.Warning).ToList();
                        currList.Where(item => !item.Error.StartsWith("Incompatible subitems")).ToList()
                            .ForEach(item => { item.Status = CopyStatus.None; item.Error = string.Empty; });

                        errorCount = 0;
                        if (currList.Count > 0)
                        {
                            log($"Retry pass {retries}: {currList.Count} object(s) remaining");
                        }
                    }
                    retries++;
                    foreach (var item in currList)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }
                        try
                        {
                            SchemaTransfer.TransferObject(item.Object, options.DropAndRecreateObjects && !options.ClearDestinationDatabase,
                                                         overrideCollation, useSourceCollation, false, null);
                            item.Status = CopyStatus.Ok;
                            item.Error = "";
                            if (item.Object is DatabaseDdlTrigger)
                            {
                                SchemaTransfer.RunInDestination($"SELECT 'DISABLE TRIGGER ' + QUOTENAME(name) + ' ON DATABASE' FROM sys.triggers WHERE name='{item.Name}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                if (ex.Message.StartsWith("Incompatible subitems"))
                                {
                                    HandleWarning(item, ex);
                                }
                                else if ((item.Type == "Table" || item.Type == "View") && previousListCount != currList.Count)
                                {
                                    lstDelete.Add($"IF OBJECT_ID('{item.Object}','{item.Type.Substring(0, 1).Replace("T", "U")}') IS NOT NULL DROP {item.Type.ToUpperInvariant()} {item.Object}");
                                    //if this table/view is to be deleted, set its triggers as deleted
                                    foreach (var subitem in CopyList.Where(x => x.Parent == item))
                                    {
                                        if (subitem.Status != CopyStatus.Error)
                                        {
                                            subitem.Status = CopyStatus.Error;
                                            subitem.Error = "Needs to be recreated";
                                        }
                                    }
                                }
                            }
                            catch { }
                            if (string.IsNullOrEmpty(item.Error))
                            {
                                item.Status = CopyStatus.Error;
                                item.Error = string.Empty;
                            }
                            var exc = ex;
                            while (exc != null)
                            {
                                lastError = $"{exc.Message} (affected object: {item.Name})";
                                if (exc.Message != item.Error)
                                {
                                    if (item.Error != "")
                                    {
                                        item.Error += ";";
                                    }
                                    //sometimes error messages are duplicated by SMO, discard duplicates
                                    item.Error += string.Join(Environment.NewLine, exc.Message.Split(Environment.NewLine.ToCharArray()).Where(s => !string.IsNullOrEmpty(s)).Distinct());
                                }
                                exc = exc.InnerException;
                                if (item.Parent != null)
                                {
                                    item.Error += $";{item.Parent.Type}: {item.Parent.Name}";
                                }
                            }
                            if (item.Status != CopyStatus.Warning)
                            {
                                errorCount++;
                            }
                        }
                    }
                    lstDelete.ForEach(item => SchemaTransfer.RunInDestination($"SELECT '{item.Replace("'", "''")}'"));
                    lstDelete.Clear();
                    previousListCount = currList.Count;
                    finishedPass1 = errorCount == 0 || errorCount == currList.Count;
                }

                if (CopyConstraints)
                {
                    finishedPass2 = finishedPass1 = errorCount == 0;
                    CopyConstraints = false;
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }
                    SchemaTransfer.RefreshDestination();

                    //indexes: tables should go first
                    log("Processing indexes");
                    foreach (var item in CopyList.Where(i => i.Type == "Table" || i.Type == "View").OrderBy(ix => ix.Type).ToList())
                    {
                        try
                        {
                            SchemaTransfer.ApplyIndexes(item.Object, options.CopyFullText && options.CopyConstraints);
                        }
                        catch (Exception ex)
                        {
                            HandleWarning(item, ex);
                        }
                    }
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    //foreign keys
                    log("Processing foreign keys");
                    foreach (var item in CopyList.Where(i => i.Type == "Table").ToList())
                    {
                        try
                        {
                            SchemaTransfer.ApplyForeignKeys(item.Object, disableNotForReplication);
                        }
                        catch (Exception ex)
                        {
                            HandleWarning(item, ex);
                        }
                    }
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    //checks
                    log("Processing check constraints");
                    foreach (var item in CopyList.Where(i => i.Type == "Table"))
                    {
                        try
                        {
                            SchemaTransfer.ApplyChecks(item.Object, disableNotForReplication);
                        }
                        catch (Exception ex)
                        {
                            HandleWarning(item, ex);
                        }
                    }
                    if (!finishedPass2)
                    {
                        log("Retrying failed objects");
                    }
                }
                else
                {
                    finishedPass2 = true;
                }
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (options.CopyExtendedProperties)
            {
                log("Processing extended properties");
                try
                {
                    SchemaTransfer.CopyExtendedProperties(CopyList.Select(o => o.Object));
                }
                catch (Exception ex)
                {
                    if (options.StopIfErrors)
                    {
                        log($"Error copying extended properties: {ex.Message}");
                    }
                    lastError = ex.Message;
                    errorCount++;
                }
            }

            if (options.CopyPermissions)
            {
                log("Processing permissions");
                try
                {
                    SchemaTransfer.CopyRolePermissions();
                }
                catch (Exception ex)
                {
                    if (options.StopIfErrors)
                    {
                        log($"Error copying permissions: {ex.Message}");
                    }
                    lastError = ex.Message;
                    errorCount++;
                }
            }
            SchemaTransfer.EnableDestinationConstraints();
            if (engine.DisableDisabledObjects)
            {
                SchemaTransfer.DisableDisabledObjects();
            }
            if (options.CopySecurity)
            {
                SchemaTransfer.CopySchemaAuthorization();
            }
            if (options.CopyData)
            {
                SchemaTransfer.RemoveSchemaBindingFromDestination();
            }
            SchemaTransfer.EnableDestinationDDLTriggers();

            //surface the objects that failed
            foreach (var failed in CopyList.Where(o => o.Status == CopyStatus.Error && !string.IsNullOrEmpty(o.Error)))
            {
                log($"  ERROR {failed.Type} {failed.Name}: {failed.Error}");
            }
        }

        private void HandleWarning(SqlSchemaObject item, Exception ex)
        {
            if (item.Status != CopyStatus.Error)
            {
                item.Status = CopyStatus.Warning;
                item.Error = string.Empty;
                var exc = ex;
                while (exc != null)
                {
                    lastError = $"{exc.Message} (affected object: {item.Name})";
                    if (exc.Message != item.Error)
                    {
                        if (item.Error != "")
                        {
                            item.Error += ";";
                        }
                        item.Error += exc.Message;
                    }
                    exc = exc.InnerException;
                }
                errorCount++;
            }
        }
    }
}
