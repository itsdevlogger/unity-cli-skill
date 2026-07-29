using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEngine;

/// The full name of a type, so `eval` can name it on the first attempt.
///
/// `eval` in the Editor compiles against every loaded assembly, so no type is out of reach — the only
/// thing that can go wrong is the qualification, and that is exactly what a plain type name does not
/// tell you. `Cinemachine.CinemachineVirtualCamera` does not compile in a Cinemachine 3 project
/// because the namespace is `Unity.Cinemachine`, and nothing short of reading the package source says
/// so. The usual fallback — matching `GetType().Name.Contains("Virtual")` — works and is fragile.
///
/// Also answers the `Type.GetType` case, which needs an assembly-qualified name and silently returns
/// null without one.
public static class M_TypeInfo
{
    /// Namespaces the eval wrapper already has open, so a type in one of these needs no qualifying
    private static readonly string[] OPEN_NAMESPACES =
    {
        "UnityEngine", "UnityEditor", "System", "System.Collections.Generic", "System.Linq"
    };

    private const int MAX_RESULTS_LIMIT = 25;

    private const string LEGEND =
        "r=results n=full name (use this in eval) a=assembly q=assembly-qualified name (use this in " +
        "Type.GetType) e=1 when eval can name it unqualified because its namespace is already open " +
        "k=kind b=base type; " +
        "eval in the Editor references every loaded assembly, so a listed type is always reachable - " +
        "only the qualification matters. Sorted best match first.";

    // Pure reflection over the loaded domain, so it can answer while the Editor is compiling —
    // which is when "what is this type called" tends to be the blocking question
    [CliCommand("m_type_info", "Resolve a type name to its full name, assembly and assembly-qualified name, so eval and Type.GetType can name it correctly on the first try.", MainThreadRequired = false)]
    public static string TypeInfo(
        [CliArg("q", "Type name or partial name, e.g. CinemachineCamera or CharacterSwitcher", Required = true)] string query,
        [CliArg("m", "Maximum number of results to return")] int max = 8)
    {
        if (string.IsNullOrEmpty(query))
        {
            return JsonHelper.Err("M_TypeInfo.TypeInfo", "no-query", "the q argument was null or empty");
        }

        max = Mathf.Clamp(max, 1, MAX_RESULTS_LIMIT);

        var needle = query.Trim();
        var needleLower = needle.ToLowerInvariant();
        var scored = new List<KeyValuePair<int, System.Type>>();

        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            System.Type[] types;

            try
            {
                types = assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException)
            {
                // A half-loadable assembly is normal in an Editor domain and not worth reporting
                continue;
            }

            foreach (var type in types)
            {
                var score = Score(type, needle, needleLower);

                if (score > 0)
                {
                    scored.Add(new KeyValuePair<int, System.Type>(score, type));
                }
            }
        }

        if (scored.Count == 0)
        {
            return "NONE";
        }

        var matches = scored
            .OrderByDescending(x => x.Key)
            .ThenBy(x => (x.Value.FullName ?? x.Value.Name).Length)
            .ThenBy(x => x.Value.FullName ?? x.Value.Name)
            .Take(max)
            .ToList();

        return Format(matches, scored.Count);
    }

    /// Exact short name first: a query is nearly always the name as it appears in the Inspector or in
    /// half-remembered code, and the full name is what is being asked for.
    private static int Score(System.Type type, string needle, string needleLower)
    {
        if (type.Name == needle)
        {
            return 100;
        }

        var fullName = type.FullName;

        if (fullName == needle)
        {
            return 90;
        }

        var nameLower = type.Name.ToLowerInvariant();

        if (nameLower == needleLower)
        {
            return 80;
        }

        if (nameLower.StartsWith(needleLower))
        {
            return 40;
        }

        if (nameLower.Contains(needleLower))
        {
            return 20;
        }

        if (fullName != null && fullName.ToLowerInvariant().Contains(needleLower))
        {
            return 10;
        }

        return 0;
    }

    private static string Format(List<KeyValuePair<int, System.Type>> matches, int totalMatches)
    {
        var sb = new StringBuilder();

        sb.Append("{\"k\":\"");
        JsonHelper.AppendProse(sb, LEGEND);
        sb.Append("\",\"r\":[");

        for (var i = 0; i < matches.Count; i++)
        {
            var type = matches[i].Value;

            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"n\":\"");
            JsonHelper.AppendString(sb, type.FullName ?? type.Name);
            sb.Append("\",\"a\":\"");
            JsonHelper.AppendString(sb, type.Assembly.GetName().Name);
            sb.Append("\",\"q\":\"");
            JsonHelper.AppendString(sb, type.FullName + ", " + type.Assembly.GetName().Name);
            sb.Append("\",\"e\":").Append(IsOpen(type.Namespace) ? '1' : '0');
            sb.Append(",\"k\":\"").Append(Kind(type));
            sb.Append("\",\"b\":\"");
            JsonHelper.AppendString(sb, type.BaseType == null ? "" : type.BaseType.Name);
            sb.Append("\"}");
        }

        sb.Append(']');

        if (totalMatches > matches.Count)
        {
            sb.Append(",\"note\":\"").Append(totalMatches - matches.Count)
                .Append(" further matches not shown - narrow the query or raise m\"");
        }

        sb.Append('}');

        return sb.ToString();
    }

    private static bool IsOpen(string space)
    {
        if (string.IsNullOrEmpty(space))
        {
            return true;
        }

        foreach (var open in OPEN_NAMESPACES)
        {
            if (space == open)
            {
                return true;
            }
        }

        return false;
    }

    private static string Kind(System.Type type)
    {
        if (type.IsEnum)
        {
            return "enum";
        }

        if (type.IsInterface)
        {
            return "interface";
        }

        if (type.IsValueType)
        {
            return "struct";
        }

        return type.IsAbstract ? "abstract class" : "class";
    }
}
