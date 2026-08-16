using System.Globalization;

namespace QbReclass.Cli;

/// <summary>Minimal argument parser: <c>verb --flag value --switch</c>.</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string verb, Dictionary<string, string?> options)
    {
        Verb = verb;
        _options = options;
    }

    public string Verb { get; }

    public static CommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var verb = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0].ToLowerInvariant()
            : "help";

        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = verb == "help" ? 0 : 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : null;

            options[key] = value;
        }

        return new CommandLine(verb, options);
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public string Require(string name) =>
        Get(name) ?? throw new ArgumentException($"Missing required option --{name}.");

    public int GetInt(string name, int fallback) =>
        int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    public decimal? GetDecimal(string name) =>
        decimal.TryParse(Get(name), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public DateOnly GetDate(string name, DateOnly fallback) =>
        DateOnly.TryParse(Get(name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : fallback;
}
