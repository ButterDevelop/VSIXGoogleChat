using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Threading.Tasks;

namespace VSIXGoogleChat
{
    internal sealed class ToggleSilentModeCommand
    {
        private readonly AsyncPackage package;

        private ToggleSilentModeCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(new Guid("bd4b2d60-8e79-4bf4-90ed-97e10855211d"), 0x0110);
            var menuItem      = new OleMenuCommand(Execute, menuCommandID)
            {
                Supported = true
            };

            commandService.AddCommand(menuItem);
        }

        public static ToggleSilentModeCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                Instance = new ToggleSilentModeCommand(package, commandService);
            }
        }

        private async void Execute(object sender, EventArgs e)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var toolWindow = this.package.FindToolWindow(typeof(ChatToolWindow), 0, true);
                if (toolWindow.Content is ChatToolWindowControl control)
                {
                    await control.ToggleSilentModeAsync(true);
                    control.ScrollToEnd();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error while executing command: {ex.Message}");
            }
        }
    }
}