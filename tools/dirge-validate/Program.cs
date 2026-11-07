using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dirge.Project;
using Dirge.Serialization;
using Dirge.Validation;

namespace Dirge.Tools.Validate;

/// <summary>
/// Headless project validation for CI: loads a <c>*.dirge.json</c>, runs <see cref="ProjectValidator"/> and prints one
/// line per diagnostic (<c>severity code subject: message</c>) or, with <c>--json</c>, the diagnostics array.
/// Exit codes: 0 no errors (warnings allowed), 1 at least one error, 2 usage or parse failure.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitErrors = 1;
    private const int ExitFailure = 2;

    private static int Main(string[] args)
    {
        string? path = null;
        bool json = false;
        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--json": json = true; break;
                case "-h" or "--help": PrintUsage(Console.Out); return ExitOk;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal) || path is not null)
                    {
                        Console.Error.WriteLine($"dirge-validate: unexpected argument '{arg}'");
                        PrintUsage(Console.Error);
                        return ExitFailure;
                    }
                    path = arg;
                    break;
            }
        }
        if (path is null)
        {
            PrintUsage(Console.Error);
            return ExitFailure;
        }

        AudioProject project;
        try
        {
            project = ProjectJson.Deserialize(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine($"dirge-validate: cannot load '{path}': {e.Message}");
            return ExitFailure;
        }

        IReadOnlyList<Diagnostic> diagnostics = ProjectValidator.Validate(project);
        int errors = 0, warnings = 0;
        foreach (Diagnostic d in diagnostics)
        {
            if (d.Severity == Severity.Error) errors++; else warnings++;
        }

        if (json) WriteJson(diagnostics);
        else WriteText(path, project, diagnostics, errors, warnings);

        return errors == 0 ? ExitOk : ExitErrors;
    }

    private static void WriteText(string path, AudioProject project, IReadOnlyList<Diagnostic> diagnostics, int errors, int warnings)
    {
        foreach (Diagnostic d in diagnostics)
        {
            string severity = d.Severity == Severity.Error ? "error" : "warning";
            string subject = d.Subject.IsNone ? "project" : d.Subject.ToString();
            Console.WriteLine($"{severity} {d.Code} {subject}: {d.Message}");
        }
        Console.WriteLine($"{path}: \"{project.Name}\" — {errors} error(s), {warnings} warning(s)");
    }

    private static void WriteJson(IReadOnlyList<Diagnostic> diagnostics)
    {
        var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var stream = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stream, options);
        writer.WriteStartArray();
        foreach (Diagnostic d in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("severity", d.Severity == Severity.Error ? "error" : "warning");
            writer.WriteString("code", d.Code);
            if (d.Subject.IsNone) writer.WriteNull("subject"); else writer.WriteString("subject", d.Subject.ToString());
            writer.WriteString("message", d.Message);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.Flush();
        stream.WriteByte((byte)'\n');
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("usage: dirge-validate <file.dirge.json> [--json]");
        output.WriteLine("  Prints validator diagnostics as 'severity code subject: message' lines (or a JSON array with --json).");
        output.WriteLine("  Exit 0: no errors. Exit 1: errors found. Exit 2: usage or parse failure.");
    }
}
