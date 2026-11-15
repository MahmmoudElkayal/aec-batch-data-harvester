using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Reporting
{
    /// <summary>
    /// The one-line status payload. Pure string work, no AutoCAD and no I/O, so the shape
    /// the batch supervisor depends on is checked by tests rather than discovered in
    /// production. A failure carries no units: the drawing was never measured.
    ///
    /// Every verdict carries <c>schema</c> and <c>tool</c>. The supervisor compares
    /// <c>schema</c> before it trusts an <c>.ok</c>, so a result produced by an older
    /// engine is re-harvested instead of merged. The keys are written in a fixed order;
    /// the integrity checks compare status files byte for byte.
    /// </summary>
    public static class StatusJson
    {
        public static string Build(
            bool success,
            string drawingName,
            string message,
            long elapsedMs,
            int rows,
            int attributes,
            int skipped,
            IReadOnlyDictionary<string, int>? unsupported,
            string? unitName,
            int? unitCode)
        {
            var sb = new StringBuilder(256);

            sb.Append('{');
            sb.Append("\"ok\":").Append(success ? "true" : "false");
            sb.Append(",\"schema\":").Append(HarvestSchema.Version.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"tool\":\"").Append(Escape(HarvestSchema.ToolVersion)).Append('"');
            sb.Append(",\"drawing\":\"").Append(Escape(drawingName)).Append('"');
            sb.Append(",\"rows\":").Append(rows.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"attrs\":").Append(attributes.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"skipped\":").Append(skipped.ToString(CultureInfo.InvariantCulture));

            if (unitName is not null && unitCode.HasValue)
            {
                sb.Append(",\"units\":\"").Append(Escape(unitName)).Append('"');
                sb.Append(",\"unitCode\":").Append(unitCode.Value.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(",\"unsupported\":").Append(Unsupported(unsupported));
            sb.Append(",\"unsupportedTotal\":")
              .Append(Total(unsupported).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"ms\":").Append(elapsedMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"message\":\"").Append(Escape(message)).Append('"');
            sb.Append('}');

            return sb.ToString();
        }

        /// <summary>
        /// Stable, sorted object: the caller's dictionary order is incidental, the status
        /// file is not. Empty is written as <c>{}</c> so a reader never has to tell
        /// "absent" from "nothing unsupported".
        /// </summary>
        private static string Unsupported(IReadOnlyDictionary<string, int>? counts)
        {
            if (counts is null || counts.Count == 0)
            {
                return "{}";
            }

            var sorted = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> pair in counts)
            {
                sorted[pair.Key] = pair.Value;
            }

            var sb = new StringBuilder(64);
            sb.Append('{');

            bool first = true;
            foreach (KeyValuePair<string, int> pair in sorted)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                sb.Append('"').Append(Escape(pair.Key)).Append("\":")
                  .Append(pair.Value.ToString(CultureInfo.InvariantCulture));
                first = false;
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static int Total(IReadOnlyDictionary<string, int>? counts)
        {
            if (counts is null)
            {
                return 0;
            }

            int total = 0;
            foreach (int count in counts.Values)
            {
                total += count;
            }

            return total;
        }

        /// <summary>Escapes the five characters JSON requires, plus control characters.</summary>
        internal static string Escape(string value)
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
    }
}
