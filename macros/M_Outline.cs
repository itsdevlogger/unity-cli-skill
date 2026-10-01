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
            var sb = new FlatBuilder();

            if (outlines.Count == 0)
            {
                return "no matching type declared under the searched roots";
            }

            var shown = 0;
            var droppedMembers = 0;

            for (var i = 0; i < outlines.Count && shown < MAX_TYPES_REPORTED; i++)
            {
                var outline = outlines[i];
                shown++;

                var declaration = outline.declaration;
                var head = declaration.FullName + "  " + declaration.kind;

                if (declaration.bases != null && declaration.bases.Length > 0)
                {
                    head += " : " + string.Join(", ", declaration.bases);
                }

                sb.Line(0, head);
                sb.Line(1, outline.file.assembly + "  "
                           + FlatHelper.At(outline.file.path, outline.file.LineOf(declaration.declStart)));

                var usings = CSharpHelper.ParseUsings(outline.file);

                if (usings != null && usings.Length > 0)
                {
                    sb.Line(1, "usings: " + string.Join(", ", usings));
                }

                var members = outline.members;
                members.Sort(CompareMembers);

                var limit = members.Count < max ? members.Count : max;

                // Inherited members become their own headed section rather than carrying a `from`
                // tag per row. That keeps the signature last on its line - it is the one field here
                // that contains spaces - and it reads as what it is: a second type's surface.
                string section = null;

                for (var j = 0; j < limit; j++)
                {
                    var member = members[j];
                    var wanted = member.from == null
                        ? "members (" + CountOwn(members, limit) + ")"
                        : "inherited from " + member.from + " (" + CountFrom(members, limit, member.from) + ")";

                    if (section != wanted)
                    {
                        sb.Line(1, wanted);
                        section = wanted;
                    }

                    AppendMember(sb, member);
                }

                if (members.Count > max)
                {
                    droppedMembers += members.Count - max;
                }
            }

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

            FlatHelper.AppendNotes(sb, notes);

            return sb.ToString();
        }

        private static int CountOwn(List<Member> members, int limit)
        {
            var n = 0;

            for (var i = 0; i < limit; i++)
            {
                if (members[i].from == null)
                {
                    n++;
                }
            }

            return n;
        }

        private static int CountFrom(List<Member> members, int limit, string from)
        {
            var n = 0;

            for (var i = 0; i < limit; i++)
            {
                if (members[i].from == from)
                {
                    n++;
                }
            }

            return n;
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

        /// line, span, kind, then the signature last because it is the field carrying spaces.
        ///
        /// The span is the column that earns this macro its keep: it says which member is the
        /// four-line accessor and which is the 200-line state machine, so the reader picks what to
        /// open before paying to read it.
        private static void AppendMember(FlatBuilder sb, Member member)
        {
            var declaration = member.declaration;
            var startLine = member.file.LineOf(declaration.declStart);
            var endLine = member.file.LineOf(declaration.declEnd > 0 ? declaration.declEnd - 1 : declaration.declStart);
            var span = endLine >= startLine ? endLine - startLine + 1 : 1;

            sb.Line(2, startLine.ToString().PadLeft(5) + span.ToString().PadLeft(5) + "  "
                       + declaration.kind.PadRight(11) + " "
                       + FlatHelper.Text(CSharpHelper.Signature(member.file, declaration.declStart, declaration.headEnd)));
        }

    }
}
