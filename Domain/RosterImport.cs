using System.Collections.Generic;

namespace RFIDLocal.Domain
{
    public sealed class RosterColumnMapping
    {
        public int ParticipantId { get; set; }
        public int Bib { get; set; }
        public int FirstName { get; set; }
        public int LastName { get; set; }
        public int Chip { get; set; }
        public int Distance { get; set; }
        public int Sex { get; set; }
        public int Category { get; set; }
        public int AgeBracket { get; set; }
        public int Age { get; set; }
        public int Team { get; set; }

        public RosterColumnMapping()
        {
            ParticipantId = Bib = FirstName = LastName = Chip = Distance = -1;
            Sex = Category = AgeBracket = Age = Team = -1;
        }
    }

    public sealed class RosterRow
    {
        public string ParticipantId { get; set; }
        public string Bib { get; set; }
        public string Name { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Chip { get; set; }
        public string Distance { get; set; }
        public string Sex { get; set; }
        public string Category { get; set; }
        public string AgeBracket { get; set; }
        public int? Age { get; set; }
        public string Team { get; set; }
        public string SourceJson { get; set; }
        public int SourceRow { get; set; }
        public string SourceSheet { get; set; }
    }

    public sealed class RosterImportPreview
    {
        public IList<RosterRow> Rows { get; set; }
        public IList<string> Errors { get; set; }
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public int MissingBib { get; set; }
        public int UnknownDistance { get; set; }
    }
}
