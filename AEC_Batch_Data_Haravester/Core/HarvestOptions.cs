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

        /// <summary>
        /// Opt-in Excel formula guard. Off by default because it alters real values: the
        /// measured corpus uses "+", "-" and "@" as legal leading characters of layer
        /// names, so prefixing them with an apostrophe changes what the drawing says.
        /// </summary>
        public const string ExcelSafeVariable = "AEC_HARVEST_EXCEL_SAFE";

        /// <summary>
        /// Opt-in UTF-8 BOM. Windows Excel reads BOM-less UTF-8 as the local ANSI code
        /// page, so a file with non-ASCII text can look wrong; parsers do not need it.
        /// </summary>
        public const string Utf8BomVariable = "AEC_HARVEST_UTF8_BOM";

        /// <summary>Set by the supervisor to the chunk manifest the engine should process.</summary>
        public const string BatchManifestVariable = "AEC_HARVEST_BATCH";

        /// <summary>
        /// Opt-in nested block traversal. Off by default because it multiplies rows on a
        /// drawing whose blocks contain geometry, and because an inner definition object
        /// is reached once per placement. On for exactly "1", like every other switch.
        /// </summary>
        public const string NestedBlocksVariable = "AEC_HARVEST_NESTED";

        public const string OutputFileName = "Extracted_BOM.csv";
        public const string StatusFolderName = "_status";
        public const string RowsFolderName = "_rows";

        /// <summary>
        /// Where a failure that has no key to name itself is explained. Fixed name, so the
        /// supervisor can look for exactly one file; no key can collide with it because
        /// every key ends in -&lt;8 hex&gt;.
        /// </summary>
        public const string DiagnosticFileName = "_harvest.error.txt";

        /// <summary>
        /// The drawing the engine is working on, so a killed process can be blamed on one
        /// drawing instead of the whole chunk. Fixed name, same collision argument.
        /// </summary>
        public const string CurrentMarkerFileName = "_current.txt";

        /// <summary>
        /// What the engine did, in one JSON object: counts and engine time. Read by the
        /// supervisor's summary and by the Phase 4 acceptance check.
        /// </summary>
        public const string BatchSummaryFileName = "_batch.json";

        private HarvestOptions(
            string outputFolder, string key, bool excelSafe, bool utf8Bom, bool nestedBlocks)
        {
            OutputFolder = outputFolder;
            Key = key;
            ExcelSafe = excelSafe;
            Utf8Bom = utf8Bom;
            IncludeNestedBlocks = nestedBlocks;
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

        /// <summary>Prefix a text field that starts with = + - @ or a tab, for Excel.</summary>
        public bool ExcelSafe { get; }

        /// <summary>Write a UTF-8 BOM at the start of each CSV file.</summary>
        public bool Utf8Bom { get; }

        /// <summary>Descend into block definitions and report the geometry they contain.</summary>
        public bool IncludeNestedBlocks { get; }

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

            return FromKey(key!);
        }

        /// <summary>
        /// One drawing in a batch: the key comes from the manifest line rather than the
        /// environment, because one process now harvests many drawings.
        /// </summary>
        public static HarvestOptions FromKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException(
                    "the batch manifest supplied a blank key; the plugin will not invent one.");
            }

            return new HarvestOptions(
                ResolveOutputFolder(),
                key,
                IsEnabled(ExcelSafeVariable),
                IsEnabled(Utf8BomVariable),
                IsEnabled(NestedBlocksVariable));
        }

        /// <summary>
        /// A switch is on only for the value "1". Anything else - including "true" -
        /// is off, so a typo cannot silently rewrite values or add a BOM.
        /// </summary>
        private static bool IsEnabled(string variable)
        {
            return string.Equals(
                Environment.GetEnvironmentVariable(variable)?.Trim(), "1", StringComparison.Ordinal);
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
