using System;
using System.Collections.Generic;
using System.Linq;
using RFIDTiming.Domain.Results;

namespace RFIDTiming.Services
{
    /// <summary>
    /// Deterministic, UI/network/reader-independent implementation of compat_actual_v1.
    /// Inputs are a versioned roster and the durable accepted timing-operation history.
    /// </summary>
    public sealed class ResultsEngine
    {
        public const string EngineVersion = "result_engine_v1";

        public bool SupportsPreset(string presetId)
        {
            return String.Equals(presetId, ResultPolicy.CompatActualV1, StringComparison.Ordinal);
        }

        public static string NormalizeDistanceKey(string distance)
        {
            return ResultDistanceKey.Normalize(distance);
        }

        public ResultSnapshot Calculate(ResultCalculationInput input, ResultPolicy policy)
        {
            return Calculate(input, policy, new[] { "10KM", "21KM" });
        }

        public ResultSnapshot Calculate(ResultCalculationInput input, ResultPolicy policy,
            IEnumerable<string> distancesExcludingGeneralTop3)
        {
            Validate(input, policy);
            var exclusionDistances = new HashSet<string>((distancesExcludingGeneralTop3 ?? Enumerable.Empty<string>())
                .Select(ResultDistanceKey.Normalize).Where(key => key != null), StringComparer.Ordinal);

            Dictionary<string, ResultParticipant> participants = input.Participants
                .ToDictionary(participant => participant.ParticipantId, StringComparer.Ordinal);
            Dictionary<string, SelectedTimingMark[]> selected = SelectMarks(input, participants);
            var results = new List<ParticipantResult>(input.Participants.Count);

            foreach (ResultParticipant participant in input.Participants)
            {
                SelectedTimingMark[] marks = selected[participant.ParticipantId];
                SelectedTimingMark start = marks[(int)TimingStage.Start];
                SelectedTimingMark finish = marks[(int)TimingStage.Finish];
                TimeSpan? elapsed = start != null && finish != null
                    ? (TimeSpan?)(finish.CapturedAt - start.CapturedAt)
                    : null;

                ResultStatus status = ResolveStatus(participant, start, finish);
                var result = new ParticipantResult(participant, NormalizeDistanceKey(participant.Distance),
                    start, finish, elapsed, status);
                if (elapsed.HasValue && elapsed.Value < TimeSpan.Zero)
                    result.Warnings.Add("finish_before_start");
                else if (elapsed.HasValue && elapsed.Value == TimeSpan.Zero)
                    result.Warnings.Add("zero_duration");
                results.Add(result);
            }

            AssignOverallPlaces(results);
            ApplyEventPolicyCategoryExclusions(results, exclusionDistances);
            AssignCategoryPlaces(results);
            AssignAwards(results, policy);
            results.Sort(CompareForDisplay);

            return new ResultSnapshot(input.EventId, input.RosterRevision, input.TimingRevision,
                policy.PresetId, policy.Revision, EngineVersion, results);
        }

        public PolicyChangePreview PreviewPolicyChange(ResultCalculationInput input,
            ResultPolicy beforePolicy, ResultPolicy afterPolicy)
        {
            ResultSnapshot before = Calculate(input, beforePolicy);
            ResultSnapshot after = Calculate(input, afterPolicy);
            Dictionary<string, ParticipantResult> beforeById = before.Results
                .ToDictionary(result => result.ParticipantId, StringComparer.Ordinal);
            var changes = new List<PolicyChange>();

            foreach (ParticipantResult afterResult in after.Results)
            {
                ParticipantResult beforeResult = beforeById[afterResult.ParticipantId];
                if (!Equivalent(beforeResult, afterResult))
                    changes.Add(new PolicyChange(afterResult.ParticipantId, beforeResult, afterResult));
            }

            return new PolicyChangePreview(before, after, changes);
        }

        private static void Validate(ResultCalculationInput input, ResultPolicy policy)
        {
            if (input == null) throw new ArgumentNullException("input");
            if (policy == null) throw new ArgumentNullException("policy");
            if (!String.Equals(policy.PresetId, ResultPolicy.CompatActualV1, StringComparison.Ordinal))
                throw new NotSupportedException("Unsupported result policy preset: " + policy.PresetId);
            if (input.Participants.Any(participant => participant == null))
                throw new ArgumentException("Participant collection cannot contain null entries.", "input");
            if (input.TimingOperations.Any(operation => operation == null))
                throw new ArgumentException("Timing operation collection cannot contain null entries.", "input");

            var participantIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ResultParticipant participant in input.Participants)
            {
                if (!participantIds.Add(participant.ParticipantId))
                    throw new ArgumentException("Participant IDs must be unique.", "input");
            }

            foreach (TimingOperation operation in input.TimingOperations)
            {
                if (!participantIds.Contains(operation.ParticipantId))
                    throw new ArgumentException("Timing operation references an unknown participant: " +
                        operation.ParticipantId, "input");
                if (operation.Stage != TimingStage.Start && operation.Stage != TimingStage.Finish)
                    throw new ArgumentException("Timing operation has an unsupported stage.", "input");
                if (operation.Kind != TimingOperationKind.RfidAccepted &&
                    operation.Kind != TimingOperationKind.ManualSet &&
                    operation.Kind != TimingOperationKind.ManualVoided)
                    throw new ArgumentException("Timing operation has an unsupported kind.", "input");
            }
        }

        private static Dictionary<string, SelectedTimingMark[]> SelectMarks(ResultCalculationInput input,
            IDictionary<string, ResultParticipant> participants)
        {
            var selected = participants.Keys.ToDictionary(
                id => id,
                id => new SelectedTimingMark[2],
                StringComparer.Ordinal);

            var seenOperationIds = new Dictionary<string, TimingOperation>(StringComparer.Ordinal);
            foreach (TimingOperation operation in input.TimingOperations)
            {
                TimingOperation previous;
                if (seenOperationIds.TryGetValue(operation.OperationId, out previous))
                {
                    if (!previous.HasSamePayload(operation))
                        throw new ArgumentException("A repeated OperationId has conflicting data: " + operation.OperationId,
                            "input");
                    continue;
                }
                seenOperationIds.Add(operation.OperationId, operation);
            }

            List<TimingOperation> uniqueOperations = seenOperationIds.Values.ToList();
            foreach (IGrouping<OperationOrderGroup, TimingOperation> orderGroup in uniqueOperations
                .GroupBy(operation => new OperationOrderGroup(operation.ParticipantId, operation.Stage,
                    operation.OperationOrder)))
            {
                if (orderGroup.Count() > 1)
                    throw new ArgumentException("OperationOrder must be unique per participant and stage.", "input");
            }

            var ordered = uniqueOperations
                .OrderBy(operation => operation.ParticipantId, StringComparer.Ordinal)
                .ThenBy(operation => operation.Stage)
                .ThenBy(operation => operation.OperationOrder)
                .ThenBy(operation => operation.OperationId, StringComparer.Ordinal);

            foreach (TimingOperation operation in ordered)
            {
                int index = (int)operation.Stage;
                SelectedTimingMark current = selected[operation.ParticipantId][index];
                if (operation.Kind == TimingOperationKind.ManualVoided)
                {
                    // SQL deletes the active row on void; a prior RFID value is not restored.
                    selected[operation.ParticipantId][index] = null;
                    continue;
                }

                if (operation.Kind == TimingOperationKind.ManualSet)
                {
                    selected[operation.ParticipantId][index] = new SelectedTimingMark(
                        operation.OperationId, operation.Stage, operation.Kind, operation.CapturedAt.Value);
                    continue;
                }

                if (current == null || (current.Kind == TimingOperationKind.RfidAccepted &&
                    operation.CapturedAt.Value < current.CapturedAt))
                {
                    selected[operation.ParticipantId][index] = new SelectedTimingMark(
                        operation.OperationId, operation.Stage, operation.Kind, operation.CapturedAt.Value);
                }
            }

            return selected;
        }

        private static ResultStatus ResolveStatus(ResultParticipant participant,
            SelectedTimingMark start, SelectedTimingMark finish)
        {
            if (participant.DnfAt.HasValue && finish == null) return ResultStatus.Dnf;
            if (participant.Chip == null) return ResultStatus.SinChip;
            if (start == null) return ResultStatus.SinSalida;
            if (finish == null) return ResultStatus.EnCarrera;
            return ResultStatus.Finalizo;
        }

        private static void AssignOverallPlaces(IList<ParticipantResult> results)
        {
            foreach (IGrouping<RankingGroup, ParticipantResult> group in results
                .Where(result => result.Elapsed.HasValue && result.DistanceKey != null)
                .GroupBy(result => new RankingGroup(result.DistanceKey, result.Sex, null)))
            {
                AssignCompetitionRanks(group.OrderBy(result => result.Elapsed.Value)
                    .ThenBy(result => result.ParticipantId, StringComparer.Ordinal),
                    (result, place) => result.OverallPlace = place);
            }
        }

        private static void AssignCategoryPlaces(IList<ParticipantResult> results)
        {
            foreach (IGrouping<RankingGroup, ParticipantResult> group in results
                .Where(result => result.Elapsed.HasValue && result.DistanceKey != null &&
                    result.Category != null && result.CategoryExclusionReason == null)
                .GroupBy(result => new RankingGroup(result.DistanceKey, result.Sex, result.Category)))
            {
                AssignCompetitionRanks(group.OrderBy(result => result.Elapsed.Value)
                    .ThenBy(result => result.ParticipantId, StringComparer.Ordinal),
                    (result, place) => result.CategoryPlace = place);
            }
        }

        private static void AssignCompetitionRanks(IEnumerable<ParticipantResult> ordered,
            Action<ParticipantResult, long> assign)
        {
            TimeSpan? previous = null;
            long place = 0;
            long index = 0;
            foreach (ParticipantResult result in ordered)
            {
                index++;
                if (!previous.HasValue || result.Elapsed.Value != previous.Value)
                    place = index;
                assign(result, place);
                previous = result.Elapsed.Value;
            }
        }

        private static void ApplyEventPolicyCategoryExclusions(IList<ParticipantResult> results, ISet<string> exclusionDistances)
        {
            foreach (ParticipantResult result in results)
            {
                if (exclusionDistances.Contains(result.DistanceKey) && result.AgeBracket != null &&
                    result.Category != null &&
                    result.OverallPlace.HasValue && result.OverallPlace.Value <= 3)
                {
                    result.CategoryExclusionReason = "event_policy_general_top3_age_bracket";
                }
            }
        }

        private static void AssignAwards(IList<ParticipantResult> results, ResultPolicy policy)
        {
            foreach (ParticipantResult result in results)
            {
                AwardRule generalRule = FindRule(policy, AwardScope.General, result);
                if (generalRule != null)
                    result.GeneralAward = new AwardDecision(AwardDecisionStatus.NotAwarded,
                        result.OverallPlace.HasValue ? "outside_award_slots" : "no_classification_place",
                        generalRule.Slots);

                AwardRule categoryRule = FindRule(policy, AwardScope.Category, result);
                if (categoryRule != null)
                    result.CategoryAward = new AwardDecision(AwardDecisionStatus.NotAwarded,
                        result.CategoryExclusionReason ?? (result.CategoryPlace.HasValue
                            ? "outside_award_slots" : "no_classification_place"),
                        categoryRule.Slots);
            }

            AssignAwardsForScope(results, policy, AwardScope.General);
            AssignAwardsForScope(results, policy, AwardScope.Category);
        }

        private static void AssignAwardsForScope(IList<ParticipantResult> results,
            ResultPolicy policy, AwardScope scope)
        {
            foreach (AwardRule rule in policy.AwardRules.Where(item => item.Scope == scope))
            {
                IEnumerable<ParticipantResult> matching = results.Where(result =>
                    result.DistanceKey == rule.DistanceKey && result.Sex == rule.Sex &&
                    (scope == AwardScope.General
                        ? result.OverallPlace.HasValue
                        : result.CategoryPlace.HasValue && result.Category == rule.Category));
                IOrderedEnumerable<ParticipantResult> ordered = matching
                    .OrderBy(result => result.Elapsed.Value)
                    .ThenBy(result => result.ParticipantId, StringComparer.Ordinal);

                if (rule.TieMode == AwardTieMode.LimitByParticipantId)
                {
                    int awarded = 0;
                    foreach (ParticipantResult result in ordered)
                    {
                        awarded++;
                        SetAward(result, scope, awarded <= rule.Slots
                            ? AwardDecisionStatus.Awarded : AwardDecisionStatus.NotAwarded,
                            awarded <= rule.Slots ? "awarded_by_explicit_participant_id_tiebreak" : "outside_award_slots",
                            rule.Slots);
                    }
                    continue;
                }

                foreach (IGrouping<TimeSpan, ParticipantResult> tieGroup in ordered.GroupBy(result => result.Elapsed.Value))
                {
                    List<ParticipantResult> tied = tieGroup.ToList();
                    long firstPlace = scope == AwardScope.General
                        ? tied[0].OverallPlace.Value : tied[0].CategoryPlace.Value;
                    long lastPlace = firstPlace + tied.Count - 1;
                    if (firstPlace > rule.Slots) continue;

                    bool crossesBoundary = lastPlace > rule.Slots;
                    AwardDecisionStatus status = crossesBoundary && rule.TieMode == AwardTieMode.ReviewAtBoundary
                        ? AwardDecisionStatus.ReviewRequired
                        : AwardDecisionStatus.Awarded;
                    string reason = status == AwardDecisionStatus.ReviewRequired
                        ? "tie_crosses_award_boundary"
                        : "awarded_with_competition_rank";
                    foreach (ParticipantResult result in tied)
                        SetAward(result, scope, status, reason, rule.Slots);
                }
            }
        }

        private static void SetAward(ParticipantResult result, AwardScope scope,
            AwardDecisionStatus status, string reason, int slots)
        {
            var decision = new AwardDecision(status, reason, slots);
            if (scope == AwardScope.General) result.GeneralAward = decision;
            else result.CategoryAward = decision;
        }

        private static AwardRule FindRule(ResultPolicy policy, AwardScope scope, ParticipantResult result)
        {
            return policy.AwardRules.FirstOrDefault(rule => rule.Scope == scope &&
                rule.DistanceKey == result.DistanceKey && rule.Sex == result.Sex &&
                (scope == AwardScope.General || rule.Category == result.Category));
        }

        private static int CompareForDisplay(ParticipantResult left, ParticipantResult right)
        {
            int compare = CompareNullable(left.DistanceKey, right.DistanceKey, StringComparer.Ordinal);
            if (compare != 0) return compare;
            compare = SexOrder(left.Sex).CompareTo(SexOrder(right.Sex));
            if (compare != 0) return compare;
            compare = CompareNullable(left.OverallPlace, right.OverallPlace);
            if (compare != 0) return compare;
            compare = CompareNullable(left.Bib, right.Bib, StringComparer.Ordinal);
            if (compare != 0) return compare;
            return StringComparer.Ordinal.Compare(left.ParticipantId, right.ParticipantId);
        }

        private static int SexOrder(string sex)
        {
            if (sex == "F") return 1;
            if (sex == "M") return 2;
            if (sex == "otro") return 3;
            if (sex == "no_informado") return 4;
            return 5;
        }

        private static int CompareNullable<T>(T left, T right) where T : class
        {
            return CompareNullable(left, right, Comparer<T>.Default);
        }

        private static int CompareNullable<T>(T left, T right, IComparer<T> comparer) where T : class
        {
            if (left == null && right == null) return 0;
            if (left == null) return 1;
            if (right == null) return -1;
            return comparer.Compare(left, right);
        }

        private static int CompareNullable<T>(T? left, T? right) where T : struct, IComparable<T>
        {
            if (!left.HasValue && !right.HasValue) return 0;
            if (!left.HasValue) return 1;
            if (!right.HasValue) return -1;
            return left.Value.CompareTo(right.Value);
        }

        private static bool Equivalent(ParticipantResult left, ParticipantResult right)
        {
            return left.Status == right.Status && left.Elapsed == right.Elapsed &&
                left.OverallPlace == right.OverallPlace && left.CategoryPlace == right.CategoryPlace &&
                left.CategoryExclusionReason == right.CategoryExclusionReason &&
                left.GeneralAward.Status == right.GeneralAward.Status &&
                left.GeneralAward.Reason == right.GeneralAward.Reason &&
                left.GeneralAward.Slots == right.GeneralAward.Slots &&
                left.CategoryAward.Status == right.CategoryAward.Status &&
                left.CategoryAward.Reason == right.CategoryAward.Reason &&
                left.CategoryAward.Slots == right.CategoryAward.Slots;
        }

        private struct RankingGroup : IEquatable<RankingGroup>
        {
            public RankingGroup(string distance, string sex, string category)
            {
                Distance = distance;
                Sex = sex;
                Category = category;
            }

            private string Distance { get; set; }
            private string Sex { get; set; }
            private string Category { get; set; }

            public bool Equals(RankingGroup other)
            {
                return String.Equals(Distance, other.Distance, StringComparison.Ordinal) &&
                    String.Equals(Sex, other.Sex, StringComparison.Ordinal) &&
                    String.Equals(Category, other.Category, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is RankingGroup && Equals((RankingGroup)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Distance == null ? 0 : StringComparer.Ordinal.GetHashCode(Distance);
                    hash = (hash * 397) ^ (Sex == null ? 0 : StringComparer.Ordinal.GetHashCode(Sex));
                    return (hash * 397) ^ (Category == null ? 0 : StringComparer.Ordinal.GetHashCode(Category));
                }
            }
        }

        private struct OperationOrderGroup : IEquatable<OperationOrderGroup>
        {
            public OperationOrderGroup(string participantId, TimingStage stage, long order)
            {
                ParticipantId = participantId;
                Stage = stage;
                Order = order;
            }

            private string ParticipantId { get; set; }
            private TimingStage Stage { get; set; }
            private long Order { get; set; }

            public bool Equals(OperationOrderGroup other)
            {
                return String.Equals(ParticipantId, other.ParticipantId, StringComparison.Ordinal) &&
                    Stage == other.Stage && Order == other.Order;
            }

            public override bool Equals(object obj)
            {
                return obj is OperationOrderGroup && Equals((OperationOrderGroup)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = ParticipantId == null ? 0 : StringComparer.Ordinal.GetHashCode(ParticipantId);
                    hash = (hash * 397) ^ Stage.GetHashCode();
                    return (hash * 397) ^ Order.GetHashCode();
                }
            }
        }
    }
}
