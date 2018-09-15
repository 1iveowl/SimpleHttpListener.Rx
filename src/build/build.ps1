param([string]$version)

if ([string]::IsNullOrEmpty($version)) {$version = "0.0.1"}

$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\MSBuild\15.0\Bin\MSBuild.exe"
&$msbuild ..\interface\ISimpleHttpListener.Rx\ISimpleHttpListener.Rx.csproj /t:Build /p:Configuration="Release"
&$msbuild ..\main\SimpleHttpListener.Rx\SimpleHttpListener.Rx.csproj /t:Build /p:Configuration="Release"


Remove-Item .\NuGet -Force -Recurse
New-Item -ItemType Directory -Force -Path .\NuGet
NuGet.exe pack SimpleHttpListener.Rx.nuspec -Verbosity detailed -Symbols -OutputDir "NuGet" -Version $version