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
        public static string ToMarkdown(AnalysisReport report)
        {
            if (report == null)
                return "# Prefab Doctor Report\n\n(no report)\n";

            var sb = new StringBuilder(4096);

            AppendHeader(sb, report);
            AppendSummary(sb, report);
            AppendSeverityBlock(sb, report, ConflictSeverity.PingPong,      "PingPong");
            AppendSeverityBlock(sb, report, ConflictSeverity.MultiOverride, "MultiOverride");
            AppendSeverityBlock(sb, report, ConflictSeverity.Orphan,        "Orphan");
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
            StringBuilder sb, AnalysisReport r, ConflictSeverity severity, string title)
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

            foreach (var go in r.GameObjects)
            {
                foreach (var conflict in go.Conflicts)
                {
                    if (conflict.Severity != severity) continue;
                    AppendConflict(sb, go, conflict);
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
                foreach (var e in sorted)
                {
                    sb.Append("  - depth ").Append(e.Depth);
                    sb.Append(" (`").Append(ShortAssetName(e.AssetPath)).Append("`): `");
                    sb.Append(e.Value ?? "(null)").Append('`');

                    if (e.Depth == firstDepth)         sb.Append(" ← first");
                    else if (e.Depth == middleDepth)   sb.Append(" ← middle");
                    else if (e.Depth == pingBackDepth) sb.Append(" ← pingBack");

                    sb.Append('\n');
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
