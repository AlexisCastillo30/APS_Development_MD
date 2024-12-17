using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Development_BIM.core
{
    [Transaction(TransactionMode.Manual)]
    public class Link_OpenWallPipe : IExternalCommand
    {
        private const double OFFSET = 30.0 / 304.8;
        private const double ROUND_INSERTION_OFFSET = 0.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Solicitar al usuario que seleccione el modelo vinculado
            Reference linkRef = uidoc.Selection.PickObject(ObjectType.Element, new LinkSelectionFilterPipe(), "Seleccione el modelo vinculado que contiene la tubería");
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
                    .Where(fi => fi.Symbol.FamilyName == "ACM_P_Firestop_Round")
                    .Select(fi => fi.Id)
                    .ToList();

                if (elementsToDelete.Any())
                {
                    doc.Delete(elementsToDelete);
                }

                deleteTrans.Commit();
            }

            // Recopilar todas las tuberías en el documento vinculado
            FilteredElementCollector pipeCollector = new FilteredElementCollector(linkedDoc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType();

            // Obtener los tipos de aperturas redondas
            FamilySymbol openingSymbolRound = GetFamilySymbolByName(doc, "ACM_P_Opening_Round");
            FamilySymbol openingSymbolRoundDrywall = GetFamilySymbolByName(doc, "ACM_P_Firestop_Round_Drywall");
            FamilySymbol openingSymbolRoundMamposteria = GetFamilySymbolByName(doc, "ACM_P_Firestop_Round_Mamposteria");

            if (openingSymbolRound == null || openingSymbolRoundMamposteria == null || openingSymbolRoundDrywall == null)
            {
                TaskDialog.Show("Error", "No se encontraron todas las familias de apertura requeridas en el proyecto.");
                return Result.Failed;
            }

            using (Transaction trans = new Transaction(doc, "Colocar ACM_Openings"))
            {
                trans.Start();

                ActivarFamilySymbols(openingSymbolRound, openingSymbolRoundMamposteria, openingSymbolRoundDrywall);
                doc.Regenerate();

                int aperturasCreadas = 0;

                foreach (Pipe pipe in pipeCollector)
                {
                    LocationCurve pipeLocation = pipe.Location as LocationCurve;
                    if (pipeLocation == null) continue;

                    Curve pipeCurve = pipeLocation.Curve;
                    BoundingBoxXYZ pipeBBox = pipe.get_BoundingBox(null);
                    if (pipeBBox == null) continue;

                    // Transformar la BoundingBox de la tubería al espacio del modelo actual
                    Transform transform = linkInstance.GetTotalTransform();
                    pipeBBox.Min = transform.OfPoint(pipeBBox.Min);
                    pipeBBox.Max = transform.OfPoint(pipeBBox.Max);

                    Outline outline = new Outline(pipeBBox.Min, pipeBBox.Max);
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

                        GeometryElement pipeGeometry = pipe.get_Geometry(new Options());
                        if (pipeGeometry == null) continue;

                        Solid pipeSolid = GetSolidFromGeometryElement(pipeGeometry);
                        if (pipeSolid == null) continue;

                        // Transformar el sólido de la tubería al espacio del modelo actual
                        pipeSolid = SolidUtils.CreateTransformed(pipeSolid, transform);

                        Solid intersection = null;
                        try
                        {
                            intersection = BooleanOperationsUtils.ExecuteBooleanOperation(pipeSolid, wallSolid, BooleanOperationsType.Intersect);
                        }
                        catch
                        {
                            continue;
                        }

                        if (intersection != null && intersection.Volume > 0)
                        {
                            XYZ intersectionPoint = intersection.ComputeCentroid();

                            double diameter = 0;

                            Parameter diameterParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);

                            if (diameterParam != null)
                            {
                                diameter = diameterParam.AsDouble();
                            }
                            else
                            {
                                continue;
                            }

                            XYZ offset = new XYZ(0, 0, -(diameter + 2 * OFFSET) / 2 + ROUND_INSERTION_OFFSET);
                            XYZ adjustedPoint = intersectionPoint + offset;

                            // Obtener el nivel del muro
                            Level wallLevel = doc.GetElement(wall.LevelId) as Level;
                            if (wallLevel == null)
                            {
                                TaskDialog.Show("Error", "No se pudo obtener el nivel del muro.");
                                return Result.Failed;
                            }

                            string wallTypeName = wall.Name.ToLower();
                            FamilySymbol openingSymbol = SelectOpeningSymbol(wallTypeName, openingSymbolRound, openingSymbolRoundMamposteria, openingSymbolRoundDrywall);

                            FamilyInstance openingInstance = doc.Create.NewFamilyInstance(adjustedPoint, openingSymbol, wall, wallLevel, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                            SetOpeningDiameter(openingInstance, diameter + 2 * OFFSET);

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

        private FamilySymbol SelectOpeningSymbol(string wallTypeName, FamilySymbol roundOpening, FamilySymbol roundMamposteria, FamilySymbol roundDrywall)
        {
            if (wallTypeName.Contains("muromamposteria"))
                return roundMamposteria;
            if (wallTypeName.Contains("murodrywall"))
                return roundDrywall;
            return roundOpening;
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

    public class LinkSelectionFilterPipe : ISelectionFilter
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
