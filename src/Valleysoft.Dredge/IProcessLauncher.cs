using System.Diagnostics;

namespace Valleysoft.Dredge;

internal interface IProcessLauncher
{
    void Start(ProcessStartInfo startInfo);
}

internal sealed class ProcessLauncher : IProcessLauncher
{
    public void Start(ProcessStartInfo startInfo)
    {
        Process.Start(startInfo);
    }
}
