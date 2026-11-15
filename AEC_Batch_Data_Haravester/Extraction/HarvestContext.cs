using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Everything an extractor needs about the drawing it is reading. The transaction is
    /// here because resolving a block reference's definition is a database read; nothing
    /// in this type is written.
    /// </summary>
    public sealed class HarvestContext
    {
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
    }
}
