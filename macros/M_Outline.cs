using System.Collections.Generic;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace UnityCliMacros
{
    /// The shape of a type — every member it declares, with no bodies.
    ///
    /// Orienting in an unfamiliar class otherwise means reading the file, and a 900-line MonoBehaviour
    /// costs 900 lines to learn that it has eleven members. This answers the same question in a few dozen
    /// lines, and the per-member line count says which of those eleven is a four-line accessor and which
    /// is the state machine — so the decision about what to actually read is made before paying for it.
    ///
    /// `inherited` matters more here than it would elsewhere. A gameplay class is routinely three deep,
    /// and half its usable surface is declared on a base that would otherwise have to be found and
    /// outlined separately. Bases are resolved through source, so this walks as far as the project's own
    /// code goes and stops where the engine begins — `MonoBehaviour` has no source to read.
    public static class M_Outline
    {
        private const int FILE_CAP = 20000;
        private const int MAX_MEMBERS_LIMIT = 300;
        private const int MAX_TYPES_REPORTED = 12;

        /// Levels of base type followed when `inherited` is on. Each level costs one more pass over the
        /// roots, and a hierarchy deeper than this is not a hierarchy anyone is reading an outline of.
        private const int MAX_INHERITANCE_DEPTH = 6;

        private const string LEGEND =
            "r=types outlined; t=type full name, k=kind, f=project-relative file path, l=declaration " +
            "line, a=assembly, b=base list as written (source cannot say which entry is the class and " +
            "which are interfaces, so treat it as unordered), u=file usings, d=members in source order. " +
            "Each member is n=name, k=kind, l=line, s=signature, len=how many lines the member spans " +
            "(1 for a field, and the number worth reading for a method). from=the base type that " +
            "declares it, present only on inherited members. " +
            "No bodies are returned: use m_find_member --code, or read the file at l, once len says the " +
            "member is worth it. Parsed from source, so it works while the project is failing to compile.";

        private struct TypeOutline
        {
            public CSharpHelper.SourceFile file;
            public CSharpHelper.TypeDecl declaration;
            public List<Member> members;
        }

        private struct Member
        {
            public CSharpHelper.SourceFile file;
            public CSharpHelper.MemberDecl declaration;
            public string from;
        }

        [CliCommand("m_outline", "List every member a type declares - kind, name, signature and line - without any bodies. Use before reading a file: it answers 'what is in this class' in 30 lines instead of 900.", MainThreadRequired = false)]
        public static string Outline(
            [CliArg("q", "Type names to outline, separated by | . Omit when passing f.")]
            string query = "",
            [CliArg("f", "Outline every type in this file instead of searching by name. Project-relative or absolute path.")]
            string file = "",
            [CliArg("kind", "Only these member kinds, separated by | : method, property, field, event, constructor, indexer, operator, finalizer, enum. Default is every kind.")]
            string kind = "",
            [CliArg("root", "Where to search, separated by | : Assets, Packages, Library (the package cache), all, or any project-relative folder.")]
            string root = "Assets",
            [CliArg("asm", "Only files compiling into assemblies whose name contains one of these, separated by | .")]
            string assembly = "",
            [CliArg("inherited", "1 also walks base types found in source and lists their members too, each marked with the type that declares it.")]
            int inherited = 0,
            [CliArg("m", "Maximum members listed per type")]
            int max = 60
        )
        {
            var names = CSharpHelper.SplitList(query);
            var hasFile = !string.IsNullOrEmpty(file);

            if (names.Length == 0 && !hasFile)
            {
                return JsonHelper.Err("M_Outline.Outline", "no-target",
                    "pass q with at least one type name, or f with a file path");
            }

            var kinds = CSharpHelper.SplitList(kind);
            var assemblies = CSharpHelper.SplitList(assembly);

            max = Mathf.Clamp(max, 1, MAX_MEMBERS_LIMIT);

            var outlines = new List<TypeOutline>();
            var missingRoots = new List<string>();
            var filesScanned = 0;
            var capped = false;
            List<string> roots = null;

            if (hasFile)
            {
                var error = OutlineOneFile(file, kinds, outlines);

                if (error != null)
                {
                    return error;
                }

                filesScanned = 1;
            }
            else
            {
                roots = CSharpHelper.ResolveRoots(root, missingRoots);

                if (roots.Count == 0)
                {
                    return JsonHelper.Err("M_Outline.Outline", "no-root",
                        "none of the search roots exist: " + string.Join(", ", missingRoots.ToArray()));
                }

                capped = CollectByName(roots, names, assemblies, kinds, outlines, ref filesScanned);
            }

            if (outlines.Count == 0)
            {
                return "NONE";
            }

            if (inherited > 0)
            {
                if (roots == null)
                {
                    roots = CSharpHelper.ResolveRoots(root, missingRoots);
                }

                capped |= AddInherited(roots, assemblies, kinds, outlines, ref filesScanned);
            }

            return Format(outlines, max, capped, filesScanned, missingRoots);
        }

        private static string OutlineOneFile(string path, string[] kinds, List<TypeOutline> outlines)
        {
            var absolute = CSharpHelper.ResolveRoot(path);
            string text;

            if (!CSharpHelper.TryRead(absolute, out text))
            {
                return JsonHelper.Err("M_Outline.OutlineOneFile", "no-file",
                    "could not read " + absolute);
            }

            var source = CSharpHelper.Load(absolute, text);

            foreach (var declaration in CSharpHelper.ParseTypes(source))
            {
                var outline = Build(source, declaration, kinds, null);

                // With a kind filter set, a type contributing nothing is noise: outlining a whole file
                // for its enums should return the enums, not every class in it with an empty member list
                if (kinds.Length == 0 || outline.members.Count > 0)
                {
                    outlines.Add(outline);
                }
            }

            return null;
        }

        /// One pass over the roots, keeping every type whose name matches a query. Exact names win, the
        /// same way `m_find_type` treats them: an outline of the wrong `Health` is worse than no outline.
        private static bool CollectByName(List<string> roots, string[] names, string[] assemblies, string[] kinds, List<TypeOutline> outlines, ref int filesScanned)
        {
            var exact = new List<TypeOutline>();
            var loose = new List<TypeOutline>();

            var capped = CSharpHelper.WalkFiles(roots, names, assemblies, FILE_CAP, ref filesScanned, source =>
            {
                foreach (var declaration in CSharpHelper.ParseTypes(source))
                {
                    foreach (var name in names)
                    {
                        if (string.Equals(declaration.name, name, System.StringComparison.OrdinalIgnoreCase))
                        {
                            exact.Add(Build(source, declaration, kinds, null));
                            break;
                        }

                        if (declaration.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            loose.Add(Build(source, declaration, kinds, null));
                            break;
                        }
                    }
                }
            });

            outlines.AddRange(exact.Count > 0 ? exact : loose);

            return capped;
        }

        /// Follows each outlined type's base list through source, one pass per level, adding the members
        /// it finds. A base that is not declared in the searched roots — anything from the engine or a
        /// precompiled package — simply yields nothing, which is the honest answer here.
        private static bool AddInherited(List<string> roots, string[] assemblies, string[] kinds, List<TypeOutline> outlines, ref int filesScanned)
        {
            var capped = false;

            // Keyed by the outline being extended, so two queried types sharing a base each get its
            // members attributed under their own entry
            var frontier = new List<string>[outlines.Count];
            var seen = new List<string>[outlines.Count];

            for (var i = 0; i < outlines.Count; i++)
            {
                frontier[i] = SimpleNames(outlines[i].declaration.bases);
                seen[i] = new List<string>(frontier[i]);
                seen[i].Add(outlines[i].declaration.name);
            }

            for (var level = 0; level < MAX_INHERITANCE_DEPTH; level++)
            {
                var wanted = new List<string>();

                foreach (var names in frontier)
                {
                    foreach (var name in names)
                    {
                        if (!wanted.Contains(name))
                        {
                            wanted.Add(name);
                        }
                    }
                }

                if (wanted.Count == 0)
                {
                    break;
                }

                var found = new List<CSharpHelper.TypeHit>();

                capped |= CSharpHelper.WalkFiles(roots, wanted.ToArray(), assemblies, FILE_CAP, ref filesScanned, source =>
                {
                    foreach (var declaration in CSharpHelper.ParseTypes(source))
                    {
                        foreach (var name in wanted)
                        {
                            if (string.Equals(declaration.name, name, System.StringComparison.Ordinal))
                            {
                                found.Add(new CSharpHelper.TypeHit { file = source, declaration = declaration });
                                break;
                            }
                        }
                    }
                });

                if (found.Count == 0)
                {
                    break;
                }

                for (var i = 0; i < outlines.Count; i++)
                {
                    var next = new List<string>();

                    foreach (var hit in found)
                    {
                        if (!frontier[i].Contains(hit.declaration.name))
                        {
                            continue;
                        }

                        var outline = outlines[i];

                        foreach (var member in CSharpHelper.ParseMembers(hit.file, hit.declaration))
                        {
                            if (CSharpHelper.MatchesAny(member.kind, kinds))
                            {
                                outline.members.Add(new Member
                                {
                                    file = hit.file,
                                    declaration = member,
                                    from = hit.declaration.FullName
                                });
                            }
                        }

                        outlines[i] = outline;

                        foreach (var name in SimpleNames(hit.declaration.bases))
                        {
                            if (!seen[i].Contains(name) && !next.Contains(name))
                            {
                                next.Add(name);
                                seen[i].Add(name);
                            }
                        }
                    }

                    frontier[i] = next;
                }
            }

            return capped;
        }

        private static List<string> SimpleNames(string[] bases)
        {
            var names = new List<string>();

            if (bases == null)
            {
                return names;
            }

            foreach (var entry in bases)
            {
                var simple = CSharpHelper.SimpleTypeName(entry);

                if (simple.Length > 0 && !names.Contains(simple))
                {
                    names.Add(simple);
                }
            }

            return names;
        }

        private static TypeOutline Build(CSharpHelper.SourceFile source, CSharpHelper.TypeDecl declaration, string[] kinds, string from)
        {
            var members = new List<Member>();

            foreach (var member in CSharpHelper.ParseMembers(source, declaration))
            {
                if (CSharpHelper.MatchesAny(member.kind, kinds))
                {
                    members.Add(new Member { file = source, declaration = member, from = from });
                }
            }

            return new TypeOutline { file = source, declaration = declaration, members = members };
        }

        private static string Format(List<TypeOutline> outlines, int max, bool capped, int filesScanned, List<string> missingRoots)
        {
            var sb = new StringBuilder();

            sb.Append("{\"k\":\"");
            JsonHelper.AppendProse(sb, LEGEND);
            sb.Append("\",\"r\":[");

            var shown = 0;
            var droppedMembers = 0;

            for (var i = 0; i < outlines.Count && shown < MAX_TYPES_REPORTED; i++)
            {
                var outline = outlines[i];

                if (shown > 0)
                {
                    sb.Append(',');
                }

                shown++;

                var declaration = outline.declaration;

                sb.Append("{\"t\":\"");
                JsonHelper.AppendString(sb, declaration.FullName);
                sb.Append("\",\"k\":\"");
                JsonHelper.AppendString(sb, declaration.kind);
                sb.Append("\",\"f\":\"");
                JsonHelper.AppendString(sb, outline.file.path);
                sb.Append("\",\"l\":").Append(outline.file.LineOf(declaration.declStart));
                sb.Append(",\"a\":\"");
                JsonHelper.AppendString(sb, outline.file.assembly);
                sb.Append('"');

                AppendStrings(sb, "b", declaration.bases);
                AppendStrings(sb, "u", CSharpHelper.ParseUsings(outline.file));

                sb.Append(",\"d\":[");

                var members = outline.members;
                members.Sort(CompareMembers);

                for (var j = 0; j < members.Count && j < max; j++)
                {
                    if (j > 0)
                    {
                        sb.Append(',');
                    }

                    AppendMember(sb, members[j]);
                }

                sb.Append(']');

                if (members.Count > max)
                {
                    sb.Append(",\"more\":").Append(members.Count - max);
                    droppedMembers += members.Count - max;
                }

                sb.Append('}');
            }

            sb.Append(']');

            var notes = new List<string>();

            if (outlines.Count > shown)
            {
                notes.Add((outlines.Count - shown) + " further types not outlined - narrow q");
            }

            if (droppedMembers > 0)
            {
                notes.Add(droppedMembers + " further members not shown - raise m or filter with kind");
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

        /// Own members first in source order, then inherited ones grouped by the type declaring them —
        /// which is the order the hierarchy reads in
        private static int CompareMembers(Member a, Member b)
        {
            var aInherited = a.from != null;
            var bInherited = b.from != null;

            if (aInherited != bInherited)
            {
                return aInherited ? 1 : -1;
            }

            if (aInherited)
            {
                var byType = string.CompareOrdinal(a.from, b.from);

                if (byType != 0)
                {
                    return byType;
                }
            }

            return a.declaration.declStart.CompareTo(b.declaration.declStart);
        }

        private static void AppendMember(StringBuilder sb, Member member)
        {
            var declaration = member.declaration;
            var startLine = member.file.LineOf(declaration.declStart);
            var endLine = member.file.LineOf(declaration.declEnd > 0 ? declaration.declEnd - 1 : declaration.declStart);

            sb.Append("{\"n\":\"");
            JsonHelper.AppendString(sb, declaration.name);
            sb.Append("\",\"k\":\"");
            JsonHelper.AppendString(sb, declaration.kind);
            sb.Append("\",\"l\":").Append(startLine);
            sb.Append(",\"s\":\"");
            JsonHelper.AppendProse(sb, CSharpHelper.Signature(member.file, declaration.declStart, declaration.headEnd));
            sb.Append("\",\"len\":").Append(endLine >= startLine ? endLine - startLine + 1 : 1);

            if (member.from != null)
            {
                sb.Append(",\"from\":\"");
                JsonHelper.AppendString(sb, member.from);
                sb.Append('"');
            }

            sb.Append('}');
        }

        private static void AppendStrings(StringBuilder sb, string key, string[] values)
        {
            if (values == null || values.Length == 0)
            {
                return;
            }

            sb.Append(",\"").Append(key).Append("\":[");

            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append('"');
                JsonHelper.AppendString(sb, values[i]);
                sb.Append('"');
            }

            sb.Append(']');
        }
    }
}
