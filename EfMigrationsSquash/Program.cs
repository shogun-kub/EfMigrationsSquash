using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// ---------------------------------------------------------------------------
// EfMigrationsSquash — folds a contiguous range of EF Core migration files
// into a single "host" migration so their huge .Designer.cs files can be
// deleted and Release builds stop compiling megabytes of snapshot code.
//
// Modes:
//   --path <dir> --before <yyyyMMdd> [--dry-run]   non-interactive (scripted)
//   (no arguments)                                 interactive, as upstream
//
// Exit codes:
//   0   success (including dry-run)
//   1   write/delete failure — the host is written BEFORE sources are
//       deleted, so a failed write can never cost us the source files
//   2   argument or validation failure — nothing was written or deleted
//
// Safety model: everything up to the final write is pure in-memory work.
// Any doubt (regex miss, unexpected member layout, Roslyn syntax error in
// the folded result) aborts with exit 2 while every file is still untouched.
// ---------------------------------------------------------------------------

string? path = null;
string? before = null;
bool dryRun = false;

// Argument parsing lives first so `--dry-run`/bad flags fail fast, before we
// touch the filesystem or print the interactive warning prompt.
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--path":
            if (i + 1 >= args.Length) ExitFail("--path requires a value");
            path = args[++i];
            break;
        case "--before":
            if (i + 1 >= args.Length) ExitFail("--before requires a value");
            before = args[++i];
            break;
        case "--dry-run":
            dryRun = true;
            break;
        default:
            ExitFail($"Unknown argument: {args[i]}");
            break;
    }
}

// --path and --before are only meaningful as a pair: a path alone would
// silently fall into interactive mode inside a non-TTY (Console.ReadKey
// hangs there), a cutoff alone has no folder to scan.
if ((path == null) != (before == null))
    ExitFail("--path and --before must be specified together");

// EF migration filenames start with yyyyMMdd; anything else would compare
// garbage against filenames and select a wrong range.
if (before != null && !Regex.IsMatch(before, @"^\d{8}$"))
    ExitFail("--before must be a yyyyMMdd date");

bool nonInteractive = path != null;

// --- Resolve the migrations folder ---------------------------------------
string migrationsFolder;

if (nonInteractive)
{
    migrationsFolder = path!;
    if (!Directory.Exists(migrationsFolder))
        ExitFail($"Folder not found: {migrationsFolder}");
}
else
{
    // Interactive-only destructive prompt. Skipped for --dry-run (nothing is
    // deleted) and for non-interactive mode (no TTY to read a key from).
    if (!dryRun)
    {
        var def = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("\nWARNING:");
        Console.ForegroundColor = def;
        Console.WriteLine(" this tool will delete the squashed migration files, if something happened you may lose your migrations, make sure to use source control (like git) or to backup your migrations\n" +
            "use it at your own risk\n\npress any key to continue or (Q) to quit");

        if (Console.ReadKey().Key == ConsoleKey.Q) return;
        // Erase the keystroke echo so the prompt line stays clean (upstream fix
        // for a cursor-glitch crash — kept as-is).
        if (Console.CursorLeft > 0)
        {
            Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
            Console.Write("\0");
        }
    }

    // Interactive default layout: ./Migrations relative to the CWD, with
    // subdirectory picking when the root itself holds no migration files
    // (multi-context projects store one folder per DbContext).
    migrationsFolder = Path.Combine(Directory.GetCurrentDirectory(), "Migrations");
    if (!Directory.Exists(migrationsFolder))
        ExitFail("Migrations folder not found!");
}

var files = CollectMigrations(migrationsFolder);

if (files.Count == 0 && !nonInteractive)
{
    Console.Write("No migration files found within base directory!");

    var directories = Directory.EnumerateDirectories(migrationsFolder).ToList();

    Console.WriteLine("Select migrations directory by pressing a number:");

    foreach (var directory in directories)
    {
        Console.WriteLine($"{directories.IndexOf(directory) + 1} - {Path.GetFileName(directory)}");
    }
    var directoryNum = 0;

    while (!int.TryParse(Console.ReadLine(), out directoryNum)) Console.Write("Invalid input. Try again: ");

    if (directoryNum < 1 || directoryNum > directories.Count)
        ExitFail("No migration with this number!");

    migrationsFolder = directories[directoryNum - 1];
    files = CollectMigrations(migrationsFolder);

    if (files.Count == 0)
        ExitFail("No migration files found!");
}

if (files.Count == 0)
    ExitFail("No migration files found!");

// --- Select the fold range [startIdx..endIdx]; host = endIdx -------------
int startIdx, endIdx;

if (nonInteractive)
{
    // Ordinal compare of the full filename against the 8-digit cutoff:
    // "20251229…" < "20260101" holds digit-by-digit, and a file stamped ON
    // the cutoff date sorts after the cutoff itself (longer string with an
    // equal prefix), so --before is exclusive — exactly what we want.
    var sources = files
        .Where(f => string.CompareOrdinal(Path.GetFileName(f), before) < 0)
        .ToList();

    // One source minimum plus the host: a single file has nothing to fold.
    if (sources.Count < 2)
        ExitFail($"Need at least 2 migrations before {before} (sources + host), found {sources.Count}");

    files = sources;
    startIdx = 0;
    endIdx = files.Count - 1;
}
else
{
    var lst = string.Join('\n', files.Select((x, i) => $"{i + 1} - {Path.GetFileName(x)}"));
    Console.WriteLine("Your migrations:\n" + lst);

    int start, end;

    Console.Write("Enter starting file number: ");
    while (!int.TryParse(Console.ReadLine(), out start)) Console.Write("Invalid input. Try again: ");

    Console.Write("Enter end file number: ");
    while (!int.TryParse(Console.ReadLine(), out end)) Console.Write("Invalid input. Try again: ");

    // Upstream indexed the arrays without bounds checks; an out-of-range pick
    // used to crash mid-fold. Validate before reading a single file.
    if (start < 1 || end > files.Count || start > end)
        ExitFail($"Range must be 1 <= start <= end <= {files.Count}");

    startIdx = start - 1;
    endIdx = end - 1;
}

var rangeFiles = files.Skip(startIdx).Take(endIdx - startIdx + 1).ToList();
var hostPath = rangeFiles[^1];
var contents = new string[rangeFiles.Count];

// Read every file up front so validation and assembly see one immutable
// snapshot — a file edited between two reads would produce a fold that
// validates against different text than it writes.
for (int j = 0; j < rangeFiles.Count; j++)
{
    try
    {
        contents[j] = File.ReadAllText(rangeFiles[j]);
    }
    catch (Exception ex)
    {
        ExitFail($"Cannot read {Path.GetFileName(rangeFiles[j])}: {ex.Message}");
    }
}

// --- Validation: prove every file matches what the regex surgery expects --
// Upstream deleted sources even when the host rewrite had silently no-op'd
// (Regex.Replace returns the input unchanged on no match). Every check below
// runs BEFORE any write so a surprise costs an exit code, not the migrations.
var errors = new List<string>();

for (int j = 0; j < rangeFiles.Count; j++)
{
    var name = Path.GetFileName(rangeFiles[j]);
    var content = contents[j];

    if (!Regexes.UpMigrationRegex.IsMatch(content))
        errors.Add($"{name}: cannot find Up method");

    if (!Regexes.DownMigrationRegex.IsMatch(content))
        errors.Add($"{name}: cannot find Down method");

    var complete = Regexes.CompleteMigrationFileRegex.Match(content);
    if (!complete.Success)
    {
        errors.Add($"{name}: file structure not recognized (CompleteMigrationFileRegex)");
    }
    else
    {
        // The fold splices squashed bodies between the host's Up/Down
        // headers, so anything the capture groups swallowed from the WRONG
        // place would be re-emitted inside a method body. A class-level
        // member (access modifier present) between Up/Down lands in G4; one
        // after Down lands in G6. Both would not compile inside the host —
        // reject the file instead of producing broken C#.
        if (Regexes.MethodDeclRegex.IsMatch(complete.Groups[4].Value))
            errors.Add($"{name}: method declaration between Up and Down — unsupported layout");

        if (Regexes.MethodDeclRegex.IsMatch(complete.Groups[6].Value))
            errors.Add($"{name}: method declaration after Down — unsupported layout");
    }
}

// EF discovers migrations ONLY through [DbContext]/[Migration] attributes,
// and those live in the .Designer.cs partial — without it the folded host
// would compile but never be listed or applied by EF.
var hostDesigner = DesignerPath(hostPath);
if (!File.Exists(hostDesigner))
    errors.Add($"{Path.GetFileName(hostPath)}: Designer file missing ({Path.GetFileName(hostDesigner)}) — EF would not discover the folded migration");

if (errors.Count > 0)
{
    foreach (var e in errors) Console.Error.WriteLine("VALIDATION: " + e);
    Environment.Exit(2);
}

// --- Merge class-level helper methods out of the SOURCE files -----------
// Helper methods (e.g. BaseDb.SetSeq / *.AddStoredProcs) sit between the
// class opening brace and the Up signature — the fold only carries Up/Down
// bodies across, so without this step the sources' helpers would vanish and
// the host would no longer compile. The HOST keeps its own helpers for free:
// they live inside capture group 3 of CompleteMigrationFileRegex, which the
// rewrite template copies verbatim.
var errors2 = new List<string>();
var usedNames = new HashSet<string>(StringComparer.Ordinal);
var helperChunks = new List<string>();
var helperLog = new List<string>();

var hostRegion = ExtractPreUpRegion(contents[^1]);
if (hostRegion == null)
{
    errors2.Add($"{Path.GetFileName(hostPath)}: cannot locate class open / Up header");
}
else if (HasSubstantiveCode(hostRegion))
{
    // Seed the collision set with the host's own helper names — those cannot
    // be renamed (they are baked into the kept header text), so sources must
    // yield instead.
    foreach (Match m in Regexes.MethodDeclRegex.Matches(hostRegion))
        usedNames.Add(m.Groups[1].Value);
}

for (int j = 0; j < rangeFiles.Count - 1 && errors2.Count == 0; j++)
{
    var name = Path.GetFileName(rangeFiles[j]);
    var region = ExtractPreUpRegion(contents[j]);

    if (region == null)
    {
        errors2.Add($"{name}: cannot locate class open / Up header");
        break;
    }

    // Regions of plain EF-generated migrations hold only whitespace and the
    // /// <inheritdoc /> that belongs to Up — nothing worth copying.
    if (!HasSubstantiveCode(region))
        continue;

    var names = Regexes.MethodDeclRegex.Matches(region).Select(m => m.Groups[1].Value).ToList();
    if (names.Count == 0)
    {
        // Real code that no pattern recognized (fields, properties, exotic
        // layouts): copying it blind risks silent loss, folding it without
        // copying breaks the source's own references — abort instead.
        errors2.Add($"{name}: unrecognized class-level content before Up (no method declarations found) — aborting to avoid data loss");
        continue;
    }

    var ts = TimestampOf(rangeFiles[j]);
    var log = new List<string>();

    foreach (var name1 in names.Distinct())
    {
        if (!usedNames.Contains(name1))
        {
            usedNames.Add(name1);
            log.Add(name1);
            continue;
        }

        // Name collision across sources (seen with AddStoredProcs: two
        // different bodies under one name). Suffix with the source file's
        // 14-digit EF timestamp — unique within one migrations folder — and
        // rewrite every "Name(" occurrence in THIS source only, covering both
        // the declaration in the helper region and the call sites in its
        // Up/Down bodies.
        var newName = $"{name1}_{ts}";
        if (usedNames.Contains(newName))
        {
            errors2.Add($"{name}: cannot rename helper {name1} -> {newName} (name already used)");
            continue;
        }

        contents[j] = Regex.Replace(contents[j], $@"\b{Regex.Escape(name1)}\s*\(", newName + "(");
        usedNames.Add(newName);
        log.Add($"{name1} -> {newName}");
        helperLog.Add($"{name}: renamed {name1} -> {newName}");
    }

    if (errors2.Count > 0)
        break;

    // Re-extract after the rename pass so the chunk carries the new names.
    var cleaned = StripTrailingDocComments(ExtractPreUpRegion(contents[j])!);
    helperChunks.Add($"// helpers merged from {name}\n{cleaned}");
    helperLog.Add($"{name}: merged {string.Join(", ", log)}");
}

if (errors2.Count > 0)
{
    foreach (var e in errors2) Console.Error.WriteLine("VALIDATION: " + e);
    Environment.Exit(2);
}

// --- Assemble the folded host --------------------------------------------
var ups = new List<string>();
var downs = new List<string>();
var usings = new List<string>();

for (int j = 0; j < rangeFiles.Count; j++)
{
    var content = contents[j];
    // Provenance marker: every spaded block can be traced back to its
    // original file when someone audits the folded host later.
    var marker = $"\n// content of {Path.GetFileName(rangeFiles[j])}\n";

    usings.AddRange(Regexes.UsingsRegex.Match(content)
        .Value.Split(";", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    ups.Add(marker + Regexes.UpMigrationRegex.Match(content).Groups[1].Value);
    downs.Add(marker + Regexes.DownMigrationRegex.Match(content).Groups[1].Value);
}

var squashedUp = string.Join('\n', ups);
// Down must undo in reverse application order: the last migration applied
// has to be rolled back first, so the concatenation is reversed.
downs.Reverse();
var squashedDown = string.Join('\n', downs);
// Sources may reference types imported only in THEIR file; the host keeps a
// union of every using (HashSet de-duplicates the shared core set).
var combinedUsing = string.Join(";\n", usings.Select(x => x.Trim()).ToHashSet()) + ";";

// Helper chunks are spliced after Down's closing brace but before the class
// closing brace — member scope, so both Up and Down bodies can call them.
var helpersBlock = helperChunks.Count == 0 ? "" : "\n" + string.Join("\n", helperChunks);

var hostContent = contents[^1];
var resultContent = Regexes.CompleteMigrationFileRegex.Replace(
    hostContent,
    m => $"{combinedUsing}\n\r\n#nullable disable\n\r\n{m.Groups[3].Value}\n{squashedUp}{m.Groups[5].Value}{squashedDown}\n\t\t}}{helpersBlock}\n\t}}\n}}");

// Regex.Replace returns the input UNCHANGED when the pattern never matched.
// Writing that result and then deleting the sources would destroy the fold
// (the upstream data-loss bug) — treat a no-op as a hard validation error.
if (resultContent == hostContent)
    ExitFail("Host rewrite produced no changes — aborting (CompleteMigrationFileRegex Replace no-op)");

// --- Roslyn syntax gate ---------------------------------------------------
// Regex splicing can unbalance braces or truncate tokens in ways the
// structural checks above cannot see. Parsing the exact string we are about
// to write turns those into a clean exit-2 abort BEFORE anything touches
// disk — and --dry-run gets the same gate for free. Syntax only: symbol
// resolution would need the full project reference set, but the bodies are
// verbatim copies of code that already compiles in ORMEntityCore, so broken
// SYNTAX is the realistic failure mode of this tool. LanguageVersion.Latest
// because EF migrations use raw string literals (C# 11+).
var parseErrors = CSharpSyntaxTree
    .ParseText(resultContent, new CSharpParseOptions(LanguageVersion.Latest))
    .GetDiagnostics()
    .Where(d => d.Severity == DiagnosticSeverity.Error)
    .ToList();

if (parseErrors.Count > 0)
{
    foreach (var d in parseErrors.Take(10))
        Console.Error.WriteLine("ROSLYN: " + d);
    if (parseErrors.Count > 10)
        Console.Error.WriteLine($"ROSLYN: … and {parseErrors.Count - 10} more error(s)");
    ExitFail($"folded host has {parseErrors.Count} C# syntax error(s) — aborting before any write");
}

// --- Summary (printed BEFORE the irreversible part so a human can veto) ---
var sourceBytes = rangeFiles.Take(rangeFiles.Count - 1).Sum(f => new FileInfo(f).Length);

Console.WriteLine($"Mode:        {(dryRun ? "DRY-RUN (no changes)" : "APPLY")}");
Console.WriteLine($"Host:        {Path.GetFileName(hostPath)}");
Console.WriteLine($"Sources:     {rangeFiles.Count - 1} ({sourceBytes} bytes)");
Console.WriteLine($"Syntax:      OK (Roslyn, {resultContent.Length} chars)");
Console.WriteLine($"Delete list: {rangeFiles.Count - 1} .cs + {rangeFiles.Count - 1} .Designer.cs");

for (int j = 0; j < rangeFiles.Count - 1; j++)
{
    var src = rangeFiles[j];
    var designer = DesignerPath(src);
    Console.WriteLine($"  {Path.GetFileName(src)}");
    Console.WriteLine($"  {Path.GetFileName(designer)}{(File.Exists(designer) ? "" : "  [missing]")}");
}

if (helperLog.Count > 0)
{
    Console.WriteLine("Helpers:");
    foreach (var line in helperLog)
        Console.WriteLine($"  {line}");
}
else
{
    Console.WriteLine("Helpers:     none");
}

if (dryRun)
{
    Console.WriteLine("Dry run — no files were written or deleted.");
    Environment.Exit(0);
}

// --- Apply: write the host FIRST, then delete sources ---------------------
// Order is the rollback strategy: if the write fails (exit 1) the sources
// are still on disk and git/backup still has everything.
try
{
    File.WriteAllText(hostPath, resultContent);
    Console.WriteLine($"Host written: {hostPath} ({new FileInfo(hostPath).Length} bytes)");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed writing host: {ex.Message}");
    Environment.Exit(1);
}

Console.WriteLine("Deleting source files");
int deleted = 0, failed = 0;

for (int j = 0; j < rangeFiles.Count - 1; j++)
{
    var src = rangeFiles[j];
    try
    {
        File.Delete(src);
        File.Delete(DesignerPath(src)); // missing Designer is a documented no-op
        deleted++;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed deleting {src}: {ex.Message}");
        failed++;
    }
}

Console.WriteLine($"Done: deleted {deleted} source migration(s), {failed} failure(s). Double-check your result.");
Environment.Exit(failed > 0 ? 1 : 0);

// --- Local helpers --------------------------------------------------------

// Single funnel for exit-2 failures so no caller forgets the code and
// accidentally reports success to a calling script.
static void ExitFail(string message)
{
    Console.Error.WriteLine("ERROR: " + message);
    Environment.Exit(2);
}

// Candidate migrations in a folder. Designers are excluded because they are
// partial-class companions (folding one in would duplicate the class
// declaration); ModelSnapshot is excluded because it is not a migration at
// all and would otherwise become a selectable "source". Ordinal sort makes
// the selection deterministic — EF ids are yyyyMMddHHmmss_… prefixes, which
// sort chronologically under ordinal comparison, so the LAST pre-cutoff file
// is reliably the newest one and becomes the host.
static List<string> CollectMigrations(string folder)
{
    return Directory.EnumerateFiles(folder)
        .Where(x => x.EndsWith(".cs", StringComparison.Ordinal)
                 && !x.EndsWith(".Designer.cs", StringComparison.Ordinal)
                 && !Path.GetFileName(x).EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal)
        .ToList();
}

// EF's naming convention: "<migration>.cs" is always accompanied by
// "<migration>.Designer.cs" in the same folder.
static string DesignerPath(string migrationPath)
    => Path.Combine(Path.GetDirectoryName(migrationPath) ?? "",
                    Path.GetFileNameWithoutExtension(migrationPath) + ".Designer.cs");

// Text between the class opening brace and the Up signature — the exact
// span where class-level helper methods live in EF-style migration files.
// Returns null when either anchor is missing (malformed file).
static string? ExtractPreUpRegion(string content)
{
    var cls = Regexes.ClassOpenRegex.Match(content);
    if (!cls.Success) return null;

    var up = Regexes.UpHeaderRegex.Match(content, cls.Index);
    if (!up.Success || up.Index < cls.Index + cls.Length) return null;

    return content.Substring(cls.Index + cls.Length, up.Index - cls.Index - cls.Length);
}

// Distinguishes "region has real code" from "region is only whitespace and
// comments". The /// <inheritdoc /> directly above Up must NOT count as
// substance, otherwise all 190+ plain migrations would look like they carry
// helpers and get their doc comments copied into the host.
static bool HasSubstantiveCode(string s)
{
    s = Regex.Replace(s, @"///[^\r\n]*", "");
    s = Regex.Replace(s, @"//[^\r\n]*", "");
    s = Regex.Replace(s, @"/\*[\s\S]*?\*/", "");
    return !string.IsNullOrWhiteSpace(s);
}

// The extracted region ends right before the Up signature, so it ends with
// Up's own /// <inheritdoc /> — that comment belongs to Up, not to the
// helpers, and would dangle confusingly inside the merged chunk. Drop it and
// any trailing blank lines.
static string StripTrailingDocComments(string region)
{
    var lines = region.Replace("\r\n", "\n").Split('\n');
    int end = lines.Length;
    while (end > 0)
    {
        var t = lines[end - 1].Trim();
        if (t.Length == 0 || t.StartsWith("///", StringComparison.Ordinal)) end--;
        else break;
    }
    return string.Join("\n", lines.Take(end));
}

// Collision-rename suffix: EF migration filenames start with a 14-digit
// timestamp that is unique within one folder; fall back to a sanitized file
// name for hand-written files that break the convention.
static string TimestampOf(string path)
{
    var fileName = Path.GetFileName(path);
    var m = Regex.Match(fileName, @"^\d{14}");
    if (m.Success) return m.Value;
    return Regex.Replace(Path.GetFileNameWithoutExtension(path), @"[^\w]", "_");
}

// ---------------------------------------------------------------------------
// Regex inventory. All patterns are Compiled because they run repeatedly
// over multi-megabyte texts (the folded host is ~8 MB).
//
// Cross-cutting assumption: every migration file has EXACTLY one Up and one
// Down, both `protected override void …(MigrationBuilder …)`, and ends with
// three closing braces (Down method, class, namespace). Validation rejects
// any file that breaks this before surgery starts.
// ---------------------------------------------------------------------------
static class Regexes
{
    // Full-file decomposition used by BOTH validation and the rewrite.
    // Uses [\s\S] instead of `.` because `.` does not cross newlines without
    // RegexOptions.Singleline and migration bodies span thousands of lines.
    //
    //   ((using .+;\s*)*)                          G1/G2 — leading usings
    //   [\s\S]+                                    skip to the namespace
    //   (namespace.*\s{ … Up(…){ )                G3 — namespace through the
    //                                              Up header; for the HOST
    //                                              this also contains the
    //                                              class declaration and any
    //                                              pre-Up helpers, which is
    //                                              why they survive the fold
    //   ([\s\S]*)                                  G4 — host Up body
    //                                              (discarded: replaced by
    //                                              the squashed Up list)
    //   (}[\s\S]+ … Down(…){ )                    G5 — Up's closing brace,
    //                                              the gap, and the Down
    //                                              header (leading `}`
    //                                              closes Up)
    //   ([\s\S]*)                                  G6 — host Down body
    //                                              (discarded likewise)
    //   }\s+}\s*}                                  tail — closes Down, the
    //                                              class, and the namespace
    //
    // Greedy quantifiers are safe only because Up/Down occur exactly once;
    // stray members between them would be absorbed into G4/G6 — which is
    // precisely what the MethodDeclRegex validation checks for.
    public static readonly Regex CompleteMigrationFileRegex = new(@"((using .+;\s*)*)[\s\S]+(namespace.*\s{[\s\S]+protected\s+override\s+void\s+Up\s*\(\s*MigrationBuilder\s+\w+\)\s*{)([\s\S]*)(}[\s\S]+protected\s+override\s+void\s+Down\(MigrationBuilder\s+\w+\)\s*{)([\s\S]*)}\s+}\s*}", RegexOptions.Compiled);

    // Leading using directives, one per line, each ending in `;` — matches
    // EF's code-generation style. Feeds the de-duplicated union of usings
    // that the folded host needs to compile all merged bodies.
    public static readonly Regex UsingsRegex = new(@"((using .+;\s*)*)", RegexOptions.Compiled);

    // G1 = the Up BODY. Anchored on the exact `protected override void
    // Up(MigrationBuilder …)` signature and on a Down signature following it, so
    // a local function that happens to be named Up cannot produce a false
    // capture. Empty bodies are legal (EF generates no-op migrations) and
    // deliberately do NOT fail validation.
    public static readonly Regex UpMigrationRegex = new(@"protected\s+override\s+void\s+Up\s*\(\s*MigrationBuilder\s+\w+\)\s*{([\s\S]*)}[\s\S]+protected\s+override\s+void\s+Down", RegexOptions.Compiled);

    // G1 = the Down BODY. The leading `}` anchors to the end of Up and the
    // trailing `}\s+}\s*}` anchors to the three braces at EOF, which keeps a
    // stray member AFTER Down from being silently dropped: it would be
    // swallowed into this capture, and the G6 layout check rejects the file.
    public static readonly Regex DownMigrationRegex = new(@"}[\s\S]+protected\s+override\s+void\s+Down\s*\(\s*MigrationBuilder\s+\w+\)\s*{([\s\S]*)}\s+}\s*}", RegexOptions.Compiled);

    // First `class <name> : Migration … {` — start anchor for the helper
    // region. `[^{]*` stops at the first brace after the base list, so the
    // match can never run past the class header into a nested block.
    public static readonly Regex ClassOpenRegex = new(@"class\s+\w+\s*:\s*Migration[^{]*\{", RegexOptions.Compiled);

    // End anchor for the helper region: the Up signature itself. Deliberately
    // stops at `(` so ExtractPreUpRegion cuts exactly at the declaration.
    public static readonly Regex UpHeaderRegex = new(@"protected\s+override\s+void\s+Up\s*\(", RegexOptions.Compiled);

    // Line-anchored class-member declaration; group 1 = member name. Used
    // twice: to DISCOVER helpers in the pre-Up region, and to PROVE no
    // class-level member hides inside Up/Down bodies (G4/G6 checks).
    //
    // The access modifier (public/private/protected/internal) is mandatory
    // and is what keeps this safe:
    //   - local functions inside Up/Down have no modifier → never matched,
    //     so legal body-local helpers don't fail validation and travel with
    //     their body when folded;
    //   - call sites (`Foo(migrationBuilder);`) have no modifier → never
    //     matched;
    //   - attributes on their own line are skipped via the leading
    //     `(?:\[…\]\s*)*` so `[Obsolete]`-style prefixes don't hide a
    //     declaration. `{?` tolerates EF's style of putting the opening
    //     brace on the next line. Multiline `$` anchors to end-of-line.
    // False positives inside SQL string literals are possible but fail
    // CLOSED: worst case is a rejected file an operator inspects by hand.
    public static readonly Regex MethodDeclRegex = new(
        @"(?:^|\r?\n)[ \t]*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal)\b[^\r\n(]*?\b(\w+)\s*\([^)]*\)\s*\{?[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.Compiled);
}
