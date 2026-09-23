using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityCliMacros
{
    /// Source-level declaration lookup shared by M_FindType and M_FindMember. Not a macro — no [CliCommand].
    ///
    /// Reflection (`m_type_info`) answers "what is this type called"; it cannot answer "which file
    /// declares it", because a compiled type carries no source path. That leaves grep, and a grep over a
    /// Unity project costs three round trips: one for candidate lines, one to read enough context to
    /// tell a declaration from a call site, and usually a third once the first pattern turns out to have
    /// matched a comment or a `where T : class` constraint.
    ///
    /// So this parses rather than matching lines. Comments and string literals are blanked out first,
    /// then declarations are found structurally: a type body is brace-matched, and a member counts as a
    /// member only when it sits at that body's own brace depth. That is what keeps a local variable
    /// inside a method from being reported as a field, and a call site from being reported as a method.
    ///
    /// Everything here is file IO and text with no Unity API, so both macros run off the main thread —
    /// which matters, because "where is this class declared" is most often asked while the project is
    /// failing to compile and reflection has nothing left to say.
    public static class CSharpHelper
    {
        /// Longest declaration signature echoed back. Past this a signature stops being an identifier
        /// and starts being the payload the caller should open the file for.
        public const int MAX_SIGNATURE = 240;

        private const int MAX_FILE_BYTES = 4 * 1024 * 1024;

        /// Folders that never hold project source but do hold a great deal of .cs
        private static readonly string[] SKIPPED_DIRECTORIES =
        {
            "obj", "bin", ".git", ".vs", "node_modules", "Temp", "Logs", "Build", "Builds"
        };

        /// Words that can stand where a declaration name is expected without being one
        private static readonly HashSet<string> NON_NAMES = new HashSet<string>
        {
            "where", "new", "base", "this", "null", "true", "false", "return", "if", "else", "in", "out",
            "ref", "case", "default", "yield", "throw", "await"
        };

        /// Leading keywords stripped off a declaration before its type and name are read. `event` and
        /// `implicit`/`explicit` are also remembered, because they change the member's kind.
        private static readonly HashSet<string> MODIFIERS = new HashSet<string>
        {
            "public", "private", "protected", "internal", "static", "readonly", "const", "virtual",
            "override", "abstract", "sealed", "async", "extern", "unsafe", "partial", "new", "volatile",
            "required", "event", "implicit", "explicit", "ref", "fixed", "file"
        };

        private static readonly HashSet<string> TYPE_KEYWORDS = new HashSet<string>
        {
            "class", "struct", "interface", "enum", "record", "delegate"
        };

        // `record class` / `record struct` put the kind in the second word, so the optional group eats it
        // and the name is still the last capture. A constraint (`where T : class`) reaches this pattern
        // too and is rejected afterwards by looking at the character before the keyword.
        private static readonly Regex TYPE_PATTERN = new Regex(
            @"(?<![\w.])(class|struct|interface|enum|record)\s+(?:(?:class|struct)\s+)?([A-Za-z_]\w*)",
            RegexOptions.Compiled);

        private static readonly Regex DELEGATE_PATTERN = new Regex(
            @"(?<![\w.])delegate\s+[^;{}()]+?([A-Za-z_]\w*)\s*(?:<[^<>;{}]*>)?\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex NAMESPACE_PATTERN = new Regex(
            @"(?<![\w.])namespace\s+([\w.]+)\s*([;{])",
            RegexOptions.Compiled);

        private static readonly Regex ASMDEF_NAME_PATTERN = new Regex(
            "\"name\"\\s*:\\s*\"([^\"]+)\"",
            RegexOptions.Compiled);

        /// One parsed .cs file. `masked` is `text` with every comment and string literal blanked to
        /// spaces, same length and same line breaks, so an offset means the same thing in both.
        public sealed class SourceFile
        {
            public string path;
            public string assembly;
            public string text;
            public string masked;

            private int[] lineStarts;

            public int LineOf(int offset)
            {
                if (lineStarts == null)
                {
                    lineStarts = BuildLineStarts(text);
                }

                var low = 0;
                var high = lineStarts.Length - 1;

                while (low < high)
                {
                    var mid = (low + high + 1) / 2;

                    if (lineStarts[mid] <= offset)
                    {
                        low = mid;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }

                return low + 1;
            }
        }

        public struct TypeDecl
        {
            public string kind;
            public string name;
            public string display;
            public string ns;
            public string outer;
            public int declStart;
            public int headEnd;
            public int bodyStart;
            public int bodyEnd;

            /// Namespace, containing types and own name joined the way `eval` has to spell it
            public string FullName
            {
                get
                {
                    var sb = new StringBuilder();

                    if (!string.IsNullOrEmpty(ns))
                    {
                        sb.Append(ns).Append('.');
                    }

                    if (!string.IsNullOrEmpty(outer))
                    {
                        sb.Append(outer).Append('.');
                    }

                    return sb.Append(display).ToString();
                }
            }
        }

        public struct MemberDecl
        {
            public string kind;
            public string name;
            public int declStart;
            public int headEnd;
            public int declEnd;
        }

        // ------------------------------------------------------------------ masking

        /// `text` with comments and string literal contents replaced by spaces, preserving length and
        /// line breaks. Everything downstream matches against this, so a `{` in a string, a `class` in a
        /// doc comment and a `;` in a char literal cannot move a brace depth or invent a declaration.
        public static string Mask(string text)
        {
            var chars = text.ToCharArray();
            var length = chars.Length;
            var i = 0;

            while (i < length)
            {
                var c = chars[i];

                if (c == '/' && i + 1 < length && chars[i + 1] == '/')
                {
                    while (i < length && chars[i] != '\n')
                    {
                        chars[i] = ' ';
                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < length && chars[i + 1] == '*')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i += 2;

                    while (i < length)
                    {
                        if (chars[i] == '*' && i + 1 < length && chars[i + 1] == '/')
                        {
                            chars[i] = ' ';
                            chars[i + 1] = ' ';
                            i += 2;
                            break;
                        }

                        if (chars[i] != '\n' && chars[i] != '\r')
                        {
                            chars[i] = ' ';
                        }

                        i++;
                    }

                    continue;
                }

                if (c == '@' && i + 1 < length && chars[i + 1] == '"')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i += 2;

                    while (i < length)
                    {
                        if (chars[i] == '"')
                        {
                            // A doubled quote is an escaped quote and the literal continues
                            if (i + 1 < length && chars[i + 1] == '"')
                            {
                                chars[i] = ' ';
                                chars[i + 1] = ' ';
                                i += 2;
                                continue;
                            }

                            chars[i] = ' ';
                            i++;
                            break;
                        }

                        if (chars[i] != '\n' && chars[i] != '\r')
                        {
                            chars[i] = ' ';
                        }

                        i++;
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    var quote = c;
                    chars[i] = ' ';
                    i++;

                    while (i < length)
                    {
                        if (chars[i] == '\\')
                        {
                            chars[i] = ' ';

                            if (i + 1 < length && chars[i + 1] != '\n')
                            {
                                chars[i + 1] = ' ';
                            }

                            i += 2;
                            continue;
                        }

                        if (chars[i] == quote)
                        {
                            chars[i] = ' ';
                            i++;
                            break;
                        }

                        // An unterminated literal is a syntax error somewhere else in the file; stopping
                        // at the line end keeps the damage to that line instead of the rest of the file
                        if (chars[i] == '\n')
                        {
                            break;
                        }

                        chars[i] = ' ';
                        i++;
                    }

                    continue;
                }

                i++;
            }

            return new string(chars);
        }

        // ------------------------------------------------------------------ type parsing

        /// Every type declared in the file, nested ones included, each with its brace-matched body span
        public static List<TypeDecl> ParseTypes(SourceFile file)
        {
            var masked = file.masked;
            var namespaces = ParseNamespaces(masked);
            var declarations = new List<TypeDecl>();

            foreach (Match match in TYPE_PATTERN.Matches(masked))
            {
                var keyword = match.Groups[1].Value;
                var name = match.Groups[2].Value;

                if (NON_NAMES.Contains(name) || TYPE_KEYWORDS.Contains(name))
                {
                    continue;
                }

                // `where T : class, IFoo` reaches the same pattern; a real declaration is never
                // preceded by a colon or a comma
                var previous = PreviousNonSpace(masked, match.Index - 1);

                if (previous == ':' || previous == ',')
                {
                    continue;
                }

                var kind = keyword;

                if (keyword == "record")
                {
                    kind = match.Value.Contains(" struct") ? "record struct" : "record";
                }

                var nameEnd = match.Groups[2].Index + name.Length;
                var afterGenerics = SkipGenerics(masked, nameEnd);
                var bodyStart = IndexOfBody(masked, afterGenerics);
                var bodyEnd = bodyStart < 0 ? -1 : MatchBrace(masked, bodyStart);

                declarations.Add(new TypeDecl
                {
                    kind = kind,
                    name = name,
                    display = name + GenericSuffix(masked, nameEnd, afterGenerics),
                    ns = NamespaceAt(namespaces, match.Index),
                    outer = string.Empty,
                    declStart = DeclarationStart(masked, match.Index),

                    // A positional record has no body, and its parameter list is the whole declaration,
                    // so the head has to run to the semicolon rather than stopping at the name
                    headEnd = bodyStart < 0 ? HeadEnd(masked, afterGenerics) : bodyStart,
                    bodyStart = bodyStart,
                    bodyEnd = bodyEnd
                });
            }

            foreach (Match match in DELEGATE_PATTERN.Matches(masked))
            {
                var name = match.Groups[1].Value;

                if (NON_NAMES.Contains(name))
                {
                    continue;
                }

                var nameEnd = match.Groups[1].Index + name.Length;
                var afterGenerics = SkipGenerics(masked, nameEnd);

                declarations.Add(new TypeDecl
                {
                    kind = "delegate",
                    name = name,
                    display = name + GenericSuffix(masked, nameEnd, afterGenerics),
                    ns = NamespaceAt(namespaces, match.Index),
                    outer = string.Empty,
                    declStart = DeclarationStart(masked, match.Index),
                    headEnd = IndexOrEnd(masked, afterGenerics, ';'),
                    bodyStart = -1,
                    bodyEnd = -1
                });
            }

            declarations.Sort((a, b) => a.declStart.CompareTo(b.declStart));

            // Resolved in a second pass so a nested type can name an outer type parsed after it
            for (var i = 0; i < declarations.Count; i++)
            {
                var current = declarations[i];
                var chain = new List<string>();

                for (var j = 0; j < declarations.Count; j++)
                {
                    var candidate = declarations[j];

                    if (j == i || candidate.bodyStart < 0)
                    {
                        continue;
                    }

                    if (current.declStart > candidate.bodyStart && current.declStart < candidate.bodyEnd)
                    {
                        chain.Add(candidate.display);
                    }
                }

                if (chain.Count > 0)
                {
                    current.outer = string.Join(".", chain.ToArray());
                    declarations[i] = current;
                }
            }

            return declarations;
        }

        // ------------------------------------------------------------------ member parsing

        /// Members declared directly by `type` — not by a nested type, and not a local inside a method
        /// body. Everything between the body braces is split at its own depth into declaration heads,
        /// which is what makes `private Action a = () => { … };` read as one field rather than as a field
        /// plus whatever the lambda contains.
        public static List<MemberDecl> ParseMembers(SourceFile file, TypeDecl type)
        {
            var members = new List<MemberDecl>();

            if (type.bodyStart < 0 || type.bodyEnd < 0)
            {
                return members;
            }

            var masked = file.masked;

            if (type.kind == "enum")
            {
                ParseEnumMembers(masked, type, members);
                return members;
            }

            var depth = 0;
            var chunkStart = type.bodyStart + 1;

            for (var i = type.bodyStart + 1; i < type.bodyEnd; i++)
            {
                var c = masked[i];

                if (c == '{')
                {
                    if (depth == 0)
                    {
                        ParseChunk(masked, type, chunkStart, i, members);
                    }

                    depth++;
                    continue;
                }

                if (c == '}')
                {
                    depth--;

                    if (depth <= 0)
                    {
                        depth = 0;
                        chunkStart = i + 1;
                    }

                    continue;
                }

                if (c == ';' && depth == 0)
                {
                    ParseChunk(masked, type, chunkStart, i, members);
                    chunkStart = i + 1;
                }
            }

            return members;
        }

        private static void ParseEnumMembers(string masked, TypeDecl type, List<MemberDecl> members)
        {
            var depth = 0;
            var chunkStart = type.bodyStart + 1;

            for (var i = type.bodyStart + 1; i <= type.bodyEnd; i++)
            {
                // The last value needs no trailing comma, so the closing brace stands in for one
                var c = i < type.bodyEnd ? masked[i] : ',';

                if (c == '(' || c == '[' || c == '{')
                {
                    depth++;
                    continue;
                }

                if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                    continue;
                }

                if (c != ',' || depth != 0)
                {
                    continue;
                }

                var cursor = SkipAttributes(masked, chunkStart, i);
                var nameStart = cursor;
                var name = ReadIdentifier(masked, ref cursor, i);

                if (!string.IsNullOrEmpty(name))
                {
                    members.Add(new MemberDecl
                    {
                        kind = "enum value",
                        name = name,
                        declStart = nameStart,
                        headEnd = i,
                        declEnd = i
                    });
                }

                chunkStart = i + 1;
            }
        }

        /// Reads one declaration head — everything from the end of the previous member up to the `{` or
        /// `;` that closes this one — and classifies it. Anything that does not parse as a declaration
        /// (a stray `= 5` left over after an auto-property initialiser, a nested type ParseTypes already
        /// owns) is dropped rather than guessed at.
        private static void ParseChunk(string masked, TypeDecl type, int start, int end, List<MemberDecl> members)
        {
            var terminator = end < masked.Length ? masked[end] : ';';
            var declEnd = end + 1;

            if (terminator == '{')
            {
                var close = MatchBrace(masked, end);
                declEnd = close < 0 ? end + 1 : close + 1;
            }

            var cursor = SkipAttributes(masked, start, end);
            var declStart = cursor;
            var isEvent = false;
            var isConversion = false;

            while (cursor < end)
            {
                var save = cursor;
                var word = ReadIdentifier(masked, ref cursor, end);

                if (string.IsNullOrEmpty(word) || !MODIFIERS.Contains(word))
                {
                    cursor = save;
                    break;
                }

                if (word == "event")
                {
                    isEvent = true;
                }
                else if (word == "implicit" || word == "explicit")
                {
                    isConversion = true;
                }
            }

            cursor = SkipSpace(masked, cursor, end);

            if (cursor >= end)
            {
                return;
            }

            // A nested type is a declaration ParseTypes already reports
            var peek = cursor;
            var first = ReadIdentifier(masked, ref peek, end);

            if (TYPE_KEYWORDS.Contains(first))
            {
                return;
            }

            if (first == "operator" || isConversion)
            {
                AddOperator(masked, cursor, end, declStart, declEnd, members);
                return;
            }

            if (masked[cursor] == '~')
            {
                cursor++;
                var finalizerName = ReadIdentifier(masked, ref cursor, end);

                if (!string.IsNullOrEmpty(finalizerName))
                {
                    members.Add(Make("finalizer", "~" + finalizerName, declStart, end, declEnd));
                }

                return;
            }

            var typeStart = cursor;

            if (!SkipTypeExpression(masked, ref cursor, end))
            {
                return;
            }

            var afterType = cursor;
            cursor = SkipSpace(masked, cursor, end);

            // `Foo(` with nothing between the type expression and the paren is a constructor: what was
            // read as a return type was the type's own name
            if (cursor < end && masked[cursor] == '(')
            {
                var ctorCursor = typeStart;
                var ctorName = ReadIdentifier(masked, ref ctorCursor, end);

                if (ctorName == type.name)
                {
                    members.Add(Make("constructor", ctorName, declStart, end, declEnd));
                }

                return;
            }

            var name = ReadQualifiedName(masked, ref cursor, end);

            if (string.IsNullOrEmpty(name) || NON_NAMES.Contains(name))
            {
                // `int this[int i]` is an indexer; the name reader stops on `this` as a keyword
                if (name == "this")
                {
                    members.Add(Make("indexer", "this", declStart, end, declEnd));
                }

                return;
            }

            if (name == "operator")
            {
                AddOperator(masked, afterType, end, declStart, declEnd, members);
                return;
            }

            var afterName = SkipSpace(masked, SkipGenerics(masked, cursor), end);

            if (afterName < end && masked[afterName] == '(')
            {
                members.Add(Make("method", name, declStart, end, declEnd));
                return;
            }

            // An indexer whose type expression already swallowed the bracket cannot get here, but an
            // array-typed member with a stray bracket can, and is not a member worth guessing at
            if (afterName < end && masked[afterName] == '[')
            {
                return;
            }

            var isAssigned = afterName < end && masked[afterName] == '=';

            var isExpressionBodied = isAssigned
                && afterName + 1 < end
                && masked[afterName + 1] == '>';

            if (isEvent)
            {
                members.Add(Make("event", name, declStart, end, declEnd));
                AddExtraDeclarators(masked, afterName, end, declStart, declEnd, "event", members);
                return;
            }

            // An initialiser settles it before the terminator gets a say: `Action a = () => { … };` ends
            // its head at the lambda's brace, and reading that brace as an accessor list would turn a
            // field into a property
            if (isExpressionBodied || !isAssigned && terminator == '{')
            {
                members.Add(Make("property", name, declStart, end, declEnd));
                return;
            }

            members.Add(Make("field", name, declStart, end, declEnd));
            AddExtraDeclarators(masked, afterName, end, declStart, declEnd, "field", members);
        }

        /// The second and later names in `int a, b, c;`, which share one type expression
        private static void AddExtraDeclarators(string masked, int cursor, int end, int declStart, int declEnd, string kind, List<MemberDecl> members)
        {
            var depth = 0;

            while (cursor < end)
            {
                var c = masked[cursor];

                if (c == '(' || c == '[' || c == '<' || c == '{')
                {
                    depth++;
                }
                else if (c == ')' || c == ']' || c == '>' || c == '}')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    var next = cursor + 1;
                    var name = ReadIdentifier(masked, ref next, end);

                    if (!string.IsNullOrEmpty(name) && !NON_NAMES.Contains(name))
                    {
                        members.Add(Make(kind, name, declStart, end, declEnd));
                    }
                }

                cursor++;
            }
        }

        private static void AddOperator(string masked, int cursor, int end, int declStart, int declEnd, List<MemberDecl> members)
        {
            var save = cursor;
            var word = ReadIdentifier(masked, ref cursor, end);

            if (word != "operator")
            {
                cursor = save;
            }

            var sb = new StringBuilder("operator ");

            while (cursor < end && masked[cursor] != '(')
            {
                var c = masked[cursor];

                if (!char.IsWhiteSpace(c) && c != ';')
                {
                    sb.Append(c);
                }

                cursor++;
            }

            members.Add(Make("operator", sb.ToString().Trim(), declStart, end, declEnd));
        }

        private static MemberDecl Make(string kind, string name, int declStart, int headEnd, int declEnd)
        {
            return new MemberDecl
            {
                kind = kind,
                name = name,
                declStart = declStart,
                headEnd = headEnd,
                declEnd = declEnd
            };
        }

        // ------------------------------------------------------------------ token readers

        /// Consumes one type expression — `int`, `List<Dictionary<string, int>>`, `Foo.Bar[]`, `int?`,
        /// `(int, string)` — leaving the cursor on whatever follows it
        private static bool SkipTypeExpression(string masked, ref int cursor, int end)
        {
            cursor = SkipSpace(masked, cursor, end);

            if (cursor >= end)
            {
                return false;
            }

            if (masked[cursor] == '(')
            {
                var close = MatchPair(masked, cursor, end, '(', ')');

                if (close < 0)
                {
                    return false;
                }

                cursor = close + 1;
            }
            else
            {
                var name = ReadQualifiedName(masked, ref cursor, end);

                if (string.IsNullOrEmpty(name) || NON_NAMES.Contains(name))
                {
                    return false;
                }

                cursor = SkipGenerics(masked, cursor);
            }

            while (cursor < end)
            {
                var c = masked[cursor];

                if (c == '?' || c == '*')
                {
                    cursor++;
                    continue;
                }

                if (c == '[')
                {
                    var close = MatchPair(masked, cursor, end, '[', ']');

                    if (close < 0)
                    {
                        break;
                    }

                    // `int[]` and `int[,]` are part of the type; `this[int i]` is an indexer's parameter
                    // list and belongs to the name that follows, so it has to stay unread
                    var inner = masked.Substring(cursor + 1, close - cursor - 1).Trim(' ', ',');

                    if (inner.Length != 0)
                    {
                        break;
                    }

                    cursor = close + 1;
                    continue;
                }

                break;
            }

            return true;
        }

        private static string ReadIdentifier(string masked, ref int cursor, int end)
        {
            cursor = SkipSpace(masked, cursor, end);

            if (cursor >= end)
            {
                return string.Empty;
            }

            if (masked[cursor] != '_' && !char.IsLetter(masked[cursor]))
            {
                return string.Empty;
            }

            var start = cursor;

            while (cursor < end && (masked[cursor] == '_' || char.IsLetterOrDigit(masked[cursor])))
            {
                cursor++;
            }

            return masked.Substring(start, cursor - start);
        }

        /// A dotted name, returning only its last segment — which is what an explicit interface
        /// implementation (`void IFoo.Bar()`) has to be searchable by
        private static string ReadQualifiedName(string masked, ref int cursor, int end)
        {
            var last = ReadIdentifier(masked, ref cursor, end);

            if (string.IsNullOrEmpty(last))
            {
                return string.Empty;
            }

            while (true)
            {
                var save = cursor;
                var dot = SkipSpace(masked, cursor, end);

                if (dot >= end || masked[dot] != '.')
                {
                    cursor = save;
                    return last;
                }

                var next = dot + 1;
                var segment = ReadIdentifier(masked, ref next, end);

                if (string.IsNullOrEmpty(segment))
                {
                    cursor = save;
                    return last;
                }

                last = segment;
                cursor = next;
            }
        }

        // ------------------------------------------------------------------ offsets

        private static int SkipSpace(string text, int cursor, int end)
        {
            while (cursor < end && char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
            }

            return cursor;
        }

        /// Past any `[Attribute(…)]` groups at the head of a declaration, so the reported line is the
        /// declaration itself rather than a `[SerializeField]` sitting above it
        private static int SkipAttributes(string masked, int cursor, int end)
        {
            while (true)
            {
                cursor = SkipSpace(masked, cursor, end);

                if (cursor >= end || masked[cursor] != '[')
                {
                    return cursor;
                }

                var close = MatchPair(masked, cursor, end, '[', ']');

                if (close < 0)
                {
                    return cursor;
                }

                cursor = close + 1;
            }
        }

        private static int SkipGenerics(string masked, int cursor)
        {
            var probe = SkipSpace(masked, cursor, masked.Length);

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

                // A generic argument list never spans a statement boundary; bailing out here keeps a
                // less-than operator from swallowing the rest of the file
                if (c == ';' || c == '{' || c == '}' || c == '(' || c == ')')
                {
                    return cursor;
                }
            }

            return cursor;
        }

        /// `<T1,T2>` for a generic declaration, so the display name carries its arity. The parameter
        /// names are not reproduced: arity is what distinguishes two types sharing a name, and the
        /// letters are noise once the file and line are in hand.
        private static string GenericSuffix(string masked, int nameEnd, int afterGenerics)
        {
            if (afterGenerics <= nameEnd)
            {
                return string.Empty;
            }

            var commas = 0;

            for (var i = nameEnd; i < afterGenerics; i++)
            {
                if (masked[i] == ',')
                {
                    commas++;
                }
            }

            var parameters = new string[commas + 1];

            for (var i = 0; i < parameters.Length; i++)
            {
                parameters[i] = "T" + (i + 1);
            }

            return "<" + string.Join(",", parameters) + ">";
        }

        private static int MatchPair(string masked, int open, int end, char opener, char closer)
        {
            var depth = 0;

            for (var i = open; i < end; i++)
            {
                if (masked[i] == opener)
                {
                    depth++;
                    continue;
                }

                if (masked[i] != closer)
                {
                    continue;
                }

                depth--;

                if (depth == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        public static int MatchBrace(string masked, int open)
        {
            return MatchPair(masked, open, masked.Length, '{', '}');
        }

        /// The `{` opening a type body, skipping the base list and any `where` clauses. Returns -1 for a
        /// declaration with no body on this side, such as a `partial` forward declaration.
        private static int IndexOfBody(string masked, int cursor)
        {
            for (var i = cursor; i < masked.Length; i++)
            {
                var c = masked[i];

                if (c == '{')
                {
                    return i;
                }

                if (c == ';' || c == '}')
                {
                    return -1;
                }
            }

            return -1;
        }

        private static int IndexOrEnd(string masked, int cursor, char stop)
        {
            var index = masked.IndexOf(stop, cursor);
            return index < 0 ? masked.Length : index;
        }

        /// End of a bodiless declaration: the first character that can close one. Bounded by `}` as well
        /// as `;` so a malformed file cannot make one signature swallow the rest of its type.
        private static int HeadEnd(string masked, int cursor)
        {
            for (var i = cursor; i < masked.Length; i++)
            {
                var c = masked[i];

                if (c == ';' || c == '{' || c == '}')
                {
                    return i;
                }
            }

            return masked.Length;
        }

        /// Walks back to the end of the previous statement or brace, then forward past attributes, so
        /// the declaration's own modifiers are included and the preceding member is not
        private static int DeclarationStart(string masked, int keywordIndex)
        {
            var start = 0;

            for (var i = keywordIndex - 1; i >= 0; i--)
            {
                var c = masked[i];

                if (c == ';' || c == '{' || c == '}')
                {
                    start = i + 1;
                    break;
                }
            }

            return SkipAttributes(masked, start, keywordIndex);
        }

        private static char PreviousNonSpace(string masked, int index)
        {
            for (var i = index; i >= 0; i--)
            {
                if (!char.IsWhiteSpace(masked[i]))
                {
                    return masked[i];
                }
            }

            return '\0';
        }

        // ------------------------------------------------------------------ namespaces

        private struct NamespaceSpan
        {
            public string name;
            public int start;
            public int end;
        }

        private static List<NamespaceSpan> ParseNamespaces(string masked)
        {
            var spans = new List<NamespaceSpan>();

            foreach (Match match in NAMESPACE_PATTERN.Matches(masked))
            {
                var isFileScoped = match.Groups[2].Value == ";";
                var open = match.Groups[2].Index;
                var close = isFileScoped ? masked.Length : MatchBrace(masked, open);

                spans.Add(new NamespaceSpan
                {
                    name = match.Groups[1].Value,
                    start = open,
                    end = close < 0 ? masked.Length : close
                });
            }

            return spans;
        }

        private static string NamespaceAt(List<NamespaceSpan> spans, int offset)
        {
            var parts = new List<string>();

            foreach (var span in spans)
            {
                if (offset > span.start && offset < span.end)
                {
                    parts.Add(span.name);
                }
            }

            return parts.Count == 0 ? string.Empty : string.Join(".", parts.ToArray());
        }

        // ------------------------------------------------------------------ text extraction

        /// A declaration head with its whitespace collapsed, capped so one long parameter list cannot
        /// crowd out the next result
        public static string Signature(SourceFile file, int start, int end)
        {
            if (end > file.text.Length)
            {
                end = file.text.Length;
            }

            if (start < 0 || start >= end)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            var pendingSpace = false;

            for (var i = start; i < end && sb.Length < MAX_SIGNATURE; i++)
            {
                var c = file.text[i];

                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(c);
            }

            var text = sb.ToString().TrimEnd(' ', ',');

            return end - start > MAX_SIGNATURE ? text + " ..." : text;
        }

        /// Source text for a declaration. `lines` greater than zero takes that many lines from the one
        /// `start` falls on; -1 takes the whole declaration, through its closing brace.
        public static string Snippet(SourceFile file, int start, int end, int lines)
        {
            if (start < 0)
            {
                return string.Empty;
            }

            var from = LineStartAt(file.text, start);
            int to;

            if (lines < 0)
            {
                to = end < 0 || end > file.text.Length ? file.text.Length : end;
                to = LineEndAt(file.text, to);
            }
            else
            {
                to = from;

                for (var i = 0; i < lines && to < file.text.Length; i++)
                {
                    to = LineEndAt(file.text, to);

                    if (to < file.text.Length)
                    {
                        to++;
                    }
                }
            }

            if (to <= from)
            {
                return string.Empty;
            }

            return file.text.Substring(from, to - from);
        }

        private static int LineStartAt(string text, int offset)
        {
            if (offset > text.Length)
            {
                offset = text.Length;
            }

            for (var i = offset - 1; i >= 0; i--)
            {
                if (text[i] == '\n')
                {
                    return i + 1;
                }
            }

            return 0;
        }

        private static int LineEndAt(string text, int offset)
        {
            for (var i = offset; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    return i;
                }
            }

            return text.Length;
        }

        private static int[] BuildLineStarts(string text)
        {
            var starts = new List<int> { 0 };

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    starts.Add(i + 1);
                }
            }

            return starts.ToArray();
        }

        // ------------------------------------------------------------------ arguments and files

        /// Splits a multi-value argument. Pipe and comma only — a search root may legitimately contain a
        /// space, and silently splitting `Assets/My Folder` into two missing roots is worse than making
        /// the caller type a separator.
        public static string[] SplitList(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return new string[0];
            }

            var parts = value.Split('|', ',');
            var result = new List<string>();

            foreach (var part in parts)
            {
                var trimmed = part.Trim();

                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }

            return result.ToArray();
        }

        public static string ProjectRoot
        {
            get { return Normalize(Directory.GetCurrentDirectory()); }
        }

        public static string Normalize(string path)
        {
            return path.Replace('\\', '/').TrimEnd('/');
        }

        /// Turns a root token into an absolute directory. `Assets`, `Packages` and `Library` (which means
        /// the package cache, the only part of Library holding source) are named because they are what a
        /// caller thinks in; anything else is taken as a project-relative or absolute folder path.
        public static string ResolveRoot(string token)
        {
            var root = ProjectRoot;

            switch (token.ToLowerInvariant())
            {
                case "assets":
                    return root + "/Assets";
                case "packages":
                    return root + "/Packages";
                case "library":
                case "packagecache":
                    return root + "/Library/PackageCache";
            }

            var normalized = Normalize(token);

            return Path.IsPathRooted(normalized) ? normalized : root + "/" + normalized;
        }

        public static List<string> ResolveRoots(string rootArgument, List<string> missing)
        {
            var tokens = SplitList(rootArgument);

            if (tokens.Length == 0)
            {
                tokens = new[] { "Assets" };
            }

            var directories = new List<string>();

            foreach (var token in tokens)
            {
                if (token.ToLowerInvariant() == "all")
                {
                    AddDirectory(directories, ResolveRoot("Assets"), missing, false);
                    AddDirectory(directories, ResolveRoot("Packages"), missing, false);
                    AddDirectory(directories, ResolveRoot("Library"), missing, false);
                    continue;
                }

                AddDirectory(directories, ResolveRoot(token), missing, true);
            }

            return directories;
        }

        private static void AddDirectory(List<string> directories, string directory, List<string> missing, bool report)
        {
            if (!Directory.Exists(directory))
            {
                if (report)
                {
                    missing.Add(RelativePath(directory));
                }

                return;
            }

            if (!directories.Contains(directory))
            {
                directories.Add(directory);
            }
        }

        /// Every .cs file under the roots, skipping build output. Yields lazily, so a caller that hits
        /// its cap stops the walk rather than finishing it.
        public static IEnumerable<string> EnumerateSources(List<string> roots)
        {
            foreach (var root in roots)
            {
                foreach (var file in WalkDirectory(root))
                {
                    yield return file;
                }
            }
        }

        private static IEnumerable<string> WalkDirectory(string directory)
        {
            string[] files;

            try
            {
                files = Directory.GetFiles(directory, "*.cs");
            }
            catch (IOException)
            {
                yield break;
            }
            catch (System.UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var file in files)
            {
                yield return Normalize(file);
            }

            string[] subdirectories;

            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (IOException)
            {
                yield break;
            }
            catch (System.UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var subdirectory in subdirectories)
            {
                if (IsSkipped(Path.GetFileName(subdirectory)))
                {
                    continue;
                }

                foreach (var file in WalkDirectory(subdirectory))
                {
                    yield return file;
                }
            }
        }

        private static bool IsSkipped(string directoryName)
        {
            foreach (var skipped in SKIPPED_DIRECTORIES)
            {
                if (string.Equals(directoryName, skipped, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static string RelativePath(string absolute)
        {
            var root = ProjectRoot + "/";

            return absolute.StartsWith(root, System.StringComparison.OrdinalIgnoreCase)
                ? absolute.Substring(root.Length)
                : absolute;
        }

        public static bool TryRead(string absolutePath, out string text)
        {
            text = null;

            try
            {
                var info = new FileInfo(absolutePath);

                if (!info.Exists || info.Length > MAX_FILE_BYTES)
                {
                    return false;
                }

                text = File.ReadAllText(absolutePath);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (System.UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static SourceFile Load(string absolutePath, string text)
        {
            return new SourceFile
            {
                path = RelativePath(absolutePath),
                assembly = AssemblyOf(absolutePath),
                text = text,
                masked = Mask(text)
            };
        }

        // ------------------------------------------------------------------ assemblies

        /// Cached per directory. The value is the governing .asmdef's name, or NO_ASMDEF when the walk
        /// reached the project root without finding one. Both answers are inherited by every descendant,
        /// which is what makes caching them on the way up sound.
        private static readonly Dictionary<string, string> ASMDEF_BY_DIRECTORY =
            new Dictionary<string, string>();

        private const string NO_ASMDEF = "";

        /// The assembly a file compiles into, found by walking up to the nearest .asmdef and reading its
        /// name. Read from disk rather than through CompilationPipeline on purpose: that API wants the
        /// main thread and a successful compile, and this has to keep working when there is neither.
        public static string AssemblyOf(string absolutePath)
        {
            var directory = Normalize(Path.GetDirectoryName(absolutePath) ?? string.Empty);
            var asmdef = AsmdefGoverning(directory);

            if (asmdef.Length > 0)
            {
                return asmdef;
            }

            // Nothing governs it, so it lands in one of Unity's predefined assemblies. Which one depends
            // on this file's own path, not on anything inherited — so it is decided here and never cached.
            return IsEditorPath(directory) ? "Assembly-CSharp-Editor" : "Assembly-CSharp";
        }

        private static string AsmdefGoverning(string directory)
        {
            var root = ProjectRoot;
            var visited = new List<string>();
            var cursor = directory;

            while (!string.IsNullOrEmpty(cursor))
            {
                string cached;

                if (ASMDEF_BY_DIRECTORY.TryGetValue(cursor, out cached))
                {
                    Remember(visited, cached);
                    return cached;
                }

                visited.Add(cursor);

                var name = AsmdefNameIn(cursor);

                if (name != null)
                {
                    Remember(visited, name);
                    return name;
                }

                if (string.Equals(cursor, root, System.StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var parent = Path.GetDirectoryName(cursor);

                if (string.IsNullOrEmpty(parent))
                {
                    break;
                }

                cursor = Normalize(parent);
            }

            Remember(visited, NO_ASMDEF);
            return NO_ASMDEF;
        }

        private static bool IsEditorPath(string directory)
        {
            return directory.IndexOf("/Editor/", System.StringComparison.OrdinalIgnoreCase) >= 0
                || directory.EndsWith("/Editor", System.StringComparison.OrdinalIgnoreCase);
        }

        private static void Remember(List<string> directories, string assembly)
        {
            foreach (var directory in directories)
            {
                ASMDEF_BY_DIRECTORY[directory] = assembly;
            }
        }

        private static string AsmdefNameIn(string directory)
        {
            string[] files;

            try
            {
                files = Directory.GetFiles(directory, "*.asmdef");
            }
            catch (IOException)
            {
                return null;
            }
            catch (System.UnauthorizedAccessException)
            {
                return null;
            }

            if (files.Length == 0)
            {
                return null;
            }

            string text;

            if (!TryRead(files[0], out text))
            {
                return Path.GetFileNameWithoutExtension(files[0]);
            }

            var match = ASMDEF_NAME_PATTERN.Match(text);

            return match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(files[0]);
        }

        public static bool MatchesAny(string value, string[] needles)
        {
            if (needles.Length == 0)
            {
                return true;
            }

            foreach (var needle in needles)
            {
                if (value.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
