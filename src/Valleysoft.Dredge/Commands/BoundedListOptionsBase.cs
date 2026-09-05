using System.CommandLine;

namespace Valleysoft.Dredge.Commands;

public abstract class BoundedListOptionsBase : OptionsBase
{
    private readonly Option<int?> limitOption;

    protected BoundedListOptionsBase()
    {
        limitOption = Add(new Option<int?>("--limit")
        {
            Description = "Maximum number of results to return"
        });
        limitOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is <= 0)
            {
                result.AddError("Limit must be greater than zero.");
            }
        });
    }

    public int? Limit { get; set; }

    protected void GetBoundedListValues()
    {
        Limit = GetValue(limitOption);
    }
}
