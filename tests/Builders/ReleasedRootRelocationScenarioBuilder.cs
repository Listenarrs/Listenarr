namespace Listenarr.Tests.Builders;

internal sealed class ReleasedRootRelocationScenarioBuilder(ReleasedStorageRecoveryScenario scenario)
{
    public RootFolderRelocation Build()
    {
        var relocation = new RootFolderRelocation
        {
            RootFolderId = scenario.Root.Id,
            ActiveRootFolderId = scenario.Root.Id,
            SourcePath = scenario.Root.Path,
            TargetPath = Path.Join(scenario.Root.Path, "target"),
            DesiredName = "Released relocation",
            Status = RootFolderRelocationStatus.Running,
            TotalJobs = 1,
            TargetIdentityEnrollmentState = TargetIdentityEnrollmentState.Authorized,
            TargetDirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
            TargetDirectoryObjectIdentity = "released-other-client-relocation-target"
        };
        scenario.Job.RelocationId = relocation.Id;
        relocation.MoveJobs.Add(scenario.Job);
        return relocation;
    }
}
