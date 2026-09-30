using System.Runtime.InteropServices;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace Valleysoft.Dredge.Explorer.Tui;

// Terminal.Gui paces its main loop at 25 iterations a second and polls input
// every 20 ms, and Windows rounds each of those sleeps up to its 15.6 ms timer
// tick. Together that delays a keypress by up to ~70 ms before any drawing
// starts. While the explorer runs, the loop turns faster and Windows timers
// get 1 ms resolution; idle iterations draw nothing, so this costs little CPU.
internal sealed partial class ResponsiveLoop : IDisposable
{
    private const ushort IterationsPerSecond = 200;
    private const uint TimerResolutionMs = 1;

    private readonly ushort previous;
    private readonly bool timerRaised;

    public ResponsiveLoop()
    {
        previous = Application.MaximumIterationsPerSecond;
        Application.MaximumIterationsPerSecond = IterationsPerSecond;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                timerRaised = TimeBeginPeriod(TimerResolutionMs) == 0;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }
    }

    // Terminal.Gui never clears its cursor-needs-update flag, so every idle
    // iteration rewrites the cursor (hide or show, plus a move) to the terminal.
    // At this loop rate that is hundreds of writes a second, which keeps Windows
    // Terminal busy and visibly delays real frames. Runs at the start of each
    // iteration, after the last one placed the cursor: once the terminal cursor
    // is what the focused view wants, the flag is cleared. Anything that moves
    // the cursor, including a flush, sets the flag again.
    public static void QuietCursor(IApplication app)
    {
        IDriver? driver = app.Driver;
        if (driver is null || !driver.GetCursorNeedsUpdate())
        {
            return;
        }
        Cursor shown = driver.GetCursor();
        Cursor? wanted = app.TopRunnableView?.MostFocused?.Cursor;
        bool settled = wanted is { IsVisible: true } ? shown == wanted : !shown.IsVisible;
        if (settled)
        {
            driver.SetCursorNeedsUpdate(false);
        }
    }

    public void Dispose()
    {
        Application.MaximumIterationsPerSecond = previous;
        if (timerRaised)
        {
            _ = TimeEndPeriod(TimerResolutionMs);
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);
}
