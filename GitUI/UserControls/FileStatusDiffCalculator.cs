﻿using System;
using System.Collections.Generic;
using System.Linq;
using GitCommands;
using GitCommands.Git;
using GitUIPluginInterfaces;
using Microsoft;

namespace GitUI
{
    public sealed partial class FileStatusDiffCalculator
    {
        private readonly Func<GitModule> _getModule;

        // Currently bound revisions etc. Cache so we can reload the view, if AppSettings.ShowDiffForAllParents is changed.
        private FileStatusDiffCalculatorInfo _fileStatusDiffCalculatorInfo = new();

        // Default helper functions, can be set
        public Func<ObjectId, string>? DescribeRevision { get; set; }
        public Func<GitRevision, GitRevision>? GetActualRevision { get; set; }

        public FileStatusDiffCalculator(Func<GitModule> getModule)
        {
            _getModule = getModule;
        }

        public IReadOnlyList<FileStatusWithDescription> Reload()
            => SetDiffs(_fileStatusDiffCalculatorInfo.Revisions,
                _fileStatusDiffCalculatorInfo.HeadId);

        public IReadOnlyList<FileStatusWithDescription> SetDiffs(
            IReadOnlyList<GitRevision> revisions,
            ObjectId? headId)
        {
            _fileStatusDiffCalculatorInfo.Revisions = revisions;
            _fileStatusDiffCalculatorInfo.HeadId = headId;

            var selectedRev = revisions.FirstOrDefault();
            if (selectedRev is null)
            {
                return Array.Empty<FileStatusWithDescription>();
            }

            GitModule module = GetModule();

            List<FileStatusWithDescription> fileStatusDescs = new();
            if (revisions!.Count == 1)
            {
                // If the grid is filtered, parents may be rewritten
                GitRevision actualRev = GetActualRevisionForRevision(selectedRev);

                if (actualRev.ParentIds is null || actualRev.ParentIds.Count == 0)
                {
                    fileStatusDescs.Add(new FileStatusWithDescription(
                        firstRev: null,
                        secondRev: selectedRev,
                        summary: GetDescriptionForRevision(selectedRev.ObjectId),
                        statuses: selectedRev.TreeGuid is null

                            // likely index commit without HEAD
                            ? module.GetDiffFilesWithSubmodulesStatus(null, selectedRev.ObjectId, null)

                            // No parent for the initial commit
                            : module.GetTreeFiles(selectedRev.TreeGuid, full: true)));
                }
                else
                {
                    // Get the parents for the selected revision
                    var multipleParents = actualRev.ParentIds is null ? 0 : AppSettings.ShowDiffForAllParents ? actualRev.ParentIds.Count : 1;
                    fileStatusDescs.AddRange(actualRev
                        .ParentIds
                        .Take(multipleParents)
                        .Select(parentId =>
                            new FileStatusWithDescription(
                                firstRev: new GitRevision(parentId),
                                secondRev: selectedRev,
                                summary: TranslatedStrings.DiffWithParent + GetDescriptionForRevision(parentId),
                                statuses: module.GetDiffFilesWithSubmodulesStatus(parentId, selectedRev.ObjectId, actualRev.ParentIds[0]))));
                }

                // Show combined (merge conflicts) when a single merge commit is selected
                var isMergeCommit = (selectedRev.ParentIds?.Count ?? 0) > 1;
                if (isMergeCommit && AppSettings.ShowDiffForAllParents)
                {
                    var conflicts = module.GetCombinedDiffFileList(selectedRev.Guid);
                    if (conflicts.Count != 0)
                    {
                        // Create an artificial commit
                        fileStatusDescs.Add(new FileStatusWithDescription(
                            firstRev: new GitRevision(ObjectId.CombinedDiffId), secondRev: selectedRev, summary: TranslatedStrings.CombinedDiff, statuses: conflicts));
                    }
                }

                return fileStatusDescs;
            }

            // With more than 4, only first -> selected is interesting
            // Show multi compare if 2-4 are selected
            const int maxMultiCompare = 4;

            // With 4 selected, assume that ranges are selected: baseA..headA baseB..headB
            // the first item is therefore the second selected
            var firstRev = AppSettings.ShowDiffForAllParents && revisions.Count == maxMultiCompare
                ? revisions[2]
                : revisions.Last();

            fileStatusDescs.Add(new FileStatusWithDescription(
                firstRev: firstRev,
                secondRev: selectedRev,
                summary: TranslatedStrings.DiffWithParent + GetDescriptionForRevision(firstRev.ObjectId),
                statuses: module.GetDiffFilesWithSubmodulesStatus(firstRev.ObjectId, selectedRev.ObjectId, selectedRev.FirstParentId)));

            if (!AppSettings.ShowDiffForAllParents || revisions.Count > maxMultiCompare || headId is null)
            {
                return fileStatusDescs;
            }

            // Get base commit, add as parent if unique
            var firstRevHead = GetRevisionOrHead(firstRev, headId);
            var selectedRevHead = GetRevisionOrHead(selectedRev, headId);
            var baseRevGuid = module.GetMergeBase(firstRevHead, selectedRevHead);

            // Four selected, to check if two ranges are selected
            var baseA = (revisions.Count != 4 || baseRevGuid is null)
                ? null
                : module.GetMergeBase(GetRevisionOrHead(revisions[3], headId), firstRevHead);
            var baseB = baseA is null || baseA != revisions[3].ObjectId
                ? null
                : module.GetMergeBase(GetRevisionOrHead(revisions[1], headId), selectedRevHead);
            if (baseB != revisions[1].ObjectId)
            {
                baseB = null;
            }

            // Check for separate branches (note that artificial commits both have HEAD as BASE)
            if (baseRevGuid is null

                // For two check that the selections are in separate branches
                || (revisions.Count == 2 && (baseRevGuid == firstRevHead
                    || baseRevGuid == selectedRevHead))

                // For three, show multi-diff if not base is selected
                || (revisions.Count == 3 && baseRevGuid != revisions[1].ObjectId)

                // For four, two ranges must be selected
                || (revisions.Count == 4 && (baseA is null || baseB is null)))
            {
                // No variant of range diff, show multi diff
                fileStatusDescs.AddRange(
                    revisions
                        .Where(rev => rev != firstRev && rev != selectedRev)
                        .Select(rev => new FileStatusWithDescription(
                            firstRev: rev,
                            secondRev: selectedRev,
                            summary: TranslatedStrings.DiffWithParent + GetDescriptionForRevision(rev.ObjectId),
                            statuses: module.GetDiffFilesWithSubmodulesStatus(rev.ObjectId, selectedRev.ObjectId, selectedRev.FirstParentId))));

                return fileStatusDescs;
            }

            var allAToB = fileStatusDescs[0].Statuses;
            var allBaseToB = module.GetDiffFilesWithSubmodulesStatus(baseRevGuid, selectedRev.ObjectId, selectedRev.FirstParentId);
            var allBaseToA = module.GetDiffFilesWithSubmodulesStatus(baseRevGuid, firstRev.ObjectId, firstRev.FirstParentId);

            GitItemStatusNameEqualityComparer comparer = new();
            var sameBaseToAandB = allBaseToB.Intersect(allBaseToA, comparer).Except(allAToB, comparer).ToList();
            var onlyA = allBaseToA.Except(allBaseToB, comparer).ToList();
            var onlyB = allBaseToB.Except(allBaseToA, comparer).ToList();

            foreach (var l in new[] { allAToB, allBaseToB, allBaseToA })
            {
                foreach (var f in l)
                {
                    f.DiffStatus = GetDiffStatus(f, l == allAToB);
                }
            }

            DiffBranchStatus GetDiffStatus(GitItemStatus f, bool atoBDiff)
            {
                // Always show where the change is done
                // This means that if a file is added in A it is shown as removed in the A->B diff,
                // but marked with A
                return sameBaseToAandB.Any(i => i.Name == f.Name) ? DiffBranchStatus.SameChange
                    : onlyA.Any(i => i.Name == f.Name) ? DiffBranchStatus.OnlyAChange
                    : onlyB.Any(i => i.Name == f.Name) ? DiffBranchStatus.OnlyBChange
                    : DiffBranchStatus.UnequalChange;
            }

            GitRevision revBase = new(baseRevGuid);
            fileStatusDescs.Add(new FileStatusWithDescription(
                firstRev: revBase,
                secondRev: selectedRev,
                summary: $"{TranslatedStrings.DiffBaseWith} B {GetDescriptionForRevision(selectedRev.ObjectId)}",
                statuses: allBaseToB));
            fileStatusDescs.Add(new FileStatusWithDescription(
                firstRev: revBase,
                secondRev: firstRev,
                summary: $"{TranslatedStrings.DiffBaseWith} A {GetDescriptionForRevision(firstRev.ObjectId)}",
                statuses: allBaseToA));

            if (!module.GitVersion.SupportRangeDiffTool)
            {
                return fileStatusDescs;
            }

            // Add rangeDiff as a FileStatus item (even with artificial commits)
            var first = firstRev.ObjectId == firstRevHead ? firstRev : new GitRevision(firstRevHead);
            var selected = selectedRev.ObjectId == selectedRevHead ? selectedRev : new GitRevision(selectedRevHead);
            var (baseToFirstCount, baseToSecondCount) = module.GetCommitRangeDiffCount(first.ObjectId, selected.ObjectId);

            // first and selected has a common merge base and count must be available
            // Only a printout, so no Validates
            var desc = $"{TranslatedStrings.DiffRange} {baseToFirstCount ?? -1}↓ {baseToSecondCount ?? -1}↑ BASE {GetDescriptionForRevision(baseRevGuid)}";
            allAToB = allAToB.Append(new GitItemStatus(name: desc) { IsRangeDiff = true }).ToList();

            // Replace the A->B group with new statuses
            fileStatusDescs[0] = new(
                firstRev: fileStatusDescs[0].FirstRev,
                secondRev: fileStatusDescs[0].SecondRev,
                summary: fileStatusDescs[0].Summary,
                statuses: allAToB,
                baseA: baseA,
                baseB: baseB);

            return fileStatusDescs;

            static ObjectId GetRevisionOrHead(GitRevision rev, ObjectId headId)
                => rev.IsArtificial ? headId : rev.ObjectId;

            string GetDescriptionForRevision(ObjectId objectId)
                => DescribeRevision is not null ? DescribeRevision(objectId) : objectId.ToShortString();

            GitRevision GetActualRevisionForRevision(GitRevision revision)
                => GetActualRevision is not null ? GetActualRevision(revision) : revision;
        }

        private GitModule GetModule()
        {
            var module = _getModule();

            if (module is null)
            {
                throw new ArgumentException($"Require a valid instance of {nameof(GitModule)}");
            }

            return module;
        }
    }
}
