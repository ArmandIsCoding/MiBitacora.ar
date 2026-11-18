param([string]$Destination = (Join-Path $PSScriptRoot '..\.artifacts\local-attachments\speech.wav'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$target = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
$speech = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $spanish = $speech.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -like 'es-*' } | Select-Object -First 1
    if ($spanish) { $speech.SelectVoice($spanish.VoiceInfo.Name) }
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $speech.SetOutputToWaveFile($target, $format)
    $speech.Speak($(if ($spanish) { 'Este es un audio de prueba. Mi cuaderno es verde y la reunion es el viernes.' } else { 'This is a test recording. My notebook is green and the meeting is on Friday.' }))
    $speech.SetOutputToNull()
    Write-Output "Audio sintetico creado en $target. No contiene una voz ni una conversacion real."
} finally { $speech.Dispose() }
