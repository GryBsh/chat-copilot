[CmdletBinding()]
param(
  [string[]]$ApiEnvFiles = @(
    'api.env'
    'memory.env'
  ),
  [string[]]$MemoryPipelineEnvFiles = @(
    'memory.env'
  )
)

function secretKey {
  $key = [System.Guid]::NewGuid().ToString()
  $key = $key.Replace("-", "")
  $key = $key.Substring(0, 32)
  return [System.Convert]::ToBase64String($key);
}

function Join-EnvFiles {
  [CmdletBinding()]
  param(
    [string[]]$Path
  )
  $lines = @();
  foreach ($file in $Path) {
    $env = Get-Content $file
    foreach ($line in $env) {
      $lines += $line
    }
  }
  return [string]::Join("`n", $lines);
}

$ApiEnv = Join-EnvFiles -Path $ApiEnvFiles
$MemoryPipelineEnv = Join-EnvFiles -Path $MemoryPipelineEnvFiles