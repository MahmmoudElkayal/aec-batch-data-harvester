using System;
using System.IO;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;
using AEC_Batch_Data_Haravester.Extraction;
using AEC_Batch_Data_Haravester.Reporting;

namespace AEC_Batch_Data_Haravester.Commands
{
    /// <summary>
    /// The command surface. Deliberately thin: a command should wire inputs to a use case,
    /// not contain the use case. Both hosts (desktop AutoCAD and Core Console) call these
    /// same methods.
    /// </summary>
    public sealed class BomCommands
    {
        /// <summary>Harvest the drawing that is currently open.</summary>
        [CommandMethod("Extract_BOM")]
        public void ExtractBom()
        {
            HarvestRunner.Run();
        }

        /// <summary>
        /// Harvest every drawing in the chunk manifest named by AEC_HARVEST_BATCH, in one
        /// process, each opened as a side database. The supervisor sets the variable and
        /// reads the status files; the command reports nothing back by exit code.
        /// </summary>
        [CommandMethod("HarvestBatch")]
        public void HarvestBatch()
        {
            string? manifestPath = Environment.GetEnvironmentVariable(
                HarvestOptions.BatchManifestVariable);

            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                ReportBatchFailure(
                    HarvestOptions.BatchManifestVariable + " is not set; nothing to harvest.");
                return;
            }

            if (!File.Exists(manifestPath))
            {
                ReportBatchFailure("batch manifest not found: " + manifestPath);
                return;
            }

            try
            {
                BatchRunner.Run(manifestPath);
            }
            catch (System.Exception ex)
            {
                // Parsing failed, or the manifest could not be read. That is one failure for
                // the chunk, not a crash: the supervisor will mark the unreported drawings.
                ReportBatchFailure(
                    ".NET error: " + ex.GetType().Name + ": " + ex.Message, ex.ToString());
            }
        }

        /// <summary>
        /// Core Console discards managed stdout and stderr, so the reason has to survive in
        /// a file. This is the only fixed-name diagnostic for a failure that belongs to no
        /// single drawing; per-drawing failures write their own .fail.
        /// </summary>
        private static void ReportBatchFailure(string message, string? detail = null)
        {
            DiagnosticFile.Write(message, detail);
        }
    }
}
