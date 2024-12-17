using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Development_BIM.UI
{
    public partial class PanelAcabados : Window
    {
        private UIDocument uidoc;
        private Document doc;
        public List<RoomData> roomsSeleccionados;

        public PanelAcabados(UIDocument uidocument)
        {
            InitializeComponent();
            this.uidoc = uidocument;
            this.doc = uidocument.Document;
            roomsSeleccionados = new List<RoomData>();

            // Llenar el ListView con los rooms disponibles
            CargarRooms();
        }

        private void CargarRooms()
        {
            // Obtener los rooms del documento
            FilteredElementCollector roomCollector = new FilteredElementCollector(doc);
            ICollection<Element> rooms = roomCollector.OfCategory(BuiltInCategory.OST_Rooms)
                                                      .WhereElementIsNotElementType()
                                                      .ToElements();

            // Obtener la lista de muros de acabado únicos
            List<string> murosAcabado = ObtenerMurosAcabado();

            // Crear una lista de RoomData para almacenar la información
            List<RoomData> roomDataList = new List<RoomData>();

            // Agregar los rooms al ListView
            foreach (Element room in rooms)
            {
                string roomName = room.Name;

                // Si no hay muros de acabado, mostrar un mensaje predeterminado
                if (murosAcabado.Count == 0)
                {
                    murosAcabado.Add("No se encontró un muro de acabado");
                }

                // Agregar un RoomData con los muros de acabado únicos disponibles
                roomDataList.Add(new RoomData
                {
                    RoomName = roomName,
                    WallFinishes = murosAcabado, // Lista con muros únicos
                    WallFinishMasonry = murosAcabado.FirstOrDefault(),   // Selección inicial predeterminada
                    WallFinishConcrete = murosAcabado.FirstOrDefault(),
                    WallFinishDrywall = murosAcabado.FirstOrDefault()
                });
            }

            // Asignar la lista de rooms al ListView
            RoomsListView.ItemsSource = roomDataList;
        }
        private List<string> ObtenerMurosAcabado()
        {
            // Utilizar un HashSet para evitar tipos de muros duplicados
            HashSet<string> murosAcabado = new HashSet<string>();

            // Obtener los tipos de muros del documento
            FilteredElementCollector wallTypeCollector = new FilteredElementCollector(doc);
            ICollection<Element> wallTypes = wallTypeCollector.OfClass(typeof(WallType))
                                                              .WhereElementIsElementType()
                                                              .ToElements();

            // Filtrar los tipos de muros que contienen "Acabado" en su nombre
            foreach (WallType wallType in wallTypes)
            {
                string wallTypeName = wallType.Name;
                if (wallTypeName.Contains("Acabado")) // Verificar si el nombre contiene "Acabado"
                {
                    murosAcabado.Add(wallTypeName); // HashSet evita duplicados automáticamente
                }
            }

            // Convertir el HashSet a List y ordenar alfabéticamente
            return murosAcabado.OrderBy(w => w).ToList(); // Ordenar alfabéticamente
        }


        private void ApplyWallFinish_Click(object sender, RoutedEventArgs e)
        {
            // Obtener la lista de RoomData seleccionados por el usuario
            roomsSeleccionados = ((List<RoomData>)RoomsListView.ItemsSource)
                .Where(r => r.IsSelected)
                .ToList();

            // Verificar que hay rooms seleccionados
            if (roomsSeleccionados.Count == 0)
            {
                MessageBox.Show("Por favor, seleccione al menos una habitación.");
                return;
            }

            // Cerrar el panel y continuar con la aplicación de acabados
            this.DialogResult = true; // Marca que la selección fue exitosa
            this.Close();
        }
        private void botonCancelar_Click(object sender, RoutedEventArgs e)
        {            
            this.Hide();
        }
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void TextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {

        }
    }

    public class RoomData
    {
        public string RoomName { get; set; }
        public List<string> WallFinishes { get; set; } // Lista de opciones de acabados
        public string WallFinishMasonry { get; set; }
        public string WallFinishConcrete { get; set; }
        public string WallFinishDrywall { get; set; }

        // Propiedad para enlazar el estado del CheckBox
        public bool IsSelected { get; set; } = false;  // Por defecto no está seleccionado
    }
}
