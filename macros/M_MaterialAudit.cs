using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace UnityCliMacros
{
    /// What the project's materials are made of, in one call.
    ///
    /// Three questions come up constantly and none of them had a macro: "which shaders is this project
    /// actually using, and how many materials on each" (the answer that tells you whether an art pass is
    /// 6 materials or 600), "what is the palette" (base colour and main texture per material), and "which
    /// of these are actually used in the open scene, and how much of the screen do they cover" (the
    /// difference between a material worth restyling and one that ships with a package).
    ///
    /// Answering any of them by hand means an `eval` that walks AssetDatabase.FindAssets("t:Material"),
    /// filters out the package and engine defaults that swamp the real ones, and reaches for _BaseColor or
    /// _Color depending on pipeline. That snippet gets rewritten every time, and the filtering is the part
    /// people get wrong: a stock URP project reports ~100 materials that live in packages and cannot be
    /// edited, which drowns the dozen that matter.
    public static class M_MaterialAudit
    {
        private const int MAX_ROWS_LIMIT = 400;

        /// Property names to try, in order, across pipelines. URP/HDRP use the first, built-in the second.
        private static readonly string[] COLOR_PROPERTIES = { "_BaseColor", "_Color" };
        private static readonly string[] TEXTURE_PROPERTIES = { "_BaseMap", "_MainTex" };

        private const string LEGEND =
            "s=shaders[{n name, c material count}] sorted by count; " +
            "r=rows[{n material name, p assetPath (usable verbatim as an objectref), s shader, " +
            "c base colour hex or null when the shader has no colour property, t main texture name or null, " +
            "v material variant parent name (present only for variants - setting .shader on one throws), " +
            "u renderers using it in the open scene, a approximate ground footprint of those renderers}] " +
            "- rows are present only when detail=true; " +
            "counts: total=materials scanned, shown=rows returned, skippedPackages=materials outside " +
            "Assets/ that were excluded (pass packages=true to include them)";

        [CliCommand("m_material_audit",
            "Audit the project's materials: shader usage counts, and optionally per-material base colour, main texture, variant parent and open-scene usage. Excludes package/engine materials by default.")]
        public static string MaterialAudit(
            [CliArg("q", "Substring matched case-insensitively against the material name AND the shader name. Omit to audit everything.")]
            string query = null,
            [CliArg("detail", "Also return a row per material (name, path, colour, texture, variant parent). Off by default: the shader summary alone answers most questions and is far smaller.")]
            bool detail = false,
            [CliArg("scene", "Include open-scene usage per material: how many active renderers reference it and their approximate combined ground footprint. Implies detail.")]
            bool scene = false,
            [CliArg("packages", "Include materials that live outside Assets/ (package and engine defaults). Off by default - they cannot be edited and swamp the project's own.")]
            bool packages = false,
            [CliArg("m", "Maximum rows to return when detail is on.")]
            int max = 60
        )
        {
            if (max < 1)
            {
                max = 1;
            }

            if (max > MAX_ROWS_LIMIT)
            {
                max = MAX_ROWS_LIMIT;
            }

            if (scene)
            {
                detail = true;
            }

            var usageCount = new Dictionary<Material, int>();
            var usageArea = new Dictionary<Material, float>();

            if (scene)
            {
                CollectSceneUsage(usageCount, usageArea);
            }

            var guids = AssetDatabase.FindAssets("t:Material");
            var shaderCounts = new Dictionary<string, int>();
            var rows = new List<Material>();
            var total = 0;
            var skippedPackages = 0;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);

                if (!packages && !path.StartsWith("Assets/"))
                {
                    skippedPackages++;
                    continue;
                }

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);

                if (material == null)
                {
                    continue;
                }

                var shaderName = material.shader == null ? "<null shader>" : material.shader.name;

                if (!Matches(query, Path.GetFileNameWithoutExtension(path), shaderName))
                {
                    continue;
                }

                total++;

                if (!shaderCounts.ContainsKey(shaderName))
                {
                    shaderCounts[shaderName] = 0;
                }

                shaderCounts[shaderName]++;

                if (detail)
                {
                    rows.Add(material);
                }
            }

            var sb = new StringBuilder();
            sb.Append("{\"k\":\"");
            JsonHelper.AppendString(sb, LEGEND);
            sb.Append('"');

            sb.Append(",\"total\":").Append(total);
            sb.Append(",\"skippedPackages\":").Append(skippedPackages);

            sb.Append(",\"s\":[");
            var first = true;

            foreach (var pair in shaderCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append("{\"n\":\"");
                JsonHelper.AppendString(sb, pair.Key);
                sb.Append("\",\"c\":").Append(pair.Value).Append('}');
            }

            sb.Append(']');

            if (detail)
            {
                // Biggest on-screen contributors first when scene usage was asked for,
                // so the materials worth acting on lead. Otherwise alphabetical.
                if (scene)
                {
                    rows = rows
                        .OrderByDescending(m => usageArea.ContainsKey(m) ? usageArea[m] : -1f)
                        .ThenBy(m => m.name)
                        .ToList();
                }
                else
                {
                    rows = rows.OrderBy(m => m.name).ToList();
                }

                var shown = rows.Count < max ? rows.Count : max;
                sb.Append(",\"shown\":").Append(shown);

                if (rows.Count > shown)
                {
                    sb.Append(",\"omitted\":").Append(rows.Count - shown);
                }

                sb.Append(",\"r\":[");

                for (var i = 0; i < shown; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    AppendRow(sb, rows[i], scene, usageCount, usageArea);
                }

                sb.Append(']');
            }

            sb.Append('}');
            return sb.ToString();
        }

        /// Active renderers only: a disabled or inactive renderer contributes nothing on screen, and
        /// counting it makes an unused material look load-bearing. Footprint uses the XZ extent of world
        /// bounds, which is a rough stand-in for screen area that holds up for the overhead and
        /// three-quarter views this is usually asked about.
        private static void CollectSceneUsage(
            Dictionary<Material, int> usageCount,
            Dictionary<Material, float> usageArea)
        {
            var renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);

            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var bounds = renderer.bounds;
                var area = bounds.size.x * bounds.size.z;

                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null)
                    {
                        continue;
                    }

                    if (!usageCount.ContainsKey(material))
                    {
                        usageCount[material] = 0;
                        usageArea[material] = 0f;
                    }

                    usageCount[material]++;
                    usageArea[material] += area;
                }
            }
        }

        private static void AppendRow(
            StringBuilder sb,
            Material material,
            bool scene,
            Dictionary<Material, int> usageCount,
            Dictionary<Material, float> usageArea)
        {
            var path = AssetDatabase.GetAssetPath(material);

            sb.Append("{\"n\":\"");
            JsonHelper.AppendString(sb, Path.GetFileNameWithoutExtension(path));
            sb.Append("\",\"p\":\"");
            JsonHelper.AppendString(sb, path);
            sb.Append("\",\"s\":\"");
            JsonHelper.AppendString(sb, material.shader == null ? "<null shader>" : material.shader.name);
            sb.Append('"');

            sb.Append(",\"c\":");
            var colorProperty = FirstProperty(material, COLOR_PROPERTIES);

            if (colorProperty == null)
            {
                sb.Append("null");
            }
            else
            {
                var color = material.GetColor(colorProperty);
                sb.Append("\"#").Append(ColorUtility.ToHtmlStringRGB(color));

                if (color.a < 0.999f)
                {
                    sb.Append("@").Append(color.a.ToString("0.##"));
                }

                sb.Append('"');
            }

            sb.Append(",\"t\":");
            var textureProperty = FirstProperty(material, TEXTURE_PROPERTIES);
            var texture = textureProperty == null ? null : material.GetTexture(textureProperty);

            if (texture == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('"');
                JsonHelper.AppendString(sb, texture.name);
                sb.Append('"');
            }

            // Variants are worth flagging: Material.shader cannot be assigned on one,
            // and the call throws rather than failing quietly, so a bulk retarget has
            // to route around them via the parent.
            if (material.parent != null)
            {
                sb.Append(",\"v\":\"");
                JsonHelper.AppendString(sb, material.parent.name);
                sb.Append('"');
            }

            if (scene)
            {
                var count = usageCount.ContainsKey(material) ? usageCount[material] : 0;
                var area = usageArea.ContainsKey(material) ? usageArea[material] : 0f;
                sb.Append(",\"u\":").Append(count);
                sb.Append(",\"a\":").Append(area.ToString("0"));
            }

            sb.Append('}');
        }

        private static string FirstProperty(Material material, string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (material.HasProperty(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool Matches(string query, string materialName, string shaderName)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }

            return materialName.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0
                || shaderName.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
