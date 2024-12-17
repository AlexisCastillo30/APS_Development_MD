using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;

namespace Development_BIM.core
{
    [Transaction(TransactionMode.Manual)]
    public class OpeningWalls : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Obtener el documento activo
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Recopilar todos los muros en el documento
            FilteredElementCollector wallCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType();

            // Recopilar todos los ductos en el documento
            FilteredElementCollector ductCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(Duct))
                .WhereElementIsNotElementType();

            // Obtener el símbolo de familia "ACM_Opening"
            FamilySymbol openingSymbol = GetFamilySymbolByName(doc, "ACM_Opening");
            if (openingSymbol == null)
            {
                TaskDialog.Show("Error", "No se encontró la familia 'ACM_Opening' en el proyecto.");
                return Result.Failed;
            }

            using (Transaction trans = new Transaction(doc, "Colocar ACM_Openings"))
            {
                trans.Start();

                // Asegurar que el símbolo de familia está activo
                if (!openingSymbol.IsActive)
                {
                    openingSymbol.Activate();
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
                                    height = width = diameterParam.AsDouble();
                                }

                                // Ajustar el punto de inserción para centrar la apertura en el ducto
                                XYZ offset = new XYZ(0, 0, -height / 2);
                                XYZ adjustedPoint = intersectionPoint + offset;


                                // Obtener el nivel del muro
                                Level wallLevel = doc.GetElement(wall.LevelId) as Level;
                                if (wallLevel == null)
                                {
                                    TaskDialog.Show("Error", "No se pudo obtener el nivel del muro.");
                                    return Result.Failed;
                                }


                                // Colocar la apertura en el punto de intersección ajustado y asociar el nivel correcto
                                FamilyInstance openingInstance = doc.Create.NewFamilyInstance(adjustedPoint, openingSymbol, wall, wallLevel, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                                // Ajustar las dimensiones de la apertura para que coincidan con las del ducto
                                SetOpeningDimensions(openingInstance, height, width);

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

                    // Cambiar el nombre de la variable interna para evitar conflictos
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
            // Ajustar las dimensiones de la apertura
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
    }
}