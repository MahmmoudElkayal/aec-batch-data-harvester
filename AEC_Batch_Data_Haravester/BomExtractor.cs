using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;


namespace AEC_Batch_Data_Haravester
{
    public class BomExtractor
    {
        [CommandMethod("Extract_BOM")]
        public void ExtractBomData()
        {
            // Get the current drawing's database ( works in both UI and headless mode )
            Database db = HostApplicationServices.WorkingDatabase;

            // we create a list to hold the BOM data
            List<string> extractedLines = new List<string>();

            // extracting the name of the file ( e.g "Drawing1.dwg" ) form the file path.
            string dwgName = Path.GetFileName(db.Filename);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // opening the BlockTable ( the master index of the database ) in readonly mode
                BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;

                // We open Model Space (which is technically just a Block) in Read-Only mode.
                BlockTableRecord modelSpace = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;


                // Iterating Model Space

                // looping through each entity (ObjectID) in the Model Space record
                foreach (ObjectId objId in modelSpace)
                {
                    //Pull the object from the database and cast it specifically as an Entity
                    Entity ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;

                    //If it is not an Entity, skip to the next object
                    if (ent == null) continue;

                    //check if the isa BlockRefernce ( e,g Mechanical Equipment, Furniture, etc. ) and if it is, we will extract the data from it.
                    if (ent is BlockReference blockRef)
                    {
                        string blockName = blockRef.Name; // get the name of the block
                                                          // Extract the X and Y coordinates, formatting the numbers to 3 decimal places
                        string coordinates = $"{blockRef.Position.X:F3}, {blockRef.Position.Y:F3}, {blockRef.Position.Z:F3}";
                        // add a formatted CSV row to our list
                        extractedLines.Add($"{dwgName},Block,{blockName},{coordinates}");
                    }

                    else if (ent is Polyline poly)
                    {
                        string layer = poly.Layer; // get the layer of the polyline
                        double length = poly.Length; // get the length of the polyline
                        extractedLines.Add($"{dwgName},Polyline,{layer},{length:F3}");
                    }
                }

                // Commit is technically optional for ForRead, but good practice
                tr.Commit();
            }

            string outputDirc = @"C:\Dev\AutoCAD API\AEC_Batch_Data_ Haravester\Output"; // specify the output directory




        }

    }
}
