namespace Valleysoft.Dredge;

internal interface IProcessTerminator
{
    void Exit(int exitCode);
}

internal interface IProcessTerminationAware
{
    IProcessTerminator ProcessTerminator { get; set; }
}

internal sealed class ProcessTerminator : IProcessTerminator
{
    public void Exit(int exitCode) => Environment.Exit(exitCode);
}
