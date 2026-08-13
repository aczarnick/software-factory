using Factory.Core;

namespace Factory.Cli;

public static class Output
{
    private static readonly bool Colour =
        Environment.GetEnvironmentVariable("NO_COLOR") is null && !Console.IsOutputRedirected;

    private static string Wrap(string code, string text) => Colour ? $"[{code}m{text}[0m" : text;

    public static string Dim(string s) => Wrap("2", s);
    public static string Bold(string s) => Wrap("1", s);
    public static string Green(string s) => Wrap("32", s);
    public static string Red(string s) => Wrap("31", s);
    public static string Yellow(string s) => Wrap("33", s);
    public static string Cyan(string s) => Wrap("36", s);

    public static void Line(string s = "") => Console.WriteLine(s);
    public static void Info(string s) => Console.WriteLine(s);
    public static void Step(string s) => Console.WriteLine(Dim(s));
    public static void Success(string s) => Console.WriteLine(Green("✔ ") + s);
    public static void Warn(string s) => Console.WriteLine(Yellow("! ") + s);
    public static void Error(string s) => Console.Error.WriteLine(Red("✘ ") + s);

    public static void Header(string s)
    {
        Console.WriteLine();
        Console.WriteLine(Bold(s));
        Console.WriteLine(Dim(new string('─', Math.Min(s.Length, 60))));
    }

    public static string State(WorkItemState state) => state switch
    {
        WorkItemState.Done => Green("done"),
        WorkItemState.Verified => Green("verified"),
        WorkItemState.Failed => Red("failed"),
        WorkItemState.Blocked => Yellow("blocked"),
        WorkItemState.InProgress => Cyan("running"),
        WorkItemState.InReview => Cyan("review"),
        WorkItemState.Ready => "ready",
        WorkItemState.Draft => Dim("proposed"),
        _ => state.ToString().ToLowerInvariant()
    };

    /// <summary>Renders rows with padded columns. Padding ignores ANSI codes, which would
    /// otherwise count towards width and skew every column after the first colourised cell.</summary>
    public static void Table(IReadOnlyList<string[]> rows, params string[] headers)
    {
        if (rows.Count == 0) return;

        var widths = new int[headers.Length];
        for (var c = 0; c < headers.Length; c++)
        {
            widths[c] = headers[c].Length;
            foreach (var row in rows)
                if (c < row.Length) widths[c] = Math.Max(widths[c], Visible(row[c]).Length);
        }

        Console.WriteLine(Dim(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i])))));

        foreach (var row in rows)
        {
            var cells = row.Select((cell, i) =>
                cell + new string(' ', Math.Max(0, widths[i] - Visible(cell).Length)));
            Console.WriteLine(string.Join("  ", cells).TrimEnd());
        }
    }

    private static string Visible(string s)
    {
        if (!s.Contains('')) return s;

        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '')
            {
                while (i < s.Length && s[i] != 'm') i++;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public static string Truncate(string s, int max)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
