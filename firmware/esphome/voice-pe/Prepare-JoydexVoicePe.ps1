[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-source"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$basePreparationScript = Join-Path $PSScriptRoot "Prepare-VoicePeWakeTuning.ps1"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe.yaml"
$patchPaths = @(
    (Join-Path $PSScriptRoot "voice-session-button-controls.patch"),
    (Join-Path $PSScriptRoot "voice-session-state.patch"),
    (Join-Path $PSScriptRoot "voice-session-ready-cue.patch"),
    (Join-Path $PSScriptRoot "voice-session-end-cue.patch")
)
$gitUnixTools = "C:\Program Files\Git\usr\bin"

foreach ($requiredPath in @($basePreparationScript, $overlayPath) + $patchPaths + @($gitUnixTools, $SecretsPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "A required session-controls source is missing: $requiredPath"
    }
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The Voice PE wake-tuning preparation failed."
}

$env:PATH = "$gitUnixTools;$env:PATH"
if ($null -eq (Get-Command patch.exe -ErrorAction SilentlyContinue)) {
    throw "patch.exe is unavailable after adding Git for Windows to PATH."
}

foreach ($patchPath in $patchPaths) {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    & patch.exe --batch --dry-run --forward --fuzz=0 --strip=1 "--directory=$destinationPath" "--input=$patchPath" 2>$null
    $canApply = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $previousErrorActionPreference
    if ($canApply) {
        & patch.exe --batch --forward --fuzz=0 --strip=1 "--directory=$destinationPath" "--input=$patchPath"
        if ($LASTEXITCODE -ne 0) {
            throw "The reviewed Voice Session patch did not apply cleanly: $patchPath"
        }
        continue
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    & patch.exe --batch --dry-run --reverse --fuzz=0 --strip=1 "--directory=$destinationPath" "--input=$patchPath" 2>$null
    $alreadyApplied = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $previousErrorActionPreference
    if (-not $alreadyApplied) {
        throw "The reviewed Voice Session patch is neither cleanly applicable nor already applied: $patchPath"
    }
}

$preparedScoreSoundPath = Join-Path $destinationPath "joydex-voice-pe-cues.yaml"
$preparedScoreSound = Get-Content -LiteralPath $preparedScoreSoundPath -Raw
$readyCueRearm = 'id(joydex_voice_ready_cue_pending) = true;'
if ([regex]::Matches($preparedScoreSound, [regex]::Escape($readyCueRearm)).Count -ne 1) {
    throw "Prepared session-state patch must contain exactly one ready-cue rearm assignment."
}
if ($preparedScoreSound -notmatch '(?ms)set_action:\s*\r?\n\s*- lambda: \|-\s*\r?\n\s*if \(x == "Armed" \|\| x == "Starting"\) \{\s*\r?\n\s*id\(joydex_voice_ready_cue_pending\) = true;') {
    throw "Prepared session-state patch did not place ready-cue rearming in the select set_action."
}
$activeSessionAssignment = 'id(joydex_voice_session_was_active) = true;'
$endCueAssignment = 'id(joydex_voice_end_cue_pending) = true;'
if ([regex]::Matches($preparedScoreSound, [regex]::Escape($activeSessionAssignment)).Count -ne 1 -or
    [regex]::Matches($preparedScoreSound, [regex]::Escape($endCueAssignment)).Count -ne 1 -or
    $preparedScoreSound -notmatch '(?ms)if \(x == "Listening" \|\| x == "Muted"\) \{\s*\r?\n\s*id\(joydex_voice_session_was_active\) = true;\s*\r?\n\s*\} else if \(x == "Armed" && id\(joydex_voice_session_was_active\)\) \{\s*\r?\n\s*id\(joydex_voice_session_was_active\) = false;\s*\r?\n\s*id\(joydex_voice_end_cue_pending\) = true;') {
    throw "Prepared session-state patch did not install the active-session end-cue latch exactly once."
}
$preparedAudioPath = Join-Path $destinationPath "joydex-voice-pe-audio.yaml"
$preparedAudio = Get-Content -LiteralPath $preparedAudioPath -Raw
if ([regex]::Matches($preparedAudio, [regex]::Escape('id(joydex_voice_ready_cue_pending) = false;')).Count -ne 1 -or
    $preparedAudio -notmatch '(?ms)return x == "Listening" && id\(joydex_audio_bridge\)\.is_session_active\(\)\s*&& id\(joydex_voice_ready_cue_pending\);') {
    throw "Prepared ready-cue patch did not install its one-shot Listening latch exactly once."
}
if ([regex]::Matches($preparedAudio, [regex]::Escape('id(joydex_voice_end_cue_pending) = false;')).Count -ne 1 -or
    $preparedAudio -notmatch 'joydex_end_sound_file:\s*sounds/EndBlip\.flac' -or
    $preparedAudio -notmatch 'sound_file:\s*"joydex_end_sound"' -or
    $preparedAudio -notmatch '(?ms)return !id\(joydex_audio_bridge\)\.is_session_active\(\);.*return id\(joydex_voice_end_cue_pending\);.*media_player\.is_announcing:.*id: external_media_player.*timeout: 5s.*return id\(mww\)\.is_running\(\);') {
    throw "Prepared end-cue patch did not install the bounded post-session cue before wake rearming."
}

$preparedOverlayPath = Join-Path $destinationPath "joydex-voice-pe.yaml"
Copy-Item -LiteralPath $overlayPath -Destination $preparedOverlayPath -Force
if ((Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash -cne
    (Get-FileHash -LiteralPath $preparedOverlayPath -Algorithm SHA256).Hash) {
    throw "Prepared session-controls overlay parity failed."
}

Write-Output "Prepared the supported Joydex Voice PE source at $destinationPath"
Write-Output "Center-button controls and one-shot ready/end cues: PASS"
Write-Output "No device connection or upload was attempted."
