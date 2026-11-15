using Autodesk.AutoCAD.DatabaseServices;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Resolves the model-space <see cref="ObjectId"/> for a database.
    /// The only reason this is an interface: a valid drawing always has a model space, so
    /// the "cannot resolve" path cannot be reached from a real file. A seam makes it
    /// testable without AutoCAD instead of leaving it unexercised until production.
    /// </summary>
    public interface IModelSpaceResolver
    {
        ObjectId Resolve(Database db);
    }

    /// <summary>The production resolver: asks the database directly, no name lookup.</summary>
    public sealed class SymbolModelSpaceResolver : IModelSpaceResolver
    {
        public ObjectId Resolve(Database db)
        {
            return SymbolUtilityServices.GetBlockModelSpaceId(db);
        }
    }
}
