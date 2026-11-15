using System;
using System.Globalization;
using System.IO;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Reporting
{
    /// <summary>
    /// Records the outcome three ways:
    ///  1. a line on stdout (best effort - Core Console with redirected output drops writes
    ///     from managed code, so this is a convenience only);
    ///  2. an authoritative one-line JSON status file under &lt;output&gt;\_status\, which
    ///     is what RunPipeline.bat checks to decide pass/fail;
    ///  3. on failure, the full exception text in &lt;key&gt;.error.txt, because the status
    ///     line must stay short enough to echo.
    /// Never throws: reporting must not mask the real result.
    /// </summary>
    public sealed class FileStatusReporter : IStatusReporter
    {
        // Markers used on the console. Do not rename casually.
        public const string DoneMarker = "[AEC] DONE";
        public const string FailMarker = "[AEC] FAIL";

        private readonly HarvestOptions _options;

        public FileStatusReporter(HarvestOptions options)
        {
            _options = options;
        }

        public void Success(
            string drawingName, string message, long elapsedMs,
            int rows, int skipped, string? unitName, int? unitCode)
        {
            WriteConsole(DoneMarker + " " + message);

            Publish(
                success: true,
                drawingName,
                message,
                detail: null,
                elapsedMs,
                rows,
                skipped,
                unitName,
                unitCode);
        }

        public void Failure(string drawingName, string message, string? detail, long elapsedMs)
        {
            WriteConsole(FailMarker + " " + message);

            Publish(
                success: false,
                drawingName,
                message,
                detail,
                elapsedMs,
                rows: 0,
                skipped: 0,
                unitName: null,
                unitCode: null);
        }

        private void Publish(
            bool success,
            string drawingName,
            string message,
            string? detail,
            long elapsedMs,
            int rows,
            int skipped,
            string? unitName,
            int? unitCode)
        {
            try
            {
                Directory.CreateDirectory(_options.StatusFolder);

                string stem = _options.Key;
                string okPath = System.IO.Path.Combine(_options.StatusFolder, stem + ".ok");
                string failPath = System.IO.Path.Combine(_options.StatusFolder, stem + ".fail");

                // A re-run must not leave the previous verdict lying next to the new one.
                TryDelete(success ? failPath : okPath);

                string json = StatusJson.Build(
                    success, drawingName, message, elapsedMs, rows, skipped, unitName, unitCode);

                File.WriteAllText(success ? okPath : failPath, json);

                // Digits only, so the batch runner can echo it without escaping risks.
                if (success)
                {
                    File.WriteAllText(
                        System.IO.Path.Combine(_options.StatusFolder, stem + ".rows"),
                        rows.ToString(CultureInfo.InvariantCulture));
                }

                if (!success && !string.IsNullOrEmpty(detail))
                {
                    File.WriteAllText(
                        System.IO.Path.Combine(_options.StatusFolder, stem + ".error.txt"),
                        detail);
                }
                else
                {
                    TryDelete(System.IO.Path.Combine(_options.StatusFolder, stem + ".error.txt"));
                }
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

        private static void WriteConsole(string line)
        {
            try
            {
                Console.WriteLine(line);
                Console.Out.Flush();
            }
            catch (IOException)
            {
                // No usable stdout (desktop AutoCAD). Nothing to do.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
