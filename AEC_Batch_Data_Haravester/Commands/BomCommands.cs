using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Extraction;

namespace AEC_Batch_Data_Haravester.Commands
{
    /// <summary>
    /// The command surface. Deliberately thin: a command should wire inputs to a use case,
    /// not contain the use case. Both hosts (desktop AutoCAD and Core Console) call these
    /// same methods.
    /// </summary>
    public sealed class BomCommands
    {
        /// <summary>Harvest the drawing that is currently open.</summary>
        [CommandMethod("Extract_BOM")]
        public void ExtractBom()
        {
            HarvestRunner.Run();
        }
    }
}
