using Microsoft.VisualStudio.Shell;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VSIXGoogleChat
{
    /// <summary>
    /// A class for storing extension settings.
    /// </summary>
    [ComVisible(true)]
    public class ChatOptions : DialogPage
    {
        private string _spaceId = "";
        private string _googleCredentialsPath = "";

        [Category("Google Chat")]
        [DisplayName("Space ID")]
        [Description("Dialogue ID at Google Chat")]
        public string SpaceId
        {
            get { return _spaceId; }
            set { _spaceId = value; }
        }

        [Category("Google Chat")]
        [DisplayName("The path to the credential file")]
        [Description("The full path to the JSON file with the service account credentials")]
        public string GoogleCredentialsPath
        {
            get { return _googleCredentialsPath; }
            set { _googleCredentialsPath = value; }
        }

        [Category("Google Chat")]
        [DisplayName("Your account username")]
        [Description("Something like \"users/...\"")]
        public string MyChatUsername { get; set; } = "";

        [Category("Stealth Mode")]
        [DisplayName("Toggle Hotkey")]
        [Description("The keyboard shortcut to toggle the stealth mode.")]
        [DefaultValue("Ctrl+Shift+R")]
        public string StealthHotKey { get; set; } = "Ctrl+Shift+R";

        [Category("Stealth Mode")]
        [DisplayName("Enable Fake Output")]
        [Description("When true, disables real PowerShell and only shows fake terminal output.")]
        [DefaultValue(false)]
        public bool FakeTerminalOutput { get; set; } = false;

        [Category("Stealth Mode")]
        [DisplayName("Hide window in Stealth Mode")]
        [Description("When true, hides window when Stealth Mode becomes On.")]
        [DefaultValue(false)]
        public bool HideWindowStealthMode { get; set; } = false;

        [Category("Silent Mode")]
        [DisplayName("Toggle Hotkey")]
        [Description("The keyboard shortcut to toggle the silent mode.")]
        [DefaultValue("Ctrl+Shift+Y")]
        public string SilentHotKey { get; set; } = "Ctrl+Shift+Y";

        [Category("Notifications")]
        [DisplayName("Enable sound notifications")]
        [Description("Play a sound when new messages arrive")]
        [DefaultValue(true)]
        public bool EnableNotifications { get; set; } = true;
        [Browsable(false)]
        public string SpaceNamesMapping { get; set; } = "";

        public string GetSpaceNickname(string spaceId)
        {
            if (string.IsNullOrEmpty(spaceId) || string.IsNullOrEmpty(SpaceNamesMapping))
                return "";

            var pairs = SpaceNamesMapping.Split(';');
            foreach (var pair in pairs)
            {
                var kv = pair.Split('=');
                if (kv.Length == 2 && kv[0] == spaceId)
                    return kv[1];
            }
            return "";
        }

        public void SetSpaceNickname(string spaceId, string nickname)
        {
            if (string.IsNullOrEmpty(spaceId)) return;

            var mappings = new System.Collections.Generic.Dictionary<string, string>();
            if (!string.IsNullOrEmpty(SpaceNamesMapping))
            {
                var pairs = SpaceNamesMapping.Split(';');
                foreach (var pair in pairs)
                {
                    var kv = pair.Split('=');
                    if (kv.Length == 2)
                        mappings[kv[0]] = kv[1];
                }
            }

            if (string.IsNullOrEmpty(nickname))
                mappings.Remove(spaceId);
            else
                mappings[spaceId] = nickname;

            var list = new System.Collections.Generic.List<string>();
            foreach (var kv in mappings)
            {
                list.Add($"{kv.Key}={kv.Value}");
            }
            SpaceNamesMapping = string.Join(";", list);
        }
    }
}