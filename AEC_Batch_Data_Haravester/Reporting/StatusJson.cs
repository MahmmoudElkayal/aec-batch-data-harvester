using System.Globalization;
using System.Text;

namespace AEC_Batch_Data_Haravester.Reporting
{
    /// <summary>
    /// The one-line status payload. Pure string work, no AutoCAD and no I/O, so the shape
    /// the batch supervisor depends on is checked by tests rather than discovered in
    /// production. A failure carries no units: the drawing was never measured.
    /// </summary>
    public static class StatusJson
    {
        public static string Build(
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
            sb.Append(",\"drawing\":\"").Append(Escape(drawingName)).Append('"');
            sb.Append(",\"rows\":").Append(rows.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"skipped\":").Append(skipped.ToString(CultureInfo.InvariantCulture));

            if (unitName is not null && unitCode.HasValue)
            {
                sb.Append(",\"units\":\"").Append(Escape(unitName)).Append('"');
                sb.Append(",\"unitCode\":").Append(unitCode.Value.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(",\"ms\":").Append(elapsedMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"message\":\"").Append(Escape(message)).Append('"');
            sb.Append('}');

            return sb.ToString();
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
