using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Equilibrium.Daml.Build.Core.Processes;

// Windows passes a program one command-line string, so the argument list has to be encoded in
// a way the receiving side decodes back into the same list.
internal static class CommandLine
{
    private static readonly char[] CharsNeedingQuotes = [' ', '\t', '\n', '\v', '"'];
    private static readonly char[] CharsCmdCannotCarry = ['\0', '\r', '\n'];

    public static bool IsBatchFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    // For a program that splits its command line the way CommandLineToArgvW and the C runtime do.
    public static string ForExecutable(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteForExecutable));

    // Arguments for cmd.exe running a batch file. cmd parses the line itself first: quotes keep
    // & | < > ^ literal, /v:off keeps ! literal, but %VAR% expands even inside quotes, so each %
    // is followed by an empty substring of %cd%. The batch file then forwards the arguments
    // (usually as %*) to a program that splits them like ForExecutable expects, except that a
    // quote is doubled rather than backslash-escaped: a backslash-escaped quote would end
    // quoting as far as cmd is concerned. /s strips the outer pair of quotes; /d skips AutoRun.
    public static string ForBatch(string batchFile, IEnumerable<string> arguments)
    {
        var line = new StringBuilder("/d /s /v:off /c \"");
        line.Append(QuoteForBatch(batchFile));
        foreach (var argument in arguments)
        {
            line.Append(' ').Append(QuoteForBatch(argument));
        }
        return line.Append('"').ToString();
    }

    private static string QuoteForExecutable(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(CharsNeedingQuotes) < 0)
        {
            return argument;
        }

        // Backslashes are literal unless they precede a quote, so a run of them before a quote
        // (or before the closing quote) has to be doubled.
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            quoted.Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static string QuoteForBatch(string argument)
    {
        if (argument.IndexOfAny(CharsCmdCannotCarry) >= 0)
        {
            // cmd ends the command at a line break; there's no escape for it.
            throw new ArgumentException($"A batch file can't be passed an argument containing a line break or NUL: '{argument}'.", nameof(argument));
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            switch (c)
            {
                case '\\':
                    backslashes++;
                    continue;
                case '"':
                    quoted.Append('\\', backslashes * 2).Append("\"\"");
                    break;
                case '%':
                    quoted.Append('\\', backslashes).Append("%%cd:~,%");
                    break;
                default:
                    quoted.Append('\\', backslashes).Append(c);
                    break;
            }
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
