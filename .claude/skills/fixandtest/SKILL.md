Implement the fix described in the user's request, then automatically write tests and verify the build.

## Steps (execute all without stopping)

1. Read the relevant source files to understand current behavior
2. Implement the fix — write the code changes now
3. Write unit tests covering:
   - The exact bug/scenario being fixed
   - Edge cases and boundary conditions
   - Regression coverage for related functionality
4. Run build: `dotnet build` — fix any errors and rebuild until clean
5. Run tests: `dotnet test` — fix any failures
6. Report: files changed (with line ranges), test count before vs after, build status, test results summary

Do not ask for confirmation between steps. Execute everything, then summarize.
