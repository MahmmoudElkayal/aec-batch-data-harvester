using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using AEC_Batch_Data_Haravester.Core;
using AEC_Batch_Data_Haravester.Output;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Walks a space and writes one row per supported entity. Read-only transaction:
    /// nothing is opened ForWrite, nothing is committed back.
    ///
    /// Failure policy, applied per entity: a proxy or corrupt object is skipped and
    /// counted, because that is the one place where "keep harvesting" is the right
    /// recovery. Anything else propagates to the command boundary and fails the drawing.
    ///
    /// Each entity's rows are buffered before they reach the sink, so an extractor that
    /// throws half way through cannot publish a partial row set.
    /// </summary>
    public sealed class ModelSpaceScanner
    {
        private const string ModelSpaceLabel = "Model";

        private readonly ExtractorRegistry _registry;
        private readonly IModelSpaceResolver _resolver;

        public ModelSpaceScanner(ExtractorRegistry registry, IModelSpaceResolver resolver)
        {
            _registry = registry;
            _resolver = resolver;
        }

        public HarvestResult Scan(Database db, string drawingName, IRowSink sink)
        {
            string unit = db.Insunits.ToString();
            int unitCode = (int)db.Insunits;

            ObjectId modelSpaceId = _resolver.Resolve(db);
            ModelSpaceGuard.EnsureResolved(modelSpaceId.IsNull, drawingName);

            using Transaction tr = db.TransactionManager.StartTransaction();

            if (tr.GetObject(modelSpaceId, OpenMode.ForRead) is not BlockTableRecord modelSpace)
            {
                throw new InvalidOperationException(
                    "model space object is not a BlockTableRecord for '" + drawingName + "'");
            }

            var context = new HarvestContext(tr, drawingName, ModelSpaceLabel, unit, unitCode);
            var buffer = new List<HarvestRow>(1);
            int skipped = 0;
            int written = 0;

            foreach (ObjectId id in modelSpace)
            {
                if (id.IsErased)
                {
                    continue;
                }

                Entity? entity;
                try
                {
                    entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                }
                catch (AcException)
                {
                    skipped++;
                    continue;
                }

                if (entity is null)
                {
                    continue;
                }

                IEntityExtractor? extractor = _registry.Resolve(entity.GetRXClass());
                if (extractor is null)
                {
                    continue;
                }

                buffer.Clear();
                try
                {
                    foreach (HarvestRow row in extractor.Extract(entity, context))
                    {
                        buffer.Add(row);
                    }
                }
                catch (AcException)
                {
                    // An entity the extractor cannot read is one lost row set, not a lost
                    // drawing. Counted, never silent.
                    skipped++;
                    continue;
                }

                sink.Write(buffer);
                written += buffer.Count;
            }

            return new HarvestResult(written, skipped, unit, unitCode);
        }
    }
}
