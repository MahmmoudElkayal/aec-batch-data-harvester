using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using AEC_Batch_Data_Haravester.Core;
using AEC_Batch_Data_Haravester.Output;
using AEC_Batch_Data_Haravester.Reporting;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Harvests the drawing that is currently loaded, then reports the outcome. This is
    /// the only place that owns the try/catch boundary; everything below it either returns
    /// a result or throws.
    /// </summary>
    public static class HarvestRunner
    {
        private static readonly IModelSpaceResolver Resolver = new SymbolModelSpaceResolver();
        private static readonly ExtractorRegistry Registry = ExtractorRegistry.CreateDefault();

        public static void Run()
        {
            var stopwatch = Stopwatch.StartNew();
            string drawingName = "(unknown)";
            HarvestOptions? options = null;

            try
            {
                Database? db = HostApplicationServices.WorkingDatabase;
                if (db is null)
                {
                    options = HarvestOptions.FromEnvironment();
                    new FileStatusReporter(options).Failure(
                        drawingName, "no working database - is a drawing loaded?", null,
                        stopwatch.ElapsedMilliseconds);
                    return;
                }

                drawingName = string.IsNullOrEmpty(db.Filename)
                    ? "(unsaved drawing)"
                    : Path.GetFileName(db.Filename);

                options = HarvestOptions.FromEnvironment();
                var reporter = new FileStatusReporter(options);
                var formatter = new CsvFormatter();

                using var sink = new CsvRowSink(options, formatter);

                HarvestResult result = new ModelSpaceScanner(Registry, Resolver)
                    .Scan(db, drawingName, sink);

                sink.Complete();

                reporter.Success(
                    drawingName,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} rows, {1} skipped from {2} -> {3}",
                        result.RowsWritten, result.Skipped, drawingName, sink.Path),
                    stopwatch.ElapsedMilliseconds,
                    result.RowsWritten,
                    result.Skipped,
                    db.Insunits.ToString(),
                    (int)db.Insunits);
            }
            catch (AcException acEx)
            {
                // ex.ToString() is kept in full: when drawing 437 of 1,000 fails, the
                // stack trace is the only thing that identifies where.
                ReportFailure(
                    options,
                    drawingName,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "AutoCAD error: {0} (ErrorStatus.{1})",
                        acEx.Message, acEx.ErrorStatus),
                    acEx.ToString(),
                    stopwatch.ElapsedMilliseconds);
            }
            catch (System.Exception ex)
            {
                ReportFailure(
                    options,
                    drawingName,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        ".NET error: {0}: {1}",
                        ex.GetType().Name, ex.Message),
                    ex.ToString(),
                    stopwatch.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Options can be unavailable: no harvest key, or an unusable output folder. That
        /// still has to fail the drawing loudly, and it has to stay diagnosable.
        ///
        /// stderr alone is not enough. Core Console discards managed stdout and stderr when
        /// it is redirected (measured: a run with no key produced 0 bytes on both), so the
        /// supervisor would report "the drawing probably did not open" - a wrong diagnosis
        /// that sends someone looking at a perfectly good DWG. The output folder is known
        /// even when the key is not, so the reason goes there as a fixed-name diagnostic
        /// file. It cannot be mistaken for a drawing's status file: every key ends in
        /// -&lt;8 hex&gt;, and the supervisor only looks for &lt;key&gt;.ok / .fail.
        /// </summary>
        private static void ReportFailure(
            HarvestOptions? options, string drawingName, string message, string? detail, long elapsedMs)
        {
            if (options is not null)
            {
                new FileStatusReporter(options).Failure(drawingName, message, detail, elapsedMs);
                return;
            }

            try
            {
                Console.Error.WriteLine(FileStatusReporter.FailMarker + " " + message);
                Console.Error.Flush();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            WriteDiagnostic(message, detail);
        }

        /// <summary>
        /// Best effort, never throws: this runs on the way out of an already-failed drawing,
        /// and a second failure here must not replace the first one's message.
        /// </summary>
        private static void WriteDiagnostic(string message, string? detail)
        {
            try
            {
                string statusFolder = Path.Combine(
                    HarvestOptions.ResolveOutputFolder(), HarvestOptions.StatusFolderName);

                Directory.CreateDirectory(statusFolder);

                File.WriteAllText(
                    Path.Combine(statusFolder, HarvestOptions.DiagnosticFileName),
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
