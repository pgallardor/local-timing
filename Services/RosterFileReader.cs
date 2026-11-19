using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using RFIDLocal.Domain;
using RFIDTiming.Domain;
using RFIDTiming.Domain.Results;

namespace RFIDLocal.Services
{
    public static class RosterFileReader
    {
        private sealed class SheetMatrix
        {
            public string Name;
            public IList<IList<string>> Rows;
        }
        private const int MaxInputBytes = 20 * 1024 * 1024;
        private const long MaxExpandedBytes = 50L * 1024 * 1024;
        private static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static RosterImportPreview Read(string path)
        {
            return Read(path, null);
        }

        public static RosterImportPreview Read(string path, string csvDistance)
        {
            return Read(path, csvDistance, null);
        }

        public static IList<string> ReadHeaders(string path)
        {
            IList<string> ignored;
            return ReadHeaders(path, out ignored);
        }

        public static IList<string> ReadHeaders(string path, out IList<string> sampleValues)
        {
            sampleValues = new List<string>();
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Seleccione un archivo de nómina.", "path");
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("No se encontró el archivo de nómina.", path);
            if (file.Length > MaxInputBytes) throw new InvalidDataException("El archivo supera el límite de 20 MB.");
            IList<IList<string>> rows;
            if (String.Equals(file.Extension, ".csv", StringComparison.OrdinalIgnoreCase)) rows = ReadCsv(path);
            else if (String.Equals(file.Extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                IList<SheetMatrix> sheets = ReadXlsx(path);
                if (sheets.Count == 0 || sheets.Count > 30) throw new InvalidDataException("El XLSX debe tener entre 1 y 30 hojas.");
                string referenceHeaders = null;
                foreach (SheetMatrix sheet in sheets)
                {
                    string signature = String.Join("|", (sheet.Rows.FirstOrDefault() ?? new List<string>()).Select(NormalizeHeader));
                    if (referenceHeaders != null && !String.Equals(signature, referenceHeaders, StringComparison.Ordinal))
                        throw new InvalidDataException("La hoja " + sheet.Name + " no tiene los mismos encabezados y orden que la primera hoja.");
                    referenceHeaders = signature;
                }
                rows = sheets[0].Rows;
            }
            else throw new InvalidDataException("Use un archivo CSV o XLSX.");
            if (rows.Count == 0) throw new InvalidDataException("El archivo no contiene encabezados.");
            if (rows.Count > 1) sampleValues = rows[1].Select(value => value ?? "").ToList();
            return rows[0].Select(value => value ?? "").ToList();
        }

        public static RosterColumnMapping SuggestColumnMapping(IList<string> headers)
        {
            IList<string> normalized = (headers ?? new List<string>()).Select(NormalizeHeader).ToList();
            var mapping = new RosterColumnMapping();
            mapping.ParticipantId = Header(normalized, "participantid", "idparticipante", "participanteid", "idcorredor", "participant", "id");
            mapping.Bib = Header(normalized, "bib", "dorsal", "bibnumber", "numero", "numerodorsal", "numeropecho", "bibno");
            mapping.FirstName = Header(normalized, "firstname", "nombres", "nombre", "name", "participante", "fullname", "nombrecompleto");
            mapping.LastName = Header(normalized, "lastname", "surname", "apellido", "apellidos");
            mapping.Chip = Header(normalized, "chip", "chips", "epc", "tag", "rfid", "identificador", "chipid", "tagid");
            mapping.Distance = Header(normalized, "distance", "distancia", "race", "prueba");
            mapping.Sex = Header(normalized, "sex", "sexo", "gender", "genero");
            mapping.Category = Header(normalized, "category", "categoria", "division");
            mapping.AgeBracket = Header(normalized, "agebracket", "bracket", "bracketedad", "rangodeedad", "rangoedad", "grupoedad", "categoriaedad", "catdeedad", "categoriaetaria", "cat", "grupoetario");
            mapping.Age = Header(normalized, "age", "edad", "edadnumero", "edadanios", "ageyears");
            mapping.Team = Header(normalized, "team", "equipo", "club");
            return mapping;
        }

        public static RosterImportPreview Read(string path, string csvDistance, RosterColumnMapping mapping)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Seleccione un archivo de nómina.", "path");
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("No se encontró el archivo de nómina.", path);
            if (file.Length > MaxInputBytes) throw new InvalidDataException("El archivo supera el límite de 20 MB.");
            if (String.Equals(file.Extension, ".csv", StringComparison.OrdinalIgnoreCase))
            {
                IList<IList<string>> rows = ReadCsv(path);
                return ParseRows(rows, csvDistance, null, mapping ?? SuggestColumnMapping(rows.FirstOrDefault()));
            }
            if (!String.Equals(file.Extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Use un archivo CSV o XLSX.");
            IList<SheetMatrix> sheets = ReadXlsx(path);
            if (sheets.Count == 0 || sheets.Count > 30) throw new InvalidDataException("El XLSX debe tener entre 1 y 30 hojas.");
            string referenceHeaders = null;
            foreach (SheetMatrix sheet in sheets)
            {
                string signature = String.Join("|", (sheet.Rows.FirstOrDefault() ?? new List<string>()).Select(NormalizeHeader));
                if (referenceHeaders != null && !String.Equals(signature, referenceHeaders, StringComparison.Ordinal))
                    throw new InvalidDataException("La hoja " + sheet.Name + " no tiene los mismos encabezados y orden que la primera hoja.");
                referenceHeaders = signature;
            }
            var combined = new RosterImportPreview { Rows = new List<RosterRow>(), Errors = new List<string>() };
            foreach (SheetMatrix sheet in sheets)
            {
                RosterImportPreview parsed = ParseRows(sheet.Rows, null, sheet.Name, mapping ?? SuggestColumnMapping(sheet.Rows.FirstOrDefault()));
                foreach (RosterRow row in parsed.Rows) combined.Rows.Add(row);
                foreach (string message in parsed.Errors) combined.Errors.Add("Hoja " + sheet.Name + " · " + message);
            }
            var allBibs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allChips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RosterRow row in combined.Rows)
            {
                if (!String.IsNullOrWhiteSpace(row.ParticipantId) && !allIds.Add(row.ParticipantId))
                    combined.Errors.Add("Hoja " + row.SourceSheet + " · fila " + row.SourceRow + ": participant_id repetido en otra hoja " + row.ParticipantId + ".");
                if (!String.IsNullOrWhiteSpace(row.Bib) && !allBibs.Add(row.Bib))
                    combined.Errors.Add("Hoja " + row.SourceSheet + " · fila " + row.SourceRow + ": dorsal repetido en otra hoja " + row.Bib + ".");
                if (!String.IsNullOrWhiteSpace(row.Chip) && !allChips.Add(row.Chip))
                    combined.Errors.Add("Hoja " + row.SourceSheet + " · fila " + row.SourceRow + ": chip repetido en otra hoja " + row.Chip + ".");
            }
            if (combined.Rows.Count > 5000) throw new InvalidDataException("El libro supera el límite inicial de 5000 participantes.");
            return combined;
        }

        private static IList<IList<string>> ReadCsv(string path)
        {
            string content;
            try { content = File.ReadAllText(path, new UTF8Encoding(false, true)); }
            catch (DecoderFallbackException) { content = File.ReadAllText(path, Encoding.Default); }
            if (content.Length > 0 && content[0] == '\ufeff') content = content.Substring(1);
            string firstLine = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).FirstOrDefault() ?? "";
            int commas = CountOutsideQuotes(firstLine, ',');
            int semicolons = CountOutsideQuotes(firstLine, ';');
            int tabs = CountOutsideQuotes(firstLine, '\t');
            char delimiter = tabs > commas && tabs > semicolons ? '\t' : semicolons > commas ? ';' : ',';
            return ParseDelimited(content, delimiter);
        }

        private static int CountOutsideQuotes(string line, char target)
        {
            bool quoted = false;
            int count = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') i++;
                    else quoted = !quoted;
                }
                else if (!quoted && line[i] == target) count++;
            }
            return count;
        }

        private static IList<IList<string>> ParseDelimited(string text, char delimiter)
        {
            var rows = new List<IList<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else field.Append(c);
                }
                else if (c == '"' && field.Length == 0) quoted = true;
                else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
                else if (c == '\r' || c == '\n')
                {
                    row.Add(field.ToString()); field.Clear();
                    if (row.Any(value => !String.IsNullOrWhiteSpace(value))) rows.Add(row);
                    row = new List<string>();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
                else field.Append(c);
            }
            if (quoted) throw new InvalidDataException("El CSV termina dentro de un campo entre comillas.");
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                if (row.Any(value => !String.IsNullOrWhiteSpace(value))) rows.Add(row);
            }
            return rows;
        }

        private static IList<SheetMatrix> ReadXlsx(string path)
        {
            using (ZipArchive archive = ZipFile.OpenRead(path))
            {
                if (archive.Entries.Sum(entry => entry.Length) > MaxExpandedBytes)
                    throw new InvalidDataException("El XLSX supera el límite de contenido expandido.");
                XDocument workbook = LoadXml(archive, "xl/workbook.xml");
                XDocument relationships = LoadXml(archive, "xl/_rels/workbook.xml.rels");
                List<XElement> sheetElements = workbook.Root.Element(MainNs + "sheets").Elements(MainNs + "sheet").ToList();
                if (sheetElements.Count == 0 || sheetElements.Count > 30) throw new InvalidDataException("El libro debe tener entre 1 y 30 hojas.");
                List<string> shared = new List<string>();
                ZipArchiveEntry sharedEntry = archive.GetEntry("xl/sharedStrings.xml");
                if (sharedEntry != null)
                {
                    XDocument sharedDocument;
                    using (Stream stream = sharedEntry.Open()) sharedDocument = XDocument.Load(stream);
                    shared = sharedDocument.Root.Elements(MainNs + "si")
                        .Select(item => String.Concat(item.Descendants(MainNs + "t").Select(text => text.Value))).ToList();
                }
                Dictionary<int, string> styleFormats = ReadStyleFormats(archive);
                var result = new List<SheetMatrix>();
                int totalCells = 0;
                foreach (XElement sheetElement in sheetElements)
                {
                    string relationshipId = (string)sheetElement.Attribute(RelNs + "id");
                    XElement relation = relationships.Root.Elements(PackageRelNs + "Relationship")
                        .FirstOrDefault(item => (string)item.Attribute("Id") == relationshipId);
                    if (relation == null) throw new InvalidDataException("No se pudo resolver la hoja " + (string)sheetElement.Attribute("name") + ".");
                    string target = (string)relation.Attribute("Target");
                    string sheetPath = NormalizeZipPath(target.StartsWith("/", StringComparison.Ordinal)
                        ? target.TrimStart('/') : "xl/" + target.Replace("\\", "/"));
                    XDocument sheet = LoadXml(archive, sheetPath);
                    var rows = new List<IList<string>>();
                    foreach (XElement rowElement in sheet.Descendants(MainNs + "row"))
                    {
                        var row = new List<string>();
                        foreach (XElement cell in rowElement.Elements(MainNs + "c"))
                        {
                            totalCells++;
                            if (totalCells > 250000) throw new InvalidDataException("El XLSX supera el límite de 250 000 celdas.");
                            string reference = (string)cell.Attribute("r") ?? "A1";
                            int column = ColumnIndex(reference);
                            if (column > 99) throw new InvalidDataException("Una hoja supera 100 columnas.");
                            while (row.Count <= column) row.Add("");
                            row[column] = ReadCellText(cell, shared, styleFormats);
                        }
                        if (row.Any(value => !String.IsNullOrWhiteSpace(value))) rows.Add(row);
                        if (rows.Count > 6000) throw new InvalidDataException("Una hoja supera 6000 filas.");
                    }
                    result.Add(new SheetMatrix { Name = (string)sheetElement.Attribute("name") ?? "Hoja", Rows = rows });
                }
                return result;
            }
        }

        private static Dictionary<int, string> ReadStyleFormats(ZipArchive archive)
        {
            var result = new Dictionary<int, string>();
            ZipArchiveEntry entry = archive.GetEntry("xl/styles.xml");
            if (entry == null) return result;
            XDocument styles;
            using (Stream stream = entry.Open()) styles = XDocument.Load(stream);
            var custom = new Dictionary<int, string>();
            XElement formats = styles.Root.Element(MainNs + "numFmts");
            if (formats != null)
                foreach (XElement format in formats.Elements(MainNs + "numFmt"))
                {
                    int id; if (Int32.TryParse((string)format.Attribute("numFmtId"), out id)) custom[id] = (string)format.Attribute("formatCode") ?? "";
                }
            XElement xfs = styles.Root.Element(MainNs + "cellXfs");
            if (xfs == null) return result;
            int style = 0;
            foreach (XElement xf in xfs.Elements(MainNs + "xf"))
            {
                int formatId; Int32.TryParse((string)xf.Attribute("numFmtId"), out formatId);
                string code;
                if (custom.TryGetValue(formatId, out code)) result[style] = code;
                style++;
            }
            return result;
        }

        private static string ReadCellText(XElement cell, IList<string> shared, IDictionary<int, string> styleFormats)
        {
            string type = (string)cell.Attribute("t");
            XElement value = cell.Element(MainNs + "v");
            string text = value == null ? "" : value.Value;
            if (type == "s")
            {
                int index; return Int32.TryParse(text, out index) && index >= 0 && index < shared.Count ? shared[index] : "";
            }
            if (type == "inlineStr") return String.Concat(cell.Descendants(MainNs + "t").Select(item => item.Value));
            int style; string format;
            if ((type == null || type == "n") && Int32.TryParse((string)cell.Attribute("s"), out style) && styleFormats.TryGetValue(style, out format) &&
                Regex.IsMatch(format, "^0{2,}$"))
            {
                decimal number;
                if (Decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out number) &&
                    number >= 0 && number == Decimal.Truncate(number))
                    return number.ToString("D" + format.Length, System.Globalization.CultureInfo.InvariantCulture);
            }
            return text;
        }

        private static XDocument LoadXml(ZipArchive archive, string path)
        {
            ZipArchiveEntry entry = archive.GetEntry(path);
            if (entry == null) throw new InvalidDataException("Falta el componente XLSX " + path + ".");
            using (Stream stream = entry.Open()) return XDocument.Load(stream);
        }

        private static string NormalizeZipPath(string path)
        {
            var parts = new List<string>();
            foreach (string part in path.Split('/'))
            {
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (part.Length > 0 && part != ".") parts.Add(part);
            }
            return String.Join("/", parts);
        }

        private static int ColumnIndex(string reference)
        {
            int result = 0;
            foreach (char c in reference)
            {
                if (!Char.IsLetter(c)) break;
                result = result * 26 + (Char.ToUpperInvariant(c) - 'A' + 1);
            }
            return Math.Max(0, result - 1);
        }

        private static RosterImportPreview ParseRows(IList<IList<string>> matrix, string defaultDistance, string sourceSheet, RosterColumnMapping mapping)
        {
            var preview = new RosterImportPreview { Rows = new List<RosterRow>(), Errors = new List<string>() };
            if (matrix == null || matrix.Count < 2) { preview.Errors.Add("El archivo requiere encabezados y al menos una fila."); return preview; }
            IList<string> headers = matrix[0].Select(NormalizeHeader).ToList();
            if (mapping == null) mapping = SuggestColumnMapping(matrix[0]);
            int idColumn = mapping.ParticipantId;
            int bibColumn = mapping.Bib;
            int firstNameColumn = mapping.FirstName;
            int lastNameColumn = mapping.LastName;
            int chipColumn = mapping.Chip;
            int distanceColumn = mapping.Distance;
            int sexColumn = mapping.Sex;
            int categoryColumn = mapping.Category;
            int ageBracketColumn = mapping.AgeBracket;
            int ageColumn = mapping.Age;
            int teamColumn = mapping.Team;
            if (firstNameColumn < 0 || (distanceColumn < 0 && String.IsNullOrWhiteSpace(defaultDistance)))
            {
                preview.Errors.Add("Falta el nombre o la distancia. Usa columnas Nombre/First name y Distancia; para CSV elige una distancia y para XLSX nombra cada hoja con su distancia.");
                return preview;
            }
            var bibs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var participantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index < matrix.Count; index++)
            {
                IList<string> row = matrix[index];
                string firstName = Cell(row, firstNameColumn).Trim();
                string lastName = Cell(row, lastNameColumn).Trim();
                string name = String.Join(" ", new[] { firstName, lastName }.Where(value => value.Length > 0));
                string rowDistance = Cell(row, distanceColumn).Trim();
                string distance = NormalizeDistanceLabel(rowDistance.Length > 0 ? rowDistance : defaultDistance);
                if (name.Length == 0 || distance.Length == 0)
                {
                    preview.Errors.Add("Fila " + (index + 1) + ": falta nombre o distancia.");
                    continue;
                }
                string bib = Cell(row, bibColumn).Trim();
                string idText = Cell(row, idColumn).Trim();
                string externalId = ValidGuid(idText);
                if (idText.Length > 0 && externalId == null)
                {
                    preview.Errors.Add("Fila " + (index + 1) + ": participant_id no es un UUID válido.");
                    continue;
                }
                if (externalId != null && !participantIds.Add(externalId))
                {
                    preview.Errors.Add("Fila " + (index + 1) + ": participant_id duplicado " + externalId + ".");
                    continue;
                }
                if (bib.Length == 0 && externalId == null)
                {
                    preview.Errors.Add("Fila " + (index + 1) + ": falta dorsal o participant_id estable.");
                    continue;
                }
                string chip = ChipIdNormalizer.Normalize(Cell(row, chipColumn));
                if (name.Length > 100 || bib.Length > 50 || chip.Length > 128 || distance.Length > 32)
                {
                    preview.Errors.Add("Fila " + (index + 1) + ": nombre, dorsal, chip o distancia supera el límite admitido.");
                    continue;
                }
                if (bib.Length > 0 && !bibs.Add(bib)) preview.Errors.Add("Fila " + (index + 1) + ": dorsal duplicado " + bib + ".");
                if (chip.Length > 0 && !chips.Add(chip)) preview.Errors.Add("Fila " + (index + 1) + ": chip duplicado " + chip + ".");
                string sex = NormalizeSex(Cell(row, sexColumn));
                string ageBracketInput = Cell(row, ageBracketColumn).Trim();
                string ageBracket = NormalizeAgeBracket(ageBracketInput) ?? ageBracketInput;
                int? age = null;
                string ageText = Cell(row, ageColumn).Trim();
                int ageValue;
                if (ageText.Length > 0)
                {
                    if (!Int32.TryParse(ageText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ageValue) || ageValue < 0 || ageValue > 120)
                        preview.Errors.Add("Fila " + (index + 1) + ": la edad debe ser un entero entre 0 y 120.");
                    else age = ageValue;
                }
                var source = new Dictionary<string, string>();
                for (int column = 0; column < headers.Count; column++)
                {
                    string header = matrix[0].ElementAtOrDefault(column) ?? "columna_" + (column + 1);
                    source[header] = Cell(row, column);
                }
                preview.Rows.Add(new RosterRow
                {
                    ParticipantId = externalId, Bib = bib, Name = name, FirstName = firstName,
                    LastName = lastName.Length == 0 ? null : lastName, Chip = chip,
                    Distance = distance, Sex = sex,
                    Category = BuildCategory(Cell(row, categoryColumn).Trim(), sex, ageBracket, distance),
                    AgeBracket = ageBracket, Age = age, Team = Cell(row, teamColumn).Trim(),
                    SourceJson = new JavaScriptSerializer().Serialize(source), SourceRow = index + 1, SourceSheet = sourceSheet
                });
            }
            return preview;
        }

        private static string NormalizeDistanceLabel(string value)
        {
            string key = ResultDistanceKey.Normalize(value);
            return key ?? (value ?? "").Trim().ToUpperInvariant();
        }

        private static string BuildCategory(string category, string sex, string ageBracket, string distance)
        {
            string bracket = NormalizeAgeBracket(ageBracket) ?? NormalizeAgeBracket(category);
            string value = String.IsNullOrWhiteSpace(category) ? distance : category.Trim();
            value = Regex.Replace(value,
                @"[\s·|/]*(?:\d{1,2}\s*(?:a|al|hasta|[-–—])\s*\d{1,2}(?:\s*(?:años?|anos?))?|cat\.?\s*(?:extra|especial)|categor[ií]a\s+especial|especial)(?:\s*[·|/-]\s*sin\s+(?:derecho\s+a\s+)?premio)?\s*$",
                "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (sex == "F" || sex == "M")
                value = Regex.Replace(value,
                    @"(^|[^\p{L}\p{N}])(?:f|fem|femenin[oa]|mujer(?:es)?|female|woman|women|señora|niña|damas?|m|masc|masculin[oa]|h|hombres?|var[oó]n(?:es)?|male|man|men|señor|niño)(?=$|[^\p{L}\p{N}])",
                    "$1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            value = Regex.Replace(value, @"^[\s,;:|/·—–-]+|[\s,;:|/·—–-]+$", "");
            value = Regex.Replace(value, @"\s*[·|/]+\s*", " ");
            value = Regex.Replace(value, @"\s{2,}", " ").Trim();
            string gender = sex == "F" ? "Damas" : sex == "M" ? "Varones" : "";
            string podium = String.Join(" ", new[] { value, gender }.Where(part => !String.IsNullOrWhiteSpace(part)));
            return String.Join(" · ", new[] { podium, bracket }.Where(part => !String.IsNullOrWhiteSpace(part))).Trim();
        }

        private static string NormalizeAgeBracket(string value)
        {
            string cleaned = (value ?? "").Trim();
            if (cleaned.Length == 0) return null;
            string normalized = cleaned.Normalize(NormalizationForm.FormD);
            var compact = new StringBuilder();
            foreach (char character in normalized)
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                    compact.Append(Char.ToLowerInvariant(character));
            string key = Regex.Replace(compact.ToString(), "[^a-z0-9]", "");
            if (Regex.IsMatch(key, @"(?:cat(?:egoria)?extra|cat(?:egoria)?especial|especial|sinpremio)"))
                return "Categoría especial";
            Match range = Regex.Match(cleaned, @"(\d{1,2})\s*(?:a|al|hasta|[-–—])\s*(\d{1,2})(?:\s*(?:años?|anos?))?", RegexOptions.IgnoreCase);
            if (range.Success)
                return Int32.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture) + " a " +
                    Int32.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture) + " años";
            if (Regex.IsMatch(cleaned, @"^\d+$")) return null;
            if (Regex.IsMatch(cleaned, @"\b(?:años?|anos?)\b", RegexOptions.IgnoreCase)) return cleaned;
            return null;
        }

        private static string NormalizeHeader(string value)
        {
            string normalized = (value ?? "").Trim().ToLowerInvariant();
            normalized = normalized.Replace("á", "a").Replace("é", "e").Replace("í", "i").Replace("ó", "o").Replace("ú", "u").Replace("ñ", "n");
            return Regex.Replace(normalized, "[^a-z0-9]", "");
        }

        private static int Header(IList<string> headers, params string[] names)
        {
            foreach (string name in names)
                for (int i = 0; i < headers.Count; i++)
                    if (headers[i] == NormalizeHeader(name)) return i;
            return -1;
        }

        private static string NormalizeSex(string value)
        {
            return D07TransferPackage.NormalizeSex(value);
        }

        private static string ValidGuid(string value)
        {
            Guid parsed;
            return Guid.TryParse(value, out parsed) ? parsed.ToString("D") : null;
        }

        private static string Cell(IList<string> row, int index)
        {
            return index >= 0 && index < row.Count ? row[index] ?? "" : "";
        }
    }
}
