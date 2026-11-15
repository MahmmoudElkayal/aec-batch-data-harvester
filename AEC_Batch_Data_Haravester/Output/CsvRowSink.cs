using System.Collections.Generic;
using System.IO;
using System.Text;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Output
{
    /// <summary>
    /// Appends rows to the shared CSV, writing the header only when the file is new or
    /// empty. Phase 2 keeps the pre-refactor behaviour deliberately; Phase 3 replaces this
    /// with one file per drawing plus a merge.
    /// </summary>
    public sealed class CsvRowSink : IRowSink
    {
        private readonly StreamWriter _writer;
        private readonly CsvFormatter _formatter;

        public CsvRowSink(HarvestOptions options, CsvFormatter formatter)
        {
            _formatter = formatter;
            Path = System.IO.Path.Combine(options.OutputFolder, HarvestOptions.OutputFileName);

            Directory.CreateDirectory(options.OutputFolder);
            bool needsHeader = !File.Exists(Path) || new FileInfo(Path).Length == 0;

            // No BOM, so the file opens cleanly in Excel and in every CSV/parquet tool.
            _writer = new StreamWriter(
                Path,
                append: true,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (needsHeader)
            {
                _writer.WriteLine(_formatter.Header);
            }
        }

        public string Path { get; }

        public void Write(IReadOnlyList<HarvestRow> rows)
        {
            foreach (HarvestRow row in rows)
            {
                _writer.WriteLine(_formatter.Row(row));
            }
        }

        public void Complete()
        {
            _writer.Flush();
        }

        public void Dispose()
        {
            // Rows are only published on Complete(); disposing after a failure still has to
            // flush what was buffered, because Phase 2's contract is append-and-keep.
            _writer.Dispose();
        }
    }
}
