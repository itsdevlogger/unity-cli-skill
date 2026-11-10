using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Compilation;

namespace UnityCliMacros
{
    /// Writes one compile-result file per compilation, so an external waiter can block on a recompile
    /// without polling the bridge.
    ///
    /// No single bridge call can span a recompile. `unity command recompile` returns as soon as
    /// compilation is queued, and the Pipeline HTTP server drops any request still in flight when the
    /// domain reloads — it logs `connection forcibly closed` and `Cannot be changed after headers are
    /// sent` — because the reload tears down the assembly serving the request. Polling for the bridge to
    /// answer again only proves a reload happened, and on a compile error there is no reload at all, so
    /// that poll waits out its whole timeout on the one outcome it most needs to report.
    ///
    /// `compilationFinished` fires on success and failure alike, so the file is always written exactly
    /// once per compilation, and it carries the error messages themselves. `DidReloadScripts` then marks
    /// the point where the new assemblies are actually live. A waiter stops on `"stage":"reloaded"`
    /// (success) or `"failed":true` (failure) and cannot hang on either path.
    ///
    /// `failed` is derived only from collected errors, never from `EditorUtility.scriptCompilationFailed`
    /// — that flag is still set from the PREVIOUS compilation while the next one starts, which would make
    /// a waiter stop immediately and report the old failure as the new result.
    [InitializeOnLoad]
    public static class M_CompileSignal
    {
        /// Project-relative path of the signal file, in Library so it is never imported or committed
        public const string SIGNAL_PATH = "Library/unity-cli-compile.json";

        private const int MAX_REPORTED_ERRORS = 20;
        private static readonly List<string> ERRORS = new List<string>();

        static M_CompileSignal()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private static void OnCompilationStarted(object context)
        {
            ERRORS.Clear();
            Write("started", false);
        }

        private static void OnAssemblyCompilationFinished(string assembly, CompilerMessage[] messages)
        {
            if (messages == null) return;

            foreach (CompilerMessage message in messages)
            {
                if (message.type != CompilerMessageType.Error) continue;
                if (ERRORS.Count >= MAX_REPORTED_ERRORS) continue;

                ERRORS.Add(Describe(message));
            }
        }

        private static void OnCompilationFinished(object context)
        {
            Write("compiled", ERRORS.Count > 0);
        }

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            Write("reloaded", false);
        }

        private static string Describe(CompilerMessage message)
        {
            string text = message.message ?? string.Empty;
            if (string.IsNullOrEmpty(message.file)) return text;
            if (text.Contains(message.file)) return text;

            return $"{message.file}({message.line}): {text}";
        }

        private static void Write(string stage, bool failed)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"stage\":\"").Append(stage).Append('"');
                sb.Append(",\"failed\":").Append(failed ? "true" : "false");
                sb.Append(",\"errors\":").Append(ERRORS.Count);
                sb.Append(",\"ticks\":").Append(DateTime.UtcNow.Ticks);
                sb.Append(",\"messages\":[");

                for (int i = 0; failed && i < ERRORS.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Escape(sb, ERRORS[i]);
                }

                sb.Append("]}");

                File.WriteAllText(SIGNAL_PATH, sb.ToString());
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[M_CompileSignal.Write] could not write {SIGNAL_PATH}: {e.Message}");
            }
        }

        private static void Escape(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("X4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
