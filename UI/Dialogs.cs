using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RFIDLocal.Domain;
using RFIDLocal.Services;
using RFIDTiming.Domain.Results;

namespace RFIDLocal.UI
{
    internal static class Dialogs
    {
        private sealed class RosterColumnChoice
        {
            public int Index;
            public string Label;
            public override string ToString() { return Label; }
        }

        public static string Ask(string title, string prompt, string initialValue)
        {
            using (var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(500, 135) })
            using (var label = new Label { Text = prompt, AutoSize = false, Left = 12, Top = 12, Width = 470, Height = 38 })
            using (var input = new TextBox { Left = 12, Top = 54, Width = 470, Text = initialValue ?? "" })
            using (var ok = new Button { Text = "Aceptar", DialogResult = DialogResult.OK, Left = 300, Top = 94, Width = 86 })
            using (var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Left = 396, Top = 94, Width = 86 })
            {
                form.Controls.AddRange(new Control[] { label, input, ok, cancel });
                form.AcceptButton = ok; form.CancelButton = cancel;
                return form.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
            }
        }

        public static bool Confirm(IWin32Window owner, string message, string title)
        {
            return MessageBox.Show(owner, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        public static string Choose(IWin32Window owner, string title, string prompt, IEnumerable<string> values)
        {
            using (var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(420, 130) })
            using (var label = new Label { Text = prompt, Left = 12, Top = 12, Width = 390, Height = 24 })
            using (var list = new ComboBox { Left = 12, Top = 42, Width = 390, DropDownStyle = ComboBoxStyle.DropDownList })
            using (var ok = new Button { Text = "Aceptar", DialogResult = DialogResult.OK, Left = 220, Top = 84, Width = 86 })
            using (var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Left = 316, Top = 84, Width = 86 })
            {
                list.Items.AddRange((values ?? Enumerable.Empty<string>()).Cast<object>().ToArray());
                if (list.Items.Count > 0) list.SelectedIndex = 0;
                form.Controls.AddRange(new Control[] { label, list, ok, cancel });
                form.AcceptButton = ok; form.CancelButton = cancel;
                return form.ShowDialog(owner) == DialogResult.OK ? Convert.ToString(list.SelectedItem) : null;
            }
        }

        public static bool OperatorLogin(IWin32Window owner, string initialUrl, string initialEmail,
            out string platformUrl, out string email, out string password)
        {
            using (var form = new Form { Text = "Iniciar sesión de operador", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(540, 265) })
            using (var urlLabel = new Label { Text = "URL de la plataforma Next.js", Left = 12, Top = 12, Width = 490 })
            using (var url = new TextBox { Left = 12, Top = 34, Width = 510, Text = initialUrl ?? "" })
            using (var emailLabel = new Label { Text = "Email del operador", Left = 12, Top = 70, Width = 220 })
            using (var emailBox = new TextBox { Left = 12, Top = 92, Width = 300, Text = initialEmail ?? "" })
            using (var passwordLabel = new Label { Text = "Contraseña", Left = 12, Top = 128, Width = 180 })
            using (var passwordBox = new TextBox { Left = 12, Top = 150, Width = 300, UseSystemPasswordChar = true })
            using (var hint = new Label { Text = "La app guarda solo la sesión renovable, protegida por Windows. No almacena la contraseña.", Left = 12, Top = 187, Width = 510, Height = 32, ForeColor = Color.DimGray })
            using (var ok = new Button { Text = "Iniciar sesión", DialogResult = DialogResult.OK, Left = 330, Top = 222, Width = 105 })
            using (var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Left = 440, Top = 222, Width = 82 })
            {
                form.Controls.AddRange(new Control[] { urlLabel, url, emailLabel, emailBox, passwordLabel, passwordBox, hint, ok, cancel });
                form.AcceptButton = ok; form.CancelButton = cancel;
                if (form.ShowDialog(owner) != DialogResult.OK)
                { platformUrl = email = password = null; return false; }
                platformUrl = url.Text.Trim(); email = emailBox.Text.Trim(); password = passwordBox.Text;
                return !String.IsNullOrWhiteSpace(platformUrl) && !String.IsNullOrWhiteSpace(email) && !String.IsNullOrEmpty(password);
            }
        }

        public static PlatformEvent ChoosePlatformEvent(IWin32Window owner, IList<PlatformEvent> events)
        {
            using (var form = new Form { Text = "Elegir evento de la plataforma", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(540, 145) })
            using (var label = new Label { Text = "Eventos abiertos a preparación o captura (los cerrados y cancelados no se muestran).", Left = 12, Top = 12, Width = 510, Height = 30 })
            using (var list = new ComboBox { Left = 12, Top = 48, Width = 510, DropDownStyle = ComboBoxStyle.DropDownList })
            using (var ok = new Button { Text = "Seleccionar", DialogResult = DialogResult.OK, Left = 320, Top = 96, Width = 95 })
            using (var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Left = 425, Top = 96, Width = 97 })
            {
                foreach (PlatformEvent item in events ?? new List<PlatformEvent>()) list.Items.Add(item);
                if (list.Items.Count > 0) list.SelectedIndex = 0;
                form.Controls.AddRange(new Control[] { label, list, ok, cancel });
                form.AcceptButton = ok; form.CancelButton = cancel;
                ok.Enabled = list.Items.Count > 0;
                return form.ShowDialog(owner) == DialogResult.OK ? list.SelectedItem as PlatformEvent : null;
            }
        }

        public static LocalEvent ChooseArchivedEvent(IWin32Window owner, IList<LocalEvent> events)
        {
            using (var form = new Form { Text = "Restaurar evento archivado", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(560, 160) })
            using (var label = new Label { Text = "Elige el evento que volverá a la lista de eventos locales.", Left = 12, Top = 12, Width = 530, Height = 26 })
            using (var list = new ComboBox { Left = 12, Top = 46, Width = 530, DropDownStyle = ComboBoxStyle.DropDownList })
            using (var ok = new Button { Text = "Restaurar", DialogResult = DialogResult.OK, Left = 340, Top = 105, Width = 95 })
            using (var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Left = 445, Top = 105, Width = 97 })
            {
                foreach (LocalEvent item in events ?? new List<LocalEvent>()) list.Items.Add(item);
                if (list.Items.Count > 0) list.SelectedIndex = 0;
                form.Controls.AddRange(new Control[] { label, list, ok, cancel });
                form.AcceptButton = ok; form.CancelButton = cancel;
                ok.Enabled = list.Items.Count > 0;
                return form.ShowDialog(owner) == DialogResult.OK ? list.SelectedItem as LocalEvent : null;
            }
        }

        public static bool CreateEvent(IWin32Window owner, out string name, out DateTime date, out string timeZone, out IList<string> distances)
        {
            using (var form = new Form { Text = "Crear evento local", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(500, 275) })
            using (var nameLabel = new Label { Text = "Nombre del evento", Left = 12, Top = 12, Width = 160 })
            using (var nameBox = new TextBox { Left = 12, Top = 34, Width = 470 })
            using (var dateLabel = new Label { Text = "Fecha", Left = 12, Top = 70, Width = 100 })
            using (var dateBox = new DateTimePicker { Left = 12, Top = 92, Width = 220, Format = DateTimePickerFormat.Short })
            using (var zoneLabel = new Label { Text = "Zona horaria del evento", Left = 250, Top = 70, Width = 220 })
            using (var zoneBox = new ComboBox { Left = 250, Top = 92, Width = 232, DropDownStyle = ComboBoxStyle.DropDownList })
            using (var distanceLabel = new Label { Text = "Distancias (separadas por coma)", Left = 12, Top = 132, Width = 280 })
            using (var distanceBox = new TextBox { Left = 12, Top = 154, Width = 470, Text = "10KM, 21KM" })
            using (var hint = new Label { Text = "Se guarda como borrador. La recepción se habilita después de cargar la nómina.", Left = 12, Top = 188, Width = 470, Height = 34, ForeColor = Color.DimGray })
            using (var ok = new Button { Text = "Crear", DialogResult = DialogResult.OK, Left = 300, Top = 230, Width = 86 })
            using (var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Left = 396, Top = 230, Width = 86 })
            {
                zoneBox.Items.Add("America/Santiago"); zoneBox.Items.Add(TimeZoneInfo.Local.Id); zoneBox.SelectedIndex = 0;
                form.Controls.AddRange(new Control[] { nameLabel, nameBox, dateLabel, dateBox, zoneLabel, zoneBox, distanceLabel, distanceBox, hint, ok, cancel });
                form.AcceptButton = ok; form.CancelButton = cancel;
                if (form.ShowDialog(owner) != DialogResult.OK) { name = null; date = DateTime.MinValue; timeZone = null; distances = null; return false; }
                name = nameBox.Text.Trim(); date = dateBox.Value.Date; timeZone = Convert.ToString(zoneBox.SelectedItem);
                distances = distanceBox.Text.Split(',').Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
                return true;
            }
        }

        public static bool ConfigureDistancePolicies(IWin32Window owner, IList<string> distances,
            out IDictionary<string, bool> exclusionPolicies)
        {
            exclusionPolicies = null;
            var unique = (distances ?? new List<string>()).Select(display => new
            {
                Display = (display ?? "").Trim(),
                Key = ResultDistanceKey.Normalize(display)
            }).Where(item => item.Key != null).GroupBy(item => item.Key, StringComparer.Ordinal)
                .Select(group => group.First()).ToList();
            if (unique.Count == 0) return false;
            using (var form = new Form { Text = "Política de podios por distancia", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(650, 430) })
            using (var hint = new Label { Dock = DockStyle.Top, Height = 72, Padding = new Padding(12),
                Text = "Elige explícitamente si los tres primeros de la general por sexo también quedan fuera del subpodio etario. Cada categoría etaria conserva tres puestos." })
            using (var grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect })
            using (var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) })
            using (var apply = new Button { Text = "Confirmar políticas", Width = 140, Height = 30 })
            using (var cancel = new Button { Text = "Cancelar", Width = 90, Height = 30, DialogResult = DialogResult.Cancel })
            {
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "distance", HeaderText = "Distancia", ReadOnly = true });
                var choice = new DataGridViewComboBoxColumn { Name = "exclude", HeaderText = "Exclusión de top 3 general", FlatStyle = FlatStyle.Flat };
                choice.Items.AddRange("Elegir…", "No excluir", "Excluir top 3");
                grid.Columns.Add(choice);
                foreach (var item in unique) grid.Rows.Add(item.Display, "Elegir…");
                apply.Click += delegate
                {
                    if (grid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells[1].Value == null || Convert.ToString(row.Cells[1].Value) == "Elegir…"))
                    {
                        MessageBox.Show(form, "Selecciona Sí o No para cada distancia.", "Política incompleta", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                };
                buttons.Controls.Add(cancel); buttons.Controls.Add(apply);
                form.Controls.Add(grid); form.Controls.Add(buttons); form.Controls.Add(hint);
                form.CancelButton = cancel;
                if (form.ShowDialog(owner) != DialogResult.OK) return false;
                exclusionPolicies = new Dictionary<string, bool>(StringComparer.Ordinal);
                for (int index = 0; index < unique.Count; index++)
                    exclusionPolicies[unique[index].Key] = Convert.ToString(grid.Rows[index].Cells[1].Value) == "Excluir top 3";
                return true;
            }
        }

        public static bool MarkTime(IWin32Window owner, string title, string eventTimeZone, out DateTimeOffset capturedAt, out string reason)
        {
            using (var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(490, 180) })
            using (var label = new Label { Text = "Instante local de la marca", Left = 12, Top = 12, Width = 250 })
            using (var time = new DateTimePicker { Left = 12, Top = 36, Width = 460, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss.fff", ShowUpDown = true })
            using (var reasonLabel = new Label { Text = "Motivo obligatorio para auditoría", Left = 12, Top = 74, Width = 260 })
            using (var reasonBox = new TextBox { Left = 12, Top = 96, Width = 460 })
            using (var ok = new Button { Text = "Guardar marca", DialogResult = DialogResult.OK, Left = 280, Top = 136, Width = 100 })
            using (var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Left = 386, Top = 136, Width = 86 })
            {
                time.Value = DateTime.Now;
                form.Controls.AddRange(new Control[] { label, time, reasonLabel, reasonBox, ok, cancel });
                form.AcceptButton = ok; form.CancelButton = cancel;
                if (form.ShowDialog(owner) != DialogResult.OK || String.IsNullOrWhiteSpace(reasonBox.Text))
                { capturedAt = DateTimeOffset.MinValue; reason = null; return false; }
                TimeZoneInfo zone = LocalWorkspace.ResolveTimeZone(eventTimeZone);
                DateTime localEventTime = DateTime.SpecifyKind(time.Value, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(localEventTime))
                {
                    MessageBox.Show(form, "Ese horario no existe por el cambio de hora de la zona del evento.", "Hora no válida", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    capturedAt = DateTimeOffset.MinValue; reason = null; return false;
                }
                TimeSpan offset = zone.IsAmbiguousTime(localEventTime) ? zone.GetAmbiguousTimeOffsets(localEventTime).Max() : zone.GetUtcOffset(localEventTime);
                capturedAt = new DateTimeOffset(localEventTime, offset);
                reason = reasonBox.Text.Trim();
                return true;
            }
        }

        public static bool PreviewRoster(IWin32Window owner, RosterImportPreview preview)
        {
            using (var form = new Form { Text = "Revisar nómina", StartPosition = FormStartPosition.CenterParent, Width = 1000, Height = 650, MinimizeBox = false, MaximizeBox = true })
            using (var summary = new Label { Dock = DockStyle.Top, Height = 52, Padding = new Padding(10), Text = "Filas: " + preview.Rows.Count + " · nuevas: " + preview.Added + " · actualizar: " + preview.Updated + " · sin cambios: " + preview.Unchanged + " · distancias desconocidas: " + preview.UnknownDistance })
            using (var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, DataSource = new BindingList<RosterRow>(preview.Rows.ToList()), AllowUserToAddRows = false, AllowUserToDeleteRows = false })
            using (var errors = new ListBox { Dock = DockStyle.Bottom, Height = preview.Errors.Count == 0 ? 0 : 92, DataSource = preview.Errors.ToList() })
            using (var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) })
            using (var apply = new Button { Text = "Aplicar importación", Width = 140, Height = 30, DialogResult = preview.Errors.Count == 0 ? DialogResult.OK : DialogResult.None, Enabled = preview.Errors.Count == 0 })
            using (var cancel = new Button { Text = "Cancelar", Width = 90, Height = 30, DialogResult = DialogResult.Cancel })
            {
                buttons.Controls.Add(cancel); buttons.Controls.Add(apply);
                form.Controls.Add(grid); form.Controls.Add(errors); form.Controls.Add(buttons); form.Controls.Add(summary);
                form.AcceptButton = apply; form.CancelButton = cancel;
                return form.ShowDialog(owner) == DialogResult.OK;
            }
        }

        public static bool MapRosterColumns(IWin32Window owner, IList<string> headers, IList<string> sampleValues, bool distanceCanBeDefault,
            out RosterColumnMapping mapping)
        {
            mapping = null;
            if (headers == null || headers.Count == 0) return false;
            RosterColumnMapping suggested = RosterFileReader.SuggestColumnMapping(headers);
            var fields = new[]
            {
                new { Key = "ParticipantId", Label = "ID estable del participante" },
                new { Key = "Bib", Label = "Dorsal" },
                new { Key = "FirstName", Label = "Nombre o nombre completo" },
                new { Key = "LastName", Label = "Apellidos" },
                new { Key = "Chip", Label = "Chip / RFID" },
                new { Key = "Distance", Label = "Distancia" },
                new { Key = "Sex", Label = "Sexo" },
                new { Key = "Category", Label = "Categoría" },
                new { Key = "AgeBracket", Label = "Categoría por edad (cat.)" },
                new { Key = "Age", Label = "Edad" },
                new { Key = "Team", Label = "Equipo o club" }
            };
            RosterColumnMapping result = null;
            using (var form = new Form { Text = "Relacionar columnas de la nómina", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable, MinimizeBox = false, MaximizeBox = true, ClientSize = new Size(640, 530), MinimumSize = new Size(560, 430) })
            using (var hint = new Label { Dock = DockStyle.Top, Height = 55, Padding = new Padding(12),
                Text = "Revisa las sugerencias. Pulsa «Quitar» para desasignar un dato; las columnas no asignadas se conservarán en el origen de cada participante." })
            using (var table = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12), ColumnCount = 3 })
            using (var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) })
            using (var apply = new Button { Text = "Continuar", Width = 100, Height = 30 })
            using (var cancel = new Button { Text = "Cancelar", Width = 90, Height = 30, DialogResult = DialogResult.Cancel })
            {
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 13));
                var editors = new Dictionary<string, ComboBox>();
                foreach (var field in fields)
                {
                    int rowIndex = table.RowCount++;
                    table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                    table.Controls.Add(new Label { Text = field.Label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, rowIndex);
                    var choice = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false };
                    choice.Items.Add(new RosterColumnChoice { Index = -1, Label = "— Sin asignar —" });
                    for (int column = 0; column < headers.Count; column++)
                    {
                        string sample = sampleValues != null && column < sampleValues.Count ? (sampleValues[column] ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim() : "";
                        if (sample.Length > 24) sample = sample.Substring(0, 21) + "…";
                        choice.Items.Add(new RosterColumnChoice { Index = column, Label = ExcelColumnName(column) + " · " +
                            (String.IsNullOrWhiteSpace(headers[column]) ? "(sin título)" : headers[column].Trim()) +
                            (sample.Length == 0 ? "" : "   [" + sample + "]") });
                    }
                    int suggestedIndex = GetMappingColumn(suggested, field.Key);
                    for (int item = 0; item < choice.Items.Count; item++)
                        if (((RosterColumnChoice)choice.Items[item]).Index == suggestedIndex) { choice.SelectedIndex = item; break; }
                    editors[field.Key] = choice;
                    table.Controls.Add(choice, 1, rowIndex);
                    var remove = new Button { Text = "Quitar", Dock = DockStyle.Fill, Height = 26, Margin = new Padding(2) };
                    remove.Click += delegate { choice.SelectedIndex = 0; };
                    table.Controls.Add(remove, 2, rowIndex);
                }
                apply.Click += delegate
                {
                    var selected = new List<int>();
                    foreach (ComboBox editor in editors.Values)
                    {
                        RosterColumnChoice value = editor.SelectedItem as RosterColumnChoice;
                        if (value != null && value.Index >= 0) selected.Add(value.Index);
                    }
                    if (selected.Count != selected.Distinct().Count())
                    {
                        MessageBox.Show(form, "Cada columna del archivo se puede asignar a un solo dato.", "Revisar asignaciones", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    RosterColumnChoice nameChoice = editors["FirstName"].SelectedItem as RosterColumnChoice;
                    if (nameChoice == null || nameChoice.Index < 0)
                    {
                        MessageBox.Show(form, "Asigna una columna para el nombre del participante.", "Falta el nombre", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    RosterColumnChoice distanceChoice = editors["Distance"].SelectedItem as RosterColumnChoice;
                    if (!distanceCanBeDefault && (distanceChoice == null || distanceChoice.Index < 0))
                    {
                        MessageBox.Show(form, "Asigna la columna de distancia para las hojas Excel.", "Falta la distancia", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    result = new RosterColumnMapping();
                    foreach (KeyValuePair<string, ComboBox> editor in editors)
                    {
                        RosterColumnChoice value = editor.Value.SelectedItem as RosterColumnChoice;
                        SetMappingColumn(result, editor.Key, value == null ? -1 : value.Index);
                    }
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                };
                buttons.Controls.Add(cancel); buttons.Controls.Add(apply);
                form.Controls.Add(table); form.Controls.Add(buttons); form.Controls.Add(hint);
                form.CancelButton = cancel;
                bool accepted = form.ShowDialog(owner) == DialogResult.OK;
                mapping = accepted ? result : null;
                return accepted;
            }
        }

        private static int GetMappingColumn(RosterColumnMapping mapping, string field)
        {
            switch (field)
            {
                case "ParticipantId": return mapping.ParticipantId;
                case "Bib": return mapping.Bib;
                case "FirstName": return mapping.FirstName;
                case "LastName": return mapping.LastName;
                case "Chip": return mapping.Chip;
                case "Distance": return mapping.Distance;
                case "Sex": return mapping.Sex;
                case "Category": return mapping.Category;
                case "AgeBracket": return mapping.AgeBracket;
                case "Age": return mapping.Age;
                case "Team": return mapping.Team;
                default: return -1;
            }
        }

        private static void SetMappingColumn(RosterColumnMapping mapping, string field, int column)
        {
            switch (field)
            {
                case "ParticipantId": mapping.ParticipantId = column; break;
                case "Bib": mapping.Bib = column; break;
                case "FirstName": mapping.FirstName = column; break;
                case "LastName": mapping.LastName = column; break;
                case "Chip": mapping.Chip = column; break;
                case "Distance": mapping.Distance = column; break;
                case "Sex": mapping.Sex = column; break;
                case "Category": mapping.Category = column; break;
                case "AgeBracket": mapping.AgeBracket = column; break;
                case "Age": mapping.Age = column; break;
                case "Team": mapping.Team = column; break;
            }
        }

        private static string ExcelColumnName(int index)
        {
            int value = index + 1;
            string result = "";
            while (value > 0)
            {
                value--;
                result = (char)('A' + value % 26) + result;
                value /= 26;
            }
            return result;
        }
    }
}
