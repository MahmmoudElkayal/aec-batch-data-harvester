using System;

namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>
    /// The policy for an unresolved model space, isolated from AutoCAD types so it can be
    /// tested. A broken drawing must fail loudly: returning no rows would make it look
    /// exactly like an empty drawing, and the batch would report success.
    /// </summary>
    public static class ModelSpaceGuard
    {
        public static void EnsureResolved(bool isNull, string drawingName)
        {
            if (isNull)
            {
                throw new InvalidOperationException(
                    "model space id could not be resolved for '" + drawingName + "'");
            }
        }
    }
}
