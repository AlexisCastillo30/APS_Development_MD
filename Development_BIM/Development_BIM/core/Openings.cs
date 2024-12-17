using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Development_BIM.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Development_BIM.core
{
    [Transaction(TransactionMode.Manual)]
    class Openings : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;


            // Verifica si la aplicación actual de WPF es nula
            if (System.Windows.Application.Current == null)
            {
                // Crea una nueva instancia de la aplicación WPF si es necesario
                new System.Windows.Application();
            }

            //// Ejecutar la ventana de diálogo en el hilo de la UI
            //System.Windows.Application.Current.Dispatcher.Invoke(() =>
            //{
            //    var qs = new WPF_Opening(this, uiapp);
            //    qs.Show(); // Puedes usar Show() o ShowDialog() según lo necesites
            //});

            return Result.Succeeded;
        }
    }
}
