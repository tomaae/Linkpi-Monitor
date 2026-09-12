using System.Windows.Controls;
using System.Windows.Data;
using System.Windows;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class PasswordBoxBindingTests
{
    [Fact]
    public Task PasswordIsMaskedAndUpdatesTheConfigurationModel() => WpfTestHost.RunAsync(() =>
    {
        var model = new RtspConfiguration { Password = "existing-secret" };
        var passwordBox = new PasswordBox();
        BindingOperations.SetBinding(
            passwordBox,
            PasswordBoxBinding.PasswordProperty,
            new Binding(nameof(RtspConfiguration.Password)) { Source = model, Mode = BindingMode.TwoWay });

        Assert.Equal("existing-secret", passwordBox.Password);

        passwordBox.Password = "changed-secret";

        Assert.Equal("changed-secret", model.Password);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AttachedPropertyAccessorsAlsoWorkOnNonPasswordObjects() => WpfTestHost.RunAsync(() =>
    {
        var target = new DependencyObject();

        PasswordBoxBinding.SetPassword(target, "stored-secret");

        Assert.Equal("stored-secret", PasswordBoxBinding.GetPassword(target));
        return Task.CompletedTask;
    });
}
