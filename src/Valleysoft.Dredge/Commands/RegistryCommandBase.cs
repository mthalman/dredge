namespace Valleysoft.Dredge.Commands;

public abstract class RegistryCommandBase<TOptions> :
    CommandWithOptions<TOptions>,
    IProcessTerminationAware
    where TOptions : OptionsBase, new()
{
    public IDockerRegistryClientFactory DockerRegistryClientFactory { get; }
    protected TextWriter Output { get; }
    internal IImageSourceFactory? ImageSourceFactory { get; set; }

    private protected Task<ResolvedImage> OpenImageAsync(
        string reference,
        PlatformOptionsBase options,
        CancellationToken cancellationToken,
        IAppSettingsStore? settingsStore = null,
        ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent) =>
        (ImageSourceFactory ?? new ImageSourceFactory(DockerRegistryClientFactory, settingsStore))
            .OpenAsync(reference, options, cancellationToken, requirements);

    private protected async Task<(ResolvedImage Base, ResolvedImage Target)> OpenComparisonAsync(
        string baseReference,
        string targetReference,
        PlatformOptionsBase options,
        CancellationToken cancellationToken,
        IAppSettingsStore? settingsStore = null,
        ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
    {
        IImageSourceFactory factory = ImageSourceFactory ??
            new ImageSourceFactory(DockerRegistryClientFactory, settingsStore);
        await using ImageSourceFactory.ComparisonScope? comparison =
            (factory as ImageSourceFactory)?.CreateComparisonScope();
        factory = comparison ?? factory;
        using CancellationTokenSource acquisitionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ResolvedImage>[] tasks =
        [
            OpenAsync(baseReference),
            OpenAsync(targetReference)
        ];
        try
        {
            await Task.WhenAll(tasks);
            return (tasks[0].Result, tasks[1].Result);
        }

        catch
        {
            if (comparison is not null)
            {
                try
                {
                    await comparison.DisposeAsync();
                }
                catch (Exception exception)
                {
                    Error.WriteLine($"Failed to dispose the comparison image acquisition scope after acquisition failed: {exception.Message}");
                }
            }
            foreach (Task<ResolvedImage> task in tasks)
            {
                if (task.IsCompletedSuccessfully)
                {
                    try
                    {
                        await task.Result.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        Error.WriteLine($"Failed to dispose a comparison image after acquisition failed: {exception.Message}");
                    }
                }
            }
            throw;
        }

        async Task<ResolvedImage> OpenAsync(string reference)
        {
            try
            {
                acquisitionCancellation.Token.ThrowIfCancellationRequested();
                return await factory.OpenAsync(reference, options, acquisitionCancellation.Token, requirements);
            }
            catch
            {
                try
                {
                    await acquisitionCancellation.CancelAsync();
                }
                catch (Exception exception)
                {
                    Error.WriteLine($"Failed to cancel comparison image acquisition: {exception.Message}");
                }
                throw;
            }
        }
    }

    private protected void ReportComparison(ResolvedImage baseImage, ResolvedImage targetImage)
    {
        if (baseImage.IsDaemon || targetImage.IsDaemon)
        {
            Report("Base", baseImage);
            Report("Target", targetImage);
        }

        void Report(string operand, ResolvedImage image) =>
            Error.WriteLine(
                $"{operand}: {image.Reference}; platform {image.Platform}; " +
                $"{(image.IsDaemon ? "Docker image ID" : "registry manifest digest")} {image.Identity}");
    }

    protected RegistryCommandBase(
        string name,
        string description,
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        TextWriter? output = null)
        : base(name, description)
    {
        DockerRegistryClientFactory = dockerRegistryClientFactory;
        Output = output ?? Console.Out;
        ProcessTerminator = new ProcessTerminator();
    }

    IProcessTerminator IProcessTerminationAware.ProcessTerminator
    {
        get => ProcessTerminator;
        set => ProcessTerminator = value;
    }

    private IProcessTerminator ProcessTerminator { get; set; }

    protected Task ExecuteCommandAsync(
        string? registry,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> execute,
        TimeSpan? operationTimeout = null) =>
        CommandHelper.ExecuteCommandAsync(registry, cancellationToken, execute, Error, Exit, operationTimeout);

    protected virtual TextWriter Error => Console.Error;

    protected virtual void Exit(int exitCode) => ProcessTerminator.Exit(exitCode);
}
