using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEngine;

public static class M_FindMacro
{
    private struct MacroArgInfo
    {
        public string name;
        public string type;
        public string description;
        public bool required;
        public bool hasDefault;
        public string defaultValue;
    }

    private class MacroInfo
    {
        public string name;
        public string description;
        public List<MacroArgInfo> args;

        // Precomputed at registration so scoring never re-lowercases or re-splits per query
        public string nameLower;
        public string descLower;
        public string argTextLower;
        public string[] nameTokens;
        public string[] descTokens;
        public string[] argTokens;
    }

    // Match quality, strongest first. A hit on the name is worth far more than a hit in prose,
    // and an exact token beats a prefix which beats a bare substring.
    private const int SCORE_NAME_FULL_EXACT = 100;
    private const int SCORE_NAME_TOKEN_EXACT = 12;
    private const int SCORE_NAME_TOKEN_PREFIX = 7;
    private const int SCORE_NAME_SUBSTRING = 4;
    private const int SCORE_DESC_TOKEN_EXACT = 3;
    private const int SCORE_DESC_TOKEN_PREFIX = 2;
    private const int SCORE_DESC_SUBSTRING = 1;
    private const int SCORE_ARG_MATCH = 1;

    /// Added per term beyond the first that matched anywhere. Covering every term is strong
    /// evidence, so it must outweigh one term hitting a macro in many places.
    private const int SCORE_COVERAGE_BONUS = 10;

    /// Shortest overlap that counts as a prefix match, which is what makes
    /// material/materials and render/rendering match each other
    private const int MIN_PREFIX_LENGTH = 4;

    /// Results scoring below this fraction of the top hit are dropped as noise
    private const float RELATIVE_CUTOFF = 0.25f;

    private const int MAX_RESULTS_LIMIT = 50;

    private static readonly HashSet<string> STOP_WORDS = new HashSet<string>
    {
        "a", "an", "and", "the", "of", "for", "in", "on", "to", "with",
        "by", "from", "that", "this", "is", "are", "be", "it", "its", "or"
    };

    private static readonly List<MacroInfo> MACRO_INFOS = new List<MacroInfo>();
    private static bool isInitialized = false;

    /// Searches registered macros (CLI commands) by keyword and returns the best matches
    [CliCommand("m_find_macro", "Search for a macro by keywords and return the best matches. Names starting with m_ are editable macros from the shared library at C:/unity-cli-skill/macros.")]
    public static string FindMacro(
        [CliArg("q", "Keywords, separated by spaces or pipes", Required = true)] string keywords,
        [CliArg("m", "Maximum number of results to return", Required = false)] int maxResults = 5
        )
    {
        if (string.IsNullOrEmpty(keywords))
        {
            Debug.LogError("[M_FindMacro.FindMacro] keywords argument was null or empty");
            return "ERR no-keywords";
        }

        if (!isInitialized)
        {
            Initialize();
        }

        var terms = Tokenize(keywords)
            .Where(t => !STOP_WORDS.Contains(t))
            .Distinct()
            .ToArray();

        if (terms.Length == 0)
        {
            Debug.LogError("[M_FindMacro.FindMacro] no valid keywords after parsing input");
            return "ERR no-valid-keywords";
        }

        maxResults = Mathf.Clamp(maxResults, 1, MAX_RESULTS_LIMIT);

        var scored = MACRO_INFOS
            .Select(macro => new { macro, score = ScoreMacro(macro, terms) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.macro.name)
            .ToList();

        if (scored.Count == 0)
        {
            return "NONE";
        }

        // Once there is a strong hit the long tail is almost always noise, so drop it
        // rather than spend output on macros I would never pick. This runs before the
        // count limit: applied after, it would only ever trim results already inside it.
        var cutoff = scored[0].score * RELATIVE_CUTOFF;

        var matches = scored
            .Where(x => x.score >= cutoff)
            .Take(maxResults)
            .Select(x => new KeyValuePair<int, MacroInfo>(x.score, x.macro))
            .ToList();

        return FormatResults(matches);
    }

    /// The CLI emits results as one cell of a tab-separated row, so the returned string must stay
    /// single-line and tab-free or it corrupts that framing. Compact JSON satisfies that while
    /// escaping free text rather than mutilating it, and keeps record boundaries structural:
    ///
    ///   {"k":&lt;legend&gt;,"r":[{"n":name,"s":score,"d":desc,"a":[[argtoken,argdesc],...]},...]}
    ///
    /// "a" is omitted entirely for a macro with no args. An arg's type and arity ride in the token
    /// instead of extra keys: "name:type" then "*" required, "=value" optional with default, "?"
    /// optional without default. Split at the FIRST ':' for the type and the FIRST '=' for the
    /// default, since a default may contain either.
    ///
    /// The type is there because a name and a description do not say what shape a value takes, and
    /// guessing costs a round trip — or worse, makes a whole command look unusable and sends the
    /// caller off to write the eval it would have replaced. "objectref" and "json" are the two that
    /// matter; SKILL.md documents what to pass for each.
    private const string LEGEND =
        "r=results n=name s=score d=desc a=args[token,desc]; token: name:type then * required, =default, or ? optional-no-default; " +
        "type objectref takes a handle string ('/Root/Child', 'Assets/X.mat', 'guid:<hex>', instanceId); type json takes inline JSON; " +
        "an m_ name prefix means a macro from the shared library at C:/unity-cli-skill/macros (editable, shared by every project on this machine), anything else is built into the Pipeline package (fixed)";

    private static string FormatResults(List<KeyValuePair<int, MacroInfo>> matches)
    {
        var sb = new StringBuilder();

        sb.Append("{\"k\":\"");
        AppendJsonString(sb, LEGEND);
        sb.Append("\",\"r\":[");

        for (var i = 0; i < matches.Count; i++)
        {
            var macro = matches[i].Value;

            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"n\":\"");
            AppendJsonString(sb, macro.name);
            sb.Append("\",\"s\":");
            sb.Append(matches[i].Key);
            sb.Append(",\"d\":\"");
            AppendJsonString(sb, macro.description);
            sb.Append('"');

            if (macro.args.Count > 0)
            {
                sb.Append(",\"a\":[");

                for (var j = 0; j < macro.args.Count; j++)
                {
                    var arg = macro.args[j];

                    if (j > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append("[\"");
                    AppendJsonString(sb, arg.name);

                    if (arg.type.Length > 0)
                    {
                        sb.Append(':');
                        AppendJsonString(sb, arg.type);
                    }

                    if (arg.required)
                    {
                        sb.Append('*');
                    }
                    else if (arg.hasDefault)
                    {
                        sb.Append('=');
                        AppendJsonString(sb, arg.defaultValue);
                    }
                    else
                    {
                        sb.Append('?');
                    }

                    sb.Append("\",\"");
                    AppendJsonString(sb, arg.description);
                    sb.Append("\"]");
                }

                sb.Append(']');
            }

            sb.Append('}');
        }

        sb.Append("]}");

        return sb.ToString();
    }

    /// Everything this macro emits is either an identifier or prose, so the collapsing writer in
    /// M_Json is the right one — no macro description needs its internal line breaks preserved.
    private static void AppendJsonString(StringBuilder sb, string value)
    {
        JsonHelper.AppendProse(sb, value);
    }

    private static int ScoreMacro(MacroInfo macro, string[] terms)
    {
        var score = 0;
        var matchedTerms = 0;

        foreach (var term in terms)
        {
            var termScore = ScoreTerm(macro, term);

            if (termScore > 0)
            {
                score += termScore;
                matchedTerms++;
            }
        }

        if (matchedTerms > 1)
        {
            score += (matchedTerms - 1) * SCORE_COVERAGE_BONUS;
        }

        return score;
    }

    private static int ScoreTerm(MacroInfo macro, string term)
    {
        if (macro.nameLower == term)
        {
            return SCORE_NAME_FULL_EXACT;
        }

        var score = 0;

        // Each source contributes its single best tier, so a macro cannot farm points
        // by mentioning one term in every one of its many args
        if (ContainsExact(macro.nameTokens, term))
        {
            score += SCORE_NAME_TOKEN_EXACT;
        }
        else if (ContainsPrefix(macro.nameTokens, term))
        {
            score += SCORE_NAME_TOKEN_PREFIX;
        }
        else if (macro.nameLower.Contains(term))
        {
            score += SCORE_NAME_SUBSTRING;
        }

        if (ContainsExact(macro.descTokens, term))
        {
            score += SCORE_DESC_TOKEN_EXACT;
        }
        else if (ContainsPrefix(macro.descTokens, term))
        {
            score += SCORE_DESC_TOKEN_PREFIX;
        }
        else if (macro.descLower.Contains(term))
        {
            score += SCORE_DESC_SUBSTRING;
        }

        if (ContainsExact(macro.argTokens, term)
            || ContainsPrefix(macro.argTokens, term)
            || macro.argTextLower.Contains(term))
        {
            score += SCORE_ARG_MATCH;
        }

        return score;
    }

    private static bool ContainsExact(string[] tokens, string term)
    {
        foreach (var token in tokens)
        {
            if (token == term)
            {
                return true;
            }
        }

        return false;
    }

    /// True when one of the two is a prefix of the other and the overlap is long enough to be
    /// meaningful. Symmetric on purpose: the query may be the longer form ("materials" vs "material")
    /// just as often as the shorter one.
    private static bool ContainsPrefix(string[] tokens, string term)
    {
        if (term.Length < MIN_PREFIX_LENGTH)
        {
            return false;
        }

        foreach (var token in tokens)
        {
            if (token.Length < MIN_PREFIX_LENGTH)
            {
                continue;
            }

            if (token.StartsWith(term) || term.StartsWith(token))
            {
                return true;
            }
        }

        return false;
    }

    /// Longest enum or structured-input member list worth spelling out inline. Past this the token
    /// stops being a hint and starts being the payload.
    private const int MAX_MEMBERS_SHOWN = 8;

    /// Renders a parameter's type as a short token telling the caller what a value looks like.
    ///
    /// Three of these earn their characters. "objectref" means the arg takes a handle string rather
    /// than a name, which is the difference between a working call and a "not found". "enum(a|b|c)"
    /// and "json{field,field}" spell out the accepted values, which is otherwise unknowable without
    /// reading the package source — and an arg you cannot construct reads as a command you cannot use.
    private static string TypeToken(System.Type type)
    {
        if (type == null)
        {
            return string.Empty;
        }

        var nullable = System.Nullable.GetUnderlyingType(type);

        if (nullable != null)
        {
            type = nullable;
        }

        if (type.IsArray)
        {
            return TypeToken(type.GetElementType()) + "[]";
        }

        if (type.IsGenericType)
        {
            var generic = type.GetGenericTypeDefinition();

            if (generic == typeof(List<>) || generic == typeof(IEnumerable<>) || generic == typeof(IList<>))
            {
                return TypeToken(type.GetGenericArguments()[0]) + "[]";
            }

            if (generic == typeof(Dictionary<,>) || generic == typeof(IDictionary<,>))
            {
                return "json";
            }
        }

        if (type.IsEnum)
        {
            return "enum(" + JoinCapped(System.Enum.GetNames(type), "|") + ")";
        }

        switch (type.Name)
        {
            case "ObjectRef":
                return "objectref";
            case "String":
                return "string";
            case "Boolean":
                return "bool";
            case "Int32":
                return "int";
            case "Int64":
                return "long";
            case "Single":
                return "float";
            case "Double":
                return "double";
            case "JObject":
            case "JArray":
            case "JToken":
            case "JValue":
                return "json";
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (contract.Name != "IStructuredCommandInput")
            {
                continue;
            }

            var members = StructuredMembers(type);

            return members.Length == 0 ? "json" : "json{" + JoinCapped(members, ",") + "}";
        }

        return type.Name;
    }

    /// The [CliArg]-tagged fields and properties of a structured input DTO — the JSON keys the
    /// caller is expected to supply. Falls back to every public member when none are tagged.
    private static string[] StructuredMembers(System.Type type)
    {
        var tagged = new List<string>();
        var untagged = new List<string>();

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            var attr = field.GetCustomAttribute<CliArgAttribute>();
            (attr == null ? untagged : tagged).Add(attr == null ? field.Name : attr.Name);
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            var attr = property.GetCustomAttribute<CliArgAttribute>();
            (attr == null ? untagged : tagged).Add(attr == null ? property.Name : attr.Name);
        }

        return tagged.Count > 0 ? tagged.ToArray() : untagged.ToArray();
    }

    private static string JoinCapped(string[] values, string separator)
    {
        if (values.Length <= MAX_MEMBERS_SHOWN)
        {
            return string.Join(separator, values);
        }

        var shown = new string[MAX_MEMBERS_SHOWN + 1];
        System.Array.Copy(values, shown, MAX_MEMBERS_SHOWN);
        shown[MAX_MEMBERS_SHOWN] = "...";

        return string.Join(separator, shown);
    }

    /// Splits on anything that is not a letter or digit, so snake_case names, prose and
    /// multi-word queries all reduce to the same token space
    private static string[] Tokenize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new string[0];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Length = 0;
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens.ToArray();
    }

    private static void Initialize()
    {
        MACRO_INFOS.Clear();

        var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();

        foreach (var assembly in assemblies)
        {
            MethodInfo[] methods;

            try
            {
                methods = assembly
                    .GetTypes()
                    .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    .ToArray();
            }
            catch (ReflectionTypeLoadException)
            {
                Debug.LogError($"[M_FindMacro.Initialize] failed to load types from assembly {assembly.FullName}");
                continue;
            }

            foreach (var method in methods)
            {
                var commandAttr = method.GetCustomAttribute<CliCommandAttribute>();

                if (commandAttr == null)
                {
                    continue;
                }

                var argInfos = new List<MacroArgInfo>();

                foreach (var param in method.GetParameters())
                {
                    var argAttr = param.GetCustomAttribute<CliArgAttribute>();

                    if (argAttr == null)
                    {
                        continue;
                    }

                    var hasDefault = param.HasDefaultValue;
                    string defaultValue;

                    // Spelled out rather than left blank, so "defaults to null" is never
                    // mistaken for a literal placeholder in the formatted output
                    if (!hasDefault)
                    {
                        defaultValue = string.Empty;
                    }
                    else if (param.DefaultValue == null)
                    {
                        defaultValue = "null";
                    }
                    else if (param.DefaultValue is string stringDefault && stringDefault.Length == 0)
                    {
                        defaultValue = "''";
                    }
                    else
                    {
                        defaultValue = param.DefaultValue.ToString();
                    }

                    argInfos.Add(new MacroArgInfo
                    {
                        name = argAttr.Name,
                        type = TypeToken(param.ParameterType),
                        description = argAttr.Description,
                        required = argAttr.Required,
                        hasDefault = hasDefault,
                        defaultValue = defaultValue
                    });
                }

                var description = commandAttr.Description ?? string.Empty;
                var argText = string.Join(" ", argInfos.Select(a => $"{a.name} {a.description}"));

                MACRO_INFOS.Add(new MacroInfo
                {
                    name = commandAttr.Name,
                    description = description,
                    args = argInfos,
                    nameLower = commandAttr.Name.ToLowerInvariant(),
                    descLower = description.ToLowerInvariant(),
                    argTextLower = argText.ToLowerInvariant(),
                    nameTokens = Tokenize(commandAttr.Name),
                    descTokens = Tokenize(description),
                    argTokens = Tokenize(argText)
                });
            }
        }

        isInitialized = true;
    }
}
