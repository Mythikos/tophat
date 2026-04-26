# TopHat
TopHat is context optimization layer for LLMs intended to be used in .NET applications.

## Working with the user
- **Never provide time estimates for work.** No "30 minutes," "1 hour," "half a day." Don't tier proposals by estimated duration. Don't decide between approaches based on which is faster to implement. Describe scope by what changes (files, mechanism, risk surface) and compare approaches by technical trade-offs (correctness, dependencies, complexity, future flexibility). If asked "how long", note you are an AI and can't reliable predict how fast you can do it, describe scope instead.

## Programming Standards & Conventions
These standards are to be applied for programming tasks. C# language conventions take precedence where they conflict.

### Code Formatting
- **Indentation**: Use tabs (4-unit width), not spaces
- **Spacing**:
  - Blank space after each comma (e.g., argument lists).
  - Assignment, arithmetic, relational, and logical operators (`+`, `-`, `*`, `==`, `<=`, `>=`, `>=`, `&&`, `||`) must have one space before and after.
  - Unary operators (`++`, `--`) have no space between operator and operand.
  - **Blank lines**: Use a single blank line to separate logical blocks within a method body (e.g., between an `if` block and the next statement, between a guard clause's closing brace and the next condition). Use a single blank line to separate top-level members (methods, properties, fields) within a class, and between logical groups of using directives or class declarations. Do NOT add blank lines between tightly coupled lines that form one logical unit (e.g., a variable declaration and the statement that immediately uses it, or consecutive related assignments).
- **Line breaks**: Statements, method signatures, field declarations, and method calls should remain on a single line unless the line is egregiously long (roughly 200+ characters). Do not break code across multiple lines for style reasons. Specific guidance:
  - **Constructor assignments** with `?? throw` stay on one line (e.g., `this._connectionString = configuration.GetConnectionString("Default") ?? throw new InvalidOperationException("...");`).
  - **Service method signatures** with plain typed parameters stay on one line (e.g., `public async Task<List<T>> SearchAsync(string? a = null, string? b = null, bool useArchive = false)`).
  - **Field initializers** stay on one line unless the initializer itself contains complex nested logic (e.g., `private static readonly HashSet<string> Codes = new (StringComparer.OrdinalIgnoreCase) { "01", "02" };`).
- **Alignment**: Do not use extra spaces for vertical alignment of code. Let indentation handle structure naturally.
- **Braces**: Opening brace should always be on the next line (Allman style). Closing brace on its own line.
- **Using statements**: Always use braced `using` blocks to clearly denote scope. Do not use declaration-style `using var` statements.
- **Regions**: Use `#region` / `#endregion` to group logical sections of code where it improves readability. The region markers should hug their contents — no blank line immediately after `#region` or immediately before `#endregion`.

### Naming Conventions
- **General**: All identifiers must be descriptive and meaningful. Avoid 1-2 character names except loop counters.
- **PascalCase**: Namespaces, classes, methods, properties, constants, enums, public members.
- **camelCase**: Local variables.
- **Private instance fields**: Prefix with `_` and use camelCase (e.g., `_connectionString`).
- **Static instance fields**: Prefix with `s_` and use camelCase (e.g., `s_logger`).
- **Constants**: PascaleCase or ALL_UPPER_CASE. Use underscore convention for instance specific constants.
- **UI controls**: Names must indicate they relate to a ui element (e.g., `firstNameTextBox` or `statesDropDown`).

### Constants
- Shared constant values go into a static class named `Constants`.
- Group constants into logical sections using nested class wrappers within `Constants`.
- Instance-specific constants are defined at the top of their class and ust be `private`.
- Always use `const` or `static readonly` to prevent reassignment.

### Class Objects
- Always define an explicit constructor unless the clss is `static` or `sealed`.
- Never expose instance fields publically. Use properties (getters/setters) for access.

### Threading
- Thread methods must be thread-safe with proper locking.
- Use a thread queue for batches of threads.
- Never create threats with no handle.
- UI access from threads must use timed/incremented intervals to avoid blocking the UI thread.

### Commenting
- Comments follow the same indentation as the code they describe.
- Use inline comments for ambiguous or complex lines.
- Use block comments for class and method explanations (include argument descriptions if not self-evident).
- Use XML documentation comments (`///`) on all public methods and classes.

### Exception Handling
- Use exception handling appropriately - never as flow control.
- Overusing try/catch is as bad as not using it.
- Validate inputs at system boundaries (user input, external APIs).

### Best Practices
- **No global variables.** Use method argumetns and class properties.
- **No `goto` statements.**
- Initialize variables to a default value. Prefer environment-defined values over literals.
- Watch out for code repetition. Write modular and reusable code.
- Follow KISS, YAGNI, DRY, Single Responsibility, and Principle of Least Astonishment.

### Version Control
- Git is required. Trunk-based development:
  - `main` must always be deployment-ready.
  - Feature branches should be short-lived.
  - No direct commits to `main`, all changes via pull request.
- Repository must contain `.gitignore` and `readme.md`.