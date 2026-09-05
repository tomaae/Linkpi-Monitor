using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Linkpi_Monitor;

namespace Linkpi_Monitor.Tests;

internal static class WpfTestHost
{
    private static readonly Lazy<Task<Dispatcher>> Host = new(Start);

    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                // Load production resources without automatically opening the dashboard or contacting a device.
                typeof(Application).GetField("_startupUri", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(app, null);
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    public static async Task RunAsync(Func<Task> test)
    {
        var dispatcher = await Host.Value;
        await await dispatcher.InvokeAsync(test);
    }

    public static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    public static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
