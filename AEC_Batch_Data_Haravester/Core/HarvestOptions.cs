using System;
using System.IO;
using System.Text;

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

        public const string OutputFileName = "Extracted_BOM.csv";
        public const string StatusFolderName = "_status";

        private HarvestOptions(string outputFolder, string key)
        {
            OutputFolder = outputFolder;
            Key = key;
        }

        /// <summary>Where rows and status files go. Never empty.</summary>
        public string OutputFolder { get; }

        /// <summary>
        /// The status-file stem for this drawing. Phase 3 moves its source to the batch
        /// supervisor (one owner for the rule); for now it is derived from the drawing
        /// name, exactly as the pre-refactor code did.
        /// </summary>
        public string Key { get; }

        public string StatusFolder => Path.Combine(OutputFolder, StatusFolderName);

        public static HarvestOptions FromEnvironment(string drawingName)
        {
            return new HarvestOptions(ResolveOutputFolder(), FileNames.StatusKey(drawingName));
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

    /// <summary>Pure file-name helpers. No AutoCAD, no I/O.</summary>
    public static class FileNames
    {
        /// <summary>Drawing name -> a legal Windows file name stem.</summary>
        public static string Sanitize(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            string result = builder.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "drawing" : result;
        }

        public static string StatusKey(string drawingName)
        {
            return Sanitize(Path.GetFileNameWithoutExtension(drawingName));
        }
    }
}
