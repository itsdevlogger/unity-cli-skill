using System.Collections.Generic;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace UnityCliMacros
{
    /// Where a type is declared — file, line and signature — for one name or a batch of them.
    ///
    /// This is the half of "find the class" that `m_type_info` cannot do. Reflection knows a type's full
    /// name and assembly, but a compiled type carries no source path, so locating the declaration to
    /// read or edit it otherwise means grepping: one pass to find candidate lines, another to read
    /// enough around each to tell a declaration from a call site or a comment, and a third once
    /// `class Foo` turns out to have matched `where T : class` or a line inside a `///` block.
    ///
    /// Declarations are parsed out of the source instead, so a hit is a hit. Two consequences worth
    /// knowing: it finds types that do not compile — which is exactly when the question gets asked —
    /// and it finds every part of a `partial`, which reflection reports as one type and grep reports as
    /// an unranked pile of lines.
    public static class M_FindType
    {
        /// Ceiling on files opened in one call, so a careless `--root all` cannot walk the package cache
        /// forever. Reported in the output when it bites.
        private const int FILE_CAP = 20000;

        private const int MAX_RESULTS_LIMIT = 50;
        private const int MAX_CODE_LINES = 400;

        private const string LEGEND =
            "r=results, one entry per name asked for; q=the name, d=declarations found for it, " +
            "n=full name (namespace + any outer types), k=kind, f=project-relative file path, " +
            "l=1-based line of the declaration, a=assembly, s=declaration signature including base list, " +
            "c=source text (only when code is not 0). " +
            "Parsed from source, not reflection, so it finds partial halves separately and still works " +
            "while the project is failing to compile. A name that matches something exactly never also " +
            "returns substring matches; substring matching is only the fallback when nothing matches " +
            "exactly. An empty d means the name was not declared under the searched roots.";

        private struct Hit
        {
            public int tier;
            public CSharpHelper.SourceFile file;
            public CSharpHelper.TypeDecl declaration;
        }

        [CliCommand("m_find_type", "Find where types are declared in project source: file, line, namespace, assembly and signature. Accepts several names at once. Use instead of grepping for 'class Foo'.", MainThreadRequired = false)]
        public static string FindType(
            [CliArg("q", "Type names to locate, separated by | . Exact names are preferred; if a name matches nothing exactly, substring matches are returned instead.", Required = true)]
            string query,
            [CliArg("kind", "Only these kinds, separated by | : class, struct, interface, enum, record, delegate. Default is every kind.")]
            string kind = "",
            [CliArg("root", "Where to search, separated by | : Assets, Packages, Library (the package cache), all, or any project-relative folder such as Assets/Scripts.")]
            string root = "Assets",
            [CliArg("asm", "Only files compiling into assemblies whose name contains one of these, separated by | . Resolved from the nearest .asmdef.")]
            string assembly = "",
            [CliArg("code", "0 returns the signature only, -1 returns the whole declaration through its closing brace, N returns the first N lines.")]
            int code = 0,
            [CliArg("m", "Maximum declarations reported per name")]
            int max = 10
        )
        {
            var names = CSharpHelper.SplitList(query);

            if (names.Length == 0)
            {
                return JsonHelper.Err("M_FindType.FindType", "no-query", "the q argument held no names");
            }

            var missingRoots = new List<string>();
            var roots = CSharpHelper.ResolveRoots(root, missingRoots);

            if (roots.Count == 0)
            {
                return JsonHelper.Err("M_FindType.FindType", "no-root",
                    "none of the search roots exist: " + string.Join(", ", missingRoots.ToArray()));
            }

            var kinds = CSharpHelper.SplitList(kind);
            var assemblies = CSharpHelper.SplitList(assembly);

            max = Mathf.Clamp(max, 1, MAX_RESULTS_LIMIT);
            code = Mathf.Clamp(code, -1, MAX_CODE_LINES);

            var hits = new List<Hit>[names.Length];

            for (var i = 0; i < hits.Length; i++)
            {
                hits[i] = new List<Hit>();
            }

            var filesScanned = 0;

            var capped = CSharpHelper.WalkFiles(roots, names, assemblies, FILE_CAP, ref filesScanned, file =>
            {
                foreach (var declaration in CSharpHelper.ParseTypes(file))
                {
                    if (!CSharpHelper.MatchesAny(declaration.kind, kinds))
                    {
                        continue;
                    }

                    for (var i = 0; i < names.Length; i++)
                    {
                        var tier = Tier(declaration.name, names[i]);

                        if (tier > 0)
                        {
                            hits[i].Add(new Hit { tier = tier, file = file, declaration = declaration });
                        }
                    }
                }
            });

            return Format(names, hits, max, code, capped, filesScanned, missingRoots);
        }

        /// 3 exact, 2 exact ignoring case, 1 substring, 0 no match. Keeping the tiers apart is what lets
        /// an exact hit suppress the substring pile: a project with `Health` also has `HealthBar` and
        /// `HealthPickup`, and reporting all three for `--q Health` is the grep behaviour this replaces.
        private static int Tier(string name, string needle)
        {
            if (name == needle)
            {
                return 3;
            }

            if (string.Equals(name, needle, System.StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            return name.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
        }

        private static string Format(string[] names, List<Hit>[] hits, int max, int code, bool capped, int filesScanned, List<string> missingRoots)
        {
            var sb = new StringBuilder();

            sb.Append("{\"k\":\"");
            JsonHelper.AppendProse(sb, LEGEND);
            sb.Append("\",\"r\":[");

            var dropped = 0;

            for (var i = 0; i < names.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"q\":\"");
                JsonHelper.AppendString(sb, names[i]);
                sb.Append("\",\"d\":[");

                var ordered = Best(hits[i]);

                for (var j = 0; j < ordered.Count && j < max; j++)
                {
                    if (j > 0)
                    {
                        sb.Append(',');
                    }

                    AppendDeclaration(sb, ordered[j], code);
                }

                sb.Append("]");

                if (ordered.Count > max)
                {
                    sb.Append(",\"more\":").Append(ordered.Count - max);
                    dropped += ordered.Count - max;
                }

                sb.Append('}');
            }

            sb.Append(']');

            var notes = new List<string>();

            if (dropped > 0)
            {
                notes.Add(dropped + " further declarations not shown - raise m or narrow the query");
            }

            if (capped)
            {
                notes.Add("stopped after " + filesScanned + " files - narrow root");
            }

            if (missingRoots.Count > 0)
            {
                notes.Add("root not found: " + string.Join(", ", missingRoots.ToArray()));
            }

            if (notes.Count > 0)
            {
                sb.Append(",\"note\":\"");
                JsonHelper.AppendProse(sb, string.Join("; ", notes.ToArray()));
                sb.Append('"');
            }

            sb.Append('}');

            return sb.ToString();
        }

        /// Only the strongest tier present, ordered so the shortest-named declaration in the shallowest
        /// file comes first
        private static List<Hit> Best(List<Hit> all)
        {
            var bestTier = 0;

            foreach (var hit in all)
            {
                if (hit.tier > bestTier)
                {
                    bestTier = hit.tier;
                }
            }

            var kept = new List<Hit>();

            foreach (var hit in all)
            {
                if (hit.tier == bestTier)
                {
                    kept.Add(hit);
                }
            }

            kept.Sort((a, b) =>
            {
                var byLength = a.declaration.name.Length.CompareTo(b.declaration.name.Length);

                if (byLength != 0)
                {
                    return byLength;
                }

                var byPath = string.CompareOrdinal(a.file.path, b.file.path);

                return byPath != 0 ? byPath : a.declaration.declStart.CompareTo(b.declaration.declStart);
            });

            return kept;
        }

        private static void AppendDeclaration(StringBuilder sb, Hit hit, int code)
        {
            var declaration = hit.declaration;

            sb.Append("{\"n\":\"");
            JsonHelper.AppendString(sb, declaration.FullName);
            sb.Append("\",\"k\":\"");
            JsonHelper.AppendString(sb, declaration.kind);
            sb.Append("\",\"f\":\"");
            JsonHelper.AppendString(sb, hit.file.path);
            sb.Append("\",\"l\":").Append(hit.file.LineOf(declaration.declStart));
            sb.Append(",\"a\":\"");
            JsonHelper.AppendString(sb, hit.file.assembly);
            sb.Append("\",\"s\":\"");
            JsonHelper.AppendProse(sb, CSharpHelper.Signature(hit.file, declaration.declStart, declaration.headEnd));
            sb.Append('"');

            if (code != 0)
            {
                var end = declaration.bodyEnd < 0 ? declaration.headEnd : declaration.bodyEnd + 1;

                sb.Append(",\"c\":\"");
                JsonHelper.AppendString(sb, CSharpHelper.Snippet(hit.file, declaration.declStart, end, code));
                sb.Append('"');
            }

            sb.Append('}');
        }
    }
}
