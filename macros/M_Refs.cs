using System.Collections.Generic;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace UnityCliMacros
{
    /// Where a name is *used* — the inverse of `m_find_type` and `m_find_member`.
    ///
    /// Grep answers this badly, and not mainly because of comments: masking already handles those. It
    /// answers it badly because a flat list of line numbers has no structure. What the question actually
    /// wants is "`TakeDamage` is called from four places: `Bullet.OnHit`, `Explosion.Tick`,
    /// `EnemyAI.Attack`, and a test" — so every hit here is attributed to the member containing it, which
    /// turns a forty-line dump into four facts.
    ///
    /// It also looks where no code search can. A method reached only through a UnityEvent on a prefab has
    /// no caller in source at all, so every code-only search calls it dead; deleting it breaks a scene and
    /// nothing reports the break. Scene, prefab and asset YAML is scanned for the name alongside the
    /// source, and those hits come back as kind `yaml`.
    ///
    /// **This is name-based, not semantic.** There is no overload resolution and no type inference, so two
    /// unrelated classes each declaring `Reset` both answer to `--q Reset`. That is a very good
    /// approximation rather than a compiler's answer, and `type` and `asm` are the way to narrow it.
    public static class M_Refs
    {
        private const int FILE_CAP = 20000;
        private const int SERIALIZED_FILE_CAP = 4000;
        private const int MAX_RESULTS_LIMIT = 200;
        private const int MAX_CONTEXT_LINES = 20;
        private const int MAX_SERIALIZED_HITS = 200;

        /// How far back a receiver chain is read. `a.b.c.Name` is as deep as this needs to see to report
        /// something useful about what the name was called on.
        private const int RECEIVER_WINDOW = 80;

        private const string LEGEND =
            "r=results, one entry per name asked for; q=the name, u=uses found for it. " +
            "Per use: f=project-relative file, l=1-based line, t=type containing the use, " +
            "in=member containing the use (the one to read first), k=how the name is used, " +
            "o=the receiver written before it when there is one, s=the use's line collapsed, a=assembly, " +
            "c=surrounding source (only when ctx is not 0). " +
            "k is one of call, read, write, new, type, inherit, decl, yaml. " +
            "NAME-BASED, NOT SEMANTIC: no overload resolution and no type inference, so two unrelated " +
            "types declaring the same member name both answer here - narrow with type or asm. k is " +
            "decided from surrounding punctuation and is a good approximation, not a compiler's answer; " +
            "read is the catch-all. " +
            "k=yaml is a scene, prefab or asset wiring the name by text, with key=the YAML key that " +
            "names it (UnityEvent m_MethodName, AnimationEvent functionName, UnityEvent " +
            "m_TargetAssemblyTypeName) and n=how many times that file wires it, l being the first. " +
            "A member reached only this way has no caller in source and is NOT dead code. " +
            "Declarations are excluded unless decl=1, and come back as k=decl.";

        private struct Use
        {
            public CSharpHelper.SourceFile file;
            public int offset;
            public string kind;
            public string owner;
            public string member;
            public string receiver;

            /// Set only for a hit from serialized YAML, which has no parsed file behind it
            public string path;
            public int line;
            public string key;
            public int count;
        }

        [CliCommand("m_refs", "Find where a type or member is used: file, line, the member containing the use, and how it is used. The inverse of m_find_type and m_find_member. Use instead of grepping for a name, which cannot tell a use from a declaration or say which method it sits in.", MainThreadRequired = false)]
        public static string Refs(
            [CliArg("q", "Names to find uses of, separated by | .", Required = true)]
            string query,
            [CliArg("type", "Narrow to uses written against a matching receiver, as in Enemy.Reset() or _enemy.Reset() for 'enemy'. A use with no receiver is kept when the type containing it matches instead. Several with | .")]
            string type = "",
            [CliArg("kind", "Only these use kinds, separated by | : call, read, write, new, type, inherit, decl, yaml.")]
            string kind = "",
            [CliArg("root", "Where to search, separated by | : Assets, Packages, Library (the package cache), all, or any project-relative folder.")]
            string root = "Assets",
            [CliArg("asm", "Only files compiling into assemblies whose name contains one of these, separated by | .")]
            string assembly = "",
            [CliArg("decl", "1 also reports the declaration itself, which is excluded by default.")]
            int declarations = 0,
            [CliArg("yaml", "1 (default) also scans scene, prefab and asset YAML for the name, which is the only way a UnityEvent-wired member is visible. 0 skips it.")]
            int yaml = 1,
            [CliArg("ctx", "Lines of surrounding source per hit. 0 returns just the use's own line as the signature.")]
            int context = 0,
            [CliArg("m", "Maximum uses reported per name")]
            int max = 25
        )
        {
            var names = CSharpHelper.SplitList(query);

            if (names.Length == 0)
            {
                return JsonHelper.Err("M_Refs.Refs", "no-query", "the q argument held no names");
            }

            var missingRoots = new List<string>();
            var roots = CSharpHelper.ResolveRoots(root, missingRoots);

            if (roots.Count == 0)
            {
                return JsonHelper.Err("M_Refs.Refs", "no-root",
                    "none of the search roots exist: " + string.Join(", ", missingRoots.ToArray()));
            }

            var kinds = CSharpHelper.SplitList(kind);
            var receivers = CSharpHelper.SplitList(type);
            var assemblies = CSharpHelper.SplitList(assembly);

            max = Mathf.Clamp(max, 1, MAX_RESULTS_LIMIT);
            context = Mathf.Clamp(context, 0, MAX_CONTEXT_LINES);

            var uses = new List<Use>[names.Length];

            for (var i = 0; i < uses.Length; i++)
            {
                uses[i] = new List<Use>();
            }

            var filesScanned = 0;

            var capped = CSharpHelper.WalkFiles(roots, names, assemblies, FILE_CAP, ref filesScanned, file =>
            {
                Scan(file, names, kinds, receivers, declarations > 0, uses);
            });

            var serializedScanned = 0;

            if (yaml > 0 && CSharpHelper.MatchesAny("yaml", kinds))
            {
                var hits = new List<CSharpHelper.SerializedHit>();

                capped |= CSharpHelper.ScanSerialized(roots, names, SERIALIZED_FILE_CAP, ref serializedScanned, hits, MAX_SERIALIZED_HITS);

                foreach (var hit in hits)
                {
                    for (var i = 0; i < names.Length; i++)
                    {
                        if (!string.Equals(hit.value, names[i], System.StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(CSharpHelper.SimpleTypeName(hit.value), names[i], System.StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        AddOrCountYaml(uses[i], hit);
                    }
                }
            }

            return Format(names, uses, max, context, capped, filesScanned + serializedScanned, missingRoots);
        }

        /// Collapses repeated wiring of the same name in the same file into one row with a count.
        ///
        /// A prefab wiring one method from eleven buttons is one fact, not eleven, and a line number
        /// inside a 130,000-line prefab is not something anyone is going to act on — the file and the
        /// count are. The first line is kept so the wiring can still be found by hand if needed.
        private static void AddOrCountYaml(List<Use> uses, CSharpHelper.SerializedHit hit)
        {
            for (var i = 0; i < uses.Count; i++)
            {
                var existing = uses[i];

                if (existing.kind != "yaml" || existing.path != hit.path || existing.key != hit.key)
                {
                    continue;
                }

                existing.count++;
                uses[i] = existing;
                return;
            }

            uses.Add(new Use
            {
                kind = "yaml",
                path = hit.path,
                line = hit.line,
                key = hit.key,
                count = 1,
                owner = string.Empty,
                member = string.Empty,
                receiver = null
            });
        }

        /// Every whole-identifier occurrence in one file, attributed to the type and member containing it
        private static void Scan(CSharpHelper.SourceFile file, string[] names, string[] kinds, string[] receivers, bool includeDeclarations, List<Use>[] uses)
        {
            var types = CSharpHelper.ParseTypes(file);

            // Built once per file: every member span, so an offset can be resolved to its enclosing
            // member by containment instead of re-parsing per hit
            var owners = new List<CSharpHelper.TypeDecl>();
            var members = new List<CSharpHelper.MemberDecl>();
            var memberOwners = new List<int>();

            for (var t = 0; t < types.Count; t++)
            {
                owners.Add(types[t]);

                foreach (var member in CSharpHelper.ParseMembers(file, types[t]))
                {
                    members.Add(member);
                    memberOwners.Add(t);
                }
            }

            for (var i = 0; i < names.Length; i++)
            {
                foreach (var offset in CSharpHelper.Occurrences(file.masked, names[i]))
                {
                    var isDeclaration = IsDeclaration(offset, types, members);

                    if (!includeDeclarations && isDeclaration)
                    {
                        continue;
                    }

                    var receiver = (string)null;
                    string useKind;

                    // A declaration is not classified by the punctuation around it: `void LeafOnly()` is
                    // followed by a paren like every call is, and reporting it as one defeats the point
                    // of having asked for declarations separately
                    if (isDeclaration)
                    {
                        useKind = "decl";
                    }
                    else
                    {
                        useKind = Classify(file.masked, offset, names[i], types, out receiver);
                    }

                    if (!CSharpHelper.MatchesAny(useKind, kinds))
                    {
                        continue;
                    }

                    var memberIndex = InnermostMember(offset, members);
                    var ownerName = string.Empty;
                    var memberName = string.Empty;

                    if (memberIndex >= 0)
                    {
                        ownerName = owners[memberOwners[memberIndex]].FullName;
                        memberName = members[memberIndex].name;
                    }
                    else
                    {
                        var ownerIndex = InnermostType(offset, types);

                        if (ownerIndex >= 0)
                        {
                            ownerName = types[ownerIndex].FullName;
                        }
                    }

                    if (!KeepForReceiver(receivers, receiver, ownerName))
                    {
                        continue;
                    }

                    uses[i].Add(new Use
                    {
                        file = file,
                        offset = offset,
                        kind = useKind,
                        owner = ownerName,
                        member = memberName,
                        receiver = receiver
                    });
                }
            }
        }

        /// True when this occurrence *is* the declaration's own name. Comparing offsets rather than
        /// excluding whole declaration heads matters: `void Spawn(Enemy e)` holds a declaration of
        /// `Spawn` and a genuine use of `Enemy` in the same head.
        private static bool IsDeclaration(int offset, List<CSharpHelper.TypeDecl> types, List<CSharpHelper.MemberDecl> members)
        {
            foreach (var type in types)
            {
                if (type.nameOffset == offset)
                {
                    return true;
                }
            }

            foreach (var member in members)
            {
                if (member.nameOffset == offset)
                {
                    return true;
                }
            }

            return false;
        }

        /// How the name appears, from the punctuation around it. Strong signals only — anything that is
        /// not clearly one of the others is reported as `read` rather than guessed at.
        private static string Classify(string masked, int offset, string name, List<CSharpHelper.TypeDecl> types, out string receiver)
        {
            receiver = ReadReceiver(masked, offset);

            // A base list is the one case with an unambiguous structural answer rather than a textual one
            foreach (var type in types)
            {
                if (offset <= type.nameOffset || offset >= type.headEnd || type.bases == null)
                {
                    continue;
                }

                foreach (var entry in type.bases)
                {
                    if (string.Equals(CSharpHelper.SimpleTypeName(entry), name, System.StringComparison.Ordinal))
                    {
                        return "inherit";
                    }
                }
            }

            var before = PreviousWord(masked, offset);

            if (before == "new")
            {
                return "new";
            }

            var after = offset + name.Length;
            var afterGenerics = SkipGenericArguments(masked, after);
            var next = SkipSpace(masked, afterGenerics);

            if (next < masked.Length && masked[next] == '(')
            {
                return "call";
            }

            if (before == "typeof" || before == "is" || before == "as")
            {
                return "type";
            }

            if (IsWrite(masked, next))
            {
                return "write";
            }

            if (IsIncrementOrDecrement(masked, next) || IsIncrementOrDecrement(masked, PreviousNonSpaceIndex(masked, offset - 1) - 1))
            {
                return "write";
            }

            // `Enemy e` and `Enemy[] all` are declarations of something else, which makes this a use of
            // the name as a type
            if (next < masked.Length && (masked[next] == '_' || char.IsLetter(masked[next])) && next > after)
            {
                return "type";
            }

            if (next < masked.Length && masked[next] == '[' && next + 1 < masked.Length)
            {
                var close = SkipSpace(masked, next + 1);

                if (close < masked.Length && (masked[close] == ']' || masked[close] == ','))
                {
                    return "type";
                }
            }

            if (afterGenerics > after)
            {
                return "type";
            }

            return "read";
        }

        /// The qualifier written immediately before the name: the `enemy` of `enemy.Reset()`. Null when
        /// the name stands on its own.
        private static string ReadReceiver(string masked, int offset)
        {
            var cursor = PreviousNonSpaceIndex(masked, offset - 1);

            if (cursor < 0 || masked[cursor] != '.')
            {
                return null;
            }

            cursor = PreviousNonSpaceIndex(masked, cursor - 1);

            if (cursor < 0)
            {
                return null;
            }

            // A `)` or `]` before the dot means the receiver is an expression, not a name worth quoting
            if (!CSharpHelper.IsIdentifierChar(masked[cursor]))
            {
                return null;
            }

            var end = cursor + 1;
            var limit = offset - RECEIVER_WINDOW;

            while (cursor >= 0 && cursor > limit && CSharpHelper.IsIdentifierChar(masked[cursor]))
            {
                cursor--;
            }

            return masked.Substring(cursor + 1, end - cursor - 1);
        }

        /// With no filter everything is kept. With one, a use qualified by a matching receiver is kept,
        /// and an unqualified use is kept when the type it sits in matches instead — which is how a
        /// class's calls to its own member survive the filter.
        private static bool KeepForReceiver(string[] receivers, string receiver, string ownerName)
        {
            if (receivers.Length == 0)
            {
                return true;
            }

            if (receiver != null)
            {
                return CSharpHelper.MatchesAny(receiver, receivers);
            }

            return !string.IsNullOrEmpty(ownerName) && CSharpHelper.MatchesAny(ownerName, receivers);
        }

        private static bool IsWrite(string masked, int index)
        {
            if (index >= masked.Length)
            {
                return false;
            }

            var c = masked[index];

            if (c == '=')
            {
                // `==` is a comparison and `=>` is a lambda or an expression body
                var next = index + 1 < masked.Length ? masked[index + 1] : '\0';
                return next != '=' && next != '>';
            }

            if (c != '+' && c != '-' && c != '*' && c != '/' && c != '|' && c != '&' && c != '^' && c != '%' && c != '?')
            {
                return false;
            }

            // `+=` and friends, and `??=`
            for (var i = index; i < masked.Length && i < index + 3; i++)
            {
                if (masked[i] == '=')
                {
                    return i + 1 >= masked.Length || masked[i + 1] != '=';
                }

                if (masked[i] != c)
                {
                    return false;
                }
            }

            return false;
        }

        private static bool IsIncrementOrDecrement(string masked, int index)
        {
            if (index < 0 || index + 1 >= masked.Length)
            {
                return false;
            }

            return masked[index] == masked[index + 1] && (masked[index] == '+' || masked[index] == '-');
        }

        private static int SkipGenericArguments(string masked, int cursor)
        {
            var probe = SkipSpace(masked, cursor);

            if (probe >= masked.Length || masked[probe] != '<')
            {
                return cursor;
            }

            var depth = 0;

            for (var i = probe; i < masked.Length; i++)
            {
                var c = masked[i];

                if (c == '<')
                {
                    depth++;
                    continue;
                }

                if (c == '>')
                {
                    depth--;

                    if (depth == 0)
                    {
                        return i + 1;
                    }

                    continue;
                }

                if (c == ';' || c == '{' || c == '}' || c == '(' || c == ')' || c == '=')
                {
                    return cursor;
                }
            }

            return cursor;
        }

        private static int SkipSpace(string text, int cursor)
        {
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
            }

            return cursor;
        }

        private static int PreviousNonSpaceIndex(string text, int index)
        {
            for (var i = index; i >= 0; i--)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string PreviousWord(string masked, int offset)
        {
            var end = PreviousNonSpaceIndex(masked, offset - 1);

            if (end < 0 || !CSharpHelper.IsIdentifierChar(masked[end]))
            {
                return string.Empty;
            }

            var start = end;

            while (start >= 0 && CSharpHelper.IsIdentifierChar(masked[start]))
            {
                start--;
            }

            return masked.Substring(start + 1, end - start);
        }

        /// The smallest member span containing the offset, so a use inside a nested type's method is
        /// attributed to that method rather than to whatever encloses it
        private static int InnermostMember(int offset, List<CSharpHelper.MemberDecl> members)
        {
            var best = -1;
            var bestSpan = int.MaxValue;

            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];

                if (offset < member.declStart || offset >= member.declEnd)
                {
                    continue;
                }

                var span = member.declEnd - member.declStart;

                if (span < bestSpan)
                {
                    bestSpan = span;
                    best = i;
                }
            }

            return best;
        }

        private static int InnermostType(int offset, List<CSharpHelper.TypeDecl> types)
        {
            var best = -1;
            var bestSpan = int.MaxValue;

            for (var i = 0; i < types.Count; i++)
            {
                var type = types[i];

                if (type.bodyStart < 0 || offset < type.declStart || offset > type.bodyEnd)
                {
                    continue;
                }

                var span = type.bodyEnd - type.declStart;

                if (span < bestSpan)
                {
                    bestSpan = span;
                    best = i;
                }
            }

            return best;
        }

        private static string Format(string[] names, List<Use>[] uses, int max, int context, bool capped, int filesScanned, List<string> missingRoots)
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
                sb.Append("\",\"u\":[");

                var ordered = uses[i];
                ordered.Sort(Compare);

                for (var j = 0; j < ordered.Count && j < max; j++)
                {
                    if (j > 0)
                    {
                        sb.Append(',');
                    }

                    AppendUse(sb, ordered[j], context);
                }

                sb.Append(']');

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
                notes.Add(dropped + " further uses not shown - raise m, or narrow with type, kind or asm");
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

        /// Wiring first — it is the finding a code-only search would have missed — then by file so the
        /// uses in one place stay together
        private static int Compare(Use a, Use b)
        {
            if ((a.kind == "yaml") != (b.kind == "yaml"))
            {
                return a.kind == "yaml" ? -1 : 1;
            }

            var byPath = string.CompareOrdinal(PathOf(a), PathOf(b));

            return byPath != 0 ? byPath : LineOf(a).CompareTo(LineOf(b));
        }

        private static string PathOf(Use use)
        {
            return use.file == null ? use.path : use.file.path;
        }

        private static int LineOf(Use use)
        {
            return use.file == null ? use.line : use.file.LineOf(use.offset);
        }

        private static void AppendUse(StringBuilder sb, Use use, int context)
        {
            sb.Append("{\"f\":\"");
            JsonHelper.AppendString(sb, PathOf(use));
            sb.Append("\",\"l\":").Append(LineOf(use));
            sb.Append(",\"k\":\"");
            JsonHelper.AppendString(sb, use.kind);
            sb.Append('"');

            if (!string.IsNullOrEmpty(use.owner))
            {
                sb.Append(",\"t\":\"");
                JsonHelper.AppendString(sb, use.owner);
                sb.Append('"');
            }

            if (!string.IsNullOrEmpty(use.member))
            {
                sb.Append(",\"in\":\"");
                JsonHelper.AppendString(sb, use.member);
                sb.Append('"');
            }

            if (!string.IsNullOrEmpty(use.key))
            {
                sb.Append(",\"key\":\"");
                JsonHelper.AppendString(sb, use.key);
                sb.Append('"');
            }

            if (use.count > 1)
            {
                sb.Append(",\"n\":").Append(use.count);
            }

            if (!string.IsNullOrEmpty(use.receiver))
            {
                sb.Append(",\"o\":\"");
                JsonHelper.AppendString(sb, use.receiver);
                sb.Append('"');
            }

            if (use.file != null)
            {
                sb.Append(",\"a\":\"");
                JsonHelper.AppendString(sb, use.file.assembly);
                sb.Append("\",\"s\":\"");
                JsonHelper.AppendProse(sb, CSharpHelper.Snippet(use.file, use.offset, use.offset, 1).Trim());
                sb.Append('"');

                if (context > 0)
                {
                    sb.Append(",\"c\":\"");
                    JsonHelper.AppendString(sb, ContextAround(use.file, use.offset, context));
                    sb.Append('"');
                }
            }

            sb.Append('}');
        }

        /// `context` lines either side of the use, so a call's arguments and the branch it sits in are
        /// both visible without reading the file
        private static string ContextAround(CSharpHelper.SourceFile file, int offset, int context)
        {
            // The first hop lands on the line break before the use's own line; only the hops after that
            // buy context, so without it the window starts at the use and reads forward only
            var start = file.text.LastIndexOf('\n', offset > 0 ? offset - 1 : 0);

            if (start < 0)
            {
                start = 0;
            }

            for (var i = 0; i < context && start > 0; i++)
            {
                var previous = file.text.LastIndexOf('\n', start - 1);

                if (previous < 0)
                {
                    start = 0;
                    break;
                }

                start = previous;
            }

            return CSharpHelper.Snippet(file, start == 0 ? 0 : start + 1, offset, context * 2 + 1);
        }
    }
}
