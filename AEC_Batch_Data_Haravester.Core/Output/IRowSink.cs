using System.Collections.Generic;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Output
{
    /// <summary>
    /// Where harvested rows go. One call per entity, never per row: the scanner buffers a
    /// single entity's output so a mid-entity failure cannot leave half a row set behind.
    ///
    /// Entity rows and attribute rows are separate calls because they are separate files
    /// with separate schemas. Both are published by the same <see cref="Complete"/>, so a
    /// drawing never looks successful with only half of its output.
    /// </summary>
    public interface IRowSink : System.IDisposable
    {
        /// <summary>Absolute path of the entity file.</summary>
        string Path { get; }

        /// <summary>Absolute path of the attribute file.</summary>
        string AttributePath { get; }

        void Write(IReadOnlyList<HarvestRow> rows);

        void WriteAttributes(IReadOnlyList<HarvestAttributeRow> rows);

        /// <summary>Flush and publish both files. Only call when extraction succeeded.</summary>
        void Complete();
    }
}
