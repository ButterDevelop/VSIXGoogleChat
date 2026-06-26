using System;
using System.Linq;

namespace VSIXGoogleChat
{
    public static class FakeCommandsGenerator
    {
        private static readonly Random _rand = new();

        private static readonly string[] FakeCommands =
        [
            "dotnet build --configuration Release /p:Platform=x64 /p:DefineConstants=SILENT",
            "dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true",
            "dotnet test --filter \"Category=Unit\" --logger \"trx;LogFileName=results.trx\"",
            "dotnet clean && dotnet restore --force-evaluate",
            "dotnet ef migrations add InitialCreate --context AppDbContext",
            "dotnet user-secrets set \"ApiKey\" \"12345-ABCDE\" --project src/Project",
            "dotnet workload install wasm-tools --skip-manifest-update",
            "msbuild Solution.sln /t:Rebuild /p:Configuration=Release /m /v:minimal",
            "msbuild /target:Restore;Clean;Build /p:Platform=\"Any CPU\" /p:WarningLevel=0",
            "nuget restore Solution.sln -PackagesDirectory ./packages -NonInteractive",
            "nuget push Package.1.2.3.nupkg -Source https://api.nuget.org/v3/index.json -SkipDuplicate",
            "git commit -m \"Refactor: improve performance and fix warnings\" --no-verify",
            "git rebase --onto main feature/branch --committer-date-is-author-date",
            "git reset --soft HEAD~3 && git stash push --include-untracked",
            "docker build --no-cache --target runtime -t app/runtime:latest .",
            "docker run --rm -it -p 8080:80 -e ASPNETCORE_ENVIRONMENT=Staging app:latest",
            "docker compose -f docker-compose.override.yml up --build --force-recreate",
            "pwsh -Command \"Invoke-WebRequest -Uri https://google.com -OutFile setup.ps1\"",
            "pwsh -File ./scripts/deploy.ps1 -ResourceGroup rg-dev -Environment Staging -Verbose",
            "winget install Microsoft.DotNet.SDK.8 --silent --accept-package-agreements",
            "winget upgrade --all --source winget --force",
            "choco install git.install dotnetcore-sdk vscode --confirm --limit-output",
            "choco uninstall nodejs postman --force --remove-dependencies",
            "az group create --name ResourceGroup --location westeurope --tags Project=Chat",
            "az webapp deploy --resource-group RG --name WebApp --src-path publish.zip --type zip",
            "dotnet tool install --global dotnet-format --version 7.0.0",
            "dotnet format --folder --include-gen --exclude .git/ --severity info",
            "curl -X POST https://httpbin.org/post -H 'Content-Type: application/json' -d '{\"key\":\"value\"}'",
            "wget --recursive --level=2 --accept=pdf,docx https://google.com/",
            "echo 'PS C:\\Users\\Developer\\source\\repos\\App>' && dir | Sort-Object Length",
            "powershell -NoLogo -Command \"Get-Process | Where-Object { $_.CPU -gt 100 } | Stop-Process -Force\""
        ];

        public static string GenerateFakeCommand()
        {
            return string.Join(" && ", FakeCommands.OrderBy(x => _rand.Next()).ToArray());
        }
    }
}
