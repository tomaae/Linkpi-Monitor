using System.Windows;
using System.Windows.Controls;

namespace Linkpi_Monitor;

public static class PasswordBoxBinding
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password",
        typeof(string),
        typeof(PasswordBoxBinding),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            PasswordPropertyChanged));

    private static readonly DependencyProperty IsAttachedProperty = DependencyProperty.RegisterAttached(
        "IsAttached",
        typeof(bool),
        typeof(PasswordBoxBinding),
        new PropertyMetadata(false));

    private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached(
        "IsUpdating",
        typeof(bool),
        typeof(PasswordBoxBinding),
        new PropertyMetadata(false));

    public static string GetPassword(DependencyObject target) =>
        (string)target.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject target, string value) =>
        target.SetValue(PasswordProperty, value);

    private static void PasswordPropertyChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not PasswordBox passwordBox) return;

        if (!(bool)passwordBox.GetValue(IsAttachedProperty))
        {
            passwordBox.PasswordChanged += PasswordChanged;
            passwordBox.SetValue(IsAttachedProperty, true);
        }

        if ((bool)passwordBox.GetValue(IsUpdatingProperty)) return;

        var password = args.NewValue as string ?? string.Empty;
        if (passwordBox.Password == password) return;

        passwordBox.SetValue(IsUpdatingProperty, true);
        passwordBox.Password = password;
        passwordBox.SetValue(IsUpdatingProperty, false);
    }

    private static void PasswordChanged(object sender, RoutedEventArgs args)
    {
        var passwordBox = (PasswordBox)sender;
        if ((bool)passwordBox.GetValue(IsUpdatingProperty)) return;

        passwordBox.SetValue(IsUpdatingProperty, true);
        passwordBox.SetCurrentValue(PasswordProperty, passwordBox.Password);
        passwordBox.SetValue(IsUpdatingProperty, false);
    }
}
