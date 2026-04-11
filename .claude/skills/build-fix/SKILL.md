Run `dotnet build`, fix all errors, and repeat until the build is fully clean with 0 errors.

## Process

1. Run `dotnet build` and capture all errors
2. Group errors by type (missing package, API change, compile error, etc.)
3. Fix each group — edit the relevant files
4. Rebuild immediately after each fix batch
5. Log each iteration: iteration number, errors remaining, what was fixed, new errors introduced
6. Repeat until `dotnet build` exits with 0 errors
7. Run `dotnet test` to confirm nothing is broken
8. Report final summary: iterations taken, files changed, test results

Rules:
- Do not stop to ask questions — keep iterating
- Check existing package versions before changing dependencies (`dotnet list package`)
- If the same error appears 3+ times without progress, try a different approach
