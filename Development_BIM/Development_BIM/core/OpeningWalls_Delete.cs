using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Development_BIM.core
{
    [Transaction(TransactionMode.Manual)]
    public class OpeningWalls_Delete : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Obtener el documento activo
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;


            // Borrar instancias existentes
            using (Transaction deleteTrans = new Transaction(doc, "Borrar ACM_Openings existentes"))
            {
                deleteTrans.Start();

                // Recolectar y borrar instancias de ACM_Opening y ACM_Opening_Round
                List<ElementId> elementsToDelete = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.Symbol.FamilyName == "ACM_Opening" || fi.Symbol.FamilyName == "ACM_Opening_Round")
                    .Select(fi => fi.Id)
                    .ToList();

                if (elementsToDelete.Any())
                {
                    doc.Delete(elementsToDelete);
                    //TaskDialog.Show("Información", $"Se han borrado {elementsToDelete.Count} instancias de ACM_Opening y ACM_Opening_Round.");
                }

                deleteTrans.Commit();
            }


            // Recopilar todos los muros en el documento
            FilteredElementCollector wallCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType();

            // Recopilar todos los ductos en el documento
            FilteredElementCollector ductCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(Duct))
                .WhereElementIsNotElementType();

            // Obtener los símbolos de familia "ACM_Opening" y "ACM_Opening_Round"
            FamilySymbol openingSymbolRectangular = GetFamilySymbolByName(doc, "ACM_Opening");
            if (openingSymbolRectangular == null)
            {
                TaskDialog.Show("Error", "No se encontró la familia 'ACM_Opening' en el proyecto.");
                return Result.Failed;
            }

            FamilySymbol openingSymbolRound = GetFamilySymbolByName(doc, "ACM_Opening_Round");
            if (openingSymbolRound == null)
            {
                TaskDialog.Show("Error", "No se encontró la familia 'ACM_Opening_Round' en el proyecto.");
                return Result.Failed;
            }

            using (Transaction trans = new Transaction(doc, "Colocar ACM_Openings"))
            {
                trans.Start();

                // Asegurar que los símbolos de familia están activos
                if (!openingSymbolRectangular.IsActive)
                {
                    openingSymbolRectangular.Activate();
                    doc.Regenerate();
                }

                if (!openingSymbolRound.IsActive)
                {
                    openingSymbolRound.Activate();
                    doc.Regenerate();
                }

                int aperturasCreadas = 0;

                // Iterar sobre cada ducto
                foreach (Duct duct in ductCollector)
                {
                    // Obtener la curva de ubicación del ducto
                    LocationCurve ductLocation = duct.Location as LocationCurve;
                    if (ductLocation == null) continue;

                    Curve ductCurve = ductLocation.Curve;

                    // Obtener la BoundingBox del ducto
                    BoundingBoxXYZ ductBBox = duct.get_BoundingBox(null);
                    if (ductBBox == null) continue;

                    // Crear un filtro para encontrar muros cuya BoundingBox intersecte con la del ducto
                    Outline outline = new Outline(ductBBox.Min, ductBBox.Max);
                    BoundingBoxIntersectsFilter bboxFilter = new BoundingBoxIntersectsFilter(outline);

                    // Encontrar muros que intersectan con la BoundingBox del ducto
                    FilteredElementCollector potentialWalls = new FilteredElementCollector(doc)
                        .OfClass(typeof(Wall))
                        .WherePasses(bboxFilter);

                    foreach (Wall wall in potentialWalls)
                    {
                        // Obtener la geometría del muro
                        GeometryElement wallGeometry = wall.get_Geometry(new Options());
                        if (wallGeometry == null) continue;

                        // Obtener el sólido del muro
                        Solid wallSolid = GetSolidFromGeometryElement(wallGeometry);
                        if (wallSolid == null) continue;

                        // Obtener la geometría del ducto
                        GeometryElement ductGeometry = duct.get_Geometry(new Options());
                        if (ductGeometry == null) continue;

                        // Obtener el sólido del ducto
                        Solid ductSolid = GetSolidFromGeometryElement(ductGeometry);
                        if (ductSolid == null) continue;

                        // Comprobar la intersección entre los sólidos
                        BooleanOperationsType operation = BooleanOperationsType.Intersect;
                        try
                        {
                            Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(ductSolid, wallSolid, operation);
                            if (intersection != null && intersection.Volume > 0)
                            {
                                // Los sólidos se intersectan, proceder a crear la apertura

                                // Obtener el punto de intersección (centroide de la intersección)
                                XYZ intersectionPoint = intersection.ComputeCentroid();

                                // Obtener las dimensiones del ducto
                                double height = 0;
                                double width = 0;
                                double diameter = 0;
                                bool isRoundDuct = false;

                                Parameter heightParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                                Parameter widthParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                                Parameter diameterParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);

                                if (heightParam != null && widthParam != null)
                                {
                                    height = heightParam.AsDouble();
                                    width = widthParam.AsDouble();
                                }
                                else if (diameterParam != null)
                                {
                                    diameter = diameterParam.AsDouble();
                                    isRoundDuct = true;
                                }
                                else
                                {
                                    // No se pueden determinar las dimensiones del ducto
                                    continue;
                                }

                                // Ajustar el punto de inserción para centrar la apertura en el ducto
                                XYZ offset;
                                if (isRoundDuct)
                                {
                                    offset = new XYZ(0, 0, -diameter / 2);
                                }
                                else
                                {
                                    offset = new XYZ(0, 0, -height / 2);
                                }
                                XYZ adjustedPoint = intersectionPoint + offset;

                                // Obtener el nivel del muro
                                Level wallLevel = doc.GetElement(wall.LevelId) as Level;
                                if (wallLevel == null)
                                {
                                    TaskDialog.Show("Error", "No se pudo obtener el nivel del muro.");
                                    return Result.Failed;
                                }

                                // Seleccionar el símbolo de familia adecuado
                                FamilySymbol openingSymbol = isRoundDuct ? openingSymbolRound : openingSymbolRectangular;

                                // Colocar la apertura en el punto de intersección ajustado y asociar el nivel correcto
                                FamilyInstance openingInstance = doc.Create.NewFamilyInstance(adjustedPoint, openingSymbol, wall, wallLevel, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                                // Ajustar las dimensiones de la apertura
                                if (isRoundDuct)
                                {
                                    SetOpeningDiameter(openingInstance, diameter);
                                }
                                else
                                {
                                    SetOpeningDimensions(openingInstance, height, width);
                                }

                                aperturasCreadas++;
                            }
                        }
                        catch
                        {
                            // Manejar excepciones si la operación booleana falla
                            continue;
                        }
                    }
                }

                trans.Commit();

                TaskDialog.Show("Resultado", $"Se crearon {aperturasCreadas} aperturas.");
            }

            return Result.Succeeded;
        }




        private FamilySymbol GetFamilySymbolByName(Document doc, string familyName)
        {
            // Buscar en todas las familias cargadas en el documento
            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol));

            foreach (FamilySymbol symbol in collector)
            {
                if (symbol.FamilyName.Equals(familyName, StringComparison.InvariantCultureIgnoreCase))
                {
                    return symbol;
                }
            }

            return null;
        }

        private Solid GetSolidFromGeometryElement(GeometryElement geometryElement)
        {
            foreach (GeometryObject geomObj in geometryElement)
            {
                if (geomObj is Solid solid && solid.Volume > 0)
                {
                    return solid;
                }
                else if (geomObj is GeometryInstance geomInstance)
                {
                    GeometryElement instanceGeometry = geomInstance.GetInstanceGeometry();

                    Solid nestedSolid = GetSolidFromGeometryElement(instanceGeometry);
                    if (nestedSolid != null)
                    {
                        return nestedSolid;
                    }
                }
            }
            return null;
        }

        private void SetOpeningDimensions(FamilyInstance openingInstance, double height, double width)
        {
            // Ajustar las dimensiones de la apertura para ductos rectangulares
            Parameter openingHeightParam = openingInstance.LookupParameter("Height"); // O "Altura" si el parámetro está en español
            Parameter openingWidthParam = openingInstance.LookupParameter("Width");   // O "Ancho" si el parámetro está en español

            if (openingHeightParam != null && openingWidthParam != null)
            {
                openingHeightParam.Set(height);
                openingWidthParam.Set(width);
            }
            else
            {
                TaskDialog.Show("Error", "No se encontraron los parámetros de altura y ancho en la familia 'ACM_Opening'.");
            }
        }

        private void SetOpeningDiameter(FamilyInstance openingInstance, double diameter)
        {
            // Ajustar el diámetro de la apertura para ductos redondos
            Parameter diameterParam = openingInstance.LookupParameter("Rough Diameter"); // Asegúrate del nombre exacto del parámetro
            if (diameterParam != null)
            {
                diameterParam.Set(diameter);
            }
            else
            {
                TaskDialog.Show("Error", "No se encontró el parámetro 'Rough Diameter' en la familia 'ACM_Opening_Round'.");
            }
        }
    }
}
