# Unity Editor control (existing projects)

Drive a **running Unity Editor** to inspect and modify a project. Scope is deliberately
editing-only — creating projects, installing editors/modules, templates and licensing are out of
scope; the user handles those in Unity Hub.

## Preflight (one command)

```bash
unity status
```

`state: ready` with the right project path means you're connected — start working.
Anything else (no instances, unreachable, wrong project) → `references/troubleshooting.md`.

## How you talk to the Editor

Every capability is a **macro** — a static C# method tagged `[CliCommand]` — invoked as:

```bash
unity command <macro> --<param> <value>
```

There are ~140 built-in macros plus the shared `m_` set. **Never run `unity list`** — it dumps
~116KB of JSON schemas (~29k tokens) and is the single largest avoidable cost in a Unity session.
Use `m_find_macro` instead.

**The `m_` prefix tells you who owns a macro.** A name starting with `m_` lives in the shared macro
library at `C:/unity-cli-skill/macros/`, linked into the project as a local UPM package: you can
read its source, change it, and add to it. Anything else is built into the Pipeline package and is fixed
— a shortcoming there is something to work around, not to fix. So when a macro is awkward to use or
returns the wrong shape, check the prefix first: `m_` means the right answer is usually to improve
the macro.

**The `m_` macros are shared across every project on this machine.** They are not part of the Unity
project you happen to be in, and edits to them land in every other project too. That is the point —
and it is also the constraint: never write project-specific knowledge (a scene name, a layer
convention, a game's own types) into an `m_` macro.

## The workflow — follow this every time.

**1. Look for an existing macro.**

```bash
unity command m_find_macro --q "keywords describing the job"
```

Always start here, even when you think you know the name. Signatures drift between Pipeline
package versions, and a guessed parameter costs a failed round trip.

**2. Nothing relevant? Decide: one-off or recurring.**

A one-off is specific to this request — "which of these 43 walls has no collider", "rename these
six objects". A recurring need is one you (or the next session, in a different project) will hit
again — a search, a digest, an audit, any shaping of Unity data into a compact answer.

**3. One-off → write C# and run it through `eval`.** See *Running C#* below.

**4. Recurring → write a macro.** Add a file under `C:/unity-cli-skill/macros/` — the permanent
shared library, never the skill's own folder, which is deleted when the session ends — force a
recompile, then call it like any other macro. See *Writing a macro* below.

## The macros you will use constantly
This is not the complete list, but only the most used ones.

### `m_find_macro` — find any macro by keyword

```bash
unity command m_find_macro --q "material shader assign" --m 5
```

| Arg | Default | Meaning |
|---|---|---|
| `q` | *required* | Keywords, space- or pipe-separated. Prose is fine; stop words are dropped. |
| `m` | `5` | Max results (1–50). |

Scores name hits far above description hits, exact tokens above prefixes above substrings, and
rewards matching every term. Returns compact JSON:

```json
{"k":"<legend>","r":[{"n":"eval","s":114,"d":"Evaluate C# code…","a":[["code:string*","C# code to evaluate"],["timeout:int=5000","Timeout in ms"]]}]}
```

Read an arg token as `name:type` followed by `*` required, `=value` optional with that default, or
`?` optional with no default. Split at the **first** `:` and the **first** `=` — a default can
contain either. `NONE` means no match; broaden the keywords before concluding the capability doesn't
exist.

The type is the part that decides whether a call works on the first try:

| Token | What to pass |
|---|---|
| `objectref` | A handle string, **not** a name — see *Addressing objects* below. |
| `json` | Inline JSON. `json{a,b,c}` lists the keys it expects. |
| `enum(a\|b\|c)` | One of those literal values. |
| `string[]` | A JSON array: `--names '["A","B"]'`. |

### `eval` / `eval_file` — run C# in the live Editor

The escape hatch for anything with no macro, and the only way to make bulk work fast: one eval
that loops over 200 objects beats 200 round trips at ~1.15s each.

```bash
unity command eval --code "return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;"
```

| Macro | Args |
|---|---|
| `eval` | `code*` (C# source), `timeout=5000` (ms; hard ceiling 30000, above which it's a Bad Request) |
| `eval_file` | `file*` (path to a .cs file), `timeout=5000` |

**Use `eval_file` unless the code is a single line with no string literal in it.** This is a rule, not
a preference. Any `--code` carrying a C# string literal gets mangled by shell quoting and returns a
handful of parse errors that have nothing to do with your code. Beyond that, the response echoes every
parameter, so `--code` bills you for your script twice while `eval_file` echoes only the filename —
and iterating on a compile error becomes an edit rather than a re-paste.

Write the snippet to the scratchpad directory (**never under `Assets/`** — a stray script there
triggers a recompile), then run it.

**The snippet is a method body, not a file.** Full rules and recipes in
`references/eval-cookbook.md`; the three that bite every time:

- **`using` directives do not compile** — they parse as using-*statements* and fail with
  "Identifier expected". Fully qualify instead.
- **In scope already:** `UnityEngine`, `UnityEditor`, `System`, `System.Collections.Generic`,
  `System.Linq`. Everything else needs its full name (`UnityEngine.SceneManagement.SceneManager`,
  `System.Text.StringBuilder`, `TMPro.TextMeshProUGUI`) — and `m_type_info` tells you what that full
  name is, which is cheaper than guessing a namespace and paying for the compile error.
- **`Object` is ambiguous** between `UnityEngine.Object` and `System.Object` — always qualify.

End with `return <one compact string>;`. That value is all you get back, so assemble a summary
rather than serializing a graph.

### `m_find_logs` — search the console

```bash
unity command m_find_logs --grep NullReference
unity command m_find_logs --grep "PlayerController|Inventory" --sev error,warning --since 5m
```

| Arg | Default | Meaning |
|---|---|---|
| `grep` | — | Regex to keep, matched over the message **and** the stack trace, so a class name finds errors raised inside it. |
| `exclude` | — | Regex to drop, same reach. |
| `sev` | `error` | Comma list of `error`\|`warning`\|`log`, or `all`. `error` includes Exception and Assert. |
| `since` | — | `10m`, `2h`, `3d`, or an ISO-8601 timestamp. |
| `max` | `25` | Distinct messages returned. |
| `stack` | `0` | Frames per entry. `0` returns only the frame that names project code. |
| `width` | `220` | Message truncation; `0` disables. |
| `dedupe` | `true` | Collapse identical repeats into one entry with a count. |
| `match_case` | `false` | Make the regexes case-sensitive. |

Use this rather than `get_console_logs`, which has no text search and returns full stack traces —
one unfiltered call on a real project is 10–30k tokens of engine-internal frames. `m_find_logs` runs
off the main thread, so it still answers while the Editor is compiling.

Typical loop: `unity command clear_console` → reproduce → `m_find_logs --grep X`, so everything
returned is known to be fresh.

### `m_scene_digest` — see the shape of a scene, or search it

```bash
unity command m_scene_digest --depth 2
unity command m_scene_digest --filter TextMeshProUGUI
unity command m_scene_digest --root "/Level_01/Environment" --depth 3
```

| Arg | Default | Meaning |
|---|---|---|
| `depth` | `0` (auto) | Tree depth. Auto = 3 normally, whole scene when `filter` is set. |
| `root` | — | Start at one object instead of the scene roots. |
| `scene` | active | Open scene by name or path. |
| `filter` | — | Substring matched against object names **and** component type names; returns a flat list of hierarchy paths. |
| `collapse` | `3` | Fold runs of N+ identical siblings. `0` disables. |
| `cap` | `4000` | Objects walked before truncating. |

Use this rather than `get_scene_hierarchy`, which returns the entire tree. Start at `--depth 1` on a
scene you don't know — it names the roots for a couple of hundred tokens, and you can then aim
`--root` at the one you want.

Outline lines are `"<depth> <label>"`. The markers:

| Marker | Meaning |
|---|---|
| `!` | Inactive (`activeSelf`). |
| `@` | Prefab-instance root. |
| `~` | **The name contains whitespace you cannot retype** — a trailing space, a double space, a tab. Copy the name from the output; don't type it out. |
| `xN` | N identical adjacent siblings folded into one line. |
| `(=path)` | This subtree is identical to the one at `path`, so it isn't repeated. |
| `+N` | Children not shown at this depth. |
| `[…]` | Components; Transform omitted, `MISSING_SCRIPT xN` flags a broken MonoBehaviour reference. |

`~` matters more than it looks. Names are lookup keys and every lookup here is an exact match, so a
name that reads clean but isn't will resolve to nothing — and a null reference written into a
serialized field looks like success. In filter mode `~` covers the **whole path**, so it fires when
any ancestor's name is unclean, not just the match's own.

`filter` also fills a real gap: `find_gameobjects --name` is an **exact whole-name match**, so
there is no substring search anywhere else in the command set. Its output paths are valid
`hierarchyPath` targets — pass them back verbatim (strip only the marker characters).

### `m_inspect` — what one object is wired to, and what it overrides

```bash
unity command m_inspect --target "/Players/Player_01/FollowCamera"
unity command m_inspect --target "/Players/Player_01/FollowCamera" --type Cinemachine
```

| Arg | Default | Meaning |
|---|---|---|
| `target` | *required* | `objectref`. A Component handle resolves to its owner. |
| `type` | — | Only components whose type name contains this substring. |
| `refs` | `true` | Report object-reference properties. |
| `values` | `false` | Also report plain serialized values — much more output. |
| `children` | `false` | Inspect every descendant too. |
| `max` | `120` | Property entries before truncating. |

Every object-reference property comes back as `property → target`, where the target is a full
hierarchy path (`:Type` appended when the reference points at a *component* rather than the
GameObject), an asset path, or `null`. That is the check that tells you a duplicated object references
its own copies rather than the original's — which is the standard way a duplicate silently comes out
wrong, and is invisible in the scene outline.

For a prefab instance it also returns `pf`: the source asset path, the instance root, counts of
overridden objects / added / removed components, and every overridden property path. `m_scene_digest`
only tells you *that* something is an instance; this is what the prefab rule below needs you to know
before you edit one.

### `m_type_info` — what a type is actually called

```bash
unity command m_type_info --q CinemachineVirtualCamera
```

`q` is a name or partial name, `m` caps results (default 8). Returns full name, assembly,
assembly-qualified name (for `Type.GetType`), whether `eval` can name it unqualified, kind and base
type.

Reach for it before writing any `eval` that names a type outside the five open namespaces. A guessed
namespace is a compile error and a wasted round trip — `Cinemachine.CinemachineVirtualCamera` doesn't
exist in a Cinemachine 3 project, it's `Unity.Cinemachine.CinemachineVirtualCamera`. In the Editor
`eval` compiles against **every loaded assembly**, so a type this macro can find is always reachable;
only the qualification is ever the problem. Runs off the main thread, so it answers during a compile.

### `screenshot` — look at the result

```bash
unity command screenshot --view game --output "<scratchpad>/shot.png"
```

`view` is `game` (default) or `scene`; `output` defaults to a timestamped file under
`<project>/Temp/pipeline-screenshots/`; `width`/`height` default to the view's current size.
It returns **just the file path** — then `Read` that file.

`capture_game_view` / `capture_scene_view` return the PNG **base64-inline** (~55KB of response for
a 320×180 frame) even when given a `save_path`, and their `save_path` is project-relative so it
lands under `Assets/` and triggers an import. Reach for them only for what `screenshot` can't do:
rendering one named camera (`capture_game_view --camera <name>`).

## Writing a macro

When a need will recur, make it a macro. Cost is one file plus a recompile; the payoff is that
every future session — in *any* project on this machine — finds it through `m_find_macro` instead of
rebuilding the snippet.

**1. Write the file** at `C:/unity-cli-skill/macros/M_<Thing>.cs`. Read the existing macros in that folder
first — match their structure, naming and comment style.

```csharp
using Unity.Pipeline.Commands;
using UnityEngine;

public static class M_Example
{
    [CliCommand("m_example", "One line saying what this returns — this is what m_find_macro searches")]
    public static string Example(
        [CliArg("target", "What it operates on", Required = true)] string target,
        [CliArg("max", "Maximum results")] int max = 10)
    {
        return "…";
    }
}
```

- **The command name must start with `m_`, and the class with `M_`.** This is the only signal
  separating shared skill-owned macros from the fixed built-ins, and the whole improvement loop
  depends on it — a macro without the prefix looks like something you can't change. No exceptions.
- **Project-agnostic only.** The folder is a shared package: no hardcoded scene/asset paths, no
  dependency on a type that only this game defines, no assumption about render pipeline or package
  set that isn't checked at runtime. If a type may be absent, resolve it reflectively and return a
  clean `ERR` token instead of failing to compile in the next project.
- The method must be **`static`**; the class needs no attribute. The macros assembly references
  `Unity.Pipeline.Editor`, so Editor-side types (`ConsoleLogBuffer`, `PrefabUtility`) are available.
  A macro needing a type from another package must have that package's assembly added to
  `C:/unity-cli-skill/macros/UnityCliMacros.asmdef` — and that reference then has to exist in every project, so
  prefer reflection.
- `Required = true` on a `[CliArg]` makes the CLI reject a call that omits it; otherwise the C#
  default value is used and reported by `m_find_macro`.
- Add `MainThreadRequired = false` **only** when the work touches no Unity main-thread API — it
  buys the ability to answer while the Editor is compiling (`m_find_logs` does this).
- Name and description are the whole search surface. Write them for the query a future session
  would actually type, and say what the macro *returns*, not just what it does.

**2. Return one compact single-line string.** The CLI renders the return value as one cell of a
tab-separated row, so **tabs and newlines are destroyed in transit**. Compact JSON with
single-letter keys and a `"k"` legend explaining them is the convention in this folder — it escapes
free text instead of mutilating it, and stays readable. On bad input, `Debug.LogError` the detail
and return a short `ERR <reason>` token. (Returning an object also works — the CLI serializes it —
but you lose control of the key names and the payload gets noticeably fatter.)

**3. Compile, then call it.** A new macro does not exist until the Editor recompiles. Use the
recompile-and-wait loop in `SKILL.md` (step 4) — **do not use `recompile_status`**, it does not
report reliably; poll `m_find_macro` for the new command name instead. If the new macro never
appears, the compile failed: `unity command m_find_logs --grep "error CS" --sev error`.

## Improving the macros

The `m_` set is meant to get better with use. Notice friction as you work: an argument you had to
guess, output you had to post-process, a filter that didn't exist and forced an `eval`, a result too
fat or too lossy to use. Act on it *there and then*, while the friction is in front of you — **improving
an existing macro beats adding a new one**, because it costs the shared set nothing in surface area.
Sometimes the fix is a doc line in this file rather than code.

Only if it passes the generality test: strip out everything specific to this project (scene names, the
game's own component types, layer and tag conventions, the art layout) and ask whether anything is
left. If it collapses into "find these particular objects in this particular project", it's a one-off —
`eval` it and move on. What justifies a macro is a *shape*: a kind of query, filter, digest, audit or
bulk edit, expressed purely in engine and Editor concepts, that a session six months from now in a
project you've never seen would plausibly reach for. If a candidate needs "…for projects that use X"
to justify it, it isn't general enough.

Keep the scope honest: an `m_` macro can be changed, but a built-in can't, so a complaint about a
built-in is only actionable as "wrap it in a new `m_` macro".

## Cross-cutting rules

These apply in every domain and cause most first-attempt failures.

**Use PowerShell for `unity command` on Windows.** Git Bash rewrites any argument that starts with
`/` into a Windows path, so `--root "/Level_01"` silently arrives as
`C:/Program Files/Git/Level_01` and the command reports "not found". Every hierarchy path hits
this. PowerShell passes them through untouched.

**Use `--flag`, never `-flag`.** A single dash is parsed as something else and the value is silently
dropped, so the macro runs with the default. `-m 3` looks like it worked; `--m 3` actually did.

**Addressing objects (`objectref`).** Any arg typed `objectref` takes a **handle string**, parsed by
this grammar in priority order:

| Form | Resolves as |
|---|---|
| `/Root/Child/Leaf` | Scene hierarchy path. Leading slash. |
| `Assets/Art/Wall.mat` | Asset path. Also `Packages/…`. |
| `guid:<32 hex>` or `guid:<32 hex>:<fileId>` | Asset by GUID, optionally a sub-asset. |
| `instanceId:<n>`, or a bare positive integer | Loaded object by instance id. |
| `GlobalObjectId_V1-…` | Canonical global id. |
| **anything else** | Falls through to *hierarchy path* — so a bare name is looked up as a root object, and typos fail as "not found" rather than as a parse error. |

Get handles from `find_gameobjects` / `find_assets` / `m_scene_digest --filter` / `m_inspect` and pass
them back verbatim rather than reconstructing them. A returned `AuthoringResult` field feeds straight
back in as an `objectref`.

**Setting an object reference.** `set_component_properties` takes `properties` as JSON mapping
property name to value, and a reference value is just a handle string:

```powershell
unity command set_component_properties --target "/Players/Player_01/FollowCamera" --type CinemachineVirtualCamera --properties '{"m_Follow":"/Players/Player_01/PlayerCapsule/CameraRoot"}'
```

Single-quote the JSON in PowerShell so the double quotes survive. Vectors and colors are arrays
(`{"m_LocalPosition":[0,1,0]}`). Confirm the result with `m_inspect --target … --type <Type>` rather
than trusting the success flag — this is a two-command operation, not one. Hand-rolling
`SerializedObject` in `eval` for a single assignment is strictly more work than this.

**Find before you dump.** `find_gameobjects` (filter by name/tag/component type) returns just the
matches; `m_scene_digest --filter` covers the substring case it can't. Never `get_scene_hierarchy`.

**Destructive macros refuse by default.** Anything that deletes, clears, overwrites, or changes
project settings requires `confirm true`. Many also accept `dry_run true` — use it to preview a
bulk or irreversible change, and show the user that preview when the change is broad or hard to
undo.

**But check the flag exists.** `confirm` and `dry_run` are per-macro, not universal, and the bridge
**silently ignores** a parameter a macro doesn't declare — `set_transform --dry_run true` reports
success and moves the object anyway. `m_find_macro` lists the declared args; check there before
trusting a guard. A guard you believe is protecting you but isn't is the worst failure mode here.

The same silence applies to a *malformed* value: a parameter whose value fails type conversion falls
back to its default and the call reports success. If a result looks like the flag was ignored, it
probably was.

**Edits are in memory until saved.** Scene changes live in the open scene; call `save_scene` (or
`save_all`) when the change is complete. Asset writes commit immediately. Verify with
`list_open_scenes` — `isDirty: false` confirms the save landed.

**Don't save from inside an `eval`.** `EditorSceneManager.SaveScene` mid-snippet returns success and
then any later mutation in the same snippet re-dirties the scene, so the save you were told happened
didn't cover the change you cared about. Mark the scene dirty in the eval, then `save_scene` as the
*next* command, then `list_open_scenes` to confirm.

**Keep the user's undo working.** In `eval`, go through `UnityEditor.Undo` (`Undo.RecordObject`,
`Undo.AddComponent`, `Undo.DestroyObjectImmediate`) instead of raw `Object.DestroyImmediate` /
`AddComponent`. The built-in macros already do this. It costs nothing and preserves Ctrl+Z on work
you didn't author.

**Respect prefabs.** Editing a prefab *instance* in a scene creates an override that other
instances won't get, and can silently fail on nested prefabs. To change every instance, edit the
prefab asset (`PrefabUtility.LoadPrefabContents` / `SaveAsPrefabAsset` in eval). Detect instances
with `PrefabUtility.IsPartOfPrefabInstance` — or `m_inspect`, which reports the source asset and
every existing override in one call — and tell the user which objects you skipped and why. A partial
conversion reported as complete is worse than a refusal.

**Scripts need a compile before use.** A newly written MonoBehaviour type doesn't exist until the
Editor recompiles: use the wait loop from `SKILL.md` step 4, then `attach_script`. Domain reloads
also drop `eval` state, so don't stash values across one.

**One broad query beats many narrow ones.** The Editor dispatches **one command per ~1.15s** no
matter the transport, so consolidating into a single eval is the only thing that makes bulk work
fast. Also omit `--format json` — the default human format carries the identical payload in the
`Result` column at roughly a third the size.

## Where to look next

| The task involves | Read |
|---|---|
| Writing C# for `eval`: syntax rules, undo discipline, bulk-edit / prefab / asset-sweep recipes | `references/eval-cookbook.md` |
| Anything failing, disconnected, or behaving unexpectedly | `references/troubleshooting.md` |

Everything else: ask `m_find_macro`.

## Reporting back

State what changed, what was skipped and why, and whether the scene was saved. When you convert or
bulk-edit, a per-object one-liner (name → outcome) is far more useful than a count, because it lets
the user spot the one object that went wrong. If you verified the result with a follow-up query,
say so plainly; if you didn't, don't imply you did.
