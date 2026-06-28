using Microsoft.SqlServer.Management.Smo;
using System.Linq;

namespace Sql2SqlCloner.Core.SchemaTransfer
{
    public enum CopyStatus
    {
        None = 0,
        Waiting,
        Ok,
        Warning,
        Error
    }

    public class SqlSchemaObject
    {
        public CopyStatus Status { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public bool CopyData { get; set; }
        public NamedSmoObject Object { get; set; }
        public string Error { get; set; }
        [System.ComponentModel.Browsable(false)]
        public SqlSchemaObject Parent { get; set; }
        [System.ComponentModel.Browsable(false)]
        public string NameWithBrackets => AddBrackets(Name);

        [System.ComponentModel.Browsable(false)]
        public string NameWithoutBrackets => Name.Replace("[", "").Replace("]", "");

        public static string AddBrackets(string itemName)
        {
            if (string.IsNullOrEmpty(itemName))
            {
                return "";
            }
            var nameSplit = itemName.Split('.').ToList();
            if (nameSplit.Count < 2)
            {
                return $"[{itemName}]";
            }
            else
            {
                return $"[{nameSplit[0]}].[{string.Join(".", nameSplit.Skip(1))}]";
            }
        }
    }
}
