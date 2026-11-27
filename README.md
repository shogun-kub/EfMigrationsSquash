# EfMigrationsSquash

Fold a contiguous range of EF Core migrations into a single **host** migration
and delete their `.Designer.cs` files — so your project stops compiling
megabytes of snapshot code on every build.

Each `.Designer.cs` carries a full copy of your model snapshot (`BuildTargetModel`,
often ~45k lines / ~2 MB per migration). On large migration histories they
dominate analysis, parse and build time. Folding removes the sources of that
cost while keeping the database behaviour identical for already-applied
environments.

## How it works

For every migration in the selected range (except the host):

- the body of `Up` is appended, in order, to the host's `Up`;
- the body of `Down` is prepended (reversed order) to the host's `Down`, so a
  rollback still undoes migrations in reverse application order;
- the source `.cs` and its `.Designer.cs` are deleted.

The **host** is the newest migration in the range: it keeps its file name, its
`[Migration]`/`[DbContext]` attributes (which live in its Designer file) and its
position in `__EFMigrationsHistory`, so databases that already applied it are
unaffected. Every spliced block is marked with a `// content of <file>` comment
for later auditing.

`ModelSnapshot.cs` is never touched — new migrations keep scaffolding from it
as before.

## Features

- **Non-interactive mode** — `--path` / `--before` / `--dry-run` for scripts
  and CI; no console prompts (safe without a TTY).
- **Dry-run** — prints the full plan (host, sources, delete list, merged
  helpers) and exits without writing or deleting anything.
- **Pre-write validation** — nothing is written or deleted until every source
  matches the expected layout:
  - `Up` / `Down` / whole-file structure must parse (the whole-file regex is
    what the rewrite relies on — a silent no-op would otherwise delete the
    sources without producing a fold);
  - no class-level method between `Up` and `Down` or after `Down` (it would
    be spliced *inside* a method body and fail to compile);
  - the host's `.Designer.cs` must exist (without it EF never discovers the
    folded migration).
- **Helper-method merging** — helper methods that sources declare between the
  class opening brace and `Up` (e.g. `SetSeq`, `AddStoredProcs`) are moved into
  the host class instead of being lost. Name collisions across sources are
  resolved by renaming to `Name_<migrationTimestamp>` and rewriting the call
  sites inside that source.
- **Roslyn syntax gate** — the assembled host is parsed
  (`Microsoft.CodeAnalysis.CSharp`, C# latest) before anything touches disk;
  any syntax error prints diagnostics and aborts with exit code 2. This
  catches brace/token damage that structural regex checks cannot see.
- **Safe apply order** — the host is written *first*, sources are deleted
  *after*; a failed write never costs you the source files.
- **Summary output** — host, source count/bytes, the complete delete list
  (missing Designers are flagged), and every helper merge/rename.

## Usage

### Interactive

```text
cd <your migrations project>
EfMigrationsSquash
```

Pick the start and end migration numbers, review the summary, confirm.
`./Migrations` is used by default; if the folder holds no migration files
directly, you are prompted to choose a context subfolder.

### Non-interactive (scripts / CI)

```bash
dotnet run --project tools/EfMigrationsSquash/EfMigrationsSquash -- \
  --path <migrations folder> --before yyyyMMdd [--dry-run]
```

| Argument     | Meaning |
|--------------|---------|
| `--path`     | Folder containing the migration `.cs` files (required together with `--before`) |
| `--before`   | Exclusive cutoff `yyyyMMdd` — all migrations with a smaller file-name timestamp are folded; the newest one becomes the host |
| `--dry-run`  | Validate and print the plan only; no writes, no deletions |

Example — fold everything before 2026 into one migration:

```bash
dotnet run --project tools/EfMigrationsSquash/EfMigrationsSquash -- \
  --path pryaniky-5/Utils/DB/ORMEntityCore/Migrations/PryanikyContextCorePostgreSQLMigrations \
  --before 20260101 --dry-run   # review first, then rerun without --dry-run
```

### Exit codes

| Code | Meaning |
|------|---------|
| `0`  | Success (including dry-run) |
| `1`  | Write/delete failure — the host write happens before source deletion, so sources are still intact |
| `2`  | Argument, validation or Roslyn failure — **nothing** was written or deleted |

### Install

From NuGet (upstream package):

```bash
dotnet tool install --global EfMigrationsSquash
```

Or build this repository from source:

```bash
dotnet build tools/EfMigrationsSquash/EfMigrationsSquash
dotnet pack tools/EfMigrationsSquash/EfMigrationsSquash -c Release   # → ./nupkg
```

Requires the .NET 10 SDK (the tool targets `net10.0`).

## When NOT to use it

- **Several live databases stuck at different versions** inside the folded
  range: a database that applied only *part* of the range would try to re-run
  the entire folded `Up` and fail. Fold only when every environment is already
  past the range (or has none of it applied).
- **You need per-migration rollbacks** inside the folded range: the individual
  `Down` methods are merged into one — rolling back the host rolls back the
  whole range.
- **Do not fold applied and not-yet-applied migrations together** — same
  failure mode as above.

Recommended safety net: keep the folder in version control (or take a backup),
always review a `--dry-run` first, and verify with a schema comparison
(`pg_dump`/`diff`) on a scratch database before committing the result.

## How to verify a fold

1. `--dry-run` — review host, delete list and helper log.
2. Build the project — plus the built-in Roslyn gate already parsed the result.
3. `dotnet ef migrations list` — expect host + all migrations *after* the
   cutoff, nothing else from the folded range.
4. Migrate an empty scratch database and diff its schema against one migrated
   with the pre-fold code.

## Scope

- Works on any provider: only `MigrationBuilder` call text is moved, no SQL
  or provider-specific logic is involved.
- Touched files: migration `.cs` files in the range (rewritten host, deleted
  sources) and their `.Designer.cs` files (deleted). Nothing else.

Feel free to fork, contribute.
