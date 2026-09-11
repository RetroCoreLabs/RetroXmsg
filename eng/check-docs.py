#!/usr/bin/env python3
"""
check-docs.py - the documentation's own test.

    python eng/check-docs.py              # from the repository root
    python eng/check-docs.py some/folder  # or any folder you name

THERE IS A COPY OF THIS FILE IN EVERY RetroCore Labs C# REPOSITORY - RetroIO,
RetroX25, RetroServer, RetroCommander and RetroXmsg - and they are meant to stay
identical. Each repository carries its own so that somebody who cloned only one
of them can actually run the command its README gives; a single shared copy sat
outside all five and the instruction did not work. If you change one, change them
all. x25emu has the same checker at tests/check_docs.py.

Four things, every one of them invisible until somebody reads the result on
GitHub:

 1. Every RELATIVE markdown link resolves to a file that exists. A README full of
    links that 404 is worse than one with no links at all, because it teaches the
    reader not to trust the navigation.

 2. No relative link ESCAPES the repository root. Such a link resolves on the
    machine it was written on, where the other repository happens to sit next
    door, and 404s for everybody else. Those belong in the text as plain paths,
    not as links.

 3. No machine-specific absolute path appears anywhere in the documentation. A
    drive letter or a home directory is right on exactly one machine, and it puts
    the layout of somebody's disk into a public repository.

 4. Every mermaid block is structurally sound and follows
    MERMAID_COLOR_STANDARDS.md, which is kept beside the repositories:
      - the ```mermaid fence is closed
      - block openers and `end` balance (subgraph, and sequenceDiagram's loop,
        alt, opt, par, critical, rect, box)
      - every `class X,Y role` names a role some `classDef` defines
      - no `classDef` is left unused - which is almost always a node that was
        meant to be styled and is not
      - every classDef/style carries fill AND stroke AND color
      - no diagram forces a mermaid theme, which the standard bans because it
        breaks one of GitHub's two colour modes

Two lessons are built into the parsing, both learned by this checker being wrong
about a correct diagram:

  - a sequenceDiagram closes `loop` with `end` too, so counting `end` against
    `subgraph` alone condemns a perfectly good diagram;
  - mermaid allows a trailing semicolon on a `class` line, and some documents use
    them, so a regex anchored at end-of-line reports four dead classDefs in a
    diagram that uses all four.

Read the diagram before believing the checker about it.

Exit status is 0 when everything passes, 1 otherwise.
"""
import io
import os
import re
import sys

LINK = re.compile(r"\[([^\]]*)\]\(([^)]+)\)")

# Machine-specific absolute paths. These repos are published: a drive letter or a
# home directory in a README is wrong for every reader who is not sitting at the
# machine it was written on, and it leaks the layout of somebody's disk.
#
# This check exists because 82 of them were committed before anything looked.
# Paths inside a repo belong repo-relative; a sibling repo is named, not located;
# anything kept outside the repos is described rather than pointed at.
# %USERPROFILE% and ~ are deliberately NOT flagged: they are variables, which is
# the portable way to name a per-user location, and the first version of this
# pattern wrongly condemned a correct line for using one.
ABSPATH = re.compile(
    r"[A-Za-z]:[\\/](?:Dev|Users|Utils|Tools|Program Files|Windows|ndfs)"
    r"|/home/[A-Za-z0-9_.-]+/"
    r"|/mnt/[a-z]/(?:Dev|Users)",
    re.IGNORECASE)

# Block keywords a matching `end` closes. subgraph is the flowchart one; the
# rest belong to sequenceDiagram.
OPENERS = r"^\s*(subgraph|loop|alt|opt|par|critical|rect|box)\b"

# Directories that hold generated or third-party material rather than our docs.
SKIP = ("/bin/", "/obj/", "/.git/", "/node_modules/", "/packages/", "/TestResults/")


def markdown_files(root):
    out = []
    for base, dirs, files in os.walk(root):
        for name in files:
            if not name.endswith(".md"):
                continue
            path = os.path.join(base, name)
            rel = "/" + os.path.relpath(path, root).replace(os.sep, "/")
            if any(s in rel for s in SKIP):
                continue
            out.append(path)
    return sorted(out)


def check_abspaths(root, path, report):
    """No machine-specific absolute path may appear in published documentation."""
    rel = os.path.relpath(path, root)
    lines = io.open(path, encoding="utf-8", errors="replace").read().split(chr(10))
    for n, line in enumerate(lines, 1):
        m = ABSPATH.search(line)
        if m:
            report.append("ABSOLUTE PATH  " + rel + ":" + str(n) + "  " +
                          line.strip()[:100])


def check_links(root, path, report):
    s = io.open(path, encoding="utf-8", errors="replace").read()
    base = os.path.dirname(path)
    rel = os.path.relpath(path, root)
    checked = 0

    for m in LINK.finditer(s):
        target = m.group(2).strip()
        if target.startswith(("http://", "https://", "#", "mailto:")):
            continue
        filepart = target.split("#", 1)[0]
        if not filepart:
            continue
        # A Windows absolute path in a link is a path, not a relative link.
        if re.match(r"^[A-Za-z]:[\\/]", filepart):
            continue
        filepart = filepart.replace("%20", " ")
        resolved = os.path.normpath(os.path.join(base, filepart))
        checked += 1
        if not os.path.exists(resolved):
            report.append("BROKEN LINK  " + rel + "  ->  " + target)
            continue

        # A relative link that ESCAPES the repository root resolves here and
        # 404s on GitHub, because the thing it points at is in a different
        # repository - or in the folder the repositories sit in, which is not a
        # repository at all. Those must be plain paths, not links.
        #
        # Worth checking because it cannot be seen by eye and the filesystem
        # says yes: every one of these was introduced on purpose, by someone
        # who could open the file locally.
        if not os.path.abspath(resolved).startswith(os.path.abspath(root) + os.sep):
            report.append("ESCAPES REPO  " + rel + "  ->  " + target +
                          "  (resolves here, 404s on GitHub - use a plain path)")
    return checked


def check_mermaid(root, path, report):
    lines = io.open(path, encoding="utf-8", errors="replace").read().split(chr(10))
    rel = os.path.relpath(path, root)
    inside = False
    block = []
    start = 0
    blocks = 0

    for n, line in enumerate(lines, 1):
        if not inside and line.strip() == "```mermaid":
            inside, block, start = True, [], n
            continue
        if inside and line.strip() == "```":
            inside = False
            blocks += 1
            body = chr(10).join(block)
            where = rel + ":" + str(start) + "  "

            subs = len(re.findall(OPENERS, body, re.M))
            ends = len(re.findall(r"^\s*end\s*$", body, re.M))
            if subs != ends:
                report.append(where + "block openers=" + str(subs) +
                              " end=" + str(ends) + " MISMATCH")

            defined = set(re.findall(r"classDef\s+(\w+)", body))
            used = set()
            for m in re.finditer(r"^\s*class\s+([\w,\s]+?)\s+(\w+)\s*;?\s*$",
                                 body, re.M):
                used.add(m.group(2))
            for role in sorted(used - defined):
                report.append(where + "class uses undefined role '" + role + "'")
            for role in sorted(defined - used):
                report.append(where + "classDef '" + role + "' is never used")

            for m in re.finditer(r"(classDef\s+\w+|style\s+\w+)\s+(.+)", body):
                for want in ("fill:", "stroke:", "color:"):
                    if want not in m.group(2):
                        report.append(where + m.group(1) + " is missing " + want)

            if "theme" in body:
                report.append(where + "forces a mermaid theme")
            continue
        if inside:
            block.append(line)

    if inside:
        report.append(rel + ":" + str(start) + "  UNCLOSED mermaid fence")

    return blocks


def main():
    root = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else ".")
    if not os.path.isdir(root):
        print("not a directory: " + root)
        return 1

    report = []
    links = 0
    blocks = 0
    files = markdown_files(root)

    for path in files:
        check_abspaths(root, path, report)
        links += check_links(root, path, report)
        blocks += check_mermaid(root, path, report)

    for line in report:
        print(line)

    print("")
    print(os.path.basename(root) + ": " + str(len(files)) + " markdown file(s), " +
          str(links) + " relative link(s), " + str(blocks) + " mermaid block(s)")
    if report:
        print(str(len(report)) + " problem(s)")
        return 1
    print("no problems")
    return 0


if __name__ == "__main__":
    sys.exit(main())