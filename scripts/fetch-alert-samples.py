#!/usr/bin/env python3
"""Vendor Microsoft's Azure Monitor common alert schema samples into the test project.

Microsoft publishes no machine-readable schema for the common alert schema: the closest is
the request schema in the Logic Apps guide, which covers `essentials` only and contradicts
the samples (it types `alertRule` and `severity` as strings; budget alerts send null). What
is published is the field tables and sample payloads in the Azure Monitor docs, so those are
the contract the tests run against:

  - every ```json block in alerts-payload-samples.md and alerts-common-schema.md, verbatim
    (duplicates across the two pages are kept once), and
  - the field names of the "Essentials fields" table in alerts-common-schema.md.

The source is pinned to a commit of MicrosoftDocs/azure-monitor-docs, so a refresh is a
reviewable diff. Run from the repo root:

    python3 -I scripts/fetch-alert-samples.py             # re-fetch at the pinned commit
    python3 -I scripts/fetch-alert-samples.py --ref main  # move the pin to the current main

Then commit the script (its pin) and the TestData directory together. The code samples are
MIT licensed; the script copies the source repository's LICENSE-CODE next to them, as the
licence requires. Standard library only.
"""

import argparse
import json
import os
import re
import sys
import urllib.request

REPO = "MicrosoftDocs/azure-monitor-docs"
PINNED_REF = "4c54223c9030e68ec232fc94b331d40caa8b1f8b"
PAGES = [
    "articles/azure-monitor/alerts/alerts-payload-samples.md",
    "articles/azure-monitor/alerts/alerts-common-schema.md",
]
SCHEMA_PAGE = "articles/azure-monitor/alerts/alerts-common-schema.md"
OUT_DIR = "tests/TeamsNotificationBot.Tests/TestData/AzureMonitorAlerts"


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": "teams-notifier-fetch-alert-samples"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read().decode("utf-8")


def resolve_ref(ref):
    """A branch or tag name becomes the commit it points at, so the pin is immutable."""
    if re.fullmatch(r"[0-9a-f]{40}", ref):
        return ref
    commit = json.loads(fetch(f"https://api.github.com/repos/{REPO}/commits/{ref}"))
    return commit["sha"]


def slug(text):
    text = text.lower().replace("`", "")
    text = re.sub(r"\bwhen the monitoringservice\b|\band the monitoringservice\b|\bwith monitoringservice\b|\bmonitoringservice\b", " ", text)
    text = re.sub(r"\bsample\b", " ", text)
    return re.sub(r"[^a-z0-9]+", "-", text).strip("-")


def json_blocks(markdown):
    """Yields (heading, line number, block text) for every ```json fence."""
    heading, lines, i = None, markdown.splitlines(), 0
    while i < len(lines):
        line = lines[i]
        if line.startswith("#"):
            heading = line.lstrip("#").strip()
        if line.strip().startswith("```json"):
            start, body = i + 1, []
            i += 1
            while not lines[i].strip().startswith("```"):
                body.append(lines[i])
                i += 1
            yield heading, start, "\n".join(body)
        i += 1


def essentials_fields(markdown):
    """The first column of the table under the "Essentials fields" heading."""
    section = markdown.split("## Essentials fields", 1)[1].split("\n## ", 1)[0]
    fields = []
    for row in section.splitlines():
        cells = [c.strip() for c in row.strip().strip("|").split("|")]
        if len(cells) < 2 or cells[0] in ("Field", "") or set(cells[0]) <= set("-: "):
            continue
        fields.append(cells[0])
    return fields


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--ref", default=PINNED_REF, help="commit, branch or tag of the docs repo")
    args = parser.parse_args()
    if not os.path.isdir("tests/TeamsNotificationBot.Tests"):
        sys.exit("Run from the repository root.")

    sha = resolve_ref(args.ref)

    # Everything is downloaded and parsed before anything on disk changes, so a failed
    # download leaves the vendored samples as they were.
    raw = f"https://raw.githubusercontent.com/{REPO}/{sha}"
    pages = {page: fetch(f"{raw}/{page}") for page in PAGES}
    license_text = fetch(f"{raw}/LICENSE-CODE")

    manifest, seen, files = [], [], {}
    for page, markdown in pages.items():
        # One folder per page: headings are unique within a page, not across the two.
        folder = os.path.basename(page).removeprefix("alerts-").removesuffix(".md")
        names = set()
        for heading, line, text in json_blocks(markdown):
            parsed = json.loads(text)  # a block that doesn't parse is a docs bug worth seeing
            if not isinstance(parsed, dict):
                continue
            if "schemaId" in parsed:
                kind = "payload"
            elif set(parsed) == {"alertContext"}:
                kind = "alertContext"
            else:
                continue  # not an alert, e.g. the action group REST request on the schema page
            if parsed in seen:
                continue
            seen.append(parsed)
            name = slug(heading)
            if name in names:
                sys.exit(f"{page}:{line}: two samples named {name}")
            names.add(name)
            files[f"samples/{folder}/{name}.json"] = text.rstrip() + "\n"
            manifest.append({"file": f"samples/{folder}/{name}.json", "kind": kind, "heading": heading,
                             "source": f"{page}#L{line}"})
    essentials = essentials_fields(pages[SCHEMA_PAGE])

    samples_root = os.path.join(OUT_DIR, "samples")
    for dirpath, _, filenames in os.walk(samples_root):
        for stale in filenames:
            if stale.endswith(".json"):
                os.remove(os.path.join(dirpath, stale))

    files["LICENSE-CODE"] = license_text  # MIT: the notice travels with the copies
    for path, content in files.items():
        target = os.path.join(OUT_DIR, path)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, "w", encoding="utf-8", newline="\n") as f:
            f.write(content)

    with open(os.path.join(OUT_DIR, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({
            "source": f"https://github.com/{REPO}/tree/{sha}",
            "license": "MIT, see LICENSE-CODE in this folder (the source repository's LICENSE-CODE)",
            "essentialsFields": essentials,
            "samples": manifest,
        }, f, indent=2)
        f.write("\n")

    if sha != PINNED_REF:
        script = os.path.abspath(__file__)
        with open(script, encoding="utf-8") as f:
            source = f.read()
        with open(script, "w", encoding="utf-8", newline="\n") as f:
            f.write(source.replace(PINNED_REF, sha, 1))
    print(f"{len(manifest)} samples from {REPO}@{sha[:12]} written to {OUT_DIR}")


if __name__ == "__main__":
    main()
