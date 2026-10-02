namespace AppTrace.Cli;

/// <summary>
/// A deliberately small command-line parser: <c>command</c> followed by
/// <c>--option value</c> or <c>--flag</c>. No dependency, no hidden behaviour.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string command)
    {
        Command = command;
    }

    public string Command { get; }

    public IReadOnlyDictionary<string, string?> Options => _options;

    public static CommandLine Parse(string[] args, out string? error)
    {
        error = null;
        var positional = new List<string>();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) && !arg.StartsWith('-'))
            {
                positional.Add(arg);
                continue;
            }

            var name = arg.TrimStart('-');
            string? value = null;
            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            if (!values.TryAdd(name, value))
            {
                error = $"Option --{name} was given more than once.";
            }
        }

        var command = positional.Count > 0 ? positional[0] : "scan";
        var result = new CommandLine(command);
        foreach (var (key, value) in values)
        {
            result._options[key] = value;
        }

        if (positional.Count > 1)
        {
            error ??= $"Unexpected argument \"{positional[1]}\". Path filtering belongs in --filter.";
        }

        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public int GetInt(string name, int fallback)
        => _options.TryGetValue(name, out var value)
            && int.TryParse(value, out var parsed)
            ? parsed
            : fallback;

    public string? RequireValue(string name)
        => _options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
