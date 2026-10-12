# Reconciliation specification

## Source authority

- Import every Events-list row (`agendaProf`), including visits, tests, projects, and other categories. Do not read the regular lesson schedule.
- Original fields: `RealDate`, `PrettyDate`, `Professor`, `Disciplina`, `Evento`. There is no trustworthy stable event ID. Distinct slots and identical duplicate rows each remain separate occurrences.
- `PrettyDate` must be exactly `dd-MM-yyyy (HH:mm-HH:mm)`. Validate the `RealDate` date agrees; do not use midnight scheduler fields as the event clock. Treat displayed times as Europe/Lisbon wall time, deriving winter/summer offsets from the OS timezone database. Ambiguous fall-back times, nonexistent spring-forward times, malformed dates, nonpositive intervals, missing fields, and out-of-year rows fail the complete run.
- Summary is `[AESO] ` plus the original `Evento` without trimming or rewriting. Description is `Disciplina: {Disciplina}\nProfessor: {Professor}\n{Evento}`. No translated or inferred school content.
- Only Lisbon-local dates today and later are desired; include events earlier today. Calendar events that **started before today's date** remain protected even if their end is later today. No historic event is updated or deleted.
- Calendar-default reminders (`useDefault:true`, no explicit overrides).

## Complete-read barrier

Login, configured current school-year check, selected student context, selected enrolment validation, complete source retrieval, final repeat confirmation, complete Google list, and deterministic plan must succeed before any calendar write.

Source coverage requests the whole configured enrolment/year at increasing limits. Compare the previous ordered prefix against each larger response; any shrink or change fails. A response with `count < requestedLimit` is a candidate complete result, and a second request at that same limit must return the same ordered rows and values. `count == limit` at the maximum cap fails. More rows than requested fails. Empty source always fails because the undocumented API cannot supply trustworthy empty completeness evidence. There is no automatic empty-source deletion escape hatch.

These checks are safety evidence for the observed vendor endpoint, not a vendor-supported snapshot guarantee. An unannounced server-side cap below a requested limit cannot be proven absent by HTTP alone. Live coverage validation remains a deployment gate, and any changed vendor behavior must stop automation pending inspection.

Google lists all pages from Lisbon midnight (`singleEvents=true`, `showDeleted=false`). Pagination tokens must advance, event IDs must not repeat, malformed items fail, and 1000 pages is a fail-closed cap. No search-by-prefix filter or result-page subset may stand in for a complete calendar read.

## Ownership and identity

**The prefix alone authorizes reconciliation.** A future manual `[AESO]` event is eligible for deletion even without metadata. Nonprefixed entries are never overwritten or deleted. Removing a prefix protects that entry and can cause a new prefixed copy to appear. This risk is intentional and approved.

Matching compares summary, description, start/end instants, timed-vs-all-day representation, and default reminders. Exact matches stay untouched regardless of Google ID or ownership metadata. Matching is a multiset: one calendar entry satisfies one source occurrence. Surplus prefixed duplicates are deleted; missing duplicate occurrences are created.

SHA-256 lowercase hex Google IDs derive from scope, exact canonical row projection, and sorted multiset occurrence index. Input order changes do not change desired IDs. Scope binds school origin, enrolment, teaching type, and school year. Hex meets Google's allowed ID alphabet. IDs include content, so changed source titles/dates/descriptions become distinct replacements. All occupied IDs (protected past/nonprefixed entries and Google's cancelled-ID tombstones) advance the same deterministic chain: SHA-256 lowercase hex of the previous ID plus `:occupied`. Planner and insertion share a 100-candidate bound. Before allocating a new ID, planning predicts the chain and checks its future managed entries for manual edits, even when the primary is a tombstone or a past entry outside `timeMin`. Only candidates captured in that complete list can authorize restoration; discovering an unexpected active mismatch during insertion aborts, never authorizes a blind PATCH.

Manual edits of an existing deterministic ID are restored via PATCH, reported as a logical delete/create pair. This avoids deleting/reusing an ID retained by Google. New/replacement creations and patches finish before obsolete events are deleted. A failed write stops subsequent actions; there is no rollback.

## Write proof and concurrency

- Inserts use explicit deterministic ID and `sendUpdates=none`; success requires GET readback with exact authoritative fields. A 409 is success only after GET proves an exact active match, or leads to deterministic tombstone collision handling.
- Patch/delete first GET the exact target. Any change from the planned entry or removal of its prefix aborts. Preserve Google's ETag from the complete list; future managed list entries without an ETag fail the read barrier. Require the final GET ETag to equal the planned ETag, detecting changes to any Google property (including fields not projected into calendar matching), and send that same planned value in `If-Match` so a later concurrent edit cannot be silently overwritten.
- Patch requires GET readback matching desired fields. A precondition failure after a retried PATCH is accepted only when that readback is already exact.
- Delete requires GET readback proving missing/410/cancelled. Already missing is idempotent success; an active remaining entry is failure.
- GET and deterministic Google writes retry transport errors, HTTP 429, and 5xx at most three attempts, bounded delays. Login posts never automatically retry. 401 does not trigger repeated credential submissions. HTTP redirects are disabled in production clients.

Default mode is live read-only dry-run. Offline snapshots never authorize apply. Only explicit `--apply` enables calendar writes. All reported apply success must follow verified writes; errors cannot produce a success JSON.
