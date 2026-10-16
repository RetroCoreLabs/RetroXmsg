# Contributing to RetroXmsg

Thanks for looking. This file says how to get a build running, what the code is expected to look
like, and what will get a change sent back.

---

## Getting a build

| # | Requirement | How to check |
|---|---|---|
| 1 | The .NET SDK version pinned in `src/global.json` | `dotnet --info` |
| 2 | PowerShell 7 or newer | `$PSVersionTable.PSVersion` |
| 3 | **RetroIO, cloned beside this repository and packed** | see the next section - this one is not optional |

```powershell
# from the repository root
.\src\build.ps1
```

`build.ps1` builds, runs the tests, and publishes each application as a single self-contained
`.exe` with no loose DLLs beside it and no .NET install needed on the target machine. It **stops
before publishing if any test fails**. Useful switches: `-Configuration Debug`, `-SkipTests` when
you know why, `-Clean`.

### The part that will stop you first: the local package feed

This repository does not reference RetroIO by project. It references it as NuGet packages, pinned
in `Directory.Packages.props` to version `0.1.0-local.*`. Those packages are not on nuget.org
yet, so a plain `dotnet restore` on a fresh clone cannot find them.

What you need:

```
<the folder you work in>
├── RetroIO/          <- cloned beside this repository
├── RetroXmsg/
└── localfeed/        <- created by the command below
```

```powershell
# in the RetroIO clone
powershell -File src\eng\publish-local.ps1
```

That packs every RetroIO package into `../localfeed` with a fresh timestamp. `nuget.config` in
this repository points at `..\localfeed`, which resolves against the folder holding the config
file - so the feed is found with no absolute path anywhere. A fresh timestamp on every pack is
deliberate: it is what stops the NuGet cache serving a stale DLL after a RetroIO change.

> [!IMPORTANT]
> Pass **`-nodeReuse:false`** on every `dotnet build` and `dotnet test` you run by hand.
> `build.ps1` already does. Node reuse is on by default and leaves MSBuild worker processes
> behind that hold a lock on the output DLL; the next build then quietly keeps the old binary and
> **the tests report green against stale code.** This has actually happened here, which is why it
> is written down.

---

## What the code is expected to look like

 - **No LINQ, and avoid `foreach`.** Use index-based `for` loops. Where a hot path allocates, use
   `Span<T>`, `ArrayPool<T>`, `stackalloc` or a ring buffer. These are emulation and protocol
   paths; an enumerator allocation per packet is a real cost, not a theoretical one.
 - **Unit tests only**, in the existing test projects. Never add a standalone program that prints
   something and exits - it cannot fail a build.
 - **No FluentAssertions.** Use the test framework's own asserts.
 - **Keep the comments, and add more.** A comment that records a datasheet reference, a
   specification clause, or the reason a thing is NOT done the obvious way is the most valuable
   line in the file. Never delete one because a style guide prefers less; replace one only when it
   has become factually wrong.
 - **Plain words.** In code, comments, commit messages and documentation alike. No jargon where a
   normal word works.
 - **Run `dotnet format`** if whitespace drifts.

### Naming

Package id = root namespace = folder name. The standard is `NAMING.md`, kept beside the
repositories. The organisation is **RetroCoreLabs**; the old name `HackerCorpLabs` is retired and
must not appear in anything new.

---

## Documentation changes

```powershell
python src\eng\check-docs.py
```

It must exit 0. It fails on a relative link that does not resolve, a relative link that escapes
the repository root, a machine-specific absolute path, and a Mermaid diagram that is malformed or
breaks `MERMAID_COLOR_STANDARDS.md`.

> [!WARNING]
> **Never put an absolute path in anything committed here.** A drive letter or a home directory is
> correct on exactly one machine, and this repository is public. A path inside the repository goes
> repo-relative; a sibling repository is named, not located; anything outside is described. A
> variable such as `%USERPROFILE%` or `~` is fine - that is the portable way to name a per-user
> location.

A README follows `README-TEMPLATE.md`, beside the repositories: one sentence at the top that a
stranger to the subject understands, four badges that can each be wrong, install before
build-from-source, every application in a table linking its own README, licence last.

---

## Submitting a change

 1. Branch off `main`. Do not commit to `main` directly.
 2. One logical change per commit. A commit message says **what changed and why**, in plain
    words - the subject line in the imperative, then a blank line, then the reasoning. If you
    measured something, put the number in the message.
 3. `.\src\build.ps1` passes, including the tests, before you open the pull request.
 4. Say in the pull request what you verified and how. "Tests pass" on its own is not useful;
    "the three LAPB timer tests now cover the T2 case, 42 tests, all green" is.
 5. If something is still broken or unfinished, say so plainly. A known gap that is written down
    is fine. One that is hidden is not.

---

## Licence

By contributing you agree your work is licensed under the MIT licence in [LICENSE](../LICENSE).
