using System;
using System.IO;

namespace AEC_Batch_Data_Haravester.Reporting
{
    /// <summary>
    /// One fixed-name file explaining a failure that belongs to no single drawing.
    ///
    /// Core Console discards managed stdout and stderr when they are redirected - measured:
    /// a run with no harvest key produced 0 bytes on both - so a message written only to
    /// the console is a message nobody will ever read. The supervisor sees that the status
    /// files are missing, reports "the drawing probably did not open", and sends someone to
    /// inspect a perfectly good DWG. This file is what makes that diagnosis truthful.
    ///
    /// Best effort: it runs while unwinding an existing failure, and must never replace it.
    /// </summary>
    public static class DiagnosticFile
    {
        public static void Write(string message, string? detail)
        {
            try
            {
                string statusFolder = Path.Combine(
                    Core.HarvestOptions.ResolveOutputFolder(),
                    Core.HarvestOptions.StatusFolderName);

                Directory.CreateDirectory(statusFolder);

                File.WriteAllText(
                    Path.Combine(statusFolder, Core.HarvestOptions.DiagnosticFileName),
                    detail is null ? message : message + Environment.NewLine + detail);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
        }
    }
}
