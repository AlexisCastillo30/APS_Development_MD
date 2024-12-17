#region Namespaces
using System;
using System.Collections.Generic;
using System.Configuration.Assemblies;
using System.Reflection;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbon;
#endregion

namespace Development_BIM
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication a)
        {

            string tabName = "Digital AECOM";
            try
            {
                a.CreateRibbonTab(tabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // La pestaña "Development" ya existe
            }



            // Crea el panel "LOG" en la pestaña "Development"
            RibbonPanel ribbonLog = a.CreateRibbonPanel(tabName, "Digital Architecture");
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            PushButtonData botonLogPrim = new PushButtonData("botonLog", "Wall-Finishes", assemblyPath, "Development_BIM.core.AcabadosRooms");
            PushButton botonLog = ribbonLog.AddItem(botonLogPrim) as PushButton;
            botonLog.LargeImage = GetIconFromDll.GetEmbeddedImage("Development_BIM.Resources.Digital AECOM_Logo_Master_Logo (Black).png");





            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a)
        {          
            return Result.Succeeded;
        }




    }
}