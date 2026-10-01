using System.Collections.Generic;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace UnityCliMacros
{
    /// Where a method, property, field, event or enum value is declared — file, line, declaring type and
    /// full signature — for one name or a batch of them.
    ///
    /// The member half of the question `m_find_type` answers for types, and the one grep is worst at:
    /// `grep "Damage"` returns every call site, every comment and every local variable alongside the one
    /// declaration wanted, and no pattern distinguishes `void Damage(int a)` from `enemy.Damage(5)`
    /// without reading the surrounding lines.
    ///
    /// Here the distinction is structural rather than textual. A member is only reported when it sits at
    /// its type body's own brace depth, so a local inside a method is never a field and a call is never
    /// a method — and the declaring type comes back with it, which is usually the actual question.
    public static class M_FindMember
    {
        private const int FILE_CAP = 20000;
        private const int MAX_RESULTS_LIMIT = 50;
        private const int MAX_CODE_LINES = 400;

        private struct Hit
        {
            public int tier;
            public CSharpHelper.SourceFile file;
            public CSharpHelper.TypeDecl owner;
            public CSharpHelper.MemberDecl member;
        }

        [CliCommand("m_find_member", "Find where methods, properties, fields, events or enum values are declared: file, line, declaring type and full signature. Accepts several names at once. Use instead of grepping for a member name, which also returns every call site.", MainThreadRequired = false)]
        public static string FindMember(
            [CliArg("q", "Member names to locate, separated by | . Exact names are preferred; if a name matches nothing exactly, substring matches are returned instead.", Required = true)]
            string query,
            [CliArg("kind", "Only these kinds, separated by | : method, property, field, event, constructor, indexer, operator, finalizer, enum. Default is every kind.")]
            string kind = "",
            [CliArg("type", "Only members declared by types whose name contains one of these, separated by | . Use it when several classes declare the same member name.")]
            string type = "",
            [CliArg("root", "Where to search, separated by | : Assets, Packages, Library (the package cache), all, or any project-relative folder such as Assets/Scripts.")]
            string root = "Assets",
            [CliArg("asm", "Only files compiling into assemblies whose name contains one of these, separated by | . Resolved from the nearest .asmdef.")]
            string assembly = "",
            [CliArg("code", "0 returns the signature only, -1 returns the whole member through its closing brace, N returns the first N lines.")]
            int code = 0,
            [CliArg("m", "Maximum declarations reported per name")]
            int max = 10
        )
        {
            var names = CSharpHelper.SplitList(query);

            if (names.Length == 0)
            {
                return JsonHelper.Err("M_FindMember.FindMember", "no-query", "the q argument held no names");
            }

            var missingRoots = new List<string>();
            var roots = CSharpHelper.ResolveRoots(root, missingRoots);

            if (roots.Count == 0)
            {
                return JsonHelper.Err("M_FindMember.FindMember", "no-root",
                    "none of the search roots exist: " + string.Join(", ", missingRoots.ToArray()));
            }

            var kinds = CSharpHelper.SplitList(kind);
            var owners = CSharpHelper.SplitList(type);
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
                foreach (var owner in CSharpHelper.ParseTypes(file))
                {
                    if (!CSharpHelper.MatchesAny(owner.name, owners))
                    {
                        continue;
                    }

                    foreach (var member in CSharpHelper.ParseMembers(file, owner))
                    {
                        if (!CSharpHelper.MatchesAny(member.kind, kinds))
                        {
                            continue;
                        }

                        for (var i = 0; i < names.Length; i++)
                        {
                            var tier = Tier(member.name, names[i]);

                            if (tier > 0)
                            {
                                hits[i].Add(new Hit
                                {
                                    tier = tier,
                                    file = file,
                                    owner = owner,
                                    member = member
                                });
                            }
                        }
                    }
                }
            });

            return Format(names, hits, max, code, capped, filesScanned, missingRoots, root);
        }

        /// 3 exact, 2 exact ignoring case, 1 substring, 0 no match. A member name is usually typed from
        /// memory of the call site, so case-insensitive still has to hit; the substring tier is a last
        /// resort and is dropped whenever anything matched properly.
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

        private static string Format(string[] names, List<Hit>[] hits, int max, int code, bool capped, int filesScanned, List<string> missingRoots, string root)
        {
            var sb = new FlatBuilder();
            var dropped = 0;

            for (var i = 0; i < names.Length; i++)
            {
                var ordered = Best(hits[i]);

                if (ordered.Count == 0)
                {
                    sb.Line(0, FlatHelper.Handle(names[i]) + "  not declared by any type under " + root);
                    continue;
                }

                var shown = ordered.Count < max ? ordered.Count : max;

                sb.Line(0, FlatHelper.Handle(names[i]) + "  " + FlatHelper.Count(shown, "declaration"));

                for (var j = 0; j < shown; j++)
                {
                    AppendMember(sb, ordered[j], code);
                }

                if (ordered.Count > max)
                {
                    dropped += ordered.Count - max;
                }
            }

            var notes = new List<string>();

            if (dropped > 0)
            {
                notes.Add(dropped + " further declarations not shown - raise m, or narrow with type or kind");
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

        /// Only the strongest tier present. Overrides of one method across a hierarchy all score the
        /// same, so they are ordered by file to keep a base class and its subclasses adjacent.
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
                var byLength = a.member.name.Length.CompareTo(b.member.name.Length);

                if (byLength != 0)
                {
                    return byLength;
                }

                var byPath = string.CompareOrdinal(a.file.path, b.file.path);

                return byPath != 0 ? byPath : a.member.declStart.CompareTo(b.member.declStart);
            });

            return kept;
        }

        /// Signature, then where it lives. The kind is restated on the location line rather than left
        /// to the signature, because that is the one thing a signature genuinely cannot settle - a
        /// property and a field with an initialiser read almost identically.
        private static void AppendMember(FlatBuilder sb, Hit hit, int code)
        {
            var member = hit.member;

            sb.Line(1, FlatHelper.Text(CSharpHelper.Signature(hit.file, member.declStart, member.headEnd)));
            sb.Line(2, member.kind + " in " + hit.owner.FullName + "  " + hit.file.assembly
                       + "  " + FlatHelper.At(hit.file.path, hit.file.LineOf(member.declStart)));

            if (code != 0)
            {
                sb.Block(3, CSharpHelper.Snippet(hit.file, member.declStart, member.declEnd, code));
            }
        }
    }
}
