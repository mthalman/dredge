using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;

namespace Valleysoft.Dredge.Commands;

public abstract class OptionsBase
{
    private readonly List<Argument> arguments = [];
    private readonly List<Option> options = [];
    private ParseResult? parseResult;

    protected Argument<T> Add<T>(Argument<T> argument)
    {
        arguments.Add(argument);
        return argument;
    }

    protected Option<T> Add<T>(Option<T> option)
    {
        options.Add(option);
        return option;
    }

    protected T GetValue<T>(Argument<T> arg)
    {
        ValidateParseResult();
        return parseResult!.GetValue(arg)!;
    }

    protected T? GetValue<T>(Option<T> option)
    {
        ValidateParseResult();
        return parseResult!.GetValue(option);
    }

    protected T? GetValue<T>(Option<T> option, Option<T> alternate)
    {
        ValidateParseResult();
        Option<T> selected = option;
        foreach (Token token in parseResult!.Tokens.Where(static token => token.Type == TokenType.Option))
        {
            if (token.Value == option.Name || option.Aliases.Contains(token.Value))
            {
                selected = option;
            }
            else if (token.Value == alternate.Name || alternate.Aliases.Contains(token.Value))
            {
                selected = alternate;
            }
        }
        return parseResult.GetValue(selected);
    }

    private void ValidateParseResult()
    {
        if (parseResult is null)
        {
            throw new Exception($"'{nameof(SetParseResult)}' method must be called before get argument value");
        }
    }

    public void SetParseResult(ParseResult parseResult)
    {
        this.parseResult = parseResult;
        GetValues();
    }

    protected abstract void GetValues();

    public virtual void SetCommandOptions(Command cmd)
    {
        foreach (Argument arg in arguments)
        {
            cmd.Arguments.Add(arg);
        }

        foreach (Option option in options)
        {
            cmd.Options.Add(option);
        }
    }
}
