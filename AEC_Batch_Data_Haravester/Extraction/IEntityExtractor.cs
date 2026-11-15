using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// One entity type's harvest logic. Returns a sequence, not a single row: a created
    /// object will legitimately produce more than one row (block attributes do in Phase 5),
    /// and a signature that cannot express that would have to be replaced later anyway.
    /// </summary>
    public interface IEntityExtractor
    {
        /// <summary>The class this extractor handles; subclasses are matched by inheritance.</summary>
        RXClass TargetClass { get; }

        IEnumerable<HarvestRow> Extract(Entity entity, HarvestContext context);
    }
}
