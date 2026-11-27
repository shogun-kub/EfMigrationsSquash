using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// EfMigrationsSquash — свёртка диапазона EF-миграций в один host-файл.
// Режимы:
//   --path <каталог> --before <yyyyMMdd> [--dry-run] — неинтерактивный;
//   без аргументов — интерактивный (как в оригинале).
// Коды выхода: 0 — успех, 1 — ошибка записи/удаления, 2 — ошибка валидации/аргументов.

string? path = null;
string? before = null;
bool dryRun = false;

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

if ((path == null) != (before == null))
    ExitFail("--path and --before must be specified together");

if (before != null && !Regex.IsMatch(before, @"^\d{8}$"))
    ExitFail("--before must be a yyyyMMdd date");

bool nonInteractive = path != null;

// --- Сбор каталога миграций ------------------------------------------
string migrationsFolder;

if (nonInteractive)
{
    migrationsFolder = path!;
    if (!Directory.Exists(migrationsFolder))
        ExitFail($"Folder not found: {migrationsFolder}");
}
else
{
    if (!dryRun)
    {
        var def = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("\nWARNING:");
        Console.ForegroundColor = def;
        Console.WriteLine(" this tool will delete the squashed migration files, if something happened you may lose your migrations, make sure to use source control (like git) or to backup your migrations\n" +
            "use it at your own risk\n\npress any key to continue or (Q) to quit");

        if (Console.ReadKey().Key == ConsoleKey.Q) return;
        if (Console.CursorLeft > 0)
        {
            Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
            Console.Write("\0");
        }
    }

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

// --- Определение диапазона [startIdx..endIdx], host = endIdx -----------
int startIdx, endIdx;

if (nonInteractive)
{
    var sources = files
        .Where(f => string.CompareOrdinal(Path.GetFileName(f), before) < 0)
        .ToList();

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

    if (start < 1 || end > files.Count || start > end)
        ExitFail($"Range must be 1 <= start <= end <= {files.Count}");

    startIdx = start - 1;
    endIdx = end - 1;
}

var rangeFiles = files.Skip(startIdx).Take(endIdx - startIdx + 1).ToList();
var hostPath = rangeFiles[^1];
var contents = new string[rangeFiles.Count];

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

// --- Валидация (ничего не пишем и не удаляем при ошибках) --------------
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
        // Метод между Up и Down попадёт внутрь тела Up host-файла, метод после Down —
        // внутрь тела Down: оба случая ломают результат, прерываем.
        if (Regexes.MethodDeclRegex.IsMatch(complete.Groups[4].Value))
            errors.Add($"{name}: method declaration between Up and Down — unsupported layout");

        if (Regexes.MethodDeclRegex.IsMatch(complete.Groups[6].Value))
            errors.Add($"{name}: method declaration after Down — unsupported layout");
    }
}

var hostDesigner = DesignerPath(hostPath);
if (!File.Exists(hostDesigner))
    errors.Add($"{Path.GetFileName(hostPath)}: Designer file missing ({Path.GetFileName(hostDesigner)}) — EF would not discover the folded migration");

if (errors.Count > 0)
{
    foreach (var e in errors) Console.Error.WriteLine("VALIDATION: " + e);
    Environment.Exit(2);
}

// --- Слияние helper-методов (регион между class-{ и Up) ----------------
var errors2 = new List<string>();
var usedNames = new HashSet<string>(StringComparer.Ordinal);
var helperChunks = new List<string>();
var helperLog = new List<string>();

// Host сохраняет свои helper-методы автоматически (они входят в Groups[3]),
// но их имена нужно занести в usedNames, чтобы источники не вошли в конфликт.
var hostRegion = ExtractPreUpRegion(contents[^1]);
if (hostRegion == null)
{
    errors2.Add($"{Path.GetFileName(hostPath)}: cannot locate class open / Up header");
}
else if (HasSubstantiveCode(hostRegion))
{
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

    if (!HasSubstantiveCode(region))
        continue; // только пробелы/док-комментарии — переносить нечего

    var names = Regexes.MethodDeclRegex.Matches(region).Select(m => m.Groups[1].Value).ToList();
    if (names.Count == 0)
    {
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

        var newName = $"{name1}_{ts}";
        if (usedNames.Contains(newName))
        {
            errors2.Add($"{name}: cannot rename helper {name1} -> {newName} (name already used)");
            continue;
        }

        // Переименование внутри всего файла: объявление в регионе + вызовы в Up/Down.
        contents[j] = Regex.Replace(contents[j], $@"\b{Regex.Escape(name1)}\s*\(", newName + "(");
        usedNames.Add(newName);
        log.Add($"{name1} -> {newName}");
        helperLog.Add($"{name}: renamed {name1} -> {newName}");
    }

    if (errors2.Count > 0)
        break;

    var cleaned = StripTrailingDocComments(ExtractPreUpRegion(contents[j])!);
    helperChunks.Add($"// helpers merged from {name}\n{cleaned}");
    helperLog.Add($"{name}: merged {string.Join(", ", log)}");
}

if (errors2.Count > 0)
{
    foreach (var e in errors2) Console.Error.WriteLine("VALIDATION: " + e);
    Environment.Exit(2);
}

// --- Сборка результата --------------------------------------------------
var ups = new List<string>();
var downs = new List<string>();
var usings = new List<string>();

for (int j = 0; j < rangeFiles.Count; j++)
{
    var content = contents[j];
    var marker = $"\n// content of {Path.GetFileName(rangeFiles[j])}\n";

    usings.AddRange(Regexes.UsingsRegex.Match(content)
        .Value.Split(";", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    ups.Add(marker + Regexes.UpMigrationRegex.Match(content).Groups[1].Value);
    downs.Add(marker + Regexes.DownMigrationRegex.Match(content).Groups[1].Value);
}

var squashedUp = string.Join('\n', ups);
downs.Reverse();
var squashedDown = string.Join('\n', downs);
var combinedUsing = string.Join(";\n", usings.Select(x => x.Trim()).ToHashSet()) + ";";

var helpersBlock = helperChunks.Count == 0 ? "" : "\n" + string.Join("\n", helperChunks);

var hostContent = contents[^1];
var resultContent = Regexes.CompleteMigrationFileRegex.Replace(
    hostContent,
    m => $"{combinedUsing}\n\r\n#nullable disable\n\r\n{m.Groups[3].Value}\n{squashedUp}{m.Groups[5].Value}{squashedDown}\n\t\t}}{helpersBlock}\n\t}}\n}}");

if (resultContent == hostContent)
    ExitFail("Host rewrite produced no changes — aborting (CompleteMigrationFileRegex Replace no-op)");

// --- Сводка --------------------------------------------------------------
var sourceBytes = rangeFiles.Take(rangeFiles.Count - 1).Sum(f => new FileInfo(f).Length);

Console.WriteLine($"Mode:        {(dryRun ? "DRY-RUN (no changes)" : "APPLY")}");
Console.WriteLine($"Host:        {Path.GetFileName(hostPath)}");
Console.WriteLine($"Sources:     {rangeFiles.Count - 1} ({sourceBytes} bytes)");
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

// --- Запись и удаление ---------------------------------------------------
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
        File.Delete(DesignerPath(src)); // отсутствующий файл — no-op
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

// --- Вспомогательные функции --------------------------------------------

static void ExitFail(string message)
{
    Console.Error.WriteLine("ERROR: " + message);
    Environment.Exit(2);
}

// Все миграционные .cs в каталоге, без Designer и snapshot, ordinal-сортировка.
static List<string> CollectMigrations(string folder)
{
    return Directory.EnumerateFiles(folder)
        .Where(x => x.EndsWith(".cs", StringComparison.Ordinal)
                 && !x.EndsWith(".Designer.cs", StringComparison.Ordinal)
                 && !Path.GetFileName(x).EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal)
        .ToList();
}

static string DesignerPath(string migrationPath)
    => Path.Combine(Path.GetDirectoryName(migrationPath) ?? "",
                    Path.GetFileNameWithoutExtension(migrationPath) + ".Designer.cs");

// Регион класса после class-{ и до заголовка Up (helper-методы host сохраняются через Groups[3]).
static string? ExtractPreUpRegion(string content)
{
    var cls = Regexes.ClassOpenRegex.Match(content);
    if (!cls.Success) return null;

    var up = Regexes.UpHeaderRegex.Match(content, cls.Index);
    if (!up.Success || up.Index < cls.Index + cls.Length) return null;

    return content.Substring(cls.Index + cls.Length, up.Index - cls.Index - cls.Length);
}

// Есть ли в регионе код помимо пробелов и комментариев.
static bool HasSubstantiveCode(string s)
{
    s = Regex.Replace(s, @"///[^\r\n]*", "");
    s = Regex.Replace(s, @"//[^\r\n]*", "");
    s = Regex.Replace(s, @"/\*[\s\S]*?\*/", "");
    return !string.IsNullOrWhiteSpace(s);
}

// Убирает висящие в конце региона док-комментарии (принадлежат Up) и пустые строки.
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

// Префикс имени файла: 14-значный timestamp EF-миграции (или sanitized-имя).
static string TimestampOf(string path)
{
    var fileName = Path.GetFileName(path);
    var m = Regex.Match(fileName, @"^\d{14}");
    if (m.Success) return m.Value;
    return Regex.Replace(Path.GetFileNameWithoutExtension(path), @"[^\w]", "_");
}

static class Regexes
{
    public static readonly Regex CompleteMigrationFileRegex = new(@"((using .+;\s*)*)[\s\S]+(namespace.*\s{[\s\S]+protected\s+override\s+void\s+Up\s*\(\s*MigrationBuilder\s+\w+\)\s*{)([\s\S]*)(}[\s\S]+protected\s+override\s+void\s+Down\(MigrationBuilder\s+\w+\)\s*{)([\s\S]*)}\s+}\s*}", RegexOptions.Compiled);

    public static readonly Regex UsingsRegex = new(@"((using .+;\s*)*)", RegexOptions.Compiled);

    public static readonly Regex UpMigrationRegex = new(@"protected\s+override\s+void\s+Up\s*\(\s*MigrationBuilder\s+\w+\)\s*{([\s\S]*)}[\s\S]+protected\s+override\s+void\s+Down", RegexOptions.Compiled);

    public static readonly Regex DownMigrationRegex = new(@"}[\s\S]+protected\s+override\s+void\s+Down\s*\(\s*MigrationBuilder\s+\w+\)\s*{([\s\S]*)}\s+}\s*}", RegexOptions.Compiled);

    // class X : Migration {  — начало тела класса (для извлечения helper-региона).
    public static readonly Regex ClassOpenRegex = new(@"class\s+\w+\s*:\s*Migration[^{]*\{", RegexOptions.Compiled);

    // Заголовок Up (для границы helper-региона).
    public static readonly Regex UpHeaderRegex = new(@"protected\s+override\s+void\s+Up\s*\(", RegexOptions.Compiled);

    // Объявление метода уровня класса (доступ-модификатор обязателен):
    // локальные функции внутри Up/Down такого модификатора не имеют и не срабатывают.
    public static readonly Regex MethodDeclRegex = new(
        @"(?:^|\r?\n)[ \t]*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal)\b[^\r\n(]*?\b(\w+)\s*\([^)]*\)\s*\{?[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.Compiled);
}
