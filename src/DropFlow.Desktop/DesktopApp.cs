// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using DropFlow.Desktop.App;
using DropFlow.Desktop.Platform;

namespace DropFlow.Desktop;

/// <summary>The Avalonia application. Built in code (no XAML): Fluent theme, then the controller takes over.</summary>
public sealed class DesktopApp(DesktopCommand command, InstanceChannel? instance, AppDirectories dirs) : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Name = "DropFlow";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var controller = new AppController(this, desktop, dirs, instance);
            controller.Start(command);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
