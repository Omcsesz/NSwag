using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Nuke.Common;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Chocolatey;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitHub;
using static Nuke.Common.Tools.Chocolatey.ChocolateyTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Npm.NpmTasks;

public partial class Build
{
    string NuGetSource => "https://api.nuget.org/v3/index.json";
    [Parameter] [Secret] string NuGetApiKey;

    string MyGetGetSource => "https://www.myget.org/F/nswag/api/v2/package";
    [Parameter] [Secret] string MyGetApiKey;

    [Parameter] [Secret] string ChocoApiKey;

    [Parameter] [Secret] string NpmAuthToken;

    string ApiKeyToUse => IsTaggedBuild ? NuGetApiKey : MyGetApiKey;
    string SourceToUse => IsTaggedBuild ? NuGetSource : MyGetGetSource;

    Target Publish => _ => _
        .OnlyWhenDynamic(() => IsRunningOnWindows && (GitRepository.IsOnMainOrMasterBranch() || IsTaggedBuild) && GitRepository.GetGitHubOwner() == "RicoSuter")
        .DependsOn(Pack)
        .Requires(() => NuGetApiKey, () => MyGetApiKey, () => ChocoApiKey, () => NpmAuthToken)
        .Executes(() =>
        {
            if (IsTaggedBuild)
            {
                ChocolateyPush(_ => _
                    .SetApiKey(ChocoApiKey)
                    .SetPathToNuGetPackage(ArtifactsDirectory.GlobFiles("NSwagStudio.*.nupkg").Single())
                    .SetSource("https://push.chocolatey.org/")
                );

                try
                {
                    var userDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    File.WriteAllText(
                        Path.Combine(userDirectory, ".npmrc"),
                        "//registry.npmjs.org/:_authToken=" + NpmAuthToken + "\n");

                    // do not publish preview packages to npm
                    if (string.IsNullOrEmpty(VersionSuffix))
                    {
                        var outputs = Npm("publish", SourceDirectory / "NSwag.Npm", logOutput: false);

                        foreach (var output in outputs.Where(o => !o.Text.Contains("npm notice")))
                        {
                            if (output.Type == OutputType.Std)
                            {
                                Serilog.Log.Information(output.Text);
                            }
                            else
                            {
                                Serilog.Log.Error(output.Text);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("NPM PUBLISH FAILED: " + ex.Message);
                }
            }

            try
            {
                DotNetNuGetPush(_ => _
                        .Apply(PushSettingsBase)
                        .Apply(PushSettings)
                        .CombineWith(PushPackageFiles, (_, v) => _
                            .SetTargetPath(v))
                        .Apply(PackagePushSettings),
                    PushDegreeOfParallelism,
                    PushCompleteOnFailure);
            }
            catch (Exception e)
            {
                if (IsTaggedBuild)
                {
                    // fatal
                    throw;
                }

                Serilog.Log.Error("Could not push: {Message}", e.Message);
            }

        });

    Configure<DotNetNuGetPushSettings> PushSettingsBase => _ => _
        .SetSource(SourceToUse)
        .SetApiKey(ApiKeyToUse)
        .SetSkipDuplicate(true);

    Configure<DotNetNuGetPushSettings> PushSettings => _ => _;
    Configure<DotNetNuGetPushSettings> PackagePushSettings => _ => _;

    IEnumerable<AbsolutePath> PushPackageFiles =>
        ArtifactsDirectory.GlobFiles("*.nupkg")
            .Where(x => !x.ToString().Contains("NSwagStudio", StringComparison.OrdinalIgnoreCase));

    bool PushCompleteOnFailure => true;

    int PushDegreeOfParallelism => 1;
}