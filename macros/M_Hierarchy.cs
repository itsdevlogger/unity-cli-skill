using System.Collections.Generic;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace UnityCliMacros
{
    /// What derives from a type, and what a type derives from.
    ///
    /// Grep for `: Enemy` and it returns base lists mixed with ternaries, dictionary initialisers and
    /// label declarations — and nothing transitive, so a class extending a class extending `Enemy` stays
    /// invisible. The base list is parsed here instead, and the walk is transitive, which is what makes
    /// the real use possible: the pre-flight check before touching a base class or a virtual signature.
    ///
    /// Bases are matched by simple name through source, so this covers the project's own hierarchy and
    /// stops where the engine's begins. A gap in the chain — `: MonoBehaviour`, or a base from a
    /// precompiled package — is reported as the last link found rather than silently dropped.
    public static class M_Hierarchy
    {
        private const int FILE_CAP = 20000;
        private const int MAX_RESULTS_LIMIT = 200;

        /// Transitive levels walked when `depth` is left at -1. Each level is one more pass over the
        /// roots, and no Unity hierarchy worth auditing is deeper than this.
        private const int MAX_DEPTH = 12;

        private struct Relation
        {
            public CSharpHelper.SourceFile file;
            public CSharpHelper.TypeDecl declaration;
            public string direction;
            public int depth;
            public string via;
        }

        [CliCommand("m_hierarchy", "Find the types that derive from a class or implement an interface, and the chain a type derives from. Parsed from source base lists, so it works on types that do not compile.", MainThreadRequired = false)]
        public static string Hierarchy(
            [CliArg("q", "Type names, separated by | .", Required = true)]
            string query,
            [CliArg("dir", "down (default) finds derived types and implementors, up finds the ancestors the type derives from, both does each.")]
            string direction = "down",
            [CliArg("depth", "Transitive levels to follow. -1 (default) follows all of them, 1 returns only direct relationships.")]
            int depth = -1,
            [CliArg("root", "Where to search, separated by | : Assets, Packages, Library (the package cache), all, or any project-relative folder.")]
            string root = "Assets",
            [CliArg("asm", "Only files compiling into assemblies whose name contains one of these, separated by | .")]
            string assembly = "",
            [CliArg("m", "Maximum types reported per name")]
            int max = 40
        )
        {
            var names = CSharpHelper.SplitList(query);

            if (names.Length == 0)
            {
                return JsonHelper.Err("M_Hierarchy.Hierarchy", "no-query", "the q argument held no names");
            }

            var wanted = direction.ToLowerInvariant();

            if (wanted != "down" && wanted != "up" && wanted != "both")
            {
                return JsonHelper.Err("M_Hierarchy.Hierarchy", "bad-dir",
                    "dir was '" + direction + "'; it takes down, up or both");
            }

            var missingRoots = new List<string>();
            var roots = CSharpHelper.ResolveRoots(root, missingRoots);

            if (roots.Count == 0)
            {
                return JsonHelper.Err("M_Hierarchy.Hierarchy", "no-root",
                    "none of the search roots exist: " + string.Join(", ", missingRoots.ToArray()));
            }

            var assemblies = CSharpHelper.SplitList(assembly);

            max = Mathf.Clamp(max, 1, MAX_RESULTS_LIMIT);
            var levels = depth < 0 ? MAX_DEPTH : Mathf.Clamp(depth, 1, MAX_DEPTH);

            var relations = new List<Relation>[names.Length];

            for (var i = 0; i < relations.Length; i++)
            {
                relations[i] = new List<Relation>();
            }

            var filesScanned = 0;
            var capped = false;

            if (wanted == "down" || wanted == "both")
            {
                capped |= WalkDown(roots, names, assemblies, levels, relations, ref filesScanned);
            }

            if (wanted == "up" || wanted == "both")
            {
                capped |= WalkUp(roots, names, assemblies, levels, relations, ref filesScanned);
            }

            return Format(names, relations, max, capped, filesScanned, missingRoots);
        }

        /// Derived types, level by level. Each pass looks for base lists naming anything found in the
        /// previous one, so a subclass of a subclass arrives at depth 2 with `via` naming the link.
        private static bool WalkDown(List<string> roots, string[] names, string[] assemblies, int levels, List<Relation>[] relations, ref int filesScanned)
        {
            var capped = false;

            // Per queried name: the types whose subclasses the next pass is looking for
            var frontier = new List<string>[names.Length];
            var seen = new List<string>[names.Length];

            for (var i = 0; i < names.Length; i++)
            {
                frontier[i] = new List<string> { names[i] };
                seen[i] = new List<string> { names[i] };
            }

            for (var level = 1; level <= levels; level++)
            {
                var wanted = Union(frontier);

                if (wanted.Count == 0)
                {
                    break;
                }

                var found = new List<CSharpHelper.TypeHit>();

                capped |= CSharpHelper.WalkFiles(roots, wanted.ToArray(), assemblies, FILE_CAP, ref filesScanned, file =>
                {
                    foreach (var declaration in CSharpHelper.ParseTypes(file))
                    {
                        if (declaration.bases == null || declaration.bases.Length == 0)
                        {
                            continue;
                        }

                        found.Add(new CSharpHelper.TypeHit { file = file, declaration = declaration });
                    }
                });

                var advanced = false;

                for (var i = 0; i < names.Length; i++)
                {
                    var next = new List<string>();

                    foreach (var hit in found)
                    {
                        var via = MatchingBase(hit.declaration.bases, frontier[i]);

                        if (via == null || seen[i].Contains(hit.declaration.name))
                        {
                            continue;
                        }

                        relations[i].Add(new Relation
                        {
                            file = hit.file,
                            declaration = hit.declaration,
                            direction = "down",
                            depth = level,

                            // At depth 1 the link is the queried type itself, which says nothing
                            via = level == 1 ? null : via
                        });

                        seen[i].Add(hit.declaration.name);
                        next.Add(hit.declaration.name);
                        advanced = true;
                    }

                    frontier[i] = next;
                }

                if (!advanced)
                {
                    break;
                }
            }

            return capped;
        }

        /// The ancestor chain, level by level, following each type's own base list upwards
        private static bool WalkUp(List<string> roots, string[] names, string[] assemblies, int levels, List<Relation>[] relations, ref int filesScanned)
        {
            var capped = false;

            var frontier = new List<string>[names.Length];
            var seen = new List<string>[names.Length];
            var viaOf = new Dictionary<string, string>[names.Length];

            for (var i = 0; i < names.Length; i++)
            {
                frontier[i] = new List<string> { names[i] };
                seen[i] = new List<string> { names[i] };
                viaOf[i] = new Dictionary<string, string>();
            }

            for (var level = 0; level <= levels; level++)
            {
                var wanted = Union(frontier);

                if (wanted.Count == 0)
                {
                    break;
                }

                var found = new List<CSharpHelper.TypeHit>();

                capped |= CSharpHelper.WalkFiles(roots, wanted.ToArray(), assemblies, FILE_CAP, ref filesScanned, file =>
                {
                    foreach (var declaration in CSharpHelper.ParseTypes(file))
                    {
                        foreach (var name in wanted)
                        {
                            if (string.Equals(declaration.name, name, System.StringComparison.Ordinal))
                            {
                                found.Add(new CSharpHelper.TypeHit { file = file, declaration = declaration });
                                break;
                            }
                        }
                    }
                });

                if (found.Count == 0)
                {
                    break;
                }

                var advanced = false;

                for (var i = 0; i < names.Length; i++)
                {
                    var next = new List<string>();

                    foreach (var hit in found)
                    {
                        if (!frontier[i].Contains(hit.declaration.name))
                        {
                            continue;
                        }

                        // Level 0 resolves the queried type itself, only to read its base list
                        if (level > 0)
                        {
                            string via;
                            viaOf[i].TryGetValue(hit.declaration.name, out via);

                            relations[i].Add(new Relation
                            {
                                file = hit.file,
                                declaration = hit.declaration,
                                direction = "up",
                                depth = level,
                                via = level == 1 ? null : via
                            });

                            advanced = true;
                        }

                        if (hit.declaration.bases == null)
                        {
                            continue;
                        }

                        foreach (var entry in hit.declaration.bases)
                        {
                            var simple = CSharpHelper.SimpleTypeName(entry);

                            if (simple.Length == 0 || seen[i].Contains(simple) || next.Contains(simple))
                            {
                                continue;
                            }

                            next.Add(simple);
                            seen[i].Add(simple);
                            viaOf[i][simple] = hit.declaration.name;
                        }
                    }

                    frontier[i] = next;
                }

                if (level > 0 && !advanced)
                {
                    break;
                }
            }

            return capped;
        }

        /// The first base-list entry whose simple name is one of `candidates`
        private static string MatchingBase(string[] bases, List<string> candidates)
        {
            foreach (var entry in bases)
            {
                var simple = CSharpHelper.SimpleTypeName(entry);

                foreach (var candidate in candidates)
                {
                    if (string.Equals(simple, candidate, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static List<string> Union(List<string>[] frontiers)
        {
            var all = new List<string>();

            foreach (var frontier in frontiers)
            {
                foreach (var name in frontier)
                {
                    if (!all.Contains(name))
                    {
                        all.Add(name);
                    }
                }
            }

            return all;
        }

        private static string Format(string[] names, List<Relation>[] relations, int max, bool capped, int filesScanned, List<string> missingRoots)
        {
            var sb = new FlatBuilder();
            var dropped = 0;

            for (var i = 0; i < names.Length; i++)
            {
                var ordered = relations[i];
                ordered.Sort(Compare);

                if (ordered.Count == 0)
                {
                    sb.Line(0, FlatHelper.Handle(names[i]) + "  nothing derives from it and it derives from nothing in source");
                    continue;
                }

                var shown = ordered.Count < max ? ordered.Count : max;

                // Split the two directions into their own headed sections rather than tagging every
                // row with dir, which was the same word repeated down the whole list.
                AppendSection(sb, names[i], ordered, shown, "up");
                AppendSection(sb, names[i], ordered, shown, "down");

                if (ordered.Count > max)
                {
                    dropped += ordered.Count - max;
                }
            }

            var notes = new List<string>();

            if (dropped > 0)
            {
                notes.Add(dropped + " further types not shown - raise m or lower depth");
            }

            if (capped)
            {
                notes.Add("stopped after " + filesScanned + " files - narrow root");
            }

            if (missingRoots.Count > 0)
            {
                notes.Add("root not found: " + string.Join(", ", missingRoots.ToArray()));
            }

            FlatHelper.AppendNotes(sb, notes);

            return sb.ToString();
        }

        /// One direction's worth of relations, if there are any.
        ///
        /// Depth is rendered as indentation rather than as a number, which also retires `via`: a type
        /// sitting one level in under another IS the intermediate relationship that via named, said
        /// structurally instead of as a cross-reference the reader has to resolve.
        private static void AppendSection(FlatBuilder sb, string query, List<Relation> ordered, int shown, string direction)
        {
            var count = 0;

            for (var i = 0; i < shown; i++)
            {
                if (ordered[i].direction == direction)
                {
                    count++;
                }
            }

            if (count == 0)
            {
                return;
            }

            sb.Line(0, FlatHelper.Handle(query) + "  "
                       + (direction == "up" ? "derives from " + count : FlatHelper.Count(count, "subtype")));

            for (var i = 0; i < shown; i++)
            {
                if (ordered[i].direction == direction)
                {
                    AppendRelation(sb, ordered[i]);
                }
            }
        }

        /// Ancestors before descendants, then nearest first — the order the chain reads in
        private static int Compare(Relation a, Relation b)
        {
            if (a.direction != b.direction)
            {
                return a.direction == "up" ? -1 : 1;
            }

            if (a.depth != b.depth)
            {
                return a.depth.CompareTo(b.depth);
            }

            return string.CompareOrdinal(a.declaration.name, b.declaration.name);
        }

        private static void AppendRelation(FlatBuilder sb, Relation relation)
        {
            var declaration = relation.declaration;

            // Depth 1 sits at indent 1, so the queried type is the implicit root of the tree. The
            // base list is dropped: for a down relation it necessarily contains the query, and for
            // an up relation the next line of the chain already says it.
            sb.Line(relation.depth, declaration.FullName + "  " + declaration.kind
                                    + "  " + relation.file.assembly
                                    + "  " + FlatHelper.At(relation.file.path, relation.file.LineOf(declaration.declStart)));
        }

    }
}
