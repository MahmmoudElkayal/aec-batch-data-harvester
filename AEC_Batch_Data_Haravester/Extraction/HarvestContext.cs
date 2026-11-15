using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Everything an extractor needs about the drawing it is reading. The transaction is
    /// here because resolving a block reference's definition is a database read; nothing
    /// in this type is written.
    ///
    /// It also owns the per-space cache of constant block attributes, because that lookup
    /// belongs to the transaction, not to the block extractor: a layout can place the same
    /// door block forty times, and its constant attribute inventory should be read once,
    /// not forty times.
    /// </summary>
    public sealed class HarvestContext
    {
        private readonly Dictionary<ObjectId, ConstantAttribute[]> _constantAttributes = new();

        public HarvestContext(
            Transaction transaction, string drawing, string space, string unit, int unitCode)
        {
            Transaction = transaction;
            Drawing = drawing;
            Space = space;
            Unit = unit;
            UnitCode = unitCode;
        }

        public Transaction Transaction { get; }

        public string Drawing { get; }

        /// <summary>"Model" today; "Paper" arrives with paper-space support.</summary>
        public string Space { get; }

        public string Unit { get; }

        public int UnitCode { get; }

        /// <summary>
        /// The constant attributes of a block definition, in definition order.
        ///
        /// Constant attributes are the ones AutoCAD does not copy onto the reference: the
        /// definition is used directly for every insertion (confirmed by
        /// <c>AttributeDefinition.Constant</c>: "no Attribute will be created during
        /// insertion"). Reading only <c>BlockReference.AttributeCollection</c> therefore
        /// loses every constant value in the drawing.
        ///
        /// A proxy or unreadable definition yields an empty list: a block with unreadable
        /// constants is still a block row.
        /// </summary>
        public IReadOnlyList<ConstantAttribute> ConstantAttributes(ObjectId definitionId)
        {
            if (definitionId.IsNull)
            {
                return Array.Empty<ConstantAttribute>();
            }

            if (_constantAttributes.TryGetValue(definitionId, out ConstantAttribute[]? cached))
            {
                return cached;
            }

            var found = new List<ConstantAttribute>();

            try
            {
                if (Transaction.GetObject(definitionId, OpenMode.ForRead) is BlockTableRecord definition)
                {
                    foreach (ObjectId id in definition)
                    {
                        if (id.IsErased)
                        {
                            continue;
                        }

                        try
                        {
                            if (Transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition attribute
                                && attribute.Constant)
                            {
                                found.Add(new ConstantAttribute(attribute.Tag, attribute.TextString));
                            }
                        }
                        catch (AcException)
                        {
                            // One unreadable definition object must not lose the others.
                        }
                    }
                }
            }
            catch (AcException)
            {
                // Dangling or proxy definition: no constants, but the block row survives.
            }

            ConstantAttribute[] result = found.ToArray();
            _constantAttributes[definitionId] = result;
            return result;
        }
    }

    /// <summary>One constant attribute: the tag and the value every insertion shares.</summary>
    public readonly record struct ConstantAttribute(string Tag, string Value);
}
