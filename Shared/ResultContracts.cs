using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace RFIDTiming.Domain.Results
{
    public enum ResultStatus
    {
        Dnf,
        SinChip,
        SinSalida,
        EnCarrera,
        Finalizo
    }

    public enum TimingStage
    {
        Start,
        Finish
    }

    public enum TimingOperationKind
    {
        RfidAccepted,
        ManualSet,
        ManualVoided
    }

    public enum AwardScope
    {
        General,
        Category
    }

    public enum AwardTieMode
    {
        IncludeAllTiedParticipants,
        ReviewAtBoundary,
        LimitByParticipantId
    }

    public enum AwardDecisionStatus
    {
        NotConfigured,
        NotAwarded,
        Awarded,
        ReviewRequired
    }

    public static class ResultDistanceKey
    {
        public static string Normalize(string distance)
        {
            if (distance == null) return null;

            string upper = distance.ToUpperInvariant();
            var compact = new StringBuilder(upper.Length);
            foreach (char character in upper)
            {
                if ((character >= 'A' && character <= 'Z') || (character >= '0' && character <= '9'))
                    compact.Append(character);
            }

            string value = compact.ToString();
            if (value == "21K" || value == "21KM") return "21KM";
            if (value == "10K" || value == "10KM") return "10KM";
            return value.Length == 0 ? null : value;
        }
    }

    public sealed class ResultParticipant
    {
        public ResultParticipant(string participantId, string bib, string chip, string distance,
            string sex, string category, string ageBracket, DateTimeOffset? dnfAt)
        {
            if (String.IsNullOrWhiteSpace(participantId))
                throw new ArgumentException("ParticipantId is required.", "participantId");

            ParticipantId = participantId;
            Bib = bib;
            Chip = chip;
            Distance = distance;
            Sex = sex;
            Category = category;
            AgeBracket = ageBracket;
            DnfAt = ToUtc(dnfAt);
        }

        public string ParticipantId { get; private set; }
        public string Bib { get; private set; }
        public string Chip { get; private set; }
        public string Distance { get; private set; }
        public string Sex { get; private set; }
        public string Category { get; private set; }
        public string AgeBracket { get; private set; }
        public DateTimeOffset? DnfAt { get; private set; }

        private static DateTimeOffset? ToUtc(DateTimeOffset? value)
        {
            return value.HasValue ? (DateTimeOffset?)value.Value.ToUniversalTime() : null;
        }
    }

    /// <summary>
    /// An accepted timing history operation. OperationOrder must reflect durable application order
    /// (for example, the audit identity); replaying the same OperationId is idempotent.
    /// </summary>
    public sealed class TimingOperation
    {
        public TimingOperation(string operationId, string participantId, TimingStage stage,
            TimingOperationKind kind, long operationOrder, DateTimeOffset? capturedAt)
        {
            if (String.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("OperationId is required.", "operationId");
            if (String.IsNullOrWhiteSpace(participantId))
                throw new ArgumentException("ParticipantId is required.", "participantId");
            if (operationOrder < 0)
                throw new ArgumentOutOfRangeException("operationOrder");
            if (kind == TimingOperationKind.ManualVoided && capturedAt.HasValue)
                throw new ArgumentException("A void operation cannot carry a timing value.", "capturedAt");
            if (kind != TimingOperationKind.ManualVoided && !capturedAt.HasValue)
                throw new ArgumentException("A timing operation requires CapturedAt.", "capturedAt");

            OperationId = operationId;
            ParticipantId = participantId;
            Stage = stage;
            Kind = kind;
            OperationOrder = operationOrder;
            CapturedAt = capturedAt.HasValue
                ? (DateTimeOffset?)capturedAt.Value.ToUniversalTime()
                : null;
        }

        public string OperationId { get; private set; }
        public string ParticipantId { get; private set; }
        public TimingStage Stage { get; private set; }
        public TimingOperationKind Kind { get; private set; }
        public long OperationOrder { get; private set; }
        public DateTimeOffset? CapturedAt { get; private set; }

        internal bool HasSamePayload(TimingOperation other)
        {
            return other != null && ParticipantId == other.ParticipantId && Stage == other.Stage &&
                Kind == other.Kind && OperationOrder == other.OperationOrder && CapturedAt == other.CapturedAt;
        }
    }

    public sealed class AwardRule
    {
        public AwardRule(AwardScope scope, string distanceKey, string sex, string category,
            int slots, AwardTieMode tieMode)
        {
            if (String.IsNullOrWhiteSpace(distanceKey))
                throw new ArgumentException("A canonical distance key is required.", "distanceKey");
            if (!String.Equals(ResultDistanceKey.Normalize(distanceKey), distanceKey, StringComparison.Ordinal))
                throw new ArgumentException("DistanceKey must use the canonical SQL result key.", "distanceKey");
            if (slots < 1)
                throw new ArgumentOutOfRangeException("slots", "Award slots must be positive.");
            if (scope != AwardScope.General && scope != AwardScope.Category)
                throw new ArgumentOutOfRangeException("scope");
            if (tieMode != AwardTieMode.IncludeAllTiedParticipants &&
                tieMode != AwardTieMode.ReviewAtBoundary && tieMode != AwardTieMode.LimitByParticipantId)
                throw new ArgumentOutOfRangeException("tieMode");
            if (scope == AwardScope.General && category != null)
                throw new ArgumentException("A general award rule cannot specify a category.", "category");
            if (scope == AwardScope.Category && category == null)
                throw new ArgumentException("A category award rule must specify a category.", "category");

            Scope = scope;
            DistanceKey = distanceKey;
            Sex = sex;
            Category = category;
            Slots = slots;
            TieMode = tieMode;
        }

        public AwardScope Scope { get; private set; }
        public string DistanceKey { get; private set; }
        public string Sex { get; private set; }
        public string Category { get; private set; }
        public int Slots { get; private set; }
        public AwardTieMode TieMode { get; private set; }
    }

    /// <summary>
    /// Versioned result semantics. The first supported preset freezes the current SQL behavior;
    /// event-specific award rules are separate data and do not change classification ranks.
    /// </summary>
    public sealed class ResultPolicy
    {
        public const string CompatActualV1 = "compat_actual_v1";

        private readonly ReadOnlyCollection<AwardRule> awardRules;

        private ResultPolicy(string presetId, string revision, IEnumerable<AwardRule> rules)
        {
            if (String.IsNullOrWhiteSpace(presetId))
                throw new ArgumentException("PresetId is required.", "presetId");
            if (String.IsNullOrWhiteSpace(revision))
                throw new ArgumentException("Policy revision is required.", "revision");

            PresetId = presetId;
            Revision = revision;
            List<AwardRule> copied = (rules ?? Enumerable.Empty<AwardRule>()).ToList();
            ValidateRules(copied);
            awardRules = new ReadOnlyCollection<AwardRule>(copied);
        }

        public string PresetId { get; private set; }
        public string Revision { get; private set; }
        public IList<AwardRule> AwardRules { get { return awardRules; } }

        public static ResultPolicy CreateCompatActualV1()
        {
            return new ResultPolicy(CompatActualV1, "1", null);
        }

        public ResultPolicy WithAwardRules(string revision, IEnumerable<AwardRule> rules)
        {
            return new ResultPolicy(PresetId, revision, rules);
        }

        private static void ValidateRules(IList<AwardRule> rules)
        {
            if (rules.Any(rule => rule == null))
                throw new ArgumentException("Award rules cannot contain null entries.", "rules");

            for (int i = 0; i < rules.Count; i++)
            {
                for (int j = i + 1; j < rules.Count; j++)
                {
                    AwardRule a = rules[i];
                    AwardRule b = rules[j];
                    if (a.Scope == b.Scope && a.DistanceKey == b.DistanceKey &&
                        a.Sex == b.Sex && a.Category == b.Category)
                        throw new ArgumentException("Award rules must have unique result groups.", "rules");
                }
            }
        }
    }

    public sealed class ResultCalculationInput
    {
        public ResultCalculationInput(string eventId, string rosterRevision, string timingRevision,
            IEnumerable<ResultParticipant> participants, IEnumerable<TimingOperation> timingOperations)
        {
            if (String.IsNullOrWhiteSpace(eventId))
                throw new ArgumentException("EventId is required.", "eventId");
            if (String.IsNullOrWhiteSpace(rosterRevision))
                throw new ArgumentException("RosterRevision is required.", "rosterRevision");
            if (String.IsNullOrWhiteSpace(timingRevision))
                throw new ArgumentException("TimingRevision is required.", "timingRevision");

            EventId = eventId;
            RosterRevision = rosterRevision;
            TimingRevision = timingRevision;
            Participants = new ReadOnlyCollection<ResultParticipant>(
                (participants ?? Enumerable.Empty<ResultParticipant>()).ToList());
            TimingOperations = new ReadOnlyCollection<TimingOperation>(
                (timingOperations ?? Enumerable.Empty<TimingOperation>()).ToList());
        }

        public string EventId { get; private set; }
        public string RosterRevision { get; private set; }
        public string TimingRevision { get; private set; }
        public IList<ResultParticipant> Participants { get; private set; }
        public IList<TimingOperation> TimingOperations { get; private set; }
    }

    public sealed class SelectedTimingMark
    {
        internal SelectedTimingMark(string operationId, TimingStage stage, TimingOperationKind kind,
            DateTimeOffset capturedAt)
        {
            OperationId = operationId;
            Stage = stage;
            Kind = kind;
            CapturedAt = capturedAt.ToUniversalTime();
        }

        public string OperationId { get; private set; }
        public TimingStage Stage { get; private set; }
        public TimingOperationKind Kind { get; private set; }
        public DateTimeOffset CapturedAt { get; private set; }
        public bool IsManual { get { return Kind == TimingOperationKind.ManualSet; } }
    }

    public sealed class AwardDecision
    {
        internal AwardDecision(AwardDecisionStatus status, string reason, int? slots)
        {
            Status = status;
            Reason = reason;
            Slots = slots;
        }

        public AwardDecisionStatus Status { get; private set; }
        public string Reason { get; private set; }
        public int? Slots { get; private set; }
    }

    public sealed class ParticipantResult
    {
        internal ParticipantResult(ResultParticipant participant, string distanceKey,
            SelectedTimingMark start, SelectedTimingMark finish, TimeSpan? elapsed, ResultStatus status)
        {
            ParticipantId = participant.ParticipantId;
            Bib = participant.Bib;
            Chip = participant.Chip;
            Distance = participant.Distance;
            DistanceKey = distanceKey;
            Sex = participant.Sex;
            Category = participant.Category;
            AgeBracket = participant.AgeBracket;
            Start = start;
            Finish = finish;
            Elapsed = elapsed;
            Status = status;
            GeneralAward = new AwardDecision(AwardDecisionStatus.NotConfigured, "general_award_slots_not_configured", null);
            CategoryAward = new AwardDecision(AwardDecisionStatus.NotConfigured, "category_award_slots_not_configured", null);
            Warnings = new List<string>();
        }

        public string ParticipantId { get; private set; }
        public string Bib { get; private set; }
        public string Chip { get; private set; }
        public string Distance { get; private set; }
        public string DistanceKey { get; private set; }
        public string Sex { get; private set; }
        public string Category { get; private set; }
        public string AgeBracket { get; private set; }
        public SelectedTimingMark Start { get; private set; }
        public SelectedTimingMark Finish { get; private set; }
        public TimeSpan? Elapsed { get; private set; }
        public ResultStatus Status { get; private set; }
        public long? OverallPlace { get; internal set; }
        public long? CategoryPlace { get; internal set; }
        public string CategoryExclusionReason { get; internal set; }
        public AwardDecision GeneralAward { get; internal set; }
        public AwardDecision CategoryAward { get; internal set; }
        public IList<string> Warnings { get; private set; }

        public string StatusLabel
        {
            get
            {
                switch (Status)
                {
                    case ResultStatus.Dnf: return "DNF";
                    case ResultStatus.SinChip: return "SIN CHIP";
                    case ResultStatus.SinSalida: return "SIN SALIDA";
                    case ResultStatus.EnCarrera: return "EN CARRERA";
                    default: return "FINALIZÓ";
                }
            }
        }
    }

    public sealed class ResultSnapshot
    {
        internal ResultSnapshot(string eventId, string rosterRevision, string timingRevision,
            string presetId, string policyRevision, string engineVersion, IEnumerable<ParticipantResult> results)
        {
            EventId = eventId;
            RosterRevision = rosterRevision;
            TimingRevision = timingRevision;
            PresetId = presetId;
            PolicyRevision = policyRevision;
            EngineVersion = engineVersion;
            Results = new ReadOnlyCollection<ParticipantResult>(results.ToList());
        }

        public string EventId { get; private set; }
        public string RosterRevision { get; private set; }
        public string TimingRevision { get; private set; }
        public string PresetId { get; private set; }
        public string PolicyRevision { get; private set; }
        public string EngineVersion { get; private set; }
        public IList<ParticipantResult> Results { get; private set; }
    }

    public sealed class PolicyChange
    {
        internal PolicyChange(string participantId, ParticipantResult before, ParticipantResult after)
        {
            ParticipantId = participantId;
            Before = before;
            After = after;
        }

        public string ParticipantId { get; private set; }
        public ParticipantResult Before { get; private set; }
        public ParticipantResult After { get; private set; }
    }

    public sealed class PolicyChangePreview
    {
        internal PolicyChangePreview(ResultSnapshot before, ResultSnapshot after, IEnumerable<PolicyChange> changes)
        {
            Before = before;
            After = after;
            Changes = new ReadOnlyCollection<PolicyChange>(changes.ToList());
        }

        public ResultSnapshot Before { get; private set; }
        public ResultSnapshot After { get; private set; }
        public IList<PolicyChange> Changes { get; private set; }
    }
}
