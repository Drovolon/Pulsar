param(
    [Parameter(Mandatory = $true)]
    [string] $RepoJsonPath,

    [string] $Branch = "repo",

    [string] $Message = "Update Dalamud repository metadata"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$resolvedRepoJson = (Resolve-Path -LiteralPath $RepoJsonPath).Path
$worktreePath = Join-Path ([IO.Path]::GetTempPath()) "pulsar-repo-$([Guid]::NewGuid())"

function Assert-GitSuccess([string] $Operation)
{
    if ($LASTEXITCODE -ne 0)
    {
        throw "git failed while $Operation (exit code $LASTEXITCODE)"
    }
}

$remoteBranch = @(git -C $repoRoot ls-remote --heads origin "refs/heads/$Branch")
Assert-GitSuccess "checking whether $Branch exists"
$branchExists = $remoteBranch.Count -gt 0

if ($branchExists)
{
    git -C $repoRoot fetch origin "refs/heads/$Branch" --depth=1
    Assert-GitSuccess "fetching $Branch"

    git -C $repoRoot worktree add --detach $worktreePath FETCH_HEAD
    Assert-GitSuccess "checking out $Branch"
}
else
{
    git -C $repoRoot worktree add --detach $worktreePath HEAD
    Assert-GitSuccess "creating the temporary worktree"

    git -C $worktreePath checkout --orphan $Branch
    Assert-GitSuccess "creating orphan branch $Branch"
}

try
{
    # Keep the branch as an independent Git root containing only repo.json.
    git -C $worktreePath rm -r -f --ignore-unmatch .
    Assert-GitSuccess "clearing the repo branch"

    Copy-Item -LiteralPath $resolvedRepoJson -Destination (Join-Path $worktreePath "repo.json") -Force
    git -C $worktreePath add repo.json
    Assert-GitSuccess "staging repo.json"

    $status = git -C $worktreePath status --short
    if ([string]::IsNullOrWhiteSpace($status))
    {
        Write-Host "repo branch is already up to date"
        return
    }

    git -C $worktreePath config user.name "Pulsar Release Bot"
    git -C $worktreePath config user.email "actions@github.com"
    git -C $worktreePath commit -m $Message
    Assert-GitSuccess "committing repo.json"

    git -C $worktreePath push origin "HEAD:refs/heads/$Branch"
    Assert-GitSuccess "pushing $Branch"
}
finally
{
    git -C $repoRoot worktree remove $worktreePath --force 2>$null
}
