using System;
using System.Globalization;
using System.Linq;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Output
{
    /// <summary>
    /// The CSV schema, in one place.
    ///
    /// Before this, the header was a string array and each row was a hand-written
    /// <c>string.Join</c> listing the same values in the same order - two lists to keep in
    /// step by hand. Here the header and the values come from the same array of column
    /// definitions, so a column cannot be added to one and forgotten in the other, and
    /// adding a column is a one-line edit.
    /// </summary>
    public sealed class CsvFormatter
    {
        private const string NumericFormat = "0.000";

        private static readonly (string Header, Func<HarvestRow, string> Get)[] Columns =
        {
            ("Drawing",         r => Escape(r.Drawing)),
            ("Space",           r => Escape(r.Space)),
            ("EntityType",      r => Escape(r.EntityType)),
            ("Name",            r => Escape(r.Name)),
            ("BlockDefinition", r => Escape(r.BlockDefinition)),
            ("Layer",           r => Escape(r.Layer)),
            ("Handle",          r => Escape(r.Handle)),
            ("X",               r => Number(r.X)),
            ("Y",               r => Number(r.Y)),
            ("Z",               r => Number(r.Z)),
            ("Length",          r => Number(r.Length)),
            ("Unit",            r => Escape(r.Unit)),
            ("UnitCode",        r => r.UnitCode.ToString(CultureInfo.InvariantCulture))
        };

        public string Header { get; }

        public CsvFormatter()
        {
            Header = string.Join(",", Columns.Select(c => c.Header));
        }

        public string Row(HarvestRow row)
        {
            return string.Join(",", Columns.Select(c => c.Get(row)));
        }

        /// <summary>Column names, for tests and documentation.</summary>
        public static string[] ColumnNames()
        {
            return Columns.Select(c => c.Header).ToArray();
        }

        internal static string Number(double? value)
        {
            return value.HasValue
                ? value.Value.ToString(NumericFormat, CultureInfo.InvariantCulture)
                : string.Empty;
        }

        /// <summary>Minimal RFC 4180 quoting for values that contain , or " or a newline.</summary>
        internal static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            bool mustQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            return mustQuote
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }
    }
}
