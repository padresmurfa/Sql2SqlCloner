using Microsoft.Data.SqlClient;
using Sql2SqlCloner.Core;
using Sql2SqlCloner.Core.DataTransfer;
using Sql2SqlCloner.Core.SchemaTransfer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Sql2SqlCloner
{
    public static class Program
    {
        private static int Main(string[] args)
        {
            string configPath = null;
            var schemaOnly = false;
            var dataOnly = false;
            var assumeYes = false;

            foreach (var arg in args)
            {
                switch (arg.ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        PrintUsage();
                        return 0;
                    case "--schema-only":
                        schemaOnly = true;
                        break;
                    case "--data-only":
                        dataOnly = true;
                        break;
                    case "--yes":
                    case "-y":
                        assumeYes = true;
                        break;
                    default:
                        if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                        {
                            configPath = arg.Substring("--config=".Length);
                        }
                        else if (string.Equals(arg, "--config", StringComparison.OrdinalIgnoreCase))
                        {
                            //value is the following argument
                            var idx = Array.IndexOf(args, arg);
                            if (idx >= 0 && idx + 1 < args.Length)
                            {
                                configPath = args[idx + 1];
                            }
                        }
                        else if (arg.StartsWith("--password", StringComparison.OrdinalIgnoreCase) ||
                                 arg.StartsWith("--pwd", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.Error.WriteLine("Passwords must not be passed on the command line. " +
                                "Use the SQL2SQL_SOURCE_PASSWORD / SQL2SQL_DEST_PASSWORD / SQL2SQL_DAC_PASSWORD " +
                                "environment variables, or enter them at the masked prompt.");
                            return 2;
                        }
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(configPath))
            {
                Console.Error.WriteLine("Error: --config <path.yaml> is required.");
                Console.Error.WriteLine();
                PrintUsage();
                return 2;
            }

            if (schemaOnly && dataOnly)
            {
                Console.Error.WriteLine("Error: --schema-only and --data-only are mutually exclusive.");
                return 2;
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.Error.WriteLine("Cancellation requested, finishing current operation...");
                cts.Cancel();
            };

            try
            {
                CloneConfig.Current = CloneConfig.Load(configPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading configuration: {ex.Message}");
                return 2;
            }

            var options = CloneConfig.Current.Options;
            if (string.IsNullOrWhiteSpace(CloneConfig.Current.Source?.ConnectionString) ||
                string.IsNullOrWhiteSpace(CloneConfig.Current.Destination?.ConnectionString))
            {
                Console.Error.WriteLine("Error: both source.connectionString and destination.connectionString are required.");
                return 2;
            }

            var doSchema = options.CopySchema && !dataOnly;
            var doData = options.CopyData && !schemaOnly;
            if (!doSchema && !doData)
            {
                Console.Error.WriteLine("Error: nothing to do (check options.copySchema / options.copyData and --schema-only / --data-only).");
                return 2;
            }

            string sourceConn, destConn, dacConn = null;
            try
            {
                sourceConn = ResolvePassword(CloneConfig.Current.Source.ConnectionString, "SQL2SQL_SOURCE_PASSWORD", "source");
                destConn = ResolvePassword(CloneConfig.Current.Destination.ConnectionString, "SQL2SQL_DEST_PASSWORD", "destination");
                if (doSchema && options.DecryptObjects)
                {
                    dacConn = BuildDacConnectionString(sourceConn);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error resolving connection credentials: {ex.Message}");
                return 2;
            }

            void Log(string message) => Console.WriteLine(message);

            SqlSchemaTransfer schemaTransfer;
            try
            {
                Log("Connecting and loading source/destination metadata...");
                schemaTransfer = new SqlSchemaTransfer(sourceConn, destConn, false, dacConn, cts.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error connecting: {Flatten(ex)}");
                return 1;
            }

            if (schemaTransfer.SourceObjects == null || schemaTransfer.SourceObjects.Count == 0)
            {
                Console.Error.WriteLine("No SQL objects found in source database to copy from.");
                return 1;
            }

            if (schemaTransfer.SameDatabase)
            {
                Console.Error.WriteLine("Source and destination databases are the same. Aborting.");
                return 2;
            }

            var selectOnlyTables = doData && !doSchema;
            var selected = SelectObjects(schemaTransfer.SourceObjects, selectOnlyTables, doData);
            if (selected.Count == 0)
            {
                Console.Error.WriteLine("No objects selected to copy (check schemas / excludeObjects filters).");
                return 1;
            }

            var totalErrors = 0;

            if (doSchema)
            {
                if (options.ClearDestinationDatabase && !Confirm(assumeYes,
                        $"The destination database '{schemaTransfer.DestinationCxInfo()}' is about to be CLEARED. Continue?"))
                {
                    Console.Error.WriteLine("Aborted by user.");
                    return 130;
                }
                Log("");
                Log("=== Copying schema ===");
                var runner = new SchemaCopyRunner(schemaTransfer, selected, options.DisableNotForReplication, Log, cts.Token);
                var schemaErrors = runner.Run();
                totalErrors += schemaErrors;
                Log(schemaErrors == 0
                    ? "Schema copy completed successfully."
                    : $"Schema copy completed with {schemaErrors} error(s). Last error: {runner.LastError}");
                if (schemaErrors > 0 && options.StopIfErrors && selected.OfType<SqlSchemaTable>().Any(t => t.Status == CopyStatus.Error))
                {
                    Console.Error.WriteLine("Stopping because of schema errors on tables (stopIfErrors=true).");
                    return 1;
                }
            }

            if (doData && !cts.IsCancellationRequested)
            {
                var tablesToCopy = selected.OfType<SqlSchemaTable>().Where(t => t.CopyData).ToList();
                if (tablesToCopy.Count == 0)
                {
                    Log("No tables selected to copy data from.");
                }
                else
                {
                    if (options.DeleteDestinationTables && selectOnlyTables && !Confirm(assumeYes,
                            $"The data in destination database '{schemaTransfer.DestinationCxInfo()}' is about to be DELETED. Continue?"))
                    {
                        Console.Error.WriteLine("Aborted by user.");
                        return 130;
                    }
                    Log("");
                    Log("=== Copying data ===");
                    SqlDataTransfer dataTransfer;
                    try
                    {
                        dataTransfer = new SqlDataTransfer(sourceConn, destConn, schemaTransfer.LstPostExecutionExecute)
                        {
                            ConfirmDeleteNonCompliantData = msg =>
                                Confirm(assumeYes, $"Constraints could not be enabled ({msg}). Delete non-compliant data?")
                        };
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error preparing data transfer: {Flatten(ex)}");
                        return 1;
                    }
                    var convertCollation = options.CopyCollation == SqlCollationAction.Set_destination_db_collation;
                    var runner = new DataCopyRunner(tablesToCopy, dataTransfer, schemaTransfer, convertCollation, selectOnlyTables, Log, cts.Token);
                    var dataErrors = runner.Run();
                    totalErrors += dataErrors;
                    Log(dataErrors == 0
                        ? "Data copy completed successfully."
                        : $"Data copy completed with {dataErrors} error(s). Last error: {runner.LastError}");
                }
            }

            if (cts.IsCancellationRequested)
            {
                Console.Error.WriteLine("Operation cancelled.");
                return 130;
            }

            Log("");
            Log(totalErrors == 0
                ? $"Done. Copied from '{schemaTransfer.SourceCxInfo()}' to '{schemaTransfer.DestinationCxInfo()}'."
                : $"Finished with {totalErrors} error(s).");
            return totalErrors == 0 ? 0 : 1;
        }

        /// <summary>
        /// Replicates the object selection the WinForms ChooseSchemas dialog performed: schema include
        /// filter, excludeObjects, FilterDataLoading (TOP / WHERE / ORDER BY) and the CopyData flag.
        /// </summary>
        private static IList<SqlSchemaObject> SelectObjects(IList<SqlSchemaObject> sourceObjects, bool selectOnlyTables, bool copyingData)
        {
            var engine = CloneConfig.Current.Engine;
            var excludeObjects = ParseNameList(engine.ExcludeObjects);
            var excludeDataLoading = ParseNameList(engine.ExcludeDataLoading);
            var filters = ParseFilterDataLoading(engine.FilterDataLoading);

            var items = sourceObjects.Where(IncludeBySchema).ToList();
            if (selectOnlyTables)
            {
                items = items.Where(i => i.Type == "Table").ToList();
            }

            var selected = new List<SqlSchemaObject>();
            foreach (var item in items)
            {
                if (CheckIfInList(item.Name, excludeObjects))
                {
                    continue;
                }
                if (item is SqlSchemaTable table)
                {
                    var key = NormalizeName(item.Name);
                    if (filters.TryGetValue(key, out var f))
                    {
                        if (!string.IsNullOrEmpty(f.Where))
                        {
                            table.WhereFilter = f.Where;
                        }
                        if (f.Top > 0)
                        {
                            table.TopRecords = f.Top;
                        }
                        if (!string.IsNullOrEmpty(f.OrderBy))
                        {
                            table.OrderByFields = f.OrderBy;
                        }
                    }
                }
                selected.Add(item);
            }

            //drop children (e.g. triggers) whose parent object was excluded
            selected = selected.Where(s => s.Parent == null || selected.Contains(s.Parent)).ToList();

            if (copyingData)
            {
                foreach (var table in selected.OfType<SqlSchemaTable>())
                {
                    table.CopyData = !CheckIfInList(table.Name, excludeDataLoading);
                }
            }

            return selected;
        }

        private static bool IncludeBySchema(SqlSchemaObject item)
        {
            var include = CloneConfig.Current.IncludeSchemas;
            if (include == null)
            {
                return true; //no explicit schema list -> all non-system schemas
            }
            if (item.Type == "Schema")
            {
                return CloneConfig.Current.IncludesSchema(item.Name);
            }
            //children (triggers) follow their parent table/view
            var name = item.Parent != null ? item.Parent.Name : item.Name;
            var dotIdx = name.IndexOf('.');
            if (dotIdx <= 0)
            {
                //not schema-qualified: database-level object (user, role, assembly, DDL trigger...) -> always include
                return true;
            }
            return CloneConfig.Current.IncludesSchema(name.Substring(0, dotIdx));
        }

        private struct DataFilter
        {
            public string Where;
            public long Top;
            public string OrderBy;
        }

        private static IDictionary<string, DataFilter> ParseFilterDataLoading(string filterDataLoading)
        {
            var result = new Dictionary<string, DataFilter>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(filterDataLoading))
            {
                return result;
            }
            foreach (var entry in filterDataLoading.Replace(Environment.NewLine, "").Split(',')
                         .Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var split = entry.Trim().Split(' ').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                if (split.Count < 3)
                {
                    continue;
                }
                var key = NormalizeName(split[0]);
                result.TryGetValue(key, out var filter);
                if (split[1].Equals("WHERE", StringComparison.OrdinalIgnoreCase))
                {
                    filter.Where = string.Join(" ", new[] { "WHERE", SqlSchemaObject.AddBrackets(split[2]) }.Concat(split.Skip(3))).Trim();
                }
                else if (split[1].Equals("TOP", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(split[2], out var top))
                    {
                        filter.Top = top;
                    }
                }
                else if (split[1].Equals("ORDER", StringComparison.OrdinalIgnoreCase) &&
                         split[2].Equals("BY", StringComparison.OrdinalIgnoreCase) && split.Count > 3)
                {
                    filter.OrderBy = "ORDER BY " + string.Join(" ", split.Skip(3));
                }
                result[key] = filter;
            }
            return result;
        }

        private static string NormalizeName(string name) =>
            name?.Replace("[", "").Replace("]", "").Trim().ToUpperInvariant();

        private static IList<string> ParseNameList(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split(',').Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Replace("[", "").Replace("]", "").Trim()).ToList();

        private static bool CheckIfInList(string item, IList<string> lstExclude)
        {
            if (lstExclude.Any(s => s.Equals(item, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            foreach (var itmExclude in lstExclude.Where(i => i.IndexOf("*", StringComparison.Ordinal) >= 0))
            {
                if (item.StartsWith(itmExclude.Substring(0, itmExclude.IndexOf("*", StringComparison.Ordinal)),
                        StringComparison.InvariantCultureIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string ResolvePassword(string connectionString, string envVar, string label)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var hasUser = !string.IsNullOrEmpty(builder.UserID);
            var hasPassword = !string.IsNullOrEmpty(builder.Password);
            if (!hasUser || hasPassword || builder.IntegratedSecurity ||
                builder.Authentication != SqlAuthenticationMethod.NotSpecified)
            {
                //no SQL-auth password needed (integrated / Azure AD / already supplied / no user)
                return connectionString;
            }

            var password = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrEmpty(password))
            {
                if (Console.IsInputRedirected)
                {
                    throw new Exception($"No password for {label} connection ({builder.UserID}@{builder.DataSource}). " +
                        $"Set the {envVar} environment variable (input is not interactive).");
                }
                password = PromptMasked($"Password for {label} ({builder.UserID}@{builder.DataSource}): ");
            }
            builder.Password = password;
            return builder.ConnectionString;
        }

        private static string BuildDacConnectionString(string sourceConnectionString)
        {
            var builder = new SqlConnectionStringBuilder(sourceConnectionString);
            if (string.IsNullOrEmpty(builder.InitialCatalog))
            {
                throw new Exception("DAC database (Initial Catalog) not found in source connection string.");
            }
            if (string.IsNullOrEmpty(builder.DataSource))
            {
                throw new Exception("DAC host (Data Source) not found in source connection string.");
            }

            var dacPassword = "";
            if (string.Equals(builder.UserID, "sa", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(builder.Password))
            {
                dacPassword = builder.Password;
            }
            if (string.IsNullOrEmpty(dacPassword))
            {
                dacPassword = Environment.GetEnvironmentVariable("SQL2SQL_DAC_PASSWORD");
            }
            if (string.IsNullOrEmpty(dacPassword))
            {
                if (Console.IsInputRedirected)
                {
                    throw new Exception("No 'sa' password available for the decrypt DAC connection. " +
                        "Set the SQL2SQL_DAC_PASSWORD environment variable (input is not interactive).");
                }
                dacPassword = PromptMasked("Enter 'sa' password (for object decryption via DAC): ");
            }
            if (string.IsNullOrEmpty(dacPassword))
            {
                throw new Exception("A password is required for the decrypt DAC connection.");
            }

            return $"Packet Size=4096;User Id=sa;Password={dacPassword};Data Source=ADMIN:{builder.DataSource};" +
                   $"Initial Catalog={builder.InitialCatalog};TrustServerCertificate=true";
        }

        private static string PromptMasked(string prompt)
        {
            Console.Write(prompt);
            var sb = new StringBuilder();
            ConsoleKeyInfo key;
            while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
            {
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Length--;
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                }
            }
            Console.WriteLine();
            return sb.ToString();
        }

        private static bool Confirm(bool assumeYes, string message)
        {
            if (assumeYes)
            {
                return true;
            }
            if (Console.IsInputRedirected)
            {
                //non-interactive without --yes: do not perform destructive action
                return false;
            }
            Console.Write($"{message} [y/N] ");
            var answer = Console.ReadLine();
            return answer != null && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ||
                                      answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
        }

        private static string Flatten(Exception ex)
        {
            var sb = new StringBuilder();
            while (ex != null)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" -> ");
                }
                sb.Append(ex.Message);
                ex = ex.InnerException;
            }
            return sb.ToString();
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"Sql2SqlCloner - copy a SQL Server schema and/or data between databases.

Usage:
  Sql2SqlCloner --config <job.yaml> [--schema-only | --data-only] [--yes]

Options:
  --config <path>   Path to the YAML job file (required). Also accepts --config=<path>.
  --schema-only     Copy schema only (ignore options.copyData).
  --data-only       Copy data only (ignore options.copySchema).
  --yes, -y         Answer 'yes' to destructive confirmations (clear/delete).
  --help, -h        Show this help.

Passwords are never accepted as command-line arguments. For SQL-auth connection
strings that omit the password, set one of these environment variables, otherwise
you will be prompted with masked input:
  SQL2SQL_SOURCE_PASSWORD   password for the source connection
  SQL2SQL_DEST_PASSWORD     password for the destination connection
  SQL2SQL_DAC_PASSWORD      'sa' password for the decrypt DAC (when decryptObjects=true)

See job.sample.yaml for the full configuration reference.");
        }
    }
}
