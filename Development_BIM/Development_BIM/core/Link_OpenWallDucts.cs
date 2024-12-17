using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Development_BIM.core
{
    [Transaction(TransactionMode.Manual)]
    public class Link_OpenWallDucts : IExternalCommand
    {
        private const double OFFSET = 30.0 / 304.8;
        private const double RECTANGULAR_INSERTION_OFFSET = 0.0;
        private const double ROUND_INSERTION_OFFSET = 0.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Solicitar al usuario que seleccione el modelo vinculado
            Reference linkRef = uidoc.Selection.PickObject(ObjectType.Element, new LinkSelectionFilter(), "Seleccione el modelo vinculado que contiene los ductos");
            RevitLinkInstance linkInstance = doc.GetElement(linkRef) as RevitLinkInstance;
            Document linkedDoc = linkInstance.GetLinkDocument();

            if (linkedDoc == null)
            {
                TaskDialog.Show("Error", "No se pudo acceder al documento vinculado.");
                return Result.Failed;
            }

            // Borrar instancias existentes
            using (Transaction deleteTrans = new Transaction(doc, "Borrar ACM_Openings existentes"))
            {
                deleteTrans.Start();

                List<ElementId> elementsToDelete = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.Symbol.FamilyName == "ACM_H_Opening_Rectangular" || fi.Symbol.FamilyName == "ACM_H_Opening_Round")
                    .Select(fi => fi.Id)
                    .ToList();

                if (elementsToDelete.Any())
                {
                    doc.Delete(elementsToDelete);
                }

                deleteTrans.Commit();
            }

            // Recopilar todos los muros en el documento actual
            FilteredElementCollector wallCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType();

            // Recopilar todos los ductos en el documento vinculado
            FilteredElementCollector ductCollector = new FilteredElementCollector(linkedDoc)
                .OfClass(typeof(Duct))
                .WhereElementIsNotElementType();

            //openin Rectangular
            FamilySymbol openingSymbolRectangular = GetFamilySymbolByName(doc, "ACM_H_Opening_Rectangular");
            //Firestop rectangular  drwwall and mamposteria
            FamilySymbol openingSymbolDrywall = GetFamilySymbolByName(doc, "ACM_H_Firestop_Rectangular_Drywall");            
            FamilySymbol openingSymbolMamposteria = GetFamilySymbolByName(doc, "ACM_H_Firestop_Rectangular_Mamposteria");

            //openin round
            FamilySymbol openingSymbolRound = GetFamilySymbolByName(doc, "ACM_H_Opening_Round");
            //Firestop round  drwwall and mamposteria
            FamilySymbol openingSymbolRoundDrywall = GetFamilySymbolByName(doc, "ACM_H_Firestop_Round_Drywall");            
            FamilySymbol openingSymbolRoundMamposteria = GetFamilySymbolByName(doc, "ACM_H_Firestop_Round_Mamposteria");
            
            



            if (openingSymbolRectangular == null || openingSymbolRound == null || openingSymbolMamposteria == null || openingSymbolDrywall == null || openingSymbolRoundMamposteria == null || openingSymbolRoundDrywall == null)
            {
                TaskDialog.Show("Error", "No se encontraron todas las familias de apertura requeridas en el proyecto.");
                return Result.Failed;
            }

            using (Transaction trans = new Transaction(doc, "Colocar ACM_Openings"))
            {
                trans.Start();

                ActivarFamilySymbols(openingSymbolRectangular, openingSymbolRound, openingSymbolMamposteria, openingSymbolDrywall, openingSymbolRoundMamposteria, openingSymbolRoundDrywall);
                doc.Regenerate();

                int aperturasCreadas = 0;

                foreach (Duct duct in ductCollector)
                {
                    LocationCurve ductLocation = duct.Location as LocationCurve;
                    if (ductLocation == null) continue;

                    Curve ductCurve = ductLocation.Curve;
                    BoundingBoxXYZ ductBBox = duct.get_BoundingBox(null);
                    if (ductBBox == null) continue;

                    // Transformar la BoundingBox del ducto al espacio del modelo actual
                    Transform transform = linkInstance.GetTotalTransform();
                    ductBBox.Min = transform.OfPoint(ductBBox.Min);
                    ductBBox.Max = transform.OfPoint(ductBBox.Max);

                    Outline outline = new Outline(ductBBox.Min, ductBBox.Max);
                    BoundingBoxIntersectsFilter bboxFilter = new BoundingBoxIntersectsFilter(outline);

                    FilteredElementCollector potentialWalls = new FilteredElementCollector(doc)
                        .OfClass(typeof(Wall))
                        .WherePasses(bboxFilter);

                    foreach (Wall wall in potentialWalls)
                    {
                        GeometryElement wallGeometry = wall.get_Geometry(new Options());
                        if (wallGeometry == null) continue;

                        Solid wallSolid = GetSolidFromGeometryElement(wallGeometry);
                        if (wallSolid == null) continue;

                        GeometryElement ductGeometry = duct.get_Geometry(new Options());
                        if (ductGeometry == null) continue;

                        Solid ductSolid = GetSolidFromGeometryElement(ductGeometry);
                        if (ductSolid == null) continue;

                        // Transformar el sólido del ducto al espacio del modelo actual
                        ductSolid = SolidUtils.CreateTransformed(ductSolid, transform);

                        Solid intersection = null;
                        try
                        {
                            intersection = BooleanOperationsUtils.ExecuteBooleanOperation(ductSolid, wallSolid, BooleanOperationsType.Intersect);
                        }
                        catch
                        {
                            continue;
                        }

                        if (intersection != null && intersection.Volume > 0)
                        {
                            XYZ intersectionPoint = intersection.ComputeCentroid();

                            double height = 0, width = 0, diameter = 0;
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
                                continue;
                            }

                            XYZ offset = isRoundDuct
                                ? new XYZ(0, 0, -(diameter + 2 * OFFSET) / 2 + ROUND_INSERTION_OFFSET)
                                : new XYZ(0, 0, -(height + 2 * OFFSET) / 2 + RECTANGULAR_INSERTION_OFFSET);
                            XYZ adjustedPoint = intersectionPoint + offset;

                            // Obtener el nivel del muro
                            Level wallLevel = doc.GetElement(wall.LevelId) as Level;
                            if (wallLevel == null)
                            {
                                TaskDialog.Show("Error", "No se pudo obtener el nivel del muro.");
                                return Result.Failed;
                            }

                            string wallTypeName = wall.Name.ToLower();
                            FamilySymbol openingSymbol = SelectOpeningSymbol(wallTypeName, isRoundDuct, openingSymbolRectangular, openingSymbolRound, openingSymbolMamposteria, openingSymbolDrywall, openingSymbolRoundMamposteria, openingSymbolRoundDrywall);

                            FamilyInstance openingInstance = doc.Create.NewFamilyInstance(adjustedPoint, openingSymbol, wall, wallLevel, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                            if (isRoundDuct)
                            {
                                SetOpeningDiameter(openingInstance, diameter + 2 * OFFSET);
                            }
                            else
                            {
                                SetOpeningDimensions(openingInstance, height + 2 * OFFSET, width + 2 * OFFSET);
                            }

                            aperturasCreadas++;
                        }
                    }
                }

                trans.Commit();

                TaskDialog.Show("Resultado", $"Se crearon {aperturasCreadas} aperturas.");
            }

            return Result.Succeeded;
        }

        private void ActivarFamilySymbols(params FamilySymbol[] symbols)
        {
            foreach (var symbol in symbols)
            {
                if (!symbol.IsActive) symbol.Activate();
            }
        }

        private FamilySymbol SelectOpeningSymbol(string wallTypeName, bool isRoundDuct, FamilySymbol rectOpening, FamilySymbol roundOpening, FamilySymbol mamposteriaOpening, FamilySymbol drywallOpening, FamilySymbol roundMamposteria, FamilySymbol roundDrywall)
        {
            if (wallTypeName.Contains("muromamposteria"))
                return isRoundDuct ? roundMamposteria : mamposteriaOpening;
            if (wallTypeName.Contains("murodrywall"))
                return isRoundDuct ? roundDrywall : drywallOpening;
            return isRoundDuct ? roundOpening : rectOpening;
        }



        private FamilySymbol GetFamilySymbolByName(Document doc, string familyTypeName)
        {
            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol));

            foreach (FamilySymbol symbol in collector)
            {
                // Verificar si el nombre del tipo de familia coincide
                if (symbol.Name.Equals(familyTypeName, StringComparison.InvariantCultureIgnoreCase))
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
            Parameter openingHeightParam = openingInstance.LookupParameter("Height");
            Parameter openingWidthParam = openingInstance.LookupParameter("Width");

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
            Parameter diameterParam = openingInstance.LookupParameter("Rough Diameter");
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

    public class LinkSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem is RevitLinkInstance;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return false;
        }
    }
}