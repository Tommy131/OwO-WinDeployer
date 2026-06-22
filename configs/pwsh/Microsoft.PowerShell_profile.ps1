# PowerShell 7 $PROFILE（由 OwO! Win Deployer 同步）
# 目标：%USERPROFILE%\Documents\PowerShell\Microsoft.PowerShell_profile.ps1

# 常用别名 / 函数（按需修改）
Set-Alias ll Get-ChildItem
function gs { git status }
function .. { Set-Location .. }

# 提示当前 git 分支（轻量）
# Import-Module posh-git -ErrorAction SilentlyContinue
