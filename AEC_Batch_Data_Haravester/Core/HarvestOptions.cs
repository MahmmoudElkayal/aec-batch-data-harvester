using System;
using System.IO;

namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>
    /// Everything one harvest run needs, resolved from the environment exactly once.
    ///
    /// The old code resolved the output folder twice - once when appending rows, once when
    /// writing the status file - so the two could disagree if the environment changed
    /// between them, and neither was injectable in a test.
    /// </summary>
    public sealed class HarvestOptions
    {
        public const string OutputFolderVariable = "AEC_HARVEST_OUTPUT";
        public const string RootFolderVariable = "AEC_HARVEST_ROOT";
        public const string KeyVariable = "AEC_HARVEST_KEY";

        public const string OutputFileName = "Extracted_BOM.csv";
        public const string StatusFolderName = "_status";
        public const string RowsFolderName = "_rows";

        /// <summary>
        /// Where a failure that has no key to name itself is explained. Fixed name, so the
        /// supervisor can look for exactly one file; no key can collide with it because
        /// every key ends in -&lt;8 hex&gt;.
        /// </summary>
        public const string DiagnosticFileName = "_harvest.error.txt";

        private HarvestOptions(string outputFolder, string key)
        {
            OutputFolder = outputFolder;
            Key = key;
        }

        /// <summary>Where rows and status files go. Never empty.</summary>
        public string OutputFolder { get; }

        /// <summary>
        /// The name stem shared by this drawing's status file and its row file, supplied by
        /// the batch supervisor in <see cref="KeyVariable"/>.
        ///
        /// The plugin never derives it. Deriving it here and again in the BAT was two
        /// implementations of one rule that agreed by luck; the supervisor now owns it, so
        /// there is one answer to "which files belong to this drawing".
        /// </summary>
        public string Key { get; }

        public string StatusFolder => Path.Combine(OutputFolder, StatusFolderName);

        public string RowsFolder => Path.Combine(OutputFolder, RowsFolderName);

        /// <summary>
        /// Reads the environment, or throws. A missing key fails the drawing: falling back
        /// to the drawing name would silently reintroduce collisions between two folders
        /// holding the same file name, which is the whole reason the key exists.
        /// </summary>
        public static HarvestOptions FromEnvironment()
        {
            string? key = Environment.GetEnvironmentVariable(KeyVariable);
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException(
                    KeyVariable + " is not set. The batch supervisor owns the status key, "
                    + "and the plugin will not invent one from the drawing name.");
            }

            return new HarvestOptions(ResolveOutputFolder(), key);
        }

        /// <summary>
        /// AEC_HARVEST_OUTPUT wins; then AEC_HARVEST_ROOT\Output;
        /// otherwise an Output folder beside the loaded assembly.
        /// </summary>
        public static string ResolveOutputFolder()
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable(OutputFolderVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment;
            }

            string? root = Environment.GetEnvironmentVariable(RootFolderVariable);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.Combine(root, "Output");
            }

            string assemblyDirectory =
                Path.GetDirectoryName(typeof(HarvestOptions).Assembly.Location)
                ?? Directory.GetCurrentDirectory();

            return Path.Combine(assemblyDirectory, "Output");
        }
    }
}
