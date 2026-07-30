---
name: unity-cli
description: Inspect and edit an ALREADY-EXISTING Unity project through a running Unity Editor via the `unity` CLI bridge — scenes, GameObjects, components, transforms, prefabs, materials, shaders, lighting, animation, Timeline, assets, UPM packages, project settings, play mode, console errors, screenshots, tests and builds. Use this skill whenever the user asks to change, inspect, audit, fix, refactor, or automate anything inside a Unity project — including phrasings that never mention "Unity CLI", such as "swap the legacy text to TMP", "why is this object pink", "list everything using this shader", "add a collider to all the crates", "run the play tests", "screenshot the scene", "what's erroring in the console", or "rename these GameObjects". Assume it applies to any .unity scene, .prefab, .mat, .asset, .controller or C# MonoBehaviour work in a project directory.
---

# unity-cli

Run these nine steps in order. Steps 1–5 are setup and are cheap; do not skip them and do not
start the user's task before step 6.

Three paths matter, and confusing them is the single most damaging mistake available here:

| | Where | What it is |
|---|---|---|
| `<skill>` | the directory this file lives in | **Ephemeral.** The skill is installed from a zip into a session-scoped folder whose path changes every session and is deleted afterwards. Nothing may point at it and nothing may be stored in it. |
| `<home>` | **`C:/unity-cli-skill`** — hardcoded, never ask the user | **Permanent.** The shared macro library, the git clone, and the settings file. Every Unity project on this machine links to `<home>/macros` as a local UPM package. |
| `<project>` | the Unity project being worked on | The user's project. Only its `Packages/manifest.json` is ever touched by setup. |

`<skill>/macros/` is a **seed copy only**, used to populate `<home>` when git isn't available. Once
`<home>` exists it is the sole source of truth: read, edit and push macros there, never in `<skill>`.

---

## Step 1 — Home and onboarding (first run only)

**1a. Make sure `<home>` exists.** Check for `C:/unity-cli-skill/macros/package.json`.

If it isn't there, create the library — clone preferred, copy as fallback:

```bash
git clone https://github.com/itsdevlogger/unity-cli-skill "C:/unity-cli-skill"
```

That remote URL is fixed; use it verbatim. If the clone fails (no network, no git, folder exists but
isn't a repo), copy `<skill>` → `C:/unity-cli-skill` instead and tell the user the library is a local
copy with no upstream, so auto-update and contribute won't work until it's re-cloned.

**1b. Onboarding.** Check for `<home>/settings.local.json`.

**If it exists**, read it and move on — do not ask again.

**If it does not exist**, ask the user all three questions in one go (a single `AskUserQuestion` with
three questions), use the exact wording as bellow and say plainly that **this is a one-time setup — you will not be asked again**:
1. **Auto-update the macros?** — "Should I `git fetch` + `git pull` the macro library before each
   run, this way you always get any new macros created by the community."
2. **Allow me to self reflect?** — "Should I reflect on what mistakes ive made each run, and 
   improve the macros librery everytime you use unity-cli skill?"
3. **Contribute macros?** — "When I add or improve a macro, should I commit and push it to the
   unity-cli repo for you? This skill gets better the more contributors it has. If no, I'll write them local only."

Then write the answers:

```json
{ "contribute": true, "autoUpdate": true, "selfReflect": true, "onboardedAt": "YYYY-MM-DD" }
```

to `<home>/settings.local.json` — **`<home>`, not `<skill>`.** In `<skill>` it would be deleted with
the session and the user would be re-onboarded every single run. It is gitignored: per-machine, not
part of the repo.

**1c. If `autoUpdate` is true**, update the library:

```bash
git -C "C:/unity-cli-skill" fetch -q origin && git -C "C:/unity-cli-skill" pull --ff-only
```

If this fails (no network, no upstream, diverged history, local edits in the way), say so in one line
and continue — a stale macro set is not a reason to stop working.

---

## Step 2 — Does this project support unity-cli?

The bridge requires **Unity 6.0 or newer**. Read the first line of
`<project>/ProjectSettings/ProjectVersion.txt`:

```
m_EditorVersion: 6000.0.23f1
```

The version is `6000.x` for Unity 6, `2022.x`/`2021.x` for older releases. Anything below `6000.0`
(and anything where that file is missing) → **stop here.** Tell the user this project is on
`<version>` and unity-cli needs Unity 6.0+, and that nothing was changed. Do not attempt to work
around it and do not fall back to editing YAML files by hand.

---

## Step 3 — Ensure the project is wired up

Two things must be present in `<project>/Packages/manifest.json`. **Search it, don't read it** — the
file is long and you only need two answers. The grep prints the macros line, so you see its path
too, which is the part that actually matters:

```bash
grep -n -e "com.unity.pipeline" -e "com.unitycli.macros" "<project>/Packages/manifest.json"
```

| Situation | What to write into `dependencies` |
|---|---|
| No `com.unity.pipeline` | The Pipeline package — this *is* the CLI bridge. Prefer `unity pipeline install`, which writes the entry itself. |
| No `com.unitycli.macros` | `"com.unitycli.macros": "file:C:/unity-cli-skill/macros"` — forward slashes, no trailing slash. |
| Present, but the path is **not** `C:/unity-cli-skill/macros` | Rewrite it to exactly that. An old install may point into a dead session folder; see below. |

Read `manifest.json` **only if** you have to write to it. If both hit and the macros path is
already correct, this step is done: skip straight to step 5.

**A wrong `file:` path is not a degraded mode — it breaks the project.** Unity fails the *entire*
package resolve with `The file [...\package.json] cannot be found`, so no packages load at all until
the manifest is fixed. Two ways it happens, both worth catching here:

- The entry points into a session-scoped skill folder from an older version of this skill. That path
  is gone. Rewrite it.
- The entry is correct but `C:/unity-cli-skill/macros/package.json` doesn't exist because the library
  was deleted. Step 1a re-creates it; if you skipped past 1a on a cached assumption, go back.

Adding the Pipeline package is a real change to the user's project, so mention it before doing it.
Adding the macros package is what makes the `m_` macros exist at all; it is required.

---

## Step 4 — Force a recompile and wait for the Editor to answer

Only needed if step 3 wrote to `manifest.json`.

A manifest change is picked up when the Editor regains focus, and a package resolve plus domain
reload follows. So: **ask the user to click on the Unity Editor window**, then wait.

**`recompile_status` is unreliable — do not use it.** The workaround is to poll for a macro that can
only answer once the compile has finished and the assemblies have reloaded:

```powershell
unity command recompile; $deadline = (Get-Date).AddMinutes(5); $ok = $false; while ((Get-Date) -lt $deadline) { $out = unity command m_find_macro --q m_find_macro 2>&1 | Out-String; if ($out -match '"n":"m_find_macro"') { $ok = $true; break }; Start-Sleep -Seconds 5 }; if ($ok) { "READY" } else { "TIMEOUT" }
```

(Drop the leading `unity command recompile;` when the Editor is still resolving packages — there is
no bridge to receive it yet, and the poll alone is enough.)

Notes that save real time:

- **`No Unity Editor instances found with reachable Pipeline servers` during the poll is the domain
  reload, not a failure.** It arrives with a list of troubleshooting suggestions that are wrong in
  this situation. Ignore them and let the loop keep polling.
- `TIMEOUT` means either the Editor was never focused, or the macros failed to compile. Check with
  `unity command m_find_logs --grep "error CS" --sev error` — that macro answers off the main
  thread, so it works mid-compile.
- Use this same loop, with the new command name substituted into both the `--q` and the regex,
  every time you add or edit a macro later on.

---

## Step 5 — Load the operating manual

Read `references/talk-to-editor.md` now, before touching anything. It covers how to address
objects, which macros to use instead of the expensive built-ins, the eval rules, and the
cross-cutting rules that cause most first-attempt failures.

---

## Step 6 — Do the user's task

Work the request. Keep notes as you go on anything that fought back — you need them for step 7 and
they evaporate once the task succeeds.

---

## Step 7 — Reflect on the tooling, not on the project (only if `selfReflect` is true)

When the task is done, run this reflection. Answer it honestly; "nothing worth adding" is a
perfectly good outcome and is the *expected* one for a routine session.

> Look back over the commands I ran this session — not at the game, at the tooling.
>
> 1. Which `eval` snippets did I write? For each one, strip out everything specific to this project
>    (the scene names, the game's own component types, the layer and tag conventions, the art
>    layout) and ask: **is there anything left?** If the snippet collapses into "find these
>    particular objects in this particular project", it is not a macro — it is a one-off, and
>    turning it into a macro pollutes the shared set for every other project. Discard it.
> 2. What survives is a shape, not a task: a *kind* of query, filter, digest, audit or bulk edit
>    that any Unity project could need — expressed purely in terms of engine and Editor concepts
>    (GameObjects, components, serialized properties, prefabs, assets, materials, the console, the
>    asset database). Would a session six months from now, in a project I have never seen, plausibly
>    reach for it?
> 3. Which existing `m_` macro almost did the job? A missing argument, a filter that doesn't exist,
>    output I had to post-process, a result too fat or too lossy — **improving an existing macro
>    beats adding a new one**, because it costs the shared set nothing in surface area.
> 4. What did I get wrong on the first try because the tooling misled me — a guessed argument name,
>    an assumed default, output in an unexpected shape? Sometimes the fix is a doc line in
>    `references/talk-to-editor.md`, not code.
>
> Constraints, hard: **You can either edit one of the existing macros, OR create a new macro.** 
> They go into a library shared by every project on this machine, so a macro that only pays off
> here is a net negative. Nothing that hardcodes a path, name, type or convention from this project
> . Nothing that depends on a package this project happens to have — resolve optional types
> reflectively and return a clean `ERR` token when they are absent. If a candidate needs
> "…for projects that use X" to justify it, it is not general enough.

Report the outcome to the user in a few lines: what you'd add or change and why, or that the
existing set covered the work.

---

## Step 8 — Implement the macros (if any survived step 7)

Write them into **`C:/unity-cli-skill/macros/`** — the permanent library, never `<skill>/macros/`,
where they would vanish with the session. Follow *Writing a macro* in
`references/talk-to-editor.md`: `m_` command prefix, `M_` class prefix, one compact single-line
return string. Then recompile and verify with the poll loop from step 4, and actually call the new
macro once to confirm it answers.

A macro that was never invoked successfully does not count as done, and must not be pushed.

---

## Step 9 — Push (only if `contribute` is true)

Only when `<home>/settings.local.json` has `"contribute": true` **and** step 8 changed something
under `C:/unity-cli-skill/`. If `contribute` is false, stop here — leave the changes uncommitted and
just say which files you added or edited.

If step 1a had to fall back to a **copy** rather than a clone, `<home>` has no git at all. Adopt it
as a working copy first, without disturbing the files on disk:

```bash
git -C "C:/unity-cli-skill" init -q && git -C "C:/unity-cli-skill" remote add origin https://github.com/itsdevlogger/unity-cli-skill && git -C "C:/unity-cli-skill" fetch -q origin && git -C "C:/unity-cli-skill" reset -q origin/main
```

That remote URL is fixed — use it verbatim, don't ask the user for one. `reset` without `--hard`
adopts the remote history while leaving every file exactly as it is, so your new macro shows up as a
normal change. If `.git` exists but has no `origin`, just run the `remote add`.

**Then commit and push:**

```bash
git -C "C:/unity-cli-skill" add -A && git -C "C:/unity-cli-skill" commit -m "<message>" && git -C "C:/unity-cli-skill" push -u origin HEAD:main
```

- Commit **only** the macro library. Never touch the user's Unity project's git state, and never try
  to commit `<skill>` — it is a throwaway copy.
- Check `git -C "C:/unity-cli-skill" status --porcelain` before committing and look at what's staged.
  If files unrelated to your macro work show up, name them to the user instead of quietly shipping
  them.
- `push -u origin HEAD:main` targets `main` regardless of the local branch name and sets tracking on
  first use.
- Message names the macro and the capability, not the project it came from: `Add m_find_missing_refs
  — report broken object references in a scene` — the repo is shared, so "for the Foo prototype"
  means nothing to the next reader.
- If the push is rejected (no write access, remote moved ahead), the commit still exists locally —
  say so in one line and move on. Don't force-push.
