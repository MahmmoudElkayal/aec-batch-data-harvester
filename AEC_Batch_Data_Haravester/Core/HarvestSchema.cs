using System;
using System.Reflection;

namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>
    /// The identity of the output contract, written into every status file.
    ///
    /// Resume is the reason this exists. <c>/append</c> treats a drawing with <c>.ok</c>,
    /// a row file and an attribute file as finished, which is correct only while the
    /// engine still produces the same thing. Add constant attributes, and an old <c>.ok</c>
    /// is a promise the new engine no longer keeps: the merged CSV would contain drawings
    /// extracted under two different rules with no way to tell which is which.
    ///
    /// Bump <see cref="Version"/> whenever a change can alter rows, columns or attribute
    /// semantics. The supervisor refuses verdicts from another version, so those drawings
    /// are harvested again instead of being trusted.
    ///
    /// Version 3: nested block provenance - Container, Depth and IsXref columns.
    /// Version 2: unsupported-entity census and constant attributes.
    /// Version 1: entity + attribute CSV as first shipped.
    /// </summary>
    public static class HarvestSchema
    {
        public const int Version = 3;

        /// <summary>
        /// The assembly's informational version with the source-revision suffix removed:
        /// "0.5.0" rather than "0.5.0+5b5597d". The status file is read by people and by
        /// scripts, and a hash that changes on every build is noise in both.
        /// </summary>
        public static string ToolVersion { get; } = ReadToolVersion();

        private static string ReadToolVersion()
        {
            string? informational = typeof(HarvestSchema).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (string.IsNullOrWhiteSpace(informational))
            {
                return "0.0.0";
            }

            int revision = informational!.IndexOf('+');
            return revision > 0 ? informational.Substring(0, revision) : informational;
        }
    }
}
