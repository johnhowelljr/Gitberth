<#
.SYNOPSIS
Builds and starts every sample project on a Docker host over SSH, each with the Dockerfile Gitberth generates for it.

.DESCRIPTION
Runs Gitberth's detection on each sample (through the test project), copies the samples and the generated Dockerfiles
to the host, and runs check-on-docker.sh there. Containers only listen on the host's 127.0.0.1, and every image and
container is removed afterwards. The exit code is 0 only when every sample answered.

.EXAMPLE
./tests/samples/check-remote.ps1 -Target root@192.168.1.50 -Key ~/.ssh/id_ed25519

.EXAMPLE
./tests/samples/check-remote.ps1 -Target root@192.168.1.50 -Only python-fastapi, python-django

.EXAMPLE
./tests/samples/check-remote.ps1 -Target root@192.168.1.50 -Keep
Leaves each sample that answers running on the host's LAN address (from port 9101), with an index on port 9100.
Remove them again with -Clean.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Target,
    [string] $Key,
    [string[]] $Only = @(),
    [switch] $Keep,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ssh = @()
if ($Key) { $ssh = @('-i', $Key) }
if ($Clean) {
    $script = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'check-on-docker.sh')).Replace("`r`n", "`n")
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script))
    ssh @ssh $Target "echo $encoded | base64 -d > /tmp/check-on-docker.sh && sh /tmp/check-on-docker.sh --clean; rm -f /tmp/check-on-docker.sh"
    exit $LASTEXITCODE
}
$work = Join-Path ([IO.Path]::GetTempPath()) ('gitberth-samples-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
try {
    $env:GITBERTH_SAMPLES_OUT = Join-Path $work 'generated'
    dotnet test (Join-Path $repo 'tests/Gitberth.Tests') --nologo -v q --filter Writes_each_samples_dockerfile_for_the_docker_check | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Generating the sample Dockerfiles failed; run the tests to see why.' }
    Remove-Item Env:GITBERTH_SAMPLES_OUT

    Copy-Item -Recurse (Join-Path $repo 'tests/samples') (Join-Path $work 'samples')
    tar -czf (Join-Path $work 'samples.tgz') -C $work samples generated
    scp @ssh -q (Join-Path $work 'samples.tgz') "${Target}:/tmp/gitberth-samples.tgz"
    if ($LASTEXITCODE -ne 0) { throw "Copying the samples to $Target failed." }

    # `-Only a, b` from a PowerShell prompt is a list; through `powershell -File` it arrives as "a,b".
    $names = ($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ' '
    ssh @ssh $Target ("set -e; rm -rf /tmp/gitberth-samples; mkdir -p /tmp/gitberth-samples; " +
        "tar -xzf /tmp/gitberth-samples.tgz -C /tmp/gitberth-samples; rm /tmp/gitberth-samples.tgz; cd /tmp/gitberth-samples; " +
        "sed -i 's/\r$//' samples/check-on-docker.sh; sh samples/check-on-docker.sh $(if ($Keep) { '--keep ' })samples generated $names")
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:GITBERTH_SAMPLES_OUT -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
