---
name: worklane-board
description: Create and update cards on the WL board that tracks Worklane's own development. Use whenever work on this repo starts, finishes, or gets blocked — a feature, a bug fix, a decision to be made — and whenever asked about project status, what is left, or how long something took. Worklane tracks itself, so the board is the status record.
---

# Keeping the WL board current

Worklane is tracked in Worklane. The `WL` project is the real status record for this
codebase — the user opens it to see where the work stands. **Work that never became a card
is invisible to them**, so card the work as you do it, not in a batch at the end.

## Board coordinates

Project key `WL`, in the demo workspace.
Board: http://localhost:4200/w/demo-users-workspace/p/WL/board

Columns, in order: **Backlog → To Do → In Progress → Done**.
`In Progress` is flagged `IsInProgress` (starts the clock) and `Done` is flagged `IsDone`.

The API must be running — see `CLAUDE.md` for how to bring the stack up.

## The rules that matter

**Drag cards through the columns; never create them straight into Done.** Timing is stamped
on column *transitions*, so a card born in Done has no `StartedAt` and contributes nothing
to the dashboard. Create in `To Do`, move to `In Progress` when you start, move to `Done`
when it lands.

**Always set `estimateDays`.** An unestimated card is missing from every man-day figure.
Use the sizes the UI offers: `0.5 · 1 · 2 · 3 · 5 · 8`.

**Write the description to stand alone.** The user reads the card body, not just the title:
what was done, what it was blocked on, why it mattered, and the commit hash once it lands.
Assume the reader has not seen the conversation it came from.

**Bugs and open decisions get cards too**, not only features. A decision the user needs to
make is a card in `Backlog` describing the options.

## Working with it

```bash
# authenticate
TOKEN=$(curl -s -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"demo@worklane.app","password":"Demo123!"}' \
  | node -e "let d='';process.stdin.on('data',c=>d+=c).on('end',()=>console.log(JSON.parse(d).accessToken))")
```

Resolve the project and its columns rather than hardcoding ids — they differ per machine:

```bash
WS=$(curl -s http://localhost:5000/api/workspaces -H "Authorization: Bearer $TOKEN" | node -e "let d='';process.stdin.on('data',c=>d+=c).on('end',()=>console.log(JSON.parse(d)[0].id))")
WL=$(curl -s http://localhost:5000/api/workspaces/$WS/projects -H "Authorization: Bearer $TOKEN" | node -e "let d='';process.stdin.on('data',c=>d+=c).on('end',()=>console.log(JSON.parse(d).find(p=>p.key==='WL').id))")
curl -s http://localhost:5000/api/projects/$WL/columns -H "Authorization: Bearer $TOKEN" | node -e "let d='';process.stdin.on('data',c=>d+=c).on('end',()=>JSON.parse(d).forEach(c=>console.log(c.name,c.id)))"
```

Endpoints used:

| Action | Call |
|---|---|
| Create | `POST /api/projects/{projectId}/tasks` — `{ title, description, priority, estimateDays, columnId, assigneeId }` |
| Move | `POST /api/tasks/{taskId}/move` — `{ toColumnId, toIndex }` |
| Edit | `PATCH /api/tasks/{taskId}` — any of the create fields |
| List | `GET /api/projects/{projectId}/tasks` |
| Report | `GET /api/workspaces/{id}/reports/members` |

For more than two or three cards, write a small Node script into the scratchpad and run it,
rather than issuing curl calls one at a time. Multi-line Thai descriptions survive far
better through `JSON.stringify` than through shell quoting.

## Reporting status

`GET /api/workspaces/{id}/reports/members` returns workspace totals plus a row per member:
counts by column, estimated against actual man-days, variance, overdue, on-time rate,
average cycle time.

When quoting these numbers, state the limitation with them: **actual is elapsed weekdays,
not effort.** Someone holding three cards at once accrues full time on each, weekend work
counts as zero, and there is no holiday calendar. They are a trend, not billable days.

Do not invent historical timings for cards finished before tracking existed. An estimate
with no actual is honest; a fabricated duration is not.
