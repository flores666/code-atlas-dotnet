# Agent Rules

## Scope

- Work only in this CodeAtlas.NET repository.
- Preserve all existing behavior unless the current task explicitly requires changing it.
- Implement only the current MVP. Do not implement features from later MVPs.
- Do not add placeholder abstractions for future MVPs.
- Do not perform unrelated refactoring.
- Stop when the task is satisfied.

## Change Style

- Make minimal, production-ready changes.
- Prefer adapting and reusing existing code over introducing parallel implementations.
- If an existing implementation already solves part of the task, extend it rather than replacing it.
- Do not create abstractions unless they solve a concrete current problem.
- Follow the existing architecture and coding patterns where they are sound.
- Avoid unnecessary dependencies.

## Architecture

- Keep domain/application logic independent from Avalonia UI.
- Keep Roslyn analysis deterministic. Do not replace compiler-derived information with heuristics or AI.
- Use async APIs where appropriate and propagate `CancellationToken`.
- Handle partial failures gracefully. One broken project or source file must not make the entire application unusable.

## Privacy and Safety

- Do not introduce cloud services.
- Do not send source code outside the local machine.
- Keep the application read-only with respect to the analyzed repository unless a task explicitly says otherwise.
- Do not add destructive Git operations.

## Verification

- Add or update tests for important analysis logic.
- Run relevant builds and tests before finishing.

## Documentation

- Document only non-obvious architectural decisions.
