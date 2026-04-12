using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SashaRX.PrefabDoctor
{
    /// <summary>
    /// Serialises an <see cref="AnalysisReport"/> into a markdown string
    /// suitable for pasting into a bug report, issue tracker, or chat.
    ///
    /// PingPong / MultiOverride / Orphan are rendered in full (one bullet
    /// per override entry with its depth and source asset). Insignificant
    /// entries are aggregated by category only — their full list would
    /// overflow the clipboard on any real project and carry no analytic
    /// value anyway.
    ///
    /// No UI dependencies here: this class could be called from a CI tool
    /// or a future command-line driver without touching the editor window.
    /// </summary>
    internal static class OverrideReportFormatter
    {
        // Hard caps for clipboard-friendly reports. A real scene hierarchy
        // can easily produce 300k+ orphan conflicts, which turns a naive
        // full dump into a 40MB markdown file that nobody can paste into
        // a chat or bug tracker. We emit a bounded report instead: first
        // N entries per severity + aggregated top-K summaries for Orphan,
        // and a pure count for Insignificant.
        private const int k_MaxPingPongEntries      = 200;
        private const int k_MaxMultiOverrideEntries = 200;
        private const int k_MaxOrphanEntries        = 50;
        private const int k_OrphanTopProperties     = 20;

        public static string ToMarkdown(AnalysisReport report)
        {
            if (report == null)
                return "# Prefab Doctor Report\n\n(no report)\n";

            var sb = new StringBuilder(4096);

            AppendHeader(sb, report);
            AppendSummary(sb, report);
            AppendSeverityBlock(sb, report, ConflictSeverity.PingPong,
                "PingPong", k_MaxPingPongEntries);
            AppendSeverityBlock(sb, report, ConflictSeverity.MultiOverride,
                "MultiOverride", k_MaxMultiOverrideEntries);
            AppendOrphanBlock(sb, report);
            AppendInsignificantNote(sb, report);

            return sb.ToString();
        }

        // ── Sections ───────────────────────────────────────────────

        private static void AppendHeader(StringBuilder sb, AnalysisReport r)
        {
            sb.Append("# Prefab Doctor Report\n\n");

            string rootName = r.AnalyzedRoot != null ? r.AnalyzedRoot.name : "(null)";
            sb.Append("**Root:** ").Append(rootName).Append('\n');

            if (r.IsHierarchyMode)
            {
                sb.Append("**Mode:** Hierarchy (")
                  .Append(r.InstancesAnalyzed)
                  .Append(" instance");
                if (r.InstancesAnalyzed != 1) sb.Append('s');
                sb.Append(")\n");
            }
            else
            {
                sb.Append("**Mode:** Instance\n");
            }

            if (r.Chain != null && r.Chain.Count > 0)
            {
                sb.Append("**Chain:** ");
                for (int i = 0; i < r.Chain.Count; i++)
                {
                    if (i > 0) sb.Append(" → ");
                    var lvl = r.Chain[i];
                    sb.Append(lvl.IsSceneInstance ? "[Scene]" : ShortAssetName(lvl.AssetPath));
                }
                sb.Append('\n');
            }

            sb.Append("**Elapsed:** ").Append(r.AnalysisTimeMs.ToString("F0")).Append("ms\n\n");
        }

        private static void AppendSummary(StringBuilder sb, AnalysisReport r)
        {
            sb.Append("## Summary\n\n");
            sb.Append("| Severity | Count |\n");
            sb.Append("| --- | --- |\n");
            sb.Append("| PingPong | ").Append(r.TotalPingPong).Append(" |\n");
            sb.Append("| MultiOverride | ").Append(r.TotalMultiOverride).Append(" |\n");
            sb.Append("| Orphan | ").Append(r.TotalOrphan).Append(" |\n");
            sb.Append("| Insignificant | ").Append(r.TotalInsignificant).Append(" |\n\n");

            var cats = CountByCategory(r);
            int total = 0;
            foreach (var v in cats.Values) total += v;
            if (total == 0) return;

            sb.Append("| Category | Count |\n");
            sb.Append("| --- | --- |\n");
            AppendCategoryRow(sb, "Lightmap",     cats[OverrideCategory.Lightmap]);
            AppendCategoryRow(sb, "NetworkNoise", cats[OverrideCategory.NetworkNoise]);
            AppendCategoryRow(sb, "Transform",    cats[OverrideCategory.Transform]);
            AppendCategoryRow(sb, "StaticFlags",  cats[OverrideCategory.StaticFlags]);
            AppendCategoryRow(sb, "Name",         cats[OverrideCategory.Name]);
            AppendCategoryRow(sb, "Material",     cats[OverrideCategory.Material]);
            AppendCategoryRow(sb, "General",      cats[OverrideCategory.General]);
            sb.Append('\n');
        }

        private static void AppendCategoryRow(StringBuilder sb, string name, int count)
        {
            if (count == 0) return;
            sb.Append("| ").Append(name).Append(" | ").Append(count).Append(" |\n");
        }

        private static void AppendSeverityBlock(
            StringBuilder sb, AnalysisReport r, ConflictSeverity severity,
            string title, int maxEntries)
        {
            int count = severity switch
            {
                ConflictSeverity.PingPong      => r.TotalPingPong,
                ConflictSeverity.MultiOverride => r.TotalMultiOverride,
                ConflictSeverity.Orphan        => r.TotalOrphan,
                _                              => 0
            };
            if (count == 0) return;

            sb.Append("## ").Append(title).Append(" (").Append(count).Append(")\n\n");

            int emitted = 0;
            foreach (var go in r.GameObjects)
            {
                if (emitted >= maxEntries) break;
                foreach (var conflict in go.Conflicts)
                {
                    if (conflict.Severity != severity) continue;
                    AppendConflict(sb, go, conflict);
                    if (++emitted >= maxEntries) break;
                }
            }

            if (emitted < count)
            {
                sb.Append("_…and ").Append(count - emitted)
                  .Append(" more ").Append(title).Append(" entries not listed._\n\n");
            }
        }

        /// <summary>
        /// Orphans are usually dominated by a handful of removed components
        /// repeated across thousands of prefab instances. Instead of dumping
        /// every entry (which on a real scene means 300k+ rows), emit a
        /// histogram of the most common (ComponentType, PropertyPath) pairs
        /// followed by a small sample of full entries.
        /// </summary>
        private static void AppendOrphanBlock(StringBuilder sb, AnalysisReport r)
        {
            if (r.TotalOrphan == 0) return;

            sb.Append("## Orphan (").Append(r.TotalOrphan).Append(")\n\n");

            // Histogram by (ComponentType, PropertyPath). ComponentType is
            // always "MISSING" for true orphans, but PropertyPath varies.
            var histogram = new Dictionary<string, int>();
            foreach (var go in r.GameObjects)
            foreach (var c in go.Conflicts)
            {
                if (c.Severity != ConflictSeverity.Orphan) continue;
                string key = c.Key.PropertyPath ?? "(null)";
                histogram.TryGetValue(key, out int n);
                histogram[key] = n + 1;
            }

            if (histogram.Count > 0)
            {
                sb.Append("Top property paths (orphan count):\n\n");
                var top = new List<KeyValuePair<string, int>>(histogram);
                top.Sort(static (a, b) => b.Value.CompareTo(a.Value));

                int shown = Math.Min(top.Count, k_OrphanTopProperties);
                for (int i = 0; i < shown; i++)
                    sb.Append("- `").Append(top[i].Key).Append("` × ")
                      .Append(top[i].Value).Append('\n');
                sb.Append('\n');
            }

            // A small sample of individual entries — useful when the user
            // needs to see a specific path and fix it manually.
            sb.Append("Sample entries (first ").Append(k_MaxOrphanEntries).Append("):\n\n");
            int emitted = 0;
            foreach (var go in r.GameObjects)
            {
                if (emitted >= k_MaxOrphanEntries) break;
                foreach (var conflict in go.Conflicts)
                {
                    if (conflict.Severity != ConflictSeverity.Orphan) continue;
                    AppendConflict(sb, go, conflict);
                    if (++emitted >= k_MaxOrphanEntries) break;
                }
            }
        }

        private static void AppendConflict(StringBuilder sb, GameObjectReport go, PropertyConflict c)
        {
            sb.Append("### ").Append(go.RelativePath)
              .Append(" — ").Append(c.Key.ComponentType).Append('\n');
            sb.Append("- Property: `").Append(c.Key.PropertyPath).Append("`\n");
            sb.Append("- Category: ").Append(c.Category).Append('\n');

            if (c.Overrides != null && c.Overrides.Count > 0)
            {
                // Copy + sort by depth for display. The in-memory Overrides
                // list may be in insertion order (chain walk) which usually
                // equals depth order, but we sort explicitly so any future
                // collection path still produces deterministic output.
                var sorted = new List<OverrideEntry>(c.Overrides);
                sorted.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));

                // PingPong marker map: look up depths of first/middle/pingBack
                // via the indices that point into the ORIGINAL c.Overrides list.
                int firstDepth = -1, middleDepth = -1, pingBackDepth = -1;
                if (c.Severity == ConflictSeverity.PingPong &&
                    c.PingPongIndices.first >= 0 &&
                    c.PingPongIndices.first < c.Overrides.Count)
                {
                    firstDepth    = c.Overrides[c.PingPongIndices.first].Depth;
                    middleDepth   = c.Overrides[c.PingPongIndices.middle].Depth;
                    pingBackDepth = c.Overrides[c.PingPongIndices.pingBack].Depth;
                }

                sb.Append("- Values by depth:\n");
                // Collapse runs of identical (depth, value) entries —
                // orphan merging in CollectOverrides can produce dozens
                // of duplicate rows in one conflict, and dumping each
                // one is useless noise.
                int i = 0;
                while (i < sorted.Count)
                {
                    var head = sorted[i];
                    int run = 1;
                    while (i + run < sorted.Count
                           && sorted[i + run].Depth == head.Depth
                           && string.Equals(sorted[i + run].Value, head.Value,
                                            StringComparison.Ordinal))
                    {
                        run++;
                    }

                    sb.Append("  - depth ").Append(head.Depth);
                    sb.Append(" (`").Append(ShortAssetName(head.AssetPath)).Append("`): `");
                    sb.Append(head.Value ?? "(null)").Append('`');
                    if (run > 1) sb.Append(" × ").Append(run);

                    if (head.Depth == firstDepth)         sb.Append(" ← first");
                    else if (head.Depth == middleDepth)   sb.Append(" ← middle");
                    else if (head.Depth == pingBackDepth) sb.Append(" ← pingBack");

                    sb.Append('\n');
                    i += run;
                }

                if (c.Severity == ConflictSeverity.PingPong)
                    sb.Append("- Pattern: A → B → A\n");
            }

            sb.Append('\n');
        }

        private static void AppendInsignificantNote(StringBuilder sb, AnalysisReport r)
        {
            if (r.TotalInsignificant == 0) return;

            sb.Append("## Insignificant\n\n");
            sb.Append("Not listed individually (")
              .Append(r.TotalInsignificant)
              .Append(" entries). Use the LightmapOnly / NetworkNoiseOnly / ")
              .Append("InsignificantOnly filter dropdown in the window to inspect ")
              .Append("specific categories.\n");
        }

        // ── Helpers ────────────────────────────────────────────────

        private static Dictionary<OverrideCategory, int> CountByCategory(AnalysisReport r)
        {
            var counts = new Dictionary<OverrideCategory, int>
            {
                [OverrideCategory.General]      = 0,
                [OverrideCategory.Transform]    = 0,
                [OverrideCategory.Lightmap]     = 0,
                [OverrideCategory.NetworkNoise] = 0,
                [OverrideCategory.StaticFlags]  = 0,
                [OverrideCategory.Name]         = 0,
                [OverrideCategory.Material]     = 0,
            };
            if (r.GameObjects == null) return counts;

            foreach (var go in r.GameObjects)
            foreach (var c in go.Conflicts)
                counts[c.Category]++;

            return counts;
        }

        private static string ShortAssetName(string assetPath) =>
            string.IsNullOrEmpty(assetPath) ? "[Scene]" : Path.GetFileNameWithoutExtension(assetPath);
    }
}
