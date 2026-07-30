# unity-cli: a Claude skill for driving the Unity Editor

Lets Claude inspect and edit an **already-existing** Unity project by talking to your **running Unity
Editor** over the official [Unity CLI](https://docs.unity.com/en-us/unity-cli). Scenes, GameObjects,
components, transforms, prefabs, materials, shaders, lighting, animation, Timeline, assets, UPM
packages, project settings, play mode, console errors, screenshots, tests and builds.

It does **not** create projects, install Editors, or handle licensing. That stays in Unity Hub.

---

## Read this before installing

Four things about this skill that aren't obvious, and that you should agree to before it runs:

1. **Windows only.** The macro library path is hardcoded to `C:/unity-cli-skill` throughout the skill.
   It will not work on macOS or Linux as-is.
2. **It creates `C:/unity-cli-skill` on your machine.** A Claude skill lives in a temporary,
   session-scoped folder that's deleted afterwards, so on first run the skill `git clone`s this repo to
   `C:/unity-cli-skill` and uses that as the permanent home for the shared macro library. That clone,
   not the installed skill copy, is the source of truth from then on.
3. **It edits your project's `Packages/manifest.json` on the first run, per project.** Two dependencies get added:
   `com.unity.pipeline` (the CLI bridge itself) and `com.unitycli.macros`, linked as a local package
   from `file:C:/unity-cli-skill/macros`. That means **every Unity project on this machine
   shares a single macro library**, which is the point, and also the constraint. Claude will tell you before
   it writes to the manifest.
4. **It asks two setup questions on first run, once per machine**. They're explained below.

Requires **Unity 6.0 or newer**. The CLI bridge doesn't exist on older versions, and the skill will stop rather than fall back to hand-editing YAML.

---

## Prerequisites

**1. The Unity CLI**, installed and signed in. In PowerShell:

```powershell
$env:UNITY_CLI_CHANNEL='beta'; irm https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1 | iex
```

Reopen your terminal, then:

```powershell
unity --version
```

```powershell
unity auth login
```

The CLI is Unity's own tool and is currently on the beta channel. [Unity's install
docs](https://docs.unity.com/en-us/unity-cli/use-unity-cli) are the authoritative source if that
command changes.

**2. The Unity Pipeline package** in the project you want to work on. Open the project in Unity, then
from the project directory:

```powershell
unity pipeline install
```

```powershell
unity pipeline list
```

`Pipeline: Installed` means you're set. Requires Unity 6.0+. The skill can do this step for you and
will ask first, since it modifies your project. See the [Unity Pipeline package
docs](https://docs.unity.com/en-us/unity-production-pipeline/local-tools-cli/unity-pipeline-package).

**3. Git**, for the `C:/unity-cli-skill` clone and macro updates.

---

## Install

**If you might contribute a macro back, fork this repo first** using `Fork` at the top right of the
GitHub page. Contributions go through your fork (see [Contributing](#contributing)), and having it up
front saves a detour later. Skip it if you just want to use the skill.

**Download it:** `Code ▸ Download ZIP` on GitHub, from your fork or from this repo, since the contents
are the same.

**Then install the ZIP as a skill:**

*Claude Desktop / Cowork:* open Settings, find the Skills section, and upload the ZIP.

Restart Claude, then ask for something Unity-shaped. The skill triggers on its own.

---

## First run

First run might cost you some extra tokens, becuase of the setup process. 
Open your project in claude code or `code` section of claude desktop, and aske claude to do something.
With the Editor open on your project, Claude will:

1. Clone this repo to `C:/unity-cli-skill` (see point 2 above).
2. Ask you the two setup questions, and save your preferances to `C:/unity-cli-skill/settings.local.json`
3. Check the project is on Unity 6.0+ and wired up with unity cli, adding the two packages if needed.
4. Ask you to **click on the Unity Editor window**, because a manifest change is only picked up when
   the Editor regains focus. Then it waits out the package resolve and domain reload, which can take a
   couple of minutes.
5. Gets on with what you actually asked for.

Steps 1 to 4 are first-run only. Later sessions start at step 5.

### The two questions

Answers are stored in `C:/unity-cli-skill/settings.local.json`, which is per-machine, gitignored, and
never committed. Delete that file to be asked again.

| Question | Yes | No |
|---|---|---|
| **Auto-update the macros?** | Before each run, fetches this repo and rebases `C:/unity-cli-skill` on top of it, so you pick up macros other people have contributed while keeping your own. Never discards your local work: if the rebase can't complete, Claude aborts it, says so, and carries on. | The library stays exactly as you left it. Update it yourself with `git pull --rebase upstream main` whenever you feel like it. |
| **Contribute macros?** | When Claude writes or improves a macro while working, it commits it, pushes a branch to **your fork**, and opens a pull request — asking you first, every time. Nothing is ever pushed to the shared repo. | Macros are written to `C:/unity-cli-skill/macros` and left uncommitted, local to your machine. |

Claude decides whether something is a macro *while* it works: a one-off query runs through `eval` and
is thrown away, a shape that any project could reuse becomes a macro. Most sessions produce none, and
that's the expected outcome.

---

## What's in here

| | |
|---|---|
| `SKILL.md` | The instructions Claude follows: setup, the seven steps, the contribution flow. |
| `macros/` | The shared `m_*` macro library, a Unity UPM package (`com.unitycli.macros`) that gets linked into each project. Compact, token-cheap commands for searching, digesting and auditing a project. |
| `references/talk-to-editor.md` | The operating manual: addressing objects, which macros to prefer, `eval` rules, how to write a macro. |
| `references/eval-cookbook.md` | Working C# snippets for running through `eval`. |
| `references/troubleshooting.md` | Bridge not responding, compile errors, stale handles, exit codes. |

Macros are deliberately **project-agnostic**: no hardcoded scene names, layer conventions, or a game's
own types, and no hard dependency on optional packages. A macro that only pays off in one project
makes the shared set worse for everyone.

---

## Contributing

Open source, and the whole point is that it gets better with contributors. The macro library is only
as good as the number of Unity projects it's been sharpened against.

The shared repo takes **pull requests only**; nobody pushes to it directly. With `contribute: yes`,
Claude does the mechanical part: it commits the macro, pushes it to a `macro/<name>` branch on your
fork (it'll ask for the fork URL once and remember it), and (after checking with you) opens the PR
with `gh`. If `gh` isn't set up it hands you the link instead.

Your local `C:/unity-cli-skill` stays on `main` throughout. Claude never checks out a branch there,
because that directory is a live Unity package: switching away would delete the macro off disk and out
of every project on your machine. One branch per contribution, but they only exist on your fork, so
nothing piles up locally. GitHub doesn't auto-delete fork branches — merged or closed, they stay until
someone removes them, and they're harmless if you don't.

Good contributions to make:

- A macro that answers a *kind* of question any Unity project could ask.
- An improvement to an existing macro, such as a missing filter or a better output shape. This beats
  adding a new one, because it costs the shared set nothing in surface area.
- A line in `references/` that would have saved you a failed first attempt.

