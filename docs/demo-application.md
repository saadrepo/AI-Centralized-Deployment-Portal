# Demo application support

`sites-registry.json` now includes Demo as a separate application. Existing entries
retain their order and Portal's configuration remains unchanged.

| Field | Portal | Demo |
| --- | --- | --- |
| Registry key | Portal | Demo |
| Project | deployment-portal-poc | deployment-portal-poc |
| Repository | deployment-portal-poc | Demo-App-Repo |
| CI definition | 1 | 2 |
| Branch | refs/heads/Dev | refs/heads/Dev |
| Artifact | drop | drop |
| Target | Existing Portal mapping | Deployment-Test at deployment-test.vitonta.com |
| IIS site/application | Existing Portal mapping | CdDemo / |

The added `TfsGitProject` and `TfsGitRepository` fields default to the original Portal
project/repository for compatibility. Demo explicitly overrides its repository.
Do not derive application identity from a build number or accept a posted definition
ID. Controllers resolve the application key through the trusted registry; build
services verify definition, branch, project, repository and repository type.

## Using the application selection

Open `/TfsGitBuilds`, choose **Demo**, then **Load builds**. The query is scoped to
definition 2 and Dev. Portal remains the default and still queries definition 1.
View artifacts retains `applicationKey=Demo` through refresh/back links, Artifact
Explorer folder navigation and Prepare Deployment. Artifact Explorer also offers
application selection through the CI build picker so a different application's
build must be chosen before inspection. A Portal build cannot be inspected or
prepared under Demo, or vice versa.

The controlled preparation page resolves Demo's registered destination to the
existing SQL server with hostname `deployment-test.vitonta.com` (display name
Deployment-Test). It does not create a server or IIS site. Exactly one hostname
match and exactly one registered destination are required.

## Physical path and feature flags

Demo's destination `TargetPath` is `C:\Sites\CdDemo`, the verified existing IIS
physical path on TARGET. This mapping does not create or modify the site.
Existing form/path validation still rejects an empty or unsafe path before
downloading or staging files. `SourceReference` is empty; the manual PreStaged source flow remains
unconfigured, while controlled CD uses the verified drop artifact.

`tfs-cd.json` is unchanged: ExplorerEnabled, Enabled and ExecutionEnabled remain
false. Demo selection is available in CI Builds, while file inspection and package
preparation continue to respect their existing feature gates. This change does not
enable any flag. Operator-owned configuration/environment overrides also remain
in effect; preserve the current settings during any separately authorized release.

## Preserved safety boundary

Both applications require a successful completed build, valid commit/repository
identity, exactly one Container drop artifact, safe metadata paths and bounded
file sizes. Retrieval rechecks the pinned build and registry mapping after transfer.
Protected preparation metadata now includes CI definition, project and repository
name as well as the existing commit and repository ID. Remote staging and later
Review/queue/worker validation compare that mapping with the current registry;
changes require a new preparation. Old protected preparations/confirmation tokens
may need re-preparation after this release; they are never upgraded into approval.

Existing Update-only remote rules, configuration preservation, signed expiring
Review/Confirm, queue idempotency, audit/history, rollback and verification remain
in place. Demo cannot create a site or application through controlled CD. No database
migration, TFS CI configuration, credential store or authentication policy changed.
Assistant latest-build and artifact resolution use the selected application's same
registry scope and do not approve or execute deployments.

## Verification

Run `dotnet test DeploymentPortal.sln` (including existing local PowerShell/CD and
synthetic DPAPI fixture tests). New coverage checks definition-2 queries, repository/
project/branch isolation, Demo Container inspection, selected-app links and controller
validation, protected local preparation metadata, mapping-change rejection before
queueing, manual approval/audit, Update-only restrictions and unchanged disabled flags.
All fixtures use synthetic local files/services; no live TFS, IIS or VM change is made.
