using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// One entity type's harvest logic. Returns the whole result, not a single row: a
    /// block reference legitimately yields an entity row plus N attribute rows, and a
    /// signature that cannot express that would have to be replaced anyway.
    ///
    /// Adding a type is one class plus one line in
    /// <see cref="ExtractorRegistry.CreateDefault"/> - there is no switch to edit and no
    /// scanner change.
    /// </summary>
    public interface IEntityExtractor
    {
        /// <summary>The class this extractor handles; subclasses are matched by inheritance.</summary>
        RXClass TargetClass { get; }

        EntityExtraction Extract(Entity entity, HarvestContext context);
    }
}
