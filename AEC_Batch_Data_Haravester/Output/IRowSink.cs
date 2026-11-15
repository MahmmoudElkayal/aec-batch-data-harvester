using System.Collections.Generic;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Output
{
    /// <summary>
    /// Where harvested rows go. One call per entity, never per row: the scanner buffers a
    /// single entity's rows so a mid-entity failure cannot leave half a row set behind.
    /// </summary>
    public interface IRowSink : System.IDisposable
    {
        /// <summary>Absolute path being written.</summary>
        string Path { get; }

        void Write(IReadOnlyList<HarvestRow> rows);

        /// <summary>Flush and publish. Must be called only when extraction succeeded.</summary>
        void Complete();
    }
}
