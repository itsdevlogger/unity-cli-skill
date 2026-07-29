# Troubleshooting

## `unity status` doesn't show a ready instance

Diagnose in this order — each step is cheap and rules out a whole class of problem.

**First: are you mid-recompile?** `No Unity Editor instances found with reachable Pipeline servers`
is also what a **domain reload** looks like from outside, and it arrives with the same three
troubleshooting suggestions as a genuinely dead bridge. If you triggered a `recompile`, entered or
left play mode, or changed a package, this is expected and the next poll will succeed. Do not act on
the suggestions and do not report the Editor as down — wait a few seconds and retry. Everything below
applies only when a retry fails too.

**No instances at all.** Either the Editor isn't running, or it lacks the Pipeline package that
exposes the bridge. Ask the user to open the project in Unity, then re-run `unity status`. If the
Editor is definitely open and still nothing appears:

```bash
unity pipeline list          # is the package installed for this project?
unity pipeline install       # adds com.unity.pipeline to Packages/manifest.json
```

Installing writes to the project manifest and triggers a package resolve plus domain reload, so
confirm with the user before doing it — it's a real change to their project.

**Instance shows `unreachable`.** Its heartbeat is stale: the Editor is busy (importing,
compiling, baking, or mid-domain-reload), or it crashed. Wait and re-check rather than escalating;
a long import will clear on its own.

**Wrong project.** Several Editors can be open at once. Target one explicitly:

```bash
unity command <tool> --project-path "D:/path/to/Project"
```

There is no `--instance host:port` option — per-instance auth tokens mean the CLI must discover
Editors itself. Run from the project directory or pass `--project-path`.

## Command errors

**`COMMAND_FAILED: Compilation Failed`** — a C# error in your eval snippet. The Roslyn diagnostics
give line and column. Three recur:

- `Identifier expected` on a `using` line — the snippet is a **method body**, so `using` directives
  don't compile. Delete the line and fully qualify the names it was importing. The diagnostic
  points at the semicolon and explains nothing, so recognise it by shape.
- `could not be found (are you missing a using directive…)` — a namespace outside the implicit set
  (`UnityEngine`, `UnityEditor`, `System`, `System.Collections.Generic`, `System.Linq`) needs its
  full name: `TMPro.*`, `UnityEngine.SceneManagement.*`, `UnityEngine.UI.*`,
  `UnityEditor.SceneManagement.*`, `System.Text.StringBuilder`, `System.Collections.ArrayList`.
- `'Object' is an ambiguous reference` — write `UnityEngine.Object`.

See `eval-cookbook.md`.

**Unknown macro / unrecognized parameter** — you guessed a name, or this project's Pipeline
package version differs from what you remember. Ask the Editor for the real signature:

```bash
unity command m_find_macro --q "set material properties"
```

Never `unity list` into context — it's ~116KB (~29k tokens), the single most expensive mistake
available in a Unity session. `m_find_macro` answers the same question for ~150 tokens.

An unknown *macro* error from the bridge echoes all ~140 names back at you (~2.6k characters). An
unrecognized *parameter*, by contrast, is worse than an error: the bridge **silently ignores
parameters a macro doesn't declare**, so a `dry_run` or `confirm` flag on a macro that has none
does nothing and still reports success. Confirm the arg exists in the `m_find_macro` output before
trusting it.

**The call is refused without an error** — a destructive tool is missing `confirm true`. Setters
for project settings and anything that deletes, clears or overwrites all require it.

**Object not found** — the handle is stale, wrongly formed, or the name isn't what it looks like.

- Handles don't survive domain reloads (recompile, package change, play-mode transition). Re-query
  with `find_gameobjects` / `find_assets` and use the returned identity.
- **The name may carry whitespace you can't see.** A GameObject called `"Env_Cave "` will not match
  `Env_Cave` under any exact-match lookup, and the two are indistinguishable on screen. Run
  `m_scene_digest --filter <partial>`: a `~` marker means copy the path from the output verbatim
  instead of typing it. This one silently produces a *null reference written into a real field*, so
  suspect it early rather than late.
- Scene objects want `hierarchyPath` or `instanceId`; assets want a project `path` or `guid`.

**Path rejected under the authoring root** — bare paths are confined to the configured root. Check
with `get_authoring_root`, widen with `set_authoring_root --root Assets`.

**The console answer is buried in noise** — don't page through `get_console_logs`. Search it:

```bash
unity command m_find_logs --grep "<class|error code|keyword>" --sev error,warning
```

`grep` matches the stack trace as well as the message, so a class name finds errors thrown inside
it. If nothing matches, widen with `--sev all` before concluding the console is clean — the `b`
field in the response tells you how many entries were actually in the buffer.

## Nothing appears to happen

**The Editor is compiling or importing.** Tool calls queue behind it. `recompile_status` is
unreliable — poll for a macro instead, using the wait loop in `SKILL.md` step 4.

**The change was made but not saved.** Scene edits live in memory. Confirm with
`list_open_scenes` — `isDirty: true` means it's unsaved; call `save_scene` or `save_all`.

**You edited a prefab instance, not the asset.** The change applies to that one instance. Edit the
prefab asset instead — see the prefab recipe in `eval-cookbook.md`.

**You edited during play mode.** Play-mode changes revert on exit. Check `editor_status`.

**The object was inactive and your query skipped it.** `find_gameobjects` needs
`--include_inactive true`; `GetComponentsInChildren<T>()` in eval needs the `true` argument.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | General error |
| 2 | Bad arguments |
| 3 | Authentication failure — the user needs to run `unity auth login` themselves |
| 4 | Precondition not met, typically no active license — the user resolves this in Unity Hub |
| 6 | Command-specific failure (tool error, test failures, build failure) |

Codes 3 and 4 are account and licensing issues. Report them and let the user fix them in Hub or
via `unity auth login` — signing in or activating licenses isn't something to attempt on their
behalf.

## When you're stuck

Any single tool that fights back is usually replaceable by eval, which has the whole UnityEditor
API surface and no parameter-schema mismatch to trip over. If two attempts at a tool call fail for
schema reasons, write the C# instead — see `eval-cookbook.md`.

But **make the two attempts real ones first.** "I don't know what shape this argument takes" is not a
schema mismatch, it's a lookup: `m_find_macro` gives every arg a type token, and `objectref` /
`json{…}` / `enum(…)` say exactly what to pass. Abandoning a built-in for a hand-written
`SerializedObject` loop costs more code, loses the command's undo grouping, and has to be rewritten
next session — so it's the fallback, not the first move.
