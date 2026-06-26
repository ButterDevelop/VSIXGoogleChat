using System;

namespace VSIXGoogleChat
{
    public static class FakeFilesGenerator
    {
        private static readonly Random _rand = new();

        private static readonly string[] FakeFiles =
        [
            "AO.cs",
            "Program.cs",
            "WebApiModule.cs",
            "ConfigurationBuilderExtensions.cs",
            "Contracts.cs",
            "WebExecutionContextAccessor.cs",
            "StartModeling.cs",
            "StopModeling.cs",
            "CreateCheckListItem.cs",
            "DeleteCheckListItem.cs",
            "UpdateCheckListItem.cs",
            "SetCheckListItemValue.cs",
            "CreateIssue.cs",
            "DeleteIssue.cs",
            "UpdateIssue.cs",
            "SignalRHubRun.cs",
            "EditInformationTemplate.cs",
        ];

        public static string GenerateFakeFile()
        {
            return FakeFiles[_rand.Next(0, FakeFiles.Length)];
        }
    }
}
