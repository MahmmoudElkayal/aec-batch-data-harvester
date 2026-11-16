using System;
using System.Collections.Generic;
using System.IO;

namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>One drawing in a batch: the supervisor's key, and where the DWG is.</summary>
    public sealed record BatchEntry(string Key, string Path);

    /// <summary>
    /// Parses the supervisor's manifest. The format is one line per drawing,
    /// <c>key|full path</c>, which is the same shape tools\build-manifest.ps1 writes.
    ///
    /// Both halves validate. A malformed line that reached this point means the two sides
    /// disagree about the format, and guessing would produce a status file under a name the
    /// supervisor will never look for - a silent failure, which is the one thing this
    /// pipeline must not do.
    /// </summary>
    public static class BatchManifest
    {
        private static readonly char[] IllegalKeyCharacters =
            { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        public static IReadOnlyList<BatchEntry> ReadFile(string manifestPath)
        {
            string[] lines = File.ReadAllLines(manifestPath);
            return Parse(lines);
        }

        public static IReadOnlyList<BatchEntry> Parse(IEnumerable<string> lines)
        {
            var entries = new List<BatchEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int lineNumber = 0;

            foreach (string raw in lines)
            {
                lineNumber++;

                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int separator = line.IndexOf('|');
                if (separator <= 0 || separator == line.Length - 1)
                {
                    throw new FormatException(
                        "manifest line " + lineNumber + " is not 'key|path': " + line);
                }

                string key = line.Substring(0, separator).Trim();
                string path = line.Substring(separator + 1).Trim();

                if (key.Length == 0 || key.IndexOfAny(IllegalKeyCharacters) >= 0)
                {
                    // The key becomes a file name; anything else would put status files
                    // somewhere nobody looks.
                    throw new FormatException(
                        "manifest line " + lineNumber + " has an unusable key: " + key);
                }

                if (!seen.Add(key))
                {
                    throw new FormatException(
                        "manifest line " + lineNumber + " repeats the key: " + key);
                }

                entries.Add(new BatchEntry(key, path));
            }

            return entries;
        }
    }
}
