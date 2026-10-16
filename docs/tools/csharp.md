# C#

Write C# for this Godot project with TypeScript-like brevity. Let the compiler
infer types whenever context is clear, avoid repeating a type in the same
expression, and use newer C# syntax for collections, data, and control flow.
Keep public and Godot-facing signatures explicit so their contracts stay
readable; check the active C# project settings before using version-specific
features.

## Naming

Follow [Microsoft's C# naming conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/identifier-names):

- Use `PascalCase` for namespaces, types, methods, local functions, properties, events, constants, and public fields: `UpdatePath()`, `MoveSpeed`, and `DefaultSpeed`.
- Use `camelCase` for parameters and local variables: `moveSpeed` and `elapsedTime`.
- Use `_camelCase` for private and internal instance fields: `_moveSpeed`. Use `s_camelCase` for private and internal static fields and `t_camelCase` for thread-static fields.
- Use `PascalCase` for positional record and record struct parameters because they become public properties.
- Prefix interfaces with `I` and descriptive generic type parameters with `T`: `IPathfinder` and `TResult`.
- Use descriptive names and spell out words unless an abbreviation is widely accepted.
- Preserve engine-required names such as Godot's `_Ready()` callback.

## Preferred syntax

- Use `var` for initialized locals, `foreach` items, and `out` values when the inferred type is clear.
- Let lambda parameters and generic method type arguments infer from their call site.
- Declare field, parameter, return, and public API types explicitly; `var` remains statically typed.
- Write constructed types once: `var items = new List<int>()` or `List<int> items = new()`.
- Use target-typed `new()` for fields and properties whose declarations provide the type.
- Use collection expressions with a target type: `int[] ids = [1, 2]` or `List<int> ids = [1, 2]`.
- Use `[..existing, next]` to spread a collection; use `var ids = new[] { 1, 2 }` when no target type exists.
- Use positional `record` or `record struct` for small data values with value equality; use `with` for shallow copies.
- Prefer `init` for struct properties assigned only during construction or object initialization; use `set` for properties changed afterward.
- Use named tuples for short-lived multiple results and anonymous types for local projections.
- Use `?.`, `??`, and `??=` for null handling, and keep nullable reference analysis enabled.
- Use `is` patterns to narrow values and `switch` expressions for short value selection.
- Prefer a `switch` expression when branching on an enum; group cases that have the same result.
- Build state-machine dispatch with switch expressions. Keep transition results explicit and put multi-step actions in focused methods.
- Use `=>` for short properties, accessors, and lambdas; keep multi-step methods as blocks.
- Use `using var` when a disposable value should live until the current scope ends.
- Use `[^1]` for the last element and `[1..^1]` for a slice when the collection supports ranges.
- Use file-scoped namespaces and file-local imports; rely on implicit imports only when the project enables them.
- Prefix every reference to an instance field, property, method, or event with `this.`: `this._moveSpeed = speed; this.UpdatePath();`.
- Store a retrieved ECS component in a named local before accessing its members: `var position = entity.GetComponent<WorldPosition>();` then use `position.Value`. Use `ref var position = ref entity.GetComponent<WorldPosition>();` when modifying the entity's component in place.

## Formatting

- Add a blank line before a statement that introduces a code block, such as `if`, `foreach`, or `try`.
- When a positional `record` or `record struct` declaration spans lines, put each parameter on its own line, starting after `(`.
- Add a blank line before `return` when it starts its own line.
- Add a blank line before a statement or declaration containing a `switch` expression.
- Keep a short `if` with a single `return` on one line: `if (invalid) return;`.
- Consecutive one-line `if` statements need no blank lines between them.
- Add a blank line after local variable declarations when the next statement is not another declaration; adjacent declarations need no blank line.
- Add a blank line after a group of guard checks before the next statement.
- Apply the 120-character wrapping limit only to function and constructor calls, counting indentation. Keep a call on one line when it fits. Do not split conditions, signatures, declarations, or attributes to meet this limit.
- For a call that exceeds 120 characters, wrap the outer argument list first: put each argument on its own line after `(`, indent arguments by two spaces, and put the closing `)` on its own line aligned with the start of the statement. Keep nested calls on one line when they fit.
- Inside a condition, wrap an overlong call's argument list; keep Boolean operators on the same line as their operands.

Keep this condition on one line even though it exceeds 120 characters:

```csharp
if (this.Mode == SessionMode.Client && this._prediction.Ready && Time.GetTicksMsec() - this._lastSnapshotReceived > 5000) {
  this.Leave();
}
```

Wrap this call because its single-line form exceeds 120 characters:

```csharp
this.RpcId(
  1,
  MethodName.SubmitInput,
  sequence,
  new Vector2(input.MoveDirection.X, input.MoveDirection.Y),
  input.MineHeld
);
```

Keep short calls on one line and separate component locals from their use:

```csharp
var position = entity.GetComponent<WorldPosition>();
var bodyRadius = entity.GetComponent<BodyRadius>();

var result = this.Execution.Advance(ref order, new(position.Value, bodyRadius.Value));
```

## Exceptions

- Prefer built-in guards over an equivalent `if` and `throw`.
- Build a focused guard for reusable checks when no built-in guard fits.

## Parameters

- Put one primary input first and bundle secondary or configuration values in a second `options` parameter: `void Save(string path, SaveOptions options)`.
- Bundle multiple primary inputs in one `input` parameter; keep configuration in a separate second `options` parameter when needed: `void Transfer(TransferInput input, TransferOptions options)`.
- Prefer named tuples over dedicated input records for small parameter groups with at least two values, including constructors and public methods: `public ChunkPathCache((RegionMap map, TerrainCatalog terrain) input, int capacity)`; call with `new ChunkPathCache((map, terrain), capacity)`.
- Use positional records when an input group needs a reusable domain type, validation, or behavior.
- Give configuration record parameters defaults when callers may omit values: `record SaveOptions(bool Overwrite = false);` then `this.Save(path, new(Overwrite: true))`.
- Name tuple elements for readability; C# matches elements by position and type.

## Project boundaries

- Preserve names and signatures required by Godot callbacks, exports, signals, and GDScript callers.
- Use explicit constructors for ordinary classes and positional records for data.
- Keep top-level statements in standalone app entry points, not Godot scene scripts.
- Give a standalone lambda a typed parameter or target delegate when its signature cannot be inferred.
- Avoid `dynamic` as a substitute for TypeScript inference.

## Validation

Validate inputs only when invalid values are reasonably expected from users, external
data, or normal runtime changes. Keep checks for deleted entities and failed routes.
Do not add guards for hypothetical caller mistakes, trusted constructor dependencies,
or engine-supplied frame deltas.
