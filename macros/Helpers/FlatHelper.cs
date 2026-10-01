using System.Collections.Generic;
using System.Text;

namespace UnityCliMacros
{
    /// Shared rendering for the flat, line-oriented output the navigation macros return.
    ///
    /// These macros used to answer in compact JSON with single-letter keys and a legend explaining
    /// them. The legend was the problem: it was emitted on every call, identical every time, and on a
    /// small answer it dwarfed the answer — 641 characters of legend in front of 192 characters of
    /// result for a one-declaration m_find_type, and 157 in front of an empty console search. A
    /// line-oriented format removes the encoding, and removing the encoding removes the thing the
    /// legend existed to explain, so it deletes both at once rather than relocating one of them.
    ///
    /// What remains of a legend is the handful of real caveats — m_refs being name-based rather than
    /// semantic, m_hierarchy matching by simple name — and those are per-macro facts, not per-call
    /// ones. They live in m_find_macro's description of each macro, which the workflow already makes
    /// the first call of any session.
    ///
    /// Everything renders through here rather than in each macro, because twelve hand-rolled
    /// indent-and-escape implementations drift apart within a month and the whole point of the format
    /// is that one explanation covers all of them.
    public sealed class FlatBuilder
    {
        /// How a line break is written into the single cell the CLI gives a macro's return value.
        ///
        /// The CLI renders a macro's return as one field of a tab-separated row, and a real newline
        /// does not survive that: measured, not assumed, by emitting one and reading it back, where
        /// every break came out collapsed to a single space and the whole answer arrived as one
        /// run-on line. A real tab goes the same way, splitting the line into further columns.
        ///
        /// So the break is written as the two-character escape instead. It costs one character more
        /// than a newline, it is what the reader on the other end already sees inside log messages,
        /// and keeping it in one constant means the decision can be revisited with a single edit if
        /// the transport ever stops mangling whitespace.
        public const string NEWLINE = "\\n";

        /// Two spaces per level. Deep enough to read as nesting, shallow enough that four levels of
        /// it — which m_refs reaches — still leaves room for a long path on the same line.
        private const string INDENT = "  ";

        private readonly StringBuilder _sb = new StringBuilder();
        private bool _any;

        public bool IsEmpty
        {
            get { return !_any; }
        }

        /// One line at the given nesting depth. The separator goes in front of each line after the
        /// first rather than behind every line, so the result never ends in a dangling break.
        public FlatBuilder Line(int indent, string text)
        {
            if (_any)
            {
                _sb.Append(NEWLINE);
            }

            for (var i = 0; i < indent; i++)
            {
                _sb.Append(INDENT);
            }

            if (!string.IsNullOrEmpty(text))
            {
                _sb.Append(text);
            }

            _any = true;
            return this;
        }

        public FlatBuilder Line(string text)
        {
            return Line(0, text);
        }

        /// Multi-line source text, indented as a block. Used by the --code and --ctx arguments, which
        /// are the only places a macro returns something it did not compose itself.
        public FlatBuilder Block(int indent, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return this;
            }

            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                Line(indent, line);
            }

            return this;
        }

        public override string ToString()
        {
            return _sb.ToString();
        }
    }

    public static class FlatHelper
    {
        /// A location, as path:line — the form that is clickable in a terminal and pasteable into a
        /// Read, rather than two fields the reader has to recombine.
        public static string At(string path, int line)
        {
            return Handle(path) + ":" + line;
        }

        /// A value that has to survive being passed back in — a path, a hierarchy path, an object
        /// name. Clean ones are written as they are; the rest are escaped and flagged with a leading
        /// tilde.
        ///
        /// This is the one thing the flat format must not lose. JSON escaping made a trailing space
        /// or a non-breaking space visible for free; plain text hides it completely, and these
        /// strings are lookup keys, so a name that reads correct but is not resolves to nothing and
        /// the null that gets written looks like success. The tilde is the marker m_scene_digest
        /// already uses for the same hazard.
        public static string Handle(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            if (JsonHelper.IsClean(value))
            {
                return value;
            }

            var sb = new StringBuilder("~");
            JsonHelper.AppendString(sb, value);
            return sb.ToString();
        }

        /// Free text going on a line of its own — a signature, a log message, a source line. Runs of
        /// whitespace collapse to one space, because the line breaks and alignment inside them are
        /// formatting rather than content, and a stray newline would break the row.
        public static string Text(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            var sb = new StringBuilder();
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

                sb.Append(c);
                wrote = true;
            }

            return sb.ToString();
        }

        /// "1 use" / "3 uses". A count in the header is what lets the reader skip counting rows, and
        /// getting the plural right costs one call.
        public static string Count(int n, string singular)
        {
            return n + " " + (n == 1 ? singular : singular + "s");
        }

        /// The namespace and any outer types in front of a simple name, or empty when there are none.
        /// A signature cannot show this — `public class ObjectPicker` says nothing about the
        /// namespace it sits in — so it is reported beside the location instead.
        public static string Scope(string fullName, string simpleName)
        {
            if (string.IsNullOrEmpty(fullName) || string.IsNullOrEmpty(simpleName))
            {
                return "";
            }

            var suffix = "." + simpleName;

            return fullName.EndsWith(suffix, System.StringComparison.Ordinal)
                ? fullName.Substring(0, fullName.Length - suffix.Length)
                : "";
        }

        /// Trailing diagnostics — results dropped, a file cap hit, a root that does not exist. Always
        /// last, always prefixed, so they can never be mistaken for a result.
        public static void AppendNotes(FlatBuilder sb, List<string> notes)
        {
            if (notes == null || notes.Count == 0)
            {
                return;
            }

            sb.Line(0, "note: " + string.Join("; ", notes.ToArray()));
        }
    }
}
