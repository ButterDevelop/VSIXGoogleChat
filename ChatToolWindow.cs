using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VSIXGoogleChat
{
    [Guid("4ba04ec2-370f-4763-ab1e-c28a6342d316")]
    public class ChatToolWindow : ToolWindowPane
    {
        private readonly ChatToolWindowControl _control;

        public ChatToolWindow() : base(null)
        {
            this.Caption = "Internal PowerShell";
            _control = new ChatToolWindowControl();
            this.Content = _control;

            _control.RequestWindowVisibility += (visible) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var frame = this.Frame as IVsWindowFrame;
                if (visible)
                    frame?.Show();
                else
                    frame?.Hide();
            };
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _control.SetPackage(this.Package as AsyncPackage);
        }

        public async Task ToggleStealthModeAsync()
        {
            var control = (ChatToolWindowControl)this.Content;
            await control.ToggleStealthModeAsync();
        }

        public async Task ToggleSilentModeAsync()
        {
            var control = (ChatToolWindowControl)this.Content;
            await control.ToggleSilentModeAsync();
        }
    }
}