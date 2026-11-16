using System;
using System.Linq;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Output
{
    /// <summary>
    /// The attribute file's schema, in one place - same rule as <see cref="CsvFormatter"/>:
    /// the header and the values come from the same column list, so they cannot drift.
    ///
    /// Long format (one row per attribute) rather than extra columns on the entity row,
    /// because an attribute tag may itself be "#": a wide schema would need a column named
    /// "#" and would change with every batch.
    ///
    /// Every column is text, including <c>IsConstant</c> (1 or 0), so the Excel guard
    /// applies to all of them. That is safe: an attribute value is data, never a number
    /// the pipeline later does arithmetic on.
    /// </summary>
    public sealed class AttributeCsvFormatter
    {
        private readonly bool _excelSafe;

        private static readonly (string Header, Func<HarvestAttributeRow, string> Get)[] Columns =
        {
            ("Drawing",      r => r.Drawing),
            ("EntityHandle", r => r.EntityHandle),
            ("Container",    r => r.Container),
            ("Tag",          r => r.Tag),
            ("Value",        r => r.Value),
            ("IsConstant",   r => r.IsConstant ? "1" : "0")
        };

        public AttributeCsvFormatter(bool excelSafe = false)
        {
            _excelSafe = excelSafe;
            Header = string.Join(",", Columns.Select(c => c.Header));
        }

        public string Header { get; }

        public string Row(HarvestAttributeRow row)
        {
            return string.Join(",", Columns.Select(c => Cell(c.Get(row))));
        }

        /// <summary>Column names, for tests and documentation.</summary>
        public static string[] ColumnNames()
        {
            return Columns.Select(c => c.Header).ToArray();
        }

        private string Cell(string value)
        {
            return CsvFormatter.Escape(_excelSafe ? CsvFormatter.ExcelGuard(value) : value);
        }
    }
}
