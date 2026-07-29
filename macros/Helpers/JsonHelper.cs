using System.Text;
using UnityEngine;


public static class JsonHelper
{
    /// Appends value as the body of a JSON string, losslessly. Whitespace and control characters
    /// become escapes rather than disappearing, so the result still round-trips to the original —
    /// which is what an object name or a hierarchy path has to do to be usable as a target.
    ///
    /// Whitespace that is not a plain space is escaped even when JSON would allow it through, so a
    /// non-breaking space or a tab inside a name is visible to whoever reads the output.
    public static void AppendString(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    continue;
                case '\\':
                    sb.Append("\\\\");
                    continue;
                case '\n':
                    sb.Append("\\n");
                    continue;
                case '\r':
                    sb.Append("\\r");
                    continue;
                case '\t':
                    sb.Append("\\t");
                    continue;
            }

            if (c < ' ' || c == '\u007f' || (char.IsWhiteSpace(c) && c != ' '))
            {
                sb.Append("\\u").Append(((int)c).ToString("x4"));
                continue;
            }

            sb.Append(c);
        }
    }

    /// Appends value as the body of a JSON string, collapsing whitespace runs to a single space.
    /// For free text — legends, descriptions, log messages — where the line breaks are formatting
    /// rather than content and preserving them only costs escapes. Never use this on a name, a path,
    /// or anything else that has to be passed back verbatim.
    public static void AppendProse(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var pendingSpace = false;
        var wrote = false;

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c < ' ')
            {
                pendingSpace = wrote;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            if (c == '"' || c == '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
            wrote = true;
        }
    }

    /// True when a name can be retyped by hand and still match. False for leading or trailing
    /// whitespace, doubled spaces, tabs, non-breaking spaces and control characters — all of which
    /// are invisible in output that is otherwise correct, so a name carrying one has to be flagged as
    /// well as preserved.
    public static bool IsClean(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (value[0] == ' ' || value[value.Length - 1] == ' ')
        {
            return false;
        }

        var previousWasSpace = false;

        foreach (var c in value)
        {
            if (c < ' ' || c == '\u007f')
            {
                return false;
            }

            // Any whitespace that is not a plain space is indistinguishable from one on screen
            if (char.IsWhiteSpace(c) && c != ' ')
            {
                return false;
            }

            if (c == ' ')
            {
                if (previousWasSpace)
                {
                    return false;
                }

                previousWasSpace = true;
                continue;
            }

            previousWasSpace = false;
        }

        return true;
    }

    /// The folder's error convention in one call: the detail goes to the console where there is room
    /// for it, and the caller gets back a short token it can branch on. Returns the token so a macro
    /// can `return M_Json.Err(...)` in one line.
    public static string Err(string source, string reason, string detail)
    {
        Debug.LogError($"[{source}] {detail}");
        return "ERR " + reason;
    }
}
