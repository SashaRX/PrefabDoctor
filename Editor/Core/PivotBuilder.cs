using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.PrefabDoctor
{
    /// <summary>
    /// Pure transform from <see cref="AnalysisReport"/> + one instance root
    /// into an <see cref="InstancePivotBlock"/> suitable for the pivot view.
    /// </summary>
    internal static class PivotBuilder
    {
        /// <summary>
        /// Build a pivot block for a single scene prefab instance.
        /// <paramref name="passesFilter"/> is the active severity/category
        /// filter from the window — conflicts that fail it are dropped before
        /// a GO group is built. Returns null if the instance has no matching
        /// conflicts.
        /// </summary>
        public static InstancePivotBlock Build(
            AnalysisReport report,
            GameObject instanceRoot,
            Func<PropertyConflict, bool> passesFilter)
        {
            if (report == null || instanceRoot == null) return null;
            passesFilter ??= static _ => true;

            int selectedInstanceId = instanceRoot.GetInstanceID();
            // Non-hierarchy reports don't populate InstanceRoot / scoped keys
            // per GO — every GameObjectReport implicitly belongs to the single
            // analyzed root. Short-circuit the attribution check so the pivot
            // view is usable in instance mode too.
            bool includeAll = !report.IsHierarchyMode;
            var visibleDepths = new HashSet<int>();
            var goGroups = new List<GoPropertyGroup>();

            foreach (var goReport in report.GameObjects)
            {
                if (!includeAll
                    && !BelongsToInstance(goReport, selectedInstanceId)) continue;

                GoPropertyGroup bucket = null;

                for (int i = 0; i < goReport.Conflicts.Count; i++)
                {
                    var conflict = goReport.Conflicts[i];
                    if (!passesFilter(conflict)) continue;

                    var row = new PropertyRow { Conflict = conflict };
                    for (int j = 0; j < conflict.Overrides.Count; j++)
                    {
                        var entry = conflict.Overrides[j];
                        // Prefer the first entry at a given depth if the
                        // analyzer ever emits duplicates (shouldn't, but
                        // defensive — ByDepth is one-to-one by contract).
                        row.ByDepth[entry.Depth] = entry;
                        visibleDepths.Add(entry.Depth);
                    }

                    bucket ??= new GoPropertyGroup
                    {
                        GoPath = goReport.RelativePath,
                        GoDisplayName = BuildGoDisplayName(goReport.RelativePath),
                        Go = goReport.Instance,
                        GoReport = goReport,
                    };
                    bucket.Properties.Add(row);
                }

                if (bucket != null) goGroups.Add(bucket);
            }

            if (goGroups.Count == 0) return null;

            goGroups.Sort(static (a, b) =>
                string.Compare(a.GoPath, b.GoPath, StringComparison.OrdinalIgnoreCase));

            var sortedDepths = new List<int>(visibleDepths);
            sortedDepths.Sort();

            // In hierarchy mode report.Chain is built from the OUTER analyzed
            // root (e.g. Level), but each instance has its own variant chain.
            // Rebuild it per instance so depth column headers and cell-level
            // "Ping source prefab" actions point at the correct asset. For
            // instance-mode analyses report.Chain already describes the
            // selected root — reuse it to save a BuildChain call.
            List<NestingLevel> chain = report.IsHierarchyMode
                ? new OverrideAnalyzer().BuildChain(instanceRoot)
                : report.Chain;

            return new InstancePivotBlock
            {
                InstanceRoot = instanceRoot,
                InstanceName = instanceRoot.name,
                Chain = chain,
                VisibleDepths = sortedDepths,
                GoGroups = goGroups,
            };
        }

        private static bool BelongsToInstance(
            GameObjectReport goReport, int selectedInstanceId)
        {
            if (goReport == null) return false;
            if (goReport.InstanceRoot != null
                && goReport.InstanceRoot.GetInstanceID() == selectedInstanceId)
                return true;

            var key = goReport.InstanceScopedPathKey;
            if (string.IsNullOrEmpty(key)) return false;

            int colon = key.IndexOf(':');
            if (colon <= 0) return false;

            return int.TryParse(key.AsSpan(0, colon), out int id)
                && id == selectedInstanceId;
        }

        private static string BuildGoDisplayName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "<root>";
            int lastSlash = path.LastIndexOf('/');
            return lastSlash < 0 ? path : path[(lastSlash + 1)..];
        }
    }
}
