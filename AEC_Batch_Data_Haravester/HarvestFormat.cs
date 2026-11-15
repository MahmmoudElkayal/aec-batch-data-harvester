using System;
using System.Globalization;
using System.Text;

namespace AEC_Batch_Data_Haravester
{
    /// <summary>
    /// Everything the harvester writes, as pure string work: CSV rows and the one-line
    /// status JSON.
    ///
    /// It deliberately references no AutoCAD type. That is not tidiness - a type that owns
    /// a static field of an AutoCAD type cannot even be loaded without the CAD assemblies,
    /// so the schema and the escaping could not be checked outside a CAD host. Here they
    /// can: tests/Harvest.Checks runs them in a plain console process, which is how a
    /// column drifting out of step with its header gets caught by a test instead of by
    /// somebody's spreadsheet.
    /// </summary>
    internal static class HarvestFormat
    {
        private const string NumericFormat = "0.000";

        // One source of truth for order: Row() below must emit these in this sequence.
        internal static readonly string[] CsvHeader =
        {
            "Drawing", "Space", "EntityType", "Name", "BlockDefinition", "Layer", "Handle",
            "X", "Y", "Z", "Length", "Unit", "UnitCode"
        };

        internal static string Row(
            string drawing, string space, string entityType, string name,
            string blockDefinition, string layer, string handle,
            double? x, double? y, double? z, double? length,
            string unit, int unitCode)
        {
            return string.Join(",", new[]
            {
                Escape(drawing),
                Escape(space),
                Escape(entityType),
                Escape(name),
                Escape(blockDefinition),
                Escape(layer),
                Escape(handle),
                Number(x),
                Number(y),
                Number(z),
                Number(length),
                Escape(unit),
                unitCode.ToString(CultureInfo.InvariantCulture)
            });
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

        /// <summary>
        /// Builds the one-line status JSON. Units arrive as primitives, not as the AutoCAD
        /// UnitsValue enum, for the same reason the class exists at all.
        /// A failure carries no units: the drawing was not measured.
        /// </summary>
        internal static string BuildStatusJson(
            bool success,
            string drawingName,
            string message,
            long elapsedMs,
            int rows,
            int skipped,
            string? unitName,
            int? unitCode)
        {
            var sb = new StringBuilder(256);

            sb.Append('{');
            sb.Append("\"ok\":").Append(success ? "true" : "false");
            sb.Append(",\"drawing\":\"").Append(JsonEscape(drawingName)).Append('"');
            sb.Append(",\"rows\":").Append(rows.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"skipped\":").Append(skipped.ToString(CultureInfo.InvariantCulture));

            if (unitName is not null && unitCode.HasValue)
            {
                sb.Append(",\"units\":\"").Append(JsonEscape(unitName)).Append('"');
                sb.Append(",\"unitCode\":").Append(unitCode.Value.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(",\"ms\":").Append(elapsedMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"message\":\"").Append(JsonEscape(message)).Append('"');
            sb.Append('}');

            return sb.ToString();
        }

        /// <summary>Escapes the five characters JSON requires, plus control characters.</summary>
        internal static string JsonEscape(string value)
        {
            var sb = new StringBuilder(value.Length + 8);

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            return sb.ToString();
        }

        /// <summary>Drawing name -> a legal Windows file name stem.</summary>
        internal static string SanitizeFileName(string name)
        {
            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            string result = builder.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "drawing" : result;
        }
    }
}
