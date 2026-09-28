using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Views;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

// Terminal.Gui keeps process-wide state, so explorer UI tests never run in parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExplorerUiCollection
{
    public const string Name = "Explorer UI";
}

// Drives an explorer window headlessly with the ANSI driver and the input injector:
// the same path keystrokes and clicks take in a terminal.
internal sealed class ExplorerUiHarness : IDisposable
{
    private readonly SessionToken token;

    private readonly SynchronizationContext? context;

    public ExplorerUiHarness(int width, int height, Func<IApplication, ExplorerWindow> create)
    {
        context = SynchronizationContext.Current;
        Width = width;
        Height = height;
        App = Application.Create();
        App.Init(DriverRegistry.Names.ANSI);
        App.Driver!.SetScreenSize(width, height);
        Window = create(App);
        token = App.Begin(Window)!;
        Window.SyncFocus();
        App.LayoutAndDraw(true);
        Input = App.GetInputInjector();
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public IApplication App { get; }
    public ExplorerWindow Window { get; }
    public IInputInjector Input { get; }
    public ExplorerState State => Window.State;

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        App.Driver!.SetScreenSize(width, height);
        App.LayoutAndDraw(true);
    }

    public void Pump()
    {
        Input.ProcessQueue();
        App.TimedEvents?.RunTimers();
        // Processing input lets the ANSI driver re-detect the size, which is meaningless headless.
        if (App.Driver!.Contents!.GetLength(1) != Width || App.Driver.Contents.GetLength(0) != Height)
        {
            App.Driver.SetScreenSize(Width, Height);
        }
        App.LayoutAndDraw();
    }

    public void Until(Func<bool> condition, string what, int seconds = 60)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        Pump();
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.{Environment.NewLine}{Screen()}");
            }
            Thread.Sleep(20);
            Pump();
        }
    }

    // Terminal.Gui owns this thread's SynchronizationContext, so awaited continuations
    // only run while the main loop is pumped.
    public T Wait<T>(Func<Task<T>> start, string what, int seconds = 60)
    {
        Task<T> task = start();
        Until(() => task.IsCompleted, what, seconds);
        return task.GetAwaiter().GetResult();
    }

    public void Press(Key key)
    {
        Input.InjectKey(key, new InputInjectionOptions());
        Pump();
    }

    public void Type(string text)
    {
        foreach (char c in text)
        {
            Press(new Key(c));
        }
    }

    public void Click(int x, int y)
    {
        Input.InjectMouse(new Mouse { ScreenPosition = new(x, y), Flags = MouseFlags.LeftButtonPressed }, new InputInjectionOptions());
        Pump();
        Input.InjectMouse(new Mouse { ScreenPosition = new(x, y), Flags = MouseFlags.LeftButtonReleased }, new InputInjectionOptions());
        Pump();
    }

    public void DoubleClick(int x, int y)
    {
        // Terminal.Gui turns a second press/release at the same spot into a double-click.
        Click(x, y);
        Click(x, y);
    }

    public void Wheel(int x, int y, bool down)
    {
        Input.InjectMouse(new Mouse { ScreenPosition = new(x, y), Flags = down ? MouseFlags.WheeledDown : MouseFlags.WheeledUp }, new InputInjectionOptions());
        Pump();
    }

    public string Row(int y)
    {
        Cell[,] cells = App.Driver!.Contents!;
        return string.Concat(Enumerable.Range(0, cells.GetLength(1)).Select(x => cells[y, x].Grapheme ?? " "));
    }

    public string Screen()
    {
        StringBuilder text = new();
        for (int y = 0; y < App.Driver!.Contents!.GetLength(0); y++)
        {
            text.AppendLine(Row(y).TrimEnd());
        }
        return text.ToString();
    }

    public (int X, int Y) Find(string text, int fromRow = 0)
    {
        for (int y = fromRow; y < App.Driver!.Contents!.GetLength(0); y++)
        {
            int x = Row(y).IndexOf(text, StringComparison.Ordinal);
            if (x >= 0)
            {
                return (x, y);
            }
        }
        return (-1, -1);
    }

    public bool Shows(string text) => Find(text).Y >= 0;

    // Modal pickers run a nested main loop; this feeds keys into it once it opens.
    public bool AnswerDialog(Action open, params Key[] keys)
    {
        bool saw = false;
        void OnIteration(object? sender, EventArgs<IApplication?> e)
        {
            if (saw || App.TopRunnableView is not Dialog)
            {
                return;
            }
            saw = true;
            App.AddTimeout(TimeSpan.FromSeconds(20), () =>
            {
                (App.TopRunnableView as Dialog)?.RequestStop();
                return false;
            });
            foreach (Key key in keys)
            {
                Input.InjectKey(key, new InputInjectionOptions());
            }
        }
        App.Iteration += OnIteration;
        try
        {
            open();
        }
        finally
        {
            App.Iteration -= OnIteration;
        }
        Pump();
        return saw;
    }

    // Drives a modal picker from its own nested loop: each step waits for its condition and then acts,
    // so async work inside the dialog keeps running between steps. The last step should close it.
    public bool InDialog(Action open, params DialogStep[] steps)
    {
        bool saw = false;
        int next = 0;
        Exception? failure = null;
        DateTime deadline = DateTime.MaxValue;
        void Fail(Exception exception)
        {
            failure ??= exception;
            (App.TopRunnableView as Dialog)?.RequestStop();
        }
        void OnIteration(object? sender, EventArgs<IApplication?> e)
        {
            if (failure is not null || App.TopRunnableView is not Dialog)
            {
                return;
            }
            if (!saw)
            {
                saw = true;
                deadline = DateTime.UtcNow.AddSeconds(30);
            }
            if (next >= steps.Length)
            {
                return;
            }
            try
            {
                if (steps[next].Ready())
                {
                    steps[next++].Act();
                    deadline = DateTime.UtcNow.AddSeconds(30);
                }
                else if (DateTime.UtcNow > deadline)
                {
                    Fail(new TimeoutException($"Timed out waiting for {steps[next].What}.{Environment.NewLine}{Screen()}"));
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }
        App.Iteration += OnIteration;
        try
        {
            open();
        }
        finally
        {
            App.Iteration -= OnIteration;
        }
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        Assert.True(next == steps.Length, $"The dialog closed after {next} of {steps.Length} steps.");
        Pump();
        return saw;
    }

    // Injected keys are queued for the dialog's loop rather than processed immediately.
    public void Send(params Key[] keys)
    {
        foreach (Key key in keys)
        {
            Input.InjectKey(key, new InputInjectionOptions());
        }
    }

    public void Send(string text) => Send(text.Select(c => new Key(c)).ToArray());

    public void Dispose()
    {
        App.End(token);
        Window.Dispose();
        App.Dispose();
        SynchronizationContext.SetSynchronizationContext(context);
    }
}

internal sealed record DialogStep(string What, Func<bool> Ready, Action Act)
{
    public static DialogStep When(string what, Func<bool> ready, Action act) => new(what, ready, act);
    public static DialogStep Then(Action act) => new("the next step", () => true, act);
}
