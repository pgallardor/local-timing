using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RFIDLocal.Domain;
using RFIDLocal.Services;
using RFIDTiming.Domain.Results;

namespace RFIDLocal.UI
{
    public sealed class MainForm : Form
    {
        private sealed class DistanceFilterChoice
        {
            public DistanceFilterChoice(string label, string value) { Label = label; Value = value; }
            public string Label { get; private set; }
            public string Value { get; private set; }
            public override string ToString() { return Label; }
        }

        private sealed class CategoryFilterChoice
        {
            public CategoryFilterChoice(string label, string value, bool isAll, bool isUncategorized)
            { Label = label; Value = value; IsAll = isAll; IsUncategorized = isUncategorized; }
            public string Label { get; private set; }
            public string Value { get; private set; }
            public bool IsAll { get; private set; }
            public bool IsUncategorized { get; private set; }
            public override string ToString() { return Label; }
        }

        private readonly LocalWorkspace workspace;
        private readonly ListBox eventsList = new ListBox();
        private readonly Label summary = new Label();
        private readonly Label operationSummary = new Label();
        private readonly NumericUpDown receiverPort = new NumericUpDown();
        private readonly DataGridView rosterGrid = NewGrid();
        private readonly DataGridView resultsGrid = NewGrid();
        private readonly DataGridView receiptsGrid = NewGrid();
        private readonly DataGridView auditGrid = NewGrid();
        private readonly ComboBox rosterDistanceFilter = new ComboBox();
        private readonly TextBox rosterBibFilter = new TextBox();
        private readonly Label rosterCountLabel = new Label();
        private readonly ComboBox resultsCategoryFilter = new ComboBox();
        private readonly TextBox resultsBibFilter = new TextBox();
        private readonly Label resultsCountLabel = new Label();
        private DataTable rosterTable;
        private DataTable resultsTable;
        private bool loadingRosterFilters;
        private bool loadingResultsFilters;
        private readonly Button reprocessButton = new Button { Text = "Reprocesar chip seleccionado", AutoSize = true, Height = 28 };
        private readonly ToolStripMenuItem enableButton = new ToolStripMenuItem("Habilitar recepción local");
        private readonly ToolStripMenuItem closeButton = new ToolStripMenuItem("Cerrar recepción");
        private readonly Button receiverButton = new Button { Text = "Iniciar receptor" };
        private readonly ToolStripMenuItem claimButton = new ToolStripMenuItem("Tomar autoridad / renovar lector");
        private readonly ToolStripMenuItem syncButton = new ToolStripMenuItem("Sincronizar ahora");
        private readonly ToolStripMenuItem publishButton = new ToolStripMenuItem("Crear paquete de publicación");
        private readonly ToolStripMenuItem receiptButton = new ToolStripMenuItem("Cargar recibo web");
        private readonly ToolStripMenuItem toggleDnfMenuItem = new ToolStripMenuItem("Marcar o quitar DNF seleccionado");
        private readonly ToolStripMenuItem officialStartMenuItem = new ToolStripMenuItem("Registrar salida oficial");
        private readonly ToolStripMenuItem officialCategoryStartMenuItem = new ToolStripMenuItem("Registrar salida oficial por categoría");
        private readonly ToolStripMenuItem correctMarkMenuItem = new ToolStripMenuItem("Corregir marca seleccionada");
        private readonly ToolStripMenuItem recalculateMenuItem = new ToolStripMenuItem("Recalcular resultados");
        private readonly ToolStripMenuItem exportResultsMenuItem = new ToolStripMenuItem("Exportar resultados a CSV");
        private readonly ToolStripMenuItem importPreparationMenuItem = new ToolStripMenuItem("Preparar desde remoto");
        private readonly ToolStripMenuItem operatorLoginMenuItem = new ToolStripMenuItem("Iniciar sesión en plataforma");
        private readonly ToolStripMenuItem archiveEventMenuItem = new ToolStripMenuItem("Archivar evento seleccionado");
        private readonly ToolStripMenuItem restoreArchivedEventMenuItem = new ToolStripMenuItem("Restaurar evento archivado…");
        private readonly Label footerStatus = new Label();
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private readonly ManualResetEventSlim calculationFinished = new ManualResetEventSlim(true);
        private readonly OperatorSessionStore operatorSessionStore = new OperatorSessionStore();
        private readonly OperatorPlatformClient operatorPlatformClient;
        private LocalRaceDatabase database;
        private LocalEvent selectedEvent;
        private LocalIngestReceiver receiver;
        private string displayedRosterRevision;
        private string appliedCalculationRevision;
        private int calculationPending;
        private volatile bool closing;

        public MainForm()
        {
            Text = "RFID Local Race · operación offline";
            Width = 1420; Height = 900; MinimumSize = new Size(1000, 700);
            StartPosition = FormStartPosition.CenterScreen;
            workspace = new LocalWorkspace(LocalWorkspace.DefaultDataRoot);
            operatorPlatformClient = new OperatorPlatformClient(operatorSessionStore);
            BuildUi();
            refreshTimer.Interval = 2000;
            refreshTimer.Tick += (sender, args) => RefreshPanels();
            refreshTimer.Start();
            Shown += (sender, args) => RefreshEventList(null);
            FormClosing += ClosingApplication;
        }

        private void BuildUi()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(layout);

            var menu = new MenuStrip { Dock = DockStyle.Fill, Padding = new Padding(6, 2, 0, 2) };
            var eventMenu = new ToolStripMenuItem("Evento");
            eventMenu.DropDownItems.Add(MenuItem("Crear evento local", CreateEvent));
            eventMenu.DropDownItems.Add(MenuItem("Actualizar lista de eventos", (s, e) => RefreshEventList(selectedEvent == null ? null : selectedEvent.EventId)));
            eventMenu.DropDownItems.Add(new ToolStripSeparator());
            eventMenu.DropDownItems.Add(claimButton); claimButton.Click += ClaimAuthority;
            eventMenu.DropDownItems.Add(new ToolStripSeparator());
            eventMenu.DropDownItems.Add(archiveEventMenuItem); archiveEventMenuItem.Click += ArchiveSelectedEvent;
            eventMenu.DropDownItems.Add(restoreArchivedEventMenuItem); restoreArchivedEventMenuItem.Click += RestoreArchivedEvent;

            var rosterMenu = new ToolStripMenuItem("Nómina");
            rosterMenu.DropDownItems.Add(MenuItem("Importar CSV o Excel", ImportRoster));
            rosterMenu.DropDownItems.Add(toggleDnfMenuItem); toggleDnfMenuItem.Click += ToggleDnf;

            var operationMenu = new ToolStripMenuItem("Operación");
            operationMenu.DropDownItems.Add(enableButton); enableButton.Click += EnableReception;
            operationMenu.DropDownItems.Add(closeButton); closeButton.Click += CloseReception;
            operationMenu.DropDownItems.Add(new ToolStripSeparator());
            operationMenu.DropDownItems.Add(officialStartMenuItem); officialStartMenuItem.Click += SetOfficialStart;
            operationMenu.DropDownItems.Add(officialCategoryStartMenuItem); officialCategoryStartMenuItem.Click += SetOfficialCategoryStart;
            operationMenu.DropDownItems.Add(correctMarkMenuItem); correctMarkMenuItem.Click += CorrectMark;

            var resultsMenu = new ToolStripMenuItem("Resultados");
            resultsMenu.DropDownItems.Add(recalculateMenuItem); recalculateMenuItem.Click += (s, e) => RequestCalculation(true);
            resultsMenu.DropDownItems.Add(exportResultsMenuItem); exportResultsMenuItem.Click += ExportResults;

            var platformMenu = new ToolStripMenuItem("Plataforma");
            platformMenu.DropDownItems.Add(operatorLoginMenuItem); operatorLoginMenuItem.Click += LoginToPlatform;
            platformMenu.DropDownItems.Add(importPreparationMenuItem); importPreparationMenuItem.Click += ImportPreparation;
            platformMenu.DropDownItems.Add(new ToolStripSeparator());
            platformMenu.DropDownItems.Add(syncButton); syncButton.Click += SyncDirectly;
            platformMenu.DropDownItems.Add(publishButton); publishButton.Click += CreatePublicationPackage;
            platformMenu.DropDownItems.Add(receiptButton); receiptButton.Click += ImportPublicationReceipt;

            var backupMenu = new ToolStripMenuItem("Respaldo");
            backupMenu.DropDownItems.Add(MenuItem("Crear respaldo del evento", CreateBackup));
            backupMenu.DropDownItems.Add(MenuItem("Restaurar respaldo", RestoreBackup));
            menu.Items.AddRange(new ToolStripItem[] { eventMenu, rosterMenu, operationMenu, resultsMenu, platformMenu, backupMenu });
            MainMenuStrip = menu;
            layout.Controls.Add(menu, 0, 0);

            summary.Dock = DockStyle.Fill; summary.Padding = new Padding(12, 8, 12, 8);
            summary.BackColor = Color.FromArgb(239, 244, 250); summary.Font = new Font(Font, FontStyle.Bold);
            layout.Controls.Add(summary, 0, 1);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 275, FixedPanel = FixedPanel.Panel1 };
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(new Label { Text = "Eventos locales", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), Font = new Font(Font, FontStyle.Bold) }, 0, 0);
            eventsList.Dock = DockStyle.Fill; eventsList.IntegralHeight = false; eventsList.SelectedIndexChanged += EventSelectionChanged;
            left.Controls.Add(eventsList, 0, 1); split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(BuildTabs());
            layout.Controls.Add(split, 0, 2);

            footerStatus.Dock = DockStyle.Fill; footerStatus.Padding = new Padding(8, 4, 8, 0); footerStatus.Text = "Datos locales en " + workspace.DataRoot;
            layout.Controls.Add(footerStatus, 0, 3);
            UpdateActions();
        }

        private TabControl BuildTabs()
        {
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var roster = new TabPage("Nómina");
            rosterGrid.SelectionChanged += (sender, args) => UpdateActions();
            var rosterToolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(8, 7, 8, 4),
                WrapContents = false,
                AutoSize = false
            };
            rosterToolbar.Controls.Add(new Label { Text = "Distancia", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            rosterDistanceFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            rosterDistanceFilter.Width = 170;
            rosterDistanceFilter.SelectedIndexChanged += (sender, args) => ApplyRosterFilters();
            rosterToolbar.Controls.Add(rosterDistanceFilter);
            rosterToolbar.Controls.Add(new Label { Text = "Dorsal (parcial)", AutoSize = true, Padding = new Padding(6, 5, 0, 0) });
            rosterBibFilter.Width = 130;
            rosterBibFilter.MaxLength = 50;
            rosterBibFilter.TextChanged += (sender, args) => ApplyRosterFilters();
            rosterToolbar.Controls.Add(rosterBibFilter);
            var clearRosterFilters = new Button { Text = "Limpiar filtros", AutoSize = true, Height = 27, Margin = new Padding(8, 1, 4, 0) };
            clearRosterFilters.Click += ClearRosterFilters;
            rosterToolbar.Controls.Add(clearRosterFilters);
            rosterCountLabel.AutoSize = true;
            rosterCountLabel.Padding = new Padding(8, 5, 0, 0);
            rosterToolbar.Controls.Add(rosterCountLabel);
            roster.Controls.Add(rosterGrid);
            roster.Controls.Add(rosterToolbar);

            var operation = new TabPage("Operación");
            var operationToolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 45, Padding = new Padding(8), WrapContents = false };
            operationToolbar.Controls.Add(new Label { Text = "Puerto loopback", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            receiverPort.Minimum = 1024; receiverPort.Maximum = 65535; receiverPort.Value = LocalWorkspace.DefaultReceiverPort; receiverPort.Width = 90;
            operationToolbar.Controls.Add(receiverPort);
            receiverButton.Width = 125; receiverButton.Click += ToggleReceiver; operationToolbar.Controls.Add(receiverButton);
            operationSummary.Dock = DockStyle.Fill; operationSummary.Padding = new Padding(14); operationSummary.Font = new Font(Font.FontFamily, 11);
            operation.Controls.Add(operationSummary); operation.Controls.Add(operationToolbar);

            var results = new TabPage("Resultados y podios");
            resultsGrid.SelectionChanged += (sender, args) => UpdateActions();
            var resultsToolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(8, 7, 8, 4),
                WrapContents = false,
                AutoSize = false
            };
            resultsToolbar.Controls.Add(new Label { Text = "Dorsal (parcial)", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            resultsBibFilter.Width = 130;
            resultsBibFilter.MaxLength = 50;
            resultsBibFilter.TextChanged += (sender, args) => ApplyResultsFilters();
            resultsToolbar.Controls.Add(resultsBibFilter);
            resultsToolbar.Controls.Add(new Label { Text = "Categoría", AutoSize = true, Padding = new Padding(8, 5, 0, 0) });
            resultsCategoryFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            resultsCategoryFilter.Width = 180;
            resultsCategoryFilter.SelectedIndexChanged += (sender, args) => ApplyResultsFilters();
            resultsToolbar.Controls.Add(resultsCategoryFilter);
            var clearResultsFilters = new Button { Text = "Limpiar filtros", AutoSize = true, Height = 27, Margin = new Padding(8, 1, 4, 0) };
            clearResultsFilters.Click += ClearResultsFilters;
            resultsToolbar.Controls.Add(clearResultsFilters);
            resultsCountLabel.AutoSize = true;
            resultsCountLabel.Padding = new Padding(8, 5, 0, 0);
            resultsToolbar.Controls.Add(resultsCountLabel);
            results.Controls.Add(resultsGrid);
            results.Controls.Add(resultsToolbar);

            var incidents = new TabPage("Incidentes y recibos");
            var incidentHeader = new Label { Dock = DockStyle.Top, Height = 56, Padding = new Padding(10), Name = "incidentHeader" };
            var incidentActions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(5) };
            reprocessButton.Click += ReprocessUnknownChip; incidentActions.Controls.Add(reprocessButton);
            receiptsGrid.SelectionChanged += (sender, args) => UpdateActions();
            incidents.Controls.Add(receiptsGrid); incidents.Controls.Add(incidentHeader); incidents.Controls.Add(incidentActions);

            var history = new TabPage("Historial"); history.Controls.Add(auditGrid);
            tabs.TabPages.Add(roster); tabs.TabPages.Add(operation); tabs.TabPages.Add(results); tabs.TabPages.Add(incidents); tabs.TabPages.Add(history);
            return tabs;
        }

        private static ToolStripMenuItem MenuItem(string label, EventHandler handler)
        {
            var item = new ToolStripMenuItem(label);
            item.Click += handler;
            return item;
        }

        private static DataGridView NewGrid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AutoGenerateColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None
            };
        }

        private static DataTable ToTable(IList<Dictionary<string, object>> rows, params string[] columns)
        {
            var table = new DataTable();
            foreach (string column in columns) table.Columns.Add(column, typeof(object));
            foreach (Dictionary<string, object> row in rows)
            {
                DataRow dataRow = table.NewRow();
                foreach (string column in columns)
                {
                    object value;
                    dataRow[column] = row.TryGetValue(column, out value) && value != null ? value : DBNull.Value;
                }
                table.Rows.Add(dataRow);
            }
            return table;
        }

        private void RefreshEventList(string preserveId)
        {
            try
            {
                IList<LocalEvent> events = workspace.ListEvents();
                eventsList.SelectedIndexChanged -= EventSelectionChanged;
                eventsList.Items.Clear();
                foreach (LocalEvent race in events) eventsList.Items.Add(race);
                int selectIndex = -1;
                if (!String.IsNullOrWhiteSpace(preserveId))
                    for (int i = 0; i < eventsList.Items.Count; i++)
                        if (((LocalEvent)eventsList.Items[i]).EventId == preserveId) { selectIndex = i; break; }
                if (selectIndex < 0 && eventsList.Items.Count > 0) selectIndex = 0;
                if (selectIndex >= 0) eventsList.SelectedIndex = selectIndex;
                eventsList.SelectedIndexChanged += EventSelectionChanged;
                if (selectIndex >= 0) SelectEvent((LocalEvent)eventsList.SelectedItem);
                else ShowNoEvent();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo abrir la lista local. No se inicializó una base vacía.\r\n" + ex.Message,
                    "Datos locales", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ShowNoEvent();
            }
        }

        private void EventSelectionChanged(object sender, EventArgs e)
        {
            LocalEvent race = eventsList.SelectedItem as LocalEvent;
            if (race != null) SelectEvent(race);
        }

        private void SelectEvent(LocalEvent race)
        {
            if (selectedEvent != null && selectedEvent.EventId == race.EventId && database != null)
            {
                selectedEvent = database.GetSummary();
                RefreshPanels();
                return;
            }
            calculationFinished.Wait(10000);
            StopReceiver();
            if (database != null) { database.Dispose(); database = null; }
            database = workspace.Open(race);
            selectedEvent = database.GetSummary();
            displayedRosterRevision = null; appliedCalculationRevision = null;
            resultsGrid.DataSource = null;
            ResetRosterFilters();
            resultsTable = null;
            ResetResultsFilters();
            RefreshPanels();
            TryStartReceiver(false);
        }

        private void ShowNoEvent()
        {
            StopReceiver(); if (database != null) { database.Dispose(); database = null; }
            selectedEvent = null; summary.Text = "Crea un evento, restaura un respaldo o recupera un evento archivado desde el menú Evento.";
            operationSummary.Text = "No hay un evento abierto.";
            rosterGrid.DataSource = null; resultsGrid.DataSource = null; receiptsGrid.DataSource = null; auditGrid.DataSource = null;
            rosterTable = null; resultsTable = null;
            ResetRosterFilters();
            ResetResultsFilters();
            UpdateActions();
        }

        private void CreateEvent(object sender, EventArgs e)
        {
            string name, timeZone; DateTime date; IList<string> distances;
            if (!Dialogs.CreateEvent(this, out name, out date, out timeZone, out distances)) return;
            IDictionary<string, bool> exclusionPolicies;
            if (!Dialogs.ConfigureDistancePolicies(this, distances, out exclusionPolicies)) return;
            try
            {
                LocalDevice device;
                LocalEvent created = workspace.CreateEvent(name, date, timeZone, distances, exclusionPolicies, out device);
                RefreshEventList(created.EventId);
                MessageBox.Show(this,
                    "Evento local creado en borrador.\r\n\r\nEn RFIDTiming, elija RFIDLocal en “Origen de eventos”, pulse “Listar eventos” y seleccione “" +
                    created.Name + "”. El endpoint, el evento y el lector se completan desde esta app.\r\n\r\n" +
                    "Credencial local del lector: " + device.Credential + "\r\n\r\nGuarde esta credencial en el perfil de RFIDTiming: no queda almacenada en texto legible y se muestra una sola vez. Puede rotarla después.",
                    "Credencial del lector", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError("No se pudo crear el evento", ex); }
        }

        private void ArchiveSelectedEvent(object sender, EventArgs e)
        {
            if (database == null || selectedEvent == null) { ShowSelectEvent(); return; }
            if (selectedEvent.Status == "open")
            {
                MessageBox.Show(this, "Cierra la recepción antes de archivar el evento.", "Recepción activa", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string eventId = selectedEvent.EventId;
            string eventName = selectedEvent.Name;
            if (!Dialogs.Confirm(this,
                "El evento “" + eventName + "” saldrá de la lista activa y quedará guardado en Archivados. Podrás restaurarlo después con todos sus datos. ¿Continuar?",
                "Archivar evento")) return;
            if (!calculationFinished.Wait(10000))
            {
                MessageBox.Show(this, "Espera a que termine el cálculo de resultados e inténtalo nuevamente.", "Cálculo en curso", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StopReceiver();
            if (database != null) { database.Dispose(); database = null; }
            selectedEvent = null;
            try
            {
                workspace.ArchiveEvent(eventId);
                RefreshEventList(null);
                footerStatus.Text = "Evento “" + eventName + "” archivado. Puedes restaurarlo desde Evento > Restaurar evento archivado.";
            }
            catch (Exception ex)
            {
                RefreshEventList(eventId);
                ShowError("No se pudo archivar el evento", ex);
            }
        }

        private void RestoreArchivedEvent(object sender, EventArgs e)
        {
            try
            {
                IList<LocalEvent> archived = workspace.ListArchivedEvents();
                if (archived.Count == 0)
                {
                    MessageBox.Show(this, "No hay eventos archivados para restaurar.", "Eventos archivados", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                LocalEvent choice = Dialogs.ChooseArchivedEvent(this, archived);
                if (choice == null) return;
                string preserveId = selectedEvent == null ? null : selectedEvent.EventId;
                LocalEvent restored = workspace.RestoreArchivedEvent(choice.EventId);
                RefreshEventList(String.IsNullOrWhiteSpace(preserveId) ? restored.EventId : preserveId);
                footerStatus.Text = "Evento “" + restored.Name + "” restaurado a la lista local.";
            }
            catch (Exception ex) { ShowError("No se pudo restaurar el evento archivado", ex); }
        }

        private void ImportRoster(object sender, EventArgs e)
        {
            if (database == null) { ShowSelectEvent(); return; }
            if (!selectedEvent.HasAuthority) { MessageBox.Show(this, "La copia restaurada está en consulta. Tome autoridad local antes de cambiar la nómina.", "Solo lectura", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (selectedEvent.Status == "open" && !Dialogs.Confirm(this,
                "La nómina cambiará mientras la recepción está habilitada. Las lecturas ya guardadas se conservan y el cambio crea una nueva revisión. ¿Continuar?", "Actualizar nómina durante operación")) return;
            using (var dialog = new OpenFileDialog { Title = "Importar nómina", Filter = "Nómina CSV o Excel|*.csv;*.xlsx|CSV|*.csv|Excel|*.xlsx", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    bool isCsv = String.Equals(Path.GetExtension(dialog.FileName), ".csv", StringComparison.OrdinalIgnoreCase);
                    IList<string> sampleValues;
                    IList<string> headers = RosterFileReader.ReadHeaders(dialog.FileName, out sampleValues);
                    RosterColumnMapping mapping;
                    if (!Dialogs.MapRosterColumns(this, headers, sampleValues, isCsv, out mapping)) return;
                    string fallbackDistance = null;
                    if (isCsv && mapping.Distance < 0)
                    {
                        fallbackDistance = Dialogs.Choose(this, "Distancia de la nómina CSV",
                            "El archivo no tiene una columna de distancia asignada. Elige la distancia que se aplicará a todas sus filas.", database.GetDistances());
                        if (fallbackDistance == null) return;
                    }
                    RosterImportPreview preview = RosterFileReader.Read(dialog.FileName, fallbackDistance, mapping);
                    database.AssessRosterImport(preview);
                    if (preview.Rows.Count == 0)
                    {
                        MessageBox.Show(this, String.Join("\r\n", preview.Errors), "Revisar archivo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (!Dialogs.PreviewRoster(this, preview)) return;
                    database.ApplyRoster(preview.Rows);
                    footerStatus.Text = "Importación guardada como nueva revisión de nómina.";
                    RefreshEventList(selectedEvent.EventId);
                }
                catch (Exception ex) { ShowError("No se pudo importar la nómina", ex); }
            }
        }

        private void EnableReception(object sender, EventArgs e)
        {
            if (database == null) { ShowSelectEvent(); return; }
            try
            {
                if (selectedEvent.Status == "closed" && !Dialogs.Confirm(this,
                    "Reabrir la recepción permite al lector entregar nuevas lecturas. ¿Habilitarla?", "Reabrir recepción")) return;
                string previousStatus = selectedEvent.Status;
                database.SetEventStatus("open");
                if (!TryStartReceiver(true)) { database.SetEventStatus(previousStatus); return; }
                RefreshPanels(); RefreshEventList(selectedEvent.EventId);
            }
            catch (Exception ex) { ShowError("No se pudo habilitar la recepción", ex); }
        }

        private void CloseReception(object sender, EventArgs e)
        {
            if (database == null) return;
            if (!Dialogs.Confirm(this, "El receptor local conservará las lecturas como bloqueadas y rechazará nuevos cruces hasta reabrir. ¿Cerrar recepción?", "Cerrar recepción")) return;
            try { database.SetEventStatus("closed"); RefreshPanels(); RefreshEventList(selectedEvent.EventId); }
            catch (Exception ex) { ShowError("No se pudo cerrar la recepción", ex); }
        }

        private void ToggleReceiver(object sender, EventArgs e)
        {
            if (receiver != null && receiver.IsRunning) StopReceiver();
            else TryStartReceiver(true);
            RefreshPanels();
        }

        private bool TryStartReceiver(bool showSuccess)
        {
            if (database == null) return false;
            if (receiver != null && receiver.IsRunning) return true;
            try
            {
                receiver = new LocalIngestReceiver(workspace, database, (int)receiverPort.Value);
                receiverPort.Enabled = false;
                if (showSuccess) footerStatus.Text = "Receptor activo en " + receiver.Endpoint + " (loopback solamente).";
                return true;
            }
            catch (Exception ex)
            {
                receiver = null;
                if (showSuccess) MessageBox.Show(this, "No se pudo iniciar el receptor en 127.0.0.1:" + receiverPort.Value + ".\r\n" + ex.Message,
                    "Receptor local", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void StopReceiver()
        {
            if (receiver != null) { receiver.Dispose(); receiver = null; }
            receiverPort.Enabled = true;
        }

        private void SetOfficialStart(object sender, EventArgs e)
        {
            if (database == null) { ShowSelectEvent(); return; }
            string distance = Dialogs.Choose(this, "Salida oficial", "Seleccione distancia", database.GetDistances());
            if (distance == null) return;
            DateTimeOffset time; string reason;
            if (!Dialogs.MarkTime(this, "Registrar salida oficial · " + distance, selectedEvent.TimeZone, out time, out reason)) return;
            if (!Dialogs.Confirm(this, "Se guardará una operación manual auditada de salida para cada participante de " + distance + ". ¿Continuar?", "Salida oficial")) return;
            try { database.SetOfficialStart(distance, time, reason); RefreshPanels(); }
            catch (Exception ex) { ShowError("No se pudo registrar la salida", ex); }
        }

        private void SetOfficialCategoryStart(object sender, EventArgs e)
        {
            if (database == null) { ShowSelectEvent(); return; }
            string distance = Dialogs.Choose(this, "Salida oficial por categoría", "Seleccione distancia", database.GetDistances());
            if (distance == null) return;
            IList<string> categories = database.GetCategories(distance);
            if (categories.Count == 0)
            {
                MessageBox.Show(this, "No hay categorías cargadas en " + distance + ".", "Salida oficial por categoría",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string category = Dialogs.Choose(this, "Salida oficial por categoría", "Seleccione categoría de " + distance, categories);
            if (category == null) return;
            DateTimeOffset time; string reason;
            if (!Dialogs.MarkTime(this, "Registrar salida oficial · " + category + " · " + distance, selectedEvent.TimeZone, out time, out reason)) return;
            if (!Dialogs.Confirm(this, "Se guardará una salida manual auditada para cada participante de la categoría " + category + " en " + distance + ". ¿Continuar?", "Salida oficial por categoría")) return;
            try { database.SetOfficialStartForCategory(distance, category, time, reason); RefreshPanels(); }
            catch (Exception ex) { ShowError("No se pudo registrar la salida por categoría", ex); }
        }

        private void CorrectMark(object sender, EventArgs e)
        {
            if (database == null || resultsGrid.SelectedRows.Count == 0) { MessageBox.Show(this, "Seleccione un participante en Resultados y podios."); return; }
            DataGridViewRow row = resultsGrid.SelectedRows[0];
            string participantId = Convert.ToString(row.Cells["participant_id"].Value);
            string bib = Convert.ToString(row.Cells["bib"].Value);
            string stage = Dialogs.Choose(this, "Corregir marca", "Etapa que se corregirá para dorsal " + bib, new[] { "start", "finish" });
            if (stage == null) return;
            DateTimeOffset time; string reason;
            if (!Dialogs.MarkTime(this, "Corregir " + stage + " · dorsal " + bib, selectedEvent.TimeZone, out time, out reason)) return;
            try { database.SetManualMark(participantId, stage, time, reason); RefreshPanels(); }
            catch (Exception ex) { ShowError("No se pudo corregir la marca", ex); }
        }

        private void ToggleDnf(object sender, EventArgs e)
        {
            if (database == null || rosterGrid.SelectedRows.Count == 0) { MessageBox.Show(this, "Seleccione un participante en la nómina."); return; }
            DataGridViewRow row = rosterGrid.SelectedRows[0];
            string id = Convert.ToString(row.Cells["participant_id"].Value);
            string bib = Convert.ToString(row.Cells["bib"].Value);
            bool isDnf = row.Cells["dnf_at"].Value != DBNull.Value && row.Cells["dnf_at"].Value != null;
            string action = isDnf ? "quitar DNF" : "marcar DNF";
            string reason = Dialogs.Ask("Auditar " + action, "Motivo", "Corrección del operador");
            if (String.IsNullOrWhiteSpace(reason)) return;
            try { database.SetDnf(id, !isDnf, reason); footerStatus.Text = "Estado deportivo actualizado para dorsal " + bib + "."; RefreshPanels(); }
            catch (Exception ex) { ShowError("No se pudo actualizar el estado", ex); }
        }

        private void ClaimAuthority(object sender, EventArgs e)
        {
            if (database == null) return;
            string prompt = selectedEvent.HasAuthority
                ? "La credencial actual del lector dejará de servir. Se generará otra y el evento volverá a borrador hasta habilitar recepción. ¿Continuar?"
                : "Esta copia restaurada pasará a ser la autoridad local. Se generará una credencial nueva y el evento volverá a borrador hasta habilitar recepción. ¿Tomar autoridad?";
            if (!Dialogs.Confirm(this, prompt, selectedEvent.HasAuthority ? "Rotar credencial" : "Autoridad local")) return;
            try
            {
                LocalDevice device = database.RotateDeviceCredential();
                selectedEvent = database.GetSummary();
                RefreshPanels(); RefreshEventList(selectedEvent.EventId);
                MessageBox.Show(this, "Se renovó la credencial del lector para RFIDTiming. Elija RFIDLocal en “Origen de eventos”, pulse “Listar eventos” y seleccione “" +
                    selectedEvent.Name + "”; el endpoint, el evento y el lector se completan desde esta app.\r\n\r\nCredencial: " + device.Credential +
                    "\r\n\r\nLa credencial se muestra solo esta vez.",
                    "Autoridad local preparada", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError("No se pudo tomar autoridad local", ex); }
        }

        private void ReprocessUnknownChip(object sender, EventArgs e)
        {
            if (database == null || receiptsGrid.SelectedRows.Count == 0) return;
            DataGridViewRow row = receiptsGrid.SelectedRows[0];
            string status = Convert.ToString(row.Cells["status"].Value);
            if (status != "unknown_chip")
            {
                MessageBox.Show(this, "La recuperación local por reintento solo aplica a recibos unknown_chip. Los recibos bloqueados por cierre requieren conciliación deportiva explícita.", "Recibo no reprocesable", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string deviceId = Convert.ToString(row.Cells["device_id"].Value);
            string readingId = Convert.ToString(row.Cells["reading_id"].Value);
            string reason = Dialogs.Ask("Reprocesar lectura original", "Motivo de autorización", "Nómina corregida y revisada");
            if (String.IsNullOrWhiteSpace(reason)) return;
            if (!Dialogs.Confirm(this, "Se volverá a evaluar el payload guardado con el mismo device_id/reading_id. No se generará una lectura nueva. ¿Continuar?", "Confirmar reprocesamiento")) return;
            try
            {
                string result = database.ReprocessUnknownChip(deviceId, readingId, reason);
                footerStatus.Text = "Recibo original reprocesado: " + result + ".";
                appliedCalculationRevision = null;
                RefreshPanels();
            }
            catch (Exception ex) { ShowError("No se pudo reprocesar el recibo", ex); }
        }

        private void CreateBackup(object sender, EventArgs e)
        {
            if (database == null) { ShowSelectEvent(); return; }
            string safeName = new string(selectedEvent.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
            using (var dialog = new SaveFileDialog { Title = "Guardar respaldo de evento", Filter = "Respaldo RFID local|*.yrbackup", FileName = safeName + ".yrbackup", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string output = new BackupService().Create(database, selectedEvent, dialog.FileName);
                    footerStatus.Text = "Respaldo verificado guardado en " + output;
                    MessageBox.Show(this, "Respaldo consistente creado. El paquete no incluye la credencial legible del lector.\r\n" + output, "Respaldo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { ShowError("No se pudo respaldar el evento", ex); }
            }
        }

        private void ImportPreparation(object sender, EventArgs e)
        {
            try
            {
                OperatorSession session = EnsureOperatorSession();
                if (session == null) return;
                IList<PlatformEvent> events = operatorPlatformClient.ListAvailableEvents(session);
                PlatformEvent selected = Dialogs.ChoosePlatformEvent(this, events);
                if (selected == null) return;
                if (workspace.ListEvents().Any(item => String.Equals(item.EventId, selected.Id, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Ese evento ya existe en el espacio local. La actualización de preparaciones existentes requiere conciliación y no sobrescribirá la base.");
                string package = operatorPlatformClient.DownloadPreparation(session, selected.Id);
                if (database != null) { StopReceiver(); database.Dispose(); database = null; selectedEvent = null; }
                LocalEvent imported = workspace.ImportPreparationJson(package);
                RefreshEventList(imported.EventId);
                footerStatus.Text = "Preparación descargada directamente desde " + selected.Name + ".";
                MessageBox.Show(this,
                    "Preparación remota importada con " + imported.ParticipantCount + " participantes. Quedó en consulta; toma autoridad local y configura un lector local antes de operar. La sesión renovable queda protegida por Windows.",
                    "Preparación remota", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError("No se pudo preparar el evento remoto", ex); RefreshEventList(selectedEvent == null ? null : selectedEvent.EventId); }
        }

        private void LoginToPlatform(object sender, EventArgs e)
        {
            try
            {
                OperatorSession session = PromptOperatorLogin();
                if (session != null) footerStatus.Text = "Sesión de operador iniciada para " + session.OperatorEmail + ".";
            }
            catch (Exception ex) { ShowError("No se pudo iniciar sesión", ex); }
        }

        private void SyncDirectly(object sender, EventArgs e)
        {
            if (database == null || selectedEvent == null) { ShowSelectEvent(); return; }
            if (!selectedEvent.HasAuthority)
            {
                MessageBox.Show(this, "Toma autoridad local antes de sincronizar una revisión.", "Evento en consulta", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (selectedEvent.Status == "closed" && !Dialogs.Confirm(this,
                "La revisión se publicará como final y el evento quedará cerrado en la plataforma. ¿Continuar?", "Sincronización final")) return;
            try
            {
                OperatorSession session = EnsureOperatorSession();
                if (session == null) return;
                string package = database.BuildEventTransferPackage();
                string receipt = operatorPlatformClient.Publish(session, package, selectedEvent.RemoteEventId, selectedEvent.Status == "closed");
                database.ApplyPublicationReceipt(receipt);
                RefreshPanels(); RefreshEventList(selectedEvent.EventId);
                footerStatus.Text = "Sincronización directa confirmada por la plataforma.";
                MessageBox.Show(this, "La plataforma validó y recibió la revisión local. El recibo quedó registrado en esta base; no se generaron archivos de transferencia.",
                    "Sincronización completada", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError("No se pudo sincronizar con la plataforma", ex); }
        }

        private OperatorSession EnsureOperatorSession()
        {
            OperatorSession session = operatorSessionStore.Load();
            if (session != null)
            {
                try { return operatorPlatformClient.EnsureFresh(session); }
                catch { operatorSessionStore.Clear(); }
            }
            return PromptOperatorLogin();
        }

        private OperatorSession PromptOperatorLogin()
        {
            OperatorSession previous = operatorSessionStore.Load();
            string platformUrl, email, password;
            if (!Dialogs.OperatorLogin(this, previous == null ? "" : previous.PlatformUrl,
                previous == null ? "" : previous.OperatorEmail, out platformUrl, out email, out password)) return null;
            return operatorPlatformClient.Login(platformUrl, email, password);
        }

        private void CreatePublicationPackage(object sender, EventArgs e)
        {
            if (database == null || selectedEvent == null) { ShowSelectEvent(); return; }
            if (!selectedEvent.HasAuthority)
            {
                MessageBox.Show(this, "La copia está en consulta. Toma autoridad local antes de crear una revisión para publicar.", "Evento en consulta", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                string package = database.BuildEventTransferPackage();
                string safeName = new string(selectedEvent.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
                Dictionary<string, object> envelope = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = D07TransferPackage.MaximumBytes }
                    .Deserialize<Dictionary<string, object>>(package);
                Dictionary<string, object> manifest = D07TransferPackage.Object(D07TransferPackage.Get(envelope, "manifest"), "manifest");
                string fileName = safeName + "-revision-" + D07TransferPackage.Integer(manifest, "revision") + ".yrtransfer";
                using (var dialog = new SaveFileDialog { Title = "Guardar revisión durable para publicación web", Filter = "Paquete de transferencia D07|*.yrtransfer|JSON|*.json", FileName = fileName })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        File.WriteAllText(dialog.FileName, package, new UTF8Encoding(false));
                        footerStatus.Text = "Paquete guardado. Súbelo en administración web para previsualizarlo y confirmar publicación.";
                        MessageBox.Show(this,
                            "La revisión quedó guardada también en el outbox local. En administración web carga este archivo, revisa la previsualización y confirma publicación. Después descarga el recibo .yrreceipt para marcar esta revisión como confirmada aquí.",
                            "Revisión lista", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else footerStatus.Text = "Revisión conservada en el outbox local; puedes exportarla más tarde.";
                }
            }
            catch (Exception ex) { ShowError("No se pudo preparar la revisión", ex); }
        }

        private void ImportPublicationReceipt(object sender, EventArgs e)
        {
            if (database == null || selectedEvent == null) { ShowSelectEvent(); return; }
            using (var dialog = new OpenFileDialog { Title = "Cargar recibo descargado desde administración web", Filter = "Recibo D07|*.yrreceipt;*.json|Todos los archivos|*.*" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    database.ApplyPublicationReceipt(File.ReadAllText(dialog.FileName, Encoding.UTF8));
                    RefreshPanels(); RefreshEventList(selectedEvent.EventId);
                    footerStatus.Text = "Recibo remoto asociado a la misma transferencia y huella del outbox.";
                }
                catch (Exception ex) { ShowError("No se pudo validar el recibo web", ex); }
            }
        }

        private void RestoreBackup(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Title = "Restaurar respaldo de evento", Filter = "Respaldo RFID local|*.yrbackup", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (database != null) { StopReceiver(); database.Dispose(); database = null; selectedEvent = null; }
                try
                {
                    LocalEvent restored = new BackupService().Restore(workspace, dialog.FileName);
                    RefreshEventList(restored.EventId);
                    footerStatus.Text = "Respaldo restaurado en consulta. Tome autoridad local antes de habilitar recepción.";
                }
                catch (Exception ex) { ShowError("No se pudo restaurar el respaldo", ex); RefreshEventList(null); }
            }
        }

        private void ExportResults(object sender, EventArgs e)
        {
            if (!(resultsGrid.DataSource is DataTable) || ((DataTable)resultsGrid.DataSource).Rows.Count == 0)
            { MessageBox.Show(this, "Calcule resultados antes de exportar."); return; }
            using (var dialog = new SaveFileDialog { Title = "Exportar resultados", Filter = "CSV|*.csv", FileName = (selectedEvent == null ? "resultados" : selectedEvent.Name + "-resultados") + ".csv" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    DataTable table = (DataTable)resultsGrid.DataSource;
                    using (var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true)))
                    {
                        writer.WriteLine(String.Join(";", table.Columns.Cast<DataColumn>().Where(column => column.ColumnName != "participant_id").Select(column => Csv(column.ColumnName))));
                        foreach (DataRow row in table.Rows)
                            writer.WriteLine(String.Join(";", table.Columns.Cast<DataColumn>().Where(column => column.ColumnName != "participant_id").Select(column => Csv(Convert.ToString(row[column], CultureInfo.CurrentCulture)))));
                    }
                    footerStatus.Text = "Exportación generada. Revisa la política de privacidad antes de compartirla.";
                }
                catch (Exception ex) { ShowError("No se pudo exportar el CSV", ex); }
            }
        }

        private void RefreshPanels()
        {
            if (database == null || selectedEvent == null) { UpdateActions(); return; }
            try
            {
                selectedEvent = database.GetSummary();
                string activeDevice = database.GetActiveDeviceId();
                string endpoint = receiver != null && receiver.IsRunning ? receiver.Endpoint : "receptor detenido";
                summary.Text = selectedEvent.Name + " · " + selectedEvent.EventDate.ToString("yyyy-MM-dd") + " · " + selectedEvent.Status.ToUpperInvariant() +
                    " · " + selectedEvent.ParticipantCount + " participantes · autoridad " + (selectedEvent.HasAuthority ? "local" : "pendiente") +
                    " · " + selectedEvent.Source + " · zona " + selectedEvent.TimeZone + " · revisión web " +
                    (selectedEvent.LastRemoteRevision == 0 ? "sin confirmar" : selectedEvent.LastRemoteRevision.ToString(CultureInfo.InvariantCulture) + " · " + selectedEvent.PublicationState) + "\r\n" +
                    "Evento " + selectedEvent.EventId + " · lector " + (activeDevice ?? "sin lector") + " · " + endpoint;
                var counts = database.GetReceiptCounts();
                long accepted = Count(counts, "accepted"), ignored = Count(counts, "later_than_current") + Count(counts, "manual_override");
                long blocked = Count(counts, "event_closed"), rejected = counts.Where(pair => pair.Key != "accepted" && pair.Key != "later_than_current" && pair.Key != "manual_override" && pair.Key != "event_closed").Sum(pair => pair.Value);
                operationSummary.Text = "Estado de recepción: " + selectedEvent.Status + "\r\n" +
                    "Receptor: " + endpoint + "\r\nDispositivo activo: " + (activeDevice ?? "ninguno") + "\r\n" +
                    "Recibos durablemente confirmados: " + selectedEvent.ReceiptCount + "\r\nAceptadas: " + accepted + " · ignoradas por política: " + ignored + " · bloqueadas: " + blocked + " · rechazadas: " + rejected + "\r\n" +
                    "Salud local: sin dependencia de Internet. La cola que aún espera en el lector se consulta en RFIDTiming.";
                receiverButton.Text = receiver != null && receiver.IsRunning ? "Detener receptor" : "Iniciar receptor";
                var page = FindPage("Incidentes y recibos");
                if (page != null && page.Controls["incidentHeader"] is Label)
                    ((Label)page.Controls["incidentHeader"]).Text = "Confirmadas: " + selectedEvent.ReceiptCount + " · aceptadas: " + accepted + " · ignoradas: " + ignored + " · bloqueadas: " + blocked + " · rechazadas: " + rejected + " (lista hasta 1000 recibos recientes).";

                string revision = database.GetRevisionKey();
                string rosterRevision = revision.Split(':')[0];
                if (displayedRosterRevision != rosterRevision)
                {
                    rosterTable = ToTable(database.GetParticipants(), "participant_id", "bib", "name", "chip", "distance", "sex", "category", "age_bracket", "dnf_at", "source_row", "updated_at");
                    PopulateRosterDistanceFilter();
                    displayedRosterRevision = rosterRevision;
                }
                string selectedReading = receiptsGrid.SelectedRows.Count == 0 ? null : Convert.ToString(receiptsGrid.SelectedRows[0].Cells["reading_id"].Value);
                receiptsGrid.DataSource = ToTable(database.GetReceipts(), "received_at", "device_id", "reading_id", "status", "reason", "message");
                if (receiptsGrid.Columns.Contains("device_id")) receiptsGrid.Columns["device_id"].Visible = false;
                if (!String.IsNullOrWhiteSpace(selectedReading) && receiptsGrid.Columns.Contains("reading_id"))
                    foreach (DataGridViewRow row in receiptsGrid.Rows)
                        if (String.Equals(Convert.ToString(row.Cells["reading_id"].Value), selectedReading, StringComparison.OrdinalIgnoreCase)) { row.Selected = true; break; }
                auditGrid.DataSource = ToTable(database.GetAudit(), "created_at", "action", "entity_id", "details_json");
                if (auditGrid.Columns.Contains("entity_id")) auditGrid.Columns["entity_id"].Visible = false;
                if (appliedCalculationRevision != revision && Volatile.Read(ref calculationPending) == 0) RequestCalculation(false);
                UpdateActions();
            }
            catch (Exception ex)
            {
                footerStatus.Text = "No se pudo actualizar la vista local: " + ex.Message;
            }
        }

        private void PopulateRosterDistanceFilter()
        {
            string previousDistance = (rosterDistanceFilter.SelectedItem as DistanceFilterChoice)?.Value;
            loadingRosterFilters = true;
            rosterDistanceFilter.Items.Clear();
            rosterDistanceFilter.Items.Add(new DistanceFilterChoice("Todas las distancias", ""));
            if (rosterTable != null && rosterTable.Columns.Contains("distance"))
            {
                IEnumerable<string> distances = rosterTable.Rows.Cast<DataRow>()
                    .Select(row => Convert.ToString(row["distance"]).Trim())
                    .Where(value => !String.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
                foreach (string distance in distances)
                    rosterDistanceFilter.Items.Add(new DistanceFilterChoice(distance, distance));
            }
            int selectedIndex = 0;
            if (!String.IsNullOrWhiteSpace(previousDistance))
                for (int index = 1; index < rosterDistanceFilter.Items.Count; index++)
                    if (String.Equals(((DistanceFilterChoice)rosterDistanceFilter.Items[index]).Value,
                        previousDistance, StringComparison.OrdinalIgnoreCase)) { selectedIndex = index; break; }
            if (rosterDistanceFilter.Items.Count > 0) rosterDistanceFilter.SelectedIndex = selectedIndex;
            loadingRosterFilters = false;
            ApplyRosterFilters();
        }

        private void ApplyRosterFilters()
        {
            if (loadingRosterFilters) return;
            if (rosterTable == null)
            {
                rosterGrid.DataSource = null;
                rosterCountLabel.Text = "";
                return;
            }

            DistanceFilterChoice distanceChoice = rosterDistanceFilter.SelectedItem as DistanceFilterChoice;
            string selectedDistance = distanceChoice == null ? "" : distanceChoice.Value;
            string bibFilter = rosterBibFilter.Text.Trim();
            DataTable visibleRows = rosterTable.Clone();
            foreach (DataRow row in rosterTable.Rows)
            {
                string distance = Convert.ToString(row["distance"]).Trim();
                string bib = Convert.ToString(row["bib"]);
                if (!String.IsNullOrWhiteSpace(selectedDistance) &&
                    !String.Equals(distance, selectedDistance, StringComparison.OrdinalIgnoreCase)) continue;
                if (!String.IsNullOrWhiteSpace(bibFilter) &&
                    (bib ?? "").IndexOf(bibFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                visibleRows.ImportRow(row);
            }
            rosterGrid.DataSource = visibleRows;
            if (rosterGrid.Columns.Contains("participant_id")) rosterGrid.Columns["participant_id"].Visible = false;
            rosterCountLabel.Text = "Mostrando " + visibleRows.Rows.Count + " de " + rosterTable.Rows.Count;
        }

        private void ClearRosterFilters(object sender, EventArgs eventArgs)
        {
            loadingRosterFilters = true;
            if (rosterDistanceFilter.Items.Count > 0) rosterDistanceFilter.SelectedIndex = 0;
            rosterBibFilter.Clear();
            loadingRosterFilters = false;
            ApplyRosterFilters();
        }

        private void ResetRosterFilters()
        {
            loadingRosterFilters = true;
            rosterDistanceFilter.Items.Clear();
            rosterBibFilter.Clear();
            rosterCountLabel.Text = "";
            loadingRosterFilters = false;
        }

        private void PopulateResultsCategoryFilter()
        {
            CategoryFilterChoice previous = resultsCategoryFilter.SelectedItem as CategoryFilterChoice;
            loadingResultsFilters = true;
            resultsCategoryFilter.Items.Clear();
            resultsCategoryFilter.Items.Add(new CategoryFilterChoice("Todas las categorías", "", true, false));
            if (resultsTable != null && resultsTable.Columns.Contains("categoria"))
            {
                List<string> categories = resultsTable.Rows.Cast<DataRow>()
                    .Select(row => Convert.ToString(row["categoria"]).Trim())
                    .Where(value => !String.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string category in categories)
                    resultsCategoryFilter.Items.Add(new CategoryFilterChoice(category, category, false, false));
                if (resultsTable.Rows.Cast<DataRow>().Any(row => String.IsNullOrWhiteSpace(Convert.ToString(row["categoria"]))))
                    resultsCategoryFilter.Items.Add(new CategoryFilterChoice("(Sin categoría)", "", false, true));
            }
            int selectedIndex = 0;
            if (previous != null && !previous.IsAll)
                for (int index = 1; index < resultsCategoryFilter.Items.Count; index++)
                {
                    CategoryFilterChoice choice = (CategoryFilterChoice)resultsCategoryFilter.Items[index];
                    if (choice.IsUncategorized == previous.IsUncategorized &&
                        String.Equals(choice.Value, previous.Value, StringComparison.OrdinalIgnoreCase))
                    { selectedIndex = index; break; }
                }
            if (resultsCategoryFilter.Items.Count > 0) resultsCategoryFilter.SelectedIndex = selectedIndex;
            loadingResultsFilters = false;
            ApplyResultsFilters();
        }

        private void ApplyResultsFilters()
        {
            if (loadingResultsFilters) return;
            if (resultsTable == null)
            {
                resultsGrid.DataSource = null;
                resultsCountLabel.Text = "";
                return;
            }

            CategoryFilterChoice categoryChoice = resultsCategoryFilter.SelectedItem as CategoryFilterChoice;
            string bibFilter = resultsBibFilter.Text.Trim();
            DataTable visibleRows = resultsTable.Clone();
            foreach (DataRow row in resultsTable.Rows)
            {
                string bib = Convert.ToString(row["dorsal"]);
                string category = Convert.ToString(row["categoria"]).Trim();
                if (!String.IsNullOrWhiteSpace(bibFilter) &&
                    (bib ?? "").IndexOf(bibFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (categoryChoice != null && !categoryChoice.IsAll &&
                    (categoryChoice.IsUncategorized
                        ? !String.IsNullOrWhiteSpace(category)
                        : !String.Equals(category, categoryChoice.Value, StringComparison.OrdinalIgnoreCase))) continue;
                visibleRows.ImportRow(row);
            }
            resultsGrid.DataSource = visibleRows;
            if (resultsGrid.Columns.Contains("participant_id")) resultsGrid.Columns["participant_id"].Visible = false;
            resultsCountLabel.Text = "Mostrando " + visibleRows.Rows.Count + " de " + resultsTable.Rows.Count;
        }

        private void ClearResultsFilters(object sender, EventArgs eventArgs)
        {
            loadingResultsFilters = true;
            if (resultsCategoryFilter.Items.Count > 0) resultsCategoryFilter.SelectedIndex = 0;
            resultsBibFilter.Clear();
            loadingResultsFilters = false;
            ApplyResultsFilters();
        }

        private void ResetResultsFilters()
        {
            loadingResultsFilters = true;
            resultsCategoryFilter.Items.Clear();
            resultsBibFilter.Clear();
            resultsCountLabel.Text = "";
            loadingResultsFilters = false;
        }

        private TabPage FindPage(string title)
        {
            foreach (Control control in Controls)
            {
                var layout = control as TableLayoutPanel;
                if (layout == null) continue;
                foreach (Control child in layout.Controls)
                {
                    var split = child as SplitContainer;
                    if (split == null) continue;
                    var tabs = split.Panel2.Controls.OfType<TabControl>().FirstOrDefault();
                    return tabs == null ? null : tabs.TabPages.Cast<TabPage>().FirstOrDefault(page => page.Text == title);
                }
            }
            return null;
        }

        private void RequestCalculation(bool force)
        {
            if (database == null || selectedEvent == null) return;
            string requestedRevision;
            try { requestedRevision = database.GetRevisionKey(); }
            catch { return; }
            if (!force && appliedCalculationRevision == requestedRevision) return;
            if (Interlocked.CompareExchange(ref calculationPending, 1, 0) != 0) return;
            calculationFinished.Reset();
            LocalRaceDatabase capturedDatabase = database;
            string eventId = selectedEvent.EventId;
            Task.Run(() => capturedDatabase.CalculateResults()).ContinueWith(task =>
            {
                calculationFinished.Set();
                if (closing || IsDisposed || !IsHandleCreated) { Interlocked.Exchange(ref calculationPending, 0); return; }
                BeginInvoke((Action)(() =>
                {
                    Interlocked.Exchange(ref calculationPending, 0);
                    if (database != capturedDatabase || selectedEvent == null || selectedEvent.EventId != eventId) return;
                    if (task.IsFaulted)
                    {
                        footerStatus.Text = "No se actualizaron resultados: " + task.Exception.GetBaseException().Message;
                        return;
                    }
                    string currentRevision = database.GetRevisionKey();
                    string snapshotRevision = task.Result.RosterRevision.Substring("roster-".Length) + ":" + task.Result.TimingRevision.Substring("timing-".Length);
                    if (snapshotRevision == currentRevision)
                    {
                        resultsTable = ResultsTable(task.Result, selectedEvent.TimeZone, database);
                        PopulateResultsCategoryFilter();
                        appliedCalculationRevision = currentRevision;
                        footerStatus.Text = "Resultados actualizados · " + task.Result.EngineVersion + " · nómina " + task.Result.RosterRevision + " · marcas " + task.Result.TimingRevision;
                    }
                    else RequestCalculation(false);
                }));
            });
        }

        private static DataTable ResultsTable(ResultSnapshot snapshot, string eventTimeZone, LocalRaceDatabase database)
        {
            var table = new DataTable();
            string[] columns = { "participant_id", "dorsal", "nombre", "chip", "distancia", "sexo", "categoria", "estado", "salida", "llegada", "tiempo", "puesto_general", "puesto_categoria", "premio_general", "motivo_premio_general", "premio_categoria", "motivo_premio_categoria", "exclusion_categoria", "advertencias" };
            foreach (string column in columns) table.Columns.Add(column, typeof(object));
            TimeZoneInfo zone = LocalWorkspace.ResolveTimeZone(eventTimeZone);
            var names = database.GetParticipants().ToDictionary(row => Convert.ToString(row["participant_id"]),
                row => Convert.ToString(row["name"]), StringComparer.Ordinal);
            foreach (ParticipantResult result in snapshot.Results)
            {
                DataRow row = table.NewRow();
                row["participant_id"] = result.ParticipantId;
                string participantName;
                names.TryGetValue(result.ParticipantId, out participantName);
                row["dorsal"] = result.Bib ?? ""; row["nombre"] = participantName ?? "";
                row["chip"] = result.Chip ?? ""; row["distancia"] = result.Distance ?? ""; row["sexo"] = result.Sex ?? ""; row["categoria"] = result.Category ?? "";
                row["estado"] = result.StatusLabel; row["salida"] = FormatMark(result.Start, zone); row["llegada"] = FormatMark(result.Finish, zone);
                row["tiempo"] = result.Elapsed.HasValue ? FormatDuration(result.Elapsed.Value) : "";
                row["puesto_general"] = result.OverallPlace.HasValue ? (object)result.OverallPlace.Value : "";
                row["puesto_categoria"] = result.CategoryPlace.HasValue ? (object)result.CategoryPlace.Value : "";
                row["premio_general"] = AwardLabel(result.GeneralAward.Status); row["motivo_premio_general"] = result.GeneralAward.Reason;
                row["premio_categoria"] = AwardLabel(result.CategoryAward.Status); row["motivo_premio_categoria"] = result.CategoryAward.Reason;
                row["exclusion_categoria"] = result.CategoryExclusionReason == "event_policy_general_top3_age_bracket" ? "Excluido del subpodio por política del evento (puesto general ≤ 3)." : result.CategoryExclusionReason ?? "";
                row["advertencias"] = String.Join(", ", result.Warnings);
                table.Rows.Add(row);
            }
            return table;
        }

        private static string FormatMark(SelectedTimingMark mark, TimeZoneInfo zone)
        {
            return mark == null ? "" : TimeZoneInfo.ConvertTime(mark.CapturedAt, zone).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + (mark.IsManual ? " · manual" : "");
        }

        private static string AwardLabel(AwardDecisionStatus status)
        {
            switch (status)
            {
                case AwardDecisionStatus.NotConfigured: return "SIN CONFIGURAR";
                case AwardDecisionStatus.NotAwarded: return "SIN PREMIO";
                case AwardDecisionStatus.Awarded: return "PREMIADO";
                case AwardDecisionStatus.ReviewRequired: return "REVISAR EMPATE";
                default: return status.ToString();
            }
        }

        private static string FormatDuration(TimeSpan value)
        {
            string sign = value < TimeSpan.Zero ? "-" : "";
            value = value.Duration();
            return sign + ((int)value.TotalHours).ToString("00", CultureInfo.InvariantCulture) + value.ToString(@"\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        }

        private static long Count(IDictionary<string, long> values, string key)
        {
            long value; return values.TryGetValue(key, out value) ? value : 0;
        }

        private static string Csv(string value)
        {
            string text = value ?? "";
            if (text.Length > 0 && (text[0] == '=' || text[0] == '+' || text[0] == '-' || text[0] == '@' || text[0] == '\t' || text[0] == '\r'))
                text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private void UpdateActions()
        {
            bool hasEvent = database != null && selectedEvent != null;
            bool authority = hasEvent && selectedEvent.HasAuthority;
            enableButton.Enabled = hasEvent && authority && selectedEvent.Status != "open";
            closeButton.Enabled = hasEvent && authority && selectedEvent.Status == "open";
            receiverButton.Enabled = hasEvent;
            claimButton.Text = authority ? "Rotar credencial del lector" : "Tomar autoridad local";
            claimButton.Enabled = hasEvent;
            archiveEventMenuItem.Enabled = hasEvent && selectedEvent.Status != "open";
            restoreArchivedEventMenuItem.Enabled = true;
            publishButton.Enabled = hasEvent && authority;
            syncButton.Enabled = hasEvent && authority;
            receiptButton.Enabled = hasEvent;
            toggleDnfMenuItem.Enabled = hasEvent && authority && rosterGrid.SelectedRows.Count > 0;
            officialStartMenuItem.Enabled = hasEvent && authority;
            officialCategoryStartMenuItem.Enabled = hasEvent && authority;
            correctMarkMenuItem.Enabled = hasEvent && authority && resultsGrid.SelectedRows.Count > 0;
            recalculateMenuItem.Enabled = hasEvent;
            exportResultsMenuItem.Enabled = hasEvent;
            importPreparationMenuItem.Enabled = true;
            operatorLoginMenuItem.Enabled = true;
            reprocessButton.Enabled = hasEvent && authority && selectedEvent.Status == "open" && receiptsGrid.SelectedRows.Count > 0 &&
                Convert.ToString(receiptsGrid.SelectedRows[0].Cells["status"].Value) == "unknown_chip";
            receiverPort.Enabled = receiver == null;
        }

        private void ClosingApplication(object sender, FormClosingEventArgs e)
        {
            refreshTimer.Stop();
            if (receiver != null && receiver.IsRunning &&
                !Dialogs.Confirm(this, "El receptor está habilitado. Confirme que la captura RFID está detenida antes de cerrar esta app. Las lecturas en la cola del lector permanecerán en su outbox durable. ¿Cerrar?", "Receptor local activo"))
            { e.Cancel = true; refreshTimer.Start(); return; }
            closing = true;
            calculationFinished.Wait(10000);
            StopReceiver();
            if (database != null) database.Dispose();
        }

        private void ShowSelectEvent()
        {
            MessageBox.Show(this, "Seleccione o cree un evento local primero.", "Evento requerido", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowError(string title, Exception ex)
        {
            MessageBox.Show(this, title + ":\r\n" + ex.Message, "RFID Local Race", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
