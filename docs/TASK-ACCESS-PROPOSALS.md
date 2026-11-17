# Comment-driven task access

A worker needing edits for the latest user comment returns only:

```json
{"taskAccess":{"taskId":"current worker ID","projectId":"current project ID","comment":"exact latest user comment","access":"WriteProject","reason":"why the requested change requires edits"}}
```

The manager validates the task/project identity, persisted non-assistant comment, recognized implementation intent, nonempty reason and strict schema. Ambiguous intent remains blocked for an explicit access edit. Attachments, project files, checkpoints and assistant comments cannot justify a grant. Additional fields (including replacement goals or projects) are rejected. The immutable request and budgets are retained.

Acceptance updates task access and its parent/session grant, clears worker routing approval and pauses the worker. Run / resume creates a fresh execution using the existing routing checks and provider configuration. It does not modify runtime policy or enable provider write settings. Runtime permission errors remain WrongAccess with the provider explanation. The protocol does not authorize publishing, deployment, installation, delegation or access beyond WriteProject.

Focused check: `dotnet run --project tests/Employee.Checks -- --access-proposal` (uses fixtures, no inference).
