using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Development_BIM.core
{
    [Transaction(TransactionMode.Manual)]
    internal class Filter : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            FilteredElementCollector collectorwalls = new FilteredElementCollector(doc);
            ICollection<Element> listaWalls = collectorwalls.OfCategory(BuiltInCategory.OST_Walls).WhereElementIsElementType().ToElements();

            List<string> wallnames = new List<string>();

            foreach (Element wall in listaWalls)
            {
                string wallname = wall.Name != string.Empty ? wall.Name : wall.Id.ToString();
                string wallid = wall.Id.ToString();

                string wallInfo = $"Name: {wallname} Id: {wallid}";

                wallnames.Add(wallInfo);
            }

            string muros = string.Join(Environment.NewLine, wallnames);
            TaskDialog.Show("Muros", muros);

            FilteredElementCollector collectorducts = new FilteredElementCollector(doc);
            ICollection<Element> listDucts = collectorducts.OfCategory(BuiltInCategory.OST_DuctCurves).WhereElementIsElementType().ToElements();
            List<string> ductnames = new List<string>();

            foreach (Element duct in listDucts)
            {
                string ductname = duct.Name != string.Empty ? duct.Name : duct.Id.ToString();
                string ductid = duct.Id.ToString();

                string ductInfo = $"name: {ductname} id: {ductid}";

                ductnames.Add(ductInfo);
            }

            string ductos = string.Join(Environment.NewLine, ductnames);
            TaskDialog.Show("ductos", ductos);



            return Result.Succeeded;
        }
    }
}
