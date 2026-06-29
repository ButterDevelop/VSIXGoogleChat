using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;

namespace VSIXGoogleChat
{
    internal sealed class ToggleNotificationsCommand
    {
        private readonly AsyncPackage   _package;
        private readonly OleMenuCommand _menuCommand;
        private ChatOptions? _options;

        private ToggleNotificationsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(new Guid("bd4b2d60-8e79-4bf4-90ed-97e10855211d"), 0x0111);
            _menuCommand = new(Execute, menuCommandID)
            {
                Supported = true
            };
            _menuCommand.BeforeQueryStatus += OnBeforeQueryStatus;
            commandService.AddCommand(_menuCommand);
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            _options ??= (ChatOptions)_package.GetDialogPage(typeof(ChatOptions));

            _menuCommand?.Text = _options.EnableNotifications
                    ? "Disable Notifications"
                    : "Enable Notifications";
        }

        public static ToggleNotificationsCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                Instance = new ToggleNotificationsCommand(package, commandService);
            }
        }

        private async void Execute(object sender, EventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var options = (ChatOptions)_package.GetDialogPage(typeof(ChatOptions));
            options.EnableNotifications = !options.EnableNotifications;

            _menuCommand?.Text = options.EnableNotifications
                    ? "Disable Notifications"
                    : "Enable Notifications";

            var toolWindow = _package.FindToolWindow(typeof(ChatToolWindow), 0, true);
            if (toolWindow?.Content is ChatToolWindowControl control)
            {
                await control.ToggleNotificationsAsync(options.EnableNotifications);
            }
        }
    }
}