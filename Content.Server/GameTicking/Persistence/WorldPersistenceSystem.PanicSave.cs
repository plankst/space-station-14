using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Content.Shared.GameTicking;
using Robust.Shared.Asynchronous;
using Robust.Shared.Utility;

namespace Content.Server.GameTicking.Persistence;

/// <summary>
/// Panic save: when the server's window is closed with the X, or Windows shuts down / logs off, the world is saved to
/// <see cref="PanicSavePath"/> before the process is killed. Windows only gives the server a few seconds (about 5 for
/// the X), so the panic save is written to its own file and never touches the main save, the backup or the autosave.
/// It isn't loaded automatically: to use it, rename it over the main save while the server is off.
/// </summary>
public sealed partial class WorldPersistenceSystem
{
    [Dependency] private ITaskManager _tasks = default!;

    private readonly List<PosixSignalRegistration> _closeSignals = new();
    private int _panicSaveStarted;

    /// <summary>
    /// Where the panic save goes (e.g. <c>World_PanicSave.yml</c>).
    /// </summary>
    public ResPath PanicSavePath => WithSuffix("_PanicSave");

    /// <summary>
    /// Whether a panic save was made. The normal shutdown save is skipped afterwards: Windows may kill the server
    /// at any moment, and the main save should never be in the middle of being replaced when that happens.
    /// </summary>
    public bool PanicSaved { get; private set; }

    private void WatchForClose()
    {
        WarnIfPanicSaveIsNewer();

        // A panic save means the process is about to die; if a new round starts after all, save normally again.
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => PanicSaved = false);

        // On Windows, SIGHUP is the console window being closed and SIGTERM is Windows shutting down or logging off.
        // (Ctrl+C and the shutdown command already do a normal save.)
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            _closeSignals.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => OnClosing("server window closed")));
            _closeSignals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => OnClosing("Windows shutting down")));
        }
        catch (Exception e)
        {
            Log.Warning($"Couldn't watch for the server window closing, so there won't be a panic save: {e.Message}");
        }
    }

    private void StopWatchingForClose()
    {
        foreach (var registration in _closeSignals)
        {
            registration.Dispose();
        }

        _closeSignals.Clear();
    }

    /// <summary>
    /// Runs on a background thread when Windows tells the server it's about to be closed. The server is killed as
    /// soon as this returns (or when Windows runs out of patience), so it waits for the save on the game thread.
    /// </summary>
    private void OnClosing(string reason)
    {
        if (Interlocked.Exchange(ref _panicSaveStarted, 1) != 0)
            return;

        var done = new ManualResetEventSlim();
        _tasks.RunOnMainThread(() =>
        {
            try
            {
                PanicSave(reason);
            }
            finally
            {
                done.Set();
            }
        });

        // Windows kills the process on its own schedule; this is only a backstop.
        if (!done.Wait(TimeSpan.FromSeconds(30)))
            System.Console.WriteLine("Panic save didn't finish in time.");
    }

    /// <summary>
    /// Saves the world to <see cref="PanicSavePath"/>. Must run on the game thread.
    /// </summary>
    public bool PanicSave(string reason)
    {
        if (!_enabled || !CanSave())
            return false;

        Log.Warning($"Making a panic save ({reason}).");
        try
        {
            PrepareForFinalSave();
            PanicSaved = SaveWorld($"panic save: {reason}", panic: true);
        }
        catch (Exception e)
        {
            Log.Error($"Panic save failed: {e}");
        }

        return PanicSaved;
    }

    /// <summary>
    /// A panic save isn't loaded automatically, so point it out when it's newer than the main save.
    /// </summary>
    private void WarnIfPanicSaveIsNewer()
    {
        if (!_enabled || _res.UserData.RootDir is not { } root)
            return;

        try
        {
            var panic = Path.Combine(root, PanicSavePath.ToRelativePath().ToString());
            var main = Path.Combine(root, _savePath.ToRelativePath().ToString());
            if (!File.Exists(panic) || File.Exists(main) && File.GetLastWriteTimeUtc(main) >= File.GetLastWriteTimeUtc(panic))
                return;

            Log.Warning($"{PanicSavePath} is newer than {_savePath}: the server was closed before it could save normally. " +
                        $"To continue from the panic save, stop the server and rename it to {_savePath.Filename}.");
        }
        catch (Exception e)
        {
            Log.Warning($"Couldn't check for a panic save: {e.Message}");
        }
    }
}
