<#
.SYNOPSIS
    1c.GeneCompilerPatch 仓库安全自查脚本。

.DESCRIPTION
    检查范围 = git 已跟踪文件 + 未被 .gitignore 排除的未跟踪文件
    （即「git add . 后真正会进入仓库的内容」；publish/、.tmp/ 等已忽略目录自动跳过）。

    检查项：
      1. secrets   敏感信息：私钥、常见平台 token、密钥形赋值、JWT
      2. privacy   本机路径泄露：用户目录、SteamLibrary 等盘符绝对路径
      3. artifacts 构建产物误入库：*.pdb、src/bin、src/obj，以及超大文件
      4. ignore    .gitignore 覆盖有效性（关键路径是否真的被排除）
      5. exec      脚本中的下载执行（download-and-execute）模式
      6. nuget     NuGet.config 包源白名单（仅允许 nuget.org）
      7. xxe       XML 外部实体声明
      8. history   （可选，-IncludeHistory）git 全部历史中的敏感信息

    退出码：0 = 通过；1 = 存在 HIGH 级发现（可接 CI）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\SecurityCheck.ps1
    powershell -ExecutionPolicy Bypass -File tools\SecurityCheck.ps1 -IncludeHistory
#>
[CmdletBinding()]
param(
    [switch]$IncludeHistory
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------- findings

$script:Findings = New-Object System.Collections.Generic.List[object]

function Add-Finding {
    param(
        [ValidateSet('HIGH', 'WARN', 'INFO')]
        [string]$Severity,
        [string]$Check,
        [string]$File,
        [int]$Line,
        [string]$Detail
    )
    $script:Findings.Add([pscustomobject]@{
            Severity = $Severity
            Check    = $Check
            File     = $File
            Line     = $Line
            Detail   = $Detail
        })
}

function Write-Report {
    $high = @($script:Findings | Where-Object Severity -eq 'HIGH')
    $warn = @($script:Findings | Where-Object Severity -eq 'WARN')
    $info = @($script:Findings | Where-Object Severity -eq 'INFO')

    Write-Host ''
    Write-Host ('===== SecurityCheck 汇总：HIGH={0} WARN={1} INFO={2} =====' -f $high.Count, $warn.Count, $info.Count)

    foreach ($group in @('HIGH', 'WARN', 'INFO')) {
        foreach ($f in ($script:Findings | Where-Object Severity -eq $group)) {
            $color = switch ($group) {
                'HIGH' { 'Red' }
                'WARN' { 'Yellow' }
                default { 'Gray' }
            }
            $loc = if ($f.Line -gt 0) { '{0}:{1}' -f $f.File, $f.Line } else { $f.File }
            Write-Host ('[{0}] {1} ({2}) {3}' -f $f.Severity, $loc, $f.Check, $f.Detail) -ForegroundColor $color
        }
    }

    if ($high.Count -gt 0) {
        Write-Host '结果：不通过（存在 HIGH 级发现，禁止提交/发布前必须处理）。' -ForegroundColor Red
        exit 1
    }
    elseif ($warn.Count -gt 0) {
        Write-Host '结果：通过（有 WARN，建议人工复核）。' -ForegroundColor Yellow
        exit 0
    }
    else {
        Write-Host '结果：通过。' -ForegroundColor Green
        exit 0
    }
}

# ---------------------------------------------------------------- target files

$BinaryExtensions = @('.dll', '.pdb', '.png', '.psd', '.jpg', '.jpeg', '.zip', '.7z',
    '.rar', '.cache', '.bin', '.ttf', '.otf', '.woff', '.mp3', '.wav', '.ico')

function Get-TargetFiles {
    # 已跟踪 + 未跟踪但未被忽略：等价于 git add . 之后会入库的全部文件
    $entries = @(& git -C $RepoRoot ls-files --cached --others --exclude-standard 2>$null) |
        Where-Object { $_ }
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'git ls-files 失败：当前目录不在 git 仓库内？' -ForegroundColor Red
        exit 1
    }
    return @($entries | ForEach-Object { Join-Path $RepoRoot $_ } |
        Where-Object { Test-Path $_ -PathType Leaf })
}

function Get-RelPath([string]$FullPath) {
    return $FullPath.Substring($RepoRoot.Length).TrimStart('\', '/')
}

# ---------------------------------------------------------------- 1. secrets

$SecretPatterns = @(
    @{ Name = '私钥文件';                Pattern = '-----BEGIN [A-Z ]*PRIVATE KEY-----';                          Severity = 'HIGH' }
    @{ Name = 'AWS AccessKey';           Pattern = 'AKIA[0-9A-Z]{16}';                                            Severity = 'HIGH' }
    @{ Name = 'GitHub token';            Pattern = 'gh[pousr]_[A-Za-z0-9]{20,}';                                  Severity = 'HIGH' }
    @{ Name = 'GitHub fine-grained PAT'; Pattern = 'github_pat_[A-Za-z0-9_]{20,}';                                Severity = 'HIGH' }
    @{ Name = 'Slack token';             Pattern = 'xox[baprs]-[A-Za-z0-9-]{10,}';                                Severity = 'HIGH' }
    @{ Name = 'OpenAI 风格 key';         Pattern = 'sk-[A-Za-z0-9]{20,}';                                         Severity = 'HIGH' }
    @{ Name = 'JWT';                     Pattern = 'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]*';   Severity = 'HIGH' }
    @{ Name = '密钥形赋值';              Pattern = '(?i)(api[_-]?key|apikey|secret|passwd|password|token|access[_-]?key)\s*[:=]\s*["''][^"''\s]{8,}["'']'; Severity = 'WARN' }
)

function Test-Secrets {
    param([object[]]$Files)

    $textFiles = @($Files | Where-Object { $BinaryExtensions -notcontains [System.IO.Path]::GetExtension($_).ToLower() })
    foreach ($file in $textFiles) {
        $rel = Get-RelPath $file
        $lines = $null
        try {
            $lines = [System.IO.File]::ReadAllLines($file)
        }
        catch {
            Add-Finding 'INFO' 'secrets' $rel 0 ('无法按文本读取，已跳过：' + $_.Exception.Message)
            continue
        }
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($rule in $SecretPatterns) {
                if ($lines[$i] -match $rule.Pattern) {
                    Add-Finding $rule.Severity 'secrets' $rel ($i + 1) ($rule.Name + '：' + $lines[$i].Trim().Substring(0, [Math]::Min(80, $lines[$i].Trim().Length)))
                }
            }
        }
    }
}

# ---------------------------------------------------------------- 2. privacy

$LocalPathPattern = '[A-Za-z]:[\\/](Users|SteamLibrary|Program Files)[\\/]\S*'

function Test-LocalPaths {
    param([object[]]$Files)

    $textFiles = @($Files | Where-Object { $BinaryExtensions -notcontains [System.IO.Path]::GetExtension($_).ToLower() })
    foreach ($file in $textFiles) {
        $rel = Get-RelPath $file
        try {
            $lines = [System.IO.File]::ReadAllLines($file)
        }
        catch { continue }
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $LocalPathPattern) {
                Add-Finding 'WARN' 'privacy' $rel ($i + 1) ('疑似本机绝对路径：' + $matches[0])
            }
        }
    }
}

# ---------------------------------------------------------------- 3. artifacts

function Test-Artifacts {
    param([object[]]$Files)

    foreach ($file in $Files) {
        $rel = Get-RelPath $file
        $ext = [System.IO.Path]::GetExtension($file).ToLower()

        if ($ext -eq '.pdb') {
            Add-Finding 'HIGH' 'artifacts' $rel 0 'PDB 文件内嵌本机路径等私有信息，不应提交'
        }
        if ($rel -match '(^|[\\/])src[\\/](bin|obj)([\\/]|$)') {
            Add-Finding 'HIGH' 'artifacts' $rel 0 '构建中间产物（bin/obj）不应提交'
        }
        if ($ext -eq '.dll') {
            Add-Finding 'INFO' 'artifacts' $rel 0 'DLL 入库（本仓库按设计提交构建产物，请确认来源可复现）'
        }
        if ((Get-Item $file).Length -gt 10MB) {
            Add-Finding 'WARN' 'artifacts' $rel 0 ('文件超过 10MB：' + [Math]::Round((Get-Item $file).Length / 1MB, 1) + 'MB')
        }
    }
}

# ---------------------------------------------------------------- 4. ignore effectiveness

function Test-GitIgnore {
    if (-not (Test-Path (Join-Path $RepoRoot '.gitignore'))) {
        Add-Finding 'WARN' 'ignore' '.gitignore' 0 '缺少 .gitignore'
        return
    }

    # 这些路径必须被忽略；探测文件名是占位符，check-ignore 只按模式匹配
    $mustIgnore = @(
        'src/bin/placeholder.dll',
        'src/obj/placeholder.cache',
        '.tmp/placeholder',
        '.agents/placeholder',
        '.docs/placeholder',
        'publish/placeholder.ps1',
        'About/placeholder.psd',
        'build/placeholder.pdb'
    )
    foreach ($probe in $mustIgnore) {
        & git -C $RepoRoot check-ignore -q -- $probe 2>$null
        if ($LASTEXITCODE -ne 0) {
            Add-Finding 'WARN' 'ignore' $probe 0 '该关键路径未被 .gitignore 覆盖'
        }
    }

    # porcelain 输出不含被忽略文件；这里若出现 pdb/bin/obj，说明 gitignore 有漏洞
    $status = @(& git -C $RepoRoot status --porcelain --untracked-files=all 2>$null) |
        Where-Object { $_ -match '^\?\?\s+(.+)$' } |
        ForEach-Object { $matches[1] } |
        Where-Object { $_ -match '(\.pdb$)|(src[\\/]bin[\\/])|(src[\\/]obj[\\/])|(^|[\\/])\.tmp[\\/]' }
    foreach ($path in $status) {
        Add-Finding 'HIGH' 'ignore' $path 0 '待入库列表中出现构建产物（.gitignore 未覆盖）'
    }
}

# ---------------------------------------------------------------- 5. download-and-execute

$ExecPatterns = @(
    @{ Name = 'IEX 管道执行下载内容';  Pattern = '(?i)(iex|invoke-expression)\s*\(\s*\(?(\$?download|iwr|invoke-webrequest|new-object\s+net\.webclient|\[system\.net\.webclient\])'; Severity = 'HIGH' }
    @{ Name = 'WebClient 下载执行';    Pattern = '(?i)(DownloadString|DownloadData|DownloadFile)\s*\(\s*["''`]https?://'; Severity = 'WARN' }
    @{ Name = 'curl|sh';               Pattern = '(?i)curl\s[^|]*\|\s*(ba)?sh';                                  Severity = 'HIGH' }
    @{ Name = 'certutil 远程下载';     Pattern = '(?i)certutil\s+(-urlcache|-verifyctl)';                        Severity = 'WARN' }
    @{ Name = 'powershell -enc';       Pattern = '(?i)powershell(.*)\s(-enc|-encodedcommand)\s';                 Severity = 'HIGH' }
)

function Test-DownloadExec {
    param([object[]]$Files)

    $scriptFiles = @($Files | Where-Object { '.ps1', '.bat', '.cmd', '.py', '.psm1' -contains [System.IO.Path]::GetExtension($_).ToLower() })
    foreach ($file in $scriptFiles) {
        $rel = Get-RelPath $file
        try {
            $lines = [System.IO.File]::ReadAllLines($file)
        }
        catch { continue }
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($rule in $ExecPatterns) {
                if ($lines[$i] -match $rule.Pattern) {
                    Add-Finding $rule.Severity 'exec' $rel ($i + 1) ($rule.Name + '：' + $lines[$i].Trim().Substring(0, [Math]::Min(80, $lines[$i].Trim().Length)))
                }
            }
        }
    }
}

# ---------------------------------------------------------------- 6. nuget sources

function Test-NugetSources {
    param([object[]]$Files)

    $nugetConfigs = @($Files | Where-Object { (Get-RelPath $_) -ieq 'nuget.config' -or (Get-RelPath $_) -ieq 'src/nuget.config' })
    foreach ($file in $nugetConfigs) {
        $rel = Get-RelPath $file
        $content = [System.IO.File]::ReadAllText($file)
        $sources = [regex]::Matches($content, '<add\s+key="[^"]*"\s+value="([^"]*)"', 'IgnoreCase')
        foreach ($m in $sources) {
            $url = $m.Groups[1].Value
            if ($url -notmatch '^https://api\.nuget\.org/v3/index\.json$') {
                Add-Finding 'WARN' 'nuget' $rel 0 ('非官方包源：' + $url)
            }
        }
    }
}

# ---------------------------------------------------------------- 7. xxe

function Test-Xxe {
    param([object[]]$Files)

    $xmlFiles = @($Files | Where-Object { '.xml' -eq [System.IO.Path]::GetExtension($_).ToLower() })
    foreach ($file in $xmlFiles) {
        $rel = Get-RelPath $file
        try {
            $content = [System.IO.File]::ReadAllText($file)
        }
        catch { continue }
        if ($content -match '<!ENTITY\s+\S+\s+SYSTEM') {
            Add-Finding 'WARN' 'xxe' $rel 0 '存在 XML 外部实体声明（<!ENTITY ... SYSTEM>）'
        }
    }
}

# ---------------------------------------------------------------- 8. git history（可选）

function Test-History {
    & git -C $RepoRoot rev-parse HEAD 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'history：仓库尚无提交，跳过。' -ForegroundColor Gray
        return
    }

    $log = @(& git -C $RepoRoot log --all -p -U0 --no-color 2>$null)
    $currentFile = ''
    for ($i = 0; $i -lt $log.Count; $i++) {
        if ($log[$i] -match '^diff --git a/(.+?) b/') {
            $currentFile = $matches[1]
            continue
        }
        if ($currentFile -and $log[$i] -match '^\+') {
            foreach ($rule in $SecretPatterns) {
                if ($log[$i] -match $rule.Pattern) {
                    Add-Finding $rule.Severity 'history' $currentFile 0 ('历史提交中出现 ' + $rule.Name)
                }
            }
        }
    }
}

# ---------------------------------------------------------------- main

Write-Host ('SecurityCheck：{0}' -f $RepoRoot)

$files = Get-TargetFiles
Write-Host ('扫描目标（git 视角）：{0} 个文件' -f $files.Count)

Test-Secrets      -Files $files
Test-LocalPaths   -Files $files
Test-Artifacts    -Files $files
Test-GitIgnore
Test-DownloadExec -Files $files
Test-NugetSources -Files $files
Test-Xxe          -Files $files

if ($IncludeHistory) {
    Test-History
}

Write-Report
