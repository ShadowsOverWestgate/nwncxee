"""Import the gitea-fetch.py dump into a Plane project through the ORM, keeping Gitea's numbers
as sequence ids (CX-42 == #42) and original timestamps. Runs INSIDE the api container:
  docker cp gitea.json plane-app-api-1:/tmp/ && docker cp tools/plane/plane-import.py plane-app-api-1:/tmp/
  docker exec -e DUMP=/tmp/gitea.json -e PROJECT_ID=<uuid> -e ACTOR_EMAIL=<email> plane-app-api-1 \
      python manage.py shell -c "exec(open('/tmp/plane-import.py').read())"
Idempotent per number: an existing work item with that sequence_id is skipped."""
import json, os, re, html
from datetime import datetime, timezone
from django.db import transaction
from plane.db.models import (Issue, IssueSequence, IssueComment, IssueLabel, IssueAssignee,
                             IssueRelation, Label, State, Project, User)

dump = json.load(open(os.environ["DUMP"]))
project = Project.objects.get(pk=os.environ["PROJECT_ID"])
actor = User.objects.get(email=os.environ["ACTOR_EMAIL"])
users = {"xtul": actor}
states = {s.group: s for s in State.objects.filter(project=project)}
labels = {l.name: l for l in Label.objects.filter(project=project)}
ts = lambda s: datetime.strptime(s, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc) if s else None
PARENT = re.compile(r"^(?:Part of |Map:? )?#(\d+)")

def parent_of(body):
    for line in body.splitlines():
        line = line.strip()
        if not line or line.startswith("## Parent"):
            continue
        m = PARENT.match(line)
        return int(m.group(1)) if m else None
    return None

def byline(user, when, url=None):
    who = "" if user == "xtul" else f"@{html.escape(user)} · "
    link = f' · <a href="{url}">Gitea</a>' if url else ""
    return f"<p><em>{who}{when[:10]}{link}</em></p>"

existing = set(Issue.objects.filter(project=project).values_list("sequence_id", flat=True))
by_number = {}
created = 0
with transaction.atomic():
    for it in dump:
        n = it["number"]
        if n in existing:
            by_number[n] = Issue.objects.get(project=project, sequence_id=n)
            continue
        state = states["completed" if it["state"] == "closed" else "unstarted"]
        issue = Issue(project=project, workspace=project.workspace, name=it["title"][:255], state=state,
                      description_html=it["html"] + byline(it["user"], it["created_at"], it["url"]),
                      created_by=actor, updated_by=actor)
        issue.save()
        Issue.objects.filter(pk=issue.pk).update(sequence_id=n, created_at=ts(it["created_at"]),
                                                 updated_at=ts(it["updated_at"]), completed_at=ts(it["closed_at"]))
        IssueSequence.objects.filter(issue=issue).update(sequence=n)
        IssueLabel.objects.bulk_create([IssueLabel(issue=issue, label=labels[l], project=project,
                                                   workspace=project.workspace) for l in it["labels"] if l in labels])
        IssueAssignee.objects.bulk_create([IssueAssignee(issue=issue, assignee=users[a], project=project,
                                                         workspace=project.workspace) for a in it["assignees"] if a in users])
        for c in it["comments"]:
            cm = IssueComment(issue=issue, project=project, workspace=project.workspace, actor=actor,
                              comment_html=byline(c["user"], c["created_at"]) + c["html"], created_by=actor)
            cm.save()
            IssueComment.objects.filter(pk=cm.pk).update(created_at=ts(c["created_at"]), updated_at=ts(c["created_at"]))
        by_number[n] = issue
        created += 1
    # second pass: ids exist now
    parents = relations = 0
    for it in dump:
        issue = by_number[it["number"]]
        p = parent_of(it["body_md"])
        if p in by_number and p != it["number"] and issue.parent_id is None:
            Issue.objects.filter(pk=issue.pk).update(parent=by_number[p]); parents += 1
        for b in it["blocked_by"]:
            if b in by_number and not IssueRelation.objects.filter(issue=issue, related_issue=by_number[b]).exists():
                IssueRelation.objects.create(issue=issue, related_issue=by_number[b], relation_type="blocked_by",
                                             project=project, workspace=project.workspace, created_by=actor)
                relations += 1
print(f"created {created} work items ({len(existing)} skipped), {parents} parent links, {relations} blocked_by edges")
