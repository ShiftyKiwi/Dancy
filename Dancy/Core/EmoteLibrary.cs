using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dancy.Core.Models;
using Dancy.Domain;
using Lumina.Excel.Sheets;

namespace Dancy.Core
{
    /// <summary>
    /// Global in-memory cache of all player emotes for search / selection.
    /// Call EmoteLibrary.Initialize() once during plugin startup.
    /// </summary>
    public static class EmoteLibrary
    {
        public static List<LuminaEmote> AllEmotes { get; private set; } = new();
        public static List<LuminaEmote> LoopingEmotes { get; private set; } = new();

        private static readonly Dictionary<string, ResolvedEmoteInfo> EmotesByTimeline = new(StringComparer.OrdinalIgnoreCase);

        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized)
                return;

            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            if (sheet == null)
                return;

            var list = new List<LuminaEmote>();

            foreach (var emote in sheet)
            {
                if (emote.Name.IsEmpty)
                    continue;

                string name = emote.Name.ExtractText();
                string command = emote.TextCommand.ValueNullable?.Command.ToString() ?? string.Empty;

                // Command-backed records form the regular emote catalog. Named
                // state targets without commands are added below from their exact
                // ActionTimeline identities.
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                var timelineKeys = emote.ActionTimeline
                    .Where(t => t.ValueNullable != null)
                    .Select(t => t.Value.Key.ToString())
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (timelineKeys.Count == 0)
                    continue;

                var loopTimelineKey = timelineKeys.FirstOrDefault(key =>
                    GamePathIdentity.Parse(key).Phase == AnimationPhase.Loop);
                var primaryTimelineKey = loopTimelineKey ?? timelineKeys[0];

                string category = emote.EmoteCategory.ValueNullable?.Name.ExtractText()
                                  ?? "Unknown";

                var resolved = new ResolvedEmoteInfo
                {
                    Name = name,
                    Command = command,
                    RowId = emote.RowId,
                };
                foreach (var timelineKey in timelineKeys)
                {
                    var filename = System.IO.Path.GetFileNameWithoutExtension(timelineKey);
                    if (!string.IsNullOrWhiteSpace(filename))
                        EmotesByTimeline.TryAdd(filename, resolved);
                }

                var primaryPhase = GamePathIdentity.Parse(primaryTimelineKey).Phase;
                list.Add(new LuminaEmote
                {
                    TargetId = $"emote:{emote.RowId}",
                    Name = name,
                    Command = command,
                    RowId = emote.RowId,
                    PrimaryTimelineKey = primaryTimelineKey,
                    TimelineKeys = timelineKeys,
                    IsLoopCapable = loopTimelineKey != null,
                    PrimaryPhase = primaryPhase,
                    Behavior = TargetSemantics.Classify(
                        hasCommand: true,
                        hasLoopTimeline: loopTimelineKey != null,
                        context: TargetContext.Emote,
                        primaryPhase: primaryPhase),
                    Context = TargetContext.Emote,
                    Trigger = command,
                    Category = category,
                });
            }

            AddPersistentStateTargets(list);

            AllEmotes = list
                .OrderBy(e => e.Behavior)
                .ThenBy(e => e.Context)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            LoopingEmotes = AllEmotes.Where(e => e.Behavior == TargetBehavior.LoopingEmote).ToList();

            _initialized = true;
        }

        public static ResolvedEmoteInfo? ResolveTimeline(string gamePath)
        {
            var filename = System.IO.Path.GetFileNameWithoutExtension(gamePath);
            return !string.IsNullOrWhiteSpace(filename)
                   && EmotesByTimeline.TryGetValue(filename, out var emote)
                ? emote
                : null;
        }

        private static void AddPersistentStateTargets(List<LuminaEmote> targets)
        {
            var timelineSheet = Plugin.DataManager.GetExcelSheet<ActionTimeline>();
            if (timelineSheet is null)
                return;

            var timelines = timelineSheet
                .Select(timeline => new TimelineRecord(timeline, timeline.Key.ExtractText()))
                .Where(record => !string.IsNullOrWhiteSpace(record.Key))
                .ToList();

            PromoteOrAddPersistentTarget(
                targets,
                timelines,
                "emote/sit",
                "Sit",
                "/lounge",
                "Seated on furniture state",
                TargetContext.ChairSit,
                "Current ActionTimeline: resident chair state (priority 9, slot 2).");
            PromoteOrAddPersistentTarget(
                targets,
                timelines,
                "emote/jmn",
                "Sit on Ground",
                "/groundsit",
                "Ground-sit state",
                TargetContext.GroundSit,
                "Current ActionTimeline: resident ground-sit state (priority 8, slot 2).");
            PromoteOrAddPersistentTarget(
                targets,
                timelines,
                "emote/bed_liedown_loop",
                "Sleep",
                string.Empty,
                "Bed or inn sleep state",
                TargetContext.SleepOrLie,
                "Current ActionTimeline: resident bed sleep loop (priority 0, slot 3).");
            PromoteOrAddPersistentTarget(
                targets,
                timelines,
                "normal/idle",
                "Standing Idle",
                string.Empty,
                "Standing state",
                TargetContext.StandingIdle,
                "Current ActionTimeline: normal idle state (priority 7, slot 0).");

            AddChangePoseTargets(targets, timelines);
        }

        private static void PromoteOrAddPersistentTarget(
            List<LuminaEmote> targets,
            IReadOnlyList<TimelineRecord> timelines,
            string timelineKey,
            string name,
            string command,
            string trigger,
            TargetContext context,
            string evidence)
        {
            var matches = timelines
                .Where(record => string.Equals(record.Key, timelineKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
                return;

            var existing = targets.FirstOrDefault(target => target.TimelineKeys
                .Any(key => string.Equals(key, timelineKey, StringComparison.OrdinalIgnoreCase)));
            if (existing is not null)
            {
                existing.PrimaryTimelineKey = timelineKey;
                existing.PrimaryPhase = GamePathIdentity.Parse(timelineKey).Phase;
                existing.Behavior = TargetBehavior.PersistentPose;
                existing.Context = context;
                existing.Trigger = trigger;
                existing.ClassificationEvidence = evidence;
                return;
            }

            targets.Add(new LuminaEmote
            {
                TargetId = $"state:{timelineKey}",
                Name = name,
                Command = command,
                RowId = matches[0].Timeline.RowId,
                PrimaryTimelineKey = timelineKey,
                TimelineKeys = [timelineKey],
                IsLoopCapable = GamePathIdentity.Parse(timelineKey).Phase == AnimationPhase.Loop,
                PrimaryPhase = GamePathIdentity.Parse(timelineKey).Phase,
                Behavior = TargetBehavior.PersistentPose,
                Context = context,
                Trigger = trigger,
                Category = "Persistent state",
                ClassificationEvidence = evidence,
            });
        }

        private static void AddChangePoseTargets(List<LuminaEmote> targets, IReadOnlyList<TimelineRecord> timelines)
        {
            var families = new[]
            {
                new PoseFamily(@"^emote/pose(?<number>\d{2})_loop$", "Standing Pose", TargetContext.StandingIdle, "Change Pose while standing", new TimelineSignature(7, 0)),
                new PoseFamily(@"^emote/s_pose(?<number>\d{2})_loop$", "Chair Pose", TargetContext.ChairSit, "Change Pose while seated on furniture", new TimelineSignature(9, 2)),
                new PoseFamily(@"^emote/j_pose(?<number>\d{2})_loop$", "Ground-sit Pose", TargetContext.GroundSit, "Change Pose while sitting on the ground", new TimelineSignature(8, 2)),
                new PoseFamily(@"^emote/l_pose(?<number>\d{2})_loop$", "Lying Pose", TargetContext.SleepOrLie, "Change Pose while lying down", new TimelineSignature(0, 3)),
            };

            foreach (var group in timelines
                         .Where(record => record.Timeline.IsLoop && record.Timeline.Resident)
                         .GroupBy(record => record.Key, StringComparer.OrdinalIgnoreCase))
            {
                var family = families
                    .Select(definition => (Definition: definition, Match: Regex.Match(group.Key, definition.Pattern, RegexOptions.IgnoreCase)))
                    .FirstOrDefault(result => result.Match.Success);
                if (family.Match is not { Success: true })
                    continue;

                var number = family.Match.Groups["number"].Value.TrimStart('0');
                if (string.IsNullOrWhiteSpace(number))
                    number = "0";
                var hasExpectedStateSignature = group.Any(record => family.Definition.Signature.Matches(record.Timeline));
                var context = hasExpectedStateSignature ? family.Definition.Context : TargetContext.OtherPersistentPose;
                var name = hasExpectedStateSignature
                    ? $"{family.Definition.NamePrefix} {number}"
                    : $"Change Pose {number}";
                var trigger = hasExpectedStateSignature
                    ? family.Definition.Trigger
                    : "Change Pose state (context not identified by current game data)";
                var evidence = hasExpectedStateSignature
                    ? $"Current ActionTimeline state signature matches {TargetSemantics.DisplayName(context)} (priority {family.Definition.Signature.Priority}, slot {family.Definition.Signature.Slot})."
                    : "Current ActionTimeline is resident and looped, but its state signature did not match a named standing, chair, ground-sit, or lying state.";

                targets.Add(new LuminaEmote
                {
                    TargetId = $"pose:{group.Key}",
                    Name = name,
                    RowId = group.First().Timeline.RowId,
                    PrimaryTimelineKey = group.Key,
                    TimelineKeys = [group.Key],
                    IsLoopCapable = true,
                    PrimaryPhase = AnimationPhase.Loop,
                    Behavior = TargetBehavior.PersistentPose,
                    Context = context,
                    Trigger = trigger,
                    Category = "Change Pose",
                    ClassificationEvidence = evidence,
                });
            }
        }

        private sealed record TimelineRecord(ActionTimeline Timeline, string Key);

        private sealed record TimelineSignature(byte Priority, byte Slot)
        {
            public bool Matches(ActionTimeline timeline)
                => timeline.Priority == Priority && timeline.Slot == Slot;
        }

        private sealed record PoseFamily(string Pattern, string NamePrefix, TargetContext Context, string Trigger, TimelineSignature Signature);
    }
}
