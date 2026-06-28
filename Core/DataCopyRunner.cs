using Microsoft.SqlServer.Management.Smo;
using Sql2SqlCloner.Core.DataTransfer;
using Sql2SqlCloner.Core.SchemaTransfer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Sql2SqlCloner.Core
{
    /// <summary>
    /// Headless port of the former <c>CopyTabledata</c> WinForms orchestration
    /// (constructor SELECT-building + <c>DoWork</c>). Builds a per-table source <c>SELECT</c>
    /// (honoring TOP / global TOP / collation conversion / WHERE / ORDER BY) and bulk-copies the
    /// rows into the destination table, remapping the destination schema when renaming.
    /// </summary>
    public class DataCopyRunner
    {
        private sealed class TablePlan
        {
            public SqlSchemaTable Item;
            public string TableName;
            public string Sql;
            public long Top;
        }

        private readonly SqlDataTransfer DataTransfer;
        private readonly SqlSchemaTransfer SchemaTransfer;
        private readonly Action<string> log;
        private readonly CancellationToken token;
        private readonly IList<TablePlan> plans = new List<TablePlan>();
        private int errorCount;
        private string lastError = "";

        public IList<SqlSchemaTable> CopyList { get; }
        public int ErrorCount => errorCount;
        public string LastError => lastError;

        public DataCopyRunner(IList<SqlSchemaTable> list, SqlDataTransfer dataTransfer, SqlSchemaTransfer schemaTransfer,
            bool convertCollation, bool selectOnlyTables, Action<string> log, CancellationToken token)
        {
            CopyList = list;
            DataTransfer = dataTransfer;
            SchemaTransfer = schemaTransfer;
            this.log = log ?? (_ => { });
            this.token = token;

            var options = CloneConfig.Current.Options;
            //refresh so freshly created (and possibly renamed) destination tables are visible
            if (SchemaTransfer.DestinationObjects == null || SchemaTransfer.DestinationObjects.Count == 0 || convertCollation)
            {
                SchemaTransfer.RefreshDestinationObjects();
            }

            if (selectOnlyTables && options.DeleteDestinationTables)
            {
                log("Deleting existing data from destination tables");
                DataTransfer.DisableAllDestinationConstraints();
                DataTransfer.DeleteDestinationDatabase();
            }

            var sourceTables = SchemaTransfer.SourceObjects.OfType<SqlSchemaTable>()
                .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Object as Table, StringComparer.OrdinalIgnoreCase);
            var destinationTables = SchemaTransfer.DestinationObjects.OfType<SqlSchemaTable>()
                .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Object as Table, StringComparer.OrdinalIgnoreCase);

            var globalTop = CloneConfig.Current.Engine.GlobalTop;
            if (globalTop < 0)
            {
                globalTop = 0;
            }

            foreach (var item in list)
            {
                var itemTopRecords = item.TopRecords;
                var stritemTopRecords = "";
                if (itemTopRecords <= 0)
                {
                    itemTopRecords = globalTop;
                }
                if (globalTop > 0 && itemTopRecords > 0 && itemTopRecords > globalTop)
                {
                    itemTopRecords = globalTop;
                }
                if (itemTopRecords > 0)
                {
                    if (item.RowCount < itemTopRecords)
                    {
                        itemTopRecords = item.RowCount;
                    }
                    stritemTopRecords = $" TOP {itemTopRecords}";
                }
                else
                {
                    itemTopRecords = item.RowCount;
                }

                var fields = " *";
                if (convertCollation && sourceTables.TryGetValue(item.Name, out var sourceTable))
                {
                    if (destinationTables.TryGetValue(item.Name, out var destinationTable))
                    {
                        var selectList = new StringBuilder();
                        foreach (Column col in sourceTable.Columns)
                        {
                            if (!col.Computed)
                            {
                                selectList.Append(selectList.Length == 0 ? " " : ",");
                                if (!string.IsNullOrEmpty(col.Collation))
                                {
                                    selectList.Append(col).Append(" COLLATE ").Append(
                                        destinationTable.Columns[col.Name].Collation).Append(" AS ").Append(col);
                                }
                                else
                                {
                                    selectList.Append(col);
                                }
                            }
                        }
                        fields = selectList.ToString();
                    }
                }

                plans.Add(new TablePlan
                {
                    Item = item,
                    TableName = item.NameWithBrackets,
                    Sql = $"SELECT{stritemTopRecords}{fields} FROM {item.NameWithBrackets} WITH(NOLOCK) {item.WhereFilter} {item.OrderByFields}".Trim(),
                    Top = itemTopRecords
                });
            }
        }

        /// <summary>Runs the full data copy. Returns the number of errors encountered.</summary>
        public int Run()
        {
            DoWork();
            return errorCount;
        }

        private void DoWork()
        {
            var engine = CloneConfig.Current.Engine;
            DataTransfer.DisableAllDestinationConstraints();

            var current = 0;
            foreach (var plan in plans)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }
                current++;
                try
                {
                    if (plan.Top != 0)
                    {
                        DataTransfer.TransferData(plan.TableName, plan.Sql);
                    }
                    //enable table constraints for standalone tables to avoid a single fat transaction at the end
                    if (!plan.Item.HasRelationships)
                    {
                        DataTransfer.EnableTableConstraints(plan.TableName);
                    }
                    log($"  [{current}/{plans.Count}] {plan.Item.Name}: {plan.Top} records copied");
                }
                catch (Exception exc)
                {
                    lastError = exc.Message;
                    if (exc.InnerException != null && exc.Message != exc.InnerException.Message)
                    {
                        lastError = $"{exc.Message}. {exc.InnerException.Message}";
                    }
                    log($"  [{current}/{plans.Count}] {plan.Item.Name}: ERROR {lastError}");
                    errorCount++;
                }
            }

            try
            {
                SchemaTransfer?.ReAddSchemaBindingToDestination();
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                log($"Error re-adding schema binding: {lastError}");
                errorCount++;
            }

            if (errorCount == 0)
            {
                log("Enabling constraints");
                try
                {
                    DataTransfer.EnableAllDestinationConstraints();
                    if (engine.DisableDisabledObjects)
                    {
                        DataTransfer.DisableDisabledObjects();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    log($"Completed with errors, constraints not enabled: {ex.Message}");
                    errorCount++;
                }
            }
        }
    }
}
