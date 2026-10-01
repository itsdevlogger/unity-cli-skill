using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace UnityCliMacros
{
    /// Searching the console instead of dumping it.
    ///
    /// `get_console_logs` returns every entry with its full stack trace and offers no text search —
    /// only severity and limit. On a real project one unfiltered call is 10-30k tokens, nearly all of
    /// it engine-internal frames, and the two things actually worth asking ("show me the
    /// NullReferenceExceptions", "is anything complaining about PlayerController") cannot be expressed
    /// at the source at all. This filters, groups and trims inside the Editor so only the answer
    /// crosses the wire.
    public static class M_ConsoleLogs
    {
        private class Group
        {
            public string kind;
            public string message;
            public string stack;
            public DateTime stamp;
            public bool hasStamp;
            public int count;
        }

        /// A console entry, copied out of the package's own DTO.
        private class Entry
        {
            public string type;
            public string message;
            public string stack;
            public string timestampUtc;
        }

        /// Reflection over the package's console buffer, for the same reason RefaranceHelper exists:
        /// as of com.unity.pipeline 0.6.0-exp.1 both `ConsoleLogBuffer` and the `ConsoleLogEntryDto`
        /// it hands back are `internal`, and so is the `ConsoleCommands` class wrapping them — there
        /// is no public surface left to bind against from this assembly. Everything is resolved once
        /// and cached, and a shape change surfaces as a clean "ERR no-console-buffer" rather than a
        /// NullReferenceException halfway through a query.
        private static class ConsoleBuffer
        {
            private const string TYPE_NAME = "Unity.Pipeline.Editor.Commands.Observability.ConsoleLogBuffer";
            private const string ASSEMBLY_NAME = "Unity.Pipeline.Editor";

            private static bool s_resolved;
            private static MethodInfo s_snapshot;
            private static PropertyInfo s_count;
            private static PropertyInfo s_type;
            private static PropertyInfo s_message;
            private static PropertyInfo s_stack;
            private static PropertyInfo s_stamp;

            /// Why the last resolve failed, for the caller to log. Empty once bound.
            public static string Error { get; private set; }

            /// Entries currently retained by the buffer, or 0 when it could not be reached.
            public static int Count
            {
                get
                {
                    if (!Resolve())
                    {
                        return 0;
                    }

                    try
                    {
                        return (int)s_count.GetValue(null);
                    }
                    catch (Exception error)
                    {
                        Error = error.Message;
                        return 0;
                    }
                }
            }

            /// Oldest-first copy of the buffer. False leaves entries empty and sets Error.
            public static bool TrySnapshot(out List<Entry> entries)
            {
                entries = new List<Entry>();

                if (!Resolve())
                {
                    return false;
                }

                try
                {
                    var raw = s_snapshot.Invoke(null, null) as System.Collections.IEnumerable;

                    if (raw == null)
                    {
                        Error = TYPE_NAME + ".Snapshot() returned null";
                        return false;
                    }

                    foreach (var item in raw)
                    {
                        if (item == null)
                        {
                            continue;
                        }

                        entries.Add(new Entry
                        {
                            type = s_type.GetValue(item) as string,
                            message = s_message.GetValue(item) as string,
                            stack = s_stack.GetValue(item) as string,
                            timestampUtc = s_stamp.GetValue(item) as string
                        });
                    }

                    return true;
                }
                catch (Exception error)
                {
                    Error = error.Message;
                    entries.Clear();
                    return false;
                }
            }

            private static bool Resolve()
            {
                if (s_resolved)
                {
                    return string.IsNullOrEmpty(Error);
                }

                s_resolved = true;
                Error = string.Empty;

                var buffer = FindType();

                if (buffer == null)
                {
                    Error = "type " + TYPE_NAME + " not found in " + ASSEMBLY_NAME;
                    return false;
                }

                const BindingFlags statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

                s_snapshot = buffer.GetMethod("Snapshot", statics, null, Type.EmptyTypes, null);
                s_count = buffer.GetProperty("Count", statics);

                if (s_snapshot == null || s_count == null)
                {
                    Error = TYPE_NAME + " no longer exposes Snapshot()/Count";
                    return false;
                }

                // The entry type is taken off the returned collection rather than looked up by name:
                // the DTO is internal too, and this way a rename of it costs nothing here.
                var element = ElementTypeOf(s_snapshot.ReturnType);

                if (element == null)
                {
                    Error = "cannot infer the entry type from " + s_snapshot.ReturnType.Name;
                    return false;
                }

                const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                s_type = element.GetProperty("Type", members);
                s_message = element.GetProperty("Message", members);
                s_stack = element.GetProperty("StackTrace", members);
                s_stamp = element.GetProperty("TimestampUtc", members);

                if (s_type == null || s_message == null || s_stack == null || s_stamp == null)
                {
                    Error = element.Name + " no longer exposes Type/Message/StackTrace/TimestampUtc";
                    return false;
                }

                return true;
            }

            /// Type.GetType only resolves the assembly-qualified name once that assembly is loaded, so
            /// fall back to a scan of the domain before giving up.
            private static Type FindType()
            {
                var direct = Type.GetType(TYPE_NAME + ", " + ASSEMBLY_NAME, false);

                if (direct != null)
                {
                    return direct;
                }

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name != ASSEMBLY_NAME)
                    {
                        continue;
                    }

                    var found = assembly.GetType(TYPE_NAME, false);

                    if (found != null)
                    {
                        return found;
                    }
                }

                return null;
            }

            private static Type ElementTypeOf(Type collection)
            {
                if (collection.IsArray)
                {
                    return collection.GetElementType();
                }

                if (collection.IsGenericType && collection.GetGenericArguments().Length == 1)
                {
                    return collection.GetGenericArguments()[0];
                }

                foreach (var contract in collection.GetInterfaces())
                {
                    if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    {
                        return contract.GetGenericArguments()[0];
                    }
                }

                return null;
            }
        }

        /// The bridge reports a per-entry LogType, but severity tiers are families: asking for
        /// "error" has to sweep up Exception and Assert too or a NullReferenceException goes missing.
        private static readonly Dictionary<string, string[]> FAMILIES = new Dictionary<string, string[]>
        {
            { "error", new[] { "error", "exception", "assert" } },
            { "warning", new[] { "warning" } },
            { "log", new[] { "log" } }
        };

        private static readonly Dictionary<string, string> MARKS = new Dictionary<string, string>
        {
            { "error", "E" }, { "exception", "E" }, { "assert", "A" },
            { "warning", "W" }, { "log", "L" }
        };

        private static readonly Regex RELATIVE = new Regex(@"^(\d+)([smhd])$");

        private static readonly Dictionary<char, int> UNIT_SECONDS = new Dictionary<char, int>
        {
            { 's', 1 }, { 'm', 60 }, { 'h', 3600 }, { 'd', 86400 }
        };

        [CliCommand("m_find_logs", "Search the Editor console by regex over message AND stack trace, deduped and stack-trimmed. Use instead of get_console_logs.", MainThreadRequired = false)]
        public static string FindLogs(
            [CliArg("grep", "Regex to keep, matched against the message AND the stack trace (so a class name finds errors raised inside it)")]
            string grep = "",
            [CliArg("exclude", "Regex to drop, matched against the message AND the stack trace")]
            string exclude = "",
            [CliArg("sev", "Comma list of error|warning|log, or all")]
            string severity = "error",
            [CliArg("since", "Only entries newer than a duration (10m, 2h, 3d) or an ISO-8601 timestamp")]
            string since = "",
            [CliArg("max", "Distinct messages to return")]
            int max = 25,
            [CliArg("stack", "Stack frames per entry. 0 returns only the single frame that names project code.")]
            int stackFrames = 0,
            [CliArg("width", "Truncate each message to this many characters. 0 disables truncation.")]
            int width = 220,
            [CliArg("dedupe", "Collapse identical repeated messages into one entry with a count")]
            bool dedupe = true,
            [CliArg("match_case", "Make the regexes case-sensitive")]
            bool matchCase = false
        )
        {
            HashSet<string> wanted;

            if (!ParseSeverities(severity, out wanted))
            {
                Debug.LogError($"[M_ConsoleLogs.FindLogs] sev must be a comma list of error|warning|log, or all; got '{severity}'");
                return "ERR bad-sev";
            }

            var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            Regex keep = null;
            Regex drop = null;

            try
            {
                if (!string.IsNullOrEmpty(grep))
                {
                    keep = new Regex(grep, options);
                }

                if (!string.IsNullOrEmpty(exclude))
                {
                    drop = new Regex(exclude, options);
                }
            }
            catch (ArgumentException error)
            {
                Debug.LogError($"[M_ConsoleLogs.FindLogs] bad regex: {error.Message}");
                return "ERR bad-regex";
            }

            var hasSince = false;
            var cutoff = DateTime.MinValue;

            if (!string.IsNullOrEmpty(since))
            {
                if (!ParseSince(since, out cutoff))
                {
                    Debug.LogError($"[M_ConsoleLogs.FindLogs] since must be a duration (10m, 2h) or an ISO timestamp; got '{since}'");
                    return "ERR bad-since";
                }

                hasSince = true;
            }

            max = Mathf.Clamp(max, 1, 200);
            stackFrames = Mathf.Clamp(stackFrames, 0, 40);

            List<Entry> snapshot;

            if (!ConsoleBuffer.TrySnapshot(out snapshot))
            {
                Debug.LogError($"[M_ConsoleLogs.FindLogs] cannot reach the package console buffer: {ConsoleBuffer.Error}");
                return "ERR no-console-buffer";
            }

            var groups = new List<Group>();
            var index = new Dictionary<string, Group>();
            var matched = 0;

            // Newest first: the tail of the console is what anyone asking about it means.
            for (var i = snapshot.Count - 1; i >= 0; i--)
            {
                var entry = snapshot[i];
                var kind = (entry.type ?? "Log").ToLowerInvariant();

                if (!wanted.Contains(kind))
                {
                    continue;
                }

                DateTime stamp;
                var hasStamp = TryParseStamp(entry.timestampUtc, out stamp);

                if (hasSince && hasStamp && stamp < cutoff)
                {
                    continue;
                }

                var message = (entry.message ?? string.Empty).Trim();
                var stack = entry.stack ?? string.Empty;
                var haystack = message + "\n" + stack;

                if (keep != null && !keep.IsMatch(haystack))
                {
                    continue;
                }

                if (drop != null && drop.IsMatch(haystack))
                {
                    continue;
                }

                matched++;

                if (dedupe)
                {
                    var key = kind + " " + message;
                    Group existing;

                    if (index.TryGetValue(key, out existing))
                    {
                        existing.count++;
                        continue;
                    }

                    var created = NewGroup(kind, message, stack, stamp, hasStamp);
                    index[key] = created;
                    groups.Add(created);
                    continue;
                }

                groups.Add(NewGroup(kind, message, stack, stamp, hasStamp));
            }

            return Format(groups, matched, snapshot.Count, ConsoleBuffer.Count, max, stackFrames, width);
        }

        private static Group NewGroup(string kind, string message, string stack, DateTime stamp, bool hasStamp)
        {
            return new Group
            {
                kind = kind,
                message = message,
                stack = stack,
                stamp = stamp,
                hasStamp = hasStamp,
                count = 1
            };
        }

        /// Compact single-line JSON, for the same reason M_FindMacro uses it: the CLI renders the
        /// return value as one cell of a tab-separated row, so tabs and newlines are destroyed in
        /// transit. JSON escapes free text instead of mutilating it.
        private static string Format(List<Group> groups, int matched, int fetched, int buffered, int max, int stackFrames, int width)
        {
            var shown = Math.Min(groups.Count, max);

            // The most repeated call in any build-fix loop is the one that finds nothing, and it used
            // to answer with four zeroed counters behind a legend explaining what the zeroes meant.
            if (shown == 0)
            {
                return buffered == 0 ? "console is empty" : "no matches in " + buffered + " buffered";
            }

            var sb = new FlatBuilder();

            sb.Line(0, FlatHelper.Count(matched, "match") + " of " + buffered + " buffered"
                       + (groups.Count > shown ? ", " + (groups.Count - shown) + " distinct not shown" : ""));

            for (var i = 0; i < shown; i++)
            {
                var group = groups[i];

                // Kind, repeat count and time are all fixed-width, so the message takes the terminal
                // slot it needs.
                sb.Line(1, MarkOf(group.kind)
                           + ("x" + group.count).PadLeft(5) + "  "
                           + (group.hasStamp ? group.stamp.ToString("HH:mm:ss") : "--:--:--") + "  "
                           + FlatHelper.Text(Truncate(group.message, width)));

                foreach (var frame in Frames(group.stack, stackFrames))
                {
                    sb.Line(2, FlatHelper.Text(frame));
                }
            }

            return sb.ToString();
        }

        private static string MarkOf(string kind)
        {
            string mark;
            return MARKS.TryGetValue(kind, out mark) ? mark : "?";
        }

        private static string Truncate(string value, int width)
        {
            if (width <= 0 || value.Length <= width)
            {
                return value;
            }

            return value.Substring(0, width) + "...";
        }

        /// The frames worth returning: project code first, engine internals never. A trace is dozens of
        /// UnityEngine.* frames wrapped around the one line that says which of your files threw.
        private static List<string> Frames(string stack, int want)
        {
            var result = new List<string>();

            if (string.IsNullOrEmpty(stack))
            {
                return result;
            }

            var lines = stack.Split('\n');
            var trimmed = new List<string>();

            foreach (var line in lines)
            {
                var text = line.Trim();

                if (text.Length > 0)
                {
                    trimmed.Add(text);
                }
            }

            if (trimmed.Count == 0)
            {
                return result;
            }

            if (want > 0)
            {
                for (var i = 0; i < trimmed.Count && i < want; i++)
                {
                    result.Add(trimmed[i]);
                }

                return result;
            }

            foreach (var frame in trimmed)
            {
                if (frame.Contains("Assets/") || frame.Contains("Assets\\"))
                {
                    result.Add(frame);
                    return result;
                }
            }

            result.Add(trimmed[0]);

            return result;
        }

        private static bool ParseSeverities(string severity, out HashSet<string> wanted)
        {
            wanted = new HashSet<string>();

            if (string.IsNullOrEmpty(severity))
            {
                return false;
            }

            foreach (var part in severity.Split(','))
            {
                var token = part.Trim().ToLowerInvariant();

                if (token.Length == 0)
                {
                    continue;
                }

                if (token == "all")
                {
                    foreach (var family in FAMILIES.Values)
                    {
                        foreach (var kind in family)
                        {
                            wanted.Add(kind);
                        }
                    }

                    continue;
                }

                string[] kinds;

                if (!FAMILIES.TryGetValue(token, out kinds))
                {
                    return false;
                }

                foreach (var kind in kinds)
                {
                    wanted.Add(kind);
                }
            }

            return wanted.Count > 0;
        }

        private static bool ParseSince(string text, out DateTime cutoff)
        {
            cutoff = DateTime.MinValue;
            var value = text.Trim();
            var match = RELATIVE.Match(value);

            if (match.Success)
            {
                int amount;

                if (!int.TryParse(match.Groups[1].Value, out amount))
                {
                    return false;
                }

                cutoff = DateTime.UtcNow.AddSeconds(-amount * UNIT_SECONDS[match.Groups[2].Value[0]]);
                return true;
            }

            DateTime parsed;

            if (!DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out parsed))
            {
                return false;
            }

            cutoff = parsed;
            return true;
        }

        private static bool TryParseStamp(string text, out DateTime stamp)
        {
            stamp = DateTime.MinValue;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out stamp);
        }

        /// Log messages and stack traces are free text whose line breaks are formatting, so they go
        /// through the collapsing writer in M_Json rather than the lossless one.
        private static void AppendJsonString(StringBuilder sb, string value)
        {
            JsonHelper.AppendProse(sb, value);
        }
    }
}