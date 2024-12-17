using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;
using Development_BIM;
using Development_BIM.UI;

namespace Development_BIM.core
{
    [Transaction(TransactionMode.Manual)]
    public class AcabadosRooms : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Obtener las habitaciones del proyecto sin necesidad de seleccionar manualmente
            FilteredElementCollector roomCollector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType();

            IList<Element> selectedRooms = roomCollector.ToElements();

            if (selectedRooms.Count == 0)
            {
                TaskDialog.Show("Error", "No se encontraron habitaciones.");
                return Result.Failed;
            }

            // Mostrar el panel de acabados y obtener la selección del usuario
            PanelAcabados panelAcabados = new PanelAcabados(uidoc);
            bool? result = panelAcabados.ShowDialog();

            if (result != true)
            {
                return Result.Cancelled; // Si se cancela, salir del comando.
            }

            // Obtener los rooms seleccionados junto con los acabados
            List<RoomData> roomsSeleccionados = panelAcabados.roomsSeleccionados;

            // Opciones de límites para elementos espaciales
            SpatialElementBoundaryOptions sbo = new SpatialElementBoundaryOptions();

            // Parámetros a ser utilizados para el tipo de muro y la altura
            string whParam = "Unbounded Height";

            List<string> modifiedRooms = new List<string>();  // Lista para almacenar habitaciones modificadas
            List<string> errorList = new List<string>();
            List<RoomWallData> finalWalls = new List<RoomWallData>();

            // Recoger todos los muros existentes una sola vez para mejorar el rendimiento
            FilteredElementCollector wallCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType();

            // Diccionario para almacenar los tipos de muros y evitar buscar repetidamente
            Dictionary<string, WallType> wallTypeDict = new Dictionary<string, WallType>();
            foreach (WallType wallType in new FilteredElementCollector(doc).OfClass(typeof(WallType)))
            {
                wallTypeDict[wallType.Name] = wallType;
            }

            using (Transaction tx = new Transaction(doc, "Crear muros alrededor de habitaciones"))
            {
                tx.Start();

                foreach (Element element in selectedRooms)
                {
                    Room room = element as Room;
                    if (room == null) continue;

                    // Obtener el WallFinishMasonry seleccionado por el usuario para esta habitación
                    RoomData roomData = roomsSeleccionados.FirstOrDefault(r => r.RoomName == room.Name);
                    if (roomData == null)
                    {
                        errorList.Add($"Habitación {room.Name} no seleccionada.");
                        continue;
                    }

                    WallType wallType = GetWallType(roomData.WallFinishMasonry, wallTypeDict);
                    if (wallType == null)
                    {
                        errorList.Add($"Habitación {room.Name} - Parámetro de tipo de acabado no es válido.");
                        continue;
                    }

                    double wallThickness = wallType.Width;
                    double wallOffset = wallThickness / 2.0;
                    double wallHeight = GetWallHeight(room, whParam);
                    if (wallHeight <= 0)
                    {
                        errorList.Add($"Habitación {room.Name} - Parámetro de altura no válido o igual a cero.");
                        continue;
                    }

                    IList<IList<BoundarySegment>> boundaries = room.GetBoundarySegments(sbo);
                    if (boundaries == null || boundaries.Count == 0)
                    {
                        errorList.Add($"Habitación {room.Name} - La habitación debe estar cerrada.");
                        continue;
                    }

                    // Crear muros alrededor de la habitación
                    List<Wall> createdWalls = new List<Wall>();
                    foreach (IList<BoundarySegment> segGroup in boundaries)
                    {
                        List<Curve> mergedCurves = MergeColinearCurves(segGroup);
                        for (int i = 0; i < mergedCurves.Count; i++)
                        {
                            Curve boundaryCurve = mergedCurves[i];
                            if (boundaryCurve == null) continue;

                            // Verificar si el siguiente segmento está alineado correctamente
                            if (i < mergedCurves.Count - 1)
                            {
                                Curve nextCurve = mergedCurves[i + 1];
                                XYZ endOfCurrent = boundaryCurve.GetEndPoint(1);
                                XYZ startOfNext = nextCurve.GetEndPoint(0);

                                // Si los extremos no coinciden, ajustarlos para evitar la discontinuidad
                                if (!endOfCurrent.IsAlmostEqualTo(startOfNext, 0.001))
                                {
                                    Line correctedLine = Line.CreateBound(endOfCurrent, startOfNext);
                                    boundaryCurve = correctedLine;  // Actualizar el segmento corregido
                                }
                            }

                            Curve invertedCurve = boundaryCurve.CreateReversed();
                            XYZ direction = GetOffsetDirection(invertedCurve);

                            // Desplazar la curva hacia el interior del room
                            XYZ offsetVector = direction * wallOffset;
                            Curve offsetCurve = invertedCurve.CreateTransformed(Transform.CreateTranslation(offsetVector));

                            // Crear el muro con la curva desplazada
                            Wall wall = Wall.Create(doc, offsetCurve, wallType.Id, room.LevelId, wallHeight, 0, false, false);
                            if (wall != null)
                            {
                                // Configurar propiedades del muro creado
                                wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).Set(
                                    room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET).AsDouble());
                                wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).Set(wallHeight);
                                wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING).Set(0);  // No limitar habitación
                                wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set((int)WallLocationLine.FinishFaceInterior);

                                createdWalls.Add(wall);
                            }
                        }
                    }

                    // Intentar unir los muros creados con los muros existentes cercanos
                    TryJoinWallsWithExistingWalls(doc, createdWalls, wallCollector.ToList());

                    finalWalls.Add(new RoomWallData { Room = room, Walls = createdWalls });

                    // Añadir la habitación a la lista de habitaciones modificadas
                    modifiedRooms.Add(room.Name);
                }

                tx.Commit();
            }

            // Mostrar las habitaciones modificadas
            if (modifiedRooms.Count > 0)
            {
                string modifiedMessage = "Habitaciones modificadas:\n" + string.Join("\n", modifiedRooms);
                TaskDialog.Show("Éxito", modifiedMessage);
            }
            else
            {
                TaskDialog.Show("Error", "No se modificaron habitaciones.");
            }

            return Result.Succeeded;
        }

        // Método para obtener el tipo de muro
        private WallType GetWallType(string wallTypeName, Dictionary<string, WallType> wallTypeDict)
        {
            if (string.IsNullOrEmpty(wallTypeName)) return null;
            if (wallTypeDict.ContainsKey(wallTypeName)) return wallTypeDict[wallTypeName];
            return null;
        }

        // Método para obtener la altura del muro
        private double GetWallHeight(Room room, string whParam)
        {
            Parameter heightParam = room.LookupParameter(whParam);
            return heightParam != null ? heightParam.AsDouble() : 0;
        }

        // Método para fusionar curvas colineales
        private List<Curve> MergeColinearCurves(IList<BoundarySegment> segments)
        {
            List<Curve> mergedCurves = new List<Curve>();

            for (int i = 0; i < segments.Count; i++)
            {
                Curve currentCurve = segments[i].GetCurve();
                if (currentCurve == null) continue;

                if (mergedCurves.Count == 0)
                {
                    mergedCurves.Add(currentCurve);
                }
                else
                {
                    Curve lastCurve = mergedCurves[mergedCurves.Count - 1];

                    if (AreCurvesColinear(lastCurve, currentCurve))
                    {
                        XYZ startPoint = lastCurve.GetEndPoint(0);
                        XYZ endPoint = currentCurve.GetEndPoint(1);
                        Line mergedLine = Line.CreateBound(startPoint, endPoint);

                        mergedCurves[mergedCurves.Count - 1] = mergedLine;
                    }
                    else
                    {
                        mergedCurves.Add(currentCurve);
                    }
                }
            }

            return mergedCurves;
        }

        // Método para verificar si dos curvas son colineales
        private bool AreCurvesColinear(Curve curve1, Curve curve2)
        {
            Line line1 = curve1 as Line;
            Line line2 = curve2 as Line;

            if (line1 == null || line2 == null) return false;

            XYZ direction1 = (line1.GetEndPoint(1) - line1.GetEndPoint(0)).Normalize();
            XYZ direction2 = (line2.GetEndPoint(1) - line2.GetEndPoint(0)).Normalize();

            return direction1.IsAlmostEqualTo(direction2) || direction1.IsAlmostEqualTo(-direction2);
        }

        // Método para obtener la dirección de desplazamiento perpendicular a la curva
        private XYZ GetOffsetDirection(Curve curve)
        {
            XYZ normal = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
            return normal.CrossProduct(XYZ.BasisZ);
        }

        // Método para intentar unir muros creados con muros existentes
        private void TryJoinWallsWithExistingWalls(Document doc, List<Wall> createdWalls, ICollection<Element> existingWalls)
        {
            foreach (Wall newWall in createdWalls)
            {
                foreach (Wall existingWall in existingWalls)
                {
                    try
                    {
                        if (newWall.Id == existingWall.Id) continue;

                        if (!JoinGeometryUtils.AreElementsJoined(doc, newWall, existingWall))
                        {
                            JoinGeometryUtils.JoinGeometry(doc, newWall, existingWall);
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException)
                    {
                        // Ignorar errores de unión
                    }
                }
            }
        }
    }

    public class RoomWallData
    {
        public Room Room { get; set; }
        public List<Wall> Walls { get; set; }
    }
}
