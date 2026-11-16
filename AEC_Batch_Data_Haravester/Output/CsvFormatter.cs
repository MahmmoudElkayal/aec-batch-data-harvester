using System;
using System.Buffers;
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
    ///
    /// Text columns go through <see cref="ExcelGuard"/> and quoting; numeric columns do
    /// neither. That split is what makes the opt-in Excel guard safe: a negative
    /// coordinate must never gain an apostrophe, because then it is no longer a number.
    /// </summary>
    public sealed class CsvFormatter
    {
        private const string NumericFormat = "0.000";

        // One allocation for the life of the process. The previous version allocated a
        // four-element array on every cell, which is 6,748 wasted arrays per run.
        private static readonly SearchValues<char> QuotingSpecials =
            SearchValues.Create(",\"\r\n");

        private readonly bool _excelSafe;

        private static readonly (string Header, Func<HarvestRow, string> Get, bool Text)[] Columns =
        {
            ("Drawing",         r => r.Drawing,         true),
            ("Space",           r => r.Space,           true),
            ("EntityType",      r => r.EntityType,      true),
            ("Name",            r => r.Name,            true),
            ("BlockDefinition", r => r.BlockDefinition, true),
            ("Layer",           r => r.Layer,           true),
            ("Handle",          r => r.Handle,          true),
            ("Container",       r => r.Container,       true),
            ("Depth",           r => r.Depth.ToString(CultureInfo.InvariantCulture), false),
            ("IsXref",          r => r.IsXref ? "1" : "0", false),
            ("X",               r => Number(r.X),       false),
            ("Y",               r => Number(r.Y),       false),
            ("Z",               r => Number(r.Z),       false),
            ("Length",          r => Number(r.Length),  false),
            ("Area",            r => Number(r.Area),    false),
            ("Unit",            r => r.Unit,            true),
            ("UnitCode",        r => r.UnitCode.ToString(CultureInfo.InvariantCulture), false)
        };

        public CsvFormatter(bool excelSafe = false)
        {
            _excelSafe = excelSafe;
            Header = string.Join(",", Columns.Select(c => c.Header));
        }

        public string Header { get; }

        public string Row(HarvestRow row)
        {
            return string.Join(",", Columns.Select(c => Cell(c.Get(row), c.Text)));
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

            bool mustQuote = value.AsSpan().IndexOfAny(QuotingSpecials) >= 0;
            return mustQuote
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        /// <summary>
        /// Excel interprets a leading = + - @ or tab as the start of a formula. Prefixing
        /// an apostrophe forces text. Off by default: "+", "-" and "@" are legal leading
        /// characters of a layer name, so this changes real data when it fires.
        /// </summary>
        internal static string ExcelGuard(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            char first = value[0];
            if (first == '=' || first == '+' || first == '-' || first == '@' || first == '\t')
            {
                return "'" + value;
            }

            return value;
        }

        private string Cell(string value, bool text)
        {
            if (!text)
            {
                return value;
            }

            return Escape(_excelSafe ? ExcelGuard(value) : value);
        }
    }
}
