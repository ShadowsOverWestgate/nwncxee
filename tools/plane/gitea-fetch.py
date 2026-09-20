#!/usr/bin/env python3
"""Dump every Gitea issue (not PRs) of the repo, with comments and blocking dependencies, to JSON;
bodies are rendered to HTML here since the Plane container has no markdown lib.
Usage: GITEA_TOKEN=... gitea-fetch.py <owner/repo> <out.json>"""
import json, os, sys, urllib.request, html
import markdown

repo, out = sys.argv[1], sys.argv[2]
tok = os.environ["GITEA_TOKEN"]
base = f"https://git.westgate.pw/api/v1/repos/{repo}"
md = markdown.Markdown(extensions=["fenced_code", "tables", "sane_lists"])

def get(path):
    req = urllib.request.Request(base + path, headers={"Authorization": f"token {tok}"})
    with urllib.request.urlopen(req) as r:
        return json.load(r)

def render(text):
    md.reset()
    return md.convert(text or "") or "<p></p>"

issues = []
page = 1
while True:
    batch = get(f"/issues?state=all&type=issues&limit=50&page={page}")
    if not batch:
        break
    issues += batch
    page += 1
print(f"{len(issues)} issues", file=sys.stderr)

dump = []
for i in sorted(issues, key=lambda x: x["number"]):
    n = i["number"]
    comments = get(f"/issues/{n}/comments") if i["comments"] else []
    deps = get(f"/issues/{n}/dependencies")
    dump.append({
        "number": n,
        "title": i["title"],
        "html": render(i["body"]),
        "body_md": i["body"] or "",
        "state": i["state"],
        "user": i["user"]["login"],
        "created_at": i["created_at"],
        "updated_at": i["updated_at"],
        "closed_at": i["closed_at"],
        "labels": [l["name"] for l in i["labels"]],
        "assignees": [a["login"] for a in (i["assignees"] or [])],
        "url": i["html_url"],
        "blocked_by": [d["number"] for d in deps],
        "comments": [{"user": c["user"]["login"], "created_at": c["created_at"], "html": render(c["body"])}
                     for c in comments],
    })
    if n % 50 == 0:
        print(f"  ..#{n}", file=sys.stderr)

json.dump(dump, open(out, "w"))
print(f"wrote {out}: {len(dump)} issues, {sum(len(d['comments']) for d in dump)} comments, "
      f"{sum(len(d['blocked_by']) for d in dump)} dependency edges", file=sys.stderr)
