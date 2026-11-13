using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MyTodo.App;

/// <summary>
/// Custom entry point: one instance only (a second launch, a jump-list item or a reminder button is
/// redirected to the running instance).
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectedToExistingInstance()) return 0;

        Application.Start(_args =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectedToExistingInstance()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey("MyTodo.DailyPlanner.Main");
        if (main.IsCurrent)
        {
            main.Activated += (_, e) => App.Instance?.OnActivatedFromAnotherInstance(e);
            return false;
        }
        // Waiting on a thread-pool task keeps the STA thread free of re-entrancy during the redirect.
        Task.Run(async () => await main.RedirectActivationToAsync(activation)).Wait();
        return true;
    }
}
