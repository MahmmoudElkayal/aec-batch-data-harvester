namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Text that goes into a CSV field must not contain a raw line break.
    ///
    /// The pipeline is deliberately line-oriented: the merge concatenates files line by
    /// line, the status file records a row count, and the supervisor counts lines to
    /// compare against it. A quoted field containing a real newline is valid RFC 4180 but
    /// breaks every one of those counts, and turns one MText note into two apparent rows.
    /// Rendering the break as the two characters \n keeps the row count honest and keeps
    /// the break visible; nothing else about the text is changed.
    /// </summary>
    internal static class TextValue
    {
        public static string SingleLine(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Replace("\n", "\\n");
        }
    }
}
